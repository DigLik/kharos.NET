using Kharos.Content;
using Kharos.I18n;
using Kharos.Protocol;

namespace Kharos.Client.Game;

/// <summary>Языки клиента (Н6-5, Д3): словари оболочки и модов партии, сообщения хоста на языке игрока.</summary>
public sealed partial class ClientGame
{
    /// <summary>Язык игрока для сообщений хоста и оболочки; моды получают его входом кадра (<see cref="ClientInput.Lang"/>).</summary>
    public string Lang { get; set; } = Langs.Default;

    /// <summary>Словари: оболочка движка, затем моды партии по порядку загрузки (поздний перекрывает ранний).</summary>
    public Texts Texts => Tools.Texts;

    /// <summary>Словари оболочки и всех модов партии.</summary>
    internal static Texts BuildTexts(GameContent content)
    {
        var texts = global::Kharos.I18n.Texts.Shell();
        foreach (var mod in content.Mods)
            texts.AddMod(mod.Name, mod.I18n);
        return texts;
    }

    /// <summary>Строка чата на языке игрока: сообщение хоста — из словаря по ключу, слова человека — как написал.</summary>
    public string TextOf(ChatLine line) => line.Args is null ? line.Text : Texts.Host(Lang, line.Text, line.Args);

    /// <summary>Причина отказа на языке игрока: ключ хоста с подстановками или готовый текст.</summary>
    private string RefusedText((string Reason, bool Password, IReadOnlyList<string>? Args) refusal) =>
        refusal.Args is null ? refusal.Reason : ShellOnly.Host(Lang, refusal.Reason, refusal.Args);

    /// <summary>Оболочка без модов: отказ приходит до загрузки модов, а ключи отказов — в оболочке.</summary>
    private static readonly global::Kharos.I18n.Texts ShellOnly = global::Kharos.I18n.Texts.Shell();
}
