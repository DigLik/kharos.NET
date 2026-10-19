using Base.Shared.Data;
using Kharos.Mod.Sdk;

namespace Base.Client;

/// <summary>
/// Уточнение спрайтов зданий записью Paint (buildingsPass.ts): кадр — (шаг 8 к/с + своя фаза Building.phase) по кругу из
/// 16, у работающего — из второго набора «в работе»; вариант — соседство стен, ворот и труб, открытые ворота, створки
/// шахты, руда под шахтой, ракеты перехватчика; стройка — «растёт снизу» на долю сделанного, площадка без здания —
/// чертёж; сворачивающееся главное здание уходит сверху вниз; своё выключенное — темнее, без огней, анимация стоит.
/// </summary>
internal sealed class Paint(Fx fx)
{
    private const int ArtFrames = BuildingKit.Frames;
    private const double FramesPerSecond = 8;

    /// <summary>Сколько секунд здание ещё работает после того, как работа сдвинулась последний раз (WORK_HOLD).</summary>
    private const double WorkHold = 0.6;

    /// <summary>Запас вокруг экрана, тайлы (VISIBLE_MARGIN): здание за краем светит в кадр.</summary>
    private const double Margin = 5;

    /// <summary>Стыковка стен и труб (WALL_CONNECTION), открытые ворота (GATE_OPEN_VARIANT).</summary>
    private const int North = 1, East = 2, South = 4, West = 8, GateOpen = 16;

    /// <summary>Пуск шахты: ступени створок и подъёма, доли срока пуска (SILO_DOOR_STEPS, SILO_RISE_STEPS, DOORS_SHARE, RISE_FROM).</summary>
    private const int DoorSteps = 8, RiseSteps = 4;
    private const double DoorsShare = 0.35, RiseFrom = 0.6;

    private readonly Dictionary<string, (bool Working, int Variants)> _art = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, (double Value, double Until)> _activity = [];
    private readonly HashSet<long> _walls = [], _pipes = [];
    private readonly Dictionary<long, uint> _occupied = [];
    private readonly List<uint> _gone = [];

    private static long Key(double x, double y) => (long)(Math.Floor(x) + 65536) << 20 | (long)(Math.Floor(y) + 65536);

    /// <summary>Есть ли у облика вида кадры работы и сколько вариантов (как их объявил ArtSection).</summary>
    private (bool Working, int Variants) ArtOf(string type, BuildingSpec spec)
    {
        if (!_art.TryGetValue(type, out var art))
        {
            var look = BuildingArt.Look(type, new BuildingInfo((int)spec.Width, (int)spec.Height, BuildingKit.Role.Storage));
            _art[type] = art = (look.Working, Math.Max(1, look.Variants));
        }
        return art;
    }

    public void Update(Economy economy)
    {
        var c = fx.C;
        var step = (int)Math.Floor(fx.Time * FramesPerSecond);
        var buildings = c.Each(fx.Building, fx.Position);
        var sites = c.Each(fx.Site, fx.Position);
        Neighbours(buildings, sites);
        foreach (var entity in buildings)
        {
            if (fx.BuildingOf(entity) is not ({ } type, { } spec) || c.Pose(entity) is not { } at)
                continue;
            if (!fx.Visible(at.X, at.Y, spec.Width, spec.Height, Margin))
                continue;
            var art = ArtOf(type, spec);
            var site = c.Get(entity, fx.Site);
            var working = site is null && Working(entity, economy);
            if (site is null && !working && art.Working && type != "interceptor" && WorkOf(entity, spec) is { } value)
            {
                var state = _activity.GetValueOrDefault(entity, (value, 0));
                if (Math.Abs(value - state.Value) > 1e-9)
                    state.Until = fx.Time + WorkHold;
                _activity[entity] = (value, state.Until);
                working = fx.Time < state.Until;
            }
            var off = site is null && fx.Mine(entity) && SwitchedOff(entity);
            var packing = c.Get(entity, fx.Converting);
            var built = site is not null
                ? site.Number(fx.Site["progress"]) / fx.SiteTicks(spec)
                : packing is not null ? Remaining(packing) : 1;
            var phase = (int)fx.Num(entity, fx.Building, "phase");
            var frame = (int)((uint)((off ? 0 : step) + phase) % ArtFrames) + (working && !off && packing is null && art.Working ? ArtFrames : 0);
            var variant = art.Variants > 1 ? Variant(entity, type, spec, at.X, at.Y) : 0;
            c.Draw.Paint(entity, variant, frame, built < 1 ? (float)Math.Max(0, built) : null, off ? 1 : null);
        }
        // Площадки, к которым строитель ещё не приступил: здания на них нет — чертёж.
        foreach (var entity in sites)
        {
            if (c.Has(entity, fx.Building) || fx.BuildingOf(entity) is not ({ } type, { } spec) || c.Pose(entity) is not { } at)
                continue;
            if (!fx.Visible(at.X, at.Y, spec.Width, spec.Height, Margin))
                continue;
            var art = ArtOf(type, spec);
            var variant = art.Variants > 1 ? Variant(null, type, spec, at.X, at.Y) : 0;
            c.Draw.Paint(entity, variant, 0, 0f);
        }
        if (step % 64 == 0 && _activity.Count > 0)
        {
            _gone.Clear();
            foreach (var entity in _activity.Keys)
                if (!c.Has(entity, fx.Building))
                    _gone.Add(entity);
            foreach (var entity in _gone)
                _activity.Remove(entity);
        }
    }

