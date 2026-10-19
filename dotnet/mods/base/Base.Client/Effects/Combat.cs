using Base.Shared.Data;
using Kharos.Mod.Sdk;
using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>
/// Клубы дыма и пыли (combatPass.ts): живут только на экране — место и скорость в тайлах, возраст и срок в секундах,
/// начальный поперечник, рост к концу, яркость, плотность и цвет (у дыма — серый, темнеющий к ночи).
/// </summary>
internal sealed class Smoke(Fx fx)
{
    /// <summary>Больше клубов разом не бывает: старые уступают место новым (PUFF_LIMIT, DUST_LIMIT).</summary>
    private const int PuffLimit = 700, DustLimit = 500;

    /// <summary>Сколько разных клубов (PUFF_KINDS) и сторона картинки в тайлах.</summary>
    private const int Kinds = 3;
    private const double Native = 1;

    /// <summary>Дым набирает долю скорости ветра и всплывает (SMOKE_WIND, SMOKE_RISE); ночью темнеет не дочерна (SMOKE_NIGHT).</summary>
    private const double SmokeWind = 0.12, SmokeRise = 0.25, SmokeNight = 0.75;

    /// <summary>Пыль под летающими набирает долю ветра и не всплывает (DUST_WIND).</summary>
    private const double DustWind = 0.2;

    private struct Puff
    {
        public double X, Y, SpeedX, SpeedY, Age, Life, Size, Grow, Shade, Level;
        public int Kind;
        public Rgb Color;
    }

    private readonly List<Puff> _puffs = [], _dust = [];

    /// <summary>Новый клуб дыма (puff в combatPass.ts).</summary>
    public void Add(double x, double y, double size, double life, double shade, double level, double speedX = 0, double speedY = 0)
    {
        if (_puffs.Count >= PuffLimit)
            _puffs.RemoveAt(0);
        _puffs.Add(new Puff
        {
            X = x, Y = y, SpeedX = speedX, SpeedY = speedY, Life = life * (0.75 + fx.Random() * 0.5), Size = size, Grow = 1.2 + fx.Random(),
            Shade = shade, Level = level, Kind = (int)Math.Floor(fx.Random() * Kinds),
        });
    }

    /// <summary>Клуб пыли своего цвета, разлетается от юнита.</summary>
    public void AddDust(double x, double y, double speedX, double speedY, double life, double size, double grow, double level, Rgb color)
    {
        if (_dust.Count >= DustLimit)
            _dust.RemoveAt(0);
        _dust.Add(new Puff { X = x, Y = y, SpeedX = speedX, SpeedY = speedY, Life = life, Size = size, Grow = grow, Shade = 1, Level = level, Kind = (int)Math.Floor(fx.Random() * Kinds), Color = color });
    }

    /// <summary>Пыль: быстро тормозит, сносится ветром и тает; лежит под летающими.</summary>
    public void DrawDust(double windX, double windY)
    {
        var drag = Fx.Exp(-fx.Delta * 3.5);
        var look = fx.Look("effects/puff");
        var kept = 0;
        for (var i = 0; i < _dust.Count; i++)
        {
            var item = _dust[i];
            item.Age += fx.Delta;
            if (item.Age >= item.Life)
                continue;
            item.SpeedX = windX * DustWind + (item.SpeedX - windX * DustWind) * drag;
            item.SpeedY = windY * DustWind + (item.SpeedY - windY * DustWind) * drag;
            item.X += item.SpeedX * fx.Delta;
            item.Y += item.SpeedY * fx.Delta;
            _dust[kept++] = item;
            if (!fx.Visible(item.X, item.Y, 2))
                continue;
            var age = item.Age / item.Life;
            var size = item.Size * (1 + (item.Grow - 1) * age);
            var level = item.Level * Math.Min(1, age * 6) * (1 - age);
            fx.Sprite(look, item.Kind, item.X - size / 2, item.Y - size / 2, size, Native, (float)level, Fx.Over, item.Color.Hex);
        }
        _dust.RemoveRange(kept, _dust.Count - kept);
    }

