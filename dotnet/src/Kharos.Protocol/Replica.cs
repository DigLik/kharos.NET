using Kharos.Engine.Layers;
using Kharos.Engine.Terrain;
using Kharos.Engine.World;

namespace Kharos.Protocol;

/// <summary>Сигнал хоста на клиенте: тик и сам сигнал.</summary>
public readonly record struct ReplicaSignal(ulong Tick, Signal Signal);

/// <summary>Сдвиг сущности последним сообщением: место и угол до него и за сколько тиков она пришла к новому (span).</summary>
public readonly record struct PoseMove(uint Entity, double X, double Y, double Angle, int Span);

/// <summary>
/// Реплика на клиенте (Р10: клиент всегда реплика): мир по схеме хоста, слои, туман и сигналы — из приветствия,
/// снимка и дельт. Мир — только на чтение: менять его можно приказом (<see cref="DeltaCodec.Order"/>). Вызовы — из
/// одного потока (потока клиента).
/// <para>
/// Мир реплики — только то, что зрителю видно (интерес хоста): сущности приходят, когда входят в обзор, и уходят,
/// когда пропадают из него. Числа — с точностью <see cref="Format"/>. Изменения последнего сообщения — в
/// <see cref="Changes"/>, сдвиги мест — в <see cref="Moves"/>. Снимок заменяет мир целиком (<see cref="WorldVersion"/>
/// растёт): ссылки на прежний <see cref="World"/> устаревают. Сообщения вне мира (чат, игроки, куски модов, отказ)
/// копятся в <see cref="Inbox"/>.
/// </para>
/// </summary>
public sealed class Replica
{
    /// <summary>Больше сообщений вне мира не копится: старые выбрасываются.</summary>
    public const int MaxInbox = 1024;

    private readonly List<ReplicaSignal> _signals = [];
    private readonly List<PoseMove> _moves = [];
    private readonly Dictionary<uint, int> _moved = [];
    private int _sight = -1, _explored = -1;
    private Kharos.Engine.Terrain.Land? _terrain;
    private readonly List<LandTile> _landQueue = [];

    /// <summary>Приветствие хоста; null — ещё не пришло.</summary>
    public Welcome? Welcome { get; private set; }

    public Schema? Schema { get; private set; }

    /// <summary>Точность чисел хоста.</summary>
    public NetFormat? Format { get; private set; }

    /// <summary>Мир реплики; null — снимка ещё не было.</summary>
    public GameWorld? World { get; private set; }

    public LayerSet? Layers { get; private set; }

    /// <summary>Карта мира байтами от хоста (Land.Pack), если её не повторить генератором; null — не присылали.</summary>
    public byte[]? Land { get; private set; }

    /// <summary>
    /// Карта клиента: из приветствия (генератор и область) или присланная хостом (<see cref="Land"/>), с правками
    /// <see cref="MessageType.LandEdits"/> поверх; null — у партии нет карты. Собирается при первом обращении; после него
    /// правки хоста применяются к ней сразу (<see cref="LandRevision"/> растёт), до него копятся и ложатся при сборке.
    /// Неверная карта хоста — <see cref="FormatException"/>.
    /// </summary>
    public Kharos.Engine.Terrain.Land? Terrain
    {
        get
        {
            if (_terrain is null && Welcome is { Land: { } config, Map: { } map })
            {
                _terrain = Land is { } packed
                    ? Kharos.Engine.Terrain.Land.Unpack(config, packed)
                    : new Kharos.Engine.Terrain.Land(config, LandArea.Of(map.Left, map.Top, map.Right, map.Bottom));
                foreach (var edit in _landQueue)
                    _terrain.SetStored(edit.X, edit.Y, edit.Tile);
                _landQueue.Clear();
            }
            return _terrain;
        }
    }

    /// <summary>Сколько правок карты хоста применено (для тестов и перерисовки).</summary>
    public int LandRevision { get; private set; }

