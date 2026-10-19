using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Kharos.Client.Game;
using Kharos.I18n;

namespace Kharos.Client.Shell;

/// <summary>Во что играть (Launch в launch.ts): слот локальной игры, сервер или партия по имени (KHAROS_GAME, снимок).</summary>
public abstract record Launch;

/// <summary>Локальная игра в слоте сохранений.</summary>
public sealed record SlotLaunch(SaveSlot Slot) : Launch;

/// <summary>Игра на сервере: адрес ws:// или wss://, ник и пароль (пароль не запоминается).</summary>
public sealed record ServerLaunch(string Url, string Name, string? Password = null) : Launch;

/// <summary>Партия без слота: запись games.json (null — по умолчанию) и настройки поверх неё (JSON).</summary>
public sealed record GameLaunch(string? Game, string? Settings = null) : Launch;

/// <summary>Сервер, на котором уже играли: адрес, с каким ником и когда последний раз, мс эпохи.</summary>
public sealed record RecentServer(string Url, string Name, long Played);

/// <summary>Действие оболочки, которое можно перевесить на другую клавишу.</summary>
public enum ShellKey : byte { Fullscreen, Menu, Chat }

/// <summary>
/// Настройки игрока в хранилище клиента (settings.ts, launch.ts): язык, экран (полный экран, вертикальная
/// синхронизация, масштаб интерфейса, предел кадров), клавиши оболочки, сервер из формы, недавние серверы и последняя
/// игра для «Продолжить». Громкости — <see cref="Kharos.Audio.AudioPrefs"/> в том же хранилище. Всё — JSON под
/// «engine/shell/…»; испорченное читается как умолчание.
/// </summary>
public sealed class ShellPrefs
{
    public const string SettingsKey = "engine/shell/settings";
    public const string ServerKey = "engine/shell/server";
    public const string ServersKey = "engine/shell/servers";
    public const string LastKey = "engine/shell/last";

    /// <summary>Сколько недавних серверов помнится.</summary>
    public const int RecentServers = 8;

    /// <summary>Масштабы интерфейса на выбор; 0 — по экрану.</summary>
    public static readonly float[] Scales = [0, 1, 1.5f, 2];

    /// <summary>Пределы кадров на выбор; 0 — без предела.</summary>
    public static readonly int[] FpsLimits = [30, 60, 120, 0];

    /// <summary>Клавиши оболочки по умолчанию.</summary>
    public static readonly IReadOnlyDictionary<ShellKey, string> DefaultKeys = new Dictionary<ShellKey, string>
    {
        [ShellKey.Fullscreen] = "f11",
        [ShellKey.Menu] = "f10",
        [ShellKey.Chat] = "enter",
    };

    private readonly IStorage _storage;
    private readonly Dictionary<ShellKey, string> _keys = new(DefaultKeys);

    public ShellPrefs(IStorage storage)
    {
        _storage = storage;
        Read();
    }

    public IStorage Storage => _storage;

    public string Lang { get; private set; } = Langs.Default;
    public bool Fullscreen { get; private set; }
    public bool VSync { get; private set; } = true;
    public float Scale { get; private set; }
    public int FpsLimit { get; private set; }

    /// <summary>Есть ли сохранённый выбор языка (нет — оболочка берёт язык системы).</summary>
    public bool LangChosen { get; private set; }

    public string KeyOf(ShellKey key) => _keys[key];

    public void SetLang(string lang)
    {
        if (!Langs.Is(lang))
            return;
        (Lang, LangChosen) = (lang, true);
        Write();
    }

    public void SetFullscreen(bool value) { Fullscreen = value; Write(); }
    public void SetVSync(bool value) { VSync = value; Write(); }
    public void SetScale(float value) { Scale = Scales.Contains(value) ? value : 0; Write(); }
    public void SetFpsLimit(int value) { FpsLimit = Math.Max(0, value); Write(); }

    /// <summary>Перевешивает действие на клавишу; та же клавиша у другого действия меняется местами с прежней.</summary>
    public void SetKey(ShellKey key, string name)
    {
        foreach (var other in _keys.Keys.ToArray())
            if (other != key && _keys[other] == name)
                _keys[other] = _keys[key];
        _keys[key] = name;
        Write();
    }

    // ------------------------------------------------------------------ сервер и последняя игра

    /// <summary>Сервер из формы «Сетевая игра»: адрес и ник.</summary>
    public (string Url, string Name) Server()
    {
        using var json = Parse(ServerKey);
        var root = json?.RootElement;
        return (Text(root, "url") ?? DefaultServer, Text(root, "name") ?? "");
    }

    /// <summary>Адрес сервера на этой машине.</summary>
    public const string DefaultServer = "ws://localhost:8787";

    public void StoreServer(string url, string name) =>
        Store(ServerKey, json =>
        {
            json.WriteStartObject();
            json.WriteString("url", url);
            json.WriteString("name", name);
            json.WriteEndObject();
        });

