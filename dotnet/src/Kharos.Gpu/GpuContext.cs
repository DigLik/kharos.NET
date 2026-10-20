using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core.Contexts;
using Silk.NET.WebGPU;
using Silk.NET.WebGPU.Extensions.WGPU;

namespace Kharos.Gpu;

/// <summary>Ошибка WebGPU: проверка шейдера, конвейера или команды не прошла.</summary>
public sealed class GpuException(string message) : Exception(message);

/// <summary>
/// Устройство WebGPU (wgpu-native): экземпляр, адаптер, устройство, очередь и, если рисуем в окно, поверхность.
/// Без окна — для тестов, снимков и записи роликов. Бэкенд по умолчанию — основной для ОС (Vulkan, Metal, DX12);
/// KHAROS_GPU_BACKEND = vulkan | dx12 | metal | gl — задать явно.
/// </summary>
public sealed unsafe class GpuContext : IDisposable
{
    private readonly List<string> _errors = [];

    /// <summary>Программы и конвейеры устройства, общие у его рендеров (<see cref="Pipelines.For"/>).</summary>
    internal readonly Lock CacheGate = new();
    internal Pipelines? SharedPipelines;
    private GCHandle _self;
    private bool _disposed;

    public WebGPU Api { get; }
    internal Wgpu Native { get; }
    internal Instance* Instance { get; private set; }
    internal Adapter* Adapter { get; private set; }
    internal Device* Device { get; private set; }
    internal Queue* Queue { get; private set; }
    internal Surface* Surface { get; private set; }
    /// <summary>Формат поверхности окна; без окна — Undefined.</summary>
    public TextureFormat SurfaceFormat { get; private set; }
    public bool HasSurface => Surface != null;
    /// <summary>Видеокарта и бэкенд — для журнала и отчётов.</summary>
    public string AdapterName { get; private set; } = "";
    public BackendType Backend { get; private set; }
    /// <summary>Программный адаптер (WARP, llvmpipe): кадры у него могут отличаться от видеокарт.</summary>
    public bool IsSoftware { get; private set; }
    /// <summary>Версия wgpu-native: major.minor.patch.build. Сборка из Silk.NET 2.23 номера не знает (0).</summary>
    public string NativeVersion
    {
        get
        {
            var version = Native.GetVersion();
            return version == 0 ? "без номера" : $"{version >> 24}.{(version >> 16) & 0xff}.{(version >> 8) & 0xff}.{version & 0xff}";
        }
    }
    private PresentMode _presentMode = PresentMode.Fifo;
    private CompositeAlphaMode _alphaMode = CompositeAlphaMode.Auto;

    private GpuContext()
    {
        Api = WebGPU.GetApi();
        Native = new Wgpu(Api.Context);
        _self = GCHandle.Alloc(this);
    }

    /// <summary>Устройство без окна. Бросает <see cref="GpuException"/>, если адаптера нет.</summary>
    public static GpuContext CreateHeadless() => Create(null, true);

    /// <summary>Устройство без окна или null с причиной — для тестов на машинах без видеокарты.</summary>
    public static GpuContext? TryCreateHeadless(out string reason)
    {
        try
        {
            var context = CreateHeadless();
            reason = "";
            return context;
        }
        catch (Exception error) when (error is GpuException or DllNotFoundException or EntryPointNotFoundException or FileNotFoundException)
        {
            reason = error.Message;
            return null;
        }
    }

    /// <summary>Устройство для окна Silk.NET (IWindow — тоже INativeWindowSource). vsync — Fifo, иначе Mailbox или Immediate.</summary>
    public static GpuContext CreateForWindow(INativeWindowSource window, bool vsync = true) => Create(window, vsync);

    private static GpuContext Create(INativeWindowSource? window, bool vsync)
    {
        var context = new GpuContext();
        try
        {
            context.Init(window, vsync);
            return context;
        }
        catch
        {
            context.Dispose();
            throw;
        }
    }