    /// <summary>Дым: тормозит, набирает скорость ветра, всплывает; клубы растут и тают; ночью — темнее и синее.</summary>
    public void DrawSmoke(double windX, double windY, double light)
    {
        var day = SmokeNight + (1 - SmokeNight) * light;
        var warm = day * (0.8 + 0.2 * light);
        var drag = Fx.Exp(-fx.Delta * 2.5);
        var look = fx.Look("effects/puff");
        var kept = 0;
        for (var i = 0; i < _puffs.Count; i++)
        {
            var item = _puffs[i];
            item.Age += fx.Delta;
            if (item.Age >= item.Life)
                continue;
            item.SpeedX = windX * SmokeWind + (item.SpeedX - windX * SmokeWind) * drag;
            item.SpeedY = windY * SmokeWind - SmokeRise + (item.SpeedY - windY * SmokeWind + SmokeRise) * drag;
            item.X += item.SpeedX * fx.Delta;
            item.Y += item.SpeedY * fx.Delta;
            _puffs[kept++] = item;
            if (!fx.Visible(item.X, item.Y, 2))
                continue;
            var age = item.Age / item.Life;
            var size = item.Size * (1 + (item.Grow - 1) * age);
            var level = item.Level * Math.Min(1, age * 8) * (1 - age);
            var tint = new Rgb((float)(item.Shade * warm), (float)(item.Shade * warm), (float)(item.Shade * day));
            fx.Sprite(look, item.Kind, item.X - size / 2, item.Y - size / 2, size, Native, (float)level, Fx.Top, tint.Hex);
        }
        _puffs.RemoveRange(kept, _puffs.Count - kept);
    }
}

/// <summary>
/// Бой (combatPass.ts): выстрелы — пуля с трассером, ракета по дуге с тенью, ядро, струя огня, цепной разряд, лазер;
/// вспышки у ствола; взрывы Blast (и аварии Burst — хост показывает их взрывом) кадрами effects/blast; огни — взрывы,
/// лучи, вспышки, ракеты; дым у ствола, за ракетой и ядром, после взрыва; пыль под летающими; метки ядерного удара и
/// сигналы союзников. Светящееся рисуется поверх (в TS — сложением), тёмное — смешением.
/// </summary>
internal sealed class Combat(Fx fx, Smoke smoke)
{
    private const int BlastFrames = 8;
    private const double BlastNative = 3;

    /// <summary>Картинка взрыва шире его радиуса, пятно света — ещё шире (BLAST_SCALE, BLAST_LIGHT).</summary>
    private const double BlastScale = 2.6, BlastLight = 5;

    private const double BulletTail = 0.45, RocketBody = 0.3;
    private const double RocketSmokeStep = 0.22, ShellSmokeStep = 0.4;

    /// <summary>Дуга ракеты — доля дальности выстрела (ROCKET_ARC).</summary>
    private const double RocketArc = 0.3;

    /// <summary>Излом разряда (ARC_STEP, ARC_SWAY, ARC_RATE).</summary>
    private const double ArcStep = 0.45, ArcSway = 0.28, ArcRate = 30;

    private const int MuzzleTicks = 1, FlameTongues = 6;

    /// <summary>Пыль под летающими (DUST_RATE, DUST_RING, DUST_SPEED).</summary>
    private const double DustRate = 30, DustRing = 0.7, DustSpeed = 1.6;

