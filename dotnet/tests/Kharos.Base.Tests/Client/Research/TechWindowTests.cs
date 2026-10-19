using Base.Shared.Orders;
using Kharos.Client.Ui;

namespace Kharos.Base.Tests.ResearchWindow;

/// <summary>
/// Окно исследований клиента base (Research/TechWindow.cs, gui/tech.ts) на обвязке Viewer: схватка skirmish, хост
/// сценария и клиент в режиме CLR. Тестов TS у окна нет — проверяется поведение: открытие, состояния тем, приказы по
/// щелчку, путь к теме, отмена.
/// </summary>
public sealed class TechWindowTests
{
    // Цвета фона значка плитки по доступности (STATUS_COLORS в tech.ts): готово, можно, станет можно, закрыто.
    private static readonly uint Done = new Kharos.Mod.Sdk.Client.Color(97, 132, 163, 46).Rgba;
    private static readonly uint Available = new Kharos.Mod.Sdk.Client.Color(143, 224, 168, 51).Rgba;
    private static readonly uint Soon = new Kharos.Mod.Sdk.Client.Color(240, 201, 90, 51).Rgba;
    private static readonly uint Locked = new Kharos.Mod.Sdk.Client.Color(255, 122, 102, 56).Rgba;

    private static (Sim Sim, Viewer Viewer) Scene(params string[] learned)
    {
        Assert.SkipWhen(Viewer.Unavailable(Sim.DefaultMode) is not null, Viewer.Unavailable(Sim.DefaultMode) ?? "");
        var sim = Sim.Create(new SimOptions { Game = "skirmish", Players = [1] });
        sim.SetCredits(1, 5000);
        if (learned.Length > 0)
            sim.Learn(1, learned);
        // Высокий экран: плитки закрытых тем лежат ниже открытых и должны попадать на экран.
        var viewer = sim.Viewer(1, new ViewerOptions { Width = 1600, Height = 1400 });
        viewer.Run(0.5);
        return (sim, viewer);
    }

    /// <summary>Открыть окно клавишей R и дождаться плиток.</summary>
    private static void Open(Viewer viewer)
    {
        viewer.Press("r");
        viewer.Run(0.2);
        Assert.True(viewer.Ui.Has("tech"), "окно исследований не открылось");
    }

    /// <summary>Доступность темы по цвету фона значка её плитки.</summary>
    private static string StatusOf(Viewer viewer, string topic)
    {
        var tile = viewer.Ui.Find("tech:" + topic) ?? throw new InvalidOperationException($"нет плитки {topic}");
        var back = tile.Children[0].Style.Background?.Rgba;
        return back == Done ? "done" : back == Available ? "available" : back == Soon ? "soon" : back == Locked ? "locked" : $"?{back}";
    }

    private static void ClearOrders(Viewer viewer) => viewer.Sent.Clear();

    [Fact(DisplayName = "окно исследований открывается клавишей R и закрывается по R, Esc и кнопке «✕»")]
    public void OpensAndClosesByHotkey()
    {
        var (sim, viewer) = Scene();
        using var _ = sim;
        Assert.False(viewer.Ui.Has("tech"));
        Open(viewer);
        Assert.Contains("Исследования", viewer.Ui.Texts());
        Assert.Contains("Лабораторий нет: постройте лабораторию — без неё исследования стоят", viewer.Ui.Texts());
        Assert.Contains("Очередь:", viewer.Ui.Texts());
        // Дерево целиком: у каждой темы есть плитка (кроме прячущихся за прокруткой, видна хотя бы первая).
        Assert.True(viewer.Ui.Has("tech:science"));
        Assert.True(viewer.Ui.Has("tech:list"));
        viewer.Press("r");
        viewer.Run(0.1);
        Assert.False(viewer.Ui.Has("tech"));
        Open(viewer);
        viewer.Press("escape");
        viewer.Run(0.1);
        Assert.False(viewer.Ui.Has("tech"), "Esc не закрыл окно");
        Open(viewer);
        viewer.Click("tech:close");
        viewer.Run(0.1);
        Assert.False(viewer.Ui.Has("tech"), "«✕» не закрыла окно");
        Assert.Empty(viewer.Log);
    }

