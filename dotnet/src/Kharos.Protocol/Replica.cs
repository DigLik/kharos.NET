using Kharos.Engine.Layers;
using Kharos.Engine.World;

namespace Kharos.Protocol;

/// <summary>Сигнал хоста на клиенте: тик и сам сигнал.</summary>
public readonly record struct ReplicaSignal(ulong Tick, Signal Signal);

/// <summary>
/// Реплика на клиенте (Р10: клиент всегда реплика): мир по схеме хоста, слои, туман и сигналы — из приветствия,
/// снимка и дельт. Мир — только на чтение: менять его можно приказом (<see cref="DeltaCodec.Order"/>). Вызовы — из
/// одного потока (потока клиента).
/// <para>
/// В мире реплики включено <see cref="GameWorld.TrackChanges"/>: клиент сам забирает изменения
/// (<see cref="GameWorld.TakeChanges"/>) — что сдвинулось, появилось, пропало. Снимок заменяет мир целиком
/// (<see cref="WorldVersion"/> растёт): ссылки на прежний <see cref="World"/> устаревают.
/// </para>
/// </summary>
public sealed class Replica
{
    private readonly List<ReplicaSignal> _signals = [];
    private int _sight = -1, _explored = -1;

    /// <summary>Приветствие хоста; null — ещё не пришло.</summary>
    public Welcome? Welcome { get; private set; }

    public Schema? Schema { get; private set; }

    /// <summary>Мир реплики; null — снимка ещё не было.</summary>
    public GameWorld? World { get; private set; }

    public LayerSet? Layers { get; private set; }

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

    /// <summary>Применяет сообщение хоста; неверное — <see cref="FormatException"/> (реплика могла измениться частично).</summary>
    public MessageType Apply(ReadOnlySpan<byte> message)
    {
        if (message.IsEmpty)
            throw new FormatException("пустое сообщение");
        var type = (MessageType)message[0];
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

    private void ApplyWelcome(Welcome welcome)
    {
        Welcome = welcome;
        Schema = welcome.BuildSchema();
        Layers = new LayerSet(welcome.Layers);
        Layers.AddPlayer(welcome.Player);
        _sight = Layers.Find(Fog.Sight) ?? -1;
        _explored = Layers.Find(Fog.Explored) ?? -1;
        World = null;
        Tick = welcome.Tick;
    }

    private void ApplySnapshot(ReadOnlySpan<byte> message)
    {
        if (Schema is null || Welcome is null)
            throw new FormatException("снимок до приветствия");
        var r = new WireReader(message);
        r.Byte();
        Tick = r.Varint();
        var world = WorldSave.Load(Schema, r.Blob());
        world.TrackChanges = true;
        var layers = new LayerSet(Welcome.Layers);
        layers.AddPlayer(Welcome.Player);
        foreach (var change in LayerCodec.Decode(r.Blob()))
            layers.Apply(change);
        if (!r.End)
            throw new FormatException("лишние байты в снимке");
        World = world;
        Layers = layers;
        WorldVersion++;
        FogRevision++;
    }

    private void ApplyDelta(ReadOnlySpan<byte> message)
    {
        if (World is not { } world || Layers is null)
            throw new FormatException("дельта до снимка");
        var schema = world.Schema;
        var r = new WireReader(message);
        r.Byte();
        Tick = r.Varint();
        r.Varint(); // номер следующей сущности хоста: реплике не нужен, сущности приходят со своими номерами

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
                throw new FormatException($"строка «{text}» не на своём номере {id}");
        }

        uint entity = 0;
        var destroyed = r.Count();
        for (var at = 0; at < destroyed; at++)
        {
            entity += r.U32();
            world.Destroy(entity);
        }

        entity = 0;
        var created = r.Count();
        Span<bool> present = schema.Count <= 256 ? stackalloc bool[schema.Count] : new bool[schema.Count];
        for (var at = 0; at < created; at++)
        {
            entity += r.U32();
            Ensure(world, entity);
            var components = r.Count();
            present.Clear();
            for (var k = 0; k < components; k++)
            {
                var component = Component(ref r, schema);
                present[component] = true;
                world.Add(entity, component);
                Values(ref r, world, entity, component, ulong.MaxValue);
            }
            // Снимок мог уже принести сущность: лишнее снимается.
            for (var c = 0; c < schema.Count; c++)
                if (!present[c])
                    world.Remove(entity, c);
        }

        var full = r.Count();
        for (var at = 0; at < full; at++)
        {
            entity = r.U32();
            var component = Component(ref r, schema);
            Ensure(world, entity);
            world.Add(entity, component);
            Values(ref r, world, entity, component, ulong.MaxValue);
        }

        var removed = r.Count();
        for (var at = 0; at < removed; at++)
        {
            entity = r.U32();
            world.Remove(entity, Component(ref r, schema));
        }

        var groups = r.Count();
        for (var g = 0; g < groups; g++)
        {
            var component = Component(ref r, schema);
            var mask = r.Varint();
            var count = r.Count();
            long previous = 0;
            for (var at = 0; at < count; at++)
            {
                previous += r.SVarint();
                entity = previous is > 0 and <= uint.MaxValue ? (uint)previous : throw new FormatException($"сущность {previous}");
                Values(ref r, world, entity, component, mask);
            }
        }

        var layerBytes = r.Blob();
        if (!layerBytes.IsEmpty)
        {
            var fog = false;
            foreach (var change in LayerCodec.Decode(layerBytes))
            {
                Layers.Apply(change);
                fog |= change.Layer is Fog.Sight or Fog.Explored;
            }
            if (fog)
                FogRevision++;
        }

        var signals = r.Count();
        for (var at = 0; at < signals; at++)
        {
            var name = r.Text();
            _signals.Add(new ReplicaSignal(Tick, new Signal(name, r.Blob().ToArray())));
        }
        if (!r.End)
            throw new FormatException("лишние байты в дельте");
    }

    /// <summary>Сущность с номером хоста: номера до неё выдаются заранее (у хоста они могли не дожить до рассылки).</summary>
    private static void Ensure(GameWorld world, uint entity)
    {
        if (world.Alive(entity))
            return;
        while (world.NextEntity <= entity)
            world.Reserve();
        if (!world.IsReserved(entity))
            throw new FormatException($"сущность {entity} уже жила");
        world.Revive(entity);
    }

    private static int Component(ref WireReader r, Schema schema)
    {
        var component = r.Varint();
        return component < (ulong)schema.Count ? (int)component : throw new FormatException($"нет компонента №{component}");
    }

    /// <summary>Читает поля по маске; у сущности нет компонента — значения пропускаются.</summary>
    private static void Values(ref WireReader r, GameWorld world, uint entity, int component, ulong mask)
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
                    var value = r.F64();
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
