using System.Diagnostics;
using System.Globalization;
using Hello;
using Kharos.Client.Game;
using Kharos.Client.Ui;
using Kharos.Content;
using Kharos.Engine.World;
using Kharos.Gpu;
using Kharos.Host;
using MemoryPack;
using HostGame = Kharos.Host.Host;
using RenderFrame = Kharos.Gpu.Frame;

namespace Kharos.Client.Tests.Game;

/// <summary>
/// Партия hello целиком: хост в своём потоке (LocalGame), клиент с client.wasm через транспорт в памяти. Без собранных
/// host.wasm и client.wasm тесты пропускаются: pwsh dotnet/tools/build-mods.ps1 -Mod hello.
/// </summary>
internal sealed class HelloSession : IDisposable
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public static string ModDir => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Repo.Cache("x"))!, "..", "..", "mods", "hello"));

    public static bool Built => File.Exists(Path.Combine(ModDir, "host.wasm")) && File.Exists(Path.Combine(ModDir, "client.wasm"));

    public const string NotBuilt = "mods/hello/host.wasm и client.wasm не собраны: pwsh dotnet/tools/build-mods.ps1 -Mod hello";

    public HelloSession(int width = 1280, int height = 720)
    {
        Assert.SkipUnless(Built, NotBuilt);
        var content = GameContent.Load([ModPackage.Read(ModDir)]);
        Local = LocalGame.Start(() => new HostGame(content, new HostOptions { Seed = 1 }));
        Game = new ClientGame(content, Local.Client, new ClientGameOptions { Log = Log.Add });
        Input = new ClientInput { Width = width, Height = height };
    }

    public LocalGame Local { get; }
    public ClientGame Game { get; }
    public List<string> Log { get; } = [];
    public ClientInput Input { get; set; }

    /// <summary>Кадр по часам: ввод — <see cref="Input"/> с текущим временем.</summary>
    public RenderFrame Step(ClientInput? input = null)
    {
        var given = (input ?? Input) with { Time = _clock.Elapsed.TotalSeconds };
        return Game.Step(given);
    }

    /// <summary>Кадры по ~60 в секунду, пока until не скажет «да»; не дождались — провал с причиной.</summary>
    public RenderFrame Until(Func<RenderFrame, bool> until, string what, double seconds = 15)
    {
        var deadline = _clock.Elapsed.TotalSeconds + seconds;
        while (true)
        {
            var frame = Step();
            if (until(frame))
                return frame;
            Assert.True(_clock.Elapsed.TotalSeconds < deadline, $"не дождались: {what}; журнал: {string.Join(" | ", Log.TakeLast(5))}");
            Thread.Sleep(15);
        }
    }

    /// <summary>Свои юниты (с Drive) в реплике.</summary>
    public List<uint> Units()
    {
        var world = Game.Replica.World!;
        var drive = world.Schema.Id("Drive");
        var units = new List<uint>();
        world.Query([drive, Builtin.Owner], units);
        units.RemoveAll(entity => world.Number(entity, Builtin.Owner, 0) != Game.Replica.Player);
        units.Sort();
        return units;
    }

    public static int Sprites(RenderFrame frame) => frame.SpriteLayers.Sum(layer => layer.Count) / RenderFrame.Sprite;

    public void Dispose()
    {
        Game.Dispose();
        Local.Dispose();
    }
}

