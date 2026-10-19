using System.Buffers.Binary;
using Base.Host.Kernel;
using Base.Host.Sections.Mining;
using Base.Shared.Data;

namespace Base.Host.Sections.Logistics;

/// <summary>
/// Грузы для логистики: номер груза — место в <see cref="Goods.All"/> (порядок столбцов Stock), в полях Hauler, Carry,
/// Fetch, Trade и Batch груз лежит номером Proto (resources/…, products/…, ores/…; −1 — нет). Фильтр грузовика — маска
/// по номерам грузов. Списки сущностей (маршрут, назначение) — u32 подряд.
/// </summary>
internal static class Cargo
{
    /// <summary>Остатки меньше этого не в счёт (EPSILON).</summary>
    public const double Epsilon = Stocks.Epsilon;

    private static int[] _protos = [];
    private static readonly Dictionary<int, int> ByProto = [];
    private static int _wares;

    /// <summary>Сколько грузов всего.</summary>
    public static int Count => Goods.All.Count;

    /// <summary>Готовое (WARES): ресурсы и изделия — первые <see cref="Wares"/> грузов; дальше — руда.</summary>
    public static int Wares => _wares;

    /// <summary>Готовит таблицу грузов (привязка системы); номера Proto берутся при первом обращении — инструменты при загрузке недоступны.</summary>
    public static void Load()
    {
        var tables = Core.Tables;
        _wares = tables.Resources.Count + tables.Products.Count;
        _protos = [];
        ByProto.Clear();
    }

    private static void Protos()
    {
        if (_protos.Length == Count)
            return;
        var tables = Core.Tables;
        var protos = new int[Count];
        ByProto.Clear();
        for (var good = 0; good < Count; good++)
        {
            var name = Goods.All[good];
            var section = tables.Resources.ContainsKey(name) ? "resources" : tables.Products.ContainsKey(name) ? "products" : "ores";
            protos[good] = Core.Proto(section, name);
            if (protos[good] >= 0)
                ByProto[protos[good]] = good;
        }
        _protos = protos;
    }

    /// <summary>Номер Proto груза; нет груза — −1.</summary>
    public static int Proto(int good)
    {
        Protos();
        return good >= 0 && good < _protos.Length ? _protos[good] : -1;
    }

    /// <summary>Груз по номеру Proto; не груз — −1.</summary>
    public static int Of(double proto)
    {
        Protos();
        return ByProto.GetValueOrDefault((int)proto, -1);
    }

    /// <summary>Номер груза по имени; нет — −1.</summary>
    public static int Index(string? good) => good is null ? -1 : Goods.IndexOf(good);

    public static string Name(int good) => good >= 0 && good < Count ? Goods.All[good] : "";

    /// <summary>Руда ли это (isOre).</summary>
    public static bool IsOre(int good) => good >= _wares;

    /// <summary>Изделие ли это (isProduct).</summary>
    public static bool IsProduct(int good) => good >= Core.Tables.Resources.Count && good < _wares;

    // ===== Фильтр =====

    /// <summary>Пускает ли маска груз: пустая — любой.</summary>
    public static bool Carries(int mask, int good) => mask == 0 || (good >= 0 && (mask & (1 << good)) != 0);

    /// <summary>Маска из имён грузов: неизвестные выбрасываются.</summary>
    public static int Mask(IEnumerable<string> goods)
    {
        var mask = 0;
        foreach (var name in goods)
            if (Index(name) is var good and >= 0 and < 31)
                mask |= 1 << good;
        return mask;
    }

    // ===== Списки сущностей =====

    public static List<uint> Entities(byte[]? bytes)
    {
        var list = new List<uint>();
        if (bytes is null)
            return list;
        for (var at = 0; at + 4 <= bytes.Length; at += 4)
            list.Add(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at)));
        return list;
    }

    public static byte[] Pack(IReadOnlyList<uint> entities)
    {
        var bytes = new byte[entities.Count * 4];
        for (var i = 0; i < entities.Count; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), entities[i]);
        return bytes;
    }

    // ===== Виды =====

    /// <summary>Чем кормят здание (feedOf): патроны турели и станции поставки, стержни реактора; нет — −1.</summary>
    public static int Feed(BuildingSpec spec) => spec.Ammo || spec.Depot ? Index("ammo") : spec.Burns is { } burns ? Index(burns.Good) : -1;

    /// <summary>Что перерабатывает здание (refines); нет — −1.</summary>
    public static int Refines(BuildingSpec spec) => Index(spec.Refines);

    /// <summary>Сырьё рецепта цеха и сколько каждого держат про запас (stockedFor: рецепт × STOCKED_CYCLES).</summary>
    public static IReadOnlyDictionary<string, double> Recipe(BuildingSpec spec) =>
        spec.Assembles is { } product && Core.Tables.Products.TryGetValue(product, out var recipe) ? recipe.Recipe : Empty;

    /// <summary>Сколько циклов рецепта цех держит про запас (STOCKED_CYCLES).</summary>
    public const double StockedCycles = 4;

    private static readonly Dictionary<string, double> Empty = [];
}
