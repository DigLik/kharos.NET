using Base.Shared;
using Base.Shared.Data;
using Base.Shared.Rules;
using Kharos.Mod.Sdk;

namespace Base.Client;

/// <summary>
/// Энергия на реплике клиента (income.ts: powerStates, loadedPlants) — как её посчитал хост по сетям зон: компонент Grid
/// у здания сети (<see cref="GridWire"/>; биты состояния видит каждый, как в TS). Перегруз — у станций, нехватка — у
/// включённых потребителей; станции под нагрузкой — их анимация «в работе». Пересчёт раз в тик (и раз в полсекунды,
/// пока партия стоит).
/// </summary>
internal sealed class Economy(Fx fx, Paint paint)
{
    public const int Overload = 1, Starved = 2;

    private double _tick = double.NaN, _time = double.NegativeInfinity;
    private ComponentRef? _grid;
    private bool _bound;

    /// <summary>Здания с бедой энергии: <see cref="Overload"/> или <see cref="Starved"/>.</summary>
    public Dictionary<uint, int> States { get; } = [];

    /// <summary>Станции под нагрузкой: их анимация — «в работе».</summary>
    public HashSet<uint> Loaded { get; } = [];

    public void Update()
    {
        var c = fx.C;
        if (_tick == c.Tick && c.Time - _time < 0.5)
            return;
        _tick = c.Tick;
        _time = c.Time;
        States.Clear();
        Loaded.Clear();
        if (!_bound)
        {
            _bound = true;
            try
            {
                _grid = c.Component(Names.Grid.Name);
            }
            catch (Exception)
            {
                _grid = null;
            }
        }
        if (_grid is not { } grid)
            return;
        var field = grid[Names.Grid.State];
        foreach (var entity in c.Each(grid, fx.Building))
        {
            if (c.Has(entity, fx.Site) || c.Get(entity, grid) is not { } values)
                continue;
            var state = (int)values.Number(field);
            if ((state & GridWire.Overload) != 0)
                States[entity] = Overload;
            else if ((state & GridWire.Starved) != 0 && !paint.SwitchedOff(entity))
                States[entity] = Starved;
            if ((state & GridWire.Loaded) != 0)
                Loaded.Add(entity);
        }
    }
}

/// <summary>
/// Тревоги над зданиями и юнитами (powerPass.ts, statusBars.ts) и полоски над своими стройками (selectionPass.ts):
/// молнии перегруза на станциях; значки над зданиями — «Не подъехать», нехватка энергии, недостающий груз или кредиты
/// (<see cref="Lacks"/>; на исходе — бледный), «Выключено»; полосы прочности и щита над крышей; значок над запертым
/// грузовиком и над своим юнитом с пустым магазином (на исходе — бледный); полоски прочности юнитов; ход стройки и
/// рамка ещё не начатой площадки. Тревожные значки мигают, место держат и пока погашены.
/// </summary>
internal sealed class Power(Fx fx, Economy economy, Lacks lacks, Paint paint)
{
    /// <summary>Молния: сколько раз в секунду меняется, какую долю кадров видна, сколько картинок, насколько выше основания.</summary>
    private const double BoltRate = 14, BoltShown = 0.6, BoltRise = 0.5;
    private const int Bolts = 6;

    /// <summary>Значок: сторона в тайлах, мигание (ICON_TILES, ICON_PERIOD, ICON_SHOWN).</summary>
    private const double IconTiles = 0.75, IconPeriod = 1, IconShown = 0.7, IconNative = 1;

    /// <summary>Значок запертого грузовика выше середины, значок над юнитом выше края, бледный — на исходе.</summary>
    private const double TruckIconRise = 0.9, UnitIconGap = 0.45;
    private const float LowAlpha = 0.5f;

    /// <summary>Полосы состояния здания (STATUS_BAR, STATUS_STACK в пикселях, STATUS_GAP в тайлах).</summary>
    private const double StatusBar = 3, StatusStack = 1, StatusGap = 0.1;

    /// <summary>Полоска прочности юнита: высота в пикселях и отступ в тайлах (combatPass.ts).</summary>
    private const double UnitBar = 3, UnitBarGap = 0.25;

    /// <summary>Полоска хода стройки: высота в пикселях и зазор в тайлах (selectionPass.ts).</summary>
    private const double SiteBar = 4, SiteBarGap = 0.3;

