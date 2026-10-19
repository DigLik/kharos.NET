namespace Base.Client;

/// <summary>
/// Брошенный на землю груз (dropsPass.ts): кучка ящиков цвета того груза, которого в ней больше всего; чем больше груза,
/// тем больше ящиков (до четырёх, ящик — на 15 единиц). Над покрытием и под зданиями.
/// </summary>
internal sealed class Drops(Fx fx)
{
    private const int Crates = 4;
    private const double PerCrate = 15;

    public void Draw()
    {
        var c = fx.C;
        foreach (var entity in c.Each(fx.Drop, fx.Position))
        {
            if (c.Pose(entity) is not { } at || !fx.Visible(at.X, at.Y, 1) || c.Get(entity, fx.Stock) is not { } stock)
                continue;
            double load = 0, best = 0;
            var main = 0;
            for (var good = 0; good < Icons.Goods.Length; good++)
            {
                var amount = stock.Number(fx.Stock[Icons.Goods[good]]);
                load += amount;
                if (amount > best)
                    (best, main) = (amount, good);
            }
            if (load <= 0)
                continue;
            var count = Math.Min(Crates, Math.Max(1, (int)Math.Ceiling(load / PerCrate)));
            fx.Sprite(fx.Look("drops/" + Icons.Goods[main]), count - 1, at.X, at.Y, 1, 1, 1, Fx.Floor);
        }
    }
}
