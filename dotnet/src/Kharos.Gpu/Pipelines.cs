using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.WebGPU;

namespace Kharos.Gpu;

/// <summary>Смешение цвета, как blend_func у GL; к альфе — то же, если не сказано иное.</summary>
internal enum Blend { None, Alpha, Premultiplied, Additive, Multiply, Interface }

/// <summary>Раскладка инстансов или вершин: шаг и атрибуты (формат, смещение, location).</summary>
internal readonly record struct Layout(int Stride, VertexStepMode Step, (VertexFormat Format, int Offset, int Location)[] Attributes);

/// <summary>Программы WGSL и конвейеры: общее начало (common.wgsl) плюс файл прохода, как COMMON + шейдер у render.rs.</summary>
internal sealed unsafe class Pipelines : IDisposable
{
    private readonly GpuContext _gpu;
    private readonly Dictionary<string, nint> _modules = [];
    private readonly Dictionary<string, nint> _pipelines = [];
    public BindGroupLayout* Uniforms { get; }
    public BindGroupLayout* Image { get; }
    public PipelineLayout* Layout { get; }

    /// <summary>Байт параметров одного вызова (структура Draw в common.wgsl) и шаг слотов.</summary>
    public const int DrawSize = 128;
    public const int PaletteSize = 12 * 16;

    public Pipelines(GpuContext gpu)
    {
        _gpu = gpu;
        var api = gpu.Api;
        var uniforms = stackalloc BindGroupLayoutEntry[2];
        uniforms[0] = new BindGroupLayoutEntry
        {
            Binding = 0,
            Visibility = ShaderStage.Vertex | ShaderStage.Fragment,
            Buffer = new BufferBindingLayout { Type = BufferBindingType.Uniform, HasDynamicOffset = true, MinBindingSize = DrawSize },
        };
        uniforms[1] = new BindGroupLayoutEntry
        {
            Binding = 1,
            Visibility = ShaderStage.Vertex | ShaderStage.Fragment,
            Buffer = new BufferBindingLayout { Type = BufferBindingType.Uniform, MinBindingSize = PaletteSize },
        };
        var uniformsDescriptor = new BindGroupLayoutDescriptor { EntryCount = 2, Entries = uniforms };
        Uniforms = api.DeviceCreateBindGroupLayout(gpu.Device, &uniformsDescriptor);

        var image = stackalloc BindGroupLayoutEntry[2];
        image[0] = new BindGroupLayoutEntry
        {
            Binding = 0,
            Visibility = ShaderStage.Fragment,
            Texture = new TextureBindingLayout { SampleType = TextureSampleType.Float, ViewDimension = TextureViewDimension.Dimension2D },
        };
        image[1] = new BindGroupLayoutEntry { Binding = 1, Visibility = ShaderStage.Fragment, Sampler = new SamplerBindingLayout { Type = SamplerBindingType.Filtering } };
        var imageDescriptor = new BindGroupLayoutDescriptor { EntryCount = 2, Entries = image };
        Image = api.DeviceCreateBindGroupLayout(gpu.Device, &imageDescriptor);

        var groups = stackalloc BindGroupLayout*[2] { Uniforms, Image };
        var layout = new PipelineLayoutDescriptor { BindGroupLayoutCount = 2, BindGroupLayouts = groups };
        Layout = api.DeviceCreatePipelineLayout(gpu.Device, &layout);
        gpu.ThrowIfErrors("раскладки привязок");
    }

    /// <summary>Исходник программы: common.wgsl и файл прохода.</summary>
    public static string Source(string file) => Resource("common.wgsl") + "\n" + Resource(file);

