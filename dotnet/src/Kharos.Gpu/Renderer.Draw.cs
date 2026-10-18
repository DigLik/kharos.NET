using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.WebGPU;
using WBuffer = Silk.NET.WebGPU.Buffer;
using WColor = Silk.NET.WebGPU.Color;

namespace Kharos.Gpu;

public sealed unsafe partial class Renderer
{
    /// <summary>
    /// Кадр: мир, слой поверх мира и интерфейс. Порядок — как draw_world и draw_batch у render.rs. Цели ночи (силуэты и
    /// освещённость) от экрана не зависят, поэтому рисуются до него — весь кадр уходит одним проходом по экрану.
    /// </summary>
    public void Render(Frame frame)
    {
        if (_scene is null) return;
        var view = frame.View;
        if (frame.FogUpdate is { } fogUpdate) SetFog(fogUpdate.Alpha, fogUpdate.Width, fogUpdate.Height);
        UpdateLand(view);
        WritePalette(frame.Palette);
        var night = frame.Light < 1;
        if (night) NightTargets();
        Reserve(frame);
        _dataUsed = 0;
        _uniformUsed = 0;

        var api = _gpu.Api;
        var encoder = api.DeviceCreateCommandEncoder(_gpu.Device, null);
        // Инстансы слоя — в буфер один раз: их берут и тени, и спрайты, и силуэты.
        Span<int> layerOffsets = stackalloc int[Frame.Layers];
        for (int layer = 0; layer < Frame.Layers; layer++) layerOffsets[layer] = Push(CollectionsMarshal.AsSpan(frame.SpriteLayers[layer]));

        if (night)
        {
            // Лучам фар мешают наземные и здания; лежащее на земле и летающие — нет.
            var occluders = Begin(encoder, _occluders!.View, default);
            for (int layer = 1; layer < 5; layer++) Sprites(occluders, "silhouette", view, frame, layer, layerOffsets[layer], 2);
            End(occluders);
            var tint = LightTint(frame.Light);
            var light = Begin(encoder, _lightmap!.View, new WColor(tint.X, tint.Y, tint.Z, 1));
            Spots(light, view, frame.Spots, LightColor, 1);
            if (frame.Beams.Count > 0)
            {
                var offset = Push(CollectionsMarshal.AsSpan(frame.Beams));
                var draw = Camera(view);
                draw.ColorR = LightColor.X; draw.ColorG = LightColor.Y; draw.ColorB = LightColor.Z;
                draw.Strength = 1;
                Use(light, "beam", draw, _occluders);
                api.RenderPassEncoderSetVertexBuffer(light, 0, _data, (ulong)offset, (ulong)(frame.Beams.Count * 4));
                api.RenderPassEncoderDraw(light, 4, (uint)(frame.Beams.Count / 8), 0, 0);
            }
            End(light);
        }

        var pass = Begin(encoder, _scene.View, new WColor(Empty.X, Empty.Y, Empty.Z, 1));
        Terrain(pass, view, frame);
        // Следы — над землёй и под юнитами, до освещения: ночью темнеют вместе с землёй.
        if (frame.Decals.Count > 0)
        {
            var offset = Push(CollectionsMarshal.AsSpan(frame.Decals));
            Use(pass, "decal", Camera(view), _atlas ?? _white);
            api.RenderPassEncoderSetVertexBuffer(pass, 0, _data, (ulong)offset, (ulong)(frame.Decals.Count * 4));
            api.RenderPassEncoderDraw(pass, 4, (uint)(frame.Decals.Count / Frame.Decal), 0, 0);
        }
        // По слою: сначала тени слоя, потом он сам — тень соседа не ляжет на кабину.
        for (int layer = 0; layer < Frame.Layers; layer++)
        {
            if (layer > 0) Sprites(pass, "sprite", view, frame, layer, layerOffsets[layer], 1);
            Sprites(pass, "sprite", view, frame, layer, layerOffsets[layer], 0);
        }
        Weather(pass, view, frame);
        var bloom = DayBloom + (1 - DayBloom) * (1 - frame.Light);
        if (night)
        {
            Use(pass, "blit", Camera(view), _lightmap!);
            api.RenderPassEncoderDraw(pass, 4, 1, 0, 0);
        }
        Spots(pass, view, frame.Blooms, BloomColor, bloom);
        Fog(pass, view, frame);
        DrawBatch(pass, frame.World, view, true);
        DrawBatch(pass, frame.Interface, view, false);
        End(pass);

        SurfaceTexture surface = default;
        TextureView* surfaceView = null;
        if (_gpu.HasSurface)
        {
            api.SurfaceGetCurrentTexture(_gpu.Surface, &surface);
            if (surface.Status == SurfaceGetCurrentTextureStatus.Success)
            {
                surfaceView = api.TextureCreateView(surface.Texture, null);
                var present = Begin(encoder, surfaceView, new WColor(0, 0, 0, 1));
                Use(present, "present", Camera(view), _scene);
                api.RenderPassEncoderDraw(present, 4, 1, 0, 0);
                End(present);
            }
            else if (surface.Status is SurfaceGetCurrentTextureStatus.Outdated or SurfaceGetCurrentTextureStatus.Lost)
            {
                _gpu.ConfigureSurface(Width, Height);
            }
        }

        fixed (byte* data = _dataStaging)
            if (_dataUsed > 0) api.QueueWriteBuffer(_gpu.Queue, _data, 0, data, (nuint)_dataUsed);
        fixed (byte* uniforms = _uniformStaging)
            if (_uniformUsed > 0) api.QueueWriteBuffer(_gpu.Queue, _uniforms, 0, uniforms, (nuint)_uniformUsed);
        Submit(encoder);
        if (surfaceView != null)
        {
            api.SurfacePresent(_gpu.Surface);
            api.TextureViewRelease(surfaceView);
        }
        if (surface.Texture != null) api.TextureRelease(surface.Texture);
        _gpu.ThrowIfErrors("кадр");
    }

