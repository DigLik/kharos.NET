using Kharos.Content;
using Kharos.Engine.World;
using Kharos.Host;
using Kharos.Plugins.Game;
using KHost = Kharos.Host.Host;
using KHostOptions = Kharos.Host.HostOptions;

namespace Kharos.Base.Scenario;

/// <summary>
/// Сценарий base на хосте (docs/dotnet-n5.md, Н5.6): аналог <c>createSim</c> и <c>sim.advance</c> из packages/tests.
/// Партия идёт настоящим хостом с модом base — в режиме CLR (по умолчанию, быстро, с отладчиком) или на host.wasm
/// (<c>KHAROS_MODS=wasm</c>); тесты не знают разницы, мир совпадает до байта.
/// <code>
/// using var sim = Sim.Create(new SimOptions { Size = 256, Rules = new { techTree = false } });
/// var unit = sim.PutUnit("builder", 1, 10.5, 10.5);        // правка edit.unit, сразу применена
/// sim.Send(1, new MoveOrder { Units = [unit], X = 20, Y = 10 });
/// sim.Seconds(5);
/// Assert.True(sim.Until(() => sim.At(unit).X > 19, limit: 30));
/// var after = sim.ThroughSave();                           // Host.Save → Host.Load, как throughJson
/// </code>
/// Устройство: <c>Sim.cs</c> — партия, ход времени, ошибки; <c>Sim.Orders.cs</c> — приказы и запросы;
/// <c>Sim.World.cs</c> — чтение и запись компонентов по именам; <c>Sim.Edit.cs</c> — правки (edit.*);
/// <c>Sim.Save.cs</c> — сохранение. Разделы base дополняют класс своим <c>Sim.&lt;Раздел&gt;.cs</c> (partial): свои
/// помощники сцен, поиск зданий и юнитов — рядом с тестами раздела, общее не трогают. Вызовы — из одного потока.
/// </summary>
public sealed partial class Sim : IDisposable
{
    /// <summary>Длина тика, секунды (20 Гц, как TICK в harness).</summary>
    public const double Dt = 0.05;

    private static readonly Lazy<ModPackage> Package = new(() => ModPackage.Read(ModDir));

    private readonly SimOptions _options;
    private readonly KHostOptions _hostOptions;
    private readonly List<string> _log;
    private int _logged;
    private long _ticks;
    private int _pending;

    private Sim(KHost host, SimOptions options, KHostOptions hostOptions, List<string> log)
    {
        Host = host;
        _options = options;
        _hostOptions = hostOptions;
        _log = log;
    }

    // ===== Поиск мода =====

