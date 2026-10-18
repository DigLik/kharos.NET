using Silk.NET.WebGPU;

namespace Kharos.Gpu;

/// <summary>Выборка текстуры: в GL это параметры самой текстуры, здесь — отдельный объект. Края — CLAMP_TO_EDGE.</summary>
internal enum Filter { Nearest, Linear, Minimap }

/// <summary>Текстура, её вид и группа привязки (текстура и выборка) для шейдеров.</summary>
internal sealed unsafe class GpuTexture : IDisposable
{
    private readonly GpuContext _gpu;
    public Texture* Texture { get; private set; }
    public TextureView* View { get; private set; }
    public BindGroup* Group { get; private set; }
    public int Width { get; }
    public int Height { get; }
    public TextureFormat Format { get; }

    public GpuTexture(GpuContext gpu, int width, int height, TextureFormat format, TextureUsage usage, BindGroupLayout* layout, Sampler* sampler)
    {
        _gpu = gpu;
        Width = width;
        Height = height;
        Format = format;
        var api = gpu.Api;
        var descriptor = new TextureDescriptor
        {
            Usage = usage | TextureUsage.TextureBinding,
            Dimension = TextureDimension.Dimension2D,
            Size = new Extent3D((uint)width, (uint)height, 1),
            Format = format,
            MipLevelCount = 1,
            SampleCount = 1,
        };
        Texture = api.DeviceCreateTexture(gpu.Device, &descriptor);
        View = api.TextureCreateView(Texture, null);
        var entries = stackalloc BindGroupEntry[2];
        entries[0] = new BindGroupEntry { Binding = 0, TextureView = View };
        entries[1] = new BindGroupEntry { Binding = 1, Sampler = sampler };
        var group = new BindGroupDescriptor { Layout = layout, EntryCount = 2, Entries = entries };
        Group = api.DeviceCreateBindGroup(gpu.Device, &group);
        gpu.ThrowIfErrors("текстура");
    }

    /// <summary>Пикселей в байтах: R8 — 1, RGBA8 — 4.</summary>
    public int PixelBytes => Format is TextureFormat.R8Unorm ? 1 : 4;

    /// <summary>Прямоугольник пикселей, строки подряд без выравнивания.</summary>
    public void Write(ReadOnlySpan<byte> pixels, int x, int y, int width, int height)
    {
        if (pixels.Length < width * height * PixelBytes) throw new ArgumentException($"мало пикселей: {pixels.Length} на {width}×{height}", nameof(pixels));
        var destination = new ImageCopyTexture { Texture = Texture, MipLevel = 0, Origin = new Origin3D((uint)x, (uint)y, 0), Aspect = TextureAspect.All };
        var layout = new TextureDataLayout { Offset = 0, BytesPerRow = (uint)(width * PixelBytes), RowsPerImage = (uint)height };
        var size = new Extent3D((uint)width, (uint)height, 1);
        fixed (byte* data = pixels)
            _gpu.Api.QueueWriteTexture(_gpu.Queue, &destination, data, (nuint)(width * height * PixelBytes), &layout, &size);
    }

    public void Dispose()
    {
        var api = _gpu.Api;
        if (Group != null) api.BindGroupRelease(Group);
        if (View != null) api.TextureViewRelease(View);
        // Только Release, без Destroy: запись очереди в текстуру, ещё не отправленная, держит её — wgpu-native 0.19
        // иначе падает на следующей отправке («Texture is destroyed»).
        if (Texture != null) api.TextureRelease(Texture);
        Group = null; View = null; Texture = null;
    }
}
