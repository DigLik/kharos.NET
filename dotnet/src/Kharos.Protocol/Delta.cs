using Kharos.Engine.Layers;
using Kharos.Engine.Terrain;
using Kharos.Engine.World;

namespace Kharos.Protocol;

/// <summary>
/// Изменения мира с прошлой рассылки — итогом, а не по событиям: что появилось (целиком), что пропало, у оставшихся —
/// добавленные заново целиком, снятые компоненты и изменённые поля. Сущность, появившаяся и пропавшая между рассылками,
/// не попадает никуда. Значения берутся из мира при кодировании (<see cref="DeltaCodec.Encode"/>): кодировать — до
/// следующего тика. Одна на всех зрителей, фильтр — при кодировании.
/// </summary>
public sealed class WorldDelta
{
    public ulong Tick { get; private set; }
    public uint NextEntity { get; private set; }

    /// <summary>Номер первой новой строки мира и сами строки (строки только добавляются).</summary>
    public int StringsFrom { get; private set; }
    public List<string> Strings { get; } = [];

    /// <summary>Пропавшие (жившие до прошлой рассылки) — по возрастанию.</summary>
    public List<uint> Destroyed { get; } = [];

    /// <summary>Появившиеся и живые — по возрастанию: уходят со всеми компонентами.</summary>
    public List<uint> Created { get; } = [];

    /// <summary>Компоненты прежних сущностей, которые уходят целиком (добавлены, возможно, после снятия).</summary>
    public List<ComponentChange> Full { get; } = [];

    /// <summary>Снятые компоненты прежних сущностей.</summary>
    public List<ComponentChange> Removed { get; } = [];

    /// <summary>Изменённые поля прежних компонентов — по компоненту, внутри — по маске (в порядке правок).</summary>
    public List<FieldChange> Fields { get; } = [];

    public List<LayerChange> Layers { get; } = [];
    public List<Signal> Signals { get; } = [];

    /// <summary>Правки карты для всех: у партии нет тумана, игроки знают всё (<see cref="MessageType.LandEdits"/>).</summary>
    public List<LandTile> LandEdits { get; } = [];

    /// <summary>Правки карты по игрокам: что каждый узнал с прошлой рассылки (у партии с туманом — память игроков).</summary>
    public Dictionary<uint, List<LandTile>> LandKnown { get; } = [];

    /// <summary>
    /// Блоки запоминаемых слоёв (покрытие) по игрокам, какими игрок их знает: идут ему вместо блоков слоя мира, которые
    /// при тумане ему не уходят.
    /// </summary>
    public Dictionary<uint, List<LayerChange>> LayersKnown { get; } = [];

    /// <summary>Правки карты, которые идут игроку этой рассылкой.</summary>
    public IReadOnlyList<LandTile> LandFor(uint player) => LandEdits.Count > 0 ? LandEdits : LandKnown.TryGetValue(player, out var known) ? known : [];

    public bool IsEmpty => Strings.Count == 0 && Destroyed.Count == 0 && Created.Count == 0 && Full.Count == 0 && Removed.Count == 0 && Fields.Count == 0 && Layers.Count == 0 && Signals.Count == 0 && LandEdits.Count == 0 && LandKnown.Count == 0 && LayersKnown.Count == 0;

    /// <summary>Итог изменений: changes — из <see cref="GameWorld.TakeChanges"/>, строки — с номера stringsFrom.</summary>
    public static WorldDelta Build(GameWorld world, WorldChanges changes, int stringsFrom, IEnumerable<LayerChange> layers, IEnumerable<Signal> signals, ulong tick)
    {
        var delta = new WorldDelta { Tick = tick, NextEntity = world.NextEntity, StringsFrom = stringsFrom };
        for (var id = stringsFrom; id < world.Strings.Count; id++)
            delta.Strings.Add(world.Strings.Text((uint)id));
        var born = new HashSet<uint>(changes.Spawned);
        foreach (var entity in born)
            if (world.Alive(entity))
                delta.Created.Add(entity);
        delta.Created.Sort();
        var created = new HashSet<uint>(delta.Created);
        foreach (var entity in new HashSet<uint>(changes.Destroyed))
            if (!born.Contains(entity))
                delta.Destroyed.Add(entity);
        delta.Destroyed.Sort();
        var touched = new HashSet<ComponentChange>();
        foreach (var change in changes.Added.Concat(changes.Removed))
        {
            if (created.Contains(change.Entity) || !world.Alive(change.Entity) || !touched.Add(change))
                continue;
            (world.Has(change.Entity, change.Component) ? delta.Full : delta.Removed).Add(change);
        }
        delta.Full.Sort(static (a, b) => a.Entity != b.Entity ? a.Entity.CompareTo(b.Entity) : a.Component.CompareTo(b.Component));
        delta.Removed.Sort(static (a, b) => a.Entity != b.Entity ? a.Entity.CompareTo(b.Entity) : a.Component.CompareTo(b.Component));
        var full = delta.Full.Count > 0 ? new HashSet<ComponentChange>(delta.Full) : null;
        var kept = new List<FieldChange>(changes.Fields.Count);
        foreach (var field in changes.Fields)
            if ((created.Count == 0 || !created.Contains(field.Entity)) && world.Has(field.Entity, field.Component) && (full is null || !full.Contains(new ComponentChange(field.Entity, field.Component))))
                kept.Add(field);
        // Поля уже идут по компонентам; внутри компонента — по маске, порядок правок сохраняется (без сортировки).
        var masks = new List<ulong>();
        for (var start = 0; start < kept.Count;)
        {
            var end = start;
            masks.Clear();
            for (; end < kept.Count && kept[end].Component == kept[start].Component; end++)
                if (!masks.Contains(kept[end].Mask))
                    masks.Add(kept[end].Mask);
            if (masks.Count == 1)
                delta.Fields.AddRange(kept.GetRange(start, end - start));
            else
                foreach (var mask in masks)
                    for (var at = start; at < end; at++)
                        if (kept[at].Mask == mask)
                            delta.Fields.Add(kept[at]);
            start = end;
        }
        delta.Layers.AddRange(layers);
        delta.Signals.AddRange(signals);
        return delta;
    }
}