    /// <summary>Земля шейдером из TS: zoom и screen — в физических пикселях, камера — по модулю окна.</summary>
    private void Terrain(RenderPassEncoder* pass, View view, Frame frame)
    {
        if (!_hasLand) return;
        var draw = Physical(view);
        draw.Time = MathF.Floor(frame.Time * SwampFps) / SwampFps;
        draw.Samples = TerrainSamples;
        draw.DunesX = Dunes.X; draw.DunesY = Dunes.Y; draw.DunesZ = Dunes.Z;
        Use(pass, "terrain", draw, _land);
        _gpu.Api.RenderPassEncoderDraw(pass, 4, 1, 0, 0);
    }

    /// <summary>Осадки: тот же полноэкранный ход, что у земли, и альфа-смешение.</summary>
    private void Weather(RenderPassEncoder* pass, View view, Frame frame)
    {
        if (!_hasLand || frame.Precipitation <= 0) return;
        var draw = Physical(view);
        draw.Time = frame.Time;
        draw.WindX = frame.Wind.X; draw.WindY = frame.Wind.Y;
        draw.DriftX = frame.Drift.X; draw.DriftY = frame.Drift.Y;
        draw.Precipitation = frame.Precipitation;
        draw.Detail = WeatherDetail;
        Use(pass, "weather", draw, _land);
        _gpu.Api.RenderPassEncoderDraw(pass, 4, 1, 0, 0);
    }

    /// <summary>Туман войны поверх освещённого мира (fogPass.ts).</summary>
    private void Fog(RenderPassEncoder* pass, View view, Frame frame)
    {
        if (frame.Fog is not { } origin || _fog is null) return;
        var zoom = view.Zoom * view.Scale;
        var draw = new DrawUniforms
        {
            CameraX = view.X, CameraY = view.Y, Zoom = zoom, ScreenX = Width, ScreenY = Height,
            OriginX = origin.X, OriginY = origin.Y, SizeX = _fog.Width, SizeY = _fog.Height,
        };
        Use(pass, "fog", draw, _fog);
        _gpu.Api.RenderPassEncoderDraw(pass, 4, 1, 0, 0);
    }

