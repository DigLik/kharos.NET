using Base.Client.Sounds;
using Kharos.Audio;

namespace Kharos.Base.Tests.AudioTests;

/// <summary>Банк звуков base: полнота, повторяемость, форма записей и совпадение с TS-эталонами.</summary>
public sealed class BankTests
{
    private const int Rate = AudioFormat.SampleRate;

    private static readonly string[] Pilot =
    [
        // sounds/synth
        "rifle", "machinegun", "cannon", "launcher", "laser", "arc", "blast", "bigBlast", "attacked", "click", "hover", "open", "deny", "select",
        "selectBuilding", "place", "order", "attackOrder", "spotted", "found", "nuke", "nukeClose", "ping", "intercepted",
        "hum", "machinery", "drill", "steps", "creak", "clank", "thunder", "noise:white", "noise:pink", "noise:brown", "reverb",
        // sounds/motors
        "motor:petrol:idle", "motor:petrol:load", "motor:diesel:idle", "motor:diesel:load", "motor:heavy:idle", "motor:heavy:load",
        "motor:rotor:idle", "motor:rotor:load", "motor:props:idle", "motor:props:load", "motor:jet:idle", "motor:jet:load",
        "running:clatter", "running:roll", "running:squeal", "running:screech", "ignition", "ignitionDiesel", "airBrake",
        // sounds/world
        "built", "deploy", "rollout", "brownout", "countdown", "unitReady", "researched", "upgraded", "warheadReady", "reward", "delivered", "protectionOver",
        // sounds/world, остальное (Б-Зв): бой, щиты, ворота, корабли, деньги, чат и петли мира
        "flame", "flak", "artillery", "bombFall", "ricochet", "wreck", "collapse", "nukeBlast", "shieldHit", "sizzle", "burn", "zap",
        "doorsLocked", "liftoff", "shieldDown", "shieldUp", "gate", "crates", "paved", "radar", "shipLaunch", "shipLand", "depleted",
        "powerOn", "powerOff", "cargo", "coins", "gift", "defeat", "surrender", "chat", "chatSystem",
        "overload", "construct", "weld", "tractor", "servo", "reactor", "dome", "hydraulic", "steam", "rocket", "charge", "flow",
    ];

    private static readonly HashSet<string> WorldLoopNames = ["overload", "construct", "weld", "tractor", "servo", "reactor", "dome", "hydraulic", "steam", "rocket", "charge", "flow"];

    [Fact]
    public void BankHasEveryPilotSound()
    {
        var names = SoundBank.All.Select(entry => entry.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Equal(Pilot.Order(StringComparer.Ordinal), names.Order(StringComparer.Ordinal));
        foreach (var entry in SoundBank.All)
        {
            var loop = entry.Name.StartsWith("hum") || entry.Name is "machinery" or "drill" or "steps" || entry.Name.StartsWith("noise:") || entry.Name.StartsWith("motor:") || entry.Name.StartsWith("running:") || entry.Name == "reverb" || WorldLoopNames.Contains(entry.Name);
            Assert.Equal(loop ? 1 : SoundBank.Variants, entry.Variants);
        }
    }

    [Fact]
    public void RenderingIsDeterministic()
    {
        // Те же зёрна — те же сэмплы; другой вариант — другой шум.
        foreach (var name in new[] { "rifle", "arc", "ignitionDiesel", "airBrake", "thunder", "creak", "deploy", "researched", "motor:heavy:load", "running:roll", "noise:pink", "reverb" })
        {
            var entry = SoundBank.All.Single(e => e.Name == name);
            var first = entry.Render(Rate, 0);
            var second = entry.Render(Rate, 0);
            Assert.Equal(first.Length, second.Length);
            for (var channel = 0; channel < first.Length; channel++)
                Assert.True(first[channel].AsSpan().SequenceEqual(second[channel]), $"{name}: канал {channel} разошёлся");
            if (entry.Variants > 1)
                Assert.False(first[0].AsSpan().SequenceEqual(entry.Render(Rate, 1)[0]), $"{name}: варианты не различаются");
        }
    }

    [Fact]
    public void RecordsAreFiniteBoundedAndAudible()
    {
        foreach (var (name, rendered) in BankFixture.All)
        {
            var stereo = name.StartsWith("noise:") || name == "reverb";
            foreach (var channels in rendered.Variants)
            {
                Assert.Equal(stereo ? 2 : 1, channels.Length);
                foreach (var data in channels)
                {
                    Assert.NotEmpty(data);
                    Assert.Equal(channels[0].Length, data.Length);
                    double peak = 0, sum = 0;
                    foreach (var value in data)
                    {
                        Assert.True(float.IsFinite(value), $"{name}: не число");
                        peak = Math.Max(peak, Math.Abs(value));
                        sum += value * (double)value;
                    }
                    Assert.True(peak > 0.05, $"{name}: тихо, пик {peak}");
                    if (name.StartsWith("noise:"))
                        Assert.InRange(Math.Sqrt(sum / data.Length), 0.2499, 0.2501);
                    else
                        Assert.InRange(peak, 0.9999, 1.0001);
                }
            }
        }
        // Петли — на целое число тактов мотора: двигатель внутреннего сгорания без шва.
        Assert.Equal(2 * Rate, BankFixture.All["motor:petrol:idle"].Variants[0][0].Length);
        Assert.Equal(4 * Rate, BankFixture.All["hum"].Variants[0][0].Length);
        Assert.Equal(8 * Rate, BankFixture.All["noise:brown"].Variants[0][0].Length);
        Assert.Equal(4 * Rate, BankFixture.All["reverb"].Variants[0][0].Length);
    }

    [Fact]
    public void MatchesTsGoldens()
    {
        foreach (var (key, golden) in BankGolden.All)
        {
            var split = key.LastIndexOf(':');
            var name = key[..split];
            var variant = int.Parse(key[(split + 1)..]);
            var data = BankFixture.All[name].Variants[variant][0];
            Assert.Equal(golden.Length, data.Length);
            var worst = 0.0;
            for (var k = 0; k < golden.Samples.Length; k++)
                worst = Math.Max(worst, Math.Abs(golden.Samples[k] - data[k * golden.Stride]));
            Assert.True(worst <= 2e-6, $"{key}: расхождение с TS {worst}");
            double sum = 0;
            foreach (var value in data)
                sum += Math.Abs(value);
            Assert.True(Math.Abs(sum - golden.Sum) <= 1e-6 * golden.Sum, $"{key}: сумма {sum} вместо {golden.Sum}");
        }
    }
}
