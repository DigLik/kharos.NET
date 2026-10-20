using System.Diagnostics;
using Kharos.Client.Game;
using Kharos.Content;
using Kharos.Protocol;

namespace Kharos.Client.Net;

/// <summary>
/// Вход в партию хоста до <see cref="ClientGame"/>: приветствие, моды хоста, снимок. Моды берутся из своих (local —
/// если их пакет совпал с хостом по хешу), из кэша, остальные просятся у хоста (<see cref="MessageType.Fetch"/>) и
/// приходят кусками; собранный пакет проверяется по хешу и кладётся в кэш. Пока моды идут, реплика уже живёт:
/// дельты применяются. Готово — когда есть и моды (сумма сошлась с хостом), и снимок. Отказ хоста —
/// <see cref="Refused"/>. Вызовы — из одного потока.
/// <code>
/// var connector = GameConnector.Connect(transport, new ContentCache(dir), TimeSpan.FromSeconds(60));
/// using var game = new ClientGame(connector);
/// </code>
/// </summary>
public sealed class GameConnector
{
    private readonly ContentCache? _cache;
    private readonly Dictionary<string, ModPackage> _local = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ModPackage> _ready = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (byte[] Hash, byte[] Buffer, int Received)> _downloads = new(StringComparer.Ordinal);
    private Welcome? _welcome;

    /// <param name="local">Свои моды клиента: годятся, если хеш их пакета совпал с хостом.</param>
    public GameConnector(ITransport transport, ContentCache? cache, IEnumerable<ModPackage>? local = null)
    {
        Transport = transport;
        _cache = cache;
        foreach (var package in local ?? [])
            _local[ContentCache.Key(ContentPack.Hash(ContentCache.ToPack(package).Encode()))] = package;
    }

    public ITransport Transport { get; }
    public Replica Replica { get; } = new();

    /// <summary>Моды хоста; null — ещё не все.</summary>
    public GameContent? Content { get; private set; }

    /// <summary>Почему хост не пустил; null — пустил (или ещё не ответил).</summary>
    public string? Refused { get; private set; }

    /// <summary>Хост не пустил из-за версии протокола (<c>versionOld</c> или <c>versionNew</c>): игроку нужна новая сборка.</summary>
    public bool VersionMismatch { get; private set; }

    /// <summary>Язык, на котором пишется причина отказа хоста (сообщение хоста идёт ключом, Н6-5).</summary>
    public string Lang { get; set; } = global::Kharos.I18n.Langs.Default;

    private static readonly global::Kharos.I18n.Texts Texts = global::Kharos.I18n.Texts.Shell();

    /// <summary>Отказ из-за пароля: спросить его у игрока.</summary>
    public bool PasswordNeeded { get; private set; }

    /// <summary>Что пошло не так при сборке модов; null — ничего.</summary>
    public string? Failure { get; private set; }

    /// <summary>Сколько сжатых байт модов надо скачать и сколько пришло.</summary>
    public long Expected { get; private set; }
    public long Received { get; private set; }

    /// <summary>Сколько модов взято из кэша и скачано.</summary>
    public int FromCache { get; private set; }
    public int Downloaded { get; private set; }

    public bool Ready => Content is not null && Replica.Ready;

    /// <summary>Разбирает всё пришедшее; true — готово.</summary>
    public bool Poll()
    {
        while (Failure is null && Refused is null && Transport.TryReceive(out var message))
        {
            if (message.Length > 0 && message[0] == (byte)MessageType.Chunk)
            {
                Chunk(Messages.ReadChunk(message));
                continue;
            }
            Replica.Apply(message);
            if (message[0] == (byte)MessageType.Refused)
            {
                var (reason, password, args) = Messages.ReadRefused(message);
                Refused = args is null ? reason : Texts.Host(Lang, reason, args);
                VersionMismatch = args is not null && reason is "versionOld" or "versionNew";
                PasswordNeeded = password;
            }
            else if (message[0] == (byte)MessageType.Welcome && !ReferenceEquals(_welcome, Replica.Welcome))
                Welcome(Replica.Welcome!);
        }
        return Ready;
    }

