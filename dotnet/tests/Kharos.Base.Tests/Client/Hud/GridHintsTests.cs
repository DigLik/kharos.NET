using Base.Client;
using static Kharos.Base.Tests.Client.Hud.RequestButtonsTests;

namespace Kharos.Base.Tests.Client.Hud;

/// <summary>Сетка команд и «Первые шаги»: подсказка текущего шага (hint) и новые кнопки (fresh, seen) — ui/commands.ts.</summary>
public sealed class GridHintsTests
{
    private static HudState Builder(params string[] buildings) => new()
    {
        Credits = 1000,
        Construction = new ConstructionState(null, null, [.. buildings.Select(building => new BuildOption(building, 100, true, 0, 0, []))], ["road", "foundation"]),
    };

    [Fact(DisplayName = "сетка: кнопка здания текущего первого шага и её раздел подсвечены (hint), другие — нет")]
    public void StepHintsBuildingAndSection()
    {
        var state = Builder("generator", "smelter");
        var actions = new GridActions { Send = _ => { }, Step = new StepSpec("smelter", Building: "smelter") };
        var root = Commands.Of(state, "root", actions, Tables.Value, T);
        Assert.True(root.First(slot => slot?.Label == T("grid.sections.industry"))!.Hint);
        Assert.False(root.First(slot => slot?.Label == T("grid.sections.economy"))!.Hint);
        var industry = Commands.Of(state, "industry", actions, Tables.Value, T);
        Assert.True(industry.First(slot => slot?.Building == "smelter")!.Hint);
        Assert.DoesNotContain(Commands.Of(state, "industry", new GridActions { Send = _ => { } }, Tables.Value, T), slot => slot?.Hint == true);
    }

    [Fact(DisplayName = "сетка: незнакомая кнопка стройки и её раздел — новые (fresh), нажатие отмечает её знакомой")]
    public void FreshUntilSeen()
    {
        var state = Builder("generator", "smelter");
        var seen = new HashSet<string> { "generator", "road", "foundation" };
        var actions = new GridActions { Send = _ => { }, IsSeen = seen.Contains, MarkSeen = key => seen.Add(key) };
        var root = Commands.Of(state, "root", actions, Tables.Value, T);
        Assert.True(root.First(slot => slot?.Label == T("grid.sections.industry"))!.Fresh);
        Assert.False(root.First(slot => slot?.Label == T("grid.sections.economy"))!.Fresh);
        Assert.False(root.First(slot => slot?.Label == T("grid.sections.paving"))!.Fresh);
        var smelter = Commands.Of(state, "industry", actions, Tables.Value, T).First(slot => slot?.Building == "smelter")!;
        Assert.True(smelter.Fresh);
        Assert.Equal("smelter", smelter.Seen);
        smelter.Run();
        Assert.Contains("smelter", seen);
        Assert.False(Commands.Of(state, "root", actions, Tables.Value, T).First(slot => slot?.Label == T("grid.sections.industry"))!.Fresh);
    }
}
