using Base.Shared;
using Base.Shared.Rules;
using Kharos.Base.Tests.Researching;

namespace Kharos.Base.Tests.ClientState;

/// <summary>
/// Почему лаборатория стоит (пакет Э): строку мира Research.lack клиент не читает, поэтому хост пишет рядом число
/// Research.cause (LabLack) и Proto груза Research.good; окно исследований показывает по ним точную причину.
/// </summary>
public sealed class LabCauseTests
{
    /// <summary>Число причины и груз совпадают со строкой lack хоста.</summary>
    private static void Agrees(Sim sim, uint lab, string expected)
    {
        var (_, lack) = sim.LabOf(lab);
        Assert.Equal(expected, lack);
        var cause = (int)sim.Num(lab, Names.Research.Name, Names.Research.Cause);
        Assert.Equal(LabLack.Of(lack), cause);
        var good = (int)sim.Num(lab, Names.Research.Name, Names.Research.Good);
        if (cause == LabLack.Material)
            Assert.Equal(sim.GoodProto(lack), good);
        else
            Assert.Equal(-1, good);
        Assert.Equal(lack, LabLack.Name(cause, cause == LabLack.Material ? lack : null));
    }

    [Fact(DisplayName = "лаборатория говорит числом, чего ей не хватает: груз, кредиты, выключена, энергия")]
    public void HostWritesCause()
    {
        using var @base = Lab.Make(["lab"]);
        var (sim, lab) = (@base.Sim, @base.Buildings[0]);
        @base.Stash(("metal", 200), ("silicon", 200));
        sim.Learn(1, "science", "prospecting", "metalFound", "siliconFound");
        Lab.Plan(sim, "vehicles");
        sim.Advance();
        Agrees(sim, lab, "metalOre");

        sim.Learn(1, "metalOre", "siliconOre");
        Lab.SetPurse(sim, 1, 0, 0);
        sim.Seconds(1);
        Agrees(sim, lab, "credits");

        sim.SetCredits(1, 10000);
        sim.Add(lab, Names.Off.Name);
        sim.Seconds(1);
        Agrees(sim, lab, "off");

        sim.Remove(lab, Names.Off.Name);
        sim.Erase([.. @base.Plants]);
        sim.Seconds(1);
        Agrees(sim, lab, "power");
    }

    [Fact(DisplayName = "окно исследований называет причину по числу хоста: энергия, потом «выключена»")]
    public void WindowShowsCause()
    {
        Assert.SkipWhen(Viewer.Unavailable(Sim.DefaultMode) is not null, Viewer.Unavailable(Sim.DefaultMode) ?? "");
        using var sim = Sim.Create(new SimOptions { Game = "skirmish", Players = [1] });
        sim.SetCredits(1, 5000);
        sim.Learn(1, "science", "prospecting", "metalFound", "metalOre");
        var lab = Lab.PlaceAnywhere(sim, "lab");
        Lab.Plan(sim, "ammo");
        sim.Seconds(1);
        Assert.Equal("power", sim.LabOf(lab).Lack);
        using var viewer = sim.Viewer(1, new ViewerOptions { Width = 1600, Height = 1400 });
        viewer.Run(0.3);
        viewer.Press("r");
        viewer.Run(0.3);
        Assert.True(viewer.Ui.Has("tech"), "окно исследований не открылось");
        string Lacks() => viewer.Ui.Text("tech:lacks") ?? "";
        Assert.Contains(Kharos.Base.Tests.Client.Hud.RequestButtonsTests.T("tech.lackPower"), Lacks(), StringComparison.Ordinal);

        sim.Add(lab, Names.Off.Name);
        sim.Seconds(1);
        viewer.Run(0.3);
        Assert.Equal("off", sim.LabOf(lab).Lack);
        Assert.Contains(Kharos.Base.Tests.Client.Hud.RequestButtonsTests.T("tech.lackOff"), Lacks(), StringComparison.Ordinal);
        Assert.DoesNotContain(Kharos.Base.Tests.Client.Hud.RequestButtonsTests.T("tech.lackPower"), Lacks(), StringComparison.Ordinal);
        Assert.Empty(viewer.Log);
    }
}
