using Base.Shared;
using Base.Shared.Orders;
using Base.Shared.Queries;
using Kharos.Engine.Layers;

namespace Kharos.Base.Scenario;

/// <summary>Месторождение карты: левый верхний тайл, вид (запись deposits.json) и запас, каким он был (DepositSpot в TS).</summary>
public sealed record DepositSpot(int X, int Y, string Kind, double Reserve);

// Раздел Mining (С2а): месторождения, дропы, склады — чтение слоя и мира напрямую (как sim.deposits и world.query в TS) и
// правки редактора. Общее для тестов раздела и тех, кто сверяется с TS: depositAt, depositIn, reserveLeft, dropItems.
public sealed partial class Sim
{
    /// <summary>Сторона ячейки слоя месторождений (DEPOSIT_CELL) и самого месторождения (DEPOSIT_SIZE).</summary>
    public const int DepositCell = 40, DepositSize = 2;

    private static readonly Lazy<string[]> DepositKinds = new(() =>
        [.. Content().Data("deposits")!.Value.EnumerateObject().Select(property => property.Name)]);

    /// <summary>Виды месторождений по порядку deposits.json (DEPOSIT_TYPES).</summary>
    public static IReadOnlyList<string> DepositTypes => DepositKinds.Value;

    private int DepositLayer => Host.Layers.Id("deposits");

    private DepositSpot SpotOf(LayerItem item) => new((int)item.X, (int)item.Y, DepositTypes[(int)item.Fields[0]], item.Fields[1]);

    /// <summary>Все месторождения карты: по ячейкам, в ячейке — по порядку записи (<c>[...sim.deposits.cells.values()].flat()</c>).</summary>
    public List<DepositSpot> Deposits()
    {
        var layers = Host.Layers;
        var cells = layers.Cells(DepositLayer, LayerOwner.World);
        cells.Sort(static (a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));
        return [.. cells.SelectMany(cell => layers.Items(DepositLayer, LayerOwner.World, cell)).Select(SpotOf)];
    }

    /// <summary>Месторождения ячейки (cellX, cellY) слоя (depositsIn).</summary>
    public List<DepositSpot> DepositsIn(int cellX, int cellY) =>
        [.. Host.Layers.Items(DepositLayer, LayerOwner.World, (cellX, cellY)).Select(SpotOf)];

    /// <summary>Первое месторождение ячейки (depositIn); нет — null.</summary>
    public DepositSpot? DepositIn(int cellX, int cellY) => DepositsIn(cellX, cellY).FirstOrDefault();

    /// <summary>Месторождение с левым верхним тайлом ровно в (x, y) (depositAt); нет — null.</summary>
    public DepositSpot? DepositAt(int x, int y) => DepositsIn(Floor(x), Floor(y)).FirstOrDefault(spot => spot.X == x && spot.Y == y);

    /// <summary>Ближайшее к точке месторождение не дальше reach тайлов от точки до его центра (depositNear); нет — null.</summary>
    public DepositSpot? DepositNear(double x, double y, double reach)
    {
        DepositSpot? best = null;
        var distance = reach;
        for (var cellY = Floor((int)Math.Floor(y - reach - DepositSize)); cellY <= Floor((int)Math.Floor(y + reach)); cellY++)
            for (var cellX = Floor((int)Math.Floor(x - reach - DepositSize)); cellX <= Floor((int)Math.Floor(x + reach)); cellX++)
                foreach (var spot in DepositsIn(cellX, cellY))
                {
                    var d = Math.Sqrt(Math.Pow(spot.X + DepositSize / 2.0 - x, 2) + Math.Pow(spot.Y + DepositSize / 2.0 - y, 2));
                    if (d <= distance)
                        (best, distance) = (spot, d);
                }
        return best;
    }

    private static int Floor(int tile) => (int)Math.Floor(tile / (double)DepositCell);

    /// <summary>Сущность Deposit (сколько добыто) на месторождении; ещё не добывали — 0.</summary>
    public uint DepositEntity(int x, int y) =>
        With(Names.Deposit.Name, Names.Position.Name).FirstOrDefault(entity => At(entity) == (x, y));

    /// <summary>Сколько осталось в месторождении (reserveLeft): запрос мода; месторождения нет — 0.</summary>
    public double ReserveLeft(int x, int y) => Query<double>(MiningQueries.Reserve, new DepositArgs { X = x, Y = y });

    /// <summary>
    /// Забирает из месторождения до amount, как шахта (takeReserve): прямо в мир, сущность Deposit заводится при первой добыче.
    /// Возвращает, сколько забрано.
    /// </summary>
    public double MineDeposit(int x, int y, double amount)
    {
        var taken = Math.Min(amount, ReserveLeft(x, y));
        if (taken <= 0)
            return 0;
        var entity = DepositEntity(x, y);
        if (entity == 0)
        {
            entity = World.Spawn();
            Add(entity, Names.Position.Name);
            Set(entity, "Position.x", x);
            Set(entity, "Position.y", y);
            Add(entity, Names.Deposit.Name);
        }
        Set(entity, "Deposit.mined", Num(entity, "Deposit.mined") + taken);
        return taken;
    }

    /// <summary>Выработать месторождение до конца (takeReserve на весь запас).</summary>
    public void Exhaust(DepositSpot spot) => MineDeposit(spot.X, spot.Y, spot.Reserve);

    /// <summary>Смена месторождений: тики, к которым ждут новых (DepositLayer.due), и сколько мест перепробовано (spawned).</summary>
    public RenewalInfo Renewal() => Query<RenewalInfo>(MiningQueries.Renewal);

    // ===== Дропы и склады =====

    /// <summary>Бросает груз на тайл точки (x, y) (dropItems): правка edit.drop. Возвращает дроп на этом тайле; груза нет — 0.</summary>
    public uint DropItems(double x, double y, params (string Good, double Amount)[] items)
    {
        Edit(new EditDropOrder { X = x, Y = y, Items = items.ToDictionary(item => item.Good, item => item.Amount) });
        return DropAt((int)Math.Floor(x), (int)Math.Floor(y));
    }

    /// <summary>Бросает на землю весь груз сущности (dropCargo): правка edit.drop; сам склад сущности не трогает.</summary>
    public void DropCargo(uint entity) => Edit(new EditDropOrder { Entity = entity });

    /// <summary>Дроп на тайле (x, y) (dropAt); нет — 0.</summary>
    public uint DropAt(int x, int y) => With(Names.Drop.Name, Names.Position.Name).FirstOrDefault(entity => At(entity) == (x, y));

    /// <summary>Все дропы мира.</summary>
    public List<uint> Drops() => With(Names.Drop.Name);

    /// <summary>Сколько груза good (имя из data) лежит на складе сущности; склада нет — 0.</summary>
    public double Stock(uint entity, string good) => Has(entity, Names.Stock.Name) ? Num(entity, Names.Stock.Name, good) : 0;
}