    private static readonly Rgb BarBack = new(0.03f, 0.05f, 0.08f), BarGood = new(0.45f, 0.9f, 0.55f), BarBad = new(1, 0.35f, 0.25f);
    private static readonly Rgb BarShield = new(0.3f, 0.55f, 1), BarBuilding = new(0.45f, 0.9f, 0.55f), Planned = new(0.3f, 0.6f, 1);

    /// <summary>Магазины юнитов (supply.ts): секунд огня всеми стволами в полном магазине, у пехоты — дольше.</summary>
    private const double MagazineSeconds = 50, InfantryMagazineSeconds = 120;

    private readonly Dictionary<string, double> _cheapest = new(StringComparer.Ordinal);
    private readonly List<(int Look, int Frame, bool Shown, float Alpha)> _icons = [];

    public void Update()
    {
        var c = fx.C;
        var step = Math.Floor(fx.Time * BoltRate);
        var iconOn = fx.Time % IconPeriod / IconPeriod < IconShown;
        var (icon, off, lack, broke, blocked, bolt) = (fx.Look("power/icon"), fx.Look("power/off"), fx.Look("power/lack"), fx.Look("power/broke"),
            fx.Look("power/blocked"), fx.Look("power/bolt"));
        var barHeight = StatusBar * fx.Pixel;
        var stack = StatusStack * fx.Pixel;
        foreach (var entity in c.Each(fx.Building, fx.Position))
        {
            if (fx.BuildingOf(entity) is not { } kind || c.Pose(entity) is not { } at)
                continue;
            var spec = kind.Spec;
            var (x, y) = (at.X, at.Y);
            if (!fx.Visible(x, y, spec.Width, spec.Height, 2))
                continue;
            var state = economy.States.GetValueOrDefault(entity);
            if (state == Economy.Overload && Fx.Noise(step, entity) < BoltShown)
            {
                var frame = (int)Math.Floor(Fx.Noise(entity, step) * Bolts);
                // Молния растягивается на ширину здания и поднимается над крышей.
                fx.Sprite(bolt, frame, x, y - BoltRise, spec.Width, 2, 1, Fx.Top);
            }
            var (health, shield) = Bars(entity, spec);
            var rows = (health is null ? 0 : 1) + (shield is null ? 0 : 1);
            var lifted = y - (rows > 0 ? StatusGap + rows * (StatusBar + StatusStack) * fx.Pixel : 0);
            _icons.Clear();
            if (fx.Mine(entity) && c.Get(entity, fx.Unreachable) is { } unreachable && unreachable.Number(fx.Unreachable["until"]) > c.Tick)
                _icons.Add((blocked, 0, iconOn, 1));
            if (state == Economy.Starved && !paint.SwitchedOff(entity))
                _icons.Add((icon, 0, iconOn, 1));
            var hasLack = lacks.Of.TryGetValue(entity, out var good);
            if (hasLack)
                _icons.Add(good == Lacks.Credits ? (broke, 0, iconOn, 1) : (lack, good, iconOn, 1));
            if (!hasLack && lacks.Low.TryGetValue(entity, out var low))
                _icons.Add((lack, low, true, LowAlpha));
            if (!c.Has(entity, fx.Site) && fx.Mine(entity) && paint.SwitchedOff(entity))
                _icons.Add((off, 0, true, 1));
            for (var i = 0; i < _icons.Count; i++)
            {
                var (look, frame, shown, alpha) = _icons[i];
                if (shown)
                    fx.Sprite(look, frame, x + spec.Width / 2 + (i - _icons.Count / 2.0) * IconTiles, lifted - IconTiles / 2, IconTiles, IconNative, alpha, Fx.Top);
            }
            // Полосы над крышей, сверху вниз: прочность, под ней запас щита; нижняя — вплотную к крыше.
            var row = 0;
            foreach (var (share, color) in new[] { (health, health > 0.5 ? BarGood : BarBad), (shield, BarShield) })
            {
                if (share is not { } value)
                    continue;
                var top = y - StatusGap - (rows - row) * barHeight - (rows - 1 - row) * stack;
                fx.Box(x, top, spec.Width, barHeight, BarBack);
                fx.Box(x, top, spec.Width * value, barHeight, color);
                row++;
            }
        }
        Trucks(iconOn, blocked);
        Magazines(iconOn, lack);
        Sites(iconOn, lack, broke);
        Units();
    }

