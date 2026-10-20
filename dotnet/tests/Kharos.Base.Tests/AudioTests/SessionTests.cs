using System.Globalization;
using Base.Client.Audio;
using Kharos.Audio;
using Kharos.Client.Game;
using Kharos.Client.Ui;
using Kharos.Engine.World;
using S = Kharos.Mod.Sdk.Client;

namespace Kharos.Base.Tests.AudioTests;

/// <summary>
/// Звук партии base от начала до конца: хост и client.wasm настоящие, движок звука — офлайн (устройства нет, ничего не
/// звучит вслух). Без собранных host.wasm и client.wasm тесты пропускаются: pwsh dotnet/tools/build-mods.ps1 -Mod base.
/// </summary>
public sealed class SessionTests
{
    private const int Rate = AudioFormat.SampleRate;

    /// <summary>Цифры прогона — в журнал тестов и в audio-report.txt рядом со сборкой.</summary>
    private static void Report(string text)
    {
        TestContext.Current.SendDiagnosticMessage(text);
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "audio-report.txt"), text + Environment.NewLine);
    }

    [Fact]
    public void BankAndVoicesComeFromTheClientMod()
    {
        using var session = new BaseSession();
        var manifest = Assert.Single(session.Game.Mods, mod => mod.Name == "base").Manifest;
        Assert.Equal(BankFixture.All.Keys.Order(StringComparer.Ordinal), manifest.Bank.Select(b => b.Name).Order(StringComparer.Ordinal));
        Assert.Contains(manifest.Voices, voice => voice.Name == FlightAudio.Voice);
        // Банк в движке — тот же, что считает мод сам (по длине и пику каждой записи).
        foreach (var (name, rendered) in BankFixture.All)
        {
            Assert.True(session.Engine!.Bank.TryGet(name, out var record), name);
            Assert.Equal(rendered.Variants.Length, record.Variants.Count);
            Assert.Equal(rendered.Variants[0][0].Length, record.Variants[0].Length);
            Assert.Equal(rendered.Variants[0].Length, record.Variants[0].ChannelCount);
        }
        Assert.True(session.Engine!.HasVoice(FlightAudio.Voice));
        Assert.Empty(session.Game.Errors);
    }

    [Fact]
    public void SquadDrivingMakesSoundOverTheBackground()
    {
        using var session = new BaseSession();
        var engine = session.Engine!;
        var game = session.Game;
        // Дождаться юнитов и повернуть камеру к ним: звук слышен там, где экран.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        List<uint> units;
        while ((units = session.Units()).Count < 3)
        {
            Assert.True(DateTime.UtcNow < deadline, "нет юнитов");
            session.Step();
            Thread.Sleep(10);
        }
        var (x, y) = session.Centre(units);
        void Watch(double seconds)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < until)
            {
                session.Look(x, y);
                session.Step();
                Thread.Sleep(10);
            }
        }

        // Стоят: слышен только фон — ветер и подложка, она вступает за шесть секунд.
        Watch(1.5);
        var quietFrom = session.Output.Count - Rate;
        var quiet = BaseSession.Rms(session.Output, quietFrom, Rate);
        Assert.True(quiet > 1e-5, $"фон молчит: {quiet}");
        var voicesIdle = engine.ActiveVoices;

        // Едут: стартёры, моторы с голосами по числу машин (пехота шагает без мотора), ход.
        session.Move(units, x + 10, y + 3);
        var driveFrom = session.Output.Count;
        Watch(3);
        var driving = BaseSession.Rms(session.Output, driveFrom + Rate / 2, 2 * Rate);
        var peak = session.Output.Skip(driveFrom).Max(MathF.Abs);
        Assert.True(driving > 3 * quiet, $"моторы не слышны: {driving} против фона {quiet}");
        Assert.True(peak <= 1.2f, $"выход перегружен: {peak}");
        Assert.True(engine.ActiveVoices >= voicesIdle + session.Motorized(units), $"голосов {engine.ActiveVoices} при {voicesIdle} в покое");
        Assert.All(session.Output.Skip(driveFrom), value => Assert.True(float.IsFinite(value)));

        Report(string.Create(CultureInfo.InvariantCulture, $"Фон {quiet:F5}, моторы {driving:F5} (×{driving / quiet:F1}), пик {peak:F3}, голосов {voicesIdle} → {engine.ActiveVoices}"));

        // Доехали и стоят: мотор сбрасывает обороты, звук падает к холостым.
        Watch(1.5);
        Assert.Empty(game.Errors);
        Assert.Empty(session.Log);
        Assert.Null(session.Local.Failure);
    }

    [Fact]
    public void BaseAtWorkRunsTheMachineryLoop()
    {
        using var session = new BaseSession();
        var world = session.Game.Replica.World!;
        var producer = world.Schema.Id("Producer");
        var building = world.Schema.Id("Building");
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var mcv = new List<uint>();
        while (mcv.Count == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "нет MCV");
            session.Step();
            Thread.Sleep(10);
            world = session.Game.Replica.World!;
            world.Query([producer, Builtin.Owner], mcv);
        }
        // MCV разворачивается в главное здание — оно производит строителей.
        session.Game.Send("deploy", MemoryPack.MemoryPackSerializer.Serialize(new global::Base.Shared.Orders.DeployOrder { Unit = mcv[0] }));
        var factories = new List<uint>();
        while (factories.Count == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "главное здание не встало");
            session.Step();
            Thread.Sleep(10);
            world = session.Game.Replica.World!;
            world.Query([producer, building, Builtin.Owner], factories);
        }
        var factory = factories[0];
        double X() => world.Number(factory, Builtin.Position, 0) + 1.5;
        double Y() => world.Number(factory, Builtin.Position, 1) + 1.5;
        // Звука ждём по числу отрендеренных отсчётов, а не по часам стенки: под нагрузкой кадров меньше, но отсчёты
        // считаются по времени кадров, и окно измерения всегда одной длины.
        void Watch(double seconds)
        {
            var target = session.Output.Count + (int)(seconds * Rate);
            while (session.Output.Count < target)
            {
                session.Look(X(), Y());
                session.Step();
                Thread.Sleep(10);
            }
        }

        Watch(1.5);
        var voices = session.Engine!.ActiveVoices;
        var before = BaseSession.Rms(session.Output, session.Output.Count - Rate, Rate);
        Assert.Equal(0f, session.Engine.LoopVolume("machinery"));
        session.Game.Send("produce", MemoryPack.MemoryPackSerializer.Serialize(new global::Base.Shared.Orders.ProduceOrder { Producer = factory, Unit = "builder" }));
        // Петля цеха заводится, когда хост (под нагрузкой он медленнее) начал производство и клиент это увидел. Меряем саму
        // петлю — громкость, которую звук машин просит у неё, а не уровень всего вывода: на него под нагрузкой влияет
        // остальной фон, и сравнение «до и после» выходило случайным.
        var started = DateTime.UtcNow.AddSeconds(60);
        while (session.Engine.LoopVolume("machinery") <= 0)
        {
            Assert.True(DateTime.UtcNow < started, "петля цеха не завелась");
            session.Look(X(), Y());
            session.Step();
            Thread.Sleep(10);
        }
        // Громкость петли — то, что звук машин просит прямо сейчас, и она гаснет, пока цех не двигался дольше WorkHold. Под
        // нагрузкой хост может стоять дольше этого между своими тиками, и единственный замер в конце окна попадал в паузу.
        // Берём наибольшую громкость за окно в секунду звука: она по-прежнему доказывает, что петля просит нужный уровень
        // у цеха посреди экрана.
        var level = session.Engine.LoopVolume("machinery");
        var windowEnd = session.Output.Count + Rate;
        while (session.Output.Count < windowEnd)
        {
            session.Look(X(), Y());
            session.Step();
            Thread.Sleep(10);
            level = Math.Max(level, session.Engine.LoopVolume("machinery"));
        }
        var after = BaseSession.Rms(session.Output, session.Output.Count - Rate, Rate);
        Report(string.Create(CultureInfo.InvariantCulture, $"Главное здание: петля цеха {level:F4}, фон {before:F5}, с цехом {after:F5}, голосов {voices} → {session.Engine.ActiveVoices}"));
        // Цех посреди экрана: громкость петли — 0,25 × (1 − e^(−близость/3)), близость у середины — около 1.
        Assert.True(level > 0.03, $"петля цеха тиха: {level}");
        Assert.Empty(session.Game.Errors);
    }

    [Fact]
    public void AmbienceBackgroundRuns()
    {
        // Быстрый прогон: кадры по 1/60 с, звук — столько же. Пульс и тревога на нуле, а фон живёт: ноты, даль, эхо.
        using var session = new BaseSession(synthetic: true);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (session.Units().Count < 3)
        {
            Assert.True(DateTime.UtcNow < deadline, "нет юнитов");
            session.Step();
            Thread.Sleep(10);
        }
        var voices = 0;
        for (var second = 0; second < 40; second++)
        {
            session.Run(1);
            voices = Math.Max(voices, session.Engine!.ActiveVoices);
        }
        var output = session.Output;
        Assert.True(output.Count >= 38 * Rate, $"отрендерено {output.Count}");
        // Подложка вступила: последние десять секунд громче первой.
        var first = BaseSession.Rms(output, 0, Rate);
        var last = BaseSession.Rms(output, output.Count - 10 * Rate, 10 * Rate);
        Assert.True(last > 2 * first, $"фон не вступил: {first} → {last}");
        Assert.True(voices >= 3, $"голосов в фоне {voices}: ноты не планируются");
        Assert.All(output, value => Assert.True(float.IsFinite(value)));
        Assert.True(output.Max(MathF.Abs) < 1f);
        Assert.Empty(session.Game.Errors);
        Assert.Empty(session.Log);
    }

    [Fact]
    public void UiClickMakesSound()
    {
        using var fonts = Fonts.Load();
        var engine = new AudioEngine(new AudioEngineOptions { Seed = 9 });
        BankFixture.Fill(engine);
        var sounds = new ModSounds(engine);
        var scene = new ClientScene(new Gui(fonts));
        var mod = new S.ClientContext();
        var writer = new AbiWriter();
        var input = new FrameInput();
        var interfaceSounds = new InterfaceAudio();
        var frame = 0;

        // Кадр мода: интерфейс (по желанию) и звук интерфейса, выход — в сцену и движок; звук — на сто миллисекунд.
        float[] Frame(ClientInput given, Action<S.ClientContext>? ui = null)
        {
            given = given with { Time = frame / 10.0, Width = 800, Height = 600 };
            scene.Gui.Begin(given.ToUi());
            input.Write(writer, new FrameClock(given.Time, 0.1, (ulong)frame, 0, 0.05, 1), given, scene.Camera, scene.Gui, scene.Atmosphere, [], engine.Now, sounds.TakeEnded());
            var output = mod.Frame(writer.Written, c =>
            {
                ui?.Invoke(c);
                interfaceSounds.Update(c);
            });
            ClientStream.Apply(output, scene, sounds, new Listener(0, 0, 32, 800, 600));
            scene.Gui.Finish();
            frame++;
            return engine.Render(4800).Left;
        }

        var empty = Frame(new ClientInput(), c => c.Ui.Box(new S.Style { Id = "t:panel", Anchor = new S.Anchor(Left: 10, Top: 10), Skin = S.Skin.Panel, Padding = 8 }, () =>
            c.Ui.Button("t:btn", S.Style.Button with { Height = 30 }, () => c.Ui.Text("OK"))));
        Assert.True(empty.Max(MathF.Abs) < 1e-6f, "до щелчка тихо");
        var rect = scene.Gui.RectOf("t:btn") ?? throw new Xunit.Sdk.XunitException("нет кнопки");
        var pointer = (rect.X + rect.W / 2, rect.Y + rect.H / 2);
        Frame(new ClientInput() with { Pointer = pointer }, Build);
        // Нажали: звук щелчка (Play click) — тон 1400 → 1100 Гц с щелчком в начале.
        var click = Frame(new ClientInput() with { Pointer = pointer, Pressed = PointerButtons.Left, Down = PointerButtons.Left }, Build);
        var rms = BaseSession.Rms(click, 0, click.Length);
        Assert.True(rms > 0.005, $"щелчка не слышно: {rms}");
        Assert.True(click.Max(MathF.Abs) <= 0.35f * 1.05f, "громче 0,35 нельзя");
        Frame(new ClientInput() with { Pointer = pointer, Released = PointerButtons.Left }, Build);
        Assert.Empty(scene.Errors);

        void Build(S.ClientContext c) => c.Ui.Box(new S.Style { Id = "t:panel", Anchor = new S.Anchor(Left: 10, Top: 10), Skin = S.Skin.Panel, Padding = 8 }, () =>
            c.Ui.Button("t:btn", S.Style.Button with { Height = 30 }, () => c.Ui.Text("OK")));
    }

    [Fact]
    public void AudioStageCostWithTwentyBuggies()
    {
        // Кадр клиента с 20 багги, которые едут: со звуком и без. Разница медиан — цена звука за кадр (обработчики
        // audio, разбор их команд движком, голоса); сам рендер идёт в аудиопотоке и сюда не входит.
        double Median(BaseSession session, out int voices)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            List<uint> units;
            while ((units = session.Units()).Count < 20)
            {
                Assert.True(DateTime.UtcNow < deadline, "нет юнитов");
                session.Step();
                Thread.Sleep(10);
            }
            var (x, y) = session.Centre(units);
            session.Move(units, x + 14, y + 6);
            var mods = new List<double>();
            voices = 0;
            for (var i = 0; i < 300; i++)
            {
                session.Look(x + 7, y + 3);
                session.Step();
                Thread.Sleep(8);
                if (i >= 60)
                    mods.Add(session.Game.Stats.Mods);
                voices = Math.Max(voices, session.Engine?.ActiveVoices ?? 0);
            }
            Assert.Empty(session.Game.Errors);
            mods.Sort();
            return mods[mods.Count / 2];
        }

        double with, without;
        int voices;
        using (var session = new BaseSession(audio: true, buggies: 20))
            with = Median(session, out voices);
        using (var session = new BaseSession(audio: false, buggies: 20))
            without = Median(session, out _);
        var report = string.Create(CultureInfo.InvariantCulture, $"Звук base, 20 багги едут: моды за кадр со звуком {with:F3} мс, без звука {without:F3} мс, звук ≈ {with - without:F3} мс; голосов до {voices}");
        Report(report);
        Assert.True(with - without < 4, report);
    }
}
