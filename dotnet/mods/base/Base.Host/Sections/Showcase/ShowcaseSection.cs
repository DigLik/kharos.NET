using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Showcase;

/// <summary>
/// Раздел Showcase (docs/dotnet-n6.md, Н6-4): сценки витрины меню — партия showcase в data/games.json, меню запускает её
/// через LocalGame без тумана. Пока пуст: ни систем, ни приказов, ни запросов — мир и порядок тика не меняются.
/// </summary>
internal static class ShowcaseSection
{
    public static void Setup(ModBuilder mod)
    {
        // TODO(В): сценки из tests/Kharos.Base.Tests/.../Players/ShowcaseTests.cs и партия showcase в data/games.json.
        _ = mod;
    }
}
