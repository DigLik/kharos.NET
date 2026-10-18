using System.Buffers.Binary;
using System.Text;
using Kharos.Mod.Abi;
using MemoryPack;
using Wasmtime;

namespace Kharos.Plugins.Game;

/// <summary>
/// Кадр системы в памяти мода: блок столбцов f64 (столбец k — с k·n) и номера сущностей. Спаны верны до следующего
/// вызова мода. Движок копирует QueryColumns.Data в <see cref="Data"/> одним CopyTo и обратно.
/// </summary>
public readonly ref struct SystemFrame
{
    internal SystemFrame(Span<double> data, Span<uint> entities, int width)
    {
        Data = data;
        Entities = entities;
        Width = width;
    }

    /// <summary>Все столбцы подряд: <see cref="Width"/> × <see cref="Count"/>.</summary>
    public Span<double> Data { get; }

    /// <summary>Номера сущностей.</summary>
    public Span<uint> Entities { get; }

    /// <summary>Столбцов f64.</summary>
    public int Width { get; }

    /// <summary>Сущностей.</summary>
    public int Count => Entities.Length;

    /// <summary>Столбец k.</summary>
    public Span<double> Column(int k) => Data.Slice(k * Count, Count);
}

/// <summary>
/// Экземпляр игрового мода: своё хранилище Wasmtime с пределом памяти, рантайм мода запущен, kh_load сделан.
/// У мода хоста — системы, события и приказы; у клиентского (ModLoadInfo.Side = "client") — облики, звуки и кадр
/// (<see cref="Look"/>, <see cref="Bank"/>, <see cref="Frame(ReadOnlySpan{byte})"/>). Вызовы — из одного потока. Состояния между вызовами у мода нет, поэтому после ловушки (<see cref="Faulted"/>)
/// экземпляр выбрасывается и создаётся новый.
/// </summary>
public sealed class GameModuleInstance : IDisposable
{
    private readonly GameModule _module;
    private readonly Store _store;
    private readonly Wasmtime.Memory _memory;
    private readonly Func<int, int, int>? _prepare;
    private readonly Func<int, int, int>? _system;
    private readonly Func<int, int, int, int>? _event;
    private readonly Func<int, int, int, int, int>? _command;
    private readonly Func<int, int>? _look;
    private readonly Func<int, int, int, int>? _bank;
    private readonly Func<int, int, int>? _frame;
    private readonly Func<int, int> _alloc;
    private readonly Action<int> _free;
    private readonly int _shared;
    private readonly GameStoreData _storeData;
    private readonly int[] _capacity;
    private readonly int[] _data;
    private readonly int[] _entities;
    private readonly ulong _callTicks;
    private int _scratch;
    private int _scratchCapacity;
    private bool _disposed;

