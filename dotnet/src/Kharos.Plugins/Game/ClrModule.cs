using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using Kharos.Mod.Abi;
using MemoryPack;

namespace Kharos.Plugins.Game;

/// <summary>
/// Мод в режиме CLR (docs/dotnet-n5.md, Н5-5; клиент — docs/dotnet-n6.md, Н6-6): сборка .NET мода (Base.Host.dll,
/// Base.Client.dll — обычная сборка net10.0, без wasm) и его Kharos.Mod.Sdk грузятся в свою collectible
/// AssemblyLoadContext на экземпляр: у SDK статика, и так у каждого экземпляра — своя. Вызовы — те же, что экспорты kh_*,
/// через Kharos.Mod.Sdk.Runtime.ClrBridge. Мод хоста — <see cref="CreateInstance"/>, клиентский (ModLoadInfo.Side =
/// "client") — <see cref="CreateClientInstance"/> с рукопожатием ClientAbi.Version. Пределов времени и памяти нет
/// (зацикленный мод повесит тест); ловушек нет — исключения мода приходят кодом ошибки, как в wasm.
/// </summary>
public sealed class ClrModule : IClientModModule
{
    private readonly GameModuleOptions _options;
    private int _strikes;
    private bool _disposed;

    /// <param name="modId">Имя мода.</param>
    /// <param name="assembly">Путь к сборке мода (рядом — Kharos.Mod.Sdk.dll).</param>
    /// <param name="load">Что отдать моду в kh_load.</param>
    /// <param name="options">Журнал и число сбоев до выключения; пределы времени и памяти здесь не действуют.</param>
    public ClrModule(string modId, string assembly, ModLoadInfo load, GameModuleOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(modId);
        if (!File.Exists(assembly))
            throw new ModAbiException(modId, $"нет сборки мода {assembly}");
        ModId = modId;
        AssemblyPath = Path.GetFullPath(assembly);
        // Клиентский мод говорит на клиентском ABI (ClientAbi.Version), хоста — на ModAbi.Version (как GameModule).
        IsClient = load.Side == ClientAbi.Side;
        if (IsClient)
            load.Abi = ClientAbi.Version;
        LoadInfo = load;
        LoadBytes = MemoryPackSerializer.Serialize(load);
        _options = options ?? new GameModuleOptions();
    }

    /// <inheritdoc/>
    public string ModId { get; }

    /// <summary>Сборка мода.</summary>
    public string AssemblyPath { get; }

    /// <summary>Клиентский мод (ModLoadInfo.Side = "client").</summary>
    public bool IsClient { get; }

    /// <inheritdoc/>
    public ModLoadInfo LoadInfo { get; }
    internal byte[] LoadBytes { get; }
    internal GameModuleOptions Options => _options;

    /// <inheritdoc/>
    public int Strikes => Volatile.Read(ref _strikes);

    /// <inheritdoc/>
    public bool Disabled => Strikes >= _options.MaxStrikes;

    /// <inheritdoc/>
    public void Strike() => Interlocked.Increment(ref _strikes);

