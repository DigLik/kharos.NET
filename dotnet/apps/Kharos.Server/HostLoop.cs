using System.Collections.Concurrent;
using System.Diagnostics;
using Kharos.Engine.World;
using Kharos.Host;
using Kharos.Protocol;
using KHost = Kharos.Host.Host;

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

/// <summary>Сохранение не подошло миру хоста.</summary>
internal sealed class RestoreException(string message) : Exception(message);

/// <summary>
/// Поток хоста: создаёт <see cref="KHost"/> у себя (моды Wasmtime вызываются только оттуда), входит в мир из
/// сохранения, гоняет <see cref="HostSession.Step"/> на 20 Гц, сохраняет раз в 30 с и при остановке. Всё остальное
/// говорит с хостом через <see cref="Post"/> и <see cref="Invoke{T}"/>: они выполняются в потоке хоста между тиками.
/// </summary>
public sealed class HostLoop : IDisposable
{
    private readonly Func<KHost> _create;
    private readonly ISaveStore _store;
    private readonly IPlayerRegistry _registry;
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

    private HostLoop(Func<KHost> create, ISaveStore store, IPlayerRegistry registry, HostLoopOptions options, Action<string> log)
    {
        _create = create;
        _store = store;
        _registry = registry;
        _options = options;
        _log = log;
        _thread = new Thread(Run) { Name = "Kharos host", IsBackground = true };
    }

    /// <summary>Запускает поток и ждёт, пока хост создан и вошёл в мир; ошибка — исключение здесь же.</summary>
    public static HostLoop Start(Func<KHost> create, ISaveStore store, IPlayerRegistry registry, HostLoopOptions? options, Action<string> log)
    {
        var loop = new HostLoop(create, store, registry, options ?? new HostLoopOptions(), log);
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
            host = _create();
            session = new HostSession(host);
            try
            {
                Enter(host, session);
            }
            catch (RestoreException error)
            {
                // Хост уже тронут сохранением, которое ему не подошло: откладываем файл и поднимаем хост заново с чистого мира.
                _store.Reject(error.Message, _log);
                host.Dispose();
                host = _create();
                session = new HostSession(host);
                Enter(host, session);
            }
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
                _log($"мир сохранён в {_store.Description}");
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
    /// Начало: партия и мир. Игроки из сохранения входят до подмены мира: событие входа ставит их стартовые юниты, а
    /// мир из сохранения затем заменяет всё. ШОВ: загрузка сохранения нового хоста вместо <see cref="WorldRestore"/>.
    /// </summary>
    private void Enter(KHost host, HostSession session)
    {
        var saved = _store.Load(_log);
        host.Start();
        if (saved is null)
        {
            _log($"новый мир, партия {host.Game.Name}, моды: {string.Join(", ", host.Content.Mods.Select(mod => mod.Name))}");
            return;
        }
        try
        {
            foreach (var record in saved.Players)
                host.Join(record.Player);
            WorldRestore.Apply(host.World, saved.World);
        }
        catch (FormatException error)
        {
            throw new RestoreException(error.Message);
        }
        _registry.Restore(saved.Players);
        session.Broadcast();
        _log($"мир загружен из {_store.Description}: сущностей {host.World.Count}, игроков {saved.Players.Count}");
    }

    private void Loop(HostSession session)
    {
        var clock = Stopwatch.StartNew();
        var step = _options.Step.TotalMilliseconds;
        var next = step;
        var nextSave = _options.SaveEvery.TotalMilliseconds;
        var nextReport = _options.ReportEvery.TotalMilliseconds;
        double busy = 0;
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
                if (session.Connections.Count > 0)
                    _log($"клиентов {session.Connections.Count}, сущностей {session.Host.World.Count}, тик {(ticks == 0 ? 0 : busy / ticks):0.00} мс");
                busy = 0;
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

    /// <summary>Снимок мира — в потоке хоста, запись файла — следом в фоне, по очереди.</summary>
    private Task Save(HostSession session)
    {
        var host = session.Host;
        var snapshot = new SaveSnapshot(Welcome.ProtocolVersion, host.Tick, _registry.Players, WorldSave.Save(host.World));
        var previous = _writing;
        var write = Task.Run(async () =>
        {
            // Упавшая запись не должна оставаться в цепочке навсегда: следующая всё равно пойдёт.
            try { await previous.ConfigureAwait(false); } catch (Exception) { }
            _store.Write(snapshot);
        });
        _writing = write.ContinueWith(task =>
        {
            if (task.IsFaulted)
                _log($"мир не сохранён: {task.Exception!.GetBaseException().Message}");
        }, TaskScheduler.Default);
        return write;
    }
}