    /// <summary>Сколько превращения ещё впереди, плавно между тиками (remainingOf).</summary>
    private double Remaining(ComponentValues converting)
    {
        var total = converting.Number(fx.Converting["total"]);
        return total > 0 ? Math.Clamp((converting.Number(fx.Converting["left"]) - fx.Alpha) / total, 0, 1) : 0;
    }

    /// <summary>Выключено игроком (switchedOff): у цеха — свой флаг on.</summary>
    public bool SwitchedOff(uint entity) =>
        fx.C.Get(entity, fx.Assembly) is { } assembly ? !assembly.Flag(fx.Assembly["on"]) : fx.C.Has(entity, fx.Off);

    /// <summary>
    /// Работает ли само по себе: реактор жжёт стержень, станция под нагрузкой, в шахте готовая боеголовка, в
    /// перехватчике ракета, лаборатория ведёт тему.
    /// </summary>
    private bool Working(uint entity, Economy economy)
    {
        var c = fx.C;
        if (c.Get(entity, fx.Reactor) is { } reactor && reactor.Number(fx.Reactor["left"]) > 0 && !c.Has(entity, fx.Off))
            return true;
        if (economy.Loaded.Contains(entity))
            return true;
        if (c.Get(entity, fx.Silo) is { } silo && silo.Flag(fx.Silo["ready"]))
            return true;
        if (fx.Num(entity, fx.Interceptor, "missiles") > 0)
            return true;
        return fx.Num(entity, fx.Research, "speed") > 0;
    }

    /// <summary>
    /// Мера работы, которая движется, пока здание работает (workOf): шахта — сколько добыто на складе, переработка —
    /// сколько её руды, цех — ход сборки. Склад чужого не виден — null.
    /// </summary>
    private double? WorkOf(uint entity, BuildingSpec spec)
    {
        var c = fx.C;
        if (spec.Extract)
        {
            if (c.Get(entity, fx.Stock) is not { } stock)
                return null;
            double load = 0;
            foreach (var good in Icons.Goods)
                load += stock.Number(fx.Stock[good]);
            return load;
        }
        if (spec.Refines is { } ore)
            return c.Get(entity, fx.Stock) is { } stock && fx.Stock.Find(ore) is { } field ? stock.Number(field) : null;
        return c.Get(entity, fx.Assembly) is { } assembly ? assembly.Number(fx.Assembly["progress"]) : null;
    }

