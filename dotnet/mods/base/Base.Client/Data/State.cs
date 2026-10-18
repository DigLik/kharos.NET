using Kharos.Mod.Sdk;
using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>Компоненты схемы мира, которые читает подача (берутся при загрузке).</summary>
internal sealed class Parts(LoadContext ctx)
{
    public ComponentRef Position { get; } = ctx.Component("Position");
    public ComponentRef Owner { get; } = ctx.Component("Owner");
    public ComponentRef Proto { get; } = ctx.Component("Proto");
    public ComponentRef Drive { get; } = ctx.Component("Drive");
    public ComponentRef Unit { get; } = ctx.Component("Unit");
    public ComponentRef Building { get; } = ctx.Component("Building");
    public ComponentRef Producer { get; } = ctx.Component("Producer");
    public ComponentRef Player { get; } = ctx.Component("Player");
    public ComponentRef Deposit { get; } = ctx.Component("Deposit");
}

/// <summary>Данные, компоненты и вспомогательные чтения мира — общие для панелей, управления и эффектов.</summary>
internal sealed class BaseState(ClientData data, Parts parts)
{
    private readonly Dictionary<int, Kind?> _kinds = [];

    public ClientData Data { get; } = data;
    public Parts P { get; } = parts;

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

    /// <summary>Кредиты своего игрока.</summary>
    public double Credits(ClientContext c)
    {
        var id = P.Player["id"];
        foreach (var entity in c.Each(P.Player))
            if (c.Get(entity, P.Player) is { } player && player.Number(id) == c.Player)
                return player.Number(P.Player["credits"]);
        return 0;
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
