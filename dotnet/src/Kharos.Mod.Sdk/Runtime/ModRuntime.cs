using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Kharos.Mod.Abi;
using MemoryPack;

namespace Kharos.Mod.Sdk.Runtime;

/// <summary>
/// Сторона мода в ABI (docs/dotnet.md, §4.2): то, что делают экспорты <c>kh_*</c>. Их пишет за мододела
/// build/ModExports.cs, сюда — только вызовы. Один мод на модуль wasm (или на AssemblyLoadContext в режиме CLR); вызовы
/// — из одного потока хоста. Работа — в методах *Core без адресов wasm32: их зовут и экспорты wasm (адреса — i32 в
/// общем блоке), и мост режима CLR (<see cref="ClrBridge"/>, адреса — nint).
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static unsafe class ModRuntime
{
    private static readonly CommandWriter Commands = new();
    private static readonly ReplyBuffer Reply = new();
    private static byte* _shared;
    private static NativeBuffer _out;
    private static NativeBuffer _error;
    private static ModBuilder? _builder;
    private static SystemSlot[] _systems = [];
    private static byte* _lastOut;
    private static int _lastLength;

    /// <summary>Схема мира после kh_load — для инструментов (<see cref="Tools"/>).</summary>
    internal static WorldSchema? Schema { get; private set; }

    /// <summary>Система: объявление, привязка и части кадра (главная и таблицы) вне GC.</summary>
    private sealed class SystemSlot(SystemDeclaration declaration, SystemBinding binding)
    {
        public SystemDeclaration Declaration { get; } = declaration;
        public SystemBinding Binding { get; } = binding;
        public FramePart Main { get; } = new(binding.Width);
        public FramePart[] Tables { get; } = [.. Enumerable.Range(0, binding.Tables).Select(i => new FramePart(binding.Table(i).Width))];

        /// <summary>Заголовок кадра для wasm (FrameSize + TableSize на таблицу).</summary>
        public byte* Header;

        public FramePart Part(int part) => part == 0 ? Main : Tables[part - 1];
    }

    // ===== Экспорты wasm (адреса — i32) =====

    /// <summary>kh_abi_version.</summary>
    public static int AbiVersion() => ModAbi.Version;

    /// <summary>kh_alloc: память под данные хоста, выровнена на 16; 0 — нет памяти.</summary>
    public static int Alloc(int size)
    {
        if (size <= 0)
            return 0;
        try
        {
            return (int)(nint)NativeMemory.AlignedAlloc((nuint)size, 16);
        }
        catch (OutOfMemoryException)
        {
            return 0;
        }
    }

    /// <summary>kh_free.</summary>
    public static void Free(int ptr)
    {
        if (ptr != 0)
            NativeMemory.AlignedFree((void*)(nint)ptr);
    }

    /// <summary>kh_shared: адрес общего блока.</summary>
    public static int Shared() => (int)(nint)SharedBlock();

    /// <summary>kh_load: разбор ModLoadInfo, Setup и Load мода, привязка систем; в out — ModManifest.</summary>
    public static int Load(delegate*<Mod> create, int ptr, int len)
    {
        Mod mod;
        try
        {
            mod = create();
        }
        catch (Exception ex)
        {
            return ToShared(Fail(ModAbi.ErrorException, $"kh_load: {ex.GetType().Name}: {ex.Message}"));
        }
        return ToShared(LoadCore(mod, new ReadOnlySpan<byte>((void*)(nint)ptr, len)));
    }

    /// <summary>kh_prepare: кадр системы на n сущностей; растит буферы, если мало.</summary>
    public static int Prepare(int system, int n)
    {
        var code = PrepareCore(system, 0, n);
        return code < 0 ? FailFrame() : (int)(nint)WriteHeader(_systems[system]);
    }

    /// <summary>kh_prepare_table: побочная таблица системы на n сущностей; адрес того же кадра.</summary>
    public static int PrepareTable(int system, int table, int n)
    {
        if (_builder is not null && (uint)system < (uint)_systems.Length && (uint)table >= (uint)_systems[system].Tables.Length)
        {
            Fail(ModAbi.ErrorUnknownId, $"kh_prepare_table: у системы {system} нет таблицы {table}");
            return 0;
        }
        var code = PrepareCore(system, table + 1, n);
        return code < 0 ? FailFrame() : (int)(nint)WriteHeader(_systems[system]);
    }

    /// <summary>kh_system: система над n сущностями кадра (счёт таблиц — в заголовке кадра); в out — буфер команд.</summary>
    public static int System(int system, int n)
    {
        if (_builder is null)
            return ToShared(Fail(ModAbi.ErrorNotLoaded, "kh_system до kh_load"));
        if ((uint)system >= (uint)_systems.Length)
            return ToShared(Fail(ModAbi.ErrorUnknownId, $"kh_system: нет системы {system}"));
        var slot = _systems[system];
        Span<int> counts = stackalloc int[1 + slot.Tables.Length];
        counts[0] = n;
        for (var t = 0; t < slot.Tables.Length; t++)
            counts[t + 1] = slot.Header is null ? 0 : *(int*)(slot.Header + ModAbi.FrameSize + t * ModAbi.TableSize + ModAbi.TableCount);
        var shared = SharedBlock();
        return ToShared(SystemCore(system, counts, *(ulong*)(shared + ModAbi.SharedTick), *(double*)(shared + ModAbi.SharedDt), *(uint*)(shared + ModAbi.SharedNextEntity)));
    }

    /// <summary>kh_event: обработчик события; в out — буфер команд.</summary>
    public static int Event(int handler, int ptr, int len)
    {
        var shared = SharedBlock();
        return ToShared(EventCore(handler, new ReadOnlySpan<byte>((void*)(nint)ptr, Math.Max(0, len)), *(ulong*)(shared + ModAbi.SharedTick), *(double*)(shared + ModAbi.SharedDt), *(uint*)(shared + ModAbi.SharedNextEntity)));
    }

    /// <summary>kh_command: приказ игрока (права — в общем блоке); в out — буфер команд.</summary>
    public static int Command(int order, int player, int ptr, int len)
    {
        var shared = SharedBlock();
        return ToShared(CommandCore(order, (uint)player, *(uint*)(shared + ModAbi.SharedRights), new ReadOnlySpan<byte>((void*)(nint)ptr, Math.Max(0, len)),
            *(ulong*)(shared + ModAbi.SharedTick), *(double*)(shared + ModAbi.SharedDt), *(uint*)(shared + ModAbi.SharedNextEntity)));
    }

    /// <summary>kh_query: запрос (только чтение); в out — ответ.</summary>
    public static int Query(int query, int ptr, int len)
    {
        var shared = SharedBlock();
        return ToShared(QueryCore(query, *(uint*)(shared + ModAbi.SharedRights), new ReadOnlySpan<byte>((void*)(nint)ptr, Math.Max(0, len)),
            *(ulong*)(shared + ModAbi.SharedTick), *(double*)(shared + ModAbi.SharedDt)));
    }

    // ===== Работа (без адресов wasm) =====

    /// <summary>Загрузка: Setup, схема, Load, привязка систем; в выходе — ModManifest.</summary>
    internal static int LoadCore(Mod mod, ReadOnlySpan<byte> bytes)
    {
        try
        {
            var info = MemoryPackSerializer.Deserialize<ModLoadInfo>(bytes);
            if (info is null)
                return Fail(ModAbi.ErrorBadInput, "kh_load: пустой ModLoadInfo");
            if (info.Abi != ModAbi.Version)
                return Fail(ModAbi.ErrorAbi, $"kh_load: ABI хоста {info.Abi}, мода {ModAbi.Version}");

            var builder = new ModBuilder();
            mod.Setup(builder);
            var manifest = builder.ToManifest();
            var schema = new WorldSchema(info.Components, info.Layers);
            Schema = schema;
            mod.Load(new LoadContext(info, schema));

            var systems = new SystemSlot[builder.Systems.Count];
            for (var id = 0; id < systems.Length; id++)
            {
                var declaration = builder.Systems[id];
                var binding = new SystemBinding(declaration.Name, declaration.QueryNames, declaration.TableInfos, schema);
                declaration.System.Bind(binding);
                systems[id] = new SystemSlot(declaration, binding);
            }

            Release();
            _builder = builder;
            _systems = systems;
            _out.Clear();
            _out.Write(MemoryPackSerializer.Serialize(manifest));
            return Done(_out.Pointer, _out.Length);
        }
        catch (Exception ex)
        {
            return Fail(ex is MemoryPackSerializationException ? ModAbi.ErrorBadInput : ModAbi.ErrorException, $"kh_load: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Часть кадра (0 — главная, t + 1 — таблица t) на n сущностей.</summary>
    internal static int PrepareCore(int system, int part, int n)
    {
        if (_builder is null)
            return Fail(ModAbi.ErrorNotLoaded, "kh_prepare до kh_load");
        if ((uint)system >= (uint)_systems.Length || n < 0)
            return Fail(ModAbi.ErrorUnknownId, $"kh_prepare: нет системы {system} или n = {n}");
        var slot = _systems[system];
        if ((uint)part > (uint)slot.Tables.Length)
            return Fail(ModAbi.ErrorUnknownId, $"kh_prepare: у системы {system} нет части {part}");
        return slot.Part(part).Reserve(n) ? ModAbi.Ok : Fail(ModAbi.ErrorOutOfMemory, $"kh_prepare: нет памяти на {n} сущностей");
    }

    /// <summary>Адреса части кадра после <see cref="PrepareCore"/> (режим CLR).</summary>
    internal static (nint Data, nint Entities, int Capacity) PartOf(int system, int part)
    {
        var frame = _systems[system].Part(part);
        return ((nint)frame.Data, (nint)frame.Entities, frame.Capacity);
    }

    /// <summary>Система: counts — сущностей в главной части и в таблицах; в выходе — буфер команд.</summary>
    internal static int SystemCore(int system, ReadOnlySpan<int> counts, ulong tick, double dt, uint next)
    {
        if (_builder is null)
            return Fail(ModAbi.ErrorNotLoaded, "kh_system до kh_load");
        if ((uint)system >= (uint)_systems.Length)
            return Fail(ModAbi.ErrorUnknownId, $"kh_system: нет системы {system}");
        var slot = _systems[system];
        if (counts.Length != 1 + slot.Tables.Length)
            return Fail(ModAbi.ErrorBadInput, $"kh_system: частей {counts.Length}, у системы {1 + slot.Tables.Length}");
        for (var part = 0; part < counts.Length; part++)
        {
            var frame = slot.Part(part);
            if (counts[part] < 0 || counts[part] > frame.Capacity || (counts[part] > 0 && frame.Data is null))
                return Fail(ModAbi.ErrorCapacity, $"kh_system: часть {part}: n = {counts[part]}, место на {frame.Capacity}");
            frame.Begin(counts[part]);
        }
        Commands.Reset(next);
        try
        {
            var ctx = new SystemContext(slot.Main, slot.Tables, slot.Binding, tick, dt, Commands);
            slot.Declaration.System.Run(ctx);
        }
        catch (Exception ex)
        {
            return Fail(ModAbi.ErrorException, $"{slot.Declaration.Name}: {ex.GetType().Name}: {ex.Message}");
        }
        return Done(Commands.Pointer, Commands.Length);
    }

    /// <summary>Событие; в выходе — буфер команд.</summary>
    internal static int EventCore(int handler, ReadOnlySpan<byte> payload, ulong tick, double dt, uint next)
    {
        if (_builder is null)
            return Fail(ModAbi.ErrorNotLoaded, "kh_event до kh_load");
        if ((uint)handler >= (uint)_builder.Events.Count)
            return Fail(ModAbi.ErrorUnknownId, $"kh_event: нет обработчика {handler}");
        Commands.Reset(next);
        var (name, run) = _builder.Events[handler];
        try
        {
            run(new EventContext(name, payload, tick, dt, Commands));
        }
        catch (Exception ex)
        {
            return Fail(ModAbi.ErrorException, $"событие {name}: {ex.GetType().Name}: {ex.Message}");
        }
        return Done(Commands.Pointer, Commands.Length);
    }

    /// <summary>Приказ; в выходе — буфер команд.</summary>
    internal static int CommandCore(int order, uint player, uint rights, ReadOnlySpan<byte> payload, ulong tick, double dt, uint next)
    {
        if (_builder is null)
            return Fail(ModAbi.ErrorNotLoaded, "kh_command до kh_load");
        if ((uint)order >= (uint)_builder.Orders.Count)
            return Fail(ModAbi.ErrorUnknownId, $"kh_command: нет приказа {order}");
        Commands.Reset(next);
        var (name, run) = _builder.Orders[order];
        try
        {
            run(new OrderContext(name, player, (OrderRights)rights, payload, tick, dt, Commands));
        }
        catch (Exception ex)
        {
            return Fail(ModAbi.ErrorException, $"приказ {name}: {ex.GetType().Name}: {ex.Message}");
        }
        return Done(Commands.Pointer, Commands.Length);
    }

    /// <summary>Запрос; в выходе — ответ.</summary>
    internal static int QueryCore(int query, uint rights, ReadOnlySpan<byte> payload, ulong tick, double dt)
    {
        if (_builder is null)
            return Fail(ModAbi.ErrorNotLoaded, "kh_query до kh_load");
        if ((uint)query >= (uint)_builder.Queries.Count)
            return Fail(ModAbi.ErrorUnknownId, $"kh_query: нет запроса {query}");
        Reply.Clear();
        var (name, run) = _builder.Queries[query];
        try
        {
            run(new QueryContext(name, (OrderRights)rights, payload, tick, dt, Reply));
        }
        catch (Exception ex)
        {
            return Fail(ModAbi.ErrorException, $"запрос {name}: {ex.GetType().Name}: {ex.Message}");
        }
        return Done(Reply.Pointer, Reply.Length);
    }

    /// <summary>Выход последнего успешного вызова (буфер команд, манифест, ответ запроса).</summary>
    internal static ReadOnlySpan<byte> Output => new(_lastOut, _lastLength);

    /// <summary>Текст последней ошибки.</summary>
    internal static string Error => Encoding.UTF8.GetString(_error.Written);

    /// <summary>Освобождает память вне GC (кадры, буферы): режим CLR выгружает мод вместе с хостом.</summary>
    internal static void Release()
    {
        foreach (var slot in _systems)
        {
            slot.Main.Free();
            foreach (var table in slot.Tables)
                table.Free();
            NativeMemory.AlignedFree(slot.Header);
            slot.Header = null;
        }
        _systems = [];
    }

    /// <summary>Всё, включая общий блок и буферы выхода (последний вызов экземпляра).</summary>
    internal static void ReleaseAll()
    {
        Release();
        _builder = null;
        Commands.Free();
        Reply.Free();
        _out.Free();
        _error.Free();
        NativeMemory.AlignedFree(_shared);
        _shared = null;
        _lastOut = null;
        _lastLength = 0;
    }

    internal static byte* SharedBlock()
    {
        if (_shared is null)
            _shared = (byte*)NativeMemory.AlignedAlloc(ModAbi.SharedSize, 8);
        return _shared;
    }

    /// <summary>Заголовок кадра для хоста wasm: части и их адреса (i32).</summary>
    private static byte* WriteHeader(SystemSlot slot)
    {
        if (slot.Header is null)
        {
            var size = ModAbi.FrameSize + ModAbi.TableSize * slot.Tables.Length;
            slot.Header = (byte*)NativeMemory.AlignedAlloc((nuint)size, 8);
            new Span<byte>(slot.Header, size).Clear();
        }
        var header = slot.Header;
        *(int*)(header + ModAbi.FrameSystem) = Array.IndexOf(_systems, slot);
        *(int*)(header + ModAbi.FrameCapacity) = slot.Main.Capacity;
        *(int*)(header + ModAbi.FrameWidth) = slot.Main.Width;
        *(int*)(header + ModAbi.FrameData) = (int)(nint)slot.Main.Data;
        *(int*)(header + ModAbi.FrameEntities) = (int)(nint)slot.Main.Entities;
        *(int*)(header + ModAbi.FrameTables) = slot.Tables.Length;
        for (var t = 0; t < slot.Tables.Length; t++)
        {
            var table = header + ModAbi.FrameSize + t * ModAbi.TableSize;
            *(int*)(table + ModAbi.TableCapacity) = slot.Tables[t].Capacity;
            *(int*)(table + ModAbi.TableWidth) = slot.Tables[t].Width;
            *(int*)(table + ModAbi.TableData) = (int)(nint)slot.Tables[t].Data;
            *(int*)(table + ModAbi.TableEntities) = (int)(nint)slot.Tables[t].Entities;
        }
        return header;
    }

    /// <summary>Итог вызова — в общий блок wasm: адрес и длина выхода или текста ошибки.</summary>
    private static int ToShared(int code)
    {
        var shared = SharedBlock();
        if (code >= 0)
        {
            *(int*)(shared + ModAbi.SharedOutPtr) = (int)(nint)_lastOut;
            *(int*)(shared + ModAbi.SharedOutLen) = _lastLength;
            *(int*)(shared + ModAbi.SharedErrPtr) = 0;
            *(int*)(shared + ModAbi.SharedErrLen) = 0;
        }
        else
        {
            *(int*)(shared + ModAbi.SharedOutPtr) = 0;
            *(int*)(shared + ModAbi.SharedOutLen) = 0;
            *(int*)(shared + ModAbi.SharedErrPtr) = (int)(nint)_error.Pointer;
            *(int*)(shared + ModAbi.SharedErrLen) = _error.Length;
        }
        return code;
    }

    private static int FailFrame()
    {
        ToShared(-1);
        return 0;
    }

    /// <summary>Успех: выход запомнен и записан в общий блок (его читают и экспорты клиента).</summary>
    internal static int Done(byte* ptr, int len)
    {
        _lastOut = ptr;
        _lastLength = len;
        return ToShared(ModAbi.Ok);
    }

    internal static int Fail(int code, string message)
    {
        _error.Clear();
        var count = Encoding.UTF8.GetByteCount(message);
        Encoding.UTF8.GetBytes(message, _error.Room(count));
        _error.Length = count;
        _lastOut = null;
        _lastLength = 0;
        return ToShared(code);
    }
}

/// <summary>Ответ запроса: байты вне GC (их адрес отдаётся хосту).</summary>
internal sealed unsafe class ReplyBuffer
{
    private NativeBuffer _buffer;

    public byte* Pointer => _buffer.Pointer;
    public int Length => _buffer.Length;

    public void Clear() => _buffer.Clear();

    public void Set(ReadOnlySpan<byte> bytes)
    {
        _buffer.Clear();
        _buffer.Write(bytes);
    }

    public void Free() => _buffer.Free();
}