    private static readonly Rgb Bullet = new(1, 0.9f, 0.55f), Tracer = new(0.75f, 0.4f, 0.05f), Rocket = new(0.85f, 0.87f, 0.9f);
    private static readonly Rgb Flame = new(1, 0.6f, 0.2f), Shell = new(0.12f, 0.12f, 0.14f), Laser = new(1, 0.15f, 0.3f);
    private static readonly Rgb Ember = new(1, 0.35f, 0.08f), Spark = new(0.3f, 0.6f, 1), Core = new(1, 1, 1);
    private static readonly Rgb LaunchSite = new(1, 0.7f, 0.2f), Ping = new(0.3f, 0.9f, 0.5f), Strike = new(1, 0.15f, 0.1f);

    /// <summary>Цвет пыли по земле (SAND_DUST по биомам, ROCK_DUST, SPRAY над болотом).</summary>
    private static readonly uint[] SandDust = [0xd9b57c, 0xe6dfcf, 0xc98660, 0xb3a679];
    private const uint RockDust = 0xaa9d89, Spray = 0xc4d6cf;

    /// <summary>Где снаряд оставил последний клуб; по нему же видно, какие выстрелы и взрывы уже дымили.</summary>
    private readonly Dictionary<uint, (double X, double Y)> _trails = [];
    private readonly HashSet<uint> _alive = [];
    private readonly List<uint> _gone = [];
    private readonly Dictionary<uint, (double X, double Y, int Seen)> _flyers = [];
    private int _dustFrame;

    /// <summary>Доля срока, которую выстрел или взрыв уже прожил, с долей тика (ageOf).</summary>
    private double AgeOf(double age, double life) => Math.Min(1, (age + fx.Alpha) / Math.Max(1, life));

    /// <summary>Высота ракеты над землёй в точке (x, y): по пройденной доле пути (arcLift).</summary>
    private static double ArcLift(double fromX, double fromY, double toX, double toY, double x, double y)
    {
        var flown = Fx.Hypot(x - fromX, y - fromY);
        var left = Fx.Hypot(toX - x, toY - y);
        var reach = flown + left;
        return reach > 0 ? DetMath.Sin(Math.PI * (flown / reach)) * reach * RocketArc : 0;
    }

    /// <summary>Дым и огни боя (проход lights в combatPass.ts): до рисунков.</summary>
    public void Emit(Missiles missiles)
    {
        _alive.Clear();
        missiles.Emit(_trails, _alive);
        Blasts();
        Shots();
        _gone.Clear();
        foreach (var entity in _trails.Keys)
            if (!_alive.Contains(entity))
                _gone.Add(entity);
        foreach (var entity in _gone)
            _trails.Remove(entity);
    }

    private void Blasts()
    {
        var c = fx.C;
        foreach (var entity in c.Each(fx.Blast, fx.Position))
        {
            if (c.Get(entity, fx.Blast) is not { } blast || c.Pose(entity) is not { } at)
                continue;
            _alive.Add(entity);
            var size = blast.Number(fx.Blast["size"]);
            var lift = blast.Number(fx.Blast["lift"]);
            var left = 1 - AgeOf(blast.Number(fx.Blast["age"]), blast.Number(fx.Blast["life"]));
            var reach = size * 16;
            c.Draw.Light(at.X, at.Y - lift, (float)(reach * BlastLight * left), (float)(reach * (BlastLight / 2) * left), (float)left);
            if (_trails.ContainsKey(entity))
                continue;
            _trails[entity] = (at.X, at.Y);
            // После взрыва остаётся тёмный дым: чем больше взрыв, тем его больше и тем дольше он висит.
            var count = (int)Math.Round(3 + size * 4);
            for (var i = 0; i < count; i++)
                smoke.Add(at.X + fx.Spread(size), at.Y - lift + fx.Spread(size), size * (0.5 + fx.Random() * 0.4), 1.4 + size, 0.3, 0.75, fx.Spread(0.5), fx.Spread(0.5));
        }
    }

