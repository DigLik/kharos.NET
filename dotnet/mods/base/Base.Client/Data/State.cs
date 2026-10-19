using Kharos.Mod.Sdk;
using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>Компоненты схемы мира, которые читает подача (берутся при загрузке).</summary>
internal sealed class Parts(LoadContext ctx)
{
    public ComponentRef Position { get; } = ctx.Component("Position");
    public ComponentRef Owner { get; } = ctx.Component("Owner");
    public ComponentRef Sight { get; } = ctx.Component("Sight");
    public ComponentRef Proto { get; } = ctx.Component("Proto");
    // Модель TS (docs/dotnet-n5.md, Н5.1): ход — Mover, едет ли юнит — есть ли Path (цель — goalX, goalY), радиус — Body,
    // размер здания — Footprint (место здания — левый верхний тайл), турель стоит на носителе — Mount.parent.
    public ComponentRef Mover { get; } = ctx.Component("Mover");
    public ComponentRef Path { get; } = ctx.Component("Path");
    public ComponentRef Body { get; } = ctx.Component("Body");
    public ComponentRef Footprint { get; } = ctx.Component("Footprint");
    public ComponentRef Mount { get; } = ctx.Component("Mount");
    public ComponentRef Unit { get; } = ctx.Component("Unit");
    public ComponentRef Building { get; } = ctx.Component("Building");
    public ComponentRef Producer { get; } = ctx.Component("Producer");
    public ComponentRef Converting { get; } = ctx.Component("Converting");
    public ComponentRef Deploys { get; } = ctx.Component("Deploys");
    public ComponentRef Player { get; } = ctx.Component("Player");

    /// <summary>Наука игрока на сущности счёта и состояние лаборатории (раздел Research).</summary>
    public ComponentRef Science { get; } = ctx.Component("Science");
    public ComponentRef Research { get; } = ctx.Component("Research");
    public ComponentRef Off { get; } = ctx.Component("Off");

    /// <summary>Слой месторождений: предметы «вид, остаток».</summary>
    public LayerRef? Deposits { get; } = ctx.Layer("deposits");

    /// <summary>Стройка (площадка), укладка покрытия и слой готового покрытия — раздел Controls (постановка, зоны).</summary>
    public ComponentRef Site { get; } = ctx.Component("Site");
    public ComponentRef Pave { get; } = ctx.Component("Pave");
    public LayerRef? Paving { get; } = ctx.Layer("paving");
}

/// <summary>Данные, компоненты, подача партии и вспомогательные чтения мира — общие для разделов клиента.</summary>
internal sealed class BaseState(ClientData data, Parts parts, Base.Shared.Data.ClientSpec? profile = null)
{
    private readonly Dictionary<int, Kind?> _kinds = [];

    public ClientData Data { get; } = data;
    public Parts P { get; } = parts;

    /// <summary>Подача партии (подобъект client записи data/games.json); у чужой партии — null.</summary>
    public Base.Shared.Data.ClientSpec? Profile { get; } = profile;

    /// <summary>Вид сущности (запись data); у сущности нет вида или данных — null.</summary>
    public Kind? KindOf(ClientContext c, uint entity) => KindOf(c, c.Proto(entity));

    /// <summary>Вид по номеру Proto.id.</summary>
    public Kind? KindOf(ClientContext c, int proto)
    {
        if (proto < 0)
            return null;
        if (!_kinds.TryGetValue(proto, out var kind))
            _kinds[proto] = kind = c.ProtoPath(proto) is { } path ? Data.Find(path) : null;
        return kind;
    }

    /// <summary>Номер вида юнита по имени («buggy»); нет такого — -1.</summary>
    public int UnitProto(ClientContext c, string name) => c.ProtoId("units/" + name);

    /// <summary>Свой счёт: сущность Player игрока (чужие до клиента не доходят — @net owner); нет — null.</summary>
    public ComponentValues? Account(ClientContext c)
    {
        var id = P.Player["id"];
        foreach (var entity in c.Each(P.Player))
            if (c.Get(entity, P.Player) is { } player && player.Number(id) == c.Player)
                return player;
        return null;
    }

    /// <summary>Кредиты своего игрока.</summary>
    public double Credits(ClientContext c) => Account(c)?.Number(P.Player["credits"]) ?? 0;

    /// <summary>Радиус тела юнита, тайлы; нет тела — запасной.</summary>
    public double Radius(ClientContext c, uint entity, double fallback = 0.5) =>
        c.Get(entity, P.Body) is { } body ? body.Number(P.Body["radius"]) : fallback;

    /// <summary>Размер основания здания, тайлы; нет основания — null.</summary>
    public (double W, double H)? Size(ClientContext c, uint entity) =>
        c.Get(entity, P.Footprint) is { } footprint ? (footprint.Number(P.Footprint["w"]), footprint.Number(P.Footprint["h"])) : null;

    /// <summary>Середина сущности: у здания — середина основания (место здания — левый верхний тайл), у остальных — Position.</summary>
    public (double X, double Y)? Center(ClientContext c, uint entity)
    {
        if (c.Pose(entity) is not { } at)
            return null;
        return Size(c, entity) is { } size ? (at.X + size.W / 2, at.Y + size.H / 2) : (at.X, at.Y);
    }

    /// <summary>Сколько тиков строится юнит вида unit (round(buildTime / тик), как buildTicks на хосте); нет времени — 1.</summary>
    public double BuildTicks(ClientContext c, Kind? unit) =>
        unit is not null && unit.TryNumber("buildTime", out var seconds) ? Math.Max(1, Math.Floor(seconds / c.TickDt + 0.5)) : 1;

    /// <summary>Очередь производителя: номера видов юнитов (Producer.queue — i32 подряд), первый строится.</summary>
    public static List<int> Queue(ComponentValues producer, ComponentRef component)
    {
        var bytes = producer.Bytes(component["queue"]);
        var list = new List<int>(bytes.Length / 4);
        for (var at = 0; at + 4 <= bytes.Length; at += 4)
            list.Add(System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at)));
        return list;
    }
}

/// <summary>Общая память обработчиков подачи (c.shared пилота): их читают и панели, и управление, и звук.</summary>
internal static class Shared
{
    /// <summary>Выделенное: массив номеров сущностей.</summary>
    public const string SelectionKey = "selection";

    /// <summary>Долгое касание стало приказом: отпускание не выделяет.</summary>
    public const string SwallowKey = "swallow";

    /// <summary>Сущность, за которой следует камера follow.</summary>
    public const string FollowKey = "follow";

    /// <summary>Час суток (его пишет погода, читают часы панели) и настроение музыки ("night" или "calm").</summary>
    public const string HourKey = "hour", MoodKey = "mood";

    public static uint[] Selection(ClientContext c) => c.Shared.TryGetValue(SelectionKey, out var value) && value is uint[] list ? list : [];
}
