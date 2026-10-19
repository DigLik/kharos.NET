using System.Diagnostics;
using Base.Shared;
using Base.Shared.Orders;
using Kharos.Base.Scenario;
using Kharos.Engine.Terrain;
using static Kharos.Balance.Report;

namespace Kharos.Balance;

/// <summary>
/// Раздел «движение» (tools/harness/movement.ts): как юниты ездят строем, расходятся, окружают цель и подъезжают к стройке.
/// formation — марш строем, встречный обмен и атака толпой; surround — отряд атакует одну цель; builders — строители едут на
/// стройку; load — время тика на больших армиях; crossing — армия через карту по диагонали на нескольких картах.
/// </summary>
public static class Movement
{
    private const uint Training = Sim.TrainingPlayer;

    /// <summary>
    /// Левый верхний тайл площадки width×height, где наземная техника едет хотя бы на 85% скорости: без болота. На болоте
    /// техника вязнет впятеро, и замер мерил бы болото, а не движение.
    /// </summary>
    private static (int X, int Y) DryArena(Sim sim, int width, int height)
    {
        for (var y = -300; y < 300; y += 3)
            for (var x = -300; x < 300; x += 3)
            {
                var fits = true;
                for (var dy = 0; dy < height && fits; dy++)
                    for (var dx = 0; dx < width && fits; dx++)
                        fits = Lib.IsWalkable(sim, x + dx, y + dy) && sim.TerrainSpeed("lancer", x + dx, y + dy) >= 0.85;
                if (fits)
                    return (x, y);
            }
        throw new InvalidOperationException($"Не нашлось сухой площадки {width}×{height}");
    }

    /// <summary>JS Math.round для неотрицательных и отрицательных.</summary>
    private static int JsRound(double value) => (int)Math.Floor(value + 0.5);

