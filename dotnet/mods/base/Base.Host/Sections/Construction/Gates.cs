using Base.Host.Kernel;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Construction;

/// <summary>
/// Ворота (gates.ts): плиты, которые выезжают из земли; секции вплотную — одни ворота. Обычно опущены: ходят и ездят все.
/// Чужой наземный юнит (не союзник хозяина) ближе GATE_CLOSE к любой секции — плиты поднимаются и становятся стеной
/// для всех; опускаются, когда чужих не осталось ближе GATE_OPEN. Секция, в проёме которой кто-то стоит, не
/// поднимается. Летающим ворота безразличны. Gate.open дублируется в Footprint.open (= Gate.open и не стройка): по нему
/// ходят пути движка. Готовые ворота получают Gate открытыми (снаряжение здания).
/// </summary>
internal sealed class Gates : ISystem
{
    public const double Close = 6, Open = 7.5;

    /// <summary>Раз во сколько тиков ворота смотрят по сторонам (CHECK_TICKS).</summary>
    public const int CheckTicks = 4;

    private Column _gate, _footprint;

    public void Bind(SystemBinding binding)
    {
        _gate = binding.Column(S.Gate.Name, S.GateOpen.Name);
        _footprint = binding.Column(S.Footprint.Name, S.FootprintOpen.Name);
    }

    /// <summary>Секция: хозяин, центр тайла, ближайший чужой, стоит ли кто в проёме.</summary>
    private sealed class Section(int row, uint player, double x, double y)
    {
        public int Row { get; } = row;
        public uint Player { get; } = player;
        public double X { get; } = x;
        public double Y { get; } = y;
        public double Foe { get; set; } = double.PositiveInfinity;
        public bool Held { get; set; }
    }

    /// <summary>Готовым воротам — Gate открытыми (снаряжение здания, Factory.OnBuilding).</summary>
    public static void Equip(Call call, uint entity, string type, uint player)
    {
        if (Factory.Building(type)?.Gate != true)
            return;
        call.Add(entity, S.Gate).Flag(S.GateOpen, true);
        call.Set(entity, S.Footprint).Flag(S.FootprintOpen, true);
    }

    public void Run(SystemContext ctx)
    {
        var entities = ctx.Entities;
        if (entities.Length == 0)
            return;
        var call = Call.Of(ctx);
        var open = ctx.Column(_gate);
        var passable = ctx.Column(_footprint);
        var list = new List<Section>(entities.Length);
        for (var row = 0; row < entities.Length; row++)
        {
            var (x, y) = call.At(entities[row]);
            list.Add(new Section(row, call.OwnerOf(entities[row]), Math.Floor(x) + 0.5, Math.Floor(y) + 0.5));
        }
        var heads = new Dictionary<uint, double>();
        double Head(uint player)
        {
            if (!heads.TryGetValue(player, out var head))
                heads[player] = head = Accounts.Find(call, player) is var account and not 0 ? call.Num(account, S.Team, S.TeamHead, -1) : -1;
            return head;
        }
        // Чужой — не хозяин, не ничей и не союзник хозяина (одинаковый Team.head ≥ 0).
        bool Foe(uint owner, uint player) => owner != player && owner != 0 && !(Head(owner) >= 0 && Head(owner) == Head(player));
        foreach (var gate in list)
            foreach (var unit in Tools.Near(gate.X, gate.Y, Open, S.Unit))
            {
                if (Factory.Unit(call.UnitType(unit))?.Kind is null or "air")
                    continue;
                var (ux, uy) = call.At(unit);
                // В проёме — тот, чей центр в тайле ворот.
                if (Math.Abs(ux - gate.X) < 0.5 && Math.Abs(uy - gate.Y) < 0.5)
                    gate.Held = true;
                var distance = Math.Sqrt((ux - gate.X) * (ux - gate.X) + (uy - gate.Y) * (uy - gate.Y));
                if (distance < gate.Foe && Foe(call.OwnerOf(unit), gate.Player))
                    gate.Foe = distance;
            }
        // Секции вплотную — одни ворота: чужой у одной поднимает все.
        var byTile = new Dictionary<(int, int), Section>();
        foreach (var gate in list)
            byTile[((int)Math.Floor(gate.X), (int)Math.Floor(gate.Y))] = gate;
        var grouped = new HashSet<Section>();
        foreach (var first in list)
        {
            if (!grouped.Add(first))
                continue;
            var group = new List<Section> { first };
            for (var at = 0; at < group.Count; at++)
            {
                var (x, y, player) = ((int)Math.Floor(group[at].X), (int)Math.Floor(group[at].Y), group[at].Player);
                foreach (var (dx, dy) in ((int, int)[])[(1, 0), (-1, 0), (0, 1), (0, -1)])
                    if (byTile.TryGetValue((x + dx, y + dy), out var next) && next.Player == player && grouped.Add(next))
                        group.Add(next);
            }
            var foe = group.Min(gate => gate.Foe);
            foreach (var gate in group)
            {
                if (open[gate.Row] != 0 && foe < Close && !gate.Held)
                    open[gate.Row] = 0;
                else if (open[gate.Row] == 0 && foe >= Open)
                    open[gate.Row] = 1;
                passable[gate.Row] = open[gate.Row];
            }
        }
    }
}
