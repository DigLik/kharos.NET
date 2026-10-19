using System.Text.Json;
using Base.Shared;
using Base.Shared.Orders;
using Base.Shared.Rules;
using Kharos.Client.Game;
using Kharos.Engine.Layers;
using Kharos.Gpu;
using Kharos.Plugins.Game;

namespace Kharos.Base.Scenario;

/// <summary>Сущность сцены сверки: здание (левый верхний тайл основания) или юнит (тайл), вид и владелец.</summary>
public sealed record SceneEntity(string Kind, string Type, int X, int Y, uint Owner);

/// <summary>
/// Сцена сверки кадров (docs/dotnet-n6.md, Н6.4, уровень 2) — то же, что SceneSpec в dotnet/tools/golden/frames/scenes.ts;
/// читается из Kharos.Client.Tests/Frames/golden/scenes.json.
/// </summary>
public sealed record FrameScene(
    string Name, string Description, int Seed, int Size, bool Fog, uint Player, int Width, int Height,
    (double X, double Y, double Zoom) Camera, (double Hour, float Light, float WindX, float WindY, float Precipitation) Weather,
    double Time, IReadOnlyList<(uint Player, double Amount)> Credits, IReadOnlyList<uint> Research, IReadOnlyList<SceneEntity> Entities,
    IReadOnlyList<(uint A, uint B)> Allies, IReadOnlyList<(uint Player, JsonElement Command)> Commands, int AdvanceTicks,
    IReadOnlyList<int> Selected, bool Hud);

/// <summary>
/// Сборка сцен сверки на Sim и Viewer и кадр без окна (Kharos.Gpu). Порядок — как в шапке frames/scenes.ts: кредиты
/// (заводят счёт) и исследования; сущности по порядку (номер в списке — индекс для commands и selected); союзы в обе
/// стороны; команды (индексы → номера сущностей); advanceTicks тиков; кадр с камерой, погодой и часами сцены. Расхождения
/// рисунка не правятся здесь — это Д1; см. <see cref="Notes"/> о том, что пришлось обойти.
/// </summary>
public static class ViewerScenes
{
    /// <summary>Эталоны кадров TS и scenes.json.</summary>
    public static string GoldenDirectory => Path.Combine(Sim.DotnetRoot, "tests", "Kharos.Client.Tests", "Frames", "golden");

    /// <summary>Все сцены из scenes.json; нет файла — пусто.</summary>
    public static IReadOnlyList<FrameScene> Load()
    {
        var path = Path.Combine(GoldenDirectory, "scenes.json");
        if (!File.Exists(path))
            return [];
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return [.. document.RootElement.GetProperty("scenes").EnumerateArray().Select(Parse)];
    }

    /// <summary>Сцена по имени.</summary>
    public static FrameScene Find(string name) => Load().FirstOrDefault(scene => scene.Name == name) ?? throw new KeyNotFoundException($"нет сцены {name} в scenes.json");

    private static FrameScene Parse(JsonElement s)
    {
        var camera = s.GetProperty("camera");
        var weather = s.GetProperty("weather");
        return new FrameScene(
            s.GetProperty("name").GetString()!, s.GetProperty("description").GetString() ?? "", s.GetProperty("seed").GetInt32(), s.GetProperty("size").GetInt32(),
            s.GetProperty("fog").GetBoolean(), s.GetProperty("player").GetUInt32(), s.GetProperty("width").GetInt32(), s.GetProperty("height").GetInt32(),
            (camera.GetProperty("x").GetDouble(), camera.GetProperty("y").GetDouble(), camera.GetProperty("zoom").GetDouble()),
            (weather.GetProperty("hour").GetDouble(), weather.GetProperty("light").GetSingle(), weather.GetProperty("windX").GetSingle(),
                weather.GetProperty("windY").GetSingle(), weather.GetProperty("precipitation").GetSingle()),
            s.GetProperty("time").GetDouble(),
            [.. s.GetProperty("credits").EnumerateArray().Select(c => (c.GetProperty("player").GetUInt32(), c.GetProperty("amount").GetDouble()))],
            [.. s.GetProperty("research").EnumerateArray().Select(r => r.GetUInt32())],
            [.. s.GetProperty("entities").EnumerateArray().Select(e => new SceneEntity(e.GetProperty("kind").GetString()!, e.GetProperty("type").GetString()!,
                e.GetProperty("x").GetInt32(), e.GetProperty("y").GetInt32(), e.GetProperty("owner").GetUInt32()))],
            [.. s.GetProperty("allies").EnumerateArray().Select(a => (a[0].GetUInt32(), a[1].GetUInt32()))],
            [.. s.GetProperty("commands").EnumerateArray().Select(c => (c.GetProperty("player").GetUInt32(), c.GetProperty("command").Clone()))],
            s.GetProperty("advanceTicks").GetInt32(),
            [.. s.GetProperty("selected").EnumerateArray().Select(i => i.GetInt32())],
            s.GetProperty("hud").GetBoolean());
    }