    private static string Resource(string file)
    {
        using var stream = typeof(Pipelines).Assembly.GetManifestResourceStream("Kharos.Gpu.Shaders." + file)
            ?? throw new InvalidOperationException($"нет шейдера {file}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private ShaderModule* Module(string file)
    {
        if (_modules.TryGetValue(file, out var cached)) return (ShaderModule*)cached;
        var code = Utf8(Source(file));
        var label = Utf8(file);
        ShaderModule* module;
        fixed (byte* source = code)
        fixed (byte* name = label)
        {
            var wgsl = new ShaderModuleWGSLDescriptor { Chain = new ChainedStruct { SType = SType.ShaderModuleWgslDescriptor }, Code = source };
            var descriptor = new ShaderModuleDescriptor { NextInChain = (ChainedStruct*)&wgsl, Label = name };
            module = _gpu.Api.DeviceCreateShaderModule(_gpu.Device, &descriptor);
        }
        _gpu.ThrowIfErrors($"шейдер {file}");
        _modules[file] = (nint)module;
        return module;
    }

    /// <summary>Конвейер под именем name: вершинная и фрагментная точки входа файла, цель format, смешение и раскладка.</summary>
    public void Create(string name, string file, string vertex, string fragment, TextureFormat format, Blend blend, Layout? layout,
        PrimitiveTopology topology = PrimitiveTopology.TriangleStrip)
    {
        var module = Module(file);
        var vertexName = Utf8(vertex);
        var fragmentName = Utf8(fragment);
        var label = Utf8(name);
        var attributes = layout is { } given ? new VertexAttribute[given.Attributes.Length] : [];
        for (int i = 0; i < attributes.Length; i++)
        {
            var (attributeFormat, offset, location) = layout!.Value.Attributes[i];
            attributes[i] = new VertexAttribute { Format = attributeFormat, Offset = (ulong)offset, ShaderLocation = (uint)location };
        }
        var blendState = State(blend);
        fixed (byte* vertexEntry = vertexName)
        fixed (byte* fragmentEntry = fragmentName)
        fixed (byte* labelText = label)
        fixed (VertexAttribute* attributePointer = attributes)
        {
            var buffer = new VertexBufferLayout
            {
                ArrayStride = (ulong)(layout?.Stride ?? 0),
                StepMode = layout?.Step ?? VertexStepMode.Vertex,
                AttributeCount = (nuint)attributes.Length,
                Attributes = attributePointer,
            };
            var target = new ColorTargetState { Format = format, Blend = blend == Blend.None ? null : &blendState, WriteMask = ColorWriteMask.All };
            var fragmentState = new FragmentState { Module = module, EntryPoint = fragmentEntry, TargetCount = 1, Targets = &target };
            var descriptor = new RenderPipelineDescriptor
            {
                Label = labelText,
                Layout = Layout,
                Vertex = new VertexState { Module = module, EntryPoint = vertexEntry, BufferCount = layout is null ? 0u : 1u, Buffers = &buffer },
                Primitive = new PrimitiveState
                {
                    Topology = topology,
                    StripIndexFormat = IndexFormat.Undefined,
                    FrontFace = FrontFace.Ccw,
                    CullMode = CullMode.None,
                },
                Multisample = new MultisampleState { Count = 1, Mask = ~0u },
                Fragment = &fragmentState,
            };
            var pipeline = _gpu.Api.DeviceCreateRenderPipeline(_gpu.Device, &descriptor);
            _gpu.ThrowIfErrors($"конвейер {name}");
            _pipelines[name] = (nint)pipeline;
        }
    }

    public RenderPipeline* this[string name] => (RenderPipeline*)_pipelines[name];

    public bool Has(string name) => _pipelines.ContainsKey(name);

    public IEnumerable<string> Names => _pipelines.Keys;

    private static BlendState State(Blend blend)
    {
        static BlendComponent Of(BlendFactor source, BlendFactor destination) => new() { Operation = BlendOperation.Add, SrcFactor = source, DstFactor = destination };
        return blend switch
        {
            Blend.Alpha => new BlendState { Color = Of(BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha), Alpha = Of(BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha) },
            Blend.Premultiplied => new BlendState { Color = Of(BlendFactor.One, BlendFactor.OneMinusSrcAlpha), Alpha = Of(BlendFactor.One, BlendFactor.OneMinusSrcAlpha) },
            Blend.Additive => new BlendState { Color = Of(BlendFactor.One, BlendFactor.One), Alpha = Of(BlendFactor.One, BlendFactor.One) },
            // DST_COLOR, ZERO: у альфы DST_COLOR — это альфа цели, как BlendFactor.Dst.
            Blend.Multiply => new BlendState { Color = Of(BlendFactor.Dst, BlendFactor.Zero), Alpha = Of(BlendFactor.Dst, BlendFactor.Zero) },
            Blend.Interface => new BlendState { Color = Of(BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha), Alpha = Of(BlendFactor.One, BlendFactor.OneMinusSrcAlpha) },
            _ => default,
        };
    }

    internal static byte[] Utf8(string text)
    {
        var bytes = new byte[Encoding.UTF8.GetByteCount(text) + 1];
        Encoding.UTF8.GetBytes(text, bytes);
        return bytes;
    }

    public void Dispose()
    {
        var api = _gpu.Api;
        foreach (var pipeline in _pipelines.Values) api.RenderPipelineRelease((RenderPipeline*)pipeline);
        foreach (var module in _modules.Values) api.ShaderModuleRelease((ShaderModule*)module);
        _pipelines.Clear();
        _modules.Clear();
        api.PipelineLayoutRelease(Layout);
        api.BindGroupLayoutRelease(Uniforms);
        api.BindGroupLayoutRelease(Image);
    }
}

/// <summary>Параметры вызова — структура Draw из common.wgsl байт в байт.</summary>
[StructLayout(LayoutKind.Explicit, Size = Pipelines.DrawSize)]
internal struct DrawUniforms
{
    [FieldOffset(0)] public float CameraX;
    [FieldOffset(4)] public float CameraY;
    [FieldOffset(8)] public float ScreenX;
    [FieldOffset(12)] public float ScreenY;
    [FieldOffset(16)] public float Zoom;
    [FieldOffset(20)] public float Pixel;
    [FieldOffset(24)] public float Time;
    [FieldOffset(28)] public float Samples;
    [FieldOffset(32)] public float DunesX;
    [FieldOffset(36)] public float DunesY;
    [FieldOffset(40)] public float DunesZ;
    [FieldOffset(44)] public float Precipitation;
    [FieldOffset(48)] public float WindX;
    [FieldOffset(52)] public float WindY;
    [FieldOffset(56)] public float DriftX;
    [FieldOffset(60)] public float DriftY;
    [FieldOffset(64)] public float OriginX;
    [FieldOffset(68)] public float OriginY;
    [FieldOffset(72)] public float SizeX;
    [FieldOffset(76)] public float SizeY;
    [FieldOffset(80)] public float ColorR;
    [FieldOffset(84)] public float ColorG;
    [FieldOffset(88)] public float ColorB;
    [FieldOffset(92)] public float Strength;
    [FieldOffset(96)] public float InsetX;
    [FieldOffset(100)] public float InsetY;
    [FieldOffset(104)] public int Mode;
    [FieldOffset(108)] public int Kind;
    [FieldOffset(112)] public int Team;
    [FieldOffset(116)] public int World;
    [FieldOffset(120)] public int Detail;
}