    private void Init(INativeWindowSource? window, bool vsync)
    {
        var extras = new InstanceExtras
        {
            Chain = new ChainedStruct { SType = (SType)NativeSType.STypeInstanceExtras },
            Backends = BackendsFromEnvironment(),
        };
        var descriptor = new InstanceDescriptor { NextInChain = (ChainedStruct*)&extras };
        Instance = Api.CreateInstance(&descriptor);
        if (Instance == null) throw new GpuException("WebGPU: экземпляр не создан");
        if (window != null)
        {
            // Без единого бэкенда wgpuInstanceCreateSurface паникует внутри wgpu и роняет процесс — сначала проверяем,
            // что адаптер вообще есть, и отвечаем исключением.
            Api.AdapterRelease(RequestAdapter(null));
            Surface = window.CreateWebGPUSurface(Api, Instance);
        }
        Adapter = RequestAdapter(Surface);
        var request = new Request();
        AdapterProperties properties = default;
        Api.AdapterGetProperties(Adapter, &properties);
        AdapterName = Marshal.PtrToStringUTF8((nint)properties.Name) ?? "";
        Backend = properties.BackendType;
        IsSoftware = properties.AdapterType == AdapterType.Cpu;

        var label = "kharos"u8.ToArray().Append((byte)0).ToArray();
        fixed (byte* name = label)
        {
            var deviceDescriptor = new DeviceDescriptor { Label = name, DefaultQueue = new QueueDescriptor { Label = name } };
            request = new Request();
            Api.AdapterRequestDevice(Adapter, &deviceDescriptor, new PfnRequestDeviceCallback(&OnDevice), &request);
        }
        if (request.Result == null) throw new GpuException("WebGPU: устройство не создано — " + Marshal.PtrToStringUTF8(request.Message));
        Device = (Device*)request.Result;
        Api.DeviceSetUncapturedErrorCallback(Device, new PfnErrorCallback(&OnError), (void*)GCHandle.ToIntPtr(_self));
        Queue = Api.DeviceGetQueue(Device);

        if (Surface != null) ChooseSurfaceFormat(vsync);
    }

    /// <summary>Адаптер, совместимый с поверхностью (или любой, если её нет); нет адаптера — <see cref="GpuException"/>.</summary>
    private Adapter* RequestAdapter(Surface* surface)
    {
        var options = new RequestAdapterOptions { CompatibleSurface = surface, PowerPreference = PowerPreference.HighPerformance, ForceFallbackAdapter = SoftwareRequested };
        var request = new Request();
        Api.InstanceRequestAdapter(Instance, &options, new PfnRequestAdapterCallback(&OnAdapter), &request);
        if (request.Result == null) throw new GpuException("WebGPU: нет адаптера — " + Marshal.PtrToStringUTF8(request.Message));
        return (Adapter*)request.Result;
    }

    /// <summary>KHAROS_GPU_ADAPTER=software: просить программный (запасной) адаптер вместо видеокарты.</summary>
    public static bool SoftwareRequested => string.Equals(Environment.GetEnvironmentVariable("KHAROS_GPU_ADAPTER"), "software", StringComparison.OrdinalIgnoreCase);

    private static InstanceBackend BackendsFromEnvironment() => Environment.GetEnvironmentVariable("KHAROS_GPU_BACKEND")?.ToLowerInvariant() switch
    {
        "vulkan" => InstanceBackend.Vulkan,
        "dx12" => InstanceBackend.DX12,
        "metal" => InstanceBackend.Metal,
        "gl" => InstanceBackend.GL,
        _ => InstanceBackend.Primary,
    };