    /// <summary>
    /// Полосы состояния здания (statusBarsOf): прочность — у повреждённого, кроме стройки (у разбора без отмены — есть);
    /// запас щита — у готового генератора щита.
    /// </summary>
    private (double? Health, double? Shield) Bars(uint entity, BuildingSpec spec)
    {
        var c = fx.C;
        double? health = null, shield = null;
        var site = c.Get(entity, fx.Site);
        if (c.Get(entity, fx.Health) is { } record && record.Number(fx.Health["value"]) < record.Number(fx.Health["max"])
            && !(site is not null && (!site.Flag(fx.Site["demolish"]) || site.Flag(fx.Site["undo"]))))
            health = Math.Max(0, record.Number(fx.Health["value"]) / record.Number(fx.Health["max"]));
        if (site is null && spec.Shield is { Capacity: > 0 } dome && c.Get(entity, fx.Shield) is { } charge)
            shield = Math.Clamp(charge.Number(fx.Shield["charge"]) / dome.Capacity, 0, 1);
        return (health, shield);
    }

    /// <summary>Запертые свои грузовики: им не выехать, пока не разберут то, чем их обстроили.</summary>
    private void Trucks(bool iconOn, int blocked)
    {
        if (!iconOn)
            return;
        var c = fx.C;
        foreach (var entity in c.Each(fx.Hauler, fx.Position))
        {
            if (!fx.Mine(entity) || c.Get(entity, fx.Hauler) is not { } hauler || !hauler.Flag(fx.Hauler["trapped"]) || c.Pose(entity) is not { } at || !fx.Visible(at.X, at.Y, 2))
                continue;
            fx.Sprite(blocked, 0, at.X - IconTiles / 2, at.Y - TruckIconRise - IconTiles / 2, IconTiles, IconNative, 1, Fx.Top);
        }
    }

    /// <summary>Свои юниты с пустым магазином — значок боеприпасов, на исходе — он же бледный.</summary>
    private void Magazines(bool iconOn, int lack)
    {
        if (!fx.UnitAmmo)
            return;
        var c = fx.C;
        var ammo = Array.IndexOf(Icons.Goods, "ammo");
        // Хозяин, вид и патроны — столбцами разом: тысяча юнитов — один вызов инструмента, а не три тысячи.
        var entities = c.Each(fx.Magazine, fx.Unit, fx.Position);
        var values = Columns(entities, [(fx.Owner, fx.Owner["player"]), (fx.Proto, fx.Proto["id"]), (fx.Magazine, fx.Magazine["ammo"])]);
        var n = entities.Length;
        for (var i = 0; i < n; i++)
        {
            var entity = entities[i];
            if ((uint)values[i] != c.Player || fx.NameOf((int)values[n + i]) is not { } type || !fx.Tables.Units.TryGetValue(type, out var spec)
                || spec.Ammo is not { } capacity || capacity <= 0)
                continue;
            var unit = (Type: type, Spec: spec);
            var rounds = values[2 * n + i];
            var empty = rounds < Cheapest(unit.Type, unit.Spec) - 1e-9;
            if (empty ? !iconOn : rounds >= capacity * Lacks.AmmoReorder)
                continue;
            if (c.Pose(entity) is not { } at || !fx.Visible(at.X, at.Y, 2))
                continue;
            var y = at.Y - fx.Radius(entity) - UnitIconGap;
            fx.Sprite(lack, ammo, at.X - IconTiles / 2, y - IconTiles / 2, IconTiles, IconNative, empty ? 1 : LowAlpha, Fx.Top);
        }
    }

    private double[] _columns = [];

    /// <summary>Поля пар у сущностей одним вызовом (Tools.Columns): pairs.Length столбцов по entities.Length подряд.</summary>
    private double[] Columns(uint[] entities, ReadOnlySpan<(ComponentRef Component, FieldRef Field)> pairs)
    {
        var count = entities.Length * pairs.Length;
        if (_columns.Length < count)
            _columns = new double[Math.Max(count, _columns.Length * 2)];
        Tools.Columns(entities, pairs, _columns);
        return _columns;
    }

