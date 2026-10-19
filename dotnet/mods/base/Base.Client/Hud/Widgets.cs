using System.Globalization;
using Base.Shared.Data;
using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>Параметр вида для карточки: ключ, подпись, значение.</summary>
internal readonly record struct StatRow(string Key, string Label, string Value);

/// <summary>
/// Общие кусочки панелей — перенос gui/widgets.ts: цвета текста, тень подписей, монета, значок груза с числом, полоса
/// готовности, подпись клавиши, энергия сети словами.
/// </summary>
internal static class Widgets
{
    // Цвета текста — те же, что у страницы (style.css).
    public static readonly Color Ink = Color.Hex(0x0b111b);
    public static readonly Color Bright = Color.Hex(0xeef6ff);
    public static readonly Color Text = Color.Hex(0xd6e6f5);
    public static readonly Color Soft = Color.Hex(0xb4c9dd);
    public static readonly Color Dim = Color.Hex(0x8fa9c2);
    public static readonly Color Muted = Color.Hex(0x6184a3);
    public static readonly Color Team = Color.Hex(0x5aa9ff);
    public static readonly Color TeamMid = Color.Hex(0x1f63d8);
    public static readonly Color Gold = Color.Hex(0xf0c95a);
    public static readonly Color Good = Color.Hex(0x8fe0a8);
    public static readonly Color Warn = Color.Hex(0xffb070);
    public static readonly Color Bad = Color.Hex(0xff7a66);
    public static readonly Color Energy = Color.Hex(0x8fd0ff);
    public static readonly Color Ally = Color.Hex(0x4ec973);

    /// <summary>Значок груза в строке: 15 × 15 (GOOD).</summary>
    public const float GoodSize = 15;

    /// <summary>Тень подписей кнопок и ячеек: пиксель чернил правее и ниже.</summary>
    public static TextStyle Shadowed(Color? color = null, bool ellipsis = false) =>
        new() { Color = color ?? TextStyle.DefaultColor, Shadow = Ink, Lift = 1, Ellipsis = ellipsis };

    /// <summary>Монета кредитов.</summary>
    public static void Coin(Ui ui, float size = 16) => ui.Coin(size);

    /// <summary>
    /// Значок груза и количество (или «есть/из»); название — в подсказке, если дан id (res в widgets.ts).
    /// </summary>
    public static void Res(ClientContext c, string resource, double? amount = null, double? of = null, string? id = null, float size = GoodSize, Color? color = null, Tr? t = null)
    {
        var ui = c.Ui;
        ui.Box(new Style { Id = id, Direction = Direction.Row, Align = Align.Center, Gap = 3 }, () =>
        {
            if (c.LookId("goods/" + resource) is var look and >= 0)
                ui.Image(look, size, size);
            if (amount is { } value)
                ui.Text(of is { } total ? $"{Commands.N(value)}/{Commands.N(total)}" : Commands.N(value), new TextStyle { Color = color ?? TextStyle.DefaultColor });
        });
        if (id is not null && t is not null)
            ui.Tip(id, t("goods." + resource));
    }

    /// <summary>
    /// Груз в строке с переносом (resRun): значок внутри текста и число. Текст и значки подряд движок собирает в одну
    /// строку со значками (Icon, ABI 2).
    /// </summary>
    public static void ResRun(ClientContext c, string resource, double? amount = null, double? of = null, TextStyle? style = null)
    {
        var look = c.LookId("goods/" + resource);
        if (look >= 0)
            c.Ui.Icon(look, 0, GoodSize);
        c.Ui.Text(amount is { } value ? of is { } total ? $"{Commands.N(value)}/{Commands.N(total)} " : $"{Commands.N(value)} " : " ", style ?? new TextStyle());
    }

    /// <summary>Полоса готовности с подписью: утопленное окно, внутри — полоса огней команды на долю value, поверх — подпись.</summary>
    public static void Bar(Ui ui, double value, string label, float? width = null)
    {
        var fill = (float)Math.Clamp(value, 0, 1);
        ui.Box(new Style { Skin = Skin.Well, Width = width, Height = 24, Padding = 4, Direction = Direction.Stack, Clip = true }, () =>
        {
            ui.Box(new Style { Grow = 1, Direction = Direction.Row }, () =>
            {
                ui.Box(new Style { Grow = fill, Background = TeamMid });
                ui.Box(new Style { Grow = 1 - fill });
            });
            ui.Box(new Style { Self = (Align.Start, Align.Center), Padding = (0, 8) }, () => ui.Text(label, Shadowed(Text, ellipsis: true)));
        });
    }

    /// <summary>Подпись клавиши — золотая.</summary>
    public static void Key(Ui ui, string label, Color? color = null) => ui.Text(label, Shadowed(color ?? Gold));

    /// <summary>Энергия сети словами (gridText): спрос и выработка, из накопителей, при полной нагрузке, перегруз.</summary>
    public static string GridText(GridState grid, Tr t) =>
        t("hud.zonePower", "demand", Commands.N(grid.Demand), "produced", Commands.N(grid.Produced))
        + (grid.Stored > 0 ? t("hud.zoneStored", "stored", Commands.N(grid.Stored)) : "")
        + (grid.Peak > grid.Demand ? t("hud.zonePeak", "peak", Commands.N(grid.Peak)) : "")
        + (grid.Overload ? t("hud.overloaded") : "");
}

