using System.ComponentModel;

namespace Kharos.Mod.Sdk.Runtime;

/// <summary>
/// Мост режима CLR (docs/dotnet-n5.md, Н5-5): хост грузит сборку мода и этот SDK в свою collectible
/// AssemblyLoadContext и зовёт то же, что экспорты wasm, — отражением, через делегаты с типами BCL. Импорты хоста
/// (<see cref="Host"/>) идут к делегатам <see cref="Attach"/> вместо модуля wasm <c>kh</c>; адреса — nint, а не i32.
/// Статика — своя у каждой загрузки (своя ALC — свой SDK): моды разных хостов не видят друг друга.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static unsafe class ClrBridge
{
    internal static Func<double>? Random;
    internal static Action<int, string>? Log;
    internal static Func<int, nint, int, nint, int, int>? Tool;

    /// <summary>Импорты хоста: random, log, tool(id, inPtr, inLen, outPtr, outCap).</summary>
    public static void Attach(Func<double> random, Action<int, string> log, Func<int, nint, int, nint, int, int> tool)
    {
        Random = random;
        Log = log;
        Tool = tool;
    }

    /// <summary>kh_load: create — класс мода (ModExports.Create сборки мода).</summary>
    public static int Load(Func<object> create, ReadOnlySpan<byte> info)
    {
        Mod mod;
        try
        {
            mod = (Mod)create();
        }
        catch (Exception ex)
        {
            return ModRuntime.Fail(global::Kharos.Mod.Abi.ModAbi.ErrorException, $"kh_load: {ex.GetType().Name}: {ex.Message}");
        }
        return ModRuntime.LoadCore(mod, info);
    }

    /// <summary>kh_prepare и kh_prepare_table: часть кадра (0 — главная, t + 1 — таблица t) на n сущностей; её адреса.</summary>
    public static int Prepare(int system, int part, int n, out nint data, out nint entities, out int capacity)
    {
        var code = ModRuntime.PrepareCore(system, part, n);
        (data, entities, capacity) = code < 0 ? (0, 0, 0) : ModRuntime.PartOf(system, part);
        return code;
    }

    /// <summary>kh_system: counts — сущностей в главной части и в таблицах.</summary>
    public static int RunSystem(int system, ReadOnlySpan<int> counts, ulong tick, double dt, uint next) =>
        ModRuntime.SystemCore(system, counts, tick, dt, next);

    /// <summary>kh_event.</summary>
    public static int RunEvent(int handler, ReadOnlySpan<byte> payload, ulong tick, double dt, uint next) =>
        ModRuntime.EventCore(handler, payload, tick, dt, next);

    /// <summary>kh_command.</summary>
    public static int RunCommand(int order, uint player, uint rights, ReadOnlySpan<byte> payload, ulong tick, double dt, uint next) =>
        ModRuntime.CommandCore(order, player, rights, payload, tick, dt, next);

    /// <summary>kh_query.</summary>
    public static int RunQuery(int query, uint rights, ReadOnlySpan<byte> payload, ulong tick, double dt) =>
        ModRuntime.QueryCore(query, rights, payload, tick, dt);

    /// <summary>Выход последнего успешного вызова: манифест, буфер команд, ответ запроса (до следующего вызова).</summary>
    public static ReadOnlySpan<byte> Output() => ModRuntime.Output;

    /// <summary>Текст последней ошибки.</summary>
    public static string Error() => ModRuntime.Error;

    // ===== Клиентский мод (ClientAbi, docs/dotnet-n6.md, Н6-6): те же kh_*, что ClientExports, без адресов wasm =====

    /// <summary>kh_abi_version клиентского мода: версия клиентского ABI этого SDK.</summary>
    public static int ClientAbiVersion() => ClientRuntime.AbiVersion();

    /// <summary>kh_load клиентского мода: create — класс мода (ClientExports.Create сборки мода).</summary>
    public static int ClientLoad(Func<object> create, ReadOnlySpan<byte> info)
    {
        global::Kharos.Mod.Sdk.Client.ClientMod mod;
        try
        {
            mod = (global::Kharos.Mod.Sdk.Client.ClientMod)create();
        }
        catch (Exception ex)
        {
            return ModRuntime.Fail(global::Kharos.Mod.Abi.ModAbi.ErrorException, $"kh_load: {ex.GetType().Name}: {ex.Message}");
        }
        return ClientRuntime.LoadCore(mod, info);
    }

    /// <summary>kh_look.</summary>
    public static int ClientLook(int look) => ClientRuntime.Look(look);

    /// <summary>kh_bank.</summary>
    public static int ClientBank(int entry, int variant, int rate) => ClientRuntime.Bank(entry, variant, rate);

    /// <summary>kh_frame: вход кадра → выход (<see cref="Output"/>).</summary>
    public static int ClientFrame(ReadOnlySpan<byte> input) => ClientRuntime.FrameCore(input);

    /// <summary>Конец экземпляра: память вне GC освобождена, импорты отвязаны.</summary>
    public static void Detach()
    {
        ClientRuntime.ReleaseAll();
        ModRuntime.ReleaseAll();
        Random = null;
        Log = null;
        Tool = null;
    }
}