public sealed class ClientGameTests
{
    [Fact]
    public void UnitsAppearAndHudButtonSpawnsMore()
    {
        using var session = new HelloSession();
        var game = session.Game;
        Assert.Equal(["units/rover"], Assert.Single(game.Mods).Manifest.Looks);
        Assert.Equal("hello:blip", Assert.Single(game.Mods[0].Manifest.Bank).Name);
        Assert.Single(game.Looks);

        // Первый кадр: туман целиком (рамка в тайл), свои юниты в обзоре — видны.
        var first = session.Step();
        var map = game.Replica.Welcome!.Map!.Value;
        var (fog, fogWidth, fogHeight) = first.FogUpdate ?? throw new Xunit.Sdk.XunitException("нет картинки тумана в первом кадре");
        Assert.Equal((map.Width + 2, map.Height + 2), (fogWidth, fogHeight));
        Assert.Equal(((float)map.Left - 1, (float)map.Top - 1), first.Fog);
        Assert.Equal(240, fog[0]);
        var unit = session.Units()[0];
        var world = game.Replica.World!;
        var (ux, uy) = ((int)Math.Floor(world.Number(unit, Builtin.Position, 0)), (int)Math.Floor(world.Number(unit, Builtin.Position, 1)));
        Assert.Equal(0, fog[(uy - map.Top + 1) * fogWidth + (ux - map.Left + 1)]);

        var start = session.Until(frame => HelloSession.Sprites(frame) >= 3, "три ровера на экране");
        Assert.Equal(3, session.Units().Count);
        Assert.Null(start.FogUpdate);
        Assert.Equal(3 * 2 * 4, start.Spots.Count);
        Assert.Equal(3, start.Beams.Count / 8);
        Assert.Equal(48f, start.View.Zoom);
        // Интерфейс: скин, шрифт, облик вида и мини-карта.
        var paints = start.Interface.Segments.Select(segment => segment.Paint.Kind).ToHashSet();
        Assert.Superset(new HashSet<PaintKind> { PaintKind.Skin, PaintKind.Font, PaintKind.Sprite, PaintKind.Minimap }, paints);

        // Щелчок по кнопке HUD: наведение, нажатие, отпускание — мод шлёт hello.spawn, хост создаёт пять юнитов.
        var rect = game.Gui.RectOf(HelloClient.SpawnButton) ?? throw new Xunit.Sdk.XunitException("нет кнопки HUD");
        var pointer = (rect.X + rect.W / 2, rect.Y + rect.H / 2);
        session.Step(session.Input with { Pointer = pointer });
        session.Step(session.Input with { Pointer = pointer, Pressed = PointerButtons.Left, Down = PointerButtons.Left });
        session.Step(session.Input with { Pointer = pointer, Released = PointerButtons.Left });
        var more = session.Until(frame => session.Units().Count >= 8 && HelloSession.Sprites(frame) >= 8, "восемь роверов после щелчка");
        Assert.Equal(8, session.Units().Count);
        Assert.Empty(game.Errors);
        Assert.Null(session.Local.Failure);
        Assert.Empty(session.Log);
        Assert.True(more.Interface.Indices.Count > 0);
    }

    [Fact]
    public void MovingUnitsLeaveTrailsAndPickFindsThem()
    {
        using var session = new HelloSession();
        var game = session.Game;
        session.Until(frame => HelloSession.Sprites(frame) >= 3, "роверы на экране");
        var units = session.Units();
        var world = game.Replica.World!;
        var (x, y) = (world.Number(units[0], Builtin.Position, 0), world.Number(units[0], Builtin.Position, 1));

        // Инструмент Pick клиента: юнит под точкой.
        Assert.Equal(units[0], game.Tools.Pick(world, x + 0.2, y));
        Assert.Null(game.Tools.Pick(world, x + 30, y + 30));

        game.Send("hello.move", MemoryPackSerializer.Serialize(new MoveOrder { Entities = [.. units], X = x + 6, Y = y - 4 }));
        var trails = session.Until(frame => frame.Decals.Count >= 2 * RenderFrame.Decal, "колея за роверами");
        Assert.All(Enumerable.Range(0, trails.Decals.Count / RenderFrame.Decal), i =>
        {
            Assert.Equal(3 / 16f, trails.Decals[i * RenderFrame.Decal + 4]);
            Assert.InRange(trails.Decals[i * RenderFrame.Decal + 13], 0.99f, 1f);
        });
        // Пыль из-под колёс — частицы в слое поверх мира; ветер атмосферы мода — в кадре.
        Assert.True(trails.World.Vertices.Count > 0);
        Assert.Equal((0.6f, 0.2f), trails.Wind);
        Assert.Empty(game.Errors);
    }

