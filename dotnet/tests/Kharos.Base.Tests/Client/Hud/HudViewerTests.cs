using System.Globalization;
using Base.Shared.Orders;
using Kharos.Client.Game;
using Kharos.Client.Ui;

namespace Kharos.Base.Tests.Client.Hud;

/// <summary>
/// HUD base на обвязке Viewer (клиент в CLR на реплике сценария, партия frames — как test, но с панелью и выбором):
/// остаток месторождения под указателем, сетка команд со страницами и ценами, верхняя полоса, мини-карта.
/// </summary>
public sealed class HudViewerTests
{
    private static void SkipUnless() => Assert.SkipWhen(Viewer.Unavailable(Sim.DefaultMode) is not null, Viewer.Unavailable(Sim.DefaultMode) ?? "");

    private static string T(string key, params string[] args) => RequestButtonsTests.T(key, args);

    private static string N(double value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>База игрока 1 на ровной скале: главное здание, электростанция, плавильня, завод; камера — на базу.</summary>
    private static (Sim Sim, Viewer Viewer, uint Factory) Base(double credits = 5000)
    {
        var sim = Sim.Create(new SimOptions { Game = "frames", Size = 64, Fog = false, Land = new { blank = true }, Players = [1] });
        sim.SetCredits(1, credits);
        sim.Learn(1);
        ViewerScenes.Place(sim, "command", 0, 0, 1);
        ViewerScenes.Place(sim, "generator", 5, 0, 1);
        ViewerScenes.Place(sim, "smelter", 0, 5, 1);
        var factory = ViewerScenes.Place(sim, "factory", 6, 5, 1);
        var viewer = sim.Viewer(1, new ViewerOptions { Width = 1280, Height = 720 });
        viewer.Camera = new CameraState(5, 6, 32);
        viewer.Step(5);
        return (sim, viewer, factory);
    }

    /// <summary>Тексты ячейки сетки через пробел; ячейки нет — null.</summary>
    private static string? Label(Viewer viewer, int cell) => viewer.Ui.Text("cell:" + cell);

    [Fact(DisplayName = "остаток месторождения в тумане неизвестен, без тумана — настоящий")]
    public void KnownReserve()
    {
        SkipUnless();
        string? Tip(bool fog, out double reserve)
        {
            using var sim = Sim.Create(new SimOptions { Game = "frames", Size = 64, Land = "default", Fog = fog, Players = [1] });
            var spot = sim.Deposits().OrderBy(spot => Math.Abs(spot.X) + Math.Abs(spot.Y)).First();
            reserve = sim.ReserveLeft(spot.X, spot.Y);
            var viewer = sim.Viewer(1, new ViewerOptions { Width = 1280, Height = 720 });
            viewer.Camera = new CameraState(spot.X + 1, spot.Y + 1, 32);
            viewer.Step(3);
            viewer.Input = viewer.Input with { Pointer = viewer.Last.View.ToScreen(spot.X + 0.5f, spot.Y + 0.5f) };
            viewer.Step(2);
            Assert.Empty(viewer.Log);
            return viewer.Ui.Text("hover");
        }
        // В тумане игрок месторождения не видел ни разу: остатка не знает (не знает о нём вовсе — подсказки нет).
        var fogged = Tip(true, out _);
        Assert.True(fogged is null || fogged.Contains(T("hud.leftUnknown"), StringComparison.Ordinal), fogged);
        // Без тумана — настоящий остаток.
        var clear = Tip(false, out var left);
        Assert.NotNull(clear);
        Assert.Contains(T("hud.left", "amount", N(Math.Floor(left))), clear, StringComparison.Ordinal);
    }

    [Fact]
    public void BuilderGridHasSectionsPagesAndCosts()
    {
        SkipUnless();
        var (sim, viewer, factory) = Base();
        using var _ = sim;
        var builder = sim.PutUnit("builder", 1, 3.5, 9.5);
        viewer.Step(2);
        viewer.ClickWorld(3.5, 9.5);
        viewer.Step();
        Assert.Equal(T("units.builder"), viewer.Ui.Text("info:title"));
        // Корень строителя: разделы по своим местам, с числом открытых зданий; разбор — в ячейке 10.
        Assert.Contains(T("grid.sections.economy"), Label(viewer, 0));
        Assert.Contains("▸", Label(viewer, 0));
        Assert.Contains(T("grid.remove"), Label(viewer, 10));
        // Раздел «Хозяйство»: здания с ценой из данных и «Назад» в углу.
        viewer.Click("cell:0");
        viewer.Step();
        var tables = RequestButtonsTests.Tables.Value;
        var shown = 0;
        for (var cell = 0; cell < 11; cell++)
        {
            if (Label(viewer, cell) is not { } text)
                continue;
            var type = tables.Buildings.First(pair => text.Contains(T("buildings." + pair.Key), StringComparison.Ordinal) && pair.Value.Menu is not null).Key;
            Assert.Contains(N(tables.Buildings[type].Cost), text);
            shown++;
        }
        Assert.True(shown > 0, "в разделе нет зданий");
        Assert.Contains(T("common.back"), Label(viewer, 11));
        // Кнопка здания включает выбор места (режим управления), «Назад» — к разделам.
        viewer.Click("cell:0");
        viewer.Step();
        Assert.Contains(viewer.Ui.Buttons(), id => id == "cell:0");
        viewer.Click("cell:11");
        viewer.Step();
        Assert.Contains(T("grid.sections.economy"), Label(viewer, 0));
        Assert.True(sim.Alive(builder));
        Assert.Empty(viewer.Log);
    }

    [Fact]
    public void FactoryGridProducesWithCostsAndHotkey()
    {
        SkipUnless();
        var (sim, viewer, factory) = Base();
        using var _ = sim;
        var (x, y) = sim.At(factory);
        var (w, h) = ViewerScenes.Footprint(sim, "factory");
        viewer.ClickWorld(x + w / 2.0, y + h / 2.0);
        viewer.Step();
        Assert.Equal(T("buildings.factory"), viewer.Ui.Text("info:title"));
        var tables = RequestButtonsTests.Tables.Value;
        var produces = tables.Buildings["factory"].Produces;
        Assert.NotEmpty(produces);
        for (var i = 0; i < Math.Min(8, produces.Length); i++)
        {
            var text = Label(viewer, i);
            Assert.NotNull(text);
            Assert.Contains(T("units." + produces[i]), text);
            Assert.Contains(N(tables.Units[produces[i]].Cost), text);
        }
        // Клавиша T — первая ячейка: заказ уходит хосту.
        viewer.Sent.Clear();
        viewer.Frame(viewer.Input with { Hits = ["t"], Keys = ["t"] });
        viewer.Step();
        var order = Assert.Single(viewer.Sent.Of<ProduceOrder>());
        Assert.Equal((factory, produces[0]), (order.Producer, order.Unit));
        Assert.Empty(viewer.Log);
    }

    [Fact]
    public void TopBarShowsCreditsIncomeAndPower()
    {
        SkipUnless();
        var (sim, viewer, _) = Base(credits: 300);
        using var _ = sim;
        viewer.Run(1);
        var top = viewer.Ui.Text("top")!;
        Assert.Contains(N(Math.Floor(sim.Credits(1))), viewer.Ui.Text("credits"));
        var economy = sim.Economy(1);
        // Подмога главного здания: кредитов меньше 600 — доход виден на полосе.
        Assert.True(economy.Income > 0, $"доход {economy.Income}");
        Assert.Contains(T("hud.perSecond", "amount", N(Math.Round(economy.Income * 100, MidpointRounding.AwayFromZero) / 100)), top);
        Assert.Equal($"⚡ {N(Math.Ceiling(economy.Demand * 10) / 10)} / {N(Math.Floor(economy.Produced * 10) / 10)}", viewer.Ui.Text("grid"));
        Assert.Contains(T("game.menu"), top);
        Assert.Contains(T("tech.button"), top);
        // Кнопка «Исследования» открывает окно (ключ окна К4).
        Assert.False(viewer.Ui.Has("tech"));
        viewer.Click("research");
        viewer.Step();
        Assert.Contains(viewer.Ui.Buttons(), id => id == "research");
        Assert.True(viewer.Ui.Has("tech"), "окно исследований не открылось");
        Assert.Empty(viewer.Log);
    }

    [Fact]
    public void MinimapClickMovesCameraAndOrders()
    {
        SkipUnless();
        var (sim, viewer, factory) = Base();
        using var _ = sim;
        var buggy = sim.PutUnit("buggy", 1, 3.5, 9.5);
        viewer.Step(2);
        viewer.ClickWorld(3.5, 9.5);
        viewer.Step();
        // Камера теперь своя у мода: щелчок по середине мини-карты ставит её в середину карты.
        viewer.Camera = null;
        viewer.Click("minimap");
        viewer.Step();
        var view = viewer.Last.View;
        Assert.True(Math.Abs(view.X) < 2 && Math.Abs(view.Y) < 2, $"камера ({view.X}, {view.Y})");
        // Правый щелчок — выделенные едут туда; Alt — сигнал союзникам.
        viewer.Sent.Clear();
        viewer.Click("minimap", PointerButtons.Right);
        var move = Assert.Single(viewer.Sent.Of<MoveOrder>());
        Assert.Equal([buggy], move.Units);
        Assert.True(Math.Abs(move.X) <= 2 && Math.Abs(move.Y) <= 2, $"({move.X}, {move.Y})");
        viewer.Input = viewer.Input with { Alt = true };
        viewer.Click("minimap");
        viewer.Input = viewer.Input with { Alt = false };
        Assert.Single(viewer.Sent.Of<PingOrder>());
        Assert.Empty(viewer.Log);
    }

    [Fact(DisplayName = "панель групп: пустая ячейка кладёт выделенных, занятая показывает число, правая кнопка по выбранной очищает")]
    public void GroupBar()
    {
        SkipUnless();
        var (sim, viewer, _) = Base();
        using (sim)
        using (viewer)
        {
            Assert.True(viewer.Ui.Has("groups"));
            Assert.Equal("3", viewer.Ui.Text("group:3"));
            viewer.ClickWorld(1.5, 1.5);
            viewer.Click("group:3");
            viewer.Step();
            Assert.Equal("3 1", viewer.Ui.Text("group:3"));
            viewer.Click("group:3", PointerButtons.Right);
            viewer.Step();
            Assert.Equal("3", viewer.Ui.Text("group:3"));
            Assert.Empty(viewer.Log);
        }
    }

    [Fact(DisplayName = "чат оболочки — правее мини-карты над нижней панелью, на узком экране — у левого края")]
    public void ChatPlace()
    {
        SkipUnless();
        var (sim, viewer, _) = Base();
        using (sim)
        using (viewer)
        {
            var wide = viewer.ChatAt!.Split(' ').Select(part => double.Parse(part, CultureInfo.InvariantCulture)).ToArray();
            Assert.True(wide[0] > 140, viewer.ChatAt);
            Assert.True(wide[1] > 100, viewer.ChatAt);
            viewer.Input = viewer.Input with { Width = 640, Height = 400 };
            viewer.Step();
            Assert.StartsWith("12 ", viewer.ChatAt);
        }
    }
}