/// <summary>
/// Параметры вида юнита для карточек и панели — перенос ui/unitStats.ts: прочность, класс, скорость, обзор, груз,
/// ремонт и по блоку строк на каждое оружие (своё и на турелях; одинаковые — одним блоком со счётчиком).
/// </summary>
internal static class Stats
{
    /// <summary>До одного знака после запятой, без хвостового нуля (tidy).</summary>
    private static string Tidy(double value) => Commands.N(Math.Round(value * 10, MidpointRounding.AwayFromZero) / 10);

    /// <summary>Обзор вида (unitSight): своё число, иначе по классу и дальности оружия — как считает хост.</summary>
    public static double Sight(BaseTables tables, Base.Shared.Data.UnitSpec spec)
    {
        var range = 0.0;
        if (spec.Weapon is { } weapon && tables.Weapons.GetValueOrDefault(weapon) is { } w)
            range = Math.Max(range, w.Range);
        foreach (var mount in spec.Mounts)
            if (tables.Turrets.GetValueOrDefault(mount.Turret)?.Weapon is { } turretWeapon && tables.Weapons.GetValueOrDefault(turretWeapon) is { } tw)
                range = Math.Max(range, tw.Range);
        // CLASS_SIGHT и SIGHT_OVER_RANGE из vision.ts.
        var own = spec.Sight ?? spec.Kind switch { "infantry" => 7, "air" => 10, _ => 8 };
        return Math.Max(own, range > 0 ? range + 1.5 : 0);
    }

    public static List<StatRow> Unit(BaseTables tables, string type, Tr t, double? sight = null)
    {
        var rows = new List<StatRow>();
        if (tables.Units.GetValueOrDefault(type) is not { } spec)
            return rows;
        void Row(string key, string value) => rows.Add(new StatRow(key, t("stats." + key), value));
        Row("hp", Commands.N(spec.Hp));
        Row("class", t("stats.vs." + spec.Kind));
        Row("speed", Tidy(spec.Speed));
        Row("sight", Tidy(sight ?? Sight(tables, spec)));
        if (spec.Inventory is { } inventory and not 0)
            Row("cargo", Commands.N(inventory));
        var weapons = new List<(string Name, int Count)>();
        void Arm(string? weapon)
        {
            if (weapon is null)
                return;
            var at = weapons.FindIndex(pair => pair.Name == weapon);
            if (at < 0)
                weapons.Add((weapon, 1));
            else
                weapons[at] = (weapon, weapons[at].Count + 1);
        }
        Arm(spec.Weapon);
        var repair = spec.Repair;
        foreach (var mount in spec.Mounts)
        {
            var turret = tables.Turrets.GetValueOrDefault(mount.Turret);
            Arm(turret?.Weapon);
            repair ??= turret?.Repair;
        }
        if (repair is { } radius and not 0)
            Row("repair", Tidy(radius));
        foreach (var (name, count) in weapons)
        {
            if (tables.Weapons.GetValueOrDefault(name) is not { } weapon)
                continue;
            Row("damage", count > 1 ? $"{Commands.N(weapon.Damage)} ×{count}" : Commands.N(weapon.Damage));
            Row("reload", t("stats.seconds", "n", Tidy(weapon.Reload)));
            Row("dps", Tidy(weapon.Damage * count / weapon.Reload));
            Row("range", weapon.MinRange is { } min and not 0 ? $"{Tidy(min)}–{Tidy(weapon.Range)}" : Tidy(weapon.Range));
            if (weapon.Splash is { } splash and not 0)
                Row("splash", Tidy(splash));
            if (weapon.Chain is { } chain and not 0)
                Row("chain", Commands.N(chain));
            var targets = t(weapon.OnlyAir ? "stats.air" : weapon.Air ? "stats.both" : "stats.ground");
            Row("targets", weapon.Lob ? $"{targets}, {t("stats.overWalls")}" : targets);
            string List(IEnumerable<KeyValuePair<string, double>> items) =>
                string.Join(", ", items.Select(pair => $"{t("stats.vs." + pair.Key)} ×{Commands.N(pair.Value)}"));
            var strong = weapon.Vs.Where(pair => pair.Value > 1).OrderByDescending(pair => pair.Value).ToList();
            var weak = weapon.Vs.Where(pair => pair.Value < 1).OrderBy(pair => pair.Value).ToList();
            if (strong.Count > 0)
                Row("strong", List(strong));
            if (weak.Count > 0)
                Row("weak", List(weak));
        }
        return rows;
    }

    /// <summary>Параметры одним текстом, строка на параметр: для всплывающей подсказки (unitStatsText).</summary>
    public static string Text(BaseTables tables, string type, Tr t, double? sight = null) =>
        string.Join('\n', Unit(tables, type, t, sight).Select(row => row.Label + ": " + row.Value));

    /// <summary>Главное одной строкой — для панели выбранного (unitStatsLine).</summary>
    public static string Line(BaseTables tables, string type, Tr t, double? sight = null)
    {
        string[] brief = ["hp", "speed", "sight", "cargo", "damage", "range"];
        return string.Join(" · ", Unit(tables, type, t, sight).Where(row => Array.IndexOf(brief, row.Key) >= 0).Select(row => row.Label + " " + row.Value));
    }

    /// <summary>Число для подписи (инвариантная культура).</summary>
    public static string Format(double value) => value.ToString(CultureInfo.InvariantCulture);
}