    [Fact]
    public void RendersHeadlessFrames()
    {
        var gpu = GpuContext.TryCreateHeadless(out var reason);
        if (gpu is null)
            Assert.Skip($"нет адаптера WebGPU: {reason}");
        using var context = gpu;
        using var session = new HelloSession();
        var game = session.Game;
        using var renderer = new Renderer(context!);
        renderer.Resize(1280, 720);
        session.Until(frame => HelloSession.Sprites(frame) >= 3, "роверы на экране");
        var units = session.Units();
        var world = game.Replica.World!;
        game.Send("hello.move", MemoryPackSerializer.Serialize(new MoveOrder { Entities = [.. units], X = world.Number(units[0], Builtin.Position, 0) + 5, Y = world.Number(units[0], Builtin.Position, 1) + 3 }));
        var frame = session.Until(f => f.Decals.Count >= 6 * RenderFrame.Decal, "колея в кадре");
        // Указатель над первым ровером: кольцо отметки.
        var target = game.Replica.World!;
        var (sx, sy) = frame.View.ToScreen((float)target.Number(units[0], Builtin.Position, 0), (float)target.Number(units[0], Builtin.Position, 1));
        frame = session.Step(session.Input with { Pointer = (sx, sy) });
        game.Upload(renderer);
        renderer.Render(frame);
        var day = renderer.ReadPixels();
        renderer.SavePng(Repo.Cache("game-day.png"));
        frame.Light = 0.2f;
        frame.Precipitation = 0.4f;
        renderer.Render(frame);
        renderer.SavePng(Repo.Cache("game-night.png"));
        var night = renderer.ReadPixels();
        Assert.True(Distinct(day) > 20, "дневной кадр однотонный");
        Assert.True(Luma(night) < Luma(day), "ночь не темнее дня");
        Assert.Empty(game.Errors);
    }

    [Fact]
    public void FrameCostWithThreeHundredUnits()
    {
        using var session = new HelloSession();
        var game = session.Game;
        var start = session.Until(frame => HelloSession.Sprites(frame) >= 3, "роверы на экране");
        var (cx, cy) = (start.View.X, start.View.Y);
        for (var i = 0; i < 3; i++)
            game.Send("hello.spawn", MemoryPackSerializer.Serialize(new SpawnOrder { Count = 100, X = cx + (i - 1) * 6, Y = cy, Spread = 12 }));
        session.Until(_ => session.Units().Count >= 303, "303 ровера", 30);
        var units = session.Units();
        game.Send("hello.move", MemoryPackSerializer.Serialize(new MoveOrder { Entities = [.. units], X = cx + 4, Y = cy + 2 }));
        const int warmup = 30, frames = 240;
        var total = new List<double>();
        var mods = new List<double>();
        var build = new List<double>();
        var sprites = 0;
        long allocated = 0;
        for (var i = 0; i < warmup + frames; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            session.Step(session.Input with { Pointer = (200 + i % 600, 300 + i % 100) });
            var after = GC.GetAllocatedBytesForCurrentThread();
            Thread.Sleep(8);
            if (i < warmup)
                continue;
            allocated += after - before;
            total.Add(game.Stats.Total);
            mods.Add(game.Stats.Mods);
            build.Add(game.Stats.Build);
            sprites = Math.Max(sprites, game.Stats.Sprites);
        }
        total.Sort();
        mods.Sort();
        build.Sort();
        var report = string.Create(CultureInfo.InvariantCulture,
            $"Клиент hello, {units.Count} юнитов, {sprites} спрайтов, кадр без видеокарты: медиана {total[frames / 2]:F3} мс " +
            $"(моды {mods[frames / 2]:F3}, сборка кадра {build[frames / 2]:F3}), 95% {total[frames * 95 / 100]:F3} мс, макс. {total[^1]:F3} мс; " +
            $"выделено {allocated / frames / 1024.0:F1} КБ на кадр");
        File.WriteAllText(Repo.Cache("game-perf.txt"), report + Environment.NewLine);
        TestContext.Current.SendDiagnosticMessage(report);
        Assert.True(sprites >= 250, report);
        Assert.True(total[frames / 2] < 16, report);
        Assert.Empty(game.Errors);
    }

    private static int Distinct(byte[] rgba)
    {
        var set = new HashSet<int>();
        for (var i = 0; i < rgba.Length; i += 4) set.Add(rgba[i] | rgba[i + 1] << 8 | rgba[i + 2] << 16);
        return set.Count;
    }

    private static double Luma(byte[] rgba)
    {
        double sum = 0;
        for (var i = 0; i < rgba.Length; i += 4) sum += 0.299 * rgba[i] + 0.587 * rgba[i + 1] + 0.114 * rgba[i + 2];
        return sum / (rgba.Length / 4);
    }
}