    private void Shots()
    {
        var c = fx.C;
        foreach (var entity in c.Each(fx.Shot, fx.Position))
        {
            if (c.Get(entity, fx.Shot) is not { } shot || fx.WeaponOf(fx.ProtoOf(entity)) is not { } weapon || c.Pose(entity) is not { } at)
                continue;
            _alive.Add(entity);
            double fromX = shot.Number(fx.Shot["fromX"]), fromY = shot.Number(fx.Shot["fromY"]);
            double toX = shot.Number(fx.Shot["toX"]), toY = shot.Number(fx.Shot["toY"]);
            var age = shot.Number(fx.Shot["age"]);
            var moving = weapon.Speed is > 0;
            if (!moving)
            {
                var left = 1 - AgeOf(age, shot.Number(fx.Shot["life"]));
                c.Draw.Light(fromX, fromY, 14, 6, (float)left);
                c.Draw.Light(toX, toY, 20, 9, (float)left);
            }
            else
            {
                if (age <= MuzzleTicks)
                    c.Draw.Light(fromX, fromY, 14, 6, 1);
                if (weapon.Shot == "rocket")
                    c.Draw.Light(at.X, at.Y, 10, 4, 0.8f);
            }
            if (!_trails.TryGetValue(entity, out var last))
            {
                _trails[entity] = (fromX, fromY);
                if (!moving)
                {
                    // Лазер и разряд дымят там, куда попали.
                    smoke.Add(toX, toY, 0.3, 0.7, 0.45, 0.5, fx.Spread(0.3), fx.Spread(0.3));
                    continue;
                }
                // Дым у ствола летит вслед за снарядом: у пули — облачко, у ядра и ракеты — целое облако.
                var reach = Fx.Hypot(toX - fromX, toY - fromY);
                if (reach == 0)
                    reach = 1;
                var (headingX, headingY) = ((toX - fromX) / reach, (toY - fromY) / reach);
                var heavy = weapon.Shot != "bullet";
                for (var i = 0; i < (heavy ? 5 : 1); i++)
                {
                    var push = heavy ? 0.6 + fx.Random() * 1.6 : 0.8;
                    smoke.Add(fromX, fromY, heavy ? 0.4 : 0.2, heavy ? 1.1 : 0.4, 0.55, heavy ? 0.7 : 0.45, headingX * push + fx.Spread(0.6), headingY * push + fx.Spread(0.6));
                }
                continue;
            }
            if (weapon.Shot is not ("rocket" or "shell"))
                continue;
            var rocket = weapon.Shot == "rocket";
            var step = rocket ? RocketSmokeStep : ShellSmokeStep;
            var distance = Fx.Hypot(at.X - last.X, at.Y - last.Y);
            var count = distance > 0 ? Math.Min(16, (int)Math.Floor(distance / step)) : 0;
            for (var i = 1; i <= count; i++)
            {
                var share = i * step / distance;
                var x = last.X + (at.X - last.X) * share;
                var y = last.Y + (at.Y - last.Y) * share;
                // Дым ракеты остаётся в воздухе, на высоте её дуги; ядро летит прямо и низко.
                if (rocket)
                    smoke.Add(x + fx.Spread(0.08), y - ArcLift(fromX, fromY, toX, toY, x, y) + fx.Spread(0.08), 0.32, 1.3, 0.55, 0.75, fx.Spread(0.25), fx.Spread(0.25));
                else
                    smoke.Add(x, y, 0.18, 0.7, 0.5, 0.55, fx.Spread(0.15), fx.Spread(0.15));
            }
            if (count > 0)
            {
                var share = count * step / distance;
                _trails[entity] = (last.X + (at.X - last.X) * share, last.Y + (at.Y - last.Y) * share);
            }
        }
    }

