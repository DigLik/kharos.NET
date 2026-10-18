using Kharos.Engine.World;
using Kharos.Protocol;

namespace Kharos.Host;

/// <summary>
/// Здание, каким игрок его запомнил: левый верхний угол, основание, хозяин и все компоненты, кроме <c>@net none</c>.
/// Что чужому знать не положено, снимает <see cref="VisionView"/>, когда шлёт. Запись не меняется, и у игроков,
/// потерявших здание из виду разом, она общая.
/// </summary>
public sealed class Ghost
{
    public required double X { get; init; }
    public required double Y { get; init; }
    public double W { get; init; } = 1;
    public double H { get; init; } = 1;
    public uint Owner { get; init; }
    public required RecordPart[] Parts { get; init; }

    /// <summary>Есть ли в записи компонент.</summary>
    public bool Has(int component) => Array.FindIndex(Parts, part => part.Def.Id == component) >= 0;

    /// <summary>Здание как оно есть сейчас.</summary>
    public static Ghost Of(GameWorld world, uint entity)
    {
        var schema = world.Schema;
        var parts = new List<RecordPart>();
        for (var c = 0; c < schema.Count; c++)
        {
            var store = world.Store(c);
            var row = store.RowOf(entity);
            if (row < 0 || schema[c].Net == NetMode.None)
                continue;
            var def = schema[c];
            var columns = new double[def.Columns];
            for (var slot = 0; slot < columns.Length; slot++)
                columns[slot] = store.Column(slot)[row];
            var bytes = new byte[def.BytesFields][];
            for (var slot = 0; slot < bytes.Length; slot++)
                bytes[slot] = store.BytesAt(slot, row).ToArray();
            parts.Add(new RecordPart(def, columns, bytes));
        }
        world.TryNumber(entity, Builtin.Position, 0, out var x);
        world.TryNumber(entity, Builtin.Position, 1, out var y);
        var (w, h) = world.TryNumber(entity, Builtin.Footprint, 0, out var fw) && world.TryNumber(entity, Builtin.Footprint, 1, out var fh) ? (fw, fh) : (1, 1);
        var owner = world.TryNumber(entity, Builtin.Owner, 0, out var player) && player >= 1 && player <= uint.MaxValue ? (uint)player : 0;
        return new Ghost { X = x, Y = y, W = w, H = h, Owner = owner, Parts = [.. parts] };
    }
}

/// <summary>
/// Какими игроки помнят чужие здания — система движка <c>engine.memory</c> (buildMemory.ts, build_memory.rs). Здание,
/// ушедшее в туман, остаётся для игрока призраком таким, каким он видел его последний раз, пока его место снова не
/// окажется в обзоре. Подключённому это показывает и <see cref="PeerSync"/> (замершие сущности), но он помнит только
/// виденное с входа; здесь то же помнит хост — за всех, в сети они или нет, — и отдаёт при входе (<see cref="VisionView"/>):
/// иначе после переподключения разведанные базы пропадали бы. Память идёт в сохранение (раздел MEMO). «Здание» — сущность
/// с компонентом из списка remember партии (<see cref="HostOptions.Remember"/>).
/// </summary>
public sealed class BuildMemory
{
    private readonly SortedDictionary<uint, SortedDictionary<uint, Ghost>> _known = [];
    private readonly Dictionary<uint, HashSet<uint>> _watched = [];
    private readonly Dictionary<uint, Ghost> _taken = [];
    private readonly List<uint> _forget = [];

    /// <summary>Что помнит игрок: сущность → запись; нет — пусто.</summary>
    public IReadOnlyDictionary<uint, Ghost> Known(uint player) =>
        _known.TryGetValue(player, out var known) ? known : (IReadOnlyDictionary<uint, Ghost>)EmptyKnown;

    /// <summary>Помнит ли игрок здание.</summary>
    public bool Remembers(uint player, uint entity) => _known.TryGetValue(player, out var known) && known.ContainsKey(entity);

    /// <summary>Игроки, которые что-то помнят, по возрастанию.</summary>
    public IEnumerable<uint> Players => _known.Where(pair => pair.Value.Count > 0).Select(pair => pair.Key);

    /// <summary>Игрок начинает заново: что он помнил, больше ничего не значит.</summary>
    public void Forget(uint player)
    {
        _known.Remove(player);
        _watched.Remove(player);
    }

    /// <summary>Запись из сохранения.</summary>
    internal void Know(uint player, uint entity, Ghost ghost)
    {
        if (!_known.TryGetValue(player, out var known))
            _known[player] = known = [];
        known[entity] = ghost;
    }

