using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.WebGPU;
using WBuffer = Silk.NET.WebGPU.Buffer;

namespace Kharos.Gpu;

/// <summary>Чанк земли по координатам в чанках: <see cref="Renderer.ChunkBytes"/> байт, тайлы строками, 4 байта на тайл.</summary>
public delegate ReadOnlySpan<byte> LandChunkSource(int chunkX, int chunkY);

/// <summary>Номер правки чанка: окно перечитывает только изменённые. 0 — чанк не правили.</summary>
public delegate int LandRevisionSource(int chunkX, int chunkY);

/// <summary>
/// Рендер кадра на WebGPU — перенос <c>apps/client/src/render.rs</c>, порядок кадра тот же:
/// <list type="number">
/// <item>земля — terrain.wgsl (перенос terrain.frag) по окну тайлов вокруг камеры (<c>map/landWindow.ts</c>);</item>
/// <item>следы, затем по слоям тени и спрайты — инстансами из общего атласа, цвет стороны — палитрой, ничьё — серым;</item>
/// <item>осадки по биомам — weather.wgsl (перенос weather.frag);</item>
/// <item>свет, как lightingPass.ts: когда темнее полудня — карта силуэтов, карта освещённости (оттенок <see cref="LightTint"/>,
/// фары, лучи с тенями, огни зданий) и умножение на неё; ореолы огней — всегда, днём слабее;</item>
/// <item>туман войны, слой поверх мира и интерфейс — батчами треугольников.</item>
/// </list>
/// Кадр рисуется в свою цель RGBA8 (снимки и запись роликов читают её), в окно — переносом поверх.
/// </summary>
public sealed unsafe partial class Renderer : IDisposable
{
    /// <summary>Сторона окна земли в тайлах; тайл (x, y) — в текселе (x mod Window, y mod Window).</summary>
    public const int Window = 512;
    public const int ChunkSize = 32;
    public const int ChunkBytes = ChunkSize * ChunkSize * 4;
    private const int WindowChunks = Window / ChunkSize;
    /// <summary>Строк окна земли под таблицей зерна (<see cref="Grain"/>): 1024 × 1024 × 4 байта по <see cref="Window"/> × 4 в строке.</summary>
    private const int GrainRows = Grain.Size * Grain.Size / Window;

    /// <summary>Оттенки освещения, как в lightingPass.ts: ночь, сумерки (добавка в середине шкалы); полдень — белый.</summary>
    public static readonly Vector3 Night = new(0.16f, 0.2f, 0.42f);
    public static readonly Vector3 Dusk = new(0.1f, 0f, -0.1f);
    /// <summary>Яркость ореолов огней днём; к ночи растёт до 1. Огни слабее <see cref="MinLevel"/> не рисуются.</summary>
    public const float DayBloom = 0.35f;
    public const float MinLevel = 0.02f;
    public static readonly Vector3 LightColor = new(0xa8 / 255f, 0xd0 / 255f, 1f);
    public static readonly Vector3 BloomColor = new(0x5a / 255f, 0xa9 / 255f, 1f);
    /// <summary>Сколько раз в секунду шагает анимация болот (<c>SWAMP_FPS</c> в terrainPass.ts).</summary>
    public const float SwampFps = 5;
    /// <summary>Цвет экрана без земли.</summary>
    private static readonly Vector3 Empty = new(0.16f, 0.13f, 0.1f);
    private const TextureFormat SceneFormat = TextureFormat.Rgba8Unorm;
    private const int Slot = 256;

    /// <summary>Конвейеры кадра — по одному на программу и смешение.</summary>
    public static IReadOnlyList<string> Passes { get; } = ["terrain", "weather", "fog", "blit", "sprite", "silhouette", "spot", "beam", "decal", "batch", "glow"];

    private readonly GpuContext _gpu;
    private readonly Pipelines _pipelines;
    private readonly Sampler* _nearest, _linear, _minimapSampler;
    private readonly GpuTexture _land, _font, _white;
    private GpuTexture? _scene, _occluders, _lightmap, _atlas, _skin, _minimap, _fog;
    private ulong _fontGeneration;

    // Окно земли: какой чанк и с какой правкой лежит в каждом слоте.
    private readonly int[] _slotX = new int[WindowChunks * WindowChunks];
    private readonly int[] _slotY = new int[WindowChunks * WindowChunks];
    private readonly int[] _slotRevision = new int[WindowChunks * WindowChunks];
    private readonly bool[] _slotFilled = new bool[WindowChunks * WindowChunks];
    private LandChunkSource? _landSource;
    private LandRevisionSource? _landRevisions;
    private bool _hasLand;