    /// <summary>Тик хоста последнего снимка или дельты.</summary>
    public ulong Tick { get; private set; }

    /// <summary>Сколько раз мир заменялся снимком.</summary>
    public int WorldVersion { get; private set; }

    /// <summary>Растёт, когда пришли изменения тумана (слои обзора) или снимок: клиенту — перерисовать туман.</summary>
    public int FogRevision { get; private set; }

    /// <summary>Сколько сообщений применено.</summary>
    public long Messages { get; private set; }

    public uint Player => Welcome?.Player ?? 0;

    /// <summary>Есть приветствие и снимок: можно рисовать.</summary>
    public bool Ready => World is not null;

    /// <summary>Сигналы, ещё не забранные клиентом.</summary>
    public int PendingSignals => _signals.Count;

    /// <summary>Изменения мира последним сообщением (снимок или дельта).</summary>
    public WorldChanges Changes { get; } = new();

    /// <summary>Сдвиги мест и углов последним сообщением.</summary>
    public IReadOnlyList<PoseMove> Moves => _moves;

    /// <summary>Сообщения вне мира по порядку прихода: забирает и очищает тот, кто их разбирает.</summary>
    public List<byte[]> Inbox { get; } = [];

    /// <summary>Применяет все пришедшие сообщения транспорта; сколько применено.</summary>
    public int Pump(ITransport transport)
    {
        var count = 0;
        while (transport.TryReceive(out var message))
        {
            Apply(message);
            count++;
        }
        return count;
    }

    /// <summary>
    /// Применяет сообщение хоста; неверное — <see cref="FormatException"/> (реплика могла измениться частично).
    /// Сообщения вне мира откладываются в <see cref="Inbox"/>.
    /// </summary>
    public MessageType Apply(ReadOnlySpan<byte> message)
    {
        var type = Protocol.Messages.TypeOf(message);
        _moves.Clear();
        _moved.Clear();
        switch (type)
        {
            case MessageType.Welcome:
                ApplyWelcome(Welcome.Decode(message));
                break;
            case MessageType.Snapshot:
                ApplySnapshot(message);
                break;
            case MessageType.Delta:
                ApplyDelta(message);
                break;
            case MessageType.Land:
                Land = Protocol.Messages.ReadLand(message);
                (_terrain, LandRevision) = (null, 0);
                _landQueue.Clear();
                break;
            case MessageType.LandEdits:
                ApplyLandEdits(Protocol.Messages.ReadLandEdits(message));
                break;
            case MessageType.Chat or MessageType.Chunk or MessageType.Players or MessageType.Refused or MessageType.Commands:
                if (Inbox.Count >= MaxInbox)
                    Inbox.RemoveAt(0);
                Inbox.Add(message.ToArray());
                break;
            default:
                throw new FormatException($"сообщение 0x{(byte)type:x2} клиенту не шлют");
        }
        Messages++;
        return type;
    }

    /// <summary>Забирает сигналы по порядку прихода.</summary>
    public void TakeSignals(List<ReplicaSignal> into)
    {
        into.AddRange(_signals);
        _signals.Clear();
    }

    /// <summary>Видит ли игрок тайл сейчас. Тумана нет — всё видно; вне карты — нет.</summary>
    public bool Sees(int x, int y) => FogValue(_sight, x, y);

    /// <summary>Разведан ли тайл.</summary>
    public bool Explored(int x, int y) => FogValue(_explored, x, y);

    /// <summary>Туман над тайлом (fogPass.ts): 0 — виден, 0,5 — разведан, 0,94 — нет.</summary>
    public double FogAlpha(int x, int y) => Fog.Alpha(Sees(x, y), Explored(x, y));

    private bool FogValue(int layer, int x, int y)
    {
        if (Welcome is not { } welcome || !welcome.Fog)
            return true;
        if (welcome.Map is not { } map || !map.Contains(x, y) || layer < 0 || Layers is null)
            return false;
        return Layers.Get(layer, LayerOwner.Player(welcome.Player), x, y, 0) != 0;
    }