    /// <summary>Новая пыль под летающими: клубы поднимаются кольцом вокруг юнита и разлетаются от него.</summary>
    public void Dust()
    {
        var c = fx.C;
        _dustFrame++;
        foreach (var entity in c.Each(fx.Unit, fx.Position))
        {
            if (fx.UnitOf(entity) is not { Spec.Kind: "air" } unit || c.Pose(entity) is not { } at || !fx.Visible(at.X, at.Y, 2))
                continue;
            var known = _flyers.TryGetValue(entity, out var last);
            _flyers[entity] = (at.X, at.Y, _dustFrame);
            var radius = unit.Spec.Radius;
            var expected = DustRate * radius * fx.Delta;
            var count = (int)Math.Floor(expected) + (fx.Random() < expected % 1 ? 1 : 0);
            for (var i = 0; i < count; i++)
            {
                // Клуб — в случайной точке пути за кадр, чтобы след быстрого юнита был сплошным.
                var share = known && Fx.Hypot(at.X - last.X, at.Y - last.Y) < 1 ? fx.Random() : 1;
                var x = known ? last.X + (at.X - last.X) * share : at.X;
                var y = known ? last.Y + (at.Y - last.Y) * share : at.Y;
                var angle = fx.Random() * Math.PI * 2;
                var reach = radius * DustRing * (0.7 + fx.Random() * 0.6);
                var (cos, sin) = (DetMath.Cos(angle), DetMath.Sin(angle));
                var (groundX, groundY) = (x + cos * reach, y + sin * reach);
                var (color, level) = DustColor(groundX, groundY);
                var speed = DustSpeed * (0.6 + fx.Random() * 0.8);
                smoke.AddDust(groundX, groundY, cos * speed, sin * speed, 0.6 + fx.Random() * 0.5, radius * (0.6 + fx.Random() * 0.4), 1.8 + fx.Random(), level, color);
            }
        }
        if (_dustFrame % 120 == 0)
        {
            _gone.Clear();
            foreach (var (entity, flyer) in _flyers)
                if (_dustFrame - flyer.Seen > 120)
                    _gone.Add(entity);
            foreach (var entity in _gone)
                _flyers.Remove(entity);
        }
    }

    /// <summary>Цвет пыли над тайлом: песок — по биому, скала — серо-бурый, над болотом — бледная взвесь.</summary>
    private (Rgb Color, double Level) DustColor(double x, double y)
    {
        if (!fx.C.Terrain(x, y, out var info))
            return (Rgb.Of(RockDust), 0.4);
        return info.Terrain switch
        {
            0 => (Rgb.Of(Spray), 0.3),
            1 => (Rgb.Of(SandDust[Math.Min(info.Biome, (byte)(SandDust.Length - 1))]), 0.5),
            _ => (Rgb.Of(RockDust), 0.4),
        };
    }

    /// <summary>Взрывы кадрами effects/blast: огненный шар темнеет до дыма, к концу дым тает.</summary>
    public void DrawBlasts()
    {
        var c = fx.C;
        var look = fx.Look("effects/blast");
        foreach (var entity in c.Each(fx.Blast, fx.Position))
        {
            if (c.Get(entity, fx.Blast) is not { } blast || c.Pose(entity) is not { } at || !fx.Visible(at.X, at.Y))
                continue;
            var y = at.Y - blast.Number(fx.Blast["lift"]);
            var age = AgeOf(blast.Number(fx.Blast["age"]), blast.Number(fx.Blast["life"]));
            var frame = Math.Min(BlastFrames - 1, (int)Math.Floor(age * BlastFrames));
            var size = blast.Number(fx.Blast["size"]) * BlastScale;
            var level = Math.Min(1, (1 - age) * 3);
            fx.Sprite(look, frame, at.X - size / 2, y - size / 2, size, BlastNative, (float)level, Fx.Top);
        }
    }