    /// <summary>Формат без sRGB, как у кадрового буфера GL: цвета шейдеров пишутся как есть.</summary>
    private void ChooseSurfaceFormat(bool vsync)
    {
        SurfaceCapabilities capabilities = default;
        Api.SurfaceGetCapabilities(Surface, Adapter, &capabilities);
        var formats = new ReadOnlySpan<TextureFormat>(capabilities.Formats, (int)capabilities.FormatCount);
        var modes = new ReadOnlySpan<PresentMode>(capabilities.PresentModes, (int)capabilities.PresentModeCount);
        var alphas = new ReadOnlySpan<CompositeAlphaMode>(capabilities.AlphaModes, (int)capabilities.AlphaModeCount);
        SurfaceFormat = formats.Contains(TextureFormat.Bgra8Unorm) ? TextureFormat.Bgra8Unorm
            : formats.Contains(TextureFormat.Rgba8Unorm) ? TextureFormat.Rgba8Unorm
            : formats.Length > 0 ? formats[0] : TextureFormat.Bgra8Unorm;
        _presentMode = vsync ? PresentMode.Fifo
            : modes.Contains(PresentMode.Mailbox) ? PresentMode.Mailbox
            : modes.Contains(PresentMode.Immediate) ? PresentMode.Immediate : PresentMode.Fifo;
        _alphaMode = alphas.Contains(CompositeAlphaMode.Opaque) ? CompositeAlphaMode.Opaque : alphas.Length > 0 ? alphas[0] : CompositeAlphaMode.Auto;
        Api.SurfaceCapabilitiesFreeMembers(capabilities);
    }

    /// <summary>Размер поверхности окна в физических пикселях.</summary>
    internal void ConfigureSurface(int width, int height)
    {
        if (Surface == null || width <= 0 || height <= 0) return;
        var configuration = new SurfaceConfiguration
        {
            Device = Device,
            Format = SurfaceFormat,
            Usage = TextureUsage.RenderAttachment,
            AlphaMode = _alphaMode,
            Width = (uint)width,
            Height = (uint)height,
            PresentMode = _presentMode,
        };
        Api.SurfaceConfigure(Surface, &configuration);
        ThrowIfErrors("поверхность");
    }

    /// <summary>Дождаться, пока видеокарта выполнит всё отправленное.</summary>
    public void WaitIdle() => Native.DevicePoll(Device, true, (WrappedSubmissionIndex*)null);

    /// <summary>Ошибки WebGPU с прошлой проверки — исключением.</summary>
    internal void ThrowIfErrors(string what)
    {
        lock (_errors)
        {
            if (_errors.Count == 0) return;
            var message = $"WebGPU, {what}: {string.Join("\n", _errors)}";
            _errors.Clear();
            throw new GpuException(message);
        }
    }

    private struct Request
    {
        public void* Result;
        public nint Message;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnAdapter(RequestAdapterStatus status, Adapter* adapter, byte* message, void* data)
    {
        var request = (Request*)data;
        request->Result = status == RequestAdapterStatus.Success ? adapter : null;
        request->Message = (nint)message;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnDevice(RequestDeviceStatus status, Device* device, byte* message, void* data)
    {
        var request = (Request*)data;
        request->Result = status == RequestDeviceStatus.Success ? device : null;
        request->Message = (nint)message;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnError(ErrorType type, byte* message, void* data)
    {
        if (GCHandle.FromIntPtr((nint)data).Target is not GpuContext context) return;
        lock (context._errors) context._errors.Add($"{type}: {Marshal.PtrToStringUTF8((nint)message)}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SharedPipelines?.Dispose();
        SharedPipelines = null;
        if (Queue != null) Api.QueueRelease(Queue);
        if (Device != null) Api.DeviceRelease(Device);
        if (Adapter != null) Api.AdapterRelease(Adapter);
        if (Surface != null) Api.SurfaceRelease(Surface);
        if (Instance != null) Api.InstanceRelease(Instance);
        Queue = null; Device = null; Adapter = null; Surface = null; Instance = null;
        if (_self.IsAllocated) _self.Free();
    }
}
