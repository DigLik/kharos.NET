using System.Text.Json;
using Kharos.Client.Game;
using Kharos.Client.Shell;
using Kharos.Host;
using Kharos.Protocol;
using HostGame = Kharos.Host.Host;

namespace Kharos.Desktop;

/// <summary>
/// Редактор карты из меню (Н6-3, editor.ts): мир слота или файла поднимается локальным хостом без тумана
/// (<see cref="EditorMap.WithoutFog"/>) и стоит — хост идёт ручным ходом (<see cref="LocalGame.Advance"/>), тик — только
/// когда клиент что-то послал (правку, строку чата), чтобы она выполнилась. «Сохранить» снимает мир между тиками и
/// возвращает ему настройки и разведанное исходного (<see cref="EditorMap.Restore"/>); уход ничего не сохраняет.
/// </summary>
internal sealed class EditorSession : IGameSession, IEditorSession
{
    private readonly DesktopLauncher _launcher;
    private readonly Task<LocalGame> _start;
    private readonly string? _settings;
    private readonly byte[]? _original;
    private SignalTransport? _transport;
    private LocalGame? _local;
    private int _tail;

    public EditorSession(DesktopLauncher launcher, EditorLaunch launch, SaveSlots slots)
    {
        (_launcher, Launch) = (launcher, launch);
        string? game = null;
        var seed = 1UL;
        if (launch.Slot is { } slot)
        {
            _original = slots.Load(slot.Id);
            (game, _settings, seed) = (slot.Game, slot.Settings, (ulong)Math.Abs(slot.Seed) + 1);
        }
        else if (launch.File is { } file)
            _original = File.ReadAllBytes(file);
        if (_original is not null)
            _settings = SaveInfo.Read(_original).Settings;
        Status = launch.Name;
        var original = _original;
        var settings = EditorMap.WithoutFog(_settings);
        _start = Task.Run(() =>
        {
            var content = launcher.Content;
            var hostOptions = new HostOptions
            {
                Game = game ?? launcher.DefaultGame,
                Settings = JsonDocument.Parse(settings).RootElement.Clone(),
                Seed = seed,
                Log = DesktopLog.Write,
            };
            return LocalGame.Start(() => original is null ? new HostGame(content, hostOptions) : HostGame.Load(content, original, hostOptions), manual: true);
        });
    }

    public Launch Launch { get; }
    public SessionState State { get; private set; } = SessionState.Connecting;
    public string? Status { get; }
    public string? Error { get; private set; }
    public bool WrongPassword => false;
    public ClientGame? Game { get; private set; }

    public void Poll()
    {
        if (State == SessionState.Failed)
            return;
        if (Game is null)
        {
            if (!_start.IsCompleted)
                return;
            if (_start.Exception is { } failure)
            {
                Fail($"редактор: хост не запустился: {failure.GetBaseException().Message}");
                return;
            }
            _local = _start.Result;
            _transport = new SignalTransport(_local.Client);
            try
            {
                Game = new ClientGame(_launcher.Content, _transport, _launcher.ClientOptions);
                State = SessionState.Ready;
            }
            catch (ClientException error)
            {
                Fail(error.Message);
            }
            return;
        }
        if (_local?.Failure is { } stopped)
        {
            Fail($"редактор: хост остановился: {stopped.Message}");
            return;
        }
        // Мир стоит: тик — только чтобы выполнить посланное клиентом, и ещё один на следующем кадре: посланное могло
        // дойти до хоста уже после тика, а ответ хоста (право редактора на /editor) — уйти только со следующим.
        if (_local is { } local && _transport is { } transport)
        {
            if (transport.TakeSent())
                _tail = 2;
            if (_tail > 0)
            {
                _tail--;
                local.Advance(local.Ticks + 1);
            }
        }
    }

    /// <summary>Мир между тиками (ручной ход: хост ждёт следующего) — с настройками и разведанным исходного.</summary>
    public byte[]? SaveMap()
    {
        if (_local is not { Host: { } host, Failure: null })
            return null;
        var save = EditorMap.Restore(host.Save(), _settings, _original);
        DesktopLog.Write($"редактор: мир сохранён, {save.Length / 1024} КБ");
        return save;
    }

    private void Fail(string error)
    {
        DesktopLog.Write(error);
        (Error, State) = (error, SessionState.Failed);
    }

    public void Retry(string password) { }

    public void Leave() { }

    public void Dispose()
    {
        Game?.Dispose();
        Game = null;
        if (_local is not null)
            _local.Dispose();
        else
            _start.ContinueWith(task => task.Result.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    /// <summary>Транспорт клиента, что помечает, посылал ли клиент что-нибудь хосту.</summary>
    private sealed class SignalTransport(MemoryTransport inner) : ITransport
    {
        private int _sent;

        public bool Closed => inner.Closed;

        public void Send(byte[] message)
        {
            inner.Send(message);
            // Экран (View) уходит каждый кадр — тика ради него не нужно.
            if (message.Length > 0 && message[0] != (byte)MessageType.View)
                Interlocked.Exchange(ref _sent, 1);
        }

        /// <summary>Было ли что-то послано с прошлого вызова.</summary>
        public bool TakeSent() => Interlocked.Exchange(ref _sent, 0) == 1;

        public bool TryReceive([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out byte[]? message) => inner.TryReceive(out message);

        public ValueTask<byte[]?> ReceiveAsync(CancellationToken cancel = default) => inner.ReceiveAsync(cancel);

        public void Dispose() => inner.Dispose();
    }
}