    /// <summary>Папка dotnet/ (где Kharos.slnx); KHAROS_DOTNET_ROOT — явно.</summary>
    public static string DotnetRoot
    {
        get
        {
            if (Environment.GetEnvironmentVariable("KHAROS_DOTNET_ROOT") is { Length: > 0 } root)
                return root;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "Kharos.slnx")))
                    return dir.FullName;
            throw new DirectoryNotFoundException("не найден Kharos.slnx выше " + AppContext.BaseDirectory);
        }
    }

    /// <summary>Папка мода base (mod.json, data, host.wasm).</summary>
    public static string ModDir => Path.Combine(DotnetRoot, "mods", "base");

    /// <summary>Режим из KHAROS_MODS (wasm | clr); не задан — CLR.</summary>
    public static ModMode DefaultMode => ModModes.FromEnvironment();

    /// <summary>Почему сценарий в этом режиме не запустить (нет host.wasm или Base.Host.dll); null — можно.</summary>
    public static string? Unavailable(ModMode mode) => mode switch
    {
        ModMode.Wasm when !File.Exists(Path.Combine(ModDir, "host.wasm")) =>
            "mods/base/host.wasm не собран: sh dotnet/tools/wasi-sdk.sh и dotnet publish mods/base/Base.Host -c Release -p:KharosWasm=true (или pwsh dotnet/tools/build-mods.ps1 -Mod base)",
        ModMode.Clr when ClrModule.Find(ModDir) is null => "Base.Host.dll не собран: dotnet build dotnet/Kharos.slnx",
        _ => null,
    };

    // ===== Создание =====

    /// <summary>
    /// Партия: хост с модом base, <c>Start</c>, вход игроков <see cref="SimOptions.Players"/>. Мод не собран в этом режиме —
    /// <see cref="SimUnavailableException"/> (тесту — <c>Assert.SkipWhen(Sim.Unavailable(mode) is not null, …)</c>).
    /// </summary>
    public static Sim Create(SimOptions? options = null)
    {
        options ??= new SimOptions();
        var mode = options.Mode ?? DefaultMode;
        if (Unavailable(mode) is { } reason)
            throw new SimUnavailableException(reason);
        var log = new List<string>();
        var hostOptions = new KHostOptions
        {
            Game = options.Game,
            Settings = options.ToSettings(),
            ModMode = mode,
            Seed = options.Seed,
            Modules = new GameModuleOptions { CallTimeoutMs = options.CallTimeoutMs },
            Log = log.Add,
        };
        var host = new KHost(Content(), hostOptions);
        var sim = new Sim(host, options, hostOptions, log);
        host.Start();
        foreach (var player in options.Players)
            host.Join(player);
        sim.Throw();
        return sim;
    }

    /// <summary>Содержимое одного мода base (разбор data/*.json и схемы — каждый раз заново, пакет читается один раз).</summary>
    public static GameContent Content() => GameContent.Load([Package.Value]);

    // ===== Состояние =====

    /// <summary>Хост партии: прямой доступ к миру, слоям, планировщику (для тестов движка и замеров).</summary>
    public KHost Host { get; }

    /// <summary>Мир партии.</summary>
    public GameWorld World => Host.World;

    /// <summary>Местность партии; null — у игры нет карты.</summary>
    public Kharos.Engine.Terrain.Land? Land => Host.Land;

    /// <summary>Как исполняется код мода.</summary>
    public ModMode Mode => Host.ModMode;

    /// <summary>Параметры, с которыми партия создана.</summary>
    public SimOptions Options => _options;

    /// <summary>Номер тика планировщика (правки <c>edit.*</c> тика не двигают).</summary>
    public ulong Tick => Host.Tick;

    /// <summary>Прошедшее время игры, секунды: тики <see cref="Advance"/> × <see cref="Dt"/>; нулевые тики правок не в счёт.</summary>
    public double Time => _ticks * Dt;

    /// <summary>Прошло тиков <see cref="Advance"/>.</summary>
    public long Ticks => _ticks;

    /// <summary>Журнал хоста: ошибки систем и модов, выключение модов (всё с начала партии).</summary>
    public IReadOnlyList<string> Log => _log;

    /// <summary>Сигналы клиентам за всё время (накапливаются, пока не взять <see cref="TakeSignals()"/>).</summary>
    public List<SimSignal> Signals { get; } = [];

    // ===== Ход времени =====

    /// <summary>Сколько тиков в секундах: как <c>Math.round(time / TICK)</c> в harness.</summary>
    public static int TicksIn(double seconds) => (int)Math.Round(seconds / Dt, MidpointRounding.AwayFromZero);

    /// <summary>Прокручивает партию на <paramref name="ticks"/> тиков.</summary>
    public void Advance(int ticks = 1)
    {
        for (var i = 0; i < ticks; i++)
        {
            Host.Step();
            _ticks++;
            _pending = 0;
            Drain();
            Throw();
        }
    }

    /// <summary>Прокручивает партию на <paramref name="seconds"/> секунд (20 тиков в секунду).</summary>
    public void Seconds(double seconds) => Advance(TicksIn(seconds));

    /// <summary>
    /// Крутит партию, пока условие не выполнится; false — не дождались за <paramref name="limit"/> секунд. Условие
    /// проверяется перед каждым тиком, как в <c>until</c> харнесса.
    /// </summary>
    public bool Until(Func<bool> done, double limit = 300)
    {
        var steps = TicksIn(limit);
        for (var i = 0; i < steps; i++)
        {
            if (done())
                return true;
            Advance();
        }
        return false;
    }

    /// <summary>
    /// Применяет отправленные приказы, не двигая время: <c>Host.Apply</c> — без тика, системы не работают, обработчики
    /// видят dt = 0. Так правки <c>edit.*</c> дают сразу готовый мир, а номер новой сущности — известным. С
    /// <see cref="SimOptions.ZeroTimeEdits"/> = false — настоящий тик.
    /// </summary>
    public void Flush()
    {
        if (!_options.ZeroTimeEdits)
        {
            Advance();
            return;
        }
        Host.Apply();
        _pending = 0;
        Drain();
        Throw();
    }

    /// <summary>Ввести игрока в партию (событие <c>player_joined</c>); уже вошедший — false.</summary>
    public bool Join(uint player)
    {
        var joined = Host.Join(player);
        Throw();
        return joined;
    }

    /// <summary>Сигналы с этим именем, накопленные с прошлого раза; остальные остаются.</summary>
    public List<SimSignal> TakeSignals(string name)
    {
        var taken = Signals.Where(signal => signal.Name == name).ToList();
        Signals.RemoveAll(signal => signal.Name == name);
        return taken;
    }

    /// <summary>Все накопленные сигналы; список очищается.</summary>
    public List<SimSignal> TakeSignals()
    {
        var taken = new List<SimSignal>(Signals);
        Signals.Clear();
        return taken;
    }

    // ===== Внутреннее =====

    /// <summary>Забирает изменения хоста (иначе копятся) и сигналы тика.</summary>
    private void Drain()
    {
        var delta = Host.TakeDelta();
        foreach (var signal in delta.Signals)
            Signals.Add(new SimSignal(Host.Tick, signal.Name, signal.Payload, signal.To, signal.At));
    }

    private void Throw()
    {
        if (_log.Count == _logged)
            return;
        var errors = _log.Skip(_logged).ToList();
        _logged = _log.Count;
        if (_options.ThrowOnError)
            throw new SimErrorException(errors);
    }

    public void Dispose() => Host.Dispose();
}
