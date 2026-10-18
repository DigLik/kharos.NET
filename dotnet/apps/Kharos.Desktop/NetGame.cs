using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kharos.Client.Net;
using Kharos.Protocol;

namespace Kharos.Desktop;

/// <summary>
/// Игра на сервере (Н4): подключение по KHAROS_SERVER и память о том, кто мы на каком сервере. Всё, что зависит от
/// договора с сервером — поля запроса рукопожатия и откуда берётся токен игрока, — собрано здесь: <see cref="Query"/> и
/// <see cref="PlayerTokens"/>. Оболочка видит лишь <see cref="Connect"/> и <see cref="Describe"/>.
/// </summary>
internal static class NetGame
{
    /// <summary>
    /// Подключается к options.Server; null — не вышло (причина в журнале). Отказ входа (пароль, версия) приходит уже
    /// после рукопожатия — см. <see cref="Describe"/>.
    /// </summary>
    public static WebSocketTransport? Connect(DesktopOptions options)
    {
        Uri address;
        try
        {
            address = new Uri(options.Server!, UriKind.Absolute);
        }
        catch (UriFormatException)
        {
            DesktopLog.Write($"KHAROS_SERVER: не адрес сервера: {options.Server}");
            return null;
        }
        if (address.Scheme is not ("ws" or "wss"))
        {
            DesktopLog.Write($"KHAROS_SERVER: нужен адрес ws:// или wss://, а не {options.Server}");
            return null;
        }

        var tokens = new PlayerTokens();
        var token = tokens.Of(address);
        DesktopLog.Write($"подключаюсь к {address.GetLeftPart(UriPartial.Path)}");
        try
        {
            var transport = WebSocketTransport.ConnectAsync(WebSocketTransport.WithQuery(address, Query(token, options))).GetAwaiter().GetResult();
            DesktopLog.Write("соединение открыто, жду приветствие");
            return transport;
        }
        catch (TransportException error)
        {
            DesktopLog.Write(error.Message);
            return null;
        }
    }

    /// <summary>
    /// Поля запроса рукопожатия, как у клиента TS (packages/client/src/net/connect.ts): id — токен игрока, name — ник,
    /// password — пароль сервера, version — версия протокола. Менять вместе с сервером.
    /// </summary>
    private static KeyValuePair<string, string?>[] Query(string token, DesktopOptions options) =>
    [
        new("id", token),
        new("name", options.Name),
        new("password", options.Password),
        new("version", Welcome.ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
    ];

    /// <summary>Почему соединение кончилось или не состоялось: текст из Close-кадра сервера (отказ) либо сбой.</summary>
    public static string Describe(WebSocketTransport transport) =>
        transport.CloseReason is { } reason
            ? $"сервер закрыл соединение: {reason}" + (transport.CloseStatus is { } status ? $" ({status})" : "")
            : "соединение закрыто";
}

/// <summary>
/// Токены игрока по адресам серверов, в servers.json рядом с журналом (<c>%LOCALAPPDATA%\Kharos</c> на Windows,
/// <c>~/.local/share/Kharos</c> на Linux, <c>~/Library/Application Support/Kharos</c> на macOS). Сервер узнаёт игрока
/// по токену и отдаёт прежнего. Пока сервер сам токен не выдаёт, клиент заводит его сам — случайный, при первом входе.
/// Когда выдача уйдёт в приветствие, сюда добавится <see cref="Remember"/> из оболочки.
/// </summary>
internal sealed class PlayerTokens
{
    public static string FilePath { get; } = Path.Combine(Path.GetDirectoryName(DesktopLog.FilePath)!, "servers.json");

    private ServersFile _file = Read();

    /// <summary>Токен для сервера; нет — заводит и сразу сохраняет.</summary>
    public string Of(Uri server)
    {
        var key = Key(server);
        if (_file.Ids.TryGetValue(key, out var token) && token.Length > 0)
            return token;
        token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        Remember(server, token);
        return token;
    }

    /// <summary>Запоминает токен, выданный сервером.</summary>
    public void Remember(Uri server, string token)
    {
        _file.Ids[Key(server)] = token;
        Write(_file);
    }

    /// <summary>Ключ — адрес без запроса: пароль и ник в файл не попадают.</summary>
    private static string Key(Uri server) => server.GetLeftPart(UriPartial.Path);

    private static ServersFile Read()
    {
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize(File.ReadAllText(FilePath), ServersJson.Default.ServersFile) is { Ids: { } ids })
                return new ServersFile(new Dictionary<string, string>(ids));
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            DesktopLog.Write($"servers.json не прочитан, токены начнутся заново: {error.Message}");
        }
        return new ServersFile([]);
    }

    private static void Write(ServersFile file)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // Через временный файл: оборванная запись не испортит прежний.
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, ServersJson.Default.ServersFile));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            DesktopLog.Write($"servers.json не записан, после перезапуска сервер выдаст нового игрока: {error.Message}");
        }
    }
}

/// <summary>Содержимое servers.json: адрес сервера → токен игрока.</summary>
internal sealed record ServersFile(Dictionary<string, string> Ids);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ServersFile))]
internal sealed partial class ServersJson : JsonSerializerContext;
