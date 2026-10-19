using System.Globalization;
using Base.Shared.Orders;
using Kharos.Client.Game;
using Kharos.Client.Ui;
using Kharos.Gpu;
using MemoryPack;
using RenderFrame = Kharos.Gpu.Frame;

namespace Kharos.Base.Tests;

/// <summary>
/// Подача base от начала до конца на модели TS: хост (host.wasm) и клиент (client.wasm) через LocalGame, ввод — как от
/// мыши. Партия — skirmish как в TS: MCV, две багги и три пехотинца, 1500 кредитов. Производство начинается с
/// разворота MCV в главное здание. Без окна и звука; картинки кадров — в dotnet/.cache/base (k-*.png), если есть адаптер
/// WebGPU. Хост тикает в десять раз быстрее часов.
/// </summary>
public sealed class BaseClientTests
{
    /// <summary>Стартовый отряд на экране: MCV, багги и пехота (наземные), их турели — слоем выше.</summary>
    private static RenderFrame OnScreen(BaseSession session) =>
        session.Until(f => BaseSession.Layers(f)[1] >= 6 && BaseSession.Layers(f)[2] >= 3, "отряд MCV на экране");

    private static double Dist((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    /// <summary>Щелчок по юниту: он под курсором в центре.</summary>
    private static void SelectUnit(BaseSession session, uint entity, bool shift = false)
    {
        var (x, y) = session.At(entity);
        session.Click(PointerButtons.Left, session.Game.Frame.View.ToScreen((float)x, (float)y), shift);
    }

    /// <summary>Выбрать MCV и развернуть его кнопкой сетки; вернуть главное здание, выбранное щелчком.</summary>
    private static uint DeployBase(BaseSession session)
    {
        var mcv = session.Mine("Producer").Single();
        SelectUnit(session, mcv);
        Assert.Equal("MCV", session.InfoTitle());
        Assert.Contains("Развернуть", session.Texts());
        session.Click(PointerButtons.Left, session.CenterOf("cell:1"));
        session.Until(_ => session.Mine("Building").Count == 1, "главное здание на месте MCV");
        var building = session.Mine("Building").Single();
        // Здание стоит по левому верхнему тайлу: щелчок — в середину основания.
        var (x, y) = session.At(building);
        session.Click(PointerButtons.Left, session.Game.Frame.View.ToScreen((float)x + 1.5f, (float)y + 1.5f));
        Assert.Equal("Главное здание", session.InfoTitle());
        return building;
    }

    /// <summary>Кредиты, как их показывает панель (число в верхней полосе).</summary>
    private static int ShownCredits(BaseSession session) =>
        session.Texts().Where(text => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _)).Select(int.Parse).FirstOrDefault();

    [Fact]
    public void SkirmishStartsAndRenders()
    {
        using var session = new BaseSession();
        var game = session.Game;
        Assert.Equal("base", Assert.Single(game.Mods).Name);
        // Облики: 18 юнитов, 8 турелей, 48 зданий (завод с чертежом, остальные заглушками) и 6 месторождений.
        Assert.Equal(18 + 8 + 48 + 6, game.Looks.Count);

        var frame = OnScreen(session);
        var layers = BaseSession.Layers(frame);
        // Наземные: MCV, две багги, три пехотинца; турели — слоем выше (MCV несёт пушку, багги — стрелков); зданий нет.
        Assert.Equal((6, 3, 0), (layers[1], layers[2], layers[3]));
        Assert.Equal(6, session.Mine("Mover").Count);
        Assert.Single(session.Of("units/mcv"));
        Assert.Equal(2, session.Of("units/buggy").Count);
        Assert.Equal(3, session.Of("units/infantry").Count);
        Assert.Empty(session.Mine("Building"));
        // MCV — свёрнутая база: Producer у него есть, а заказов ещё нет.
        Assert.Equal(session.Of("units/mcv").Single(), session.Mine("Producer").Single());

        // Камера: масштаб 32, центр у отряда (камера на три тайла южнее).
        var units = session.Mine("Mover");
        var centre = (X: units.Average(e => session.At(e).X), Y: units.Average(e => session.At(e).Y));
        Assert.Equal(32f, frame.View.Zoom);
        Assert.True(Dist(centre, (frame.View.X, frame.View.Y - 3)) < 1, $"камера ({frame.View.X}, {frame.View.Y}), отряд {centre}");

        // HUD: кредиты со своего счёта, часы (начало мира — 9 утра), меню, подсказка по управлению.
        Assert.Equal(1500, session.Credits());
        Assert.Equal(1500, ShownCredits(session));
        Assert.Contains(session.Texts(), text => System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d\d:\d\d$"));
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
        var buggies = session.Of("units/buggy");
        var squad = session.Mine("Mover");
        var start = squad.ToDictionary(e => e, session.At);

        // Щелчок по багги выделяет её; панель показывает название и параметры.
        SelectUnit(session, buggies[0]);
        Assert.Equal("Багги", session.InfoTitle());
        Assert.Contains("Прочность 150 · Скорость 6 · Обзор 11", session.Texts());

        // Правый щелчок — приказ точкой: едет только выбранная, остальные стоят. Shift — следующий приказ встаёт в
        // очередь едущей багги; доехав, она берёт его сама (followOrders; свободный юнит взял бы его сразу, как в TS).
        var target = (X: start[buggies[0]].X + 6, Y: start[buggies[0]].Y - 4);
        var queued = (X: target.X - 3, Y: target.Y + 2);
        session.Click(PointerButtons.Right, frame.View.ToScreen((float)target.X, (float)target.Y));
        session.Click(PointerButtons.Right, frame.View.ToScreen((float)queued.X, (float)queued.Y), shift: true);
        session.Until(_ => session.Queue(buggies[0]) > 0, "приказ в очереди выделенной багги");
        session.Until(_ => session.Queue(buggies[0]) == 0 && Dist(session.At(buggies[0]), queued) < 2.5, "багги доехала до точки из очереди");
        foreach (var other in squad.Where(e => e != buggies[0]))
            Assert.True(Dist(session.At(other), start[other]) < 0.5, $"сущность {other} сдвинулась");

        // Щелчок по пустой земле снимает выделение.
        session.Click(PointerButtons.Left, frame.View.ToScreen(frame.View.X + 14, frame.View.Y - 7));
        Assert.Null(session.InfoTitle());

        // Рамка вокруг отряда выделяет всех; Escape снимает.
        void Box()
        {
            var now = squad.Select(session.At).ToList();
            session.Drag(frame.View.ToScreen((float)now.Min(p => p.X) - 1, (float)now.Min(p => p.Y) - 1), frame.View.ToScreen((float)now.Max(p => p.X) + 1, (float)now.Max(p => p.Y) + 1));
        }
        Box();
        Assert.Equal("MCV, Багги ×2, Пехотинец ×3", session.InfoTitle());
        session.Step(session.Input with { Hits = ["escape"] });
        session.Step();
        Assert.Null(session.InfoTitle());
        Box();
        Assert.Equal("MCV, Багги ×2, Пехотинец ×3", session.InfoTitle());

        // Остановить: приказы и пути сняты.
        session.Step(session.Input with { Hits = ["x"], Keys = ["x"] });
        session.Step();
        session.Until(_ => session.Queue(buggies[0]) == 0, "очередь багги снята");

        // Shift добавляет к выделению: щелчок по багги, пока выделен только MCV.
        session.Step(session.Input with { Hits = ["escape"] });
        session.Step();
        SelectUnit(session, session.Of("units/mcv").Single());
        SelectUnit(session, buggies[1], shift: true);
        Assert.Equal("MCV, Багги", session.InfoTitle());
        Assert.Empty(session.Game.Errors);
    }

    [Fact]
    public void DeployThenProduceButtonSpendsCreditsAndNewUnitAppears()
    {
        using var session = new BaseSession();
        OnScreen(session);
        var building = DeployBase(session);
        // Сетка 4 × 3: строитель и грузовик (T, Y), свернуть; остальные ячейки пустые.
        Assert.Contains("Строитель", session.Texts());
        Assert.Contains("120", session.Texts());
        Assert.Contains("Грузовик", session.Texts());
        Assert.Contains("T", session.Texts());
        Assert.Contains("Правый щелчок по карте — точка сбора готовых", session.Texts());
        Assert.Equal(3, Enumerable.Range(1, 12).Count(i => session.Game.Gui.RectOf("cell:" + i) is not null));
        var grid = session.Game.Gui.RectOf("commands")!.Value;
        Assert.Equal((4 * 132 + 3 * 4, 140), (grid.W, grid.H));
        // Награда за разворот базы (rewards.json) уже на счёте: стартовых 1500 не меньше.
        var before = session.Credits();
        Assert.True(before >= 1500, $"кредитов {before}");
        Assert.Equal(before, ShownCredits(session));

        // Щелчок по кнопке: 120 кредитов списаны, заказ в производстве (полоса и очередь в панели).
        session.Click(PointerButtons.Left, session.CenterOf("cell:1"));
        session.Until(_ => session.Credits() <= before - 120, "кредиты списаны за строителя");
        session.Step();
        Assert.Contains(session.Texts(), text => text == "Главное здание");
        Assert.NotNull(session.Game.Gui.RectOf("queue:1"));
        Assert.Equal(building, session.Mine("Producer").Single());

        // Хост делает юнит за 5 секунд игры: он выходит у здания и появляется в мире и на экране.
        session.Until(_ => session.Of("units/builder").Count == 1, "строитель из главного здания");
        Assert.Null(session.Game.Gui.RectOf("queue:1"));
        Assert.Empty(session.Game.Errors);
        Assert.Null(session.Local.Failure);

        // Горячая клавиша T заказывает ещё одного, правый щелчок по кнопке отменяет последний заказ и возвращает кредиты.
        var now = session.Credits();
        session.Step(session.Input with { Hits = ["t"], Keys = ["t"] });
        session.Step();
        session.Until(_ => session.Credits() <= now - 120, "кредиты списаны горячей клавишей");
        var spent = session.Credits();
        session.Click(PointerButtons.Right, session.CenterOf("cell:1"));
        session.Until(_ => session.Credits() >= spent + 120, "заказ отменён правым щелчком, кредиты вернулись");
        Assert.Single(session.Of("units/builder"));

        // Правый щелчок по земле — точка сбора здания.
        var (x, y) = session.At(building);
        session.Click(PointerButtons.Right, session.Game.Frame.View.ToScreen((float)x + 6.5f, (float)y - 5.5f));
        session.Until(_ => session.Number(building, "Producer", "rally") == 1, "точка сбора поставлена");
        Assert.Equal(((int)Math.Floor(x + 6.5), (int)Math.Floor(y - 5.5)), ((int)session.Number(building, "Producer", "rallyX"), (int)session.Number(building, "Producer", "rallyY")));
    }

    [Fact]
    public void DepositsFromLayerAreDrawn()
    {
        using var session = new BaseSession();
        var frame = OnScreen(session);
        var known = session.DepositCount();
        // Редактор кладёт месторождения рядом с отрядом: слой deposits, предметы с видом и остатком.
        session.OpenEditor();
        var (x, y) = session.At(session.Mine("Mover")[0]);
        var kinds = new[] { "metal", "silicon", "fuel", "kharite", "lithium", "uranium" };
        for (var i = 0; i < kinds.Length; i++)
            session.Game.Send(EditDepositOrder.Name, MemoryPackSerializer.Serialize(new EditDepositOrder { X = (int)x - 9 + i * 3, Y = (int)y + 4, Kind = kinds[i], Reserve = 1500 }));
        // Хост принял сколько смог (место должно быть строимым): на экране рисуется каждое — слоем «на земле».
        session.Until(_ => session.DepositCount() > known, "месторождения дошли до клиента");
        session.Steps(10);
        var placed = session.DepositCount() - known;
        frame = session.Step();
        Assert.True(BaseSession.Layers(frame)[0] >= placed, $"спрайтов на земле {BaseSession.Layers(frame)[0]}, новых месторождений {placed}");
        Assert.Empty(session.Game.Errors);
        Assert.Empty(session.Log);
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

        // Стартовый экран: отряд, мини-карта, подсказка.
        game.Upload(renderer);
        renderer.Render(frame);
        renderer.SavePng(BaseSession.Cache("k-start.png"));

        // Развёрнутая база: выделена, в производстве строитель; точка сбора правым щелчком — флажок и пунктир.
        var building = DeployBase(session);
        session.Click(PointerButtons.Left, session.CenterOf("cell:1"));
        var (x, y) = session.At(building);
        session.Click(PointerButtons.Right, session.Game.Frame.View.ToScreen((float)x + 7, (float)y + 7));
        session.Steps(30);
        frame = session.Step(session.Input with { Pointer = (900, 300) });

        game.Upload(renderer);
        renderer.Render(frame);
        var day = renderer.ReadPixels();
        renderer.SavePng(BaseSession.Cache("k-day.png"));

        var light = frame.Light;
        frame.Light = 0.1f;
        frame.Precipitation = 0;
        renderer.Render(frame);
        var night = renderer.ReadPixels();
        renderer.SavePng(BaseSession.Cache("k-night.png"));
        frame.Light = light;

        // Отряд выделен рамкой и едет: пунктир пути, кольца, колея.
        var squad = session.Mine("Mover");
        var positions = squad.Select(session.At).ToList();
        session.Click(PointerButtons.Left, frame.View.ToScreen(frame.View.X + 14, frame.View.Y - 7));
        session.Drag(frame.View.ToScreen((float)positions.Min(p => p.X) - 1, (float)positions.Min(p => p.Y) - 1), frame.View.ToScreen((float)positions.Max(p => p.X) + 1, (float)positions.Max(p => p.Y) + 1));
        session.Click(PointerButtons.Right, frame.View.ToScreen((float)positions.Average(p => p.X) - 7, (float)positions.Average(p => p.Y) - 4));
        session.Steps(6);
        frame = session.Step(session.Input with { Pointer = (700, 250) });
        game.Upload(renderer);
        renderer.Render(frame);
        renderer.SavePng(BaseSession.Cache("k-orders.png"));

        Assert.True(Distinct(day) > 40, "дневной кадр однотонный");
        Assert.True(Luma(night) < Luma(day) * 0.7, "ночь не темнее дня");
        Assert.Empty(game.Errors);
    }

    [Fact]
    public void FrameCostOfSkirmish()
    {
        using var session = new BaseSession();
        OnScreen(session);
        DeployBase(session);
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
            $"Клиент base, схватка (главное здание, отряд, HUD, выделено здание, идёт производство), {sprites} спрайтов, кадр без видеокарты: " +
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
