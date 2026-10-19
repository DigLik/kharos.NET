using Base.Shared.Orders;
using Kharos.Client.Game;

namespace Kharos.Base.Tests.ClientEffects;

/// <summary>
/// Эффекты мира клиента base (Effects/*, docs/dotnet-n6.md, К1) на реплике хоста (Viewer): Paint зданий — стройка растёт
/// снизу, выключенное темнее; взрыв — кадры effects/blast, огонь и дым; лучи строителя к стройке.
/// </summary>
public sealed class EffectsTests
{
    /// <summary>Биты записи Paint (ClientAbi.Paint*): кадр, доля постройки, затемнение.</summary>
    private const byte PaintFrame = 2, PaintBuilt = 4, PaintDim = 8;

    /// <summary>База игрока 1 на ровной земле: главное здание, электростанция, плавильня; строитель рядом.</summary>
    private static (Sim Sim, uint Smelter, uint Builder) Base()
    {
        LacksTests.SkipUnless();
        var sim = Sim.Create(new SimOptions { Size = 64, Fog = false, Land = new { blank = true } });
        sim.SetCredits(1, 10000);
        sim.Learn(1);
        ViewerScenes.Place(sim, "command", 4, 4, 1);
        ViewerScenes.Place(sim, "generator", 9, 4, 1);
        var smelter = ViewerScenes.Place(sim, "smelter", 12, 4, 1);
        var builder = sim.PutUnit("builder", 1, 8.5, 12.5);
        return (sim, smelter, builder);
    }

    private static Viewer Watch(Sim sim)
    {
        var viewer = sim.Viewer(1, new ViewerOptions { Width = 1280, Height = 720 });
        viewer.Camera = new CameraState(10, 9, 32);
        viewer.Step(2);
        return viewer;
    }

    [Fact]
    public void SitesGrowAndSwitchedOffBuildingsDim()
    {
        var (sim, smelter, builder) = Base();
        using var _ = sim;
        sim.Send(1, new WorkOrder { Building = smelter, On = false });
        // Ровная земля — песок: под стройку кладётся фундамент.
        for (var dy = 0; dy < 2; dy++)
            for (var dx = 0; dx < 2; dx++)
                sim.Lay(global::Base.Shared.Rules.PaveRules.Foundation, 9 + dx, 9 + dy);
        sim.Send(1, new BuildOrder { Building = "radar", X = 9, Y = 9, Builders = [builder] });
        sim.Advance();
        var refused = string.Join("; ", sim.TakeSignals(global::Base.Shared.Signals.RefusedSignal.Name)
            .Select(signal => MemoryPack.MemoryPackSerializer.Deserialize<global::Base.Shared.Signals.RefusedSignal>(signal.Payload)?.Reason));
        Assert.True(sim.Count("Site") == 1, "площадки нет: " + refused);
        var site = sim.With("Site")[0];
        using var viewer = Watch(sim);
        viewer.Step(2);
        // Площадка, к которой строитель ещё не приступил, — чертёж: доля постройки 0.
        var planned = viewer.Draws.Paints[site];
        Assert.True(planned.Has(PaintBuilt));
        Assert.Equal(0f, planned.Built);
        // Своё выключенное — затемнение 1; анимация стоит: кадр — своя фаза здания.
        var off = viewer.Draws.Paints[smelter];
        Assert.True(off.Has(PaintDim));
        Assert.Equal(1f, off.Dim);
        Assert.True(off.Has(PaintFrame));
        var command = sim.With("Building").First();
        Assert.False(viewer.Draws.Paints[command].Has(PaintDim));
        Assert.False(viewer.Draws.Paints[command].Has(PaintBuilt));
        // Значок «Выключено» над плавильней.
        Assert.Contains(viewer.Draws.Sprites, sprite => sprite.Proto == viewer.Game.LookId("power/off"));

        // Стройка пошла: здание растёт снизу на долю сделанного, над ним полоска хода.
        Assert.True(viewer.Until(() => sim.Has(site, "Site") && sim.Num(site, "Site.progress") > 3, limit: 60));
        viewer.Step();
        var growing = viewer.Draws.Paints[site];
        Assert.True(growing.Has(PaintBuilt));
        Assert.InRange(growing.Built, 0.001f, 0.999f);
        // Луч стройки — от строителя к площадке, зелёный (MEND), со светом сварки.
        var mend = Kharos.Mod.Sdk.Client.Draw.Rgba(0x4cff8c) & 0xffffff;
        Assert.Contains(viewer.Draws.Lines, line => (line.Color & 0xffffff) == mend);
        Assert.NotEmpty(viewer.Draws.Lights);
        Assert.Empty(viewer.Log);
    }