    /// <summary>Самый дешёвый выстрел юнита (cheapestShot): магазин делится на секунды огня всех стволов.</summary>
    private double Cheapest(string type, Base.Shared.Data.UnitSpec spec)
    {
        if (_cheapest.TryGetValue(type, out var cost))
            return cost;
        var reloads = new List<double>();
        if (spec.Weapon is { } own && fx.Tables.Weapons.TryGetValue(own, out var weapon))
            reloads.Add(weapon.Reload);
        foreach (var mount in spec.Mounts)
            if (fx.Tables.Turrets.TryGetValue(mount.Turret, out var turret) && turret.Weapon is { } name && fx.Tables.Weapons.TryGetValue(name, out var gun))
                reloads.Add(gun.Reload);
        var seconds = spec.Kind == "infantry" ? InfantryMagazineSeconds : MagazineSeconds;
        var capacity = spec.Ammo ?? 0;
        cost = reloads.Count > 0 ? reloads.Min(reload => capacity * reload / (seconds * reloads.Count)) : 0;
        return _cheapest[type] = cost;
    }

    /// <summary>
    /// Свои стройки: площадка без здания, которая уже ждёт груза или кредитов, — значок над ней; начатая — полоска хода
    /// над полосами состояния, не начатая — тонкая рамка (ночью чертёж на земле почти не виден).
    /// </summary>
    private void Sites(bool iconOn, int lack, int broke)
    {
        var c = fx.C;
        foreach (var entity in c.Each(fx.Site, fx.Position))
        {
            if (!fx.Mine(entity) || fx.BuildingOf(entity) is not { } kind || c.Get(entity, fx.Site) is not { } site || c.Pose(entity) is not { } at)
                continue;
            var spec = kind.Spec;
            if (!fx.Visible(at.X, at.Y, spec.Width, spec.Height, 2))
                continue;
            if (iconOn && !c.Has(entity, fx.Building) && lacks.Of.TryGetValue(entity, out var good))
                fx.Sprite(good == Lacks.Credits ? broke : lack, Math.Max(0, good), at.X + spec.Width / 2 - IconTiles / 2, at.Y - IconTiles / 2, IconTiles, IconNative, 1, Fx.Top);
            var progress = site.Number(fx.Site["progress"]);
            var pixel = fx.Pixel;
            if (progress > 0)
            {
                var (health, shield) = Bars(entity, spec);
                var rows = (health is null ? 0 : 1) + (shield is null ? 0 : 1);
                var lift = rows > 0 ? StatusGap + rows * (StatusBar + StatusStack) * pixel : 0;
                var height = SiteBar * pixel;
                var top = at.Y - lift - SiteBarGap - height;
                fx.Box(at.X - pixel, top - pixel, spec.Width + pixel * 2, height + pixel * 2, BarBack);
                fx.Box(at.X, top, spec.Width * Math.Clamp(progress / fx.SiteTicks(spec), 0, 1), height, BarBuilding);
                continue;
            }
            fx.Box(at.X, at.Y, spec.Width, pixel, Planned);
            fx.Box(at.X, at.Y + spec.Height - pixel, spec.Width, pixel, Planned);
            fx.Box(at.X, at.Y, pixel, spec.Height, Planned);
            fx.Box(at.X + spec.Width - pixel, at.Y, pixel, spec.Height, Planned);
        }
    }

    /// <summary>Полоски прочности над повреждёнными юнитами — своими и чужими (combatPass.ts).</summary>
    private void Units()
    {
        var c = fx.C;
        var pixel = fx.Pixel;
        // Прочность — столбцами разом; место, радиус и вид — только у повреждённых.
        var entities = c.Each(fx.Health, fx.Unit, fx.Position);
        var values = Columns(entities, [(fx.Health, fx.Health["value"]), (fx.Health, fx.Health["max"])]);
        var n = entities.Length;
        for (var i = 0; i < n; i++)
        {
            var entity = entities[i];
            var max = values[n + i];
            if (max <= 0)
                continue;
            var health = values[i] / max;
            if (health >= 1 || c.Pose(entity) is not { } at || !fx.Visible(at.X, at.Y))
                continue;
            var radius = fx.Radius(entity);
            var half = radius + 0.1;
            var flies = fx.UnitOf(entity)?.Spec.Kind == "air";
            var top = at.Y - radius - UnitBarGap - (flies ? 0.1 : 0);
            fx.Box(at.X - half - pixel, top - (UnitBar + 2) * pixel / 2, (half + pixel) * 2, (UnitBar + 2) * pixel, BarBack);
            fx.Box(at.X - half, top - UnitBar * pixel / 2, half * 2 * Math.Max(0, health), UnitBar * pixel, health > 0.5 ? BarGood : BarBad);
        }
    }
}
