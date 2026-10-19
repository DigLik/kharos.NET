using System.Text;
using Kharos.Content;
using Kharos.Host;
using Kharos.I18n;
using Kharos.Protocol;

namespace Kharos.Client.Tests.I18n;

/// <summary>Множественное число по языкам, слияние словарей модов и сообщения хоста на проводе (протокол 3).</summary>
public sealed class PluralAndMergeTests
{
    private static byte[] Json(string text) => Encoding.UTF8.GetBytes(text);

    [Theory]
    [InlineData("ru", 1, PluralCategory.One)]
    [InlineData("ru", 21, PluralCategory.One)]
    [InlineData("ru", 11, PluralCategory.Many)]
    [InlineData("ru", 2, PluralCategory.Few)]
    [InlineData("ru", 24, PluralCategory.Few)]
    [InlineData("ru", 12, PluralCategory.Many)]
    [InlineData("ru", 14, PluralCategory.Many)]
    [InlineData("ru", 5, PluralCategory.Many)]
    [InlineData("ru", 0, PluralCategory.Many)]
    [InlineData("ru", 100, PluralCategory.Many)]
    [InlineData("ru", 101, PluralCategory.One)]
    [InlineData("uk", 1, PluralCategory.One)]
    [InlineData("uk", 3, PluralCategory.Few)]
    [InlineData("uk", 11, PluralCategory.Many)]
    [InlineData("uk", 22, PluralCategory.Few)]
    [InlineData("uz", 1, PluralCategory.One)]
    [InlineData("uz", 2, PluralCategory.Other)]
    [InlineData("uz", 0, PluralCategory.Other)]
    [InlineData("en", 1, PluralCategory.One)]
    [InlineData("en", 0, PluralCategory.Other)]
    [InlineData("de", 2, PluralCategory.Other)]
    [InlineData("es", 1, PluralCategory.One)]
    [InlineData("fr", 0, PluralCategory.One)]
    [InlineData("fr", 1, PluralCategory.One)]
    [InlineData("fr", 2, PluralCategory.Other)]
    [InlineData("pt", 0, PluralCategory.One)]
    [InlineData("pt", 5, PluralCategory.Other)]
    public void PluralCategoryFollowsTheRulesOfTheLanguage(string lang, long count, PluralCategory expected) =>
        Assert.Equal(expected, Plural.Of(lang, count));

    [Fact]
    public void NegativeCountsFollowTheirAbsoluteValue() => Assert.Equal(PluralCategory.Few, Plural.Of("ru", -3));

    [Fact]
    public void PluralPicksTheFormByKeySuffixAndFallsBackToOther()
    {
        var texts = new Texts();
        texts.Add("ru", Json("""{ "units": { "one": "{n} юнит", "few": "{n} юнита", "many": "{n} юнитов", "other": "{n} юнита" } }"""));
        texts.Add("en", Json("""{ "units": { "one": "{n} unit", "other": "{n} units" } }"""));
        Assert.Equal("1 юнит", texts.Plural("ru", "units", 1));
        Assert.Equal("3 юнита", texts.Plural("ru", "units", 3));
        Assert.Equal("11 юнитов", texts.Plural("ru", "units", 11));
        Assert.Equal("1 unit", texts.Plural("en", "units", 1));
        Assert.Equal("5 units", texts.Plural("en", "units", 5));
        // У английского нет few/many — его правила их и не требуют; у немецкого нет словаря — запас на английский.
        Assert.Equal("2 units", texts.Plural("de", "units", 2));
        Assert.Equal("none", texts.Plural("en", "none", 2));
    }

    [Fact]
    public void LaterModsOverrideEarlierOnesAndTranslationPatchesAddKeys()
    {
        var texts = Texts.Shell();
        texts.AddMod("base", [new("ru", Json("""{ "units": { "tank": "Танк", "jeep": "Джип" }, "hud": { "title": "База" } }""")), new("en", Json("""{ "units": { "tank": "Tank" } }"""))]);
        texts.AddMod("patch", [new("ru", Json("""{ "units": { "tank": "Танчик" } }""")), new("de", Json("""{ "units": { "tank": "Panzer" } }"""))]);
        Assert.Equal("Танчик", texts.T("ru", "units.tank"));
        Assert.Equal("Джип", texts.T("ru", "units.jeep"));
        Assert.Equal("База", texts.T("ru", "hud.title"));
        Assert.Equal("Panzer", texts.T("de", "units.tank"));
        Assert.Equal("Tank", texts.T("en", "units.tank"));
        // Оболочка не пропала от слияния, а в языке без строки берётся английская, затем русская.
        Assert.Equal("Play", texts.T("en", "common.play"));
        Assert.Equal("Джип", texts.T("fr", "units.jeep"));
        Assert.Equal("Tank", texts.T("fr", "units.tank"));
    }