    internal GameModuleInstance(GameModule module)
    {
        _module = module;
        var host = module.Host;
        var options = host.Options;
        ModId = module.ModId;
        IsClient = module.LoadInfo.Side == ClientAbi.Side;
        Random = new ModRandom(module.LoadInfo.Seed ^ (ulong)StableHash(ModId));
        _callTicks = host.EpochTicks(options.CallTimeoutMs);

        var data = new GameStoreData(ModId, options, module.LoadInfo.Seed);
        _storeData = data;
        _store = new Store(host.Engine, data);
        try
        {
            _store.SetLimits(memorySize: options.MemoryLimitBytes);
            _store.SetEpochDeadline(host.EpochTicks(options.LoadTimeoutMs));
            Instance instance;
            try
            {
                instance = host.Linker.Instantiate(_store, module.Module);
            }
            catch (Exception ex) when (ex is not ModException)
            {
                throw new ModAbiException(ModId, $"не создаётся экземпляр: {ex.Message}");
            }
            _memory = instance.GetMemory("memory") ?? throw new ModAbiException(ModId, "нет памяти memory");
            var abi = Export(instance.GetFunction("kh_abi_version")?.WrapFunc<int>(), "kh_abi_version");
            var shared = Export(instance.GetFunction("kh_shared")?.WrapFunc<int>(), "kh_shared");
            var load = Export(instance.GetFunction("kh_load")?.WrapFunc<int, int, int>(), "kh_load");
            _alloc = Export(instance.GetFunction("kh_alloc")?.WrapFunc<int, int>(), "kh_alloc");
            _free = Export(instance.GetFunction("kh_free")?.WrapAction<int>(), "kh_free");
            if (IsClient)
            {
                _look = Export(instance.GetFunction("kh_look")?.WrapFunc<int, int>(), "kh_look");
                _bank = Export(instance.GetFunction("kh_bank")?.WrapFunc<int, int, int, int>(), "kh_bank");
                _frame = Export(instance.GetFunction("kh_frame")?.WrapFunc<int, int, int>(), "kh_frame");
            }
            else
            {
                _prepare = Export(instance.GetFunction("kh_prepare")?.WrapFunc<int, int, int>(), "kh_prepare");
                _system = Export(instance.GetFunction("kh_system")?.WrapFunc<int, int, int>(), "kh_system");
                _event = Export(instance.GetFunction("kh_event")?.WrapFunc<int, int, int, int>(), "kh_event");
                _command = Export(instance.GetFunction("kh_command")?.WrapFunc<int, int, int, int, int>(), "kh_command");
            }
            data.Owner = this;

            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                // Реактор WASI: конструкторы libc, затем первый вызов поднимает рантайм .NET.
                instance.GetAction("_initialize")?.Invoke();
                var version = abi();
                if (version != ModAbi.Version)
                    throw new ModAbiException(ModId, $"ABI мода {version}, хоста {ModAbi.Version}");
                _shared = shared();
                if (_shared <= 0 || (long)_shared + ModAbi.SharedSize > _memory.GetLength())
                    throw new ModAbiException(ModId, $"kh_shared вернул {_shared}");

                var bytes = module.LoadBytes;
                var ptr = _alloc(bytes.Length);
                if (ptr == 0)
                    throw new ModCallException(ModId, ModAbi.ErrorOutOfMemory, "kh_alloc: нет памяти под ModLoadInfo");
                bytes.CopyTo(_memory.GetSpan(ptr, bytes.Length));
                var code = load(ptr, bytes.Length);
                _free(ptr);
                Check(code, "kh_load");
                Manifest = MemoryPackSerializer.Deserialize<ModManifest>(Output())
                    ?? throw new ModAbiException(ModId, "kh_load: пустой манифест");
            }
            catch (Exception ex) when (ex is not ModException)
            {
                throw Trap(ex, "загрузка");
            }
            StartupMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (Manifest.Abi != ModAbi.Version)
                throw new ModAbiException(ModId, $"манифест под ABI {Manifest.Abi}");
            Systems = module.Layouts(Manifest);
            _capacity = new int[Systems.Count];
            _data = new int[Systems.Count];
            _entities = new int[Systems.Count];
        }
        catch
        {
            _store.Dispose();
            throw;
        }
    }

    /// <summary>Имя мода.</summary>
    public string ModId { get; }

    /// <summary>Клиентский мод: облики, звуки и кадр вместо систем и приказов.</summary>
    public bool IsClient { get; }

    /// <summary>Что мод объявил в kh_load.</summary>
    public ModManifest Manifest { get; }

    /// <summary>Раскладки кадров систем; номер — id системы.</summary>
    public IReadOnlyList<ModSystemLayout> Systems { get; }

    /// <summary>Запуск рантайма мода и kh_load, мс.</summary>
    public double StartupMs { get; }

    /// <summary>Своё зерно kh.random экземпляра (если движок не дал <see cref="RandomSource"/>).</summary>
    public ModRandom Random { get; }

    /// <summary>Источник kh.random от мира (Scheduler.NextRandom движка); null — <see cref="Random"/>.</summary>
    public Func<double>? RandomSource { get; set; }

    /// <summary>Инструменты движка (kh.tool) для этого экземпляра: так им виден свой мир и свой мод; null — из <see cref="GameModuleOptions.Tools"/>.</summary>
    public ModToolHandler? Tools { get; set; }

    /// <summary>Мод, которому принадлежит экземпляр.</summary>
    public GameModule Module => _module;

    /// <summary>Была ловушка: экземпляр негоден.</summary>
    public bool Faulted { get; private set; }

    /// <summary>Сейчас линейной памяти, байт.</summary>
    public long MemoryBytes => _memory.GetLength();

    /// <summary>Номер системы по имени; нет — -1.</summary>
    public int SystemId(string name) => Array.FindIndex(Manifest.Systems, s => s.Name == name);

    /// <summary>Номер обработчика события; нет — -1.</summary>
    public int EventId(string name) => Array.IndexOf(Manifest.Events, name);

    /// <summary>Номер приказа; нет — -1.</summary>
    public int OrderId(string name) => Array.IndexOf(Manifest.Orders, name);

    internal double NextRandom() => RandomSource?.Invoke() ?? Random.Next();

    /// <summary>
    /// Кадр системы на n сущностей в памяти мода (kh_prepare — только если прежний мал). Сюда движок пишет столбцы и
    /// номера перед <see cref="InvokeSystem"/>.
    /// </summary>
    public SystemFrame PrepareSystem(int system, int n)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfUnusable();
        ArgumentOutOfRangeException.ThrowIfNegative(n);
        if ((uint)system >= (uint)Systems.Count)
            throw new ArgumentOutOfRangeException(nameof(system));
        var width = Systems[system].Width;
        if (n > _capacity[system])
        {
            int frame;
            _store.SetEpochDeadline(_callTicks);
            try
            {
                frame = Need(_prepare, "kh_prepare")(system, n);
            }
            catch (Exception ex) when (ex is not ModException)
            {
                throw Trap(ex, "kh_prepare");
            }
            if (frame == 0)
                throw new ModCallException(ModId, ModAbi.ErrorOutOfMemory, Error() ?? "kh_prepare вернул 0");
            var header = _memory.GetSpan(frame, ModAbi.FrameSize);
            var capacity = BinaryPrimitives.ReadInt32LittleEndian(header[ModAbi.FrameCapacity..]);
            var frameWidth = BinaryPrimitives.ReadInt32LittleEndian(header[ModAbi.FrameWidth..]);
            var data = BinaryPrimitives.ReadInt32LittleEndian(header[ModAbi.FrameData..]);
            var entities = BinaryPrimitives.ReadInt32LittleEndian(header[ModAbi.FrameEntities..]);
            if (frameWidth != width || capacity < n)
                throw new ModAbiException(ModId, $"кадр {Systems[system].Info.Name}: ширина {frameWidth} (ждали {width}), ёмкость {capacity} < {n}");
            CheckRange(data, (long)width * capacity * sizeof(double), "кадр: столбцы");
            CheckRange(entities, (long)capacity * sizeof(uint), "кадр: номера");
            _capacity[system] = capacity;
            _data[system] = data;
            _entities[system] = entities;
        }
        return new SystemFrame(
            _memory.GetSpan<double>(_data[system], width * n),
            _memory.GetSpan<uint>(_entities[system], n),
            width);
    }

    /// <summary>Свежие спаны кадра (после вызова мода прежние могли устареть).</summary>
    public SystemFrame Frame(int system, int n)
    {
        if ((uint)system >= (uint)Systems.Count || n > _capacity[system])
            throw new ArgumentOutOfRangeException(nameof(n), "кадр не подготовлен");
        var width = Systems[system].Width;
        return new SystemFrame(_memory.GetSpan<double>(_data[system], width * n), _memory.GetSpan<uint>(_entities[system], n), width);
    }

    /// <summary>kh_system над n сущностями подготовленного кадра. Возвращает буфер команд (формат движка) в памяти мода.</summary>
    public ReadOnlySpan<byte> InvokeSystem(int system, int n, in ModCall call)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfUnusable();
        WriteCall(call);
        int code;
        try
        {
            code = Need(_system, "kh_system")(system, n);
        }
        catch (Exception ex) when (ex is not ModException)
        {
            throw Trap(ex, Systems[system].Info.Name);
        }
        Check(code, Systems[system].Info.Name);
        return Output();
    }

    /// <summary>
    /// Система целиком: столбцы data (ширина × n, как QueryColumns.Data) и номера — в память мода, kh_system,
    /// столбцы компонентов из Write — обратно в data. Возвращает буфер команд (до следующего вызова мода).
    /// </summary>
    public ReadOnlySpan<byte> RunSystem(int system, ReadOnlySpan<uint> entities, Span<double> data, in ModCall call)
    {
        var n = entities.Length;
        var layout = Systems[system];
        if (data.Length != layout.Width * n)
            throw new ArgumentException($"столбцов {data.Length}, ждали {layout.Width} × {n}", nameof(data));
        var frame = PrepareSystem(system, n);
        data.CopyTo(frame.Data);
        entities.CopyTo(frame.Entities);
        InvokeSystem(system, n, call);
        var back = Frame(system, n).Data;
        foreach (var (first, count) in layout.Write)
            back.Slice(first * n, count * n).CopyTo(data.Slice(first * n, count * n));
        return Output();
    }

    /// <summary>kh_event: событие payload обработчику handler. Возвращает буфер команд.</summary>
    public ReadOnlySpan<byte> RaiseEvent(int handler, ReadOnlySpan<byte> payload, in ModCall call)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfUnusable();
        var ptr = Scratch(payload);
        WriteCall(call);
        int code;
        try
        {
            code = Need(_event, "kh_event")(handler, ptr, payload.Length);
        }
        catch (Exception ex) when (ex is not ModException)
        {
            throw Trap(ex, $"событие {handler}");
        }
        Check(code, $"событие {Name(Manifest.Events, handler)}");
        return Output();
    }

    /// <summary>kh_command: приказ игрока player. Возвращает буфер команд.</summary>
    public ReadOnlySpan<byte> RunOrder(int order, uint player, ReadOnlySpan<byte> payload, in ModCall call)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfUnusable();
        var ptr = Scratch(payload);
        WriteCall(call);
        int code;
        try
        {
            code = Need(_command, "kh_command")(order, (int)player, ptr, payload.Length);
        }
        catch (Exception ex) when (ex is not ModException)
        {
            throw Trap(ex, $"приказ {Name(Manifest.Orders, order)}");
        }
        Check(code, $"приказ {Name(Manifest.Orders, order)}");
        return Output();
    }

    /// <summary>
    /// kh_look: облик вида Manifest.Looks[look] — поток облика (ClientAbi) в памяти мода, верен до следующего вызова.
    /// Предел времени — как у загрузки: облик рисуется раз.
    /// </summary>
    public ReadOnlySpan<byte> Look(int look)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfUnusable();
        _store.SetEpochDeadline(_module.Host.EpochTicks(_module.Host.Options.LoadTimeoutMs));
        int code;
        try
        {
            code = Need(_look, "kh_look")(look);
        }
        catch (Exception ex) when (ex is not ModException)
        {
            throw Trap(ex, $"облик {Name(Manifest.Looks, look)}");
        }
        Check(code, $"облик {Name(Manifest.Looks, look)}");
        return Output();
    }

    /// <summary>kh_bank: вариант variant записи Manifest.Bank[entry] при частоте rate — channels:u8 frames:u32 f32×.</summary>
    public ReadOnlySpan<byte> Bank(int entry, int variant, int rate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfUnusable();
        _store.SetEpochDeadline(_module.Host.EpochTicks(_module.Host.Options.LoadTimeoutMs));
        var name = (uint)entry < (uint)Manifest.Bank.Length ? Manifest.Bank[entry].Name : $"№{entry}";
        int code;
        try
        {
            code = Need(_bank, "kh_bank")(entry, variant, rate);
        }
        catch (Exception ex) when (ex is not ModException)
        {
            throw Trap(ex, $"звук {name}");
        }
        Check(code, $"звук {name}");
        return Output();
    }

    /// <summary>kh_frame: вход кадра (ClientAbi) — в память мода; возвращает выход кадра (до следующего вызова).</summary>
    public ReadOnlySpan<byte> Frame(ReadOnlySpan<byte> input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfUnusable();
        var ptr = Scratch(input);
        _store.SetEpochDeadline(_callTicks);
        int code;
        try
        {
            code = Need(_frame, "kh_frame")(ptr, input.Length);
        }
        catch (Exception ex) when (ex is not ModException)
        {
            throw Trap(ex, "кадр");
        }
        Check(code, "кадр");
        return Output();
    }

    /// <summary>kh_alloc напрямую (тесты пределов): адрес или 0.</summary>
    public int Allocate(int size)
    {
        ThrowIfUnusable();
        _store.SetEpochDeadline(_callTicks);
        try
        {
            return _alloc(size);
        }
        catch (Exception ex) when (ex is not ModException)
        {
            throw Trap(ex, "kh_alloc");
        }
    }

    /// <summary>Освобождает хранилище Wasmtime экземпляра.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _store.Dispose();
    }

    private void WriteCall(in ModCall call)
    {
        var shared = _memory.GetSpan(_shared, ModAbi.SharedSize);
        BinaryPrimitives.WriteUInt64LittleEndian(shared[ModAbi.SharedTick..], call.Tick);
        BinaryPrimitives.WriteDoubleLittleEndian(shared[ModAbi.SharedDt..], call.Dt);
        BinaryPrimitives.WriteUInt32LittleEndian(shared[ModAbi.SharedNextEntity..], call.NextEntity);
        _store.SetEpochDeadline(_callTicks);
    }

    /// <summary>Выход последнего вызова: буфер команд или манифест.</summary>
    private ReadOnlySpan<byte> Output()
    {
        var shared = _memory.GetSpan(_shared, ModAbi.SharedSize);
        var ptr = BinaryPrimitives.ReadInt32LittleEndian(shared[ModAbi.SharedOutPtr..]);
        var len = BinaryPrimitives.ReadInt32LittleEndian(shared[ModAbi.SharedOutLen..]);
        if (len == 0)
            return [];
        CheckRange(ptr, len, "выход");
        return _memory.GetSpan(ptr, len);
    }

    private string? Error()
    {
        var shared = _memory.GetSpan(_shared, ModAbi.SharedSize);
        var ptr = BinaryPrimitives.ReadInt32LittleEndian(shared[ModAbi.SharedErrPtr..]);
        var len = BinaryPrimitives.ReadInt32LittleEndian(shared[ModAbi.SharedErrLen..]);
        if (len <= 0 || ptr <= 0 || (long)ptr + len > _memory.GetLength())
            return null;
        return Encoding.UTF8.GetString(_memory.GetSpan(ptr, Math.Min(len, 4096)));
    }

    private void Check(int code, string what)
    {
        if (code < 0)
            throw new ModCallException(ModId, code, $"{what}: {Error() ?? "без текста"}");
    }

    private void CheckRange(int ptr, long len, string what)
    {
        if (ptr <= 0 || len < 0 || ptr + len > _memory.GetLength())
            throw new ModAbiException(ModId, $"{what}: [{ptr}, +{len}) вне памяти");
    }

    /// <summary>payload — в буфер в памяти мода (растёт через kh_alloc).</summary>
    private int Scratch(ReadOnlySpan<byte> payload)
    {
        if (payload.Length > _scratchCapacity || _scratch == 0)
        {
            if (_scratch != 0)
                _free(_scratch);
            var capacity = Math.Max(256, Math.Max(payload.Length, _scratchCapacity * 2));
            _scratch = Allocate(capacity);
            _scratchCapacity = _scratch == 0 ? 0 : capacity;
            if (_scratch == 0)
                throw new ModCallException(ModId, ModAbi.ErrorOutOfMemory, $"kh_alloc: нет {capacity} байт под payload");
        }
        payload.CopyTo(_memory.GetSpan(_scratch, payload.Length));
        return _scratch;
    }

    private void ThrowIfUnusable()
    {
        if (Faulted)
            throw new ModException(ModId, "экземпляр негоден после ловушки");
        if (_module.Disabled)
            throw new ModDisabledException(ModId, _module.Strikes);
    }

    private ModTrapException Trap(Exception ex, string what)
    {
        Faulted = true;
        var timeout = ex is TrapException { Type: TrapCode.Interrupt };
        if (timeout)
            _module.Strike();
        var reason = timeout ? $"перерасход времени (> {_module.Host.Options.CallTimeoutMs} мс)"
            : _storeData.ExitCode is { } code ? $"мод завершился (proc_exit {code}){Flush()}"
            : ex.Message;
        return new ModTrapException(ModId, $"{what}: {reason}", timeout, ex);
    }

    /// <summary>Недописанный вывод мода (например, «Fatal error» рантайма) — в текст ошибки.</summary>
    private string Flush() => _storeData.LastError is { } line ? $": {line}" : "";

    private static string Name(string[] names, int id) => (uint)id < (uint)names.Length ? names[id] : $"№{id}";

    private T Need<T>(T? function, string name) where T : class =>
        function ?? throw new ModAbiException(ModId, IsClient ? $"{name}: у клиентского мода его нет" : $"{name}: у мода хоста его нет");

    private T Export<T>(T? function, string name) where T : class =>
        function ?? throw new ModAbiException(ModId, $"нет экспорта {name} нужного вида");

    /// <summary>Хеш имени мода, одинаковый между запусками (string.GetHashCode — нет).</summary>
    private static int StableHash(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
            hash = (hash ^ c) * 16777619u;
        return (int)hash;
    }
}