    /// <summary>Новый экземпляр мода хоста: своя ALC, мост, kh_load.</summary>
    public IModInstance CreateInstance()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsClient)
            throw new ModAbiException(ModId, "клиентский мод: экземпляр — CreateClientInstance");
        if (Disabled)
            throw new ModDisabledException(ModId, Strikes);
        return new ClrModInstance(this);
    }

    /// <summary>Новый экземпляр клиентского мода: своя ALC, мост, рукопожатие ClientAbi.Version, kh_load.</summary>
    public IClientModInstance CreateClientInstance()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsClient)
            throw new ModAbiException(ModId, "мод хоста: экземпляр — CreateInstance");
        if (Disabled)
            throw new ModDisabledException(ModId, Strikes);
        return new ClrClientInstance(this);
    }

    /// <summary>
    /// Сборка мода хоста из папки пакета: проект *.Host рядом с mod.json, bin/&lt;конфигурация&gt;/net10.0/&lt;проект&gt;.dll.
    /// Конфигурация — та же, что у Kharos.Plugins (Debug или Release), иначе — свежая из имеющихся; нет — null.
    /// </summary>
    public static string? Find(string? modDirectory) => Find(modDirectory, "*.Host");

    /// <summary>Сборка клиентской части мода: проект *.Client рядом с mod.json (как <see cref="Find(string?)"/>).</summary>
    public static string? FindClient(string? modDirectory) => Find(modDirectory, "*.Client");

    private static string? Find(string? modDirectory, string pattern)
    {
        if (modDirectory is null || !Directory.Exists(modDirectory))
            return null;
        var own = typeof(ClrModule).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;
        var found = new List<string>();
        foreach (var project in Directory.GetDirectories(modDirectory, pattern))
        {
            var name = Path.GetFileName(project);
            var bin = Path.Combine(project, "bin");
            if (!Directory.Exists(bin))
                continue;
            foreach (var configuration in Directory.GetDirectories(bin))
            {
                var dll = Path.Combine(configuration, "net10.0", $"{name}.dll");
                if (!File.Exists(dll))
                    continue;
                if (string.Equals(Path.GetFileName(configuration), own, StringComparison.OrdinalIgnoreCase))
                    return dll;
                found.Add(dll);
            }
        }
        return found.OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    /// <summary>Новые экземпляры больше не создаются (живые выгружают свои ALC сами).</summary>
    public void Dispose() => _disposed = true;

    /// <inheritdoc/>
    public override string ToString() => $"{ModId} (CLR: {AssemblyPath})";

    /// <summary>Хеш имени мода, одинаковый между запусками (как у GameModuleInstance).</summary>
    internal static int StableHash(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
            hash = (hash ^ c) * 16777619u;
        return (int)hash;
    }
}

/// <summary>
/// Загрузка мода: сборки из папки мода (сам мод, его SDK, Base.Shared и т. п.) и зависимости из его .deps.json (пакеты
/// NuGet) — свои; чего нет ни там, ни там (BCL, общие с хостом пакеты), — из контекста хоста.
/// </summary>
internal sealed class ModLoadContext(string assembly) : AssemblyLoadContext($"mod:{Path.GetFileNameWithoutExtension(assembly)}", isCollectible: true)
{
    private readonly string _directory = Path.GetDirectoryName(assembly)!;
    private readonly AssemblyDependencyResolver _resolver = new(assembly);

    protected override Assembly? Load(AssemblyName name)
    {
        var path = Path.Combine(_directory, $"{name.Name}.dll");
        if (File.Exists(path))
            return LoadFromAssemblyPath(path);
        return _resolver.ResolveAssemblyToPath(name) is { } resolved ? LoadFromAssemblyPath(resolved) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string name) =>
        _resolver.ResolveUnmanagedDllToPath(name) is { } path ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
}

/// <summary>Экземпляр мода в режиме CLR: свой SDK в своей ALC, кадры — в его памяти вне GC (как в wasm).</summary>
internal sealed unsafe class ClrModInstance : IModInstance
{
    private delegate int LoadCall(Func<object> create, ReadOnlySpan<byte> info);
    private delegate int PrepareCall(int system, int part, int n, out nint data, out nint entities, out int capacity);
    private delegate int SystemCall(int system, ReadOnlySpan<int> counts, ulong tick, double dt, uint next);
    private delegate int EventCall(int handler, ReadOnlySpan<byte> payload, ulong tick, double dt, uint next);
    private delegate int CommandCall(int order, uint player, uint rights, ReadOnlySpan<byte> payload, ulong tick, double dt, uint next);
    private delegate int QueryCall(int query, uint rights, ReadOnlySpan<byte> payload, ulong tick, double dt);
    private delegate ReadOnlySpan<byte> OutputCall();

    private readonly ClrModule _module;
    private readonly ModLoadContext _context;
    private readonly LoadCall _load;
    private readonly PrepareCall _prepare;
    private readonly SystemCall _system;
    private readonly EventCall _event;
    private readonly CommandCall _command;
    private readonly QueryCall _query;
    private readonly OutputCall _output;
    private readonly Func<string> _error;
    private readonly Action _detach;
    private readonly ModRandom _random;
    private readonly (int Capacity, nint Data, nint Entities)[][] _parts;
    private bool _disposed;

