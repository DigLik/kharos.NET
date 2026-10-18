using System.Buffers.Binary;
using Kharos.Mod.Sdk;

namespace Base.Host;

/// <summary>
/// games/skirmish: схватка — каждому вошедшему завод и три багги на своём месте карты (стартовые кредиты кладёт
/// mechanics/players). Правила берутся только у идущей партии. Данные — data/games.json, запись skirmish.
/// </summary>
internal sealed class Skirmish(BaseMod mod)
{
    public const string Name = "skirmish";

    /// <summary>Место игрока: первый — в центре, остальные — по кругу радиуса <c>radius</c>.</summary>
    internal static (double X, double Y) Home(uint player, double radius)
    {
        if (player <= 1)
            return (0, 0);
        var angle = (player - 2) * Math.PI * 2 / 6;
        return (Math.Cos(angle) * radius, Math.Sin(angle) * radius);
    }

    /// <summary>player_joined { player }: завод и стартовые юниты у дома игрока.</summary>
    public void OnJoined(EventContext ctx)
    {
        if (mod.Game != Name || !mod.D.Games.TryGetValue(Name, out var rules))
            return;
        var player = BinaryPrimitives.ReadUInt32LittleEndian(ctx.Payload);
        var p = mod.P;
        var (x, y) = Home(player, rules.Radius);
        if (mod.D.Buildings.TryGetValue("factory", out var factory))
            Entities.Factory(p, ctx.Commands, factory, x, y, player);
        var count = 0;
        foreach (var (name, amount) in rules.Start)
            for (var k = 0; k < amount; k++)
            {
                count++;
                if (!mod.D.Units.TryGetValue(name, out var kind))
                    continue;
                Entities.Unit(p, ctx.Commands, kind, new UnitSpawn(x - 2 + count * 1.4, y + 4, Math.PI / 2, player));
            }
    }
}
