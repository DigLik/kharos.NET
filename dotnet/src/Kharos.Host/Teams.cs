using Kharos.Engine.World;

namespace Kharos.Host;

/// <summary>
/// Союзы (allies.ts) по компоненту движка <c>Team { player, head }</c> на сущности счёта игрока: союзны разные игроки с
/// одинаковым <c>head ≥ 0</c>; −1 — вне союза. Союзники видят то же, что хозяин: обзор у них общий, скрытое
/// (<c>@net owner</c>, <c>@private</c>) им уходит. Читается из мира целиком — записей столько, сколько игроков.
/// </summary>
public sealed class Teams
{
    private readonly Dictionary<uint, int> _heads = [];

    /// <summary>Союзы в мире сейчас.</summary>
    public static Teams Of(GameWorld world)
    {
        var teams = new Teams();
        teams.Read(world);
        return teams;
    }

    /// <summary>Перечитывает союзы из мира.</summary>
    public void Read(GameWorld world)
    {
        _heads.Clear();
        var store = world.Store(Builtin.Team);
        var players = store.Column(0);
        var heads = store.Column(1);
        for (var row = 0; row < players.Length; row++)
            if (players[row] >= 1 && players[row] <= uint.MaxValue)
                _heads[(uint)players[row]] = heads[row] >= 0 && heads[row] <= int.MaxValue ? (int)heads[row] : -1;
    }

    /// <summary>Глава союза игрока; −1 — ни с кем.</summary>
    public int Head(uint player) => _heads.TryGetValue(player, out var head) ? head : -1;

    /// <summary>Союзны ли два разных игрока.</summary>
    public bool Allied(uint a, uint b) => a != b && a != 0 && b != 0 && Head(a) is >= 0 and var head && Head(b) == head;

    /// <summary>Группа обзора: у союзников одна (−1 − глава), у одиночки — он сам.</summary>
    public long Group(uint player) => Head(player) is >= 0 and var head ? -1L - head : player;

    /// <summary>Союзники игрока по возрастанию.</summary>
    public void Allies(uint player, List<uint> into)
    {
        into.Clear();
        if (Head(player) < 0)
            return;
        foreach (var other in _heads.Keys)
            if (Allied(player, other))
                into.Add(other);
        into.Sort();
    }
}
