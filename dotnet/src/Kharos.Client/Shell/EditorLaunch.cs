using System.Text;
using System.Text.Json.Nodes;
using Kharos.Engine.Layers;
using Kharos.Protocol;

namespace Kharos.Client.Shell;

/// <summary>
/// Редактор карты из меню (editor.ts, Н6-3): мир слота (сохранённый или ещё нет — тогда новый по настройкам слота) или
/// файл сохранения. Партия локальная, тумана в ней нет, подключение получает право редактора (/admin и /editor —
/// <see cref="EditorShell"/>). Сохраняет «Сохранить» оболочки: в тот же слот или в тот же файл.
/// </summary>
public sealed record EditorLaunch : Launch
{
    /// <summary>Слот, мир которого правится.</summary>
    public SaveSlot? Slot { get; init; }

    /// <summary>Файл сохранения, который правится (путь на диске).</summary>
    public string? File { get; init; }

    public EditorLaunch(SaveSlot slot) => Slot = slot;

    public EditorLaunch(string file) => File = file;

    /// <summary>Имя для полосы редактора: имя слота или файла.</summary>
    public string Name => Slot?.Name ?? Path.GetFileName(File) ?? "";
}

/// <summary>
/// Партия редактора у лаунчера (десктоп — EditorSession): правленый мир файлом сохранения, уже с настройками и туманом
/// исходного (<see cref="EditorMap.Restore"/>); партии нет — null.
/// </summary>
public interface IEditorSession
{
    byte[]? SaveMap();
}

/// <summary>
/// Мир редактора без тумана (editor.ts: «тумана нет; разведанное игроками сохраняется таким, каким было»): партия
/// поднимается с настройками исходного мира и <c>fog: false</c> (<see cref="WithoutFog"/>), а при сохранении настройки
/// исходного возвращаются, слой «разведано» игроков берётся из исходного сохранения (<see cref="Restore"/>). Память игроков
/// о карте и предметах (RCLL, IMEM) и о чужих зданиях (BMEM) не переносится: правки её устарили, хост заведёт её заново
/// по разведанному.
/// </summary>
public static class EditorMap
{
    /// <summary>Настройки партии (JSON поверх записи games.json) с выключенным туманом.</summary>
    public static string WithoutFog(string? settings)
    {
        var root = settings is { Length: > 0 } && JsonNode.Parse(settings) is JsonObject parsed ? parsed : new JsonObject();
        root["fog"] = false;
        return root.ToJsonString();
    }

    /// <summary>
    /// Сохранение правленого мира с настройками <paramref name="settings"/> исходного (null — без своих, по записи
    /// games.json) и его разведанным (<paramref name="original"/> — исходное сохранение; null — мира ещё не было).
    /// </summary>
    public static byte[] Restore(byte[] edited, string? settings, byte[]? original)
    {
        var sections = SaveFile.Unpack(edited);
        var meta = sections["META"];
        var r = new WireReader(meta);
        r.Text();
        r.Varint();
        r.F64();
        r.Varint();
        r.Take(8);
        r.Blob();
        r.Varint();
        var head = meta.AsSpan(0, r.At);
        var w = new WireWriter(meta.Length + (settings?.Length ?? 0) + 16).Raw(head);
        if (settings is { Length: > 0 })
            w.Blob(Encoding.UTF8.GetBytes(settings));
        sections["META"] = w.ToArray();

        var layers = LayerCodec.Decode(sections["LAYR"]).Where(change => !IsFog(change.Layer)).ToList();
        if (original is not null && SaveFile.Unpack(original).TryGetValue("LAYR", out var before))
            layers.AddRange(LayerCodec.Decode(before).Where(change => IsFog(change.Layer)));
        sections["LAYR"] = LayerCodec.Encode(layers);
        foreach (var memory in (ReadOnlySpan<string>)["RCLL", "IMEM", "BMEM"])
            sections.Remove(memory);

        // Порядок разделов — как у хоста (HostSave.Encode): META, WRLD, LAYR, LAND, MOVE, PLRS.
        string[] order = ["META", "WRLD", "LAYR", "LAND", "MOVE", "PLRS"];
        return SaveFile.Pack([
            .. order.Where(sections.ContainsKey).Select(tag => (tag, sections[tag])),
            .. sections.Where(section => !order.Contains(section.Key)).Select(section => (section.Key, section.Value)),
        ]);
    }

    private static bool IsFog(string layer) => layer is Fog.Explored or Fog.Sight;
}