    /// <summary>
    /// Сверка, после обзора: ушедшее из обзора игрока запоминается; что снова в обзоре — забывается; чего больше нет, а
    /// место в обзоре — тоже: игрок видит, что здание снесли. Снесённое на глазах не запоминается. Своё не помнят.
    /// </summary>
    public void Update(GameWorld world, Vision vision, IReadOnlyList<int> remember)
    {
        if (remember.Count == 0)
            return;
        _taken.Clear();
        var positions = world.Store(Builtin.Position);
        foreach (var player in vision.Players)
        {
            var seen = new HashSet<uint>();
            foreach (var component in remember)
                foreach (var entity in world.Store(component).Entities)
                {
                    if (seen.Contains(entity) || Owner(world, entity) == player || positions.RowOf(entity) is var row && row < 0)
                        continue;
                    var (w, h) = Footprint(world, entity);
                    if (vision.SeesArea(player, positions.Column(0)[row], positions.Column(1)[row], w, h))
                        seen.Add(entity);
                }
            _known.TryGetValue(player, out var known);
            if (_watched.TryGetValue(player, out var watched))
                foreach (var entity in watched)
                {
                    if (seen.Contains(entity) || !Remembered(world, entity, remember))
                        continue;
                    if (known is null)
                        _known[player] = known = [];
                    if (!_taken.TryGetValue(entity, out var ghost))
                        _taken[entity] = ghost = Ghost.Of(world, entity);
                    known[entity] = ghost;
                }
            if (known is not null)
            {
                // Здания не двигаются: живое в обзоре — в seen, и место проверяется только у тех, которых больше нет.
                _forget.Clear();
                foreach (var (entity, ghost) in known)
                    if (seen.Contains(entity) || (!Remembered(world, entity, remember) && vision.SeesArea(player, ghost.X, ghost.Y, ghost.W, ghost.H)))
                        _forget.Add(entity);
                foreach (var entity in _forget)
                    known.Remove(entity);
            }
            _watched[player] = seen;
        }
    }

    private static readonly SortedDictionary<uint, Ghost> EmptyKnown = [];

    private static uint Owner(GameWorld world, uint entity) =>
        world.TryNumber(entity, Builtin.Owner, 0, out var player) && player >= 1 && player <= uint.MaxValue ? (uint)player : 0;

    private static (double W, double H) Footprint(GameWorld world, uint entity) =>
        world.TryNumber(entity, Builtin.Footprint, 0, out var w) && world.TryNumber(entity, Builtin.Footprint, 1, out var h) ? (w, h) : (1, 1);

    private static bool Remembered(GameWorld world, uint entity, IReadOnlyList<int> remember)
    {
        if (!world.Alive(entity))
            return false;
        foreach (var component in remember)
            if (world.Has(entity, component))
                return true;
        return false;
    }

    // ------------------------------------------------------------------ сохранение

    /// <summary>
    /// Раздел MEMO сохранения: компоненты и поля по именам (как WorldSave), записи без повторов, кто что помнит.
    /// <code>
    /// components:varint (name:str fields:u8 (name:str kind:u8)×)×
    /// shapes:varint (x:f64 y:f64 w:f64 h:f64 owner:varint parts:varint (component:varint — номер в списке выше,
    ///   значения по полям: Number f64, Flag u8, String str, Bytes blob)×)×
    /// players:varint (player:varint count:varint (entity:varint shape:varint)×)×
    /// </code>
    /// </summary>
    public byte[] Save(GameWorld world)
    {
        var shapes = new List<Ghost>();
        var places = new Dictionary<Ghost, int>(ReferenceEqualityComparer.Instance);
        var components = new List<ComponentDef>();
        var componentAt = new Dictionary<int, int>();
        foreach (var known in _known.Values)
            foreach (var ghost in known.Values)
                if (places.TryAdd(ghost, shapes.Count))
                {
                    shapes.Add(ghost);
                    foreach (var part in ghost.Parts)
                        if (componentAt.TryAdd(part.Def.Id, components.Count))
                            components.Add(part.Def);
                }
        var w = new WireWriter(1024);
        w.Varint((ulong)components.Count);
        foreach (var def in components)
        {
            w.Text(def.Name).Byte((byte)def.Fields.Count);
            foreach (var field in def.Fields)
                w.Text(field.Name).Byte((byte)field.Kind);
        }
        w.Varint((ulong)shapes.Count);
        foreach (var ghost in shapes)
        {
            w.F64(ghost.X).F64(ghost.Y).F64(ghost.W).F64(ghost.H).Varint(ghost.Owner).Varint((ulong)ghost.Parts.Length);
            foreach (var part in ghost.Parts)
            {
                w.Varint((ulong)componentAt[part.Def.Id]);
                foreach (var field in part.Def.Fields)
                    switch (field.Kind)
                    {
                        case FieldKind.Number:
                            w.F64(part.Columns[field.Slot]);
                            break;
                        case FieldKind.Flag:
                            w.Bool(part.Columns[field.Slot] != 0);
                            break;
                        case FieldKind.String:
                            w.Text(world.Strings.Text((uint)part.Columns[field.Slot]));
                            break;
                        default:
                            w.Blob(part.Bytes[field.Slot]);
                            break;
                    }
            }
        }
        var players = _known.Where(pair => pair.Value.Count > 0).ToList();
        w.Varint((ulong)players.Count);
        foreach (var (player, known) in players)
        {
            w.Varint(player).Varint((ulong)known.Count);
            foreach (var (entity, ghost) in known)
                w.Varint(entity).Varint((ulong)places[ghost]);
        }
        return w.ToArray();
    }

