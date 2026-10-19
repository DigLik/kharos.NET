using Base.Shared.Data;
using Kharos.Mod.Sdk;
using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>Цвет эффекта: доли красного, зелёного и синего от 0 до 1 (Color в combatPass.ts).</summary>
internal readonly record struct Rgb(float R, float G, float B)
{
    /// <summary>0xRRGGBB для записей рисования.</summary>
    public uint Hex => Channel(R) << 16 | Channel(G) << 8 | Channel(B);

    private static uint Channel(float value) => (uint)Math.Round(Math.Clamp(value, 0, 1) * 255);

    /// <summary>Цвет из 0xRRGGBB (rgb в combatPass.ts).</summary>
    public static Rgb Of(uint color) => new((color >> 16 & 255) / 255f, (color >> 8 & 255) / 255f, (color & 255) / 255f);

    public Rgb Times(float level) => new(R * level, G * level, B * level);
}

/// <summary>
/// Общее эффектов мира (docs/dotnet-n6.md, К1): компоненты схемы base, таблицы data/*.json, облики Б-З по именам, часы
/// кадра и рисование в «точках эффектов» TS (размер в пикселях экрана при масштабе <see cref="EffectZoom"/>, combatPass.ts).
/// Мир читается на реплике клиента: свои склады, заказы и выключатели видны только хозяину и союзникам.
/// </summary>
internal sealed class Fx(BaseState state)
{
    /// <summary>Масштаб, при котором точка эффекта — пиксель экрана (EFFECT_ZOOM).</summary>
    public const double EffectZoom = 32;

    /// <summary>Пикселей облика на тайл.</summary>
    public const double Texels = 16;

    /// <summary>
    /// Слои свободных спрайтов: земля (следы, гарь), над зданиями (пыль — под летающими, освещается), верх — слой движка над
    /// светом и туманом (Frame.OverFog: взрывы, дым, значки сети — проходы TS после fogPass, ночью не темнеют).
    /// </summary>
    public const LookLayer Floor = LookLayer.Floor, Over = (LookLayer)4, Top = (LookLayer)7;

    private bool _tried;
    private readonly Dictionary<int, string?> _names = [];
    private readonly Dictionary<string, int> _looks = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, string> _strings = [];
    private readonly Dictionary<uint, long> _heads = [];
    private uint _random = 0x9e3779b9;

    /// <summary>Мир — base: компоненты и таблицы на месте.</summary>
    public bool Ready { get; private set; }

    public BaseTables Tables { get; private set; } = null!;

    public ComponentRef Position { get; private set; } = null!;
    public ComponentRef Owner { get; private set; } = null!;
    public ComponentRef Proto { get; private set; } = null!;
    public ComponentRef Building { get; private set; } = null!;
    public ComponentRef Site { get; private set; } = null!;
    public ComponentRef Unit { get; private set; } = null!;
    public ComponentRef Footprint { get; private set; } = null!;
    public ComponentRef Body { get; private set; } = null!;
    public ComponentRef Health { get; private set; } = null!;
    public ComponentRef Off { get; private set; } = null!;
    public ComponentRef Busy { get; private set; } = null!;
    public ComponentRef Assembly { get; private set; } = null!;
    public ComponentRef Stock { get; private set; } = null!;
    public ComponentRef Limits { get; private set; } = null!;
    public ComponentRef Producer { get; private set; } = null!;
    public ComponentRef Converting { get; private set; } = null!;
    public ComponentRef Research { get; private set; } = null!;
    public ComponentRef Interceptor { get; private set; } = null!;
    public ComponentRef Reactor { get; private set; } = null!;
    public ComponentRef Silo { get; private set; } = null!;
    public ComponentRef Shot { get; private set; } = null!;
    public ComponentRef Blast { get; private set; } = null!;
    public ComponentRef Missile { get; private set; } = null!;
    public ComponentRef Chaser { get; private set; } = null!;
    public ComponentRef Mark { get; private set; } = null!;
    public ComponentRef Beam { get; private set; } = null!;
    public ComponentRef Batch { get; private set; } = null!;
    public ComponentRef Shield { get; private set; } = null!;
    public ComponentRef Repair { get; private set; } = null!;
    public ComponentRef Dig { get; private set; } = null!;
    public ComponentRef Pave { get; private set; } = null!;
    public ComponentRef Path { get; private set; } = null!;
    public ComponentRef Hauler { get; private set; } = null!;
    public ComponentRef Unreachable { get; private set; } = null!;
    public ComponentRef Magazine { get; private set; } = null!;
    public ComponentRef Drop { get; private set; } = null!;
    public ComponentRef Accumulator { get; private set; } = null!;
    public ComponentRef Gate { get; private set; } = null!;
    public ComponentRef Player { get; private set; } = null!;
    public ComponentRef Team { get; private set; } = null!;
    public ComponentRef Mount { get; private set; } = null!;

