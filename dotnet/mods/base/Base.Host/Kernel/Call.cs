using Base.Shared.Signals;
using Kharos.Mod.Sdk;
using MemoryPack;

namespace Base.Host.Kernel;

/// <summary>
/// Вызов мода глазами разделов (приказ, событие, система, запрос): мир со своими записями поверх (<see cref="Stage"/>),
/// команды, тик и память мода. Один на вызов: <c>Call.Of(ctx)</c> в том же вызове вернёт тот же. У запроса сцены нет:
/// читается мир до вызова, писать нельзя.
/// </summary>
internal sealed class Call
{
    private static Call? _last;
    private readonly Stage? _stage;
    private readonly Dictionary<(uint, int), ComponentValues?>? _read;

    private Call(Stage? stage, ulong tick, double dt)
    {
        _stage = stage;
        Tick = tick;
        Dt = dt;
        if (stage is null)
            _read = [];
    }

    public static Call Of(in OrderContext ctx) => Of(ctx.Stage(), ctx.Tick, ctx.Dt);

    public static Call Of(in EventContext ctx) => Of(ctx.Stage(), ctx.Tick, ctx.Dt);

    public static Call Of(in SystemContext ctx) => Of(ctx.Stage(), ctx.Tick, ctx.Dt);

    /// <summary>Вызов запроса: только чтение мира.</summary>
    public static Call Of(in QueryContext ctx) => _last = new Call(null, ctx.Tick, ctx.Dt);

    private static Call Of(Stage stage, ulong tick, double dt) =>
        _last is { } last && ReferenceEquals(last._stage, stage) ? last : _last = new Call(stage, tick, dt);

    /// <summary>Сцена вызова; у запроса — исключение.</summary>
    public Stage Stage => _stage ?? throw new InvalidOperationException("запрос только читает мир");

    /// <summary>Команды вызова.</summary>
    public CommandWriter Commands => Stage.Commands;

    /// <summary>Можно ли писать (не запрос).</summary>
    public bool Writes => _stage is not null;

    public ulong Tick { get; }

    /// <summary>Длина тика, секунды; у правок без тика — 0.</summary>
    public double Dt { get; }

    /// <summary>Память мода на этот вызов (<see cref="Kernel.Memory"/>).</summary>
    internal Memory? Memory { get; set; }

    public double Random() => Kharos.Mod.Sdk.Host.Random();

    // ===== Чтение =====

    public bool Alive(uint entity) => entity != 0 && (_stage?.Alive(entity) ?? Tools.Alive(entity));

    public bool Has(uint entity, ComponentRef component) =>
        entity != 0 && (_stage?.Has(entity, component) ?? (component.Tag ? Tools.Has(entity, component) : Read(entity, component) is not null));

    /// <summary>Число поля (флаг — 0/1, строка — номер); нет компонента — fallback.</summary>
    public double Num(uint entity, ComponentRef component, FieldRef field, double fallback = 0)
    {
        if (entity == 0)
            return fallback;
        if (_stage is not null)
            return _stage.TryNumber(entity, component, field, out var value) ? value : fallback;
        return Read(entity, component) is { } values ? values.Number(field) : fallback;
    }

    public bool Flag(uint entity, ComponentRef component, FieldRef field) => Num(entity, component, field) != 0;

    /// <summary>Строка поля-строки; нет компонента — null.</summary>
    public string? Text(uint entity, ComponentRef component, FieldRef field)
    {
        if (entity == 0)
            return null;
        if (_stage is not null)
            return _stage.Text(entity, component, field);
        return Read(entity, component) is { } values ? Tools.Text((uint)values.Number(field)) : null;
    }

    /// <summary>Байты поля; нет компонента — null.</summary>
    public byte[]? Bytes(uint entity, ComponentRef component, FieldRef field)
    {
        if (entity == 0)
            return null;
        if (_stage is not null)
            return _stage.Bytes(entity, component, field);
        return Read(entity, component)?.Bytes(field);
    }

    /// <summary>Хозяин сущности; ничья или без Owner — 0.</summary>
    public uint OwnerOf(uint entity) => (uint)Num(entity, S.Owner, S.OwnerPlayer);

    public bool IsOwn(uint player, uint entity) => Has(entity, S.Owner) && OwnerOf(entity) == player;

    /// <summary>Место сущности; нет Position — (NaN, NaN).</summary>
    public (double X, double Y) At(uint entity) =>
        Has(entity, S.Position) ? (Num(entity, S.Position, S.PositionX), Num(entity, S.Position, S.PositionY)) : (double.NaN, double.NaN);

    /// <summary>Вид юнита (запись units.json); не юнит — null.</summary>
    public string? UnitType(uint entity) => Has(entity, S.Unit) ? Core.ProtoName((int)Num(entity, S.Proto, S.ProtoId, -1), "units") : null;

    /// <summary>Вид здания (запись buildings.json) — у здания и площадки стройки; не здание — null.</summary>
    public string? BuildingType(uint entity) =>
        Has(entity, S.Building) || Has(entity, S.Site) ? Core.ProtoName((int)Num(entity, S.Proto, S.ProtoId, -1), "buildings") : null;

    private ComponentValues? Read(uint entity, ComponentRef component)
    {
        if (!_read!.TryGetValue((entity, component.Id), out var values))
            _read[(entity, component.Id)] = values = Tools.Get(entity, component);
        return values;
    }

    // ===== Запись =====

    public uint Spawn() => Commands.Spawn();

    public void Destroy(uint entity) => Commands.Destroy(entity);

    public CommandWriter Add(uint entity, ComponentRef component) => Commands.Add(entity, component);

    /// <summary>Поля компонента, который у сущности есть (или добавлен в этом вызове).</summary>
    public CommandWriter Set(uint entity, ComponentRef component) => Commands.Set(entity, component);

    /// <summary>Снимает компонент, если он есть.</summary>
    public void Remove(uint entity, ComponentRef component)
    {
        if (Has(entity, component))
            Commands.Remove(entity, component);
    }

    /// <summary>Сигнал игрокам (адресно).</summary>
    public void SignalTo<T>(string name, T payload, params ReadOnlySpan<uint> players) =>
        Commands.SignalTo(name, MemoryPackSerializer.Serialize(payload), players);

    /// <summary>Отказ приказу игрока: сигнал refused ему одному. reason — ключ сообщения (раздел host словаря).</summary>
    public void Refuse(uint player, string order, string reason)
    {
        if (Writes)
            SignalTo(RefusedSignal.Name, new RefusedSignal { Order = order, Reason = reason }, player);
    }
}