    // Данные кадра: инстансы, вершины и индексы — одним буфером; параметры вызовов — слотами по 256 байт.
    private WBuffer* _data;
    private int _dataCapacity;
    private byte[] _dataStaging = [];
    private int _dataUsed;
    private WBuffer* _uniforms;
    private int _uniformCapacity;
    private byte[] _uniformStaging = [];
    private int _uniformUsed;
    private BindGroup* _uniformGroup;
    private readonly WBuffer* _palette;
    private WBuffer* _readback;
    private int _readbackSize;

    /// <summary>Размер кадра в физических пикселях.</summary>
    public int Width { get; private set; }
    public int Height { get; private set; }
    /// <summary>Барханы (uDunes): порог полей в эрге, в красных пустошах и их отступ. По умолчанию — DEFAULT_RENDER_CONFIG из packages/shared/src/settings.ts (ergDunes 0,6, redDunes 0,37, duneMargin 0,3), в double, как Float32Array у TS.</summary>
    public Vector3 Dunes { get; set; } = new((float)(0.9 - 0.6 * 0.6), (float)(0.9 - 0.37 * 0.6), 0.3f);
    /// <summary>Сколько точек усредняет земля в пикселе издалека: 4, 2 или 1 (uSamples).</summary>
    public float TerrainSamples { get; set; } = 4;
    /// <summary>Проверок тени на пиксель луча: 10, 20 или 40 (SHADOW_STEPS в lightingPass.ts; 40 — «высокая»).</summary>
    public int ShadowSteps { get; set; } = 40;
    /// <summary>Детализация осадков: 2, 1 или 0 (uDetail).</summary>
    public int WeatherDetail { get; set; } = 2;

    public Renderer(GpuContext gpu)
    {
        _gpu = gpu;
        _pipelines = new Pipelines(gpu);
        _nearest = CreateSampler(FilterMode.Nearest, FilterMode.Nearest);
        _linear = CreateSampler(FilterMode.Linear, FilterMode.Linear);
        _minimapSampler = CreateSampler(FilterMode.Linear, FilterMode.Nearest);

        var sprites = new Layout(Frame.Sprite * 4, VertexStepMode.Instance,
            [(VertexFormat.Float32x4, 0, 1), (VertexFormat.Float32x4, 16, 2), (VertexFormat.Float32x4, 32, 3), (VertexFormat.Float32x4, 48, 4)]);
        var spots = new Layout(16, VertexStepMode.Instance, [(VertexFormat.Float32x4, 0, 1)]);
        var beams = new Layout(32, VertexStepMode.Instance, [(VertexFormat.Float32x4, 0, 1), (VertexFormat.Float32x4, 16, 2)]);
        var decals = new Layout(Frame.Decal * 4, VertexStepMode.Instance,
            [(VertexFormat.Float32x4, 0, 1), (VertexFormat.Float32, 16, 2), (VertexFormat.Float32x4, 20, 3), (VertexFormat.Float32x4, 36, 4), (VertexFormat.Float32x3, 52, 5)]);
        var batch = new Layout(Unsafe.SizeOf<Vertex>(), VertexStepMode.Vertex, [(VertexFormat.Float32x2, 0, 0), (VertexFormat.Float32x2, 8, 1), (VertexFormat.Unorm8x4, 16, 2)]);
        _pipelines.Create("terrain", "terrain.wgsl", "fullscreen", "terrain", SceneFormat, Blend.None, null);
        // Осадки — с предумноженной альфой, как setBlend('alpha') у precipitationPass.ts (ONE, ONE_MINUS_SRC_ALPHA).
        _pipelines.Create("weather", "weather.wgsl", "fullscreen", "weather", SceneFormat, Blend.Premultiplied, null);
        _pipelines.Create("fog", "screen.wgsl", "fullscreen", "fog", SceneFormat, Blend.Alpha, null);
        _pipelines.Create("blit", "screen.wgsl", "fullscreen", "blit", SceneFormat, Blend.Multiply, null);
        // Спрайты и тени — с предумноженной альфой, как setBlend('alpha') у TS: множитель цвета уже умножен на альфу.
        _pipelines.Create("sprite", "sprite.wgsl", "sprite_vs", "sprite_fs", SceneFormat, Blend.Premultiplied, sprites);
        _pipelines.Create("silhouette", "sprite.wgsl", "sprite_vs", "sprite_fs", TextureFormat.R8Unorm, Blend.None, sprites);
        _pipelines.Create("spot", "light.wgsl", "spot_vs", "spot_fs", SceneFormat, Blend.Additive, spots);
        _pipelines.Create("beam", "light.wgsl", "beam_vs", "beam_fs", SceneFormat, Blend.Additive, beams);
        _pipelines.Create("decal", "decal.wgsl", "decal_vs", "decal_fs", SceneFormat, Blend.Premultiplied, decals);
        _pipelines.Create("batch", "batch.wgsl", "batch_vs", "batch_fs", SceneFormat, Blend.Interface, batch, PrimitiveTopology.TriangleList);
        _pipelines.Create("glow", "batch.wgsl", "batch_vs", "glow_fs", SceneFormat, Blend.Additive, batch, PrimitiveTopology.TriangleList);
        if (gpu.HasSurface) _pipelines.Create("present", "screen.wgsl", "fullscreen", "present", gpu.SurfaceFormat, Blend.None, null);

        // Окно земли и под ним таблица зерна (Grain): 1024² f32 — по две строки окна на строку таблицы.
        _land = NewTexture(Window, Window + GrainRows, TextureFormat.Rgba8Unorm, TextureUsage.CopyDst, Filter.Nearest);
        _land.Write(Grain.Bytes, 0, Window, Window, GrainRows);
        _font = NewTexture(Batch.AtlasSize, Batch.AtlasSize, TextureFormat.R8Unorm, TextureUsage.CopyDst, Filter.Linear);
        _white = NewTexture(1, 1, TextureFormat.Rgba8Unorm, TextureUsage.CopyDst, Filter.Nearest);
        _white.Write([255, 255, 255, 255], 0, 0, 1, 1);
        var palette = new BufferDescriptor { Usage = BufferUsage.Uniform | BufferUsage.CopyDst, Size = Pipelines.PaletteSize };
        _palette = _gpu.Api.DeviceCreateBuffer(_gpu.Device, &palette);
        EnsureUniforms(64);
        EnsureData(1 << 20);
        _gpu.ThrowIfErrors("рендер");
    }