    /// <summary>Ждёт готовности; отказ, закрытие, сбой модов или срок — <see cref="ClientException"/>.</summary>
    public void Wait(TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!Poll())
        {
            if (Refused is { } reason)
                throw new ClientException($"хост не пустил: {reason}");
            if (Failure is { } failure)
                throw new ClientException(failure);
            if (Transport.Closed)
            {
                Poll();
                throw new ClientException(Refused is { } why ? $"хост не пустил: {why}" : ClientGame.ClosedReason(Transport, "хост закрыл соединение до входа в партию"));
            }
            if (watch.Elapsed > timeout)
                throw new ClientException($"вход не состоялся за {timeout.TotalSeconds:0.#} с: {(_welcome is null ? "нет приветствия" : Content is null ? $"моды {Received}/{Expected} байт" : "нет снимка")}");
            Thread.Sleep(1);
        }
    }

    /// <summary>Подключение целиком: ждёт моды и снимок.</summary>
    public static GameConnector Connect(ITransport transport, ContentCache? cache, TimeSpan timeout, IEnumerable<ModPackage>? local = null)
    {
        var connector = new GameConnector(transport, cache, local);
        connector.Wait(timeout);
        return connector;
    }

    private void Welcome(Welcome welcome)
    {
        _welcome = welcome;
        Content = null;
        _ready.Clear();
        _downloads.Clear();
        Expected = Received = 0;
        var missing = new List<byte[]>();
        foreach (var mod in welcome.Mods)
        {
            var key = ContentCache.Key(mod.Hash);
            if (_ready.ContainsKey(key))
                continue;
            if (_local.TryGetValue(key, out var package))
            {
                _ready[key] = package;
                continue;
            }
            if (_cache?.Get(mod.Hash) is { } cached && TryUnpack(cached, out package))
            {
                _ready[key] = package;
                FromCache++;
                continue;
            }
            if (mod.Size <= 0 || mod.Size > ContentPack.MaxBytes)
            {
                Failure = $"мод {mod.Name}: размер {mod.Size}";
                return;
            }
            _downloads[key] = (mod.Hash, new byte[mod.Size], 0);
            Expected += mod.Size;
            missing.Add(mod.Hash);
        }
        if (missing.Count > 0)
            Transport.Send(Messages.Fetch(missing));
        Assemble();
    }

    private void Chunk(ContentChunk chunk)
    {
        var key = ContentCache.Key(chunk.Hash);
        if (!_downloads.TryGetValue(key, out var download) || chunk.Total != download.Buffer.Length || chunk.Offset != download.Received)
            return;
        chunk.Data.CopyTo(download.Buffer, chunk.Offset);
        download.Received += chunk.Data.Length;
        Received += chunk.Data.Length;
        _downloads[key] = download;
        if (download.Received < download.Buffer.Length)
            return;
        _downloads.Remove(key);
        byte[] pack;
        try
        {
            pack = ContentPack.Inflate(download.Buffer);
        }
        catch (FormatException error)
        {
            Failure = $"пакет мода не распаковался: {error.Message}";
            return;
        }
        if (!ContentPack.Hash(pack).AsSpan().SequenceEqual(download.Hash))
        {
            Failure = "пакет мода пришёл с другим хешем";
            return;
        }
        if (!TryUnpack(pack, out var package))
            return;
        try
        {
            _cache?.Put(download.Hash, pack);
        }
        catch (IOException)
        {
            // Кэш — не обязателен: в следующий раз мод скачается снова.
        }
        _ready[key] = package;
        Downloaded++;
        Assemble();
    }

    private bool TryUnpack(byte[] bytes, out ModPackage package)
    {
        try
        {
            package = ContentCache.ToPackage(ContentPack.Decode(bytes));
            return true;
        }
        catch (Exception error) when (error is FormatException or ContentException)
        {
            Failure = $"пакет мода не читается: {error.Message}";
            package = null!;
            return false;
        }
    }

    /// <summary>Все моды на месте — содержимое; сумма должна сойтись с хостом.</summary>
    private void Assemble()
    {
        if (_welcome is not { } welcome || _downloads.Count > 0 || Failure is not null)
            return;
        GameContent content;
        try
        {
            content = GameContent.Load(welcome.Mods.Select(mod => _ready[ContentCache.Key(mod.Hash)]));
        }
        catch (ContentException error)
        {
            Failure = $"моды хоста не собрались: {error.Message}";
            return;
        }
        if (!content.Checksum.AsSpan().SequenceEqual(welcome.Checksum))
        {
            Failure = "моды хоста и клиента разошлись: суммы не совпали";
            return;
        }
        Content = content;
    }
}
