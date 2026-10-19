using Kharos.Mod.Sdk;
using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Остаток месторождения, известный игроку — перенос game/knownReserve.ts: пока он видит хоть тайл месторождения —
/// настоящий, в тумане — каким был, когда игрок видел его в последний раз; null — с начала партии не видел ни разу. Так
/// туман не выдаёт, копают ли месторождение чужие.
/// <para>
/// Настоящий остаток (Д6): reserve предмета слоя deposits (версия игрока) минус Deposit.mined шахты на том же тайле.
/// Шахта приходит клиенту, только пока её видно, поэтому последний mined помнится здесь же.
/// </para>
/// </summary>
internal static class KnownReserve
{
    /// <summary>Сторона месторождения в тайлах (DEPOSIT_SIZE).</summary>
    public const int Size = 2;

    /// <summary>Известное месторождение: левый верхний тайл, вид (ресурс), известный остаток или null.</summary>
    public readonly record struct Known(int X, int Y, string Kind, double? Left);

    private static readonly Dictionary<(int, int), double> Seen = [];
    private static ClientContext? _owner;
    private static ComponentRef? _deposit;
    private static ComponentRef? _position;

    /// <summary>Память — на партию: новый контекст клиента начинает её заново.</summary>
    private static void Own(ClientContext c)
    {
        if (ReferenceEquals(_owner, c))
            return;
        _owner = c;
        Seen.Clear();
        try
        {
            _deposit = c.Component("Deposit");
            _position = c.Component("Position");
        }
        catch (KeyNotFoundException)
        {
            _deposit = null;
        }
    }

    /// <summary>Месторождение слоя layer (deposits), что накрывает точку мира (x, y), с известным остатком; нет — null.</summary>
    public static Known? Spot(ClientContext c, LayerRef? layer, double x, double y)
    {
        if (layer is null)
            return null;
        var kindField = IndexOf(layer, "kind");
        var reserveField = IndexOf(layer, "reserve");
        if (kindField < 0)
            return null;
        foreach (var item in Tools.Items(layer, 0, x, y, Size * 1.5))
        {
            if (x < item.X || x >= item.X + Size || y < item.Y || y >= item.Y + Size)
                continue;
            var kind = (int)item.Fields[kindField];
            var name = (uint)kind < (uint)DepositArt.Types.Length ? DepositArt.Types[kind] : "metal";
            return new Known((int)item.X, (int)item.Y, name, Left(c, (int)item.X, (int)item.Y, reserveField < 0 ? 0 : item.Fields[reserveField]));
        }
        return null;
    }

    private static int IndexOf(LayerRef layer, string field)
    {
        for (var i = 0; i < layer.Fields.Count; i++)
            if (layer.Fields[i] == field)
                return i;
        return -1;
    }

    /// <summary>Остаток месторождения с левым верхним тайлом (x, y) и остатком слоя reserve, как его знает игрок.</summary>
    public static double? Left(ClientContext c, int x, int y, double reserve)
    {
        Own(c);
        var key = (x, y);
        for (var dy = 0; dy < Size; dy++)
            for (var dx = 0; dx < Size; dx++)
            {
                if (!c.Sees(x + dx + 0.5, y + dy + 0.5))
                    continue;
                var left = Math.Max(0, reserve - MinedAt(c, x, y));
                Seen[key] = left;
                return left;
            }
        return Seen.TryGetValue(key, out var known) ? known : null;
    }

    /// <summary>Сколько добыто шахтой на тайле (x, y) — её видно: зовут, только пока месторождение в обзоре.</summary>
    private static double MinedAt(ClientContext c, int x, int y)
    {
        if (_deposit is { } deposit && _position is { } position)
            foreach (var entity in c.Within(x - 0.5, y - 0.5, x + 0.5, y + 0.5, deposit))
                if (c.Get(entity, position) is { } at && Math.Floor(at.Number(position["x"])) == x && Math.Floor(at.Number(position["y"])) == y
                    && c.Get(entity, deposit) is { } values)
                    return values.Number(deposit["mined"]);
        return 0;
    }
}