    /// <summary>Выстрелы: пули, ракеты, ядра, огонь, разряды, лазеры.</summary>
    public void DrawShots()
    {
        var c = fx.C;
        var time = fx.Time;
        var point = 1 / Fx.EffectZoom;
        foreach (var entity in c.Each(fx.Shot, fx.Position))
        {
            if (c.Get(entity, fx.Shot) is not { } shot || fx.WeaponOf(fx.ProtoOf(entity)) is not { } weapon || c.Pose(entity) is not { } at)
                continue;
            double fromX = shot.Number(fx.Shot["fromX"]), fromY = shot.Number(fx.Shot["fromY"]);
            double toX = shot.Number(fx.Shot["toX"]), toY = shot.Number(fx.Shot["toY"]);
            if (!fx.Visible(at.X, at.Y) && !fx.Visible(toX, toY))
                continue;
            var age = shot.Number(fx.Shot["age"]);
            if (weapon.Speed is not > 0)
            {
                Instant(entity, weapon, fromX, fromY, toX, toY, 1 - AgeOf(age, shot.Number(fx.Shot["life"])), time);
                continue;
            }
            var (x, y) = (at.X, at.Y);
            if (age <= MuzzleTicks)
                fx.Dot(fromX, fromY, weapon.Shot == "bullet" ? 5 : 9, Bullet);
            // Куда летит: снаряды летят прямо — к цели.
            var headingX = toX - fromX;
            var headingY = toY - fromY;
            var speed = Fx.Hypot(headingX, headingY);
            if (speed == 0)
                speed = 1;
            headingX /= speed;
            headingY /= speed;
            if (weapon.Shot == "bullet")
            {
                // След не длиннее пройденного; тёмная подложка: на светлом песке одно свечение не видно.
                var tail = Math.Min(BulletTail, Fx.Hypot(x - fromX, y - fromY));
                fx.Line(x - headingX * tail, y - headingY * tail, x, y, 2, Tracer);
                fx.Line(x - headingX * tail, y - headingY * tail, x, y, 2, Bullet);
            }
            else if (weapon.Shot == "rocket")
            {
                // Ракета идёт по дуге поверх стен: чем выше поднялась, тем дальше её тень на земле.
                var head = y - ArcLift(fromX, fromY, toX, toY, x, y);
                var tailX = x - headingX * RocketBody;
                var tailY = y - headingY * RocketBody - ArcLift(fromX, fromY, toX, toY, tailX, y - headingY * RocketBody);
                fx.Dot(x, y, 4, Rocket, 0.25);
                fx.Line(tailX - headingX * 0.2, tailY - headingY * 0.2, tailX, tailY, 3, Flame);
                fx.Line(tailX, tailY, x, head, 3, Rocket);
            }
            else
            {
                // Ядро летит быстро и прямо, низко над землёй.
                fx.Dot(x, y, 4, Shell);
                fx.Dot(x - point, y - point, 2, Rocket);
            }
        }
    }

    /// <summary>Мгновенные выстрелы: струя огня, лазер, цепной разряд; left — сколько им осталось.</summary>
    private void Instant(uint entity, WeaponSpec weapon, double fromX, double fromY, double toX, double toY, double left, double time)
    {
        var dx = toX - fromX;
        var dy = toY - fromY;
        if (weapon.Shot == "flame")
        {
            // Струя огня: широкая у цели, узкая у ствола, и языки, которые дрожат.
            var seed = entity * 7.0 + Math.Floor(time * ArcRate);
            fx.Line(fromX, fromY, toX, toY, 5, Flame, left * 0.6);
            for (var i = 1; i <= FlameTongues; i++)
            {
                var share = (double)i / FlameTongues;
                var sway = (Fx.Noise(seed, i) - 0.5) * 0.5 * share;
                fx.Dot(fromX + dx * share - dy * sway, fromY + dy * share + dx * sway, 3 + share * 6, i % 2 == 1 ? Flame : Ember, left);
            }
            return;
        }
        if (weapon.Shot == "laser")
        {
            fx.Line(fromX, fromY, toX, toY, 6, Laser, left * 0.7);
            fx.Line(fromX, fromY, toX, toY, 2, Core, left);
            fx.Dot(toX, toY, 8, Laser, left);
            return;
        }
        // Разряд — ломаная, которая дрожит: изломы меняются много раз в секунду.
        var length = Fx.Hypot(dx, dy);
        if (length == 0)
            length = 1;
        var joints = Math.Max(2, (int)Math.Ceiling(length / ArcStep));
        var arcSeed = entity * 13.0 + Math.Floor(time * ArcRate);
        var (lastX, lastY) = (fromX, fromY);
        for (var i = 1; i <= joints; i++)
        {
            var sway = i == joints ? 0 : (Fx.Noise(arcSeed, i) - 0.5) * 2 * ArcSway;
            var x = fromX + dx * i / joints + -dy / length * sway;
            var y = fromY + dy * i / joints + dx / length * sway;
            fx.Line(lastX, lastY, x, y, 5, Spark, left * 0.8);
            fx.Line(lastX, lastY, x, y, 1.5, Core, left);
            (lastX, lastY) = (x, y);
        }
        fx.Dot(toX, toY, 9, Spark, left);
    }

