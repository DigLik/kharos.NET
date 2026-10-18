using Base.Host.Kernel;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Mining;

/// <summary>Месторождение (DepositSpot): левый верхний тайл, вид (номер записи в deposits.json) и запас, каким он был.</summary>
internal readonly record struct Spot(int X, int Y, int Kind, double Reserve)
{
    /// <summary>Имя вида: ключ записи deposits.json, он же ресурс.</summary>
    public string Name => Deposits.KindName(Kind);
}

/// <summary>
/// Месторождения (deposits.ts). Месторождение — предмет слоя мира <c>deposits</c> левым верхним тайлом: поля kind и reserve.
/// Сколько из него добыто, помнит сущность Deposit на том же тайле: она появляется, когда на месторождении начали
/// добычу, нетронутые месторождения в мире сущностей не хранятся. Все месторождения карты кладёт генератор при начале
/// партии (<see cref="Generator"/>), дальше слой — данные мира: правки земли его не двигают, правки редактора и смена
/// выработанных (<see cref="Renewal"/>) меняют сам слой.
/// </summary>
internal static class Deposits
{
    /// <summary>Сторона ячейки слоя в тайлах: в каждой ячейке генератор кладёт не больше одного месторождения (DEPOSIT_CELL).</summary>
    public const int Cell = 40;

    /// <summary>Сторона месторождения в тайлах: шахта встаёт ровно на него (DEPOSIT_SIZE).</summary>
    public const int Size = 2;

    private static string[] _kinds = [];

    /// <summary>Виды месторождений по порядку записей deposits.json (DEPOSIT_TYPES): номер вида — место в этом списке.</summary>
    public static IReadOnlyList<string> Kinds => _kinds;

    /// <summary>Берёт виды из таблиц (после <c>Core.Load</c>).</summary>
    public static void Load() => _kinds = [.. Core.Tables.Deposits.Keys];

    /// <summary>Имя вида по номеру; нет такого — первый вид, как <c>DEPOSIT_TYPES[kind] ?? 'metal'</c>.</summary>
    public static string KindName(int kind) => kind >= 0 && kind < _kinds.Length ? _kinds[kind] : _kinds.Length > 0 ? _kinds[0] : "";

    /// <summary>Номер вида; нет — -1.</summary>
    public static int KindIndex(string kind) => Array.IndexOf(_kinds, kind);

    // ===== Слой =====

    private static Spot FromItem(MapItem item) => new((int)item.X, (int)item.Y, (int)item.Fields[0], item.Fields[1]);

    /// <summary>Месторождение с левым верхним тайлом ровно в (x, y); нет — null (depositAt).</summary>
    public static Spot? At(int x, int y)
    {
        if (S.Deposits is not { } layer)
            return null;
        foreach (var item in Tools.Items(layer, 0, x, y, 0.5))
            if (item.X == x && item.Y == y)
                return FromItem(item);
        return null;
    }

    /// <summary>Месторождения ячейки (cellX, cellY) слоя, левым верхним тайлом в ней (depositsIn).</summary>
    public static List<Spot> In(int cellX, int cellY)
    {
        var list = new List<Spot>();
        if (S.Deposits is not { } layer)
            return list;
        // Круг накрывает всю ячейку; чужие соседей отбрасываются.
        var (left, top) = (cellX * Cell, cellY * Cell);
        foreach (var item in Tools.Items(layer, 0, left + Cell / 2.0, top + Cell / 2.0, Cell))
            if (item.X >= left && item.X < left + Cell && item.Y >= top && item.Y < top + Cell)
                list.Add(FromItem(item));
        return list;
    }

    /// <summary>Ближайшее к точке месторождение не дальше reach тайлов (от точки до его центра); нет — null (depositNear).</summary>
    public static Spot? Near(double x, double y, double reach)
    {
        if (S.Deposits is not { } layer)
            return null;
        Spot? best = null;
        var distance = reach;
        // Предмет лежит левым верхним тайлом, а считается до центра: берём шире на размер месторождения.
        foreach (var item in Tools.Items(layer, 0, x, y, reach + Size))
        {
            var d = Math.Sqrt((item.X + Size / 2.0 - x) * (item.X + Size / 2.0 - x) + (item.Y + Size / 2.0 - y) * (item.Y + Size / 2.0 - y));
            if (d <= distance)
            {
                best = FromItem(item);
                distance = d;
            }
        }
        return best;
    }

    /// <summary>Кладёт месторождение в слой (addDeposit).</summary>
    public static void Add(Call call, Spot spot)
    {
        if (S.Deposits is { } layer)
            call.Commands.PushItem(layer, 0, spot.X, spot.Y, [spot.Kind, spot.Reserve]);
    }