    /// <summary>Правки карты хоста: на собранную карту — сразу, иначе в очередь.</summary>
    private void ApplyLandEdits(List<LandTile> edits)
    {
        foreach (var edit in edits)
        {
            if (_terrain is null)
                _landQueue.Add(edit);
            else
                _terrain.SetStored(edit.X, edit.Y, edit.Tile);
        }
        LandRevision += edits.Count;
    }

    private void ApplyWelcome(Welcome welcome)
    {
        Welcome = welcome;
        Schema = welcome.BuildSchema();
        Format = welcome.BuildFormat();
        Layers = new LayerSet(welcome.Layers);
        Layers.AddPlayer(welcome.Player);
        _sight = Layers.Find(Fog.Sight) ?? -1;
        _explored = Layers.Find(Fog.Explored) ?? -1;
        World = null;
        Land = null;
        (_terrain, LandRevision) = (null, 0);
        _landQueue.Clear();
        Tick = welcome.Tick;
    }

    private void ApplySnapshot(ReadOnlySpan<byte> message)
    {
        if (Schema is null || Welcome is null)
            throw new FormatException("снимок до приветствия");
        var world = new GameWorld(Schema) { TrackChanges = true };
        var layers = new LayerSet(Welcome.Layers);
        layers.AddPlayer(Welcome.Player);
        var r = new WireReader(message);
        r.Byte();
        Tick = r.Varint();
        ApplyBody(ref r, world, layers);
        World = world;
        Layers = layers;
        _moves.Clear();
        WorldVersion++;
        FogRevision++;
    }

    private void ApplyDelta(ReadOnlySpan<byte> message)
    {
        if (World is not { } world || Layers is null)
            throw new FormatException("дельта до снимка");
        var r = new WireReader(message);
        r.Byte();
        Tick = r.Varint();
        ApplyBody(ref r, world, Layers);
    }

    private void ApplyBody(ref WireReader r, GameWorld world, LayerSet layers)
    {
        var schema = world.Schema;
        var from = r.Int();
        var strings = r.Count();
        for (var at = 0; at < strings; at++)
        {
            var text = r.Text();
            var id = from + at;
            if (id < world.Strings.Count)
            {
                if (world.Strings.Text((uint)id) != text)
                    throw new FormatException($"строка №{id}: «{text}», а у реплики «{world.Strings.Text((uint)id)}»");
            }
            else if (world.Strings.Id(text) != id)
            {
                throw new FormatException($"строка «{text}» не на своём номере {id}");
            }
        }

        long previous = 0;
        for (var count = r.Count(); count > 0; count--)
            world.Destroy(Entity(ref r, ref previous));

        previous = 0;
        var created = r.Count();
        Span<bool> present = schema.Count <= 256 ? stackalloc bool[schema.Count] : new bool[schema.Count];
        for (var at = 0; at < created; at++)
        {
            var entity = Entity(ref r, ref previous);
            world.Restore(entity);
            var components = r.Count();
            present.Clear();
            for (var k = 0; k < components; k++)
            {
                var component = Component(ref r, schema);
                present[component] = true;
                world.Add(entity, component);
                Values(ref r, world, entity, component, ulong.MaxValue);
            }
            // Сущность могла уже быть у реплики: лишнее снимается.
            for (var c = 0; c < schema.Count; c++)
                if (!present[c])
                    world.Remove(entity, c);
        }

        for (var count = r.Count(); count > 0; count--)
        {
            var entity = r.U32();
            var component = Component(ref r, schema);
            world.Restore(entity);
            Moving(world, entity, component);
            world.Add(entity, component);
            Values(ref r, world, entity, component, ulong.MaxValue);
        }

        for (var count = r.Count(); count > 0; count--)
        {
            var entity = r.U32();
            world.Remove(entity, Component(ref r, schema));
        }

        var groups = r.Count();
        for (var g = 0; g < groups; g++)
        {
            var component = Component(ref r, schema);
            var mask = r.Varint();
            var count = r.Count();
            previous = 0;
            for (var at = 0; at < count; at++)
            {
                var entity = Entity(ref r, ref previous);
                Moving(world, entity, component);
                Values(ref r, world, entity, component, mask);
            }
        }

        previous = 0;
        for (var count = r.Count(); count > 0; count--)
        {
            var entity = Entity(ref r, ref previous);
            var span = r.Int();
            if (_moved.TryGetValue(entity, out var index))
                _moves[index] = _moves[index] with { Span = Math.Max(span, 1) };
        }

        var packed = r.Bool();
        var layerBytes = r.Blob();
        if (packed)
            layerBytes = ContentPack.Inflate(layerBytes, 64 << 20);
        if (!layerBytes.IsEmpty)
        {
            var fog = false;
            foreach (var change in LayerCodec.Decode(layerBytes))
            {
                layers.Apply(change);
                fog |= change.Layer is Fog.Sight or Fog.Explored;
            }
            if (fog)
                FogRevision++;
        }

        for (var count = r.Count(); count > 0; count--)
        {
            var name = r.Text();
            _signals.Add(new ReplicaSignal(Tick, new Signal(name, r.Blob().ToArray())));
        }
        if (!r.End)
            throw new FormatException("лишние байты в дельте");
        world.TakeChanges(Changes);
    }

