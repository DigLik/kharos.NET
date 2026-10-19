using Kharos.Base.Tests.Kernel;
using Kharos.Plugins.Game;

namespace Kharos.Base.Tests.Combat;

/// <summary>Бой в двух режимах кода мода (не из TS): CLR и host.wasm дают тот же мир до байта.</summary>
public sealed class ParityTests
{
    [Fact(DisplayName = "показательный бой в CLR и на host.wasm даёт тот же мир до байта")]
    public void ClrAndWasmAgree()
    {
        Assert.SkipWhen(Sim.Unavailable(ModMode.Wasm) is not null || Sim.Unavailable(ModMode.Clr) is not null, "нужны и Base.Host.dll, и host.wasm");
        Sim Run(ModMode mode)
        {
            var sim = Sim.Create(new SimOptions { Size = 512, Land = Scene.Land, Mode = mode });
            long seed = 11;
            var army = Sim.RandomArmy(8000, null, () => (seed = seed * 16807 % 2147483647) / 2147483647.0);
            sim.SpawnBattle(1, 0, 0, army, army, 4);
            for (var i = 0; i < 400; i++)
            {
                if (i % 20 == 0)
                    sim.DriveBattle(1);
                sim.Advance();
            }
            return sim;
        }
        using var clr = Run(ModMode.Clr);
        using var wasm = Run(ModMode.Wasm);
        Assert.True(clr.SameWorld(wasm));
    }
}
