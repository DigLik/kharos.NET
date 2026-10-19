using System.Diagnostics;
using Kharos.Protocol;

namespace Kharos.Host;

/// <summary>
/// Одиночная игра (Р10): хост в процессе клиента, в своём потоке, 20 тиков в секунду; клиент говорит с ним тем же
/// протоколом, что с сервером, через транспорт в памяти (<see cref="Client"/>). Хост создаётся и живёт в своём
/// потоке — моды Wasmtime вызываются только оттуда.
/// <code>
/// using var game = LocalGame.Start(() => new Host(GameContent.Load(modsDir)));
/// var replica = new Replica();
/// // каждый кадр:
/// replica.Pump(game.Client);
/// game.Client.Send(DeltaCodec.Order("hello.spawn", payload));
/// </code>
/// </summary>
public sealed class LocalGame : IDisposable
{
    private readonly Func<Host> _create;
    private readonly MemoryTransport _hostEnd;
    private readonly ManualResetEventSlim _stop = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly Thread _thread;
    private readonly bool _startGame;
    private readonly bool _manual;
    private readonly SemaphoreSlim _budget = new(0);
    private readonly object _tickGate = new();
    private readonly CancellationTokenSource _cancel = new();
    private readonly HostSessionOptions _sessionOptions;
    private long _ticks;
    private double _lastTickMs, _maxTickMs, _totalTickMs;

    private LocalGame(Func<Host> create, uint player, TimeSpan step, bool startGame, HostSessionOptions? session, bool manual)
    {
        _manual = manual;
        // Одиночной игре канал не тесен: без бюджета байт, вестей о входе и счёта сообщений.
        _sessionOptions = session ?? new HostSessionOptions { SendRate = 0, Local = true };
        _create = create;
        Player = player;
        Step = step;
        _startGame = startGame;
        (_hostEnd, var client) = MemoryTransport.Pair();
        Client = client;
        _thread = new Thread(Run) { Name = "Kharos host", IsBackground = true };
    }

    /// <summary>
    /// Запускает хост в своём потоке: создание (create — в потоке хоста), начало партии, вход игрока, приветствие и
    /// снимок клиенту — и ждёт, пока это сделано. Ошибка создания — исключение здесь же.
    /// </summary>
    public static LocalGame Start(Func<Host> create, uint player = 1, TimeSpan? step = null, bool startGame = true, HostSessionOptions? session = null, bool manual = false)
    {
        var game = new LocalGame(create, player, step ?? TimeSpan.FromMilliseconds(50), startGame, session, manual);
        game._thread.Start();
        game._ready.Wait();
        if (game.Failure is { } failure)
        {
            game.Dispose();
            throw new HostException($"хост не запустился: {failure.Message}", failure);
        }
        return game;
    }

    /// <summary>Конец клиента: сообщения хоста и приказы.</summary>
    public MemoryTransport Client { get; }

    public uint Player { get; }

    /// <summary>Длина тика по часам.</summary>
    public TimeSpan Step { get; }

    /// <summary>Идёт ли поток хоста.</summary>
    public bool Running => _thread.IsAlive;

    /// <summary>Чем остановился поток хоста; null — ничем.</summary>
    public Exception? Failure { get; private set; }

    public long Ticks => Interlocked.Read(ref _ticks);

    /// <summary>Время тика с рассылкой, мс: последнего, наибольшего, среднего.</summary>
    public double LastTickMs => Volatile.Read(ref _lastTickMs);
    public double MaxTickMs => Volatile.Read(ref _maxTickMs);
    public double AverageTickMs => Ticks == 0 ? 0 : Volatile.Read(ref _totalTickMs) / Ticks;

    /// <summary>Хост на время работы — только для потока хоста; после <see cref="Stop"/> — для разбора (ошибки, мир).</summary>
    public Host? Host { get; private set; }

    /// <summary>
    /// Ручной ход (запись роликов): тики идут не по часам, а по <see cref="Advance"/>, и мир не зависит от скорости кадров.
    /// </summary>
    public bool Manual => _manual;

    /// <summary>
    /// Ручной ход: довести хост до тика <paramref name="ticks"/> (всего с начала партии) и дождаться этого. Без ручного
    /// хода или при остановленном хосте ничего не делает.
    /// </summary>
    public void Advance(long ticks)
    {
        if (!_manual)
            return;
        lock (_tickGate)
        {
            var missing = ticks - Interlocked.Read(ref _ticks) - _budget.CurrentCount;
            if (missing > 0)
                _budget.Release((int)missing);
            while (Interlocked.Read(ref _ticks) < ticks && _thread.IsAlive && !_stop.IsSet)
                Monitor.Wait(_tickGate, 20);
        }
    }

    /// <summary>Сессия хоста — только для потока хоста; после <see cref="Stop"/> — для разбора.</summary>
    public HostSession? Session { get; private set; }

    /// <summary>Останавливает поток хоста и ждёт его; хост остаётся до <see cref="Dispose"/>.</summary>
    public void Stop()
    {
        _stop.Set();
        _cancel.Cancel();
        if (_thread.IsAlive && Thread.CurrentThread != _thread)
            _thread.Join();
    }

    public void Dispose()
    {
        Stop();
        Host?.Dispose();
        Host = null;
        Client.Dispose();
        _stop.Dispose();
        _cancel.Dispose();
        _budget.Dispose();
        _ready.Dispose();
    }

    private void Run()
    {
        HostSession? session = null;
        try
        {
            var host = _create();
            Host = host;
            if (_startGame && !host.Started)
                host.Start();
            session = new HostSession(host, _sessionOptions);
            Session = session;
            session.Connect(_hostEnd, Player);
        }
        catch (Exception error)
        {
            Failure = error;
            _ready.Set();
            return;
        }
        _ready.Set();
        try
        {
            var clock = Stopwatch.StartNew();
            var step = Step.TotalMilliseconds;
            var next = step;
            while (true)
            {
                if (_manual)
                {
                    // Тик по приказу: Stop будит ожидание через токен.
                    try
                    {
                        _budget.Wait(_cancel.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
                else
                {
                    var wait = next - clock.Elapsed.TotalMilliseconds;
                    if (wait > 0 && _stop.Wait(TimeSpan.FromMilliseconds(wait)))
                        break;
                }
                if (_stop.IsSet)
                    break;
                var started = Stopwatch.GetTimestamp();
                session.Step();
                var spent = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                Volatile.Write(ref _lastTickMs, spent);
                Volatile.Write(ref _maxTickMs, Math.Max(_maxTickMs, spent));
                Volatile.Write(ref _totalTickMs, _totalTickMs + spent);
                Interlocked.Increment(ref _ticks);
                if (_manual)
                {
                    lock (_tickGate)
                        Monitor.PulseAll(_tickGate);
                    if (_hostEnd.Closed)
                        break;
                    continue;
                }
                next += step;
                // Отстали больше чем на пять тиков (отладчик, сон ОС) — не догонять залпом.
                if (clock.Elapsed.TotalMilliseconds - next > step * 5)
                    next = clock.Elapsed.TotalMilliseconds + step;
                if (_hostEnd.Closed)
                    break;
            }
        }
        catch (Exception error)
        {
            Failure = error;
        }
        finally
        {
            _hostEnd.Dispose();
        }
    }
}
