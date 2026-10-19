using System.Buffers.Binary;
using Kharos.Base.Tests.Kernel;
using Kharos.Client.Game;

namespace Kharos.Base.Tests.ClientAlerts;

/// <summary>
/// Первые шаги (packages/tests/firstSteps.test.ts): окно в правом верхнем углу (узлы steps, step:ключ, steps:show);
/// шаги отмечаются по миру, а место шага ведёт кнопка «Показать».
/// </summary>
public sealed class StepsTests
{
    private static void SkipUnless() => Assert.SkipWhen(Viewer.Unavailable(Sim.DefaultMode) is not null, Viewer.Unavailable(Sim.DefaultMode) ?? "");

    private static string T(string key, params string[] args) => Kharos.Base.Tests.Client.Hud.RequestButtonsTests.T(key, args);

    private static readonly string[] Keys = ["deploy", "builder", "generator", "matter", "mine", "truck", "lab", "metalOre", "smelter", "metalYard", "spaceport", "sale"];

    /// <summary>Награды игрока (ключи rewards.json) — как после получения.</summary>
    private static void Reward(Sim sim, uint player, params string[] keys)
    {
        var bytes = new byte[keys.Length * 4];
        for (var i = 0; i < keys.Length; i++)
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4), Scene.Proto(sim, "rewards/" + keys[i]));
        sim.Set(sim.Wallet(player), "Player.rewards", bytes);
    }

    /// <summary>Сколько шагов пройдено, по подписи заголовка «n/12»; текущий шаг — первый непройденный, у него видно описание.</summary>
    private static (int Count, string? Current) Progress(Viewer viewer)
    {
        viewer.Run(0.4);
        var title = viewer.Ui.Text("steps:toggle") ?? "";
        var count = int.Parse(title.Split(' ').First(part => part.EndsWith("/12", StringComparison.Ordinal)).Split('/')[0]);
        var current = Keys.FirstOrDefault(key => viewer.Ui.Text("step:" + key) is { } text && text.Contains(T($"steps.items.{key}.tip"), StringComparison.Ordinal));
        return (count, current);
    }

    [Fact(DisplayName = "первые шаги: отмечаются по миру, пройденный дальше отмечает и прежние")]
    public void StepsFollowTheWorld()
    {
        SkipUnless();
        using var sim = Sim.Create(new SimOptions { Game = "frames", Size = 64, Fog = false, Land = new { blank = true }, Players = [1, 2] });
        sim.SetCredits(1, 1000);
        // Старт: MCV не развёрнут — текущий шаг первый.
        sim.PutUnit("mcv", 1, 10.5, 10.5);
        using var viewer = sim.Viewer(1);
        using var stranger = sim.Viewer(2);
        viewer.Camera = new CameraState(10, 10, 16);
        Assert.Equal((0, "deploy"), Progress(viewer));

        // Строитель есть, а MCV всё ещё не развёрнут: строитель мог приехать и так — шаг MCV не пройден, а строителя — да.
        sim.PutUnit("builder", 1, 12.5, 12.5);
        Assert.Equal((2, "generator"), Progress(viewer));
        Assert.Contains("✓", viewer.Ui.Text("step:builder"));
        // Чужие шаги — свои.
        Assert.Equal((0, "deploy"), Progress(stranger));

        // Награда за электростанцию остаётся, даже если станцию снесли.
        Reward(sim, 1, "generator");
        Assert.Equal((3, "matter"), Progress(viewer));

        // Открытая плавка — значит, лаборатория и шахта уже были.
        sim.Learn(1, "science", "metalOre");
        ViewerScenes.Place(sim, "matter", -20, -20, 1);
        Assert.Equal((8, "smelter"), Progress(viewer));

        // Первая продажа — пройдено всё.
        Reward(sim, 1, "generator", "sale");
        viewer.Run(0.4);
        Assert.Contains(T("steps.finished"), viewer.Ui.Text("steps"));
        Assert.Equal(12, Progress(viewer).Count);
        Assert.Null(Progress(viewer).Current);
        Assert.Empty(viewer.Log);
    }

    [Fact(DisplayName = "шаг шахты — на металлической руде; цель шага — месторождение металла, потом шахта, к которой ставить лабораторию")]
    public void MineStepNeedsMetalAndPointsThere()
    {
        SkipUnless();
        // Игра skirmish: у неё камера мода (rts), у frames камеры нет. Стартовый отряд убираем: шаги считаем с нуля.
        using var sim = Sim.Create(new SimOptions { Game = "skirmish", Fog = false, Players = [1] });
        sim.Erase([.. sim.Of(1, "Unit")]);
        sim.SetCredits(1, 1000);
        var all = sim.Deposits();
        var first = all.First(spot => spot.Kind == "metal");
        var other = all.First(spot => spot.Kind != "metal");
        sim.PutUnit("builder", 1, first.X + 6.5, first.Y + 6.5);
        var core = ViewerScenes.Place(sim, "command", first.X + 8, first.Y + 8, 1);
        var (coreX, coreY) = sim.At(core);
        // Цель — ближайшее к главному зданию свободное месторождение металла в пределах 80 тайлов.
        var metal = all.Where(spot => spot.Kind == "metal").OrderBy(spot => Math.Pow(spot.X + 1 - coreX - 1.5, 2) + Math.Pow(spot.Y + 1 - coreY - 1.5, 2)).First();
        Reward(sim, 1, "generator", "matter");
        using var viewer = sim.Viewer(1);

        void ShowAndCheck(double x, double y)
        {
            viewer.Run(0.3);
            viewer.Click("steps:show");
            viewer.Run(4);
            var view = viewer.Last.View;
            var (tx, ty) = (x, y);
            Assert.InRange((double)view.X, tx - 1.5, tx + 1.5);
            Assert.InRange((double)view.Y, ty - 8, ty + 8);
        }

        Assert.Equal((0 + 4, "mine"), Progress(viewer));
        // Цель — месторождение металла, а не первое попавшееся.
        ShowAndCheck(metal.X + 1, metal.Y + 1);
        // Шахта на другой руде шаг не проходит: на ней плавку не изучить.
        sim.PutBuilding("mine", other.X, other.Y, 1);
        Assert.Equal("mine", Progress(viewer).Current);
        sim.PutBuilding("mine", metal.X, metal.Y, 1);
        // Руду с шахты в лабораторию возят грузовики: вплотную связаны только одинаковые здания, а трубы — после плавки.
        Assert.Equal("truck", Progress(viewer).Current);
        ShowAndCheck(coreX + 1.5, coreY + 1.5);
        ViewerScenes.PlaceUnit(sim, "truck", (int)coreX + 4, (int)coreY + 1, 1);
        Assert.Equal("lab", Progress(viewer).Current);
        ShowAndCheck(metal.X + 1, metal.Y + 1);
        Assert.Empty(viewer.Log);
    }
}
