using Kharos.Engine.Layers;
using Kharos.Engine.World;
using Kharos.Protocol;

namespace Kharos.Host;

/// <summary>
/// Что игрок узнаёт о мире (host.ts: shownBy, sightingOf; wire.ts: seenBy). Сущность видна:
/// <list type="bullet">
/// <item>своя и союзная (<see cref="Teams"/>) — всегда;</item>
/// <item>с <c>Sighting</c>: everyone — всем; allies — хозяину и союзникам; listed — им и игрокам из viewers; points —
/// им и тем, кто видит точку a, b, c или место сущности; seen — как без него;</item>
/// <item>иначе — когда в обзоре её место: у здания (<c>Footprint</c>) — хоть один тайл основания; сущность на носителе
/// (<c>Mount.parent</c>) — как носитель; без места ничья видна всем, чужая — никому.</item>
/// </list>
/// Что из видимой уходит — <see cref="WireRules"/>: своему (хозяин, союзник, ничья сущность) — всё, кроме @net none;
/// чужому — без @net owner и с @private по умолчанию. Слои — мира с send и свои слои игрока (союзников — у слоёв с
/// allies); предметные слои мира с remember или sight (месторождения, следы) и покрытие при тумане идут не слоем мира, а
/// версией игрока (<see cref="Host.LayersForPlayer"/>, <see cref="ItemMemory"/>): что в тумане, не уходит. Сигналы — <see cref="IViewFilter.Hears"/>. Помнит сущности с компонентами <see cref="HostOptions.Remember"/>;
/// при входе получает призраки из <see cref="Host.Buildings"/>. Тумана у партии нет — видно всё.
/// </summary>
public sealed class VisionView : IViewFilter
{
    private readonly Host _host;
    private readonly WireRules _rules;
    private readonly Teams _teams = new();
    private readonly List<uint> _allies = [], _fresh = [];
    private (ulong Tick, ulong Revision) _read = (ulong.MaxValue, ulong.MaxValue);

    public VisionView(Host host, uint player)
    {
        _host = host;
        Player = player;
        _rules = WireRules.Of(host.World.Schema);
    }

    public uint Player { get; }

    /// <summary>Союзник ли игрок — вместо союзов мира (<c>Team</c>); null — по <c>Team</c>.</summary>
    public Func<uint, bool>? Allied { get; init; }

    /// <summary>Растёт, когда у игрока сменились союзники: что уходит, надо слать заново.</summary>
    public int TeamVersion
    {
        get
        {
            Refresh();
            return _teamVersion;
        }
    }

    private int _teamVersion;

    /// <summary>Свой ли игрок: сам зритель или союзник.</summary>
    public bool Friend(uint owner)
    {
        if (owner == Player)
            return true;
        if (owner == 0)
            return false;
        if (Allied is { } allied)
            return allied(owner);
        Refresh();
        return _teams.Allied(Player, owner);
    }

    public bool Entity(uint entity)
    {
        var world = _host.World;
        if (!world.Alive(entity))
            return false;
        var owner = OwnerOf(world, entity);
        var sighting = world.Store(Builtin.Sighting);
        var row = sighting.RowOf(entity);
        if (row >= 0)
        {
            switch ((int)sighting.Column(0)[row])
            {
                case Sightings.Everyone:
                    return true;
                case Sightings.Allies:
                    return Friend(owner);
                case Sightings.Listed:
                    return Friend(owner) || Listed(sighting.BytesAt(0, row));
                case Sightings.Points:
                    if (Friend(owner))
                        return true;
                    for (var point = 1; point + 1 <= 6; point += 2)
                        if (Sees(sighting.Column(point)[row], sighting.Column(point + 1)[row]))
                            return true;
                    return world.TryNumber(entity, Builtin.Position, 0, out var px) && world.TryNumber(entity, Builtin.Position, 1, out var py) && Sees(px, py);
            }
        }
        if (Friend(owner))
            return true;
        // На носителе — там же и тем же, что носитель.
        var target = world.TryNumber(entity, Builtin.Mount, 0, out var parent) && parent >= 1 && parent <= uint.MaxValue && world.Alive((uint)parent) ? (uint)parent : entity;
        if (target != entity && Friend(OwnerOf(world, target)))
            return true;
        if (!world.TryNumber(target, Builtin.Position, 0, out var x) || !world.TryNumber(target, Builtin.Position, 1, out var y))
            return owner == 0 && OwnerOf(world, target) == 0;
        if (world.TryNumber(target, Builtin.Footprint, 0, out var w) && world.TryNumber(target, Builtin.Footprint, 1, out var h))
            return SeesArea(x, y, w, h);
        return Sees(x, y);
    }