    /// <summary>
    /// Метки: место пуска — янтарное кольцо с волнами, точка удара — кольца взрыва и его ядра (чем ближе удар, тем чаще
    /// мигают), сигнал союзника — зелёное кольцо, к концу гаснет.
    /// </summary>
    public void DrawMarks()
    {
        var c = fx.C;
        var time = fx.Time;
        var blast = fx.Tables.Buildings.GetValueOrDefault("silo")?.Warhead?.Blast;
        foreach (var entity in c.Each(fx.Mark, fx.Position))
        {
            if (c.Get(entity, fx.Mark) is not { } mark || c.Pose(entity) is not { } at)
                continue;
            var (x, y) = (at.X, at.Y);
            var left = Math.Max(0, (mark.Number(fx.Mark["until"]) - fx.Clock) * fx.Step);
            switch (fx.Str(mark.Number(fx.Mark["kind"])))
            {
                case "ping":
                {
                    if (!fx.Visible(x, y))
                        continue;
                    var wave = time * 1.2 % 1;
                    var fade = Math.Min(1, left / 2);
                    Ring(x, y, 0.6, 3, Ping, 0.9 * fade);
                    Ring(x, y, 0.6 + wave * 2.4, 2, Ping, 0.7 * (1 - wave) * fade);
                    break;
                }
                case "launch":
                {
                    if (!fx.Visible(x, y))
                        continue;
                    var wave = time * 0.8 % 1;
                    Ring(x, y, 1.5, 3, LaunchSite, 0.8);
                    Ring(x, y, 1.5 + wave * 4, 2, LaunchSite, 0.6 * (1 - wave));
                    fx.Line(x - 0.8, y, x + 0.8, y, 2, LaunchSite, 0.8);
                    fx.Line(x, y - 0.8, x, y + 0.8, 2, LaunchSite, 0.8);
                    break;
                }
                default:
                {
                    if (blast is null || !fx.Visible(x - blast.Radius, y - blast.Radius, blast.Radius * 2, blast.Radius * 2, 3))
                        continue;
                    var beat = 0.5 + 0.5 * DetMath.Cos(time * Math.PI * 2 * (left < 5 ? 3 : 1));
                    Ring(x, y, blast.Radius, 3, Strike, 0.35 + 0.4 * beat);
                    Ring(x, y, blast.Core, 2, Strike, 0.25 + 0.3 * beat);
                    fx.Dot(x, y, 8, Strike, 0.6 + 0.4 * beat);
                    break;
                }
            }
        }
    }

    /// <summary>Кольцо радиуса r толщиной в точках эффектов.</summary>
    private void Ring(double x, double y, double radius, double thickness, Rgb color, double level)
    {
        if (level <= 0.004)
            return;
        fx.C.Draw.Circle(x, y, radius, color.Hex, (float)(fx.Sized(thickness) / fx.Pixel), (float)Math.Min(1, level));
    }
}