    [Fact(DisplayName = "состояния тем: открытая, доступная, станет доступной, закрытая; после открытия темы зависимые становятся доступными")]
    public void StatesFollowResearch()
    {
        var (sim, viewer) = Scene();
        using var _ = sim;
        Open(viewer);
        // В начале ничего не открыто: «научный метод» — открытие (само, при постройке лаборатории), поэтому всё за ним закрыто.
        Assert.Equal("locked", StatusOf(viewer, "science"));
        Assert.Equal("locked", StatusOf(viewer, "walls"));
        Assert.Equal("locked", StatusOf(viewer, "pipes"));

        // Открыт научный метод: стены, радар и дороги можно исследовать сейчас, трубам нужна руда металла — закрыты.
        sim.Learn(1, "science");
        viewer.Run(0.5);
        Assert.Equal("done", StatusOf(viewer, "science"));
        Assert.Equal("available", StatusOf(viewer, "walls"));
        Assert.Equal("available", StatusOf(viewer, "radar"));
        Assert.Equal("locked", StatusOf(viewer, "pipes"));

        // Исследованная тема переходит в раздел «Исследовано», у неё нет цены.
        sim.Learn(1, "walls");
        viewer.Run(0.5);
        Assert.Equal("done", StatusOf(viewer, "walls"));
        Assert.Contains("Исследовано", viewer.Ui.Texts());
        Assert.Contains("Пока закрыто", viewer.Ui.Texts());
        Assert.Empty(viewer.Log);
    }

    [Fact(DisplayName = "щелчок по доступной теме шлёт приказ research, Shift+щелчок — с флагом «в начало»; открытую тему и открытие не ставят")]
    public void ClickSendsResearchOrder()
    {
        var (sim, viewer) = Scene("science", "walls");
        using var _ = sim;
        Open(viewer);
        ClearOrders(viewer);

        viewer.Click("tech:radar");
        var order = Assert.Single(viewer.Sent.Of<ResearchOrder>());
        Assert.Equal(("radar", false), (order.Topic, order.Front));
        viewer.Run(0.3);
        // Хост принял тему в план; в окне она в очереди и с номером на плитке.
        Assert.Equal(["radar"], sim.PlanOf(1));
        Assert.Equal("available", StatusOf(viewer, "radar"));
        Assert.True(viewer.Ui.Has("tech:queued:radar"));
        Assert.Equal("1", viewer.Ui.Text("tech:radar")?.Split(' ').FirstOrDefault());

        ClearOrders(viewer);
        viewer.Click("tech:roads", shift: true);
        var front = Assert.Single(viewer.Sent.Of<ResearchOrder>());
        Assert.Equal(("roads", true), (front.Topic, front.Front));
        viewer.Run(0.3);
        Assert.Equal(["roads", "radar"], sim.PlanOf(1));

        // Исследованная тема и тема-открытие приказа не дают.
        ClearOrders(viewer);
        viewer.Click("tech:walls");
        viewer.Click("tech:science");
        Assert.Empty(viewer.Sent.Of<ResearchOrder>());
        Assert.Empty(viewer.Log);
    }