    /// <summary>Место и угол сущности до первой правки этим сообщением — в <see cref="Moves"/>.</summary>
    private void Moving(GameWorld world, uint entity, int component)
    {
        if (component is not (Builtin.Position or Builtin.Facing) || _moved.ContainsKey(entity))
            return;
        if (!world.TryNumber(entity, Builtin.Position, 0, out var x) || !world.TryNumber(entity, Builtin.Position, 1, out var y))
            return;
        var angle = world.TryNumber(entity, Builtin.Facing, 0, out var facing) ? facing : 0;
        _moved[entity] = _moves.Count;
        _moves.Add(new PoseMove(entity, x, y, angle, 1));
    }

    private static uint Entity(ref WireReader r, ref long previous)
    {
        previous += r.SVarint();
        return previous is > 0 and < uint.MaxValue ? (uint)previous : throw new FormatException($"сущность {previous}");
    }

    private static int Component(ref WireReader r, Schema schema)
    {
        var component = r.Varint();
        return component < (ulong)schema.Count ? (int)component : throw new FormatException($"нет компонента №{component}");
    }

    /// <summary>Читает поля по маске; у сущности нет компонента — значения пропускаются.</summary>
    private void Values(ref WireReader r, GameWorld world, uint entity, int component, ulong mask)
    {
        var store = world.Store(component);
        var has = world.Has(entity, component);
        var fields = store.Def.Fields;
        for (var index = 0; index < fields.Count; index++)
        {
            if ((mask & (1UL << index)) == 0)
                continue;
            var field = fields[index];
            switch (field.Kind)
            {
                case FieldKind.Number:
                {
                    var value = Format!.Read(ref r, component, index);
                    if (has)
                        world.SetNumber(entity, component, field.Index, value);
                    break;
                }
                case FieldKind.Flag:
                {
                    var value = r.Bool();
                    if (has)
                        world.SetNumber(entity, component, field.Index, value ? 1 : 0);
                    break;
                }
                case FieldKind.String:
                {
                    var id = r.U32();
                    if (!world.Strings.Contains(id))
                        throw new FormatException($"{store.Def.Name}.{field.Name}: нет строки №{id}");
                    if (has)
                        world.SetNumber(entity, component, field.Index, id);
                    break;
                }
                default:
                {
                    var bytes = r.Blob();
                    if (has)
                        world.SetBytes(entity, component, field.Index, bytes);
                    break;
                }
            }
        }
    }
}
