namespace Kharos.I18n;

/// <summary>Языки интерфейса с названиями на них самих (packages/i18n: LANGS).</summary>
public static class Langs
{
    public static readonly IReadOnlyList<(string Lang, string Name)> All =
    [
        ("ru", "Русский"), ("en", "English"), ("es", "Español"), ("fr", "Français"),
        ("de", "Deutsch"), ("pt", "Português"), ("uk", "Українська"), ("uz", "O'zbekcha"),
    ];

    /// <summary>Язык по умолчанию и тот, чьи строки исходные: по русскому проверяются остальные словари.</summary>
    public const string Default = "ru";

    public static bool Is(string? lang) => lang is not null && All.Any(pair => pair.Lang == lang);

    /// <summary>Язык из тега вроде «de-AT»; незнакомый — null.</summary>
    public static string? FromTag(string? tag)
    {
        var lang = (tag ?? "").ToLowerInvariant().Split('-', '_')[0];
        return Is(lang) ? lang : null;
    }
}

/// <summary>Категория множественного числа (CLDR) для целого количества.</summary>
public enum PluralCategory
{
    Other,
    One,
    Few,
    Many,
}

/// <summary>Правила множественного числа по языкам: ru и uk — три формы, fr и pt — 0 и 1 в единственном, прочие — только 1.</summary>
public static class Plural
{
    public static PluralCategory Of(string lang, long count)
    {
        var n = Math.Abs(count);
        switch (lang)
        {
            case "ru" or "uk":
                var (mod10, mod100) = (n % 10, n % 100);
                if (mod10 == 1 && mod100 != 11)
                    return PluralCategory.One;
                if (mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14)
                    return PluralCategory.Few;
                return PluralCategory.Many;
            case "fr" or "pt":
                return n <= 1 ? PluralCategory.One : PluralCategory.Other;
            default:
                // en, de, es, uz: одна форма для единицы.
                return n == 1 ? PluralCategory.One : PluralCategory.Other;
        }
    }

    /// <summary>Суффикс ключа формы: «one», «few», «many», «other».</summary>
    public static string Suffix(PluralCategory category) => category switch
    {
        PluralCategory.One => "one",
        PluralCategory.Few => "few",
        PluralCategory.Many => "many",
        _ => "other",
    };
}