    /// <summary>Слои предметов: месторождения и следы движка.</summary>
    public LayerRef? Deposits { get; private set; }
    public LayerRef? Traces { get; private set; }

    /// <summary>Правило партии unitAmmo (магазины юнитов).</summary>
    public bool UnitAmmo { get; private set; } = true;

    /// <summary>Правило партии repairCost (починка за кредиты).</summary>
    public double RepairCost { get; private set; } = 0.5;

    // ===== Кадр =====

    public ClientContext C { get; private set; } = null!;

    /// <summary>Экранные часы (секунды), длина кадра.</summary>
    public double Time { get; private set; }
    public double Delta { get; private set; }

    /// <summary>Тик реплики с долей и длина тика.</summary>
    public double Clock { get; private set; }
    public double Step { get; private set; }
    public double Alpha { get; private set; }

    /// <summary>Тайлов в пикселе экрана.</summary>
    public double Pixel { get; private set; }

    public double CameraX { get; private set; }
    public double CameraY { get; private set; }
    public double HalfWidth { get; private set; }
    public double HalfHeight { get; private set; }

    /// <summary>Начало кадра: false — мир не base (нет компонентов или данных), эффекты молчат.</summary>
    public bool Begin(ClientContext c)
    {
        C = c;
        if (!_tried)
        {
            _tried = true;
            Ready = Bind(c);
        }
        if (!Ready)
            return false;
        Time = c.Time;
        Delta = Math.Clamp(c.Dt, 0, 0.25);
        Step = c.TickDt > 0 ? c.TickDt : 0.05;
        Alpha = c.Alpha;
        Clock = c.Tick + c.Alpha;
        var zoom = c.Camera.Zoom > 0 ? c.Camera.Zoom : 32;
        Pixel = 1 / zoom;
        (CameraX, CameraY) = (c.Camera.X, c.Camera.Y);
        HalfWidth = c.ScreenWidth / 2 / zoom;
        HalfHeight = c.ScreenHeight / 2 / zoom;
        ReadTeams(c);
        return true;
    }

    private bool Bind(ClientContext c)
    {
        try
        {
            Tables = BaseTables.Load(name => c.TryData(name, out var bytes) ? bytes.ToArray() : null);
            if (Tables.Buildings.Count == 0)
                return false;
            Position = c.Component("Position");
            Owner = c.Component("Owner");
            Proto = c.Component("Proto");
            Building = c.Component("Building");
            Site = c.Component("Site");
            Unit = c.Component("Unit");
            Footprint = c.Component("Footprint");
            Body = c.Component("Body");
            Health = c.Component("Health");
            Off = c.Component("Off");
            Busy = c.Component("Busy");
            Assembly = c.Component("Assembly");
            Stock = c.Component("Stock");
            Limits = c.Component("Limits");
            Producer = c.Component("Producer");
            Converting = c.Component("Converting");
            Research = c.Component("Research");
            Interceptor = c.Component("Interceptor");
            Reactor = c.Component("Reactor");
            Silo = c.Component("Silo");
            Shot = c.Component("Shot");
            Blast = c.Component("Blast");
            Missile = c.Component("Missile");
            Chaser = c.Component("Chaser");
            Mark = c.Component("Mark");
            Beam = c.Component("Beam");
            Batch = c.Component("Batch");
            Shield = c.Component("Shield");
            Repair = c.Component("Repair");
            Dig = c.Component("Dig");
            Pave = c.Component("Pave");
            Path = c.Component("Path");
            Hauler = c.Component("Hauler");
            Unreachable = c.Component("Unreachable");
            Magazine = c.Component("Magazine");
            Drop = c.Component("Drop");
            Accumulator = c.Component("Accumulator");
            Gate = c.Component("Gate");
            Player = c.Component("Player");
            Team = c.Component("Team");
            Mount = c.Component("Mount");
        }
        catch (Exception)
        {
            return false;
        }
        // LayerRef клиенту даёт только загрузка (Parts), а на клиенте схема слоёв в ней пуста: номер слоя — место в
        // data/layers.json (движок заводит слои в этом порядке), поля — оттуда же.
        Deposits = state.P.Deposits ?? LayerOf("deposits");
        Traces = LayerOf("traces");
        ReadRules(c);
        return true;
    }