    /// <summary>
    /// Уходит ли зрителю блок слоя: слои мира с send — кроме тех, что идут ему его версией (<see cref="Host.Fogged"/>:
    /// покрытие при тумане, предметы по обзору — <see cref="ItemMemory"/>); свои слои игрока и союзные с allies.
    /// </summary>
    public bool Layer(LayerSpec spec, LayerOwner owner) =>
        spec.Send && (owner.IsWorld ? !_host.Fogged(spec.Name) : owner.PlayerId == Player || (spec.Allies && Friend(owner.PlayerId)));

    public bool Remembers(uint entity) => _host.Remembered(entity);

    public bool Sees(double x, double y) => _host.Vision is not { } vision || vision.Sees(Player, x, y);

    /// <summary>Видит ли зритель хоть один тайл прямоугольника (основание здания).</summary>
    public bool SeesArea(double x, double y, double w, double h) => _host.Vision is not { } vision || vision.SeesArea(Player, x, y, w, h);

    public bool Shows(GameWorld world, uint entity, int component) =>
        _rules.Shows(component, Mine(world, entity), new WireRules.InWorld(world, entity));

    public ulong Hidden(GameWorld world, uint entity, int component) => _rules.Hidden(component, Mine(world, entity));

    /// <summary>Пересчитывать ли сущность целиком, когда у неё сменился этот компонент (хозяин, условия исключений).</summary>
    internal bool Recreates(int component) => _rules.Recreates(component);

    /// <summary>Чужие здания, которые зритель помнит (<see cref="BuildMemory"/>), — призраками при входе.</summary>
    internal IReadOnlyDictionary<uint, Ghost> Ghosts => _host.Buildings.Known(Player);

    /// <summary>Призрак, каким он уходит зрителю: чужое — без того, что знает только хозяин.</summary>
    internal List<RecordPart> Parts(Ghost ghost)
    {
        var friend = ghost.Owner == 0 || Friend(ghost.Owner);
        var has = new InGhost(ghost);
        var parts = new List<RecordPart>(ghost.Parts.Length);
        foreach (var part in ghost.Parts)
        {
            var id = part.Def.Id;
            if (!_rules.Shows(id, friend, has))
                continue;
            var hidden = _rules.Hidden(id, friend);
            if (hidden == 0)
            {
                parts.Add(part);
                continue;
            }
            var columns = (double[])part.Columns.Clone();
            var bytes = (byte[][])part.Bytes.Clone();
            foreach (var field in part.Def.Fields)
            {
                if ((hidden & (1UL << field.Index)) == 0)
                    continue;
                if (field.Kind == FieldKind.Bytes)
                    bytes[field.Slot] = [];
                else
                    columns[field.Slot] = field.Kind == FieldKind.String ? _host.World.Strings.Find(field.Default.Text ?? "") ?? 0 : field.Default.Number;
            }
            parts.Add(part with { Columns = columns, Bytes = bytes });
        }
        return parts;
    }

    /// <summary>Своё ли зрителю: его, союзника или ничьё (ничьё уходит целиком, как в seenBy).</summary>
    private bool Mine(GameWorld world, uint entity) => OwnerOf(world, entity) is var owner && (owner == 0 || Friend(owner));

    private bool Listed(ReadOnlySpan<byte> viewers)
    {
        for (var at = 0; at + 4 <= viewers.Length; at += 4)
            if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(viewers[at..]) == Player)
                return true;
        return false;
    }

    private static uint OwnerOf(GameWorld world, uint entity) =>
        world.TryNumber(entity, Builtin.Owner, 0, out var number) && number >= 1 && number <= uint.MaxValue ? (uint)number : 0;

    /// <summary>Союзы мира — заново, когда сменились тик или состав мира.</summary>
    private void Refresh()
    {
        var world = _host.World;
        var key = (_host.Tick, world.Revision);
        if (key == _read)
            return;
        _read = key;
        _teams.Read(world);
        _teams.Allies(Player, _fresh);
        if (!_fresh.SequenceEqual(_allies))
        {
            _allies.Clear();
            _allies.AddRange(_fresh);
            _teamVersion++;
        }
    }

    private readonly struct InGhost(Ghost ghost) : WireRules.IHas
    {
        public bool Has(int component) => ghost.Has(component);
    }
}