    /// <summary>Есть ли конвейер прохода (см. <see cref="Passes"/>) — для тестов.</summary>
    public bool HasPass(string name) => _pipelines.Has(name);

    /// <summary>Исходник WGSL программы прохода — для журнала и отладки.</summary>
    public static string ShaderSource(string file) => Pipelines.Source(file);

    /// <summary>Цвет, на который умножается картинка при уровне освещения light (<c>lightTint</c>).</summary>
    public static Vector3 LightTint(float light)
    {
        var dusk = 1 - MathF.Abs(light * 2 - 1);
        static float Channel(float night, float dusk, float light, float share) => Math.Clamp(night + (1 - night) * light + dusk * share, 0, 1);
        return new(Channel(Night.X, Dusk.X, light, dusk), Channel(Night.Y, Dusk.Y, light, dusk), Channel(Night.Z, Dusk.Z, light, dusk));
    }

    /// <summary>Размер кадра в физических пикселях; у окна — и размер поверхности.</summary>
    public void Resize(int width, int height)
    {
        if (width == Width && height == Height && _scene != null) return;
        Width = Math.Max(0, width);
        Height = Math.Max(0, height);
        _scene?.Dispose();
        _scene = null;
        if (Width == 0 || Height == 0) return;
        _scene = NewTexture(Width, Height, SceneFormat, TextureUsage.RenderAttachment | TextureUsage.CopySrc, Filter.Nearest);
        _gpu.ConfigureSurface(Width, Height);
    }

    /// <summary>Общий атлас обликов (<c>atlas::pack</c>): RGBA8, тон стороны — в альфе (254 − тон).</summary>
    public void SetAtlas(ReadOnlySpan<byte> rgba, int width, int height) => Replace(ref _atlas, rgba, width, height, Filter.Nearest);

    /// <summary>Скин интерфейса (<c>gui::skin_pixels</c>).</summary>
    public void SetSkin(ReadOnlySpan<byte> rgba, int width, int height) => Replace(ref _skin, rgba, width, height, Filter.Nearest);

    /// <summary>Картинка мини-карты: уменьшается сглаженно, как картинка на холсте у TS.</summary>
    public void SetMinimap(ReadOnlySpan<byte> rgba, int width, int height) => Replace(ref _minimap, rgba, width, height, Filter.Minimap);

    /// <summary>Шрифтовой атлас R8 <see cref="Batch.AtlasSize"/>²: перезаливается, только если generation изменился.</summary>
    public void SetFontAtlas(ReadOnlySpan<byte> pixels, ulong generation)
    {
        if (generation == _fontGeneration) return;
        _font.Write(pixels, 0, 0, Batch.AtlasSize, Batch.AtlasSize);
        _fontGeneration = generation;
    }

