using Base.Host.Kernel;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Mining;

/// <summary>
/// Грузы склада (resources.ts): ресурсы, изделия и руда — по порядку столбцов Stock и Limits (schema/logistics.json:
/// resources.json, products.json, ores.json). Руда превращается в ресурс переработкой (ores.json), месторождение вида
/// «metal» даёт руду «metalOre».
/// </summary>
internal static class Goods
{
    private static string[] _all = [];
    private static FieldRef[] _stock = [];
    private static FieldRef?[] _limit = [];
    private static readonly Dictionary<string, int> Index = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> OreByResource = new(StringComparer.Ordinal);

    /// <summary>Все грузы по порядку столбцов (GOODS).</summary>
    public static IReadOnlyList<string> All => _all;

    /// <summary>Берёт грузы из таблиц и столбцы схемы (после <c>Core.Load</c>).</summary>
    public static void Load()
    {
        _all = [.. Core.Tables.Goods];
        _stock = [.. _all.Select(good => S.Stock[good])];
        _limit = [.. _all.Select(good => S.Limits.Find(good))];
        Index.Clear();
        OreByResource.Clear();
        for (var i = 0; i < _all.Length; i++)
            Index[_all[i]] = i;
        foreach (var (ore, spec) in Core.Tables.Ores)
            OreByResource[spec.Resource] = ore;
    }

    /// <summary>Номер груза в порядке столбцов; нет такого — -1.</summary>
    public static int IndexOf(string good) => Index.GetValueOrDefault(good, -1);

    /// <summary>Столбец груза в Stock.</summary>
    public static FieldRef StockField(int good) => _stock[good];

    /// <summary>Столбец груза в Limits; нет — null.</summary>
    public static FieldRef? LimitField(int good) => _limit[good];

    /// <summary>Руда ли это (isOre).</summary>
    public static bool IsOre(string good) => Core.Tables.Ores.ContainsKey(good);

    /// <summary>Что выходит из руды (resourceOf); не руда — null.</summary>
    public static string? ResourceOf(string ore) => Core.Tables.Ores.TryGetValue(ore, out var spec) ? spec.Resource : null;

    /// <summary>Чем добывают ресурс (ORE_OF): месторождение металла даёт руду металла; нет — null.</summary>
    public static string? OreOf(string resource) => OreByResource.GetValueOrDefault(resource);
}