    /// <summary>Спрайты слоя: mode 0 — сами, 1 — тени, 2 — силуэты.</summary>
    private void Sprites(RenderPassEncoder* pass, string pipeline, View view, Frame frame, int layer, int offset, int mode)
    {
        var instances = frame.SpriteLayers[layer];
        if (_atlas is null || instances.Count < Frame.Sprite) return;
        var draw = Camera(view);
        draw.Mode = mode;
        Use(pass, pipeline, draw, _atlas);
        _gpu.Api.RenderPassEncoderSetVertexBuffer(pass, 0, _data, (ulong)offset, (ulong)(instances.Count * 4));
        _gpu.Api.RenderPassEncoderDraw(pass, 4, (uint)(instances.Count / Frame.Sprite), 0, 0);
    }

    private void Spots(RenderPassEncoder* pass, View view, List<float> data, Vector3 color, float strength)
    {
        if (data.Count < 4) return;
        var offset = Push(CollectionsMarshal.AsSpan(data));
        var draw = Camera(view);
        draw.ColorR = color.X; draw.ColorG = color.Y; draw.ColorB = color.Z;
        draw.Strength = strength;
        Use(pass, "spot", draw, _white);
        _gpu.Api.RenderPassEncoderSetVertexBuffer(pass, 0, _data, (ulong)offset, (ulong)(data.Count * 4));
        _gpu.Api.RenderPassEncoderDraw(pass, 4, (uint)(data.Count / 4), 0, 0);
    }

    /// <summary>Батч поверх: world — в тайлах через камеру, иначе — в логических пикселях экрана.</summary>
    private void DrawBatch(RenderPassEncoder* pass, Batch batch, View view, bool world)
    {
        if (batch.Indices.Count == 0) return;
        var api = _gpu.Api;
        var vertices = Push(CollectionsMarshal.AsSpan(batch.Vertices));
        var indices = Push(CollectionsMarshal.AsSpan(batch.Indices));
        api.RenderPassEncoderSetVertexBuffer(pass, 0, _data, (ulong)vertices, (ulong)(batch.Vertices.Count * Marshal.SizeOf<Vertex>()));
        api.RenderPassEncoderSetIndexBuffer(pass, _data, IndexFormat.Uint32, (ulong)indices, (ulong)(batch.Indices.Count * 4));
        for (int index = 0; index < batch.Segments.Count; index++)
        {
            var (start, paint, clip) = batch.Segments[index];
            int end = index + 1 < batch.Segments.Count ? batch.Segments[index + 1].Start : batch.Indices.Count;
            if (end <= start) continue;
            var draw = Camera(view);
            draw.World = world ? 1 : 0;
            GpuTexture? texture;
            switch (paint.Kind)
            {
                case PaintKind.Fog:
                    // Чёрный с альфой из красного канала; рамка картинки срезана uv-сдвигом в шейдере.
                    texture = _fog;
                    draw.Kind = 2;
                    if (texture != null) { draw.InsetX = 1f / texture.Width; draw.InsetY = 1f / texture.Height; }
                    break;
                case PaintKind.Minimap: texture = _minimap; draw.Kind = 1; break;
                case PaintKind.Skin: texture = _skin; draw.Kind = 1; break;
                case PaintKind.Sprite: texture = _atlas; draw.Kind = 1; draw.Team = paint.Team; break;
                default: texture = _font; draw.Kind = 0; break;
            }
            if (texture is null) continue;
            // Обрезка — ножницами в физических пикселях. В GL низ прямоугольника — round((y + h)·scale) от верха,
            // высота — round(h·scale); здесь тот же прямоугольник от верха и в пределах цели (WebGPU требует).
            int left = 0, top = 0, right = Width, bottom = Height;
            if (clip is { } rect)
            {
                var scale = view.Scale;
                int x = Round(rect.X * scale), width = Math.Max(Round(rect.Width * scale), 0), height = Math.Max(Round(rect.Height * scale), 0);
                int y = Round((rect.Y + rect.Height) * scale) - height;
                left = Math.Clamp(x, 0, Width); right = Math.Clamp(x + width, 0, Width);
                top = Math.Clamp(y, 0, Height); bottom = Math.Clamp(y + height, 0, Height);
                if (right <= left || bottom <= top) continue;
            }
            api.RenderPassEncoderSetScissorRect(pass, (uint)left, (uint)top, (uint)(right - left), (uint)(bottom - top));
            Use(pass, "batch", draw, texture);
            api.RenderPassEncoderDrawIndexed(pass, (uint)(end - start), 1, (uint)start, 0, 0);
        }
        api.RenderPassEncoderSetScissorRect(pass, 0, 0, (uint)Width, (uint)Height);
    }