    [Fact]
    public void ModDictionariesTravelInThePackageAndComeBackOnTheClient()
    {
        var package = new ModPackage("lang", Json("""{ "name": "lang" }"""), new Dictionary<string, byte[]>
        {
            ["games"] = Json("{}"),
            ["i18n/ru"] = Json("""{ "x": { "y": "игра" } }"""),
        });
        Assert.DoesNotContain("i18n/ru", package.Data.Keys);
        Assert.Equal("""{ "x": { "y": "игра" } }""", Encoding.UTF8.GetString(package.I18n["ru"]));
        // Пакет для клиента держит словарь среди данных и возвращает его на место.
        var pack = Client.Net.ContentCache.ToPack(package);
        Assert.Contains(pack.Data, file => file.Name == "i18n/ru");
        var back = Client.Net.ContentCache.ToPackage(ContentPack.Decode(pack.Encode()));
        Assert.Equal(package.I18n["ru"], back.I18n["ru"]);
        Assert.Equal(package.Data.Keys, back.Data.Keys);
        var texts = new Texts();
        texts.AddMod(back.Name, back.I18n);
        Assert.Equal("игра", texts.T("ru", "x.y"));
    }

    [Fact]
    public void BadDictionaryNamesItsSource()
    {
        var error = Assert.Throws<FormatException>(() => new Texts().Add("ru", Json("""{ "a": 5 }"""), "мод base"));
        Assert.Contains("мод base", error.Message);
        Assert.Contains("a", error.Message);
        Assert.Throws<FormatException>(() => new Texts().Add("ru", Json("[1]")));
    }

    [Fact]
    public void ChatLineWithKeyAndArgsSurvivesTheWire()
    {
        var lines = new List<ChatLine>
        {
            new(3, "Ann", "joins", ChatKind.System, null, []),
            new(0, "", "list", ChatKind.System, null, ["count", "2", "names", "Ann, Bob"]),
            new(1, "Ann", "привет", ChatKind.Player),
            new(1, "Ann", "тайна", ChatKind.Whisper, "Bob"),
        };
        var back = Messages.ReadChat(Messages.Chat(lines));
        Assert.Equal(4, back.Count);
        Assert.Equal(("joins", 0), (back[0].Text, back[0].Args!.Count));
        Assert.Equal(["count", "2", "names", "Ann, Bob"], back[1].Args);
        Assert.Null(back[2].Args);
        Assert.Equal((ChatKind.Whisper, "Bob", (IReadOnlyList<string>?)null), (back[3].Kind, back[3].To, back[3].Args));
        var texts = Texts.Shell();
        Assert.Equal("В игре 2: Ann, Bob", texts.Host("ru", back[1].Text, back[1].Args));
        Assert.Contains("2", texts.Host("en", back[1].Text, back[1].Args));
        Assert.Throws<ArgumentException>(() => Messages.Chat([new ChatLine(0, "", "x", ChatKind.System, null, ["lonely"])]));
    }

    [Fact]
    public void RefusalWithKeyReachesTheClientInItsLanguage()
    {
        var message = Messages.Refused(HostText.Of("versionOld", ("server", 3), ("client", 2)), password: true);
        var (reason, password, args) = Messages.ReadRefused(message);
        Assert.True(password);
        Assert.Equal("versionOld", reason);
        Assert.Equal(["server", "3", "client", "2"], args);
        var texts = Texts.Shell();
        Assert.Contains("у сервера 3, у вас 2", texts.Host("ru", reason, args));
        Assert.Contains("3", texts.Host("en", reason, args));
        // Старый вид отказа — готовый текст — читается как прежде.
        var plain = Messages.ReadRefused(Messages.Refused("Неверный пароль"));
        Assert.Equal(("Неверный пароль", false, null), (plain.Reason, plain.Password, plain.Args));
    }

    [Fact]
    public void ProtocolVersionIsThree() => Assert.Equal(3, Welcome.ProtocolVersion);
}
