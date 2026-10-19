using Kharos.Mod.Sdk.Client;
using static Base.Client.Widgets;

namespace Base.Client;

/// <summary>
/// Раздел Alerts: тревоги и уведомления (game/alerts.ts, alerts в gui/hud.ts). <see cref="AlertWatch"/> следит за
/// репликой; здесь лента слева под верхней полосой: строка цвета вида с маркером, со временем гаснет, щелчок ведёт камеру
/// к месту (CameraShake.FlyTo). Для мини-карты (раздел Hud) список лежит в <c>c.Shared["alerts.list"]</c> —
/// <see cref="Alert"/>[] с часами <c>alerts.clock</c> (double): кольцо мигает ALERT_SECONDS.
/// </summary>
internal sealed class AlertsSection : Section
{
    /// <summary>Ключи c.Shared: список уведомлений (<see cref="Alert"/>[]) и часы уведомлений, секунды (double).</summary>
    public const string ListKey = "alerts.list", ClockKey = "alerts.clock";

    private AlertWatch? _watch;

    public override void Setup(ClientBuilder client) => client.Ui("base.alerts", Frame);

    public override void Load(BaseState state)
    {
        base.Load(state);
        _watch = new AlertWatch(state.P.Deposits);
    }

    /// <summary>Цвет маркера вида уведомления (ALERT_COLORS).</summary>
    private static Color ColorOf(string kind) => kind switch
    {
        "attacked" or "nuke" => Bad,
        "spotted" => Warn,
        "found" => Good,
        "ping" => Ally,
        _ => Text,
    };

    public override void Frame(ClientContext c)
    {
        var editor = (c.Shell & ShellMode.Editor) != 0;
        if (Profile?.Ui.Contains("hud") != true || editor || _watch is null)
        {
            c.Shared.Remove(ListKey);
            return;
        }
        _watch.Update(c);
        var list = _watch.Current;
        c.Shared[ListKey] = list.ToArray();
        c.Shared[ClockKey] = _watch.Clock;
        if (list.Count == 0)
            return;

        var ui = c.Ui;
        var top = c.Shared.TryGetValue(TopBar.TopKey, out var value) && value is float height ? height : TopBar.HeightOf(ui.Width);
        Tr t = (key, args) => c.Text(key, args);
        ui.Box(new Style { Id = "alerts", Anchor = new Anchor(Left: 12, Top: top + 6), Skin = Skin.Panel, Padding = (10, 12), Direction = Direction.Column, Gap = 4 }, () =>
        {
            foreach (var alert in list)
            {
                var age = _watch.Clock - alert.At;
                var bright = AlertWatch.Seconds * 0.75;
                var fade = age < bright ? 1 : Math.Max(0.25, 1 - 0.75 * (age - bright) / (AlertWatch.Seconds - bright));
                var id = "alert:" + alert.Id;
                var color = ColorOf(alert.Kind);
                var click = ui.Button(id, new Style { Direction = Direction.Row, Align = Align.Center, Gap = 8, Padding = (2, 4), Alpha = (float)(Math.Round(fade * 8) / 8) }, () =>
                {
                    ui.Box(new Style { Width = 6, Height = 6, Background = color });
                    ui.Text(t("hud.alerts." + alert.Text), new TextStyle { Color = color, Shadow = Ink });
                });
                ui.Tip(id, t("hud.showPlace"));
                if (click is not null)
                    CameraShake.FlyTo(c, alert.X, alert.Y);
            }
        });
    }
}