    private static int Round(float value) => (int)MathF.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>Камера в логических пикселях — uCamera, uZoom, uScreen у camera() в render.rs.</summary>
    private static DrawUniforms Camera(View view) => new()
    {
        CameraX = view.X, CameraY = view.Y, Zoom = view.Zoom, ScreenX = view.ScreenWidth, ScreenY = view.ScreenHeight,
    };

    /// <summary>Полноэкранные проходы земли: физические пиксели, камера по модулю окна.</summary>
    private DrawUniforms Physical(View view)
    {
        var zoom = view.Zoom * view.Scale;
        return new DrawUniforms
        {
            CameraX = RemEuclid(view.X, Window), CameraY = RemEuclid(view.Y, Window),
            Zoom = zoom, Pixel = 1 / zoom, ScreenX = Width, ScreenY = Height,
        };
    }

    /// <summary><c>f32::rem_euclid</c>.</summary>
    private static float RemEuclid(float value, float period)
    {
        var rest = value % period;
        return rest < 0 ? rest + period : rest;
    }

    /// <summary>Конвейер, параметры вызова (свой слот) и текстура вызова.</summary>
    private void Use(RenderPassEncoder* pass, string pipeline, in DrawUniforms draw, GpuTexture texture)
    {
        var api = _gpu.Api;
        var offset = (uint)_uniformUsed;
        MemoryMarshal.Write(_uniformStaging.AsSpan(_uniformUsed), in draw);
        _uniformUsed += Slot;
        api.RenderPassEncoderSetPipeline(pass, _pipelines[pipeline]);
        api.RenderPassEncoderSetBindGroup(pass, 0, _uniformGroup, 1, &offset);
        api.RenderPassEncoderSetBindGroup(pass, 1, texture.Group, 0, null);
    }

    private RenderPassEncoder* Begin(CommandEncoder* encoder, TextureView* target, WColor clear)
    {
        var attachment = new RenderPassColorAttachment
        {
            View = target,
            DepthSlice = uint.MaxValue,
            LoadOp = LoadOp.Clear,
            StoreOp = StoreOp.Store,
            ClearValue = clear,
        };
        var descriptor = new RenderPassDescriptor { ColorAttachmentCount = 1, ColorAttachments = &attachment };
        return _gpu.Api.CommandEncoderBeginRenderPass(encoder, &descriptor);
    }

    private void End(RenderPassEncoder* pass)
    {
        _gpu.Api.RenderPassEncoderEnd(pass);
        _gpu.Api.RenderPassEncoderRelease(pass);
    }

    private void Submit(CommandEncoder* encoder)
    {
        var api = _gpu.Api;
        var commands = api.CommandEncoderFinish(encoder, null);
        api.QueueSubmit(_gpu.Queue, 1, &commands);
        api.CommandBufferRelease(commands);
        api.CommandEncoderRelease(encoder);
    }

    /// <summary>Цели ночи — в размер кадра: силуэты R8 и освещённость RGBA8.</summary>
    private void NightTargets()
    {
        if (_lightmap is { } lightmap && lightmap.Width == Width && lightmap.Height == Height) return;
        _lightmap?.Dispose();
        _occluders?.Dispose();
        _lightmap = NewTexture(Width, Height, SceneFormat, TextureUsage.RenderAttachment, Filter.Nearest);
        _occluders = NewTexture(Width, Height, TextureFormat.R8Unorm, TextureUsage.RenderAttachment, Filter.Nearest);
    }