    [Fact]
    public void BlastDrawsFramesLightAndSmoke()
    {
        LacksTests.SkipUnless();
        using var sim = Sim.Create(new SimOptions { Size = 64, Fog = false, Land = new { blank = true } });
        var blast = sim.World.Spawn();
        sim.Add(blast, "Position");
        sim.Set(blast, "Position.x", 10);
        sim.Set(blast, "Position.y", 9);
        sim.Add(blast, "Blast");
        sim.Set(blast, "Blast.size", 1);
        sim.Set(blast, "Blast.life", 20);
        sim.Flush();
        using var viewer = Watch(sim);
        var look = viewer.Game.LookId("effects/blast");
        var puff = viewer.Game.LookId("effects/puff");
        Assert.True(look >= 0 && puff >= 0);
        var sprite = Assert.Single(viewer.Draws.Sprites, sprite => sprite.Proto == look);
        // Картинка шире радиуса в 2,6 раза, с серединой на месте взрыва; свежий взрыв — первые кадры.
        Assert.Equal(2.6 / 3, sprite.Scale, 3);
        Assert.Equal(10 - 1.3, sprite.X, 3);
        Assert.InRange(sprite.Frame, 0, 1);
        Assert.Contains(viewer.Draws.Lights, light => Math.Abs(light.X - 10) < 1e-3 && light.Spot > 0);
        Assert.Contains(viewer.Draws.Sprites, s => s.Proto == puff);
        // Взрыв догорает: кадры идут к дыму, хост убирает его по сроку.
        sim.Advance(15);
        viewer.Step();
        if (viewer.Draws.Sprites.FirstOrDefault(s => s.Proto == look) is { Proto: >= 0 } later)
            Assert.True(later.Frame >= 4, $"кадр {later.Frame}");
        Assert.Empty(viewer.Log);
    }

    [Fact]
    public void BattleDrawsShotsBarsScorchAndWrecks()
    {
        LacksTests.SkipUnless();
        using var sim = Sim.Create(new SimOptions { Size = 64, Fog = false, Land = new { blank = true } });
        var ours = sim.PutUnit("tank", 1, 8.5, 9.5);
        var theirs = sim.PutUnit("tank", 2, 12.5, 9.5);
        using var viewer = Watch(sim);
        // Танки сходятся сами: стреляют ядрами, у подбитого — полоска прочности над ним.
        Assert.True(viewer.Until(() => sim.Count("Shot") > 0, limit: 20));
        viewer.Step();
        Assert.True(viewer.Draws.Rects.Count > 0, "ни ядра, ни вспышки у ствола");
        Assert.True(viewer.Until(() => sim.HealthOf(ours) < 1 || sim.HealthOf(theirs) < 1, limit: 20));
        viewer.Step();
        var back = Kharos.Mod.Sdk.Client.Draw.Rgba(0x080d14) & 0xffffff;
        Assert.Contains(viewer.Draws.Rects, rect => (rect.Color & 0xffffff) == back);
        // Ядра оставляют гарь, погибший — остов: со слоя следов движка.
        Assert.True(viewer.Until(() => !sim.Alive(ours) || !sim.Alive(theirs), limit: 120));
        viewer.Step(2);
        var wreck = viewer.Game.LookId("decals/wreck/tank");
        var scorches = new[] { 8, 16, 24, 32, 40, 48 }.Select(size => viewer.Game.LookId("decals/scorch/" + size)).Append(viewer.Game.LookId("decals/pock")).ToHashSet();
        Assert.Contains(viewer.Draws.Sprites, sprite => sprite.Proto == wreck);
        Assert.Contains(viewer.Draws.Sprites, sprite => scorches.Contains(sprite.Proto));
        Assert.Empty(viewer.Log);
    }
}