    /// <summary>Картинка обзора: альфа на тайл (как <see cref="Frame.FogUpdate"/>), сглаживается между тайлами.</summary>
    public void SetFog(ReadOnlySpan<byte> alpha, int width, int height)
    {
        if (_fog is { } fog && fog.Width == width && fog.Height == height)
        {
            fog.Write(alpha, 0, 0, width, height);
            return;
        }
        _fog?.Dispose();
        _fog = NewTexture(width, height, TextureFormat.R8Unorm, TextureUsage.CopyDst, Filter.Linear);
        _fog.Write(alpha, 0, 0, width, height);
    }

    /// <summary>
    /// Земля, по которой рисовать: чанки вокруг камеры берутся из source перед каждым кадром, если их нет в окне
    /// или правка (revisions) сменилась. Окно забывает загруженное.
    /// </summary>
    public void SetLand(LandChunkSource source, LandRevisionSource? revisions = null)
    {
        _landSource = source;
        _landRevisions = revisions;
        Array.Clear(_slotFilled);
        _hasLand = true;
    }

    /// <summary>Чанк прямо в окно — без источника, например кадр LAND с сервера.</summary>
    public void SetLandChunk(int chunkX, int chunkY, ReadOnlySpan<byte> chunk, int revision = 0)
    {
        if (chunk.Length < ChunkBytes) throw new ArgumentException($"чанк — {ChunkBytes} байт", nameof(chunk));
        int slotX = Wrap(chunkX, WindowChunks), slotY = Wrap(chunkY, WindowChunks);
        _land.Write(chunk, slotX * ChunkSize, slotY * ChunkSize, ChunkSize, ChunkSize);
        int slot = slotY * WindowChunks + slotX;
        _slotX[slot] = chunkX;
        _slotY[slot] = chunkY;
        _slotRevision[slot] = revision;
        _slotFilled[slot] = true;
        _hasLand = true;
    }

    /// <summary>Лежит ли чанк в окне (с этой правкой).</summary>
    public bool HasLandChunk(int chunkX, int chunkY, int revision = 0)
    {
        int slot = Wrap(chunkY, WindowChunks) * WindowChunks + Wrap(chunkX, WindowChunks);
        return _slotFilled[slot] && _slotX[slot] == chunkX && _slotY[slot] == chunkY && _slotRevision[slot] == revision;
    }

    /// <summary>Земли нет: экран заливается ровным цветом.</summary>
    public void ClearLand()
    {
        _landSource = null;
        _landRevisions = null;
        Array.Clear(_slotFilled);
        _hasLand = false;
    }

    /// <summary>Чанки, которые нужны окну земли при этой камере (с запасом в 2 тайла): первый и последний по осям.</summary>
    public static (int FromX, int FromY, int ToX, int ToY) LandChunksAround(View view)
    {
        const float margin = 2;
        float halfX = view.ScreenWidth / 2 / view.Zoom + margin, halfY = view.ScreenHeight / 2 / view.Zoom + margin;
        return ((int)MathF.Floor((view.X - halfX) / ChunkSize), (int)MathF.Floor((view.Y - halfY) / ChunkSize),
            (int)MathF.Floor((view.X + halfX) / ChunkSize), (int)MathF.Floor((view.Y + halfY) / ChunkSize));
    }

    /// <summary>Чанки вокруг камеры — в окно, если их там ещё нет.</summary>
    private void UpdateLand(View view)
    {
        if (_landSource is null) return;
        var (fromX, fromY, toX, toY) = LandChunksAround(view);
        for (int chunkY = fromY; chunkY <= toY; chunkY++)
        {
            for (int chunkX = fromX; chunkX <= toX; chunkX++)
            {
                int revision = _landRevisions?.Invoke(chunkX, chunkY) ?? 0;
                if (HasLandChunk(chunkX, chunkY, revision)) continue;
                SetLandChunk(chunkX, chunkY, _landSource(chunkX, chunkY), revision);
            }
        }
    }