    private void WritePalette(float[] palette)
    {
        Span<float> padded = stackalloc float[Pipelines.PaletteSize / 4];
        padded.Clear();
        for (int i = 0; i < 12 && i * 3 + 2 < palette.Length; i++)
        {
            padded[i * 4] = palette[i * 3];
            padded[i * 4 + 1] = palette[i * 3 + 1];
            padded[i * 4 + 2] = palette[i * 3 + 2];
        }
        fixed (float* data = padded) _gpu.Api.QueueWriteBuffer(_gpu.Queue, _palette, 0, data, Pipelines.PaletteSize);
    }

    /// <summary>Место под данные кадра заранее: буфер, на который уже записаны команды, менять нельзя.</summary>
    private void Reserve(Frame frame)
    {
        static int Bytes(int count, int size) => ((count * size + Slot - 1) & ~(Slot - 1)) + Slot;
        int data = 0;
        foreach (var layer in frame.SpriteLayers) data += Bytes(layer.Count, 4);
        data += Bytes(frame.Decals.Count, 4) + Bytes(frame.Spots.Count, 4) + Bytes(frame.Blooms.Count, 4) + Bytes(frame.Beams.Count, 4);
        int vertex = Marshal.SizeOf<Vertex>();
        foreach (var batch in (ReadOnlySpan<Batch>)[frame.World, frame.Interface])
            data += Bytes(batch.Vertices.Count, vertex) + Bytes(batch.Indices.Count, 4);
        EnsureData(data);
        EnsureUniforms(32 + frame.World.Segments.Count + frame.Interface.Segments.Count);
    }

    private void EnsureData(int bytes)
    {
        if (bytes <= _dataCapacity) return;
        if (_data != null) Release(_data);
        _dataCapacity = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(bytes, 1 << 16));
        var descriptor = new BufferDescriptor { Usage = BufferUsage.Vertex | BufferUsage.Index | BufferUsage.CopyDst, Size = (ulong)_dataCapacity };
        _data = _gpu.Api.DeviceCreateBuffer(_gpu.Device, &descriptor);
        _dataStaging = new byte[_dataCapacity];
    }

    private void EnsureUniforms(int slots)
    {
        int bytes = slots * Slot;
        if (bytes <= _uniformCapacity) return;
        var api = _gpu.Api;
        if (_uniformGroup != null) api.BindGroupRelease(_uniformGroup);
        if (_uniforms != null) Release(_uniforms);
        _uniformCapacity = (int)BitOperations.RoundUpToPowerOf2((uint)bytes);
        var descriptor = new BufferDescriptor { Usage = BufferUsage.Uniform | BufferUsage.CopyDst, Size = (ulong)_uniformCapacity };
        _uniforms = api.DeviceCreateBuffer(_gpu.Device, &descriptor);
        _uniformStaging = new byte[_uniformCapacity];
        var entries = stackalloc BindGroupEntry[2];
        entries[0] = new BindGroupEntry { Binding = 0, Buffer = _uniforms, Offset = 0, Size = Pipelines.DrawSize };
        entries[1] = new BindGroupEntry { Binding = 1, Buffer = _palette, Offset = 0, Size = Pipelines.PaletteSize };
        var group = new BindGroupDescriptor { Layout = _pipelines.Uniforms, EntryCount = 2, Entries = entries };
        _uniformGroup = api.DeviceCreateBindGroup(_gpu.Device, &group);
    }

    /// <summary>Данные в буфер кадра: смещение начала, следующее — с шагом 256 байт.</summary>
    private int Push<T>(ReadOnlySpan<T> items) where T : unmanaged
    {
        var bytes = MemoryMarshal.AsBytes(items);
        var offset = _dataUsed;
        bytes.CopyTo(_dataStaging.AsSpan(offset));
        _dataUsed = (offset + bytes.Length + Slot - 1) & ~(Slot - 1);
        return offset;
    }
}