    /// <summary>Что сборка сцен обошла (по сцене): здания на песке — на фундаменте, неизвестные команды и т. п.</summary>
    public static Dictionary<string, List<string>> Notes { get; } = [];

    /// <summary>
    /// Мир сцены и зритель её игрока (камера, погода и часы — сцены). Карта: генератор default (зерно 1337 — как
    /// DEFAULT_CONFIG), сторона size TS = 2 × Size .NET (boundsOf: середина в начале координат).
    /// </summary>
    public static (Sim Sim, Viewer Viewer, uint[] Entities) Build(FrameScene scene, ModMode? mode = null)
    {
        var notes = Notes[scene.Name] = [];
        var players = new SortedSet<uint> { scene.Player };
        foreach (var (player, _) in scene.Credits) players.Add(player);
        foreach (var entity in scene.Entities.Where(e => e.Owner != 0)) players.Add(entity.Owner);
        // Земля генератора от места не зависит: карта — не вся (size TS = 2 × Size .NET), а с запасом вокруг сцены, чтобы
        // не строить 1024² тайлов на хосте и клиенте ради кадра 20 × 12 тайлов.
        var reach = scene.Entities.Select(e => Math.Max(Math.Abs(e.X), Math.Abs(e.Y)))
            .Append((int)Math.Ceiling(Math.Max(Math.Abs(scene.Camera.X), Math.Abs(scene.Camera.Y)))).Max();
        var sim = Sim.Create(new SimOptions
        {
            Size = Math.Min(scene.Size / 2, (reach + 64 + 31) / 32 * 32),
            Land = "default",
            Fog = scene.Fog,
            Players = [.. players],
            Mode = mode,
            // Сцена с панелями — партия frames (как test, но с подачей base: панель игрока и выбор щелчком).
            Game = scene.Hud ? "frames" : "test",
            // Час сцены — настройкой партии (weather.startHour, сутки стоят): его видят часы и свет клиента.
            Settings = new { weather = new { startHour = scene.Weather.Hour, dayLength = 0 } },
        });
        try
        {
            foreach (var (player, amount) in scene.Credits)
                sim.SetCredits(player, amount);
            foreach (var player in scene.Research)
                if (sim.Wallet(player) != 0)
                    sim.Learn(player);
            var ids = new uint[scene.Entities.Count];
            for (var i = 0; i < ids.Length; i++)
            {
                var e = scene.Entities[i];
                ids[i] = e.Kind == "building" ? Place(sim, e.Type, e.X, e.Y, e.Owner, notes) : PlaceUnit(sim, e.Type, e.X, e.Y, e.Owner, notes);
            }
            foreach (var (a, b) in scene.Allies)
            {
                sim.Offer(a, b);
                sim.Offer(b, a);
            }
            foreach (var (player, command) in scene.Commands)
                Command(sim, player, command, ids, notes);
            sim.Advance(scene.AdvanceTicks);
            var viewer = sim.Viewer(scene.Player, new ViewerOptions { Width = scene.Width, Height = scene.Height, Mode = mode });
            viewer.Camera = new CameraState(scene.Camera.X, scene.Camera.Y, scene.Camera.Zoom);
            viewer.Atmosphere = new AtmosphereState(scene.Weather.Light, scene.Weather.WindX, scene.Weather.WindY, scene.Weather.Precipitation);
            // Снимок, разбор, потом выделение щелчками (Shift — к выделенному) и кадр с часами сцены.
            viewer.Step(3);
            for (var i = 0; i < scene.Selected.Count; i++)
            {
                var entity = scene.Entities[scene.Selected[i]];
                var (x, y) = sim.At(ids[scene.Selected[i]]);
                // Здание стоит по левому верхнему тайлу: щелчок — в середину основания.
                if (entity.Kind == "building" && Footprint(sim, entity.Type) is var (w, h))
                    (x, y) = (x + w / 2.0, y + h / 2.0);
                // С панелями щелчок может прийтись на них: на время щелчка камера ставит сущность под верхнюю полосу.
                if (scene.Hud)
                {
                    viewer.Camera = new CameraState(x, y + (scene.Height / 2.0 - 100) / scene.Camera.Zoom, scene.Camera.Zoom);
                    viewer.Step();
                }
                viewer.ClickWorld(x, y, shift: i > 0);
            }
            if (scene.Hud)
            {
                viewer.Camera = new CameraState(scene.Camera.X, scene.Camera.Y, scene.Camera.Zoom);
                viewer.Step();
            }
            return (sim, viewer, ids);
        }
        catch
        {
            sim.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Кадр сцены картинкой RGBA8 (сверху вниз) размером сцены на gpu: сборка, последний кадр с замороженными часами
    /// (time сцены) после того, как сглаживание дошло до мест тика; без hud интерфейс снят.
    /// </summary>
    public static byte[] Render(GpuContext gpu, FrameScene scene, ModMode? mode = null)
    {
        var (sim, viewer, _) = Build(scene, mode);
        using (sim)
        {
            // Сглаживание: секунда кадров без тиков хоста — сущности на местах тика (как sim без delta в TS).
            viewer.Step(60);
            var frame = viewer.Frame();
            frame.Time = (float)scene.Time;
            if (!scene.Hud)
                frame.Interface.Clear();
            foreach (var error in viewer.Log)
                Note(scene.Name, $"клиент: {error}");
            // Рендер — один на видеокарту: сборка конвейеров на программном адаптере стоит секунды.
            Renderer renderer;
            lock (Renderers)
                renderer = Renderers.GetValue(gpu, static gpu => new Renderer(gpu));
            return viewer.Render(renderer);
        }
    }

    /// <summary>Рендеры сцен по видеокартам (живут, пока жива видеокарта).</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GpuContext, Renderer> Renderers = [];

    /// <summary>Кадр сцены по имени (для Parity.NetFrame).</summary>
    public static byte[] Render(GpuContext gpu, string name) => Render(gpu, Find(name));

    private static void Note(string scene, string note)
    {
        if (!Notes.TryGetValue(scene, out var list))
            Notes[scene] = list = [];
        list.Add(note);
    }

    /// <summary>
    /// Здание сцены (placeBuilding в TS — без правила места). Правка edit.building проверяет место (скала, не у подножия
    /// обрыва); если здесь не ставит — здание ставится на ближайшее годное место и переносится в (x, y) правкой мира
    /// напрямую, как это делает edit.move, но без проверки (с турелями). Заметка — в <see cref="Notes"/>.
    /// </summary>
    public static uint Place(Sim sim, string type, int x, int y, uint owner, List<string>? notes = null)
    {
        if (TryPut(sim, type, x, y, owner) is { } placed)
            return placed;
        var (w, h) = Footprint(sim, type);
        foreach (var (tx, ty) in Around(x, y, 80))
        {
            if (!Solid(sim, tx, ty, w, h) || TryPut(sim, type, tx, ty, owner) is not { } building)
                continue;
            var (dx, dy) = (x - tx, y - ty);
            var turrets = sim.With(Names.Mount.Name).Where(entity => (uint)sim.Num(entity, Names.Mount.Name, Names.Mount.Parent) == building);
            foreach (var entity in turrets.Prepend(building).ToList())
            {
                var (ex, ey) = sim.At(entity);
                sim.Set(entity, $"{Names.Position.Name}.{Names.Position.X}", ex + dx);
                sim.Set(entity, $"{Names.Position.Name}.{Names.Position.Y}", ey + dy);
            }
            notes?.Add($"{type} ({x}, {y}): правка не ставит его на эту землю — поставлен в ({tx}, {ty}) и перенесён без проверки места");
            return building;
        }
        // Скалы рядом нет (ровный песок): фундамент под основание делает место годным.
        for (var dy = 0; dy < h; dy++)
            for (var dx = 0; dx < w; dx++)
                sim.Lay(PaveRules.Foundation, x + dx, y + dy, owner == 0 ? 1 : owner);
        notes?.Add($"{type} ({x}, {y}): скалы рядом нет — под основание положен фундамент");
        return sim.PutBuilding(type, x, y, owner);
    }

    /// <summary>
    /// Юнит сцены (spawnUnit): в середину тайла; если правка не ставит его здесь (непроходимо), ставится на ближайший
    /// проходимый тайл и переносится напрямую. Заметка — в <see cref="Notes"/>.
    /// </summary>
    public static uint PlaceUnit(Sim sim, string type, int x, int y, uint owner, List<string>? notes = null)
    {
        foreach (var (tx, ty) in Around(x, y, 40).Prepend((x, y)))
        {
            uint unit;
            try
            {
                unit = sim.PutUnit(type, owner, tx + 0.5, ty + 0.5);
            }
            catch (InvalidOperationException)
            {
                continue;
            }
            if ((tx, ty) != (x, y))
            {
                var turrets = sim.With(Names.Mount.Name).Where(entity => (uint)sim.Num(entity, Names.Mount.Name, Names.Mount.Parent) == unit);
                foreach (var entity in turrets.Prepend(unit).ToList())
                {
                    var (ex, ey) = sim.At(entity);
                    sim.Set(entity, $"{Names.Position.Name}.{Names.Position.X}", ex + x - tx);
                    sim.Set(entity, $"{Names.Position.Name}.{Names.Position.Y}", ey + y - ty);
                }
                notes?.Add($"{type} ({x}, {y}): правка не ставит юнит сюда — поставлен в ({tx}, {ty}) и перенесён");
            }
            return unit;
        }
        throw new InvalidOperationException($"{type}: нет места рядом с ({x}, {y})");
    }

    private static uint? TryPut(Sim sim, string type, int x, int y, uint owner)
    {
        try
        {
            return sim.PutBuilding(type, x, y, owner);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Основание на скале и не у подножия обрыва (предварительно, до правки).</summary>
    private static bool Solid(Sim sim, int x, int y, int w, int h)
    {
        if (sim.Land is not { } land)
            return true;
        for (var dy = 0; dy < h; dy++)
            for (var dx = 0; dx < w; dx++)
                if (land.TerrainAt(x + dx, y + dy) != Kharos.Engine.Terrain.TerrainType.Rock || land.IsCliffFoot(x + dx + 0.5, y + dy + 0.5))
                    return false;
        return true;
    }

    /// <summary>Точки кольцами вокруг (x, y) до radius.</summary>
    private static IEnumerable<(int X, int Y)> Around(int x, int y, int radius)
    {
        for (var ring = 1; ring <= radius; ring++)
            for (var dy = -ring; dy <= ring; dy++)
                for (var dx = -ring; dx <= ring; dx++)
                    if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == ring)
                        yield return (x + dx, y + dy);
    }

    /// <summary>Основание вида здания из data/buildings.json.</summary>
    public static (int W, int H) Footprint(Sim sim, string type)
    {
        if (sim.Host.Content.Data("buildings") is { ValueKind: JsonValueKind.Object } buildings && buildings.TryGetProperty(type, out var spec))
            return (spec.TryGetProperty("width", out var w) ? w.GetInt32() : 1, spec.TryGetProperty("height", out var h) ? h.GetInt32() : 1);
        return (1, 1);
    }

    /// <summary>Команда сцены (sim.send): поля units, builders — индексы сущностей, building — индекс (если число) или вид.</summary>
    private static void Command(Sim sim, uint player, JsonElement command, uint[] ids, List<string> notes)
    {
        uint[] Units(string field) => command.TryGetProperty(field, out var list) ? [.. list.EnumerateArray().Select(i => ids[i.GetInt32()])] : [];
        int Int(string field) => command.GetProperty(field).GetInt32();
        switch (command.GetProperty("type").GetString())
        {
            case "move":
                sim.Send(player, new MoveOrder { Units = Units("units"), X = Int("x"), Y = Int("y") });
                break;
            case "stance":
                sim.Send(player, new StanceOrder { Units = Units("units"), Stance = command.GetProperty("stance").GetString()! });
                break;
            case "work":
                sim.Send(player, new WorkOrder { Building = ids[Int("building")], On = command.GetProperty("on").GetBoolean() });
                break;
            case "build":
                sim.Send(player, new BuildOrder { Building = command.GetProperty("building").GetString()!, X = Int("x"), Y = Int("y"), Builders = Units("builders") });
                break;
            case var other:
                notes.Add($"команда {other}: не знаю — пропущена");
                break;
        }
    }
}