    /// <summary>Убирает из слоя месторождение с левым верхним тайлом (x, y) и забывает, сколько из него добыли (dropDeposit).</summary>
    public static void Drop(Call call, int x, int y)
    {
        if (S.Deposits is not { } layer)
            return;
        call.Commands.DeleteItems(layer, 0, x, y);
        if (Entity(call, x, y) is var mined and not 0)
            call.Destroy(mined);
    }

    // ===== Запас =====

    /// <summary>Сущность, которая помнит, сколько из месторождения добыто (depositEntity); нет — 0.</summary>
    public static uint Entity(Call call, int x, int y)
    {
        foreach (var entity in Tools.Near(x, y, 0.5, S.Deposit))
            if (call.Alive(entity) && call.At(entity) == (x, y))
                return entity;
        if (call.Writes)
            foreach (var born in call.Stage.Spawned)
                if (call.Alive(born) && call.Has(born, S.Deposit) && call.At(born) == (x, y))
                    return born;
        return 0;
    }

    /// <summary>Сколько осталось в месторождении с левым верхним тайлом (x, y); где его нет — ноль (reserveLeft).</summary>
    public static double ReserveLeft(Call call, int x, int y) => At(x, y) is { } spot ? Left(call, spot, Entity(call, x, y)) : 0;

    /// <summary>Сколько осталось в месторождении, если сущность добытого — mined (0 — не начинали).</summary>
    public static double Left(Call call, Spot spot, uint mined) => Math.Max(0, spot.Reserve - (mined == 0 ? 0 : call.Num(mined, S.Deposit, S.DepositMined)));

    /// <summary>
    /// Забирает из месторождения до amount; mined — сущность добытого (0 — заведёт). Возвращает, сколько забрано
    /// (takeReserve).
    /// </summary>
    public static double Take(Call call, Spot spot, uint mined, double amount)
    {
        var taken = Math.Min(amount, Left(call, spot, mined));
        if (taken <= 0)
            return 0;
        if (mined == 0)
        {
            mined = call.Spawn();
            call.Add(mined, S.Position).Number(S.PositionX, spot.X).Number(S.PositionY, spot.Y);
            call.Add(mined, S.Deposit).Number(S.DepositMined, taken);
        }
        else
            call.Set(mined, S.Deposit).Number(S.DepositMined, call.Num(mined, S.Deposit, S.DepositMined) + taken);
        return taken;
    }

    /// <summary>Забирает из месторождения с левым верхним тайлом (x, y) до amount; нет месторождения — ноль.</summary>
    public static double Take(Call call, int x, int y, double amount) => At(x, y) is { } spot ? Take(call, spot, Entity(call, x, y), amount) : 0;

    // ===== Шахта =====

    /// <summary>Какая руда лежит под шахтой: вид месторождения под её левым верхним тайлом, переведённый в руду (mineOre); нет — null.</summary>
    public static string? MineOre(Call call, uint mine)
    {
        var (x, y) = call.At(mine);
        return double.IsNaN(x) || At((int)x, (int)y) is not { } spot ? null : Goods.OreOf(spot.Name);
    }

    /// <summary>Ставит ли вид здания на месторождение шахту (BuildingSpec.extract).</summary>
    public static bool IsExtractor(string? type) => Factory.Building(type)?.Extract == true;

    /// <summary>Номера Proto видов зданий-шахт.</summary>
    public static HashSet<int> ExtractorProtos() =>
        [.. Core.Tables.Buildings.Where(pair => pair.Value.Extract).Select(pair => Core.BuildingProto(pair.Key)).Where(id => id >= 0)];

    /// <summary>Стоит ли на месторождении шахта — готовая или строящаяся, чья угодно (hasMine).</summary>
    public static bool HasMine(Call call, Spot spot)
    {
        for (var y = spot.Y; y < spot.Y + Size; y++)
            for (var x = spot.X; x < spot.X + Size; x++)
            {
                var building = Tools.Occupant(x, y);
                if (building != 0 && call.Alive(building) && IsExtractor(call.BuildingType(building)))
                    return true;
                if (Placement.SiteAt(call, x, y, 1, 1) is var site and not 0 && IsExtractor(call.BuildingType(site)))
                    return true;
            }
        return false;
    }

    /// <summary>Во сколько раз шахта уровня level качает быстрее первого (levelFactor): каждый уровень — на половину.</summary>
    public static double LevelFactor(double level) => 1 + 0.5 * (level - 1);

    /// <summary>Сколько руды в секунду даёт шахта (rate вида месторождения × уровень).</summary>
    public static double Rate(string kind, double level) => (Core.Tables.Deposits.TryGetValue(kind, out var spec) ? spec.Rate : 0) * LevelFactor(level);
}