    /// <summary>Слой предметов по data/layers.json: номер — место записи; нет — null.</summary>
    private LayerRef? LayerOf(string name)
    {
        var id = 0;
        foreach (var (key, record) in Tables.Layers)
        {
            if (key == name)
                return record.Shape == "items" ? new LayerRef(name, id, record.Scope == "player", (int)(record.Cell ?? 1), 0, [.. record.Fields.Keys]) : null;
            id++;
        }
        return null;
    }

    /// <summary>Правила партии из настроек (games.json → rules); нет — по умолчанию rules.json.</summary>
    private void ReadRules(ClientContext c)
    {
        UnitAmmo = Tables.Rules.UnitAmmo;
        RepairCost = Tables.Rules.RepairCost;
        try
        {
            using var settings = System.Text.Json.JsonDocument.Parse(c.Settings());
            if (settings.RootElement.TryGetProperty("settings", out var game) && game.ValueKind == System.Text.Json.JsonValueKind.Object
                && game.TryGetProperty("rules", out var rules) && rules.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (rules.TryGetProperty("unitAmmo", out var ammo) && ammo.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                    UnitAmmo = ammo.GetBoolean();
                if (rules.TryGetProperty("repairCost", out var cost) && cost.ValueKind == System.Text.Json.JsonValueKind.Number)
                    RepairCost = cost.GetDouble();
            }
        }
        catch (Exception)
        {
            // Настроек нет (тест без оболочки) — правила по умолчанию.
        }
    }

    // ===== Виды =====

    /// <summary>Имя вида по номеру Proto (после «раздел/»); нет — null.</summary>
    public string? NameOf(int proto)
    {
        if (proto < 0)
            return null;
        if (!_names.TryGetValue(proto, out var name))
        {
            var path = C.ProtoPath(proto);
            _names[proto] = name = path is null ? null : path[(path.LastIndexOf('/') + 1)..];
        }
        return name;
    }

    public int ProtoOf(uint entity) => C.Get(entity, Proto) is { } values ? (int)values.Number(Proto["id"]) : -1;

    /// <summary>Вид здания сущности (у площадки — будущего здания): имя и запись; не здание — null.</summary>
    public (string Type, BuildingSpec Spec)? BuildingOf(uint entity) =>
        NameOf(ProtoOf(entity)) is { } name && Tables.Buildings.TryGetValue(name, out var spec) ? (name, spec) : null;

    public (string Type, Base.Shared.Data.UnitSpec Spec)? UnitOf(uint entity) =>
        NameOf(ProtoOf(entity)) is { } name && Tables.Units.TryGetValue(name, out var spec) ? (name, spec) : null;

    public WeaponSpec? WeaponOf(int proto) => NameOf(proto) is { } name && Tables.Weapons.TryGetValue(name, out var spec) ? spec : null;

    /// <summary>Номер груза по номеру Proto — порядок столбцов Stock и <see cref="Icons.Goods"/>; не груз — −1.</summary>
    public int GoodOf(int proto) => NameOf(proto) is { } name ? Array.IndexOf(Icons.Goods, name) : -1;

    /// <summary>Строка мира по номеру (строковое поле компонента).</summary>
    public string Str(double id)
    {
        var key = (uint)id;
        if (!_strings.TryGetValue(key, out var text))
            _strings[key] = text = Tools.Text(key);
        return text;
    }

    /// <summary>Номер свободного облика по имени (инструмент Look); нет — −1.</summary>
    public int Look(string name)
    {
        if (!_looks.TryGetValue(name, out var id))
            _looks[name] = id = C.LookId(name);
        return id;
    }

    // ===== Сущности =====

    public double Num(uint entity, ComponentRef component, string field, double fallback = 0) =>
        C.Get(entity, component) is { } values ? values.Number(component[field]) : fallback;

    public uint OwnerOf(uint entity) => (uint)Num(entity, Owner, "player");

    public bool Mine(uint entity) => OwnerOf(entity) == C.Player;

    /// <summary>Своё или союзное: склады и выключатели такого видны.</summary>
    public bool Friend(uint player) => player == C.Player || Allied(player);

    private void ReadTeams(ClientContext c)
    {
        _heads.Clear();
        foreach (var entity in c.Each(Team))
            if (c.Get(entity, Team) is { } team && team.Number(Team["head"]) >= 0)
                _heads[(uint)team.Number(Team["player"])] = (long)team.Number(Team["head"]);
    }

    public bool Allied(uint player) =>
        player != 0 && player != C.Player && _heads.TryGetValue(C.Player, out var mine) && _heads.TryGetValue(player, out var other) && mine == other;

    /// <summary>Основание здания (место — левый верхний тайл); не здание — null.</summary>
    public (double W, double H)? SizeOf(uint entity)
    {
        if (C.Get(entity, Footprint) is { } footprint)
            return (footprint.Number(Footprint["w"]), footprint.Number(Footprint["h"]));
        return BuildingOf(entity) is { } kind ? (kind.Spec.Width, kind.Spec.Height) : null;
    }

    /// <summary>Радиус тела юнита.</summary>
    public double Radius(uint entity) => C.Get(entity, Body) is { } body ? body.Number(Body["radius"]) : UnitOf(entity)?.Spec.Radius ?? 0.4;

    /// <summary>Середина сущности: юнит — где нарисован, здание или площадка — середина основания.</summary>
    public (double X, double Y)? CenterOf(uint entity)
    {
        if (C.Pose(entity) is not { } at)
            return null;
        if (!C.Has(entity, Unit) && SizeOf(entity) is { } size)
            return (at.X + size.W / 2, at.Y + size.H / 2);
        return (at.X, at.Y);
    }

    /// <summary>Сколько тиков строится здание (siteTicks).</summary>
    public double SiteTicks(BuildingSpec spec) => Math.Max(1, Base.Shared.Rules.BuildRules.SiteTicks(spec, Step));

    /// <summary>Кредиты своего игрока.</summary>
    public double Credits()
    {
        foreach (var entity in C.Each(Player))
            if (C.Get(entity, Player) is { } account && account.Number(Player["id"]) == C.Player)
                return account.Number(Player["credits"]);
        return 0;
    }

    // ===== Экран =====

    /// <summary>Точка в кадре (с запасом margin тайлов).</summary>
    public bool Visible(double x, double y, double margin = 3) =>
        Math.Abs(x - CameraX) < HalfWidth + margin && Math.Abs(y - CameraY) < HalfHeight + margin;

    /// <summary>Прямоугольник задевает кадр.</summary>
    public bool Visible(double left, double top, double width, double height, double margin) =>
        left + width > CameraX - HalfWidth - margin && left < CameraX + HalfWidth + margin &&
        top + height > CameraY - HalfHeight - margin && top < CameraY + HalfHeight + margin;

    /// <summary>Размер эффекта в тайлах по размеру в точках, не тоньше пикселя экрана (sized).</summary>
    public double Sized(double points) => Math.Max(points / EffectZoom, Pixel);

    /// <summary>Отрезок толщиной в точках эффектов (line в combatPass.ts).</summary>
    public void Line(double fromX, double fromY, double toX, double toY, double points, Rgb color, double level = 1) =>
        Strip(fromX, fromY, toX, toY, Sized(points), color, level);

    /// <summary>Отрезок толщиной в тайлах (strip).</summary>
    public void Strip(double fromX, double fromY, double toX, double toY, double width, Rgb color, double level = 1)
    {
        if (level <= 0.004)
            return;
        C.Draw.Line(fromX, fromY, toX, toY, color.Hex, (float)(width / Pixel), (float)Math.Min(1, level));
    }

    /// <summary>
    /// Свечение (glow в combatPass.ts): вспышки, лучи, искры, кольца — их цвет прибавляется к кадру. Как в TS, они
    /// рисуются после всех обычных (solid) — копятся за кадр и уходят разом в <see cref="FlushGlow"/>.
    /// </summary>
    private readonly List<(byte Kind, double A, double B, double C, double D, float Width, uint Color, float Alpha)> _glow = [];

    /// <summary>Отрезок свечения толщиной в точках эффектов.</summary>
    public void GlowLine(double fromX, double fromY, double toX, double toY, double points, Rgb color, double level = 1)
    {
        if (level > 0.004)
            _glow.Add((0, fromX, fromY, toX, toY, (float)(Sized(points) / Pixel), color.Hex, (float)Math.Min(1, level)));
    }

    /// <summary>Квадрат свечения стороной в точках эффектов вокруг точки.</summary>
    public void GlowDot(double x, double y, double points, Rgb color, double level = 1)
    {
        var side = Sized(points);
        if (level > 0.004)
            _glow.Add((1, x - side / 2, y - side / 2, x + side / 2, y + side / 2, 0, color.Hex, (float)Math.Min(1, level)));
    }

    /// <summary>Кольцо свечения радиуса radius тайлов, толщиной width пикселей.</summary>
    public void GlowRing(double x, double y, double radius, float width, Rgb color, double level)
    {
        if (level > 0.004)
            _glow.Add((2, x, y, radius, 0, width, color.Hex, (float)Math.Min(1, level)));
    }

    /// <summary>Свечение кадра — в выход, после обычных рисунков (glow.draw() после solid.draw()).</summary>
    public void FlushGlow()
    {
        if (_glow.Count == 0)
            return;
        var draw = C.Draw;
        draw.Glow(true);
        foreach (var (kind, a, b, c, d, width, color, alpha) in _glow)
        {
            if (kind == 0)
                draw.Line(a, b, c, d, color, width, alpha);
            else if (kind == 1)
                draw.Rect(a, b, c, d, color, null, alpha);
            else
                draw.Circle(a, b, c, color, width, alpha);
        }
        draw.Glow(false);
        _glow.Clear();
    }

    /// <summary>Квадрат стороной в точках эффектов вокруг точки (dot).</summary>
    public void Dot(double x, double y, double points, Rgb color, double level = 1)
    {
        var side = Sized(points);
        Box(x - side / 2, y - side / 2, side, side, color, level);
    }

    /// <summary>Залитый прямоугольник в тайлах.</summary>
    public void Box(double left, double top, double width, double height, Rgb color, double alpha = 1)
    {
        if (alpha <= 0.004 || width <= 0 || height <= 0)
            return;
        C.Draw.Rect(left, top, left + width, top + height, color.Hex, null, (float)Math.Min(1, alpha));
    }

    /// <summary>
    /// Свободный спрайт облика look (Б-З: начало и точка привязки — левый верхний угол) стороной size тайлов: native —
    /// сторона облика в тайлах. Сторона «своя» (0): ничья (3) рисуется серым.
    /// </summary>
    public void Sprite(int look, int frame, double left, double top, double size, double native, float alpha, LookLayer layer, uint tint = 0xffffff, double angle = 0)
    {
        if (look < 0 || alpha <= 0.004 || size <= 0)
            return;
        C.Draw.Sprite(look, left, top, angle, 0, frame, (float)(size / native), Math.Min(1, alpha), 0, layer, tint);
    }

    // ===== Числа =====

    /// <summary>Простое воспроизводимое число от 0 до 1 по двум целым (noise в combatPass.ts).</summary>
    public static double Noise(double a, double b) => ArtMath.Noise(a, b);

    /// <summary>Случайное от 0 до 1: своё зерно, одинаковое в CLR и wasm (дым и пыль).</summary>
    public double Random()
    {
        var x = _random;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        _random = x;
        return x / 4294967296.0;
    }

    /// <summary>(Random() − 0,5) × size (spread).</summary>
    public double Spread(double size) => (Random() - 0.5) * size;

    /// <summary>e^x для |x| ≤ 1 рядом Тейлора: одинаково в CLR и wasm.</summary>
    public static double Exp(double x)
    {
        double sum = 1, term = 1;
        for (var n = 1; n <= 12; n++)
        {
            term *= x / n;
            sum += term;
        }
        return sum;
    }

    public static double Hypot(double x, double y) => Math.Sqrt(x * x + y * y);

    /// <summary>Угол к (−π, π].</summary>
    public static double Wrap(double angle) => angle - Math.PI * 2 * Math.Floor((angle + Math.PI) / (Math.PI * 2));
}
