using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Kharos.Mod.Abi;
using MemoryPack;

namespace Kharos.Mod.Sdk.Runtime;

/// <summary>
/// Сторона мода в ABI (docs/dotnet.md, §4.2): то, что делают экспорты <c>kh_*</c>. Их пишет за мододела
/// build/ModExports.cs, сюда — только вызовы. Один мод на модуль wasm; вызовы — из одного потока хоста.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static unsafe class ModRuntime
{
    private static readonly CommandWriter Commands = new();
    private static byte* _shared;
    private static NativeBuffer _out;
    private static NativeBuffer _error;
    private static ModBuilder? _builder;
    private static Frame[] _frames = [];

    /// <summary>Схема мира после kh_load — для инструментов (<see cref="Tools"/>).</summary>
    internal static WorldSchema? Schema { get; private set; }

    /// <summary>Кадр системы: блок столбцов и номера сущностей в памяти вне GC.</summary>
    private struct Frame
    {
        public byte* Header;
        public int Capacity;
        public int Width;
        public double* Data;
        public uint* Entities;
    }

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
        try
        {
            var info = MemoryPackSerializer.Deserialize<ModLoadInfo>(new ReadOnlySpan<byte>((void*)(nint)ptr, len));
            if (info is null)
                return Fail(ModAbi.ErrorBadInput, "kh_load: пустой ModLoadInfo");
            if (info.Abi != ModAbi.Version)
                return Fail(ModAbi.ErrorAbi, $"kh_load: ABI хоста {info.Abi}, мода {ModAbi.Version}");

            var mod = create();
            var builder = new ModBuilder();
            mod.Setup(builder);
            var manifest = builder.ToManifest();
            var schema = new WorldSchema(info.Components);
            mod.Load(new LoadContext(info, schema));

            var frames = new Frame[builder.Systems.Count];
            for (var id = 0; id < frames.Length; id++)
            {
                var declaration = builder.Systems[id];
                var binding = new SystemBinding(declaration.Name, declaration.QueryNames, schema);
                declaration.System.Bind(binding);
                frames[id].Width = binding.Width;
            }

            _builder = builder;
            _frames = frames;
            Schema = schema;
            _out.Clear();
            _out.Write(MemoryPackSerializer.Serialize(manifest));
            return Done(_out.Pointer, _out.Length);
        }
        catch (Exception ex)
        {
            return Fail(ex is MemoryPackSerializationException ? ModAbi.ErrorBadInput : ModAbi.ErrorException, $"kh_load: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>kh_prepare: кадр системы на n сущностей; растит буферы, если мало.</summary>
    public static int Prepare(int system, int n)
    {
        if (_builder is null)
            return FailFrame(ModAbi.ErrorNotLoaded, "kh_prepare до kh_load");
        if ((uint)system >= (uint)_frames.Length || n < 0)
            return FailFrame(ModAbi.ErrorUnknownId, $"kh_prepare: нет системы {system} или n = {n}");
        ref var frame = ref _frames[system];
        if (frame.Header is null)
            frame.Header = (byte*)NativeMemory.AlignedAlloc(ModAbi.FrameSize, 8);
        if (n > frame.Capacity || frame.Data is null)
        {
            var capacity = Math.Max(n, Math.Max(16, frame.Capacity + frame.Capacity / 2));
            try
            {
                NativeMemory.AlignedFree(frame.Data);
                NativeMemory.AlignedFree(frame.Entities);
                frame.Data = null;
                frame.Entities = null;
                frame.Data = (double*)NativeMemory.AlignedAlloc((nuint)Math.Max(1, frame.Width) * (nuint)capacity * sizeof(double), 16);
                frame.Entities = (uint*)NativeMemory.AlignedAlloc((nuint)capacity * sizeof(uint), 16);
                frame.Capacity = capacity;
            }
            catch (OutOfMemoryException)
            {
                frame.Capacity = 0;
                return FailFrame(ModAbi.ErrorOutOfMemory, $"kh_prepare: нет памяти на {n} сущностей");
            }
        }
        var header = frame.Header;
        *(int*)(header + ModAbi.FrameSystem) = system;
        *(int*)(header + ModAbi.FrameCapacity) = frame.Capacity;
        *(int*)(header + ModAbi.FrameWidth) = frame.Width;
        *(int*)(header + ModAbi.FrameData) = (int)(nint)frame.Data;
        *(int*)(header + ModAbi.FrameEntities) = (int)(nint)frame.Entities;
        return (int)(nint)header;
    }

    /// <summary>kh_system: система над n сущностями кадра; в out — буфер команд.</summary>
    public static int System(int system, int n)
    {
        if (_builder is null)
            return Fail(ModAbi.ErrorNotLoaded, "kh_system до kh_load");
        if ((uint)system >= (uint)_frames.Length)
            return Fail(ModAbi.ErrorUnknownId, $"kh_system: нет системы {system}");
        ref var frame = ref _frames[system];
        if (n < 0 || n > frame.Capacity)
            return Fail(ModAbi.ErrorCapacity, $"kh_system: n = {n}, кадр на {frame.Capacity}");
        var shared = SharedBlock();
        Commands.Reset(*(uint*)(shared + ModAbi.SharedNextEntity));
        try
        {
            var ctx = new SystemContext(frame.Data, frame.Entities, n, *(ulong*)(shared + ModAbi.SharedTick), *(double*)(shared + ModAbi.SharedDt), Commands);
            _builder.Systems[system].System.Run(ctx);
        }
        catch (Exception ex)
        {
            return Fail(ModAbi.ErrorException, $"{_builder.Systems[system].Name}: {ex.GetType().Name}: {ex.Message}");
        }
        return Done(Commands.Pointer, Commands.Length);
    }

    /// <summary>kh_event: обработчик события; в out — буфер команд.</summary>
    public static int Event(int handler, int ptr, int len)
    {
        if (_builder is null)
            return Fail(ModAbi.ErrorNotLoaded, "kh_event до kh_load");
        if ((uint)handler >= (uint)_builder.Events.Count || len < 0)
            return Fail(ModAbi.ErrorUnknownId, $"kh_event: нет обработчика {handler}");
        var shared = SharedBlock();
        Commands.Reset(*(uint*)(shared + ModAbi.SharedNextEntity));
        var (name, run) = _builder.Events[handler];
        try
        {
            run(new EventContext(name, new ReadOnlySpan<byte>((void*)(nint)ptr, len), *(ulong*)(shared + ModAbi.SharedTick), *(double*)(shared + ModAbi.SharedDt), Commands));
        }
        catch (Exception ex)
        {
            return Fail(ModAbi.ErrorException, $"событие {name}: {ex.GetType().Name}: {ex.Message}");
        }
        return Done(Commands.Pointer, Commands.Length);
    }

    /// <summary>kh_command: приказ игрока; в out — буфер команд.</summary>
    public static int Command(int order, int player, int ptr, int len)
    {
        if (_builder is null)
            return Fail(ModAbi.ErrorNotLoaded, "kh_command до kh_load");
        if ((uint)order >= (uint)_builder.Orders.Count || len < 0)
            return Fail(ModAbi.ErrorUnknownId, $"kh_command: нет приказа {order}");
        var shared = SharedBlock();
        Commands.Reset(*(uint*)(shared + ModAbi.SharedNextEntity));
        var (name, run) = _builder.Orders[order];
        try
        {
            run(new OrderContext(name, (uint)player, new ReadOnlySpan<byte>((void*)(nint)ptr, len), *(ulong*)(shared + ModAbi.SharedTick), *(double*)(shared + ModAbi.SharedDt), Commands));
        }
        catch (Exception ex)
        {
            return Fail(ModAbi.ErrorException, $"приказ {name}: {ex.GetType().Name}: {ex.Message}");
        }
        return Done(Commands.Pointer, Commands.Length);
    }

    private static byte* SharedBlock()
    {
        if (_shared is null)
            _shared = (byte*)NativeMemory.AlignedAlloc(ModAbi.SharedSize, 8);
        return _shared;
    }

    private static int Done(byte* ptr, int len)
    {
        var shared = SharedBlock();
        *(int*)(shared + ModAbi.SharedOutPtr) = (int)(nint)ptr;
        *(int*)(shared + ModAbi.SharedOutLen) = len;
        *(int*)(shared + ModAbi.SharedErrPtr) = 0;
        *(int*)(shared + ModAbi.SharedErrLen) = 0;
        return ModAbi.Ok;
    }

    private static int FailFrame(int code, string message)
    {
        Fail(code, message);
        return 0;
    }

    private static int Fail(int code, string message)
    {
        var shared = SharedBlock();
        _error.Clear();
        var count = Encoding.UTF8.GetByteCount(message);
        Encoding.UTF8.GetBytes(message, _error.Room(count));
        _error.Length = count;
        *(int*)(shared + ModAbi.SharedOutPtr) = 0;
        *(int*)(shared + ModAbi.SharedOutLen) = 0;
        *(int*)(shared + ModAbi.SharedErrPtr) = (int)(nint)_error.Pointer;
        *(int*)(shared + ModAbi.SharedErrLen) = count;
        return code;
    }
}
