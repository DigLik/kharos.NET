using System.Globalization;
using Kharos.Client.Game;
using Kharos.Client.Ui;
using Kharos.Gpu;
using RenderFrame = Kharos.Gpu.Frame;

namespace Kharos.Base.Tests;

/// <summary>
/// Подача base от начала до конца: хост (host.wasm) и клиент (client.wasm) через LocalGame, ввод — как от мыши. Без окна и
/// звука; картинки кадров — в dotnet/.cache/base, если есть адаптер WebGPU. Хост тикает в десять раз быстрее часов, поэтому
/// доход идёт заметно: кредиты сверяются диапазонами.
/// </summary>
public sealed class BaseClientTests
{
    private static RenderFrame OnScreen(BaseSession session) =>
        session.Until(f => BaseSession.Layers(f)[1] >= 3 && BaseSession.Layers(f)[3] >= 1, "три багги и завод на экране");

    /// <summary>Выбрать завод щелчком.</summary>
    private static uint SelectFactory(BaseSession session, RenderFrame frame)
    {
        var factory = session.Mine("Producer").Single();
        var (x, y) = session.At(factory);
        session.Click(PointerButtons.Left, frame.View.ToScreen((float)x, (float)y));
        return factory;
    }

    private static double Dist((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Кредиты, как их показывает панель (число в верхней полосе).</summary>
    private static int ShownCredits(BaseSession session) =>
        session.Texts().Where(text => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _)).Select(int.Parse).FirstOrDefault();

    [Fact]
    public void SkirmishStartsAndRenders()
    {
        using var session = new BaseSession();
        var game = session.Game;
        Assert.Equal("base", Assert.Single(game.Mods).Name);
        // Облики: багги, стрелок, завод и шесть месторождений.
        Assert.Equal(9, game.Looks.Count);

        var frame = OnScreen(session);
        var layers = BaseSession.Layers(frame);
        // Наземные: три багги; их турели — слоем выше; здание: завод.
        Assert.Equal((3, 3, 1), (layers[1], layers[2], layers[3]));
        Assert.Equal(3, session.Mine("Drive").Count);
        Assert.Single(session.Mine("Producer"));

        // Камера: масштаб 32, центр у базы (завод в нуле, камера на три тайла южнее).
        Assert.Equal(32f, frame.View.Zoom);
        Assert.Equal((0f, 3f), (frame.View.X, frame.View.Y));

        // HUD: кредиты, доход, часы (начало мира — 9 утра), меню, подсказка по управлению.
        // Хост тикает вдесятеро быстрее, а холодный старт клиента (компиляция wasm и шейдеров) бывает долгим — доход
        // успевает набежать; верхней границы нет, HUD сверяется с хостом.
        var credits = session.Credits();
        Assert.True(credits >= 1000, $"кредитов {credits}");
        Assert.InRange(ShownCredits(session), 1000, session.Credits());
        Assert.Contains("+5/с", session.Texts());
        Assert.Contains(session.Texts(), text => text.StartsWith("09:", StringComparison.Ordinal));
        Assert.Contains("Меню", session.Texts());
        Assert.Contains("Ничего не выбрано.", session.Texts());
        Assert.Null(session.InfoTitle());
        Assert.True(frame.Interface.Segments.Select(s => s.Paint.Kind).ToHashSet().IsSupersetOf([PaintKind.Skin, PaintKind.Font, PaintKind.Minimap]));

        // Туман войны и погода утра: светло, ветер задан атмосферой мода.
        Assert.NotNull(frame.Fog);
        Assert.InRange(frame.Light, 0.9f, 1f);
        Assert.NotEqual((0f, 0f), frame.Wind);
        Assert.Empty(game.Errors);
        Assert.Null(session.Local.Failure);
        Assert.Empty(session.Log);
    }

    [Fact]
    public void SelectionByClickAndBoxAndMoveOrder()
    {
        using var session = new BaseSession();
        var frame = OnScreen(session);
        var buggies = session.Mine("Drive");
        var start = buggies.Select(session.At).ToList();

        // Щелчок по багги выделяет её; панель показывает название и параметры.
        session.Click(PointerButtons.Left, frame.View.ToScreen((float)start[0].X, (float)start[0].Y));
        Assert.Equal("Багги", session.InfoTitle());
        Assert.Contains("Прочность 150 · Скорость 6 · Обзор 11", session.Texts());

        // Правый щелчок: едет только выбранная, остальные стоят.
        session.Click(PointerButtons.Right, frame.View.ToScreen(-5, 8));
        session.Until(_ => Dist(session.At(buggies[0]), (-5, 8)) < 1, "выбранная багги доехала");
        Assert.True(Dist(session.At(buggies[1]), start[1]) < 0.1);
        Assert.True(Dist(session.At(buggies[2]), start[2]) < 0.1);

        // Щелчок по пустой земле снимает выделение.
        session.Click(PointerButtons.Left, frame.View.ToScreen(12, 6));
        Assert.Null(session.InfoTitle());

        // Рамка вокруг трёх багги выделяет все три; Escape снимает.
        var now = buggies.Select(session.At).ToList();
        session.Drag(frame.View.ToScreen((float)now.Min(p => p.X) - 1, (float)now.Min(p => p.Y) - 1), frame.View.ToScreen((float)now.Max(p => p.X) + 1, (float)now.Max(p => p.Y) + 1));
        Assert.Equal("Багги ×3", session.InfoTitle());
        session.Step(session.Input with { Hits = ["escape"] });
        session.Step();
        Assert.Null(session.InfoTitle());
        session.Drag(frame.View.ToScreen((float)now.Min(p => p.X) - 1, (float)now.Min(p => p.Y) - 1), frame.View.ToScreen((float)now.Max(p => p.X) + 1, (float)now.Max(p => p.Y) + 1));
        Assert.Equal("Багги ×3", session.InfoTitle());

        // Правый щелчок по земле: приказ ехать, группа встаёт вокруг точки.
        var target = (X: 8.0, Y: 9.0);
        session.Click(PointerButtons.Right, frame.View.ToScreen((float)target.X, (float)target.Y));
        session.Until(_ => buggies.All(b => Dist(session.At(b), target) < 3.5), "багги доехали до точки приказа");
        Assert.All(buggies, b => Assert.True(session.Number(b, "Drive", "moving") == 0 || Dist(session.At(b), target) < 3.5));

        // Shift добавляет к выделению: щелчок по заводу, пока выделены багги.
        var (fx, fy) = session.At(session.Mine("Producer").Single());
        session.Click(PointerButtons.Left, frame.View.ToScreen((float)fx, (float)fy), shift: true);
        Assert.Equal("Багги ×3, Завод", session.InfoTitle());
        Assert.Empty(session.Game.Errors);
    }

    [Fact]
    public void ProduceButtonSpendsCreditsAndNewBuggyAppears()
    {
        using var session = new BaseSession();
        var frame = OnScreen(session);
        var factory = SelectFactory(session, frame);
        Assert.Equal("Завод", session.InfoTitle());
        // Сетка 4 × 3: первая ячейка — багги за 250 с клавишей T, остальные пустые.
        Assert.Contains("Багги", session.Texts());
        Assert.Contains("250", session.Texts());
        Assert.Contains("T", session.Texts());
        Assert.Contains("Правый щелчок по карте — точка сбора готовых", session.Texts());
        Assert.Equal(1, Enumerable.Range(1, 12).Count(i => session.Game.Gui.RectOf("cell:" + i) is not null));
        var grid = session.Game.Gui.RectOf("commands")!.Value;
        Assert.Equal((4 * 132 + 3 * 4, 140), (grid.W, grid.H));
        var before = session.Credits();

        // Щелчок по кнопке: 250 кредитов списано, заказ в производстве (полоса и очередь в панели).
        session.Click(PointerButtons.Left, session.CenterOf("cell:1"));
        session.Until(_ => session.Credits() < before - 200, "кредиты списаны за багги");
        Assert.InRange(session.Credits(), before - 250, before - 150);
        session.Step();
        Assert.Contains(session.Texts(), text => text == "Завод");
        Assert.NotNull(session.Game.Gui.RectOf("queue:1"));
        Assert.Equal(factory, session.Mine("Producer").Single());

        // Хост делает юнит за 7 секунд игры: он выходит у ворот и появляется в мире и на экране.
        session.Until(_ => session.Mine("Drive").Count == 4, "четвёртая багги из завода");
        session.Until(f => BaseSession.Layers(f)[1] == 4 && BaseSession.Layers(f)[2] == 4, "четыре багги с турелями на экране");
        Assert.Empty(session.Game.Errors);
        Assert.Null(session.Local.Failure);

        // Горячая клавиша T заказывает ещё одну.
        var now = session.Credits();
        session.Step(session.Input with { Hits = ["t"], Keys = ["t"] });
        session.Step();
        session.Until(_ => session.Credits() < now - 150, "кредиты списаны горячей клавишей");
    }

    [Fact]
    public void RendersDayAndNightFramesToPng()
    {
        var gpu = GpuContext.TryCreateHeadless(out var reason);
        if (gpu is null)
            Assert.Skip($"нет адаптера WebGPU: {reason}");
        using var context = gpu;
        using var session = new BaseSession();
        using var renderer = new Renderer(context!);
        renderer.Resize(1280, 720);
        var game = session.Game;
        var frame = OnScreen(session);

        // Выделен завод, в производстве багги; точка сбора правым щелчком — флажок и пунктир.
        var factory = SelectFactory(session, frame);
        session.Click(PointerButtons.Left, session.CenterOf("cell:1"));
        var (x, y) = session.At(factory);
        session.Click(PointerButtons.Right, frame.View.ToScreen((float)x + 7, (float)y + 7));
        session.Steps(30);
        frame = session.Step(session.Input with { Pointer = (900, 300) });

        game.Upload(renderer);
        renderer.Render(frame);
        var day = renderer.ReadPixels();
        renderer.SavePng(BaseSession.Cache("base-day.png"));

        var light = frame.Light;
        frame.Light = 0.1f;
        frame.Precipitation = 0;
        renderer.Render(frame);
        var night = renderer.ReadPixels();
        renderer.SavePng(BaseSession.Cache("base-night.png"));
        frame.Light = light;

        // Три багги выделены рамкой и едут — пунктир пути, кольца, колея.
        var buggies = session.Mine("Drive");
        var positions = buggies.Select(session.At).ToList();
        session.Click(PointerButtons.Left, frame.View.ToScreen(14, 8));
        session.Drag(frame.View.ToScreen((float)positions.Min(p => p.X) - 1, (float)positions.Min(p => p.Y) - 1), frame.View.ToScreen((float)positions.Max(p => p.X) + 1, (float)positions.Max(p => p.Y) + 1));
        session.Click(PointerButtons.Right, frame.View.ToScreen(-7, 8));
        session.Steps(6);
        frame = session.Step(session.Input with { Pointer = (700, 250) });
        game.Upload(renderer);
        renderer.Render(frame);
        renderer.SavePng(BaseSession.Cache("base-orders.png"));

        Assert.True(Distinct(day) > 40, "дневной кадр однотонный");
        Assert.True(Luma(night) < Luma(day) * 0.7, "ночь не темнее дня");
        Assert.Empty(game.Errors);
    }

    [Fact]
    public void FrameCostOfSkirmish()
    {
        using var session = new BaseSession();
        var frame = OnScreen(session);
        SelectFactory(session, frame);
        session.Click(PointerButtons.Left, session.CenterOf("cell:1"));
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
            total.Add(session.Game.Stats.Total);
            mods.Add(session.Game.Stats.Mods);
            build.Add(session.Game.Stats.Build);
            sprites = Math.Max(sprites, session.Game.Stats.Sprites);
        }
        total.Sort();
        mods.Sort();
        build.Sort();
        var report = string.Create(CultureInfo.InvariantCulture,
            $"Клиент base, схватка (3 багги с турелями, завод, месторождения; HUD, выделен завод, идёт производство), {sprites} спрайтов, кадр без видеокарты: " +
            $"медиана {total[frames / 2]:F3} мс (моды {mods[frames / 2]:F3}, сборка кадра {build[frames / 2]:F3}), 95% {total[frames * 95 / 100]:F3} мс, макс. {total[^1]:F3} мс; " +
            $"выделено {allocated / frames / 1024.0:F1} КБ на кадр");
        File.WriteAllText(BaseSession.Cache("perf.txt"), report + Environment.NewLine);
        TestContext.Current.SendDiagnosticMessage(report);
        Assert.True(total[frames / 2] < 16, report);
        Assert.Empty(session.Game.Errors);
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