    /// <summary>Готовый кадр — RGBA8 строками сверху вниз; opaque — альфа 255, как видно в окне.</summary>
    public byte[] ReadPixels(bool opaque = true)
    {
        if (_scene is null) return [];
        var api = _gpu.Api;
        int row = Width * 4, padded = (row + 255) & ~255, size = padded * Height;
        if (_readback == null || _readbackSize < size)
        {
            if (_readback != null) Release(_readback);
            // Буфер создаётся отображённым и сразу отпускается: так он с рождения считается заполненным. Иначе wgpu
            // заполняет его нулями лениво, при первом отображении, — и на WARP (CI windows-latest) первое чтение кадра
            // каждого рендера давало чёрную картинку (ViewerRendersHeadlessFrame «кадр однотонный», «день 0,0» у
            // Showcase_DayDuskNight, первая сцена ParityTests), а следующие, в тот же буфер, — верную.
            var descriptor = new BufferDescriptor { Usage = BufferUsage.MapRead | BufferUsage.CopyDst, Size = (ulong)size, MappedAtCreation = true };
            _readback = api.DeviceCreateBuffer(_gpu.Device, &descriptor);
            api.BufferUnmap(_readback);
            _readbackSize = size;
        }
        var encoder = api.DeviceCreateCommandEncoder(_gpu.Device, null);
        var source = new ImageCopyTexture { Texture = _scene.Texture, Aspect = TextureAspect.All };
        var destination = new ImageCopyBuffer { Buffer = _readback, Layout = new TextureDataLayout { BytesPerRow = (uint)padded, RowsPerImage = (uint)Height } };
        var extent = new Extent3D((uint)Width, (uint)Height, 1);
        api.CommandEncoderCopyTextureToBuffer(encoder, &source, &destination, &extent);
        Submit(encoder);
        // Копия кадра закончена до отображения буфера: порядок не зависит от того, как адаптер ведёт очередь.
        _gpu.WaitIdle();
        int status = 0;
        api.BufferMapAsync(_readback, MapMode.Read, 0, (nuint)size, new PfnBufferMapCallback(&OnMapped), &status);
        while (status == 0) _gpu.WaitIdle();
        if (status - 1 != (int)BufferMapAsyncStatus.Success) throw new GpuException($"чтение кадра: {(BufferMapAsyncStatus)(status - 1)}");
        var mapped = (byte*)api.BufferGetConstMappedRange(_readback, 0, (nuint)size);
        var pixels = new byte[row * Height];
        for (int y = 0; y < Height; y++) new ReadOnlySpan<byte>(mapped + y * padded, row).CopyTo(pixels.AsSpan(y * row));
        api.BufferUnmap(_readback);
        if (opaque) for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        _gpu.ThrowIfErrors("чтение кадра");
        return pixels;
    }

    /// <summary>Кадр в PNG.</summary>
    public void SavePng(string path) => Png.Write(path, ReadPixels(), Width, Height);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnMapped(BufferMapAsyncStatus status, void* data) => *(int*)data = (int)status + 1;

    private Sampler* CreateSampler(FilterMode min, FilterMode mag)
    {
        var descriptor = new SamplerDescriptor
        {
            AddressModeU = AddressMode.ClampToEdge,
            AddressModeV = AddressMode.ClampToEdge,
            AddressModeW = AddressMode.ClampToEdge,
            MagFilter = mag,
            MinFilter = min,
            MipmapFilter = MipmapFilterMode.Nearest,
            LodMinClamp = 0,
            LodMaxClamp = 32,
            Compare = CompareFunction.Undefined,
            MaxAnisotropy = 1,
        };
        return _gpu.Api.DeviceCreateSampler(_gpu.Device, &descriptor);
    }

    private GpuTexture NewTexture(int width, int height, TextureFormat format, TextureUsage usage, Filter filter) =>
        new(_gpu, width, height, format, usage, _pipelines.Image, filter switch { Filter.Linear => _linear, Filter.Minimap => _minimapSampler, _ => _nearest });

    private void Replace(ref GpuTexture? texture, ReadOnlySpan<byte> rgba, int width, int height, Filter filter)
    {
        texture?.Dispose();
        texture = NewTexture(width, height, TextureFormat.Rgba8Unorm, TextureUsage.CopyDst, filter);
        texture.Write(rgba, 0, 0, width, height);
    }

    private static int Wrap(int value, int period) => ((value % period) + period) % period;

    /// <summary>Буфер отпускается без Destroy: wgpu освободит его, когда отправленные команды отработают.</summary>
    private void Release(WBuffer* buffer) => _gpu.Api.BufferRelease(buffer);

    public void Dispose()
    {
        foreach (var texture in new[] { _scene, _occluders, _lightmap, _atlas, _skin, _minimap, _fog, _land, _font, _white }) texture?.Dispose();
        var api = _gpu.Api;
        if (_uniformGroup != null) api.BindGroupRelease(_uniformGroup);
        foreach (var buffer in new[] { (nint)_data, (nint)_uniforms, (nint)_palette, (nint)_readback })
            if (buffer != 0) Release((WBuffer*)buffer);
        api.SamplerRelease(_nearest);
        api.SamplerRelease(_linear);
        api.SamplerRelease(_minimapSampler);
        _pipelines.Dispose();
    }
}
