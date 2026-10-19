using System.Text.RegularExpressions;
using Kharos.I18n;
using Kharos.Protocol;

namespace Kharos.Client.Tests.I18n;

/// <summary>Порт packages/tests/i18n.test.ts: словари оболочки и base, строки, значки, сообщения хоста.</summary>
public sealed partial class I18nTests
{
    /// <summary>mods/base/i18n рядом с исходниками (так же находится mods/hello у остальных тестов).</summary>
    internal static string BaseI18n => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Repo.Cache("x"))!, "..", "..", "mods", "base", "i18n"));

    /// <summary>Оболочка и словари base, как их собирает клиент партии.</summary>
    internal static Texts Merged()
    {
        var texts = Texts.Shell();
        texts.AddDirectory(BaseI18n, "base");
        return texts;
    }

    [GeneratedRegex(@"\{(\w+)(?::\w+)?\}")]
    private static partial Regex Placeholder();

    /// <summary>Что подставляется в строку: имена без «:lower» — строчными язык пишет по своим правилам.</summary>
    private static string[] Placeholders(string text) => [.. Placeholder().Matches(text).Select(match => match.Groups[1].Value).Order(StringComparer.Ordinal)];

    [Fact(DisplayName = "словари: в каждом языке те же ключи и те же подстановки, что в русском")]
    public void EveryLanguageHasTheKeysAndPlaceholdersOfRussian()
    {
        var texts = Merged();
        var source = texts.Keys("ru").Order(StringComparer.Ordinal).ToArray();
        Assert.True(source.Length > 1000, $"русский словарь пуст: {source.Length}");
        foreach (var (lang, _) in Langs.All)
        {
            Assert.Equal(source, texts.Keys(lang).Order(StringComparer.Ordinal).ToArray());
            foreach (var key in source)
                Assert.True(Placeholders(texts.Raw("ru", key)!).SequenceEqual(Placeholders(texts.Raw(lang, key)!)), $"{lang}: подстановки {key}");
        }
    }

    [Fact(DisplayName = "строки: подстановки, строчные, значки между кусками и сообщения хоста на языке игрока")]
    public void StringsSubstituteLowercaseIconsAndHostMessages()
    {
        var texts = Merged();
        Assert.Equal("2 ч 5 мин", texts.T("ru", "menu.hours", ("hours", 2), ("minutes", 5)));
        Assert.Equal("Месторождение: металл", texts.T("ru", "hud.depositTip", ("name", "Металл")));
        var icon = new object();
        Assert.Equal(
            [RichPart.Of(icon), RichPart.Of(" "), RichPart.Of("Нефть"), RichPart.Of(": "), RichPart.Of("осталось 5")],
            texts.Rich("ru", "hud.depositLeft", ("icon", icon), ("name", "Нефть"), ("left", "осталось 5")));

        var usage = HostText.Of("usage", ("command", "/msg <player> <text>"));
        Assert.Equal("Как: /msg <игрок> <текст>", texts.Host("ru", usage.Key, usage.Args));
        // Готовый текст человека остаётся как написан: ключ у него — не host.*.
        Assert.Equal("как написал человек", texts.Host("ru", "как написал человек"));
        Assert.Equal("Usage: /msg <player> <text>", texts.Host("en", usage.Key, usage.Args));
        Assert.Equal("/list — who is in the game now", texts.Host("en", "help", HostText.Of("help", ("command", "/list"), ("name", "list"), ("help", "")).Args));
    }

    [Fact]
    public void UnknownKeyAndMissingArgumentsStayAsTheyAre()
    {
        var texts = Merged();
        Assert.Equal("no.such.key", texts.T("ru", "no.such.key", ("name", "x")));
        Assert.Equal("{hours} ч {minutes} мин", texts.T("ru", "menu.hours"));
        Assert.Equal("2 ч {minutes} мин", texts.T("ru", "menu.hours", ("hours", 2)));
        Assert.Equal("totally.unknown", texts.Host("ru", "totally.unknown", ["a", "b"]));
    }

    [Fact]
    public void ShellKeysAreInTheEngineAndGameKeysInTheBaseMod()
    {
        var shell = Texts.Shell();
        Assert.True(shell.Has("ru", "menu.title"));
        Assert.True(shell.Has("ru", "host.joins"));
        Assert.False(shell.Has("ru", "hud.depositTip"));
        Assert.False(shell.Has("ru", "buildings.smelter"));
        var withBase = Merged();
        Assert.True(withBase.Has("ru", "hud.depositTip"));
        Assert.True(withBase.Has("de", "buildings.smelter"));
    }
}