    /// <summary>Стены и площадки стен, площадки труб и занятые зданиями тайлы — для стыковки.</summary>
    private void Neighbours(uint[] buildings, uint[] sites)
    {
        _walls.Clear();
        _pipes.Clear();
        _occupied.Clear();
        var c = fx.C;
        foreach (var entity in buildings)
        {
            if (fx.BuildingOf(entity) is not ({ } type, { } spec) || c.Pose(entity) is not { } at)
                continue;
            if (type is "wall" or "gate")
                _walls.Add(Key(at.X, at.Y));
            for (var dy = 0; dy < spec.Height; dy++)
                for (var dx = 0; dx < spec.Width; dx++)
                    _occupied[Key(at.X + dx, at.Y + dy)] = entity;
        }
        foreach (var entity in sites)
        {
            if (fx.BuildingOf(entity) is not ({ } type, { } spec) || c.Pose(entity) is not { } at)
                continue;
            if (type is "wall" or "gate")
                _walls.Add(Key(at.X, at.Y));
            if (spec.Pipe is not null)
                _pipes.Add(Key(at.X, at.Y));
        }
    }

    /// <summary>Здание на тайле (по зданиям кадра: occupancy клиенту не шлётся); нет — null.</summary>
    public uint? At(double x, double y) => _occupied.TryGetValue(Key(x, y), out var entity) ? entity : null;

    /// <summary>Труба стыкуется со всем, что сбоку и входит в сеть: с трубами и зданиями, кроме стен, и с площадками труб.</summary>
    private bool Joins(double x, double y)
    {
        if (_pipes.Contains(Key(x, y)))
            return true;
        return _occupied.TryGetValue(Key(x, y), out var other) && fx.BuildingOf(other) is { } kind && !kind.Spec.Isolated;
    }

    /// <summary>Вариант облика (wallVariant): entity null — площадка без здания.</summary>
    private int Variant(uint? entity, string type, BuildingSpec spec, double x, double y)
    {
        var c = fx.C;
        if (type == "silo")
            return entity is { } silo ? SiloVariant(silo) : 0;
        if (spec.Extract)
            return OreUnder(x, y, spec) is var ore and >= 0 ? ore + 1 : 0;
        if (type == "interceptor")
            return entity is { } interceptor ? (int)fx.Num(interceptor, fx.Interceptor, "missiles") : 0;
        if (spec.Pipe is not null)
            return (Joins(x, y - 1) ? North : 0) | (Joins(x + 1, y) ? East : 0) | (Joins(x, y + 1) ? South : 0) | (Joins(x - 1, y) ? West : 0);
        if (type is not ("wall" or "gate"))
            return 0;
        var variant = type == "gate" && entity is { } gate && c.Get(gate, fx.Gate) is { } open && open.Flag(fx.Gate["open"]) ? GateOpen : 0;
        if (_walls.Contains(Key(x, y - 1))) variant |= North;
        if (_walls.Contains(Key(x + 1, y))) variant |= East;
        if (_walls.Contains(Key(x, y + 1))) variant |= South;
        if (_walls.Contains(Key(x - 1, y))) variant |= West;
        return variant;
    }

    /// <summary>Ступень пуска шахты: створки расходятся, затем поднимается ракета; вне пуска — закрыта.</summary>
    private int SiloVariant(uint entity)
    {
        var start = fx.Num(entity, fx.Silo, "start", -1);
        if (start < 0 || fx.Tables.Buildings.GetValueOrDefault("silo")?.Warhead is not { } warhead)
            return 0;
        var share = (fx.Clock - start) / Math.Max(1, Math.Round(warhead.Launch / fx.Step));
        var doors = Math.Min(DoorSteps - 1, (int)Math.Floor(share / DoorsShare * (DoorSteps - 1)));
        if (share < RiseFrom)
            return Math.Max(0, doors);
        return DoorSteps - 1 + Math.Min(RiseSteps, (int)Math.Ceiling((share - RiseFrom) / (1 - RiseFrom) * RiseSteps));
    }

    /// <summary>Номер руды месторождения под шахтой (depositAt): порядок deposits.json; нет — −1.</summary>
    private int OreUnder(double x, double y, BuildingSpec spec)
    {
        if (fx.Deposits is not { } layer)
            return -1;
        var kind = Math.Max(0, layer.Fields.ToList().IndexOf("kind"));
        try
        {
            foreach (var item in Tools.Items(layer, 0, x + spec.Width / 2, y + spec.Height / 2, Math.Max(spec.Width, spec.Height) * 0.75))
                if (item.X >= x && item.X < x + spec.Width && item.Y >= y && item.Y < y + spec.Height)
                    return (int)item.Fields[kind];
        }
        catch (ToolException)
        {
        }
        return -1;
    }
}
