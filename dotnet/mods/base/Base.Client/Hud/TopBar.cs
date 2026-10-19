using Kharos.Mod.Sdk.Client;
using static Base.Client.Widgets;

namespace Base.Client;

/// <summary>
/// Верхняя полоса — перенос topBar из gui/hud.ts: кредиты и доход, энергия сети, запас в хранилищах, часы, кнопки
/// исследований и меню. На узком экране (уже 1000) — в два ряда. Защита новичка — К3б (Panels/Players).
/// </summary>
internal static class TopBar
{
    /// <summary>Высота полосы; на узком — два ряда (topHeight).</summary>
    public const float Height = 38, HeightNarrow = 64;

    /// <summary>Уже скольких пикселей экран узкий (NARROW).</summary>
    public const float Narrow = 1000;

    /// <summary>С какой доли выработки энергия на полосе желтеет — по полной нагрузке (GRID_TIGHT).</summary>
    private const double Tight = 0.8;

    public static float HeightOf(float width) => width < Narrow ? HeightNarrow : Height;

    /// <summary>Ключ c.Shared: настоящая высота полосы этого кадра (float) — под ней стоит окно исследований.</summary>
    public const string TopKey = "hud.top";

    /// <summary>Рисует полосу; tech — открыто ли окно исследований (щелчок по кнопке его переключает).</summary>
    public static void Draw(ClientContext c, HudState state, Tr t)
    {
        var ui = c.Ui;
        var narrow = ui.Width < Narrow;
        void Credits()
        {
            ui.Box(new Style { Id = "credits", Direction = Direction.Row, Align = Align.Center, Gap = 6 }, () =>
            {
                Coin(ui);
                ui.Text(Commands.N(state.Credits), new TextStyle { Font = FontName.Title, Color = Gold, Shadow = Ink, Lift = 2 });
                if (state.Income > 0)
                    ui.Text(t("hud.perSecond", "amount", Commands.N(state.Income)), new TextStyle { Color = Good });
            });
            ui.Tip("credits", t("common.credits"));
            // Срок защиты новичка — рядом с кредитами (topBar в gui/hud.ts); его ставит раздел Panels (PlayersPanel.Augment).
            if (state.Protection.Length > 0)
            {
                ui.Box(new Style { Id = "protection" }, () => ui.Text(t("hud.protection", "left", state.Protection), new TextStyle { Color = Good }));
                ui.Tip("protection", t("hud.protectionTip"));
            }
        }
        void Grid()
        {
            if (state.Grid is not { } grid)
                return;
            var color = grid.Demand > grid.Produced || grid.Overload ? Bad : grid.Peak > grid.Produced * Tight ? Warn : Energy;
            ui.Box(new Style { Id = "grid" }, () => ui.Text($"⚡ {Commands.N(grid.Demand)} / {Commands.N(grid.Produced)}", new TextStyle { Color = color }));
            ui.Tip("grid", $"{GridText(grid, t)}\n{t("hud.gridTip")}");
        }
        void Stock()
        {
            if (state.Stock is not { } stock)
                return;
            // На узком экране запас — одной строкой: что не влезло, обрезается.
            var style = new Style { Id = "stock", Direction = Direction.Row, Align = Align.Center, Gap = 8 };
            if (narrow)
                style = style with { Grow = 1, Clip = true, Width = 0 };
            ui.Box(style, () =>
            {
                foreach (var (resource, amount) in stock.Items)
                    Res(c, resource, amount, size: 20, id: "stock:" + resource, t: t);
                if (stock.Items.Count == 0)
                    ui.Text(t("hud.storesEmpty"));
            });
            ui.Tip("stock", t("hud.stockTip", "capacity", Commands.N(stock.Capacity)));
        }
        void Clock()
        {
            ui.Box(new Style { Id = "clock", Direction = Direction.Row, Gap = 4 }, () =>
            {
                ui.Text(state.Clock, new TextStyle { Color = Dim });
                if (state.Storm)
                    ui.Text(t("hud.storm"), new TextStyle { Color = Dim });
            });
            ui.Tip("clock", state.Storm ? t("hud.clockStorm") : t("hud.clock"));
        }
        void Buttons()
        {
            if (!state.Editor)
            {
                var open = c.Shared.TryGetValue(HudShared.Tech, out var value) && value is true;
                // Подсказка «Первых шагов»: следующий шаг — исследовать тему (pulse в gui/hud.ts).
                var hint = c.Shared.TryGetValue(StepsSection.CurrentKey, out var step) && step is StepSpec { Research: not null };
                if (ui.Button("research", Style.Button with { Padding = (2, 10), Height = 30, Outline = hint ? Bar.Pulse(ui, 90, 169, 255, 1, 0.3, 1) : null }, () =>
                {
                    ui.Text(t("tech.button"), Shadowed());
                    // Ход ведущейся темы отдаёт окно исследований (К4); до его первого кадра — своё чтение науки.
                    var progress = c.Shared.TryGetValue(ResearchSection.ProgressKey, out var shared) ? shared as double? : state.Current?.Progress;
                    if (progress is { } done)
                        ui.Text($"{Math.Round(done * 100, MidpointRounding.AwayFromZero)}%", Shadowed(Team));
                }, active: open) is not null)
                    HudShared.Set(c, HudShared.Tech, !open);
                // id не «tech»: так зовётся само окно исследований (К4) и его плитки «tech:…».
                ui.Tip("research", t("tech.openTip"));
            }
            if (ui.Button("menu", Style.Button with { Padding = (2, 10), Height = 30 }, () => ui.Text(t("game.menu"), Shadowed())) is not null)
                c.Ask(ShellAction.Menu);
            ui.Tip("menu", t("game.menuTip"));
        }

        c.Shared[TopKey] = HeightOf(ui.Width);
        var bar = new Style { Id = "top", Anchor = new Anchor(Left: 0, Right: 0, Top: 0), Height = HeightOf(ui.Width), Skin = Skin.Panel, Padding = (4, 12) };
        if (narrow)
        {
            ui.Box(bar with { Direction = Direction.Column, Justify = Align.Center, Align = Align.Stretch, Gap = 0 }, () =>
            {
                ui.Box(new Style { Direction = Direction.Row, Align = Align.Center, Gap = 10, Height = 28 }, () =>
                {
                    Credits();
                    ui.Space(0, 0, 1);
                    Buttons();
                });
                ui.Box(new Style { Direction = Direction.Row, Align = Align.Center, Gap = 10, Height = 24 }, () =>
                {
                    Grid();
                    Stock();
                    Clock();
                });
            });
            return;
        }
        ui.Box(bar with { Direction = Direction.Row, Align = Align.Center, Gap = 12 }, () =>
        {
            Credits();
            Grid();
            Stock();
            // Часы прижаты вправо, к кнопкам.
            ui.Space(0, 0, 1);
            Clock();
            Buttons();
        });
    }
}
