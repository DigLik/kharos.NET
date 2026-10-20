using System.Collections.Concurrent;
using System.Diagnostics;
using Kharos.Content;
using Kharos.Host;
using Kharos.Protocol;
using KHost = Kharos.Host.Host;
using KHostOptions = Kharos.Host.HostOptions;

namespace Kharos.Server;

/// <summary>Настройки цикла хоста.</summary>
public sealed record HostLoopOptions
{
    /// <summary>Длина тика по часам (шаг хоста, 20 Гц).</summary>
    public TimeSpan Step { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Как часто сохраняется мир (30 с); нулевое — только по команде и при остановке.</summary>
    public TimeSpan SaveEvery { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Как часто в журнал идёт строка о состоянии (5 с); нулевое — не идёт.</summary>
    public TimeSpan ReportEvery { get; init; } = TimeSpan.FromSeconds(5);
}

/// <summary>
/// Поток хоста: создаёт <see cref="KHost"/> у себя (моды Wasmtime вызываются только оттуда), входит в мир из
/// сохранения, гоняет <see cref="HostSession.Step"/> на 20 Гц, сохраняет раз в 30 с и при остановке. Всё остальное
/// говорит с хостом через <see cref="Post"/> и <see cref="Invoke{T}"/>: они выполняются в потоке хоста между тиками.
/// </summary>
public sealed class HostLoop : IDisposable
{
    private readonly GameContent _content;
    private readonly KHostOptions _hostOptions;
    private readonly HostSessionOptions _sessionOptions;
    private readonly string _savePath;
    private readonly Action<HostSession>? _setup;
    private readonly HostLoopOptions _options;
    private readonly Action<string> _log;
    private readonly ConcurrentQueue<Action<HostSession>> _queue = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly ManualResetEventSlim _ready = new();
    private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private volatile bool _stop;
    private Task _writing = Task.CompletedTask;
    private long _ticks;

    private HostLoop(GameContent content, KHostOptions hostOptions, HostSessionOptions sessionOptions, string savePath, Action<HostSession>? setup, HostLoopOptions options, Action<string> log)
    {
        _content = content;
        _hostOptions = hostOptions;
        _sessionOptions = sessionOptions;
        _savePath = savePath;
        _setup = setup;
        _options = options;
        _log = log;
        _thread = new Thread(Run) { Name = "Kharos host", IsBackground = true };
    }

    /// <summary>Запускает поток и ждёт, пока хост создан и вошёл в мир; ошибка — исключение здесь же.</summary>
    public static HostLoop Start(GameContent content, KHostOptions hostOptions, HostSessionOptions sessionOptions, string savePath, Action<HostSession>? setup, HostLoopOptions? options, Action<string> log)
    {
        var loop = new HostLoop(content, hostOptions, sessionOptions, savePath, setup, options ?? new HostLoopOptions(), log);
        loop._thread.Start();
        loop._ready.Wait();
        if (loop.Failure is { } failure)
        {
            loop.Dispose();
            throw failure is SettingsException or HostException ? failure : new HostException($"хост не запустился: {failure.Message}", failure);
        }
        return loop;
    }

    public long Ticks => Interlocked.Read(ref _ticks);

    /// <summary>Чем остановился поток хоста; null — штатно или ещё идёт.</summary>
    public Exception? Failure { get; private set; }

    /// <summary>Кончился поток хоста (после последнего сохранения).</summary>
    public Task Finished => _finished.Task;

    /// <summary>Время тика с рассылкой, мс: последнего и среднего.</summary>
    public double LastTickMs => Volatile.Read(ref _lastTickMs);
    private double _lastTickMs;

    /// <summary>Выполнить в потоке хоста перед следующим тиком, не дожидаясь.</summary>
    public void Post(Action<HostSession> action)
    {
        _queue.Enqueue(action);
        _wake.Release();
    }

    /// <summary>Выполнить в потоке хоста и вернуть результат.</summary>
    public Task<T> Invoke<T>(Func<HostSession, T> function)
    {
        var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_stop || _finished.Task.IsCompleted)
        {
            source.SetException(new InvalidOperationException("сервер остановлен"));
            return source.Task;
        }
        Post(session =>
        {
            try
            {
                source.SetResult(function(session));
            }
            catch (Exception error)
            {
                source.SetException(error);
            }
        });
        return source.Task;
    }

    /// <summary>Сохраняет мир сейчас; возвращается, когда файл записан.</summary>
    public async Task SaveAsync() => await await Invoke(session => Save(session));

    /// <summary>Останавливает поток: последнее сохранение, закрытие подключений, закрытие хоста.</summary>
    public void Stop()
    {
        _stop = true;
        _wake.Release();
        if (Thread.CurrentThread != _thread && _thread.IsAlive)
            _thread.Join();
    }

    public void Dispose()
    {
        Stop();
        _ready.Dispose();
        _wake.Dispose();
    }

    private void Run()
    {
        KHost? host = null;
        HostSession? session = null;
        try
        {
            host = Open();
            session = new HostSession(host, _sessionOptions);
            _setup?.Invoke(session);
        }
        catch (Exception error)
        {
            Failure = error;
            host?.Dispose();
            _ready.Set();
            _finished.TrySetResult();
            return;
        }
        _ready.Set();
        try
        {
            Loop(session);
        }
        catch (Exception error)
        {
            Failure = error;
            _log($"хост остановился: {error}");
        }
        finally
        {
            try
            {
                Save(session).GetAwaiter().GetResult();
                _log($"мир сохранён в {_savePath}");
            }
            catch (Exception error)
            {
                _log($"мир не сохранён: {error.Message}");
            }
            foreach (var connection in session.Connections.ToList())
                session.Disconnect(connection);
            host.Dispose();
            _finished.TrySetResult();
        }
    }

    /// <summary>
    /// Хост из сохранения (тик, случайность, игроки, обзор, память модов — всё у хоста) или новый с началом партии.
    /// Сохранение другой версии откладывается в <c>&lt;имя&gt;.v&lt;версия&gt;</c>, нечитаемое — в <c>.old</c>: мир начинается заново.
    /// </summary>
    private KHost Open()
    {
        var bytes = SaveFiles.Read(_savePath);
        if (bytes is not null)
        {
            try
            {
                var loaded = KHost.Load(_content, bytes, _hostOptions);
                _log($"мир загружен из {_savePath}: тик {loaded.Tick}, сущностей {loaded.World.Count}, игроков {loaded.Roster.Count}");
                return loaded;
            }
            catch (SaveVersionException error)
            {
                SaveFiles.AsideVersion(_savePath, error.Found, _log);
            }
            catch (HostException error)
            {
                SaveFiles.AsideBroken(_savePath, error.Message, _log);
            }
        }
        var host = new KHost(_content, _hostOptions);
        host.Start();
        _log($"новый мир, партия {host.Game.Name}, моды: {string.Join(", ", host.Content.Mods.Select(mod => mod.Name))}");
        return host;
    }

    private void Loop(HostSession session)
    {
        var clock = Stopwatch.StartNew();
        var step = _options.Step.TotalMilliseconds;
        var next = step;
        var nextSave = _options.SaveEvery.TotalMilliseconds;
        var nextReport = _options.ReportEvery.TotalMilliseconds;
        double busy = 0, worst = 0;
        long ticks = 0;
        while (!_stop)
        {
            var wait = next - clock.Elapsed.TotalMilliseconds;
            if (wait > 0)
            {
                // Просыпаемся и на задания: команды консоли не ждут тика целиком.
                _wake.Wait(TimeSpan.FromMilliseconds(wait));
                Drain(session);
                if (clock.Elapsed.TotalMilliseconds < next)
                    continue;
            }
            Drain(session);
            if (_stop)
                break;
            var started = Stopwatch.GetTimestamp();
            session.Step();
            var spent = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Volatile.Write(ref _lastTickMs, spent);
            Interlocked.Increment(ref _ticks);
            busy += spent;
            worst = Math.Max(worst, spent);
            ticks++;
            next += step;
            // Отстали больше чем на пять тиков (сон ОС, отладчик) — не догонять залпом.
            if (clock.Elapsed.TotalMilliseconds - next > step * 5)
                next = clock.Elapsed.TotalMilliseconds + step;
            var now = clock.Elapsed.TotalMilliseconds;
            if (_options.SaveEvery > TimeSpan.Zero && now >= nextSave)
            {
                nextSave = now + _options.SaveEvery.TotalMilliseconds;
                _ = Save(session);
            }
            if (_options.ReportEvery > TimeSpan.Zero && now >= nextReport)
            {
                nextReport = now + _options.ReportEvery.TotalMilliseconds;
                // Худший тик и память процесса — для нагрузочного смоука (Н7.3, ворота: худший ≤ 50 мс, память не растёт).
                if (session.Connections.Count > 0)
                    _log($"клиентов {session.Connections.Count}, сущностей {session.Host.World.Count}, тик {(ticks == 0 ? 0 : busy / ticks):0.00} мс, " +
                        $"худший {worst:0.00} мс, память {Environment.WorkingSet / (1024 * 1024)} МБ, куча {GC.GetTotalMemory(false) / (1024 * 1024)} МБ");
                busy = 0;
                worst = 0;
                ticks = 0;
            }
        }
        Drain(session);
    }

    private void Drain(HostSession session)
    {
        while (_queue.TryDequeue(out var action))
        {
            try
            {
                action(session);
            }
            catch (Exception error)
            {
                _log($"сбой команды на хосте: {error.Message}");
            }
        }
    }

    /// <summary>Снимок мира — в потоке хоста (копии), сжатие и запись файла — следом в фоне, по очереди.</summary>
    private Task Save(HostSession session) => SaveFromHost(session);

    /// <summary>Сохранение из потока хоста (команда консоли); завершается, когда файл записан.</summary>
    public Task SaveFromHost(HostSession session)
    {
        var captured = HostSave.Capture(session.Host);
        var previous = _writing;
        var write = Task.Run(async () =>
        {
            // Упавшая запись не должна оставаться в цепочке навсегда: следующая всё равно пойдёт.
            try { await previous.ConfigureAwait(false); } catch (Exception) { }
            SaveFiles.Write(_savePath, captured.Encode());
        });
        _writing = write.ContinueWith(task =>
        {
            if (task.IsFaulted)
                _log($"мир не сохранён: {task.Exception!.GetBaseException().Message}");
        }, TaskScheduler.Default);
        return write;
    }
}