    /// <summary>Ставит юнитов прямоугольником cols в ряд через gap тайлов от (x, y).</summary>
    private static List<uint> Block(Sim sim, string type, uint player, int count, double x, double y, int cols, double gap = 1.5)
    {
        var units = new List<uint>();
        for (var i = 0; i < count; i++)
        {
            var (tx, ty) = (JsRound(x + i % cols * gap), JsRound(y + i / cols * gap));
            // В TS юнит встаёт куда сказано; правка .NET не ставит на занятый и непроходимый тайл — тогда ближайший свободный.
            foreach (var tile in Lib.Rings(tx, ty, 4))
            {
                if (!Lib.IsWalkable(sim, tile.X, tile.Y) || sim.OccupantAt(tile.X, tile.Y) != 0)
                    continue;
                try
                {
                    units.Add(sim.PutUnit(type, player, tile.X + 0.5, tile.Y + 0.5));
                    break;
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
        return units;
    }

    /// <summary>Кто выпустил выстрел в этот тик: у нового выстрела возраст ноль. Источник — носитель, а не турель.</summary>
    private static HashSet<uint> ShootersNow(Sim sim)
    {
        var sources = new HashSet<uint>();
        foreach (var shot in sim.With(Names.Shot.Name))
        {
            if (sim.Num(shot, Names.Shot.Name, Names.Shot.Age) != 0)
                continue;
            var source = (uint)sim.Num(shot, Names.Shot.Name, Names.Shot.Source);
            if (source == 0 || !sim.Alive(source))
                continue;
            sources.Add(sim.Has(source, Names.Mount.Name) ? (uint)sim.Num(source, "Mount.parent") : source);
        }
        return sources;
    }

    /// <summary>Как юнит ехал: сколько тиков стоял с приказом, сколько крутился на месте, сколько раз сменил сторону поворота.</summary>
    private sealed class Track
    {
        public int Stood, Spun, Reversals;
        public double Turned, LastTurn, Arrived = -1;
    }

    private static double Facing(Sim sim, uint entity) => sim.Num(entity, Names.Facing.Name, Names.Facing.Angle);

    /// <summary>Крутит симуляцию до limit секунд или пока done, следя за юнитами units.</summary>
    private static (List<Track> Tracks, double Time) Follow(Sim sim, List<uint> units, double limit, Func<bool>? done = null, Action? each = null)
    {
        done ??= () => units.All(entity => !sim.Has(entity, Names.Path.Name));
        var tracks = units.ToDictionary(entity => entity, _ => new Track());
        var tick = 0;
        for (; tick < limit / Lib.Tick && !done(); tick++)
        {
            var before = units.Select(entity => sim.Alive(entity) && sim.Has(entity, Names.Position.Name)
                ? (Entity: entity, X: sim.At(entity).X, Y: sim.At(entity).Y, Face: Facing(sim, entity), Moving: sim.Has(entity, Names.Path.Name), Alive: true)
                : default).ToList();
            sim.Advance();
            each?.Invoke();
            foreach (var was in before)
            {
                if (!was.Alive || !sim.Alive(was.Entity) || !sim.Has(was.Entity, Names.Position.Name))
                    continue;
                var (px, py) = sim.At(was.Entity);
                var track = tracks[was.Entity];
                var moved = Math.Sqrt((px - was.X) * (px - was.X) + (py - was.Y) * (py - was.Y));
                var face = Facing(sim, was.Entity);
                var turn = Math.Atan2(Math.Sin(face - was.Face), Math.Cos(face - was.Face));
                track.Turned += Math.Abs(turn);
                if (was.Moving && moved < 1e-4)
                {
                    if (Math.Abs(turn) < 1e-4)
                        track.Stood++;
                    else
                        track.Spun++;
                }
                var side = Math.Abs(turn) > 1e-3 ? Math.Sign(turn) : 0;
                if (side != 0 && track.LastTurn != 0 && side != track.LastTurn)
                    track.Reversals++;
                if (side != 0)
                    track.LastTurn = side;
                if (was.Moving && !sim.Has(was.Entity, Names.Path.Name) && track.Arrived < 0)
                    track.Arrived = (tick + 1) * Lib.Tick;
            }
        }
        return ([.. tracks.Values], tick * Lib.Tick);
    }

    /// <summary>Строка таблицы строя: средние по юнитам.</summary>
    private static object[] FormationRow(string scene, string type, (List<Track> Tracks, double Time) result)
    {
        var (tracks, time) = result;
        double Mean(Func<Track, double> value) => Round(tracks.Sum(value) / tracks.Count, 1);
        return [scene, type, Round(time, 1), Mean(track => track.Stood * Lib.Tick), Mean(track => track.Spun * Lib.Tick), Mean(track => track.Reversals), Mean(track => track.Turned)];
    }

    /// <summary>Строй: марш, встречный обмен и атака толпой — для каждого из типичных видов.</summary>
    public static void PrintFormation()
    {
        Log("\nСтрой на сухом ровном поле: средние на юнит. Поворот по прямой в чистом поле — почти ноль; всё сверх — вихляние");
        // Все сцены (вид и сцена) — разом.
        var scenes = (
            from type in new[] { "infantry", "lancer", "tank" }
            from scene in Enumerable.Range(0, 3)
            select (Type: type, Scene: scene)).ToList();
        var rows = Work.Map(scenes, item =>
        {
            var type = item.Type;
            if (item.Scene == 0)
            {
                Progress($"  строй: {type}");
                // Марш строем 4×4 на ~38 тайлов.
                using var sim = Lib.NewSim();
                var at = DryArena(sim, 60, 30);
                var units = Block(sim, type, 1, 16, at.X + 2, at.Y + 10, 4);
                sim.Now(1, new MoveOrder { Units = [.. units], X = at.X + 40, Y = at.Y + 12 });
                return FormationRow("марш 16", type, Follow(sim, units, 60));
            }
            if (item.Scene == 1)
            {
                // Две группы по 12 меняются местами лоб в лоб.
                using var sim = Lib.NewSim();
                var at = DryArena(sim, 60, 30);
                var left = Block(sim, type, 1, 12, at.X + 2, at.Y + 8, 3);
                var right = Block(sim, type, 1, 12, at.X + 50, at.Y + 8, 3);
                sim.Now(1, new MoveOrder { Units = [.. left], X = at.X + 52, Y = at.Y + 10 });
                sim.Now(1, new MoveOrder { Units = [.. right], X = at.X + 4, Y = at.Y + 10 });
                return FormationRow("встречные 12+12", type, Follow(sim, [.. left, .. right], 90));
            }
            {
                // 12 атакуют одну цель среди десятка смирных пехотинцев.
                using var sim = Lib.NewSim(players: [1u, Training]);
                var at = DryArena(sim, 60, 30);
                var own = Block(sim, type, 1, 12, at.X + 2, at.Y + 8, 4);
                var foes = Block(sim, "infantry", Training, 10, at.X + 40, at.Y + 9, 5, 1);
                sim.Now(Training, new StanceOrder { Units = [.. foes], Stance = "passive" });
                sim.Now(1, new AttackOrder { Units = [.. own], Target = foes[2] });
                return FormationRow("атака 12", type, Follow(sim, own, 30, () => false));
            }
        });
        Table(["сцена", "вид", "время, с", "стоял, с", "крутился", "смен пов.", "поворот"], rows, 16, 10);
    }

    /// <summary>Один отряд атакует одну цель, которая не умирает и не отвечает. shift сдвигает цель: другая расстановка.</summary>
    private static (int Lately, double All, double Arc) Besiege(string type, int count, int cols, int shift)
    {
        using var sim = Lib.NewSim(players: [1u, Training]);
        var at = DryArena(sim, 50, 34);
        var own = Block(sim, type, 1, count, at.X + 2, at.Y + 12, cols);
        var target = sim.PutUnit("tank", Training, at.X + 32 - Math.Abs(shift) + 0.5, at.Y + 17 + shift + 0.5);
        sim.Now(Training, new StanceOrder { Units = [target], Stance = "passive" });
        sim.Now(1, new AttackOrder { Units = [.. own], Target = target });
        const double limit = 25;
        // Стреляли ли за последние 10 секунд: бой уже устоялся.
        var lately = new HashSet<uint>();
        var ever = new HashSet<uint>();
        double all = -1;
        for (var tick = 0; tick < limit / Lib.Tick; tick++)
        {
            sim.Advance();
            sim.Set(target, "Health.value", 1);
            foreach (var source in ShootersNow(sim))
            {
                ever.Add(source);
                if (tick * Lib.Tick > limit - 10)
                    lately.Add(source);
            }
            if (all < 0 && own.All(ever.Contains))
                all = tick * Lib.Tick;
        }
        // Дуга — сколько градусов круга вокруг цели занимают нападающие: круг минус самый большой пустой промежуток.
        var (cx, cy) = sim.At(target);
        var angles = own.Select(entity =>
        {
            var (px, py) = sim.At(entity);
            return Math.Atan2(py - cy, px - cx);
        }).Order().ToList();
        double widest = 0;
        for (var i = 0; i < angles.Count; i++)
        {
            var gap = (angles[(i + 1) % angles.Count] - angles[i] + 2 * Math.PI) % (2 * Math.PI);
            widest = Math.Max(widest, gap == 0 ? 2 * Math.PI : gap);
        }
        return (own.Count(lately.Contains), all, (2 * Math.PI - widest) * 180 / Math.PI);
    }

    /// <summary>Окружение: каждый отряд — в трёх расстановках.</summary>
    public static void PrintSurround()
    {
        Log("\nОкружение: отряд атакует одну цель — неубиваемый смирный танк; по три расстановки, через «/»");
        var rows = new List<object[]>();
        var fired = 0;
        var total = 0;
        (string Type, int Count, int Cols)[] squads = [("lancer", 12, 4), ("tank", 10, 4), ("infantry", 20, 5), ("flamer", 16, 4), ("tesla", 8, 4), ("artillery", 8, 4), ("buggy", 12, 4)];
        int[] shifts = [-8, 0, 9];
        // Все расстановки всех отрядов — разом.
        var sieges = Work.Map([.. squads.SelectMany(squad => shifts.Select(shift => (Squad: squad, Shift: shift)))], item =>
        {
            if (item.Shift == shifts[0])
                Progress($"  окружение: {item.Squad.Type}");
            return Besiege(item.Squad.Type, item.Squad.Count, item.Squad.Cols, item.Shift);
        });
        for (var s = 0; s < squads.Length; s++)
        {
            var (type, count, _) = squads[s];
            var results = sieges.Skip(s * shifts.Length).Take(shifts.Length).ToList();
            var lately = results.Sum(result => result.Lately);
            fired += lately;
            total += count * results.Count;
            rows.Add([
                $"{count} {type}",
                $"{lately}/{count * results.Count}",
                string.Join('/', results.Select(result => result.All < 0 ? "—" : Num(JsRound(result.All)))),
                string.Join('/', results.Select(result => Num(JsRound(result.Arc))))]);
        }
        Table(["отряд", "стреляли", "все, с", "дуга, °"], rows, 14, 16);
        Log($"  «стреляли» — за последние 10 с из 25; «все» — когда выстрелил весь отряд, «—» — так и не выстрелил. Итого {fired}/{total}");
    }

    /// <summary>База игрока 1 на скале: развёрнутое главное здание; стартовые строители убраны. И место под стройку.</summary>
    private static (Sim Sim, int SiteX, int SiteY) Base()
    {
        var sim = Lib.NewSim();
        bool Rock(int x, int y)
        {
            for (var tileY = y; tileY < y + 12; tileY++)
                for (var tileX = x; tileX < x + 12; tileX++)
                    if (sim.Land!.TerrainAt(tileX, tileY) != TerrainType.Rock || sim.Land.IsCliffFoot(tileX + 0.5, tileY + 0.5))
                        return false;
            return true;
        }
        for (var y = 0; y < 400; y++)
            for (var x = 0; x < 400; x++)
            {
                if (!Rock(x, y))
                    continue;
                sim.Edit(new EditStartOrder { Player = 1, X = x + 2, Y = y + 2 });
                foreach (var unit in sim.Of(1, Names.Unit.Name, Names.Proto.Name).Where(entity => Lib.TypeOf(sim, entity) == "mcv"))
                    sim.Send(1, new DeployOrder { Unit = unit });
                sim.Seconds(6);
                var builders = sim.Of(1, Names.Unit.Name, Names.Proto.Name).Where(entity => Lib.TypeOf(sim, entity) == "builder").ToArray();
                if (builders.Length > 0)
                    sim.Erase(builders);
                return (sim, x + 7, y + 4);
            }
        sim.Dispose();
        throw new InvalidOperationException("Не нашлось места под базу");
    }

    /// <summary>Ближайшее к (x, y) место появления: кольцами наружу с шагом в четыре тайла; не нашлось — сама точка (openSpawn).</summary>
    private static (int X, int Y) OpenSpawn(Sim sim, int x, int y, int radius = 64)
    {
        const int step = 4;
        for (var ring = 0; ring <= radius; ring += step)
            for (var dy = -ring; dy <= ring; dy += step)
                for (var dx = -ring; dx <= ring; dx += step)
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == ring && Lib.IsWalkable(sim, x + dx, y + dy))
                        return (x + dx, y + dy);
        return (x, y);
    }

    /// <summary>Строители: откуда едут — смещения от площадки — и что из этого вышло.</summary>
    public static void PrintBuilders()
    {
        Log("\nСтроители едут на стройку генератора (2×2); луч строителя — 5 тайлов. «минимум» — сколько ехать до края луча");
        const string type = "generator";
        var (width, height) = Lib.SizeOf(type);
        (string Scene, (int Dx, int Dy)[] Offsets)[] scenes =
        [
            ("один издалека", [(-12, 0)]),
            ("четверо кучкой", [(-12, 0), (-12, 2), (-14, 0), (-14, 2)]),
            ("уже в радиусе", [(-4, 0), (6, 1)]),
            ("на площадке", [(0, 0), (1, 1)]),
            ("с четырёх сторон", [(-12, 0), (13, 1), (1, -12), (1, 13)]),
        ];
        // Все сцены — разом.
        var rows = Work.Map(scenes, item =>
        {
            var (scene, offsets) = item;
            Progress($"  строители: {scene}");
            var (sim, siteX, siteY) = Base();
            using var _ = sim;
            double Edge(double x, double y) => Math.Sqrt(Math.Pow(Math.Max(Math.Max(siteX - x, x - siteX - width), 0), 2) + Math.Pow(Math.Max(Math.Max(siteY - y, y - siteY - height), 0), 2));
            var builders = offsets.Select(offset =>
            {
                var spot = OpenSpawn(sim, siteX + offset.Dx, siteY + offset.Dy, 4);
                return sim.PutUnit("builder", 1, spot.X + 0.5, spot.Y + 0.5);
            }).ToList();
            var from = builders.Select(sim.At).ToList();
            sim.Now(1, new BuildOrder { Building = type, X = siteX, Y = siteY, Builders = [.. builders] });
            var site = sim.SiteAt(siteX, siteY);
            if (site == 0)
                throw new InvalidOperationException($"Стройка не заложилась: {scene}");
            var started = new Dictionary<uint, double>();
            var driven = builders.ToDictionary(entity => entity, _ => 0.0);
            var last = builders.Select(sim.At).ToList();
            for (var tick = 0; tick < 60 / Lib.Tick && started.Count < builders.Count; tick++)
            {
                sim.Advance();
                for (var i = 0; i < builders.Count; i++)
                {
                    var position = sim.At(builders[i]);
                    driven[builders[i]] += Math.Sqrt(Math.Pow(position.X - last[i].X, 2) + Math.Pow(position.Y - last[i].Y, 2));
                    last[i] = position;
                    if (!started.ContainsKey(builders[i]) && (uint)sim.Num(builders[i], Names.Repair.Name, Names.Repair.Target) == site)
                        started[builders[i]] = (tick + 1) * Lib.Tick;
                }
            }
            var lines = new List<object[]>();
            for (var i = 0; i < builders.Count; i++)
            {
                var position = sim.At(builders[i]);
                lines.Add([
                    i != 0 ? "" : scene,
                    Round(Edge(from[i].X, from[i].Y), 1),
                    started.TryGetValue(builders[i], out var begun) ? Round(begun, 1) : "—",
                    Round(driven[builders[i]], 1),
                    Round(Math.Max(0, Edge(from[i].X, from[i].Y) - 5), 1),
                    Round(Edge(position.X, position.Y), 1)]);
            }
            return lines;
        });
        Table(["сцена", "до края", "работает", "проехал", "минимум", "встал от"], rows.SelectMany(lines => lines), 18, 10);
    }

    /// <summary>Гоняет ticks тиков: среднее и худшее время тика в мс. Поиск пути движка в .NET не считается (в TS — тайлов за тик).</summary>
    private static (double Mean, double Worst, double Search) Measure(Sim sim, int ticks, Func<int, bool>? each = null)
    {
        double total = 0, worst = 0;
        var tick = 0;
        for (; tick < ticks; tick++)
        {
            if (each is not null && !each(tick))
                break;
            var start = Stopwatch.GetTimestamp();
            sim.Advance();
            var spent = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            total += spent;
            worst = Math.Max(worst, spent);
        }
        return (Round(total / Math.Max(tick, 1)), Round(worst, 1), 0);
    }

    /// <summary>
    /// Нагрузка: время тика на больших армиях. Числа зависят от машины и в сверке с эталоном не участвуют; «поиск» (тайлы поиска
    /// пути за тик) у .NET не считается — всегда 0.
    /// </summary>
    public static void PrintLoad()
    {
        Log("\nНагрузка: время тика в мс (бюджет — 50) и тайлов поиска пути за тик");
        // Сцены — разом: марши, 150 на одну цель, бой 100 на 100.
        (string Name, Func<object[]> Run)[] scenes =
            [("марш 200", () => March(200)), ("марш 1000", () => March(1000)), ("марш 2000", () => March(2000)), ("150 на одну цель", Crowd), ("бой 100 на 100", Battle)];
        var rows = Work.Map(scenes.Select(scene => new Named<object[]>(scene.Name, scene.Run)).ToList(), scene => scene.Run());
        Table(["сцена", "стоят", "идут", "худший", "поиск"], rows, 22, 9);

        static object[] March(int count)
        {
            Progress($"  нагрузка: марш {count} лансеров");
            // Армия лансеров стоит, потом идёт строем на 40 тайлов дальше своего края.
            using var sim = Lib.NewSim();
            var side = (int)Math.Ceiling(Math.Sqrt(count));
            (int X, int Y)? at = null;
            for (var y = -400; y < 400 && at is null; y += 4)
                for (var x = -400; x < 400; x += 4)
                {
                    var open = true;
                    for (var dy = 0; dy < side * 1.5 + 4 && open; dy += 2)
                        for (var dx = 0; dx < side * 1.5 + 60 && open; dx += 2)
                            open = Lib.IsWalkable(sim, x + dx, y + dy);
                    if (!open)
                        continue;
                    at = (x, y);
                    break;
                }
            if (at is not { } start)
                throw new InvalidOperationException($"Не нашлось места под армию в {count}");
            var units = Block(sim, "lancer", 1, count, start.X, start.Y, side);
            sim.Seconds(1);
            var idle = Measure(sim, 100);
            sim.Now(1, new MoveOrder { Units = [.. units], X = JsRound(start.X + side * 1.5 + 40), Y = JsRound(start.Y + side * 0.75) });
            var moving = Measure(sim, 200);
            return [$"марш {count} лансеров", idle.Mean, moving.Mean, moving.Worst, moving.Search];
        }

        static object[] Crowd()
        {
            // Полторы сотни разных юнитов на одну цель: худший случай для мест вокруг цели.
            Progress("  нагрузка: 150 на одну цель");
            using var sim = Lib.NewSim(players: [1u, Training]);
            var at = DryArena(sim, 60, 40);
            string[] types = ["infantry", "lancer", "tank", "flamer", "buggy"];
            var own = Enumerable.Range(0, 150).Select(i => sim.PutUnit(types[i % types.Length], 1, at.X + 2 + i % 10 * 2 + 0.5, at.Y + 5 + i / 10 * 2 + 0.5)).ToList();
            var target = sim.PutUnit("tank", Training, at.X + 50 + 0.5, at.Y + 20 + 0.5);
            sim.Now(Training, new StanceOrder { Units = [target], Stance = "passive" });
            sim.Now(1, new AttackOrder { Units = [.. own], Target = target });
            var result = Measure(sim, (int)(30 / Lib.Tick), _ =>
            {
                sim.Set(target, "Health.value", 1);
                return true;
            });
            return ["150 на одну цель", "", result.Mean, result.Worst, result.Search];
        }

        static object[] Battle()
        {
            // Бой двух армий по сотне: как в показательном бою, приказы раз в четверть секунды.
            Progress("  нагрузка: бой 100 на 100");
            using var sim = Lib.NewSim(players: [1u, Training]);
            var at = DryArena(sim, 60, 40);
            string[] kinds = ["infantry", "lancer", "tank", "flamer", "buggy", "rocketeer", "artillery"];
            var army = Enumerable.Range(0, 100).Select(i => kinds[i % 7]).ToList();
            sim.SpawnBattle(1, at.X + 30, at.Y + 20, army, army, 15);
            var result = Measure(sim, (int)(60 / Lib.Tick), tick => tick % 5 != 0 || sim.DriveBattle(1));
            return ["бой 100 на 100", "", result.Mean, result.Worst, result.Search];
        }
    }

    /// <summary>Состав армии перехода: пехота, техника всех весов и немного авиации.</summary>
    private static readonly (string Type, int Count)[] CrossingArmy =
    [
        ("infantry", 50), ("flamer", 20), ("rocketeer", 20), ("buggy", 25), ("lancer", 30), ("flak", 15), ("tank", 25),
        ("artillery", 15), ("tesla", 10), ("carrier", 8), ("mcv", 2), ("drone", 10), ("gunship", 6),
    ];

    /// <summary>Ближе стольких тайлов к цели приказ, кончившийся у юнита, считается выполненным.</summary>
    private const double Arrived = 25;

    /// <summary>Сколько минут игры отводится переходу.</summary>
    private const int CrossingLimit = 20;

    private sealed class Walker
    {
        public double Arrived = -1, X, Y;
        public int Still, Longest;
        public bool Moving = true;
        public string Type = "";
    }

    /// <summary>Переход через карту 512×512 по диагонали на одной карте; seed — зерно карты, null — по умолчанию.</summary>
    private static object[][] Cross(int? seed)
    {
        using var sim = Sim.Create(new SimOptions { Size = 256, Land = seed is { } s ? Lib.LandWithSeed(s) : "default", Players = [], Rules = new { protection = 0 } });
        sim.EnsurePlayer(1);
        const int half = 256;
        var total = CrossingArmy.Sum(item => item.Count);
        (int X, int Y) Spot(int x, int y)
        {
            for (var ring = 0; ring < 60; ring++)
                for (var dy = -ring; dy <= ring; dy++)
                    for (var dx = -ring; dx <= ring; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != ring)
                            continue;
                        if (Lib.IsWalkable(sim, x + dx, y + dy) && Builders.FreeTilesNear(sim, x + dx, y + dy, total).Count >= total)
                            return (x + dx, y + dy);
                    }
            throw new InvalidOperationException("Не нашлось места под армию");
        }
        var start = Spot(-half + 40, -half + 40);
        var goal = Spot(half - 40, half - 40);
        var tiles = Builders.FreeTilesNear(sim, start.X, start.Y, total);
        var units = new List<uint>();
        var walkers = new Dictionary<uint, Walker>();
        foreach (var (type, count) in CrossingArmy)
            for (var i = 0; i < count; i++)
            {
                var tile = tiles[Math.Min(units.Count, tiles.Count - 1)];
                var entity = sim.PutUnit(type, 1, tile.X + 0.5, tile.Y + 0.5);
                units.Add(entity);
                walkers[entity] = new Walker { Type = type };
            }
        sim.Now(1, new MoveOrder { Units = [.. units], X = goal.X, Y = goal.Y });
        var began = Stopwatch.GetTimestamp();
        double worst = 0;
        var tick = 0;
        for (; tick < CrossingLimit * 60 / Lib.Tick; tick++)
        {
            foreach (var entity in units)
                (walkers[entity].X, walkers[entity].Y) = sim.At(entity);
            var before = Stopwatch.GetTimestamp();
            sim.Advance();
            worst = Math.Max(worst, Stopwatch.GetElapsedTime(before).TotalMilliseconds);
            var moving = 0;
            foreach (var entity in units)
            {
                var unit = walkers[entity];
                var (px, py) = sim.At(entity);
                var now = sim.Has(entity, Names.Path.Name);
                if (now)
                {
                    moving++;
                    unit.Still = Math.Sqrt((px - unit.X) * (px - unit.X) + (py - unit.Y) * (py - unit.Y)) < 0.01 ? unit.Still + 1 : 0;
                    unit.Longest = Math.Max(unit.Longest, unit.Still);
                }
                if (unit.Moving && !now && unit.Arrived < 0 && Math.Sqrt((px - goal.X) * (px - goal.X) + (py - goal.Y) * (py - goal.Y)) <= Arrived)
                    unit.Arrived = tick * Lib.Tick;
                unit.Moving = now;
            }
            if (moving == 0)
                break;
        }
        var spent = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
        var stuck = units.Where(entity => walkers[entity].Arrived < 0).ToList();
        var longest = units.Aggregate((best, entity) => walkers[entity].Longest > walkers[best].Longest ? entity : best);
        var rows = new List<object[]>
        {
            new object[]
            {
                seed is { } value ? Num(value) : "обычная", $"{units.Count - stuck.Count}/{units.Count}", Num(JsRound(units.Max(entity => walkers[entity].Arrived))),
                $"{Num(Round(walkers[longest].Longest * Lib.Tick, 1))} {walkers[longest].Type}", $"×{Num(JsRound(tick * Lib.Tick * 1000 / spent))}", Round(worst, 1), 0,
            },
        };
        foreach (var entity in stuck)
            rows.Add(["", $"не доехал: {walkers[entity].Type}", "", "", "", "", ""]);
        return [.. rows];
    }

    /// <summary>Переход армии через карту на нескольких картах. Долгий; эталона TS в Balance/golden нет.</summary>
    public static void PrintCrossing()
    {
        Log($"\nПереход: {CrossingArmy.Sum(item => item.Count)} разных юнитов через карту 512×512 по диагонали (~611 тайлов), до {CrossingLimit} минут игры");
        // KHAROS_BALANCE_ONLY=3 — только карта с этим зерном (обычная — «обычная»), для быстрой проверки. Карты — разом.
        static string NameOf(int? seed) => seed is { } value ? Num(value) : "обычная";
        var only = Environment.GetEnvironmentVariable("KHAROS_BALANCE_ONLY");
        var seeds = new int?[] { null, 1, 2, 3, 4, 5, 6 }.Where(seed => only is not { Length: > 0 } || only == NameOf(seed)).ToList();
        var rows = Work.Map(seeds, seed =>
        {
            Progress($"  переход: карта {NameOf(seed)}");
            return Cross(seed);
        });
        Table(["карта", "доехали", "последний", "дольше всех стоял", "скорость", "худший", "поиск"], rows.SelectMany(map => map), 10, 13);
        Log("  «последний» — когда доехал последний, с; «дольше всех стоял» — подряд с приказом, с; «худший» — тик, мс");
    }

    /// <summary>Все замеры движения, кроме долгого перехода.</summary>
    public static void Run()
    {
        // Куски — разом. Время тика в «нагрузке» под общей нагрузкой машины выше, чем в одиночку (с эталоном не сверяется).
        Report.Parallel(PrintFormation, PrintSurround, PrintBuilders, PrintLoad);
    }
}