    public ClrModInstance(ClrModule module)
    {
        _module = module;
        ModId = module.ModId;
        _random = new ModRandom(module.LoadInfo.Seed ^ (ulong)StableHash(ModId));
        _context = new ModLoadContext(module.AssemblyPath);
        try
        {
            var assembly = _context.LoadFromAssemblyPath(module.AssemblyPath);
            var sdk = _context.LoadFromAssemblyName(new AssemblyName("Kharos.Mod.Sdk"));
            var bridge = sdk.GetType("Kharos.Mod.Sdk.Runtime.ClrBridge") ?? throw new ModAbiException(ModId, "в SDK мода нет ClrBridge: SDK старый");
            var exports = assembly.GetType("Kharos.Mod.Sdk.Generated.ModExports") ?? throw new ModAbiException(ModId, "в сборке нет ModExports: это не мод хоста");
            var create = exports.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static) ?? throw new ModAbiException(ModId, "нет ModExports.Create");

            T Bind<T>(string name) where T : Delegate =>
                (bridge.GetMethod(name, BindingFlags.Public | BindingFlags.Static) ?? throw new ModAbiException(ModId, $"нет ClrBridge.{name}")).CreateDelegate<T>();

            Bind<Action<Func<double>, Action<int, string>, Func<int, nint, int, nint, int, int>>>("Attach")(NextRandom, Log, Tool);
            _load = Bind<LoadCall>("Load");
            _prepare = Bind<PrepareCall>("Prepare");
            _system = Bind<SystemCall>("RunSystem");
            _event = Bind<EventCall>("RunEvent");
            _command = Bind<CommandCall>("RunCommand");
            _query = Bind<QueryCall>("RunQuery");
            _output = Bind<OutputCall>("Output");
            _error = Bind<Func<string>>("Error");
            _detach = Bind<Action>("Detach");

            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            Check(_load(() => create.Invoke(null, null)!, module.LoadBytes), "kh_load");
            Manifest = MemoryPackSerializer.Deserialize<ModManifest>(_output()) ?? throw new ModAbiException(ModId, "kh_load: пустой манифест");
            StartupMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (Manifest.Abi != ModAbi.Version)
                throw new ModAbiException(ModId, $"манифест под ABI {Manifest.Abi}");
            Systems = GameModule.Layout(ModId, module.LoadInfo, Manifest);
            _parts = [.. Systems.Select(layout => new (int, nint, nint)[1 + layout.Tables.Count])];
        }
        catch
        {
            _context.Unload();
            throw;
        }
    }

    public string ModId { get; }
    public ModManifest Manifest { get; }
    public IReadOnlyList<ModSystemLayout> Systems { get; }

    /// <summary>Запуск: загрузка сборок и kh_load, мс.</summary>
    public double StartupMs { get; }

    /// <summary>Ловушек нет: исключения мода — коды ошибок.</summary>
    public bool Faulted => false;

    public Func<double>? RandomSource { get; set; }
    public ModToolHandler? Tools { get; set; }

    public ReadOnlySpan<byte> RunSystem(int system, IModFrame frame, in ModCall call)
    {
        ThrowIfUnusable();
        var layout = Systems[system];
        var parts = 1 + layout.Tables.Count;
        Span<int> counts = stackalloc int[parts];
        for (var part = 0; part < parts; part++)
        {
            var n = counts[part] = frame.Count(part);
            var width = part == 0 ? layout.Width : layout.Tables[part - 1].Width;
            var (data, entities) = Part(system, part, n);
            frame.Data(part)[..(width * n)].CopyTo(new Span<double>((void*)data, width * n));
            frame.Entities(part)[..n].CopyTo(new Span<uint>((void*)entities, n));
        }
        Check(_system(system, counts, call.Tick, call.Dt, call.NextEntity), layout.Info.Name);
        var main = counts[0];
        var back = new Span<double>((void*)_parts[system][0].Data, layout.Width * main);
        var into = frame.Data(0);
        foreach (var (first, count) in layout.Write)
            back.Slice(first * main, count * main).CopyTo(into.Slice(first * main, count * main));
        for (var t = 0; t < layout.Tables.Count; t++)
            if (layout.Tables[t].Write)
                new Span<double>((void*)_parts[system][t + 1].Data, layout.Tables[t].Width * counts[t + 1]).CopyTo(frame.Data(t + 1));
        return _output();
    }

    public ReadOnlySpan<byte> RaiseEvent(int handler, ReadOnlySpan<byte> payload, in ModCall call)
    {
        ThrowIfUnusable();
        Check(_event(handler, payload, call.Tick, call.Dt, call.NextEntity), $"событие {Name(Manifest.Events, handler)}");
        return _output();
    }

    public ReadOnlySpan<byte> RunOrder(int order, uint player, ReadOnlySpan<byte> payload, in ModCall call)
    {
        ThrowIfUnusable();
        Check(_command(order, player, call.Rights, payload, call.Tick, call.Dt, call.NextEntity), $"приказ {Name(Manifest.Orders, order)}");
        return _output();
    }

    public ReadOnlySpan<byte> RunQuery(int query, ReadOnlySpan<byte> payload, in ModCall call)
    {
        ThrowIfUnusable();
        Check(_query(query, call.Rights, payload, call.Tick, call.Dt), $"запрос {Name(Manifest.Queries, query)}");
        return _output();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            _detach();
        }
        finally
        {
            _context.Unload();
        }
    }

    /// <summary>Часть кадра в памяти SDK не меньше n (Prepare — только если прежняя мала).</summary>
    private (nint Data, nint Entities) Part(int system, int part, int n)
    {
        ref var cached = ref _parts[system][part];
        if (n > cached.Capacity || cached.Data == 0)
        {
            Check(_prepare(system, part, n, out var data, out var entities, out var capacity), "kh_prepare");
            cached = (capacity, data, entities);
        }
        return (cached.Data, cached.Entities);
    }

    private double NextRandom() => RandomSource?.Invoke() ?? _random.Next();

    private void Log(int level, string message) => _module.Options.Log?.Invoke(new ModLogEntry(ModId, level, message));

    /// <summary>kh.tool: адреса — в памяти процесса; ошибки инструмента — код ErrorBadInput, как у wasm.</summary>
    private int Tool(int id, nint input, int inputLength, nint output, int outputCapacity)
    {
        try
        {
            var handler = Tools;
            var request = new ReadOnlySpan<byte>((void*)input, inputLength);
            var answer = new Span<byte>((void*)output, outputCapacity);
            if (handler is not null)
                return handler(id, request, answer);
            return _module.Options.Tools.TryGetValue(id, out var tool) ? tool(request, answer) : ModAbi.ErrorUnknownId;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log(3, $"инструмент {id}: {ex.Message}");
            return ModAbi.ErrorBadInput;
        }
    }

    private void Check(int code, string what)
    {
        if (code < 0)
        {
            var text = _error();
            throw new ModCallException(ModId, code, $"{what}: {(text.Length > 0 ? text : "без текста")}");
        }
    }

    private void ThrowIfUnusable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_module.Disabled)
            throw new ModDisabledException(ModId, _module.Strikes);
    }

    private static string Name(string[] names, int id) => (uint)id < (uint)names.Length ? names[id] : $"№{id}";

    private static int StableHash(string text) => ClrModule.StableHash(text);
}