    /// <summary>Недавние серверы, свежие первыми.</summary>
    public List<RecentServer> Servers()
    {
        using var json = Parse(ServersKey);
        if (json?.RootElement is not { ValueKind: JsonValueKind.Array } list)
            return [];
        var servers = new List<RecentServer>();
        foreach (var item in list.EnumerateArray())
            if (Text(item, "url") is { } url)
                servers.Add(new RecentServer(url, Text(item, "name") ?? "", item.TryGetProperty("played", out var played) && played.TryGetInt64(out var at) ? at : 0));
        return servers;
    }

    public void Forget(string url) => StoreServers(Servers().Where(server => server.Url != url));

    /// <summary>
    /// Во что играли последним — для «Продолжить»: слот или сервер (партия без слота не в счёт). Сервер к тому же встаёт
    /// первым в недавние.
    /// </summary>
    public void RememberLast(Launch launch, long now)
    {
        switch (launch)
        {
            case SlotLaunch slot:
                Store(LastKey, json =>
                {
                    json.WriteStartObject();
                    json.WriteString("kind", "save");
                    json.WriteString("slot", slot.Slot.Id);
                    json.WriteEndObject();
                });
                break;
            case ServerLaunch server:
                StoreServers([new RecentServer(server.Url, server.Name, now), .. Servers().Where(other => other.Url != server.Url)]);
                Store(LastKey, json =>
                {
                    json.WriteStartObject();
                    json.WriteString("kind", "server");
                    json.WriteString("url", server.Url);
                    json.WriteString("name", server.Name);
                    json.WriteEndObject();
                });
                break;
        }
    }

    /// <summary>Последняя игра: слот — свежим из списка; удалённый слот не подменяется другим — тогда null.</summary>
    public Launch? Last(SaveSlots slots)
    {
        using var json = Parse(LastKey);
        var root = json?.RootElement;
        return Text(root, "kind") switch
        {
            "save" when Text(root, "slot") is { } id && slots.Find(id) is { } slot => new SlotLaunch(slot),
            "server" when Text(root, "url") is { } url => new ServerLaunch(url, Text(root, "name") ?? ""),
            _ => null,
        };
    }

    private void StoreServers(IEnumerable<RecentServer> servers) =>
        Store(ServersKey, json =>
        {
            json.WriteStartArray();
            foreach (var server in servers.Take(RecentServers))
            {
                json.WriteStartObject();
                json.WriteString("url", server.Url);
                json.WriteString("name", server.Name);
                json.WriteNumber("played", server.Played);
                json.WriteEndObject();
            }
            json.WriteEndArray();
        });

    // ------------------------------------------------------------------ чтение и запись

    private void Read()
    {
        using var json = Parse(SettingsKey);
        if (json?.RootElement is not { ValueKind: JsonValueKind.Object } root)
            return;
        if (Text(root, "lang") is { } lang && Langs.Is(lang))
            (Lang, LangChosen) = (lang, true);
        Fullscreen = root.TryGetProperty("fullscreen", out var full) && full.ValueKind == JsonValueKind.True;
        VSync = !root.TryGetProperty("vsync", out var vsync) || vsync.ValueKind != JsonValueKind.False;
        Scale = root.TryGetProperty("scale", out var scale) && scale.TryGetSingle(out var s) && Scales.Contains(s) ? s : 0;
        FpsLimit = root.TryGetProperty("fps", out var fps) && fps.TryGetInt32(out var f) && f >= 0 ? f : 0;
        if (root.TryGetProperty("keys", out var keys) && keys.ValueKind == JsonValueKind.Object)
            foreach (var key in Enum.GetValues<ShellKey>())
                if (Text(keys, Name(key)) is { Length: > 0 } name)
                    _keys[key] = name;
    }

    private void Write() =>
        Store(SettingsKey, json =>
        {
            json.WriteStartObject();
            if (LangChosen)
                json.WriteString("lang", Lang);
            json.WriteBoolean("fullscreen", Fullscreen);
            json.WriteBoolean("vsync", VSync);
            json.WriteNumber("scale", Scale);
            json.WriteNumber("fps", FpsLimit);
            json.WriteStartObject("keys");
            foreach (var (key, name) in _keys)
                json.WriteString(Name(key), name);
            json.WriteEndObject();
            json.WriteEndObject();
        });

    private static string Name(ShellKey key) => key.ToString().ToLowerInvariant();

    private JsonDocument? Parse(string key)
    {
        if (_storage.Get(key) is not { } bytes)
            return null;
        try
        {
            return JsonDocument.Parse(bytes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } item && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private void Store(string key, Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            write(json);
        _storage.Set(key, buffer.WrittenSpan);
    }

    /// <summary>Масштаб для подписи: «авто» решает вызывающий, иначе «150%».</summary>
    public static string Percent(float share) => (share * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
}
