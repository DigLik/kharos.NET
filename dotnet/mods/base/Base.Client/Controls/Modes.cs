using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Режимы наведения мышью (scene.placing, paving, deploying в TS) — общая память разделов подачи (c.Shared). Панели
/// (нижняя панель строителя, кнопка «Развернуть» у MCV, наведение на кнопку здания) включают режим вызовами отсюда, а
/// управление (Controls/Placing.cs) его ведёт: призрак здания, протяжка покрытия, отправка приказов, отмена правой
/// кнопкой и Esc. Пока режим включён, выделение и приказы точкой молчат.
/// </summary>
internal static class Modes
{
    /// <summary>Вид здания (строка: «generator», «wall»…), место под которое выбирают.</summary>
    public const string PlacingKey = "mode.placing";

    /// <summary>Инструмент покрытия (строка): foundation, road, remove, pipe, wall, gate, well, peak, cliff.</summary>
    public const string PavingKey = "mode.paving";

    /// <summary>MCV (номер сущности, uint), которому выбирают место для разворота.</summary>
    public const string DeployingKey = "mode.deploying";

    /// <summary>Указатель стоит на кнопке здания в меню стройки (bool): показать границы зон, как при зажатом Alt.</summary>
    public const string ZonesHoverKey = "mode.zonesHover";

    /// <summary>Группы 1–9 и 0: словарь «цифра → номера сущностей» (его ведёт выбор, читают цифры над юнитами и панели).</summary>
    public const string GroupsKey = "groups";

    /// <summary>Вид здания, место под которое выбирают; нет — null.</summary>
    public static string? Placing(ClientContext c) => c.Shared.TryGetValue(PlacingKey, out var value) ? value as string : null;

    /// <summary>Инструмент покрытия; нет — null.</summary>
    public static string? Paving(ClientContext c) => c.Shared.TryGetValue(PavingKey, out var value) ? value as string : null;

    /// <summary>MCV, которому выбирают место; нет — null.</summary>
    public static uint? Deploying(ClientContext c) => c.Shared.TryGetValue(DeployingKey, out var value) && value is uint entity ? entity : null;

    /// <summary>Включён ли какой-нибудь режим выбора места.</summary>
    public static bool Active(ClientContext c) => Placing(c) is not null || Paving(c) is not null || Deploying(c) is not null;

    /// <summary>Выбирать место под здание вида building (один режим за раз).</summary>
    public static void Place(ClientContext c, string building)
    {
        Cancel(c);
        c.Shared[PlacingKey] = building;
    }

    /// <summary>Тянуть мышью покрытие инструментом tool.</summary>
    public static void Pave(ClientContext c, string tool)
    {
        Cancel(c);
        c.Shared[PavingKey] = tool;
    }

    /// <summary>Выбирать место для разворота MCV.</summary>
    public static void Deploy(ClientContext c, uint mcv)
    {
        Cancel(c);
        c.Shared[DeployingKey] = mcv;
    }

    /// <summary>Выходит из режима, ничего не отдавая; false — режима не было.</summary>
    public static bool Cancel(ClientContext c)
    {
        var any = false;
        foreach (var key in (ReadOnlySpan<string>)[PlacingKey, PavingKey, DeployingKey])
            any |= c.Shared.Remove(key);
        return any;
    }
}
