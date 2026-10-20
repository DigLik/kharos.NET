using Kharos.Protocol;

namespace Kharos.Server;

/// <summary>Строка версии сервера: сборка, протокол и сохранение (Н7-4, Н7-5). Печатается при запуске и по <c>--version</c>.</summary>
public static class VersionLine
{
    public static string Text() =>
        $"kharos-server {BuildInfo.Full} (протокол {Welcome.ProtocolVersion}, сохранение {SaveFile.Version})";
}