/// <summary>
/// Экземпляр клиентского мода в режиме CLR: свой SDK в своей ALC, вызовы — ClrBridge.Client*. Рукопожатие — как у wasm:
/// версия клиентского ABI SDK мода (kh_abi_version) и манифеста должна быть <see cref="ClientAbi.Version"/>.
/// </summary>
internal sealed unsafe class ClrClientInstance : IClientModInstance
{
    private delegate int LoadCall(Func<object> create, ReadOnlySpan<byte> info);
    private delegate int FrameCall(ReadOnlySpan<byte> input);
    private delegate ReadOnlySpan<byte> OutputCall();

    private readonly ClrModule _module;
    private readonly ModLoadContext _context;
    private readonly Func<int, int> _look;
    private readonly Func<int, int, int, int> _bank;
    private readonly FrameCall _frame;
    private readonly OutputCall _output;
    private readonly Func<string> _error;
    private readonly Action _detach;
    private readonly ModRandom _random;
    private bool _disposed;

    public ClrClientInstance(ClrModule module)
    {
        _module = module;
        ModId = module.ModId;
        _random = new ModRandom(module.LoadInfo.Seed ^ (ulong)ClrModule.StableHash(ModId));
        _context = new ModLoadContext(module.AssemblyPath);
        try
        {
            var assembly = _context.LoadFromAssemblyPath(module.AssemblyPath);
            var sdk = _context.LoadFromAssemblyName(new AssemblyName("Kharos.Mod.Sdk"));
            var bridge = sdk.GetType("Kharos.Mod.Sdk.Runtime.ClrBridge") ?? throw new ModAbiException(ModId, "в SDK мода нет ClrBridge: SDK старый");
            var exports = assembly.GetType("Kharos.Mod.Sdk.Generated.ClientExports") ?? throw new ModAbiException(ModId, "в сборке нет ClientExports: это не клиентский мод");
            var create = exports.GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static) ?? throw new ModAbiException(ModId, "нет ClientExports.Create");

            T Bind<T>(string name) where T : Delegate =>
                (bridge.GetMethod(name, BindingFlags.Public | BindingFlags.Static) ?? throw new ModAbiException(ModId, $"нет ClrBridge.{name}: SDK мода без клиентского моста")).CreateDelegate<T>();

            // Рукопожатие до загрузки: SDK мода собран под тот же клиентский ABI (как kh_abi_version у client.wasm).
            var version = Bind<Func<int>>("ClientAbiVersion")();
            if (version != ClientAbi.Version)
                throw new ModAbiException(ModId, $"ABI мода {version}, клиента {ClientAbi.Version}");
            Bind<Action<Func<double>, Action<int, string>, Func<int, nint, int, nint, int, int>>>("Attach")(NextRandom, Log, Tool);
            var load = Bind<LoadCall>("ClientLoad");
            _look = Bind<Func<int, int>>("ClientLook");
            _bank = Bind<Func<int, int, int, int>>("ClientBank");
            _frame = Bind<FrameCall>("ClientFrame");
            _output = Bind<OutputCall>("Output");
            _error = Bind<Func<string>>("Error");
            _detach = Bind<Action>("Detach");

            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            Check(load(() => create.Invoke(null, null)!, module.LoadBytes), "kh_load");
            Manifest = MemoryPackSerializer.Deserialize<ModManifest>(_output()) ?? throw new ModAbiException(ModId, "kh_load: пустой манифест");
            StartupMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (Manifest.Abi != ClientAbi.Version)
                throw new ModAbiException(ModId, $"манифест под ABI {Manifest.Abi}, ждали {ClientAbi.Version}");
        }
        catch
        {
            _context.Unload();
            throw;
        }
    }

    public string ModId { get; }
    public ModManifest Manifest { get; }
    public double StartupMs { get; }

    /// <summary>Ловушек нет: исключения мода — коды ошибок.</summary>
    public bool Faulted => false;

    public Func<double>? RandomSource { get; set; }
    public ModToolHandler? Tools { get; set; }

    public ReadOnlySpan<byte> Look(int look)
    {
        ThrowIfUnusable();
        Check(_look(look), $"облик {Name(Manifest.Looks, look)}");
        return _output();
    }

    public ReadOnlySpan<byte> Bank(int entry, int variant, int rate)
    {
        ThrowIfUnusable();
        Check(_bank(entry, variant, rate), (uint)entry < (uint)Manifest.Bank.Length ? $"звук {Manifest.Bank[entry].Name}" : $"звук №{entry}");
        return _output();
    }

    public ReadOnlySpan<byte> Frame(ReadOnlySpan<byte> input)
    {
        ThrowIfUnusable();
        Check(_frame(input), "кадр");
        return _output();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            _detach();
        }
        finally
        {
            _context.Unload();
        }
    }

    private double NextRandom() => RandomSource?.Invoke() ?? _random.Next();

    private void Log(int level, string message) => _module.Options.Log?.Invoke(new ModLogEntry(ModId, level, message));

    /// <summary>kh.tool: адреса — в памяти процесса; ошибки инструмента — код ErrorBadInput, как у wasm.</summary>
    private int Tool(int id, nint input, int inputLength, nint output, int outputCapacity)
    {
        try
        {
            var handler = Tools;
            var request = new ReadOnlySpan<byte>((void*)input, inputLength);
            var answer = new Span<byte>((void*)output, outputCapacity);
            if (handler is not null)
                return handler(id, request, answer);
            return _module.Options.Tools.TryGetValue(id, out var tool) ? tool(request, answer) : ModAbi.ErrorUnknownId;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log(3, $"инструмент {id}: {ex.Message}");
            return ModAbi.ErrorBadInput;
        }
    }

    private void Check(int code, string what)
    {
        if (code < 0)
        {
            var text = _error();
            throw new ModCallException(ModId, code, $"{what}: {(text.Length > 0 ? text : "без текста")}");
        }
    }

    private void ThrowIfUnusable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_module.Disabled)
            throw new ModDisabledException(ModId, _module.Strikes);
    }

    private static string Name(string[] names, int id) => (uint)id < (uint)names.Length ? names[id] : $"№{id}";
}