    [Fact(DisplayName = "щелчок по глубокой теме ставит в план недостающих предшественников раньше неё (pathTo), они становятся жёлтыми")]
    public void ClickQueuesPathToTopic()
    {
        var (sim, viewer) = Scene("science", "metalFound");
        using var _ = sim;
        Open(viewer);
        // Трубам нужна переработка руды металла, а ей — научный метод и находка металла: путь из двух тем.
        Assert.Equal("available", StatusOf(viewer, "metalOre"));
        Assert.Equal("locked", StatusOf(viewer, "pipes"));
        ClearOrders(viewer);
        viewer.Click("tech:pipes");
        Assert.Equal("pipes", Assert.Single(viewer.Sent.Of<ResearchOrder>()).Topic);
        viewer.Run(0.3);
        Assert.Equal(["metalOre", "pipes"], sim.PlanOf(1));
        // В окне: обе в очереди по порядку, у плиток места 1 и 2, трубы — «станет можно».
        Assert.Equal("available", StatusOf(viewer, "metalOre"));
        Assert.Equal("soon", StatusOf(viewer, "pipes"));
        Assert.True(viewer.Ui.Has("tech:queued:metalOre"));
        Assert.True(viewer.Ui.Has("tech:queued:pipes"));
        var ids = viewer.Ui.Ids();
        Assert.True(ids.IndexOf("tech:queued:metalOre") < ids.IndexOf("tech:queued:pipes"));
        Assert.Equal("2", viewer.Ui.Text("tech:pipes")?.Split(' ').FirstOrDefault());
        Assert.Empty(viewer.Log);
    }

    [Fact(DisplayName = "отмена: щелчок по теме в очереди убирает её вместе с зависимыми, правый щелчок по плитке — тоже, «Очистить» опустошает план")]
    public void CancelDequeuesAndClears()
    {
        var (sim, viewer) = Scene("science", "metalFound");
        using var _ = sim;
        Open(viewer);
        viewer.Click("tech:pipes");
        viewer.Run(0.3);
        Assert.Equal(["metalOre", "pipes"], sim.PlanOf(1));

        // Тему из очереди убирают вместе с теми, кому она нужна: уходит последняя в цепочке.
        ClearOrders(viewer);
        viewer.Click("tech:queued:pipes");
        Assert.Equal("pipes", Assert.Single(viewer.Sent.Of<DequeueOrder>()).Topic);
        viewer.Run(0.3);
        Assert.Equal(["metalOre"], sim.PlanOf(1));
        Assert.False(viewer.Ui.Has("tech:queued:pipes"));

        // Убрать предшественника — уходит и зависимая тема.
        viewer.Click("tech:pipes");
        viewer.Run(0.3);
        Assert.Equal(["metalOre", "pipes"], sim.PlanOf(1));
        ClearOrders(viewer);
        viewer.Click("tech:metalOre", Kharos.Client.Ui.PointerButtons.Right);
        Assert.Equal("metalOre", Assert.Single(viewer.Sent.Of<DequeueOrder>()).Topic);
        viewer.Run(0.3);
        Assert.Empty(sim.PlanOf(1));

        // «Очистить» — приказ cancelResearch; пустой план — кнопки нет.
        Assert.False(viewer.Ui.Has("tech:clear"));
        viewer.Click("tech:radar");
        viewer.Click("tech:roads");
        viewer.Run(0.3);
        Assert.Equal(["radar", "roads"], sim.PlanOf(1));
        ClearOrders(viewer);
        viewer.Click("tech:clear");
        Assert.Single(viewer.Sent.Of<CancelResearchOrder>());
        viewer.Run(0.3);
        Assert.Empty(sim.PlanOf(1));
        Assert.False(viewer.Ui.Has("tech:clear"));
        Assert.Empty(viewer.Log);
    }

    [Fact(DisplayName = "шапка окна считает свои лаборатории, а ведущаяся тема плана показывает готовность в очереди")]
    public void HeaderCountsLabsAndQueueShowsHead()
    {
        var (sim, viewer) = Scene("science");
        using var _ = sim;
        // Здание ставится помощником сцен просмотра: он ищет место рядом, если в точке тесно.
        ViewerScenes.Place(sim, "lab", 0, 0, 1);
        viewer.Run(0.5);
        Open(viewer);
        Assert.Contains(viewer.Ui.Texts(), text => text.StartsWith("Лабораторий: 1", StringComparison.Ordinal));
        viewer.Click("tech:walls");
        viewer.Run(0.3);
        // Головная тема очереди подписана долей готовности.
        Assert.Contains(viewer.Ui.Texts(), text => text.StartsWith("0%", StringComparison.Ordinal));
        Assert.Empty(viewer.Log);
    }
}