    /// <summary>
    /// Память из раздела MEMO: компоненты и поля — по именам в схеме мира (нет — пропускаются, новые поля — по
    /// умолчанию, строки — в строки мира). Мусор — <see cref="FormatException"/>.
    /// </summary>
    public static BuildMemory Load(GameWorld world, ReadOnlySpan<byte> bytes)
    {
        var schema = world.Schema;
        var r = new WireReader(bytes);
        var saved = new (ComponentDef? Def, (string Name, FieldKind Kind, FieldDef? Field)[] Fields)[r.Count()];
        for (var c = 0; c < saved.Length; c++)
        {
            var name = r.Text();
            var def = schema.Find(name) is { } id ? schema[id] : null;
            var fields = new (string, FieldKind, FieldDef?)[r.Byte()];
            for (var f = 0; f < fields.Length; f++)
            {
                var field = r.Text();
                var kind = r.Byte() is var k && k <= (byte)FieldKind.Bytes ? (FieldKind)k : throw new FormatException($"память: вид поля {k}");
                fields[f] = (field, kind, def?.Field(field) is { } current && current.Kind == kind ? current : null);
            }
            saved[c] = (def, fields);
        }
        var shapes = new Ghost[r.Count()];
        for (var s = 0; s < shapes.Length; s++)
        {
            var (x, y, w, h) = (r.F64(), r.F64(), r.F64(), r.F64());
            var owner = r.U32();
            var parts = new List<RecordPart>();
            for (var count = r.Count(); count > 0; count--)
            {
                var at = r.Int();
                if ((uint)at >= (uint)saved.Length)
                    throw new FormatException($"память: компонент №{at}");
                var (def, fields) = saved[at];
                double[]? columns = null;
                byte[][]? blobs = null;
                if (def is not null)
                {
                    columns = new double[def.Columns];
                    blobs = new byte[def.BytesFields][];
                    foreach (var field in def.Fields)
                        if (field.Kind == FieldKind.Bytes)
                            blobs[field.Slot] = [];
                        else
                            columns[field.Slot] = field.Kind == FieldKind.String ? world.Strings.Id(field.Default.Text ?? "") : field.Default.Number;
                }
                foreach (var (_, kind, field) in fields)
                {
                    switch (kind)
                    {
                        case FieldKind.Number:
                        {
                            var value = r.F64();
                            if (field is not null)
                                columns![field.Slot] = value;
                            break;
                        }
                        case FieldKind.Flag:
                        {
                            var value = r.Bool();
                            if (field is not null)
                                columns![field.Slot] = value ? 1 : 0;
                            break;
                        }
                        case FieldKind.String:
                        {
                            var text = r.Text();
                            if (field is not null)
                                columns![field.Slot] = world.Strings.Id(text);
                            break;
                        }
                        default:
                        {
                            var blob = r.Blob().ToArray();
                            if (field is not null)
                                blobs![field.Slot] = blob;
                            break;
                        }
                    }
                }
                if (def is not null)
                    parts.Add(new RecordPart(def, columns!, blobs!));
            }
            parts.Sort(static (a, b) => a.Def.Id.CompareTo(b.Def.Id));
            shapes[s] = new Ghost { X = x, Y = y, W = w, H = h, Owner = owner, Parts = [.. parts] };
        }
        var memory = new BuildMemory();
        for (var players = r.Count(); players > 0; players--)
        {
            var player = r.U32();
            for (var count = r.Count(); count > 0; count--)
            {
                var entity = r.U32();
                var shape = r.Int();
                if ((uint)shape >= (uint)shapes.Length)
                    throw new FormatException($"память: запись №{shape}");
                memory.Know(player, entity, shapes[shape]);
            }
        }
        return r.End ? memory : throw new FormatException("лишние байты в разделе памяти");
    }
}
