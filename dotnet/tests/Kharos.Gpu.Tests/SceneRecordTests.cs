using Base.Shared.Orders;
using Kharos.Base.Scenario;
using Kharos.Client.Game;
using Kharos.Client.Looks;
using Kharos.Client.Ui;
using Kharos.Engine.Layers;
using Kharos.Engine.Terrain;
using Kharos.Engine.World;
using Kharos.Plugins.Game;
using RenderFrame = Kharos.Gpu.Frame;

namespace Kharos.Gpu.Tests;

/// <summary>
/// Записи кадра ABI 2 у движка (Д1): Paint, Sprite, Light, Beam и клетки слоёв (CellLook) — на клиенте base в режиме CLR
/// поверх сценария (Viewer). Записи подкладываются после модов (AfterMods), кадр рисуется без окна. Мод base не собран
/// или нет адаптера — пропуск.
/// </summary>
public sealed class SceneRecordTests(GpuFixture fixture)
{
    private const float Cx = 10, Cy = 8;
    private const int Building = (int)LookLayer.Building;
    // Биты маски Paint (ClientAbi.Paint*; ClientAbi в Kharos.Client — internal).
    private const byte PaintFrame = 2, PaintBuilt = 4, PaintDim = 8, PaintHide = 16;

    private static (Sim Sim, Viewer Viewer, uint Factory) Scene(float light = 1)
    {
        var unavailable = Viewer.Unavailable(ModMode.Clr);
        Assert.SkipWhen(unavailable is not null, unavailable ?? "");
        var sim = Sim.Create(new SimOptions { Size = 64, Fog = false, Land = new { blank = true }, Mode = ModMode.Clr });
        try
        {
            ViewerScenes.Place(sim, "factory", 6, 5, 1);
            var factory = sim.With("Building").First();
            var viewer = sim.Viewer(1, new ViewerOptions { Mode = ModMode.Clr, Width = 320, Height = 200 });
            viewer.Camera = new CameraState(Cx, Cy, 32);
            viewer.Atmosphere = new AtmosphereState(light, 0, 0, 0);
            return (sim, viewer, factory);
        }
        catch
        {
            sim.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Кадр с записями add после модов — копией: ClientGame переиспользует свой кадр, а тесты сравнивают кадры между
    /// собой.
    /// </summary>
    private static RenderFrame Frame(Viewer viewer, Action<ClientScene> add)
    {
        var direct = viewer.Game.AfterMods;
        viewer.Game.AfterMods = scene => { direct?.Invoke(scene); add(scene); };
        RenderFrame frame;
        try { frame = viewer.Step(2); }
        finally { viewer.Game.AfterMods = direct; }
        var copy = new RenderFrame { View = frame.View, Light = frame.Light };
        for (var layer = 0; layer < RenderFrame.Layers; layer++) copy.SpriteLayers[layer].AddRange(frame.SpriteLayers[layer]);
        copy.Cells.AddRange(frame.Cells);
        copy.Spots.AddRange(frame.Spots);
        copy.Blooms.AddRange(frame.Blooms);
        copy.Beams.AddRange(frame.Beams);
        return copy;
    }

    private static int Count(RenderFrame frame, int layer) => frame.SpriteLayers[layer].Count / RenderFrame.Sprite;

    private static float[] Sprite(RenderFrame frame, int layer, int index) => [.. frame.SpriteLayers[layer].Skip(index * RenderFrame.Sprite).Take(RenderFrame.Sprite)];

    private static Look LookOf(Viewer viewer, uint entity) => viewer.Game.LookOf((int)viewer.World.Number(entity, Builtin.Proto, 0))!;

    [Fact]
    public void Paint_HideVariantFrameBuiltDim()
    {
        var (sim, viewer, factory) = Scene();
        using var _ = sim;
        using var __ = viewer;
        var plain = Frame(viewer, _ => { });
        var buildings = Count(plain, Building);
        Assert.True(buildings >= 1);
        var look = LookOf(viewer, factory);
        var original = Sprite(plain, Building, 0);
        Assert.Equal([1f, 1, 1, 1], original[12..16]);

        Assert.Equal(buildings - 1, Count(Frame(viewer, s => s.Paints[factory] = new ScenePaint(PaintHide, 0, 0, 1, 0)), Building));

        // Фаза: кадр из ячейки CellOf; если фаз одна — та же ячейка.
        var framed = Sprite(Frame(viewer, s => s.Paints[factory] = new ScenePaint(PaintFrame, 0, 1, 1, 0)), Building, 0);
        var uv = look.Cells[ClientGame.CellOf(look, 0, 1, 0)];
        Assert.Equal([uv.U0, uv.V0, uv.U1, uv.V1], framed[4..8]);

        // Полпостройки: чертёж целиком без тени и нижняя половина как есть.
        var half = Frame(viewer, s => s.Paints[factory] = new ScenePaint(PaintBuilt, 0, 0, 0.5f, 0));
        Assert.Equal(buildings + 1, Count(half, Building));
        var blueprint = Sprite(half, Building, 0);
        var part = Sprite(half, Building, 1);
        Assert.Equal([ClientGame.Blueprint.R, ClientGame.Blueprint.G, ClientGame.Blueprint.B, ClientGame.Blueprint.A], blueprint[12..16]);
        Assert.Equal(0f, blueprint[10]);
        Assert.Equal(original[1] + original[3] / 2, part[1], 3);
        Assert.Equal(original[3] / 2, part[3], 3);
        Assert.Equal(original[7], part[7], 5);
        Assert.Equal([1f, 1, 1, 1], part[12..16]);

        // Выключенное: темнее, огни кадра гаснут.
        var off = Frame(viewer, s => s.Paints[factory] = new ScenePaint(PaintDim, 0, 0, 1, 1));
        Assert.Equal([ClientGame.SwitchedOff.R, ClientGame.SwitchedOff.G, ClientGame.SwitchedOff.B, ClientGame.SwitchedOff.A], Sprite(off, Building, 0)[12..16]);
        Assert.True(off.Spots.Count <= plain.Spots.Count);
        Assert.DoesNotContain(off.Spots.Where((_, i) => i % 4 == 0), x => x >= 6 && x <= 6 + look.Tiles.Width);

        // Кадр рисуется: недостроенное отличается от целого.
        var gpu = fixture.Require();
        Frame(viewer, _ => { });
        var whole = viewer.Render(gpu);
        Frame(viewer, s => s.Paints[factory] = new ScenePaint(PaintBuilt, 0, 0, 0.3f, 0));
        Assert.False(whole.AsSpan().SequenceEqual(viewer.Render(gpu)));
    }

    [Fact]
    public void Sprite_FreeSpriteTakesLookLayerTeamScaleAndTint()
    {
        var (sim, viewer, factory) = Scene();
        using var _ = sim;
        using var __ = viewer;
        var proto = (int)viewer.World.Number(factory, Builtin.Proto, 0);
        var look = viewer.Game.LookOf(proto)!;
        var plain = Frame(viewer, _ => { });
        var air = (int)LookLayer.Air;
        // rgba записи: r — младший байт; 0xff0000ff — красный, непрозрачный; альфа 0,5 — множитель (0,5, 0, 0, 0,5).
        var frame = Frame(viewer, s => s.Sprites.Add(new SceneSprite(proto, 0, 0, 0, 12, 9, 0.5f, 0.5f, 2, (byte)air, 0xff0000ffu)));
        Assert.Equal(Count(plain, air) + 1, Count(frame, air));
        var sprite = Sprite(frame, air, Count(frame, air) - 1);
        Assert.Equal(look.Tiles.Width * 0.5f, sprite[2], 4);
        Assert.Equal(12 + look.Offset.X * 0.5f - Cx, sprite[0], 4);
        Assert.Equal(2f, sprite[8]);
        Assert.Equal([0.5f, 0, 0, 0.5f], sprite[12..16]);
        // В слое зданий свободный спрайт встаёт в порядок по нижнему краю: ниже фабрики — после неё.
        var ghost = Frame(viewer, s => s.Sprites.Add(new SceneSprite(proto, 0, 0, 0, 12, 30, 1, 0.7f, 0, (byte)Building, 0xffffffffu)));
        Assert.Equal(0.7f, Sprite(ghost, Building, Count(ghost, Building) - 1)[15], 4);
        // Ничья сторона — серым.
        var neutral = Frame(viewer, s => s.Sprites.Add(new SceneSprite(proto, 0, 0, 0, 12, 9, 1, 1, 3, (byte)air, 0xffffffffu)));
        Assert.Equal(1f, Sprite(neutral, air, Count(neutral, air) - 1)[11]);

        var gpu = fixture.Require();
        Frame(viewer, _ => { });
        var before = viewer.Render(gpu);
        Frame(viewer, s => s.Sprites.Add(new SceneSprite(proto, 0, 0, 0, 12, 9, 0.5f, 0.5f, 2, (byte)air, 0xff0000ffu)));
        Assert.False(before.AsSpan().SequenceEqual(viewer.Render(gpu)));
    }

    [Fact]
    public void LightAndBeam_GoToLightingPass()
    {
        var (sim, viewer, _) = Scene(0.1f);
        using var __ = sim;
        using var ___ = viewer;
        var gpu = fixture.Require();
        var plain = Frame(viewer, _ => { });
        var dark = viewer.Render(gpu);
        var lit = Frame(viewer, s =>
        {
            s.Lights.Add(new SceneLight(13, 10, 48, 6, 1));
            s.Lights.Add(new SceneLight(13, 10, 48, 6, 0.01f)); // не ярче MinLevel — не рисуется
            s.Beams.Add(new SceneBeam(4, 10, 120, 1, 1, 0, 3, 0.25f));
        });
        Assert.Equal(plain.Spots.Count + 4, lit.Spots.Count);
        Assert.Equal(plain.Blooms.Count + 4, lit.Blooms.Count);
        Assert.Equal(plain.Beams.Count + 8, lit.Beams.Count);
        var bright = viewer.Render(gpu);
        double Mean(byte[] image, float x, float y)
        {
            var (sx, sy) = lit.View.ToScreen(x, y);
            double sum = 0;
            for (var dy = -3; dy <= 3; dy++)
                for (var dx = -3; dx <= 3; dx++)
                {
                    var at = (((int)sy + dy) * 320 + (int)sx + dx) * 4;
                    sum += image[at] + image[at + 1] + image[at + 2];
                }
            return sum / 49 / 3;
        }
        Assert.True(Mean(bright, 13, 10) > Mean(dark, 13, 10) + 10, "огонь");
        Assert.True(Mean(bright, 5.5f, 10) > Mean(dark, 5.5f, 10) + 5, "луч");
    }

    [Fact]
    public void CellLook_PavingConnectsByKindAndBridgesOverSwamp()
    {
        var (sim, viewer, _) = Scene();
        using var __ = sim;
        using var ___ = viewer;
        // Облики paving/* — свободные облики мода (не виды контента): номер — после всех видов.
        Assert.Equal(1, viewer.Game.CellLookCount);
        Look Named(string path) => viewer.Game.LookOf(path) ?? throw new Xunit.Sdk.XunitException($"нет облика {path}");
        Assert.True(Named("paving/road").Proto >= sim.Host.Content.Protos.Count);
        // Под заводом сцены — свой фундамент: его клетки — до наших.
        var before = Frame(viewer, _ => { }).Cells.Count / RenderFrame.Sprite;
        // Дорога в три тайла и фундамент двух хозяев рядом.
        sim.Lay("road", 12, 10);
        sim.Lay("road", 13, 10);
        sim.Lay("road", 14, 10);
        sim.Lay("foundation", 12, 12, 1);
        sim.Lay("foundation", 13, 12, 2);
        sim.Advance(1);
        var frame = Frame(viewer, _ => { });
        var layers = viewer.Game.Replica.Layers!;
        var paving = layers.Id("paving");
        Assert.True(frame.Cells.Count / RenderFrame.Sprite - before == 5,
            $"клеток {frame.Cells.Count / RenderFrame.Sprite}, до покрытия {before}; у клиента в (12, 10): мир {layers.Get(paving, LayerOwner.World, 12, 10, 0)}, " +
            $"игрок {layers.Get(paving, LayerOwner.Player(1), 12, 10, 0)}; у хоста {sim.PavingCode(12, 10)}; облик paving/road: {viewer.Game.LookId("paving/road")}; журнал: {string.Join("; ", viewer.Log.Take(5))}");
        Uv UvAt(RenderFrame f, int x, int y)
        {
            for (var i = 0; i < f.Cells.Count; i += RenderFrame.Sprite)
                if (MathF.Abs(f.Cells[i] + f.View.X - x) < 0.01f && MathF.Abs(f.Cells[i + 1] + f.View.Y - y) < 0.01f)
                    return new Uv(f.Cells[i + 4], f.Cells[i + 5], f.Cells[i + 6], f.Cells[i + 7]);
            throw new Xunit.Sdk.XunitException($"нет клетки в ({x}, {y})");
        }
        var road = Named("paving/road");
        var foundation = Named("paving/foundation");
        Assert.Equal(road.Cells[ClientGame.CellOf(road, 0, 0, 2)], UvAt(frame, 12, 10));
        Assert.Equal(road.Cells[ClientGame.CellOf(road, 0, 0, 2 | 8)], UvAt(frame, 13, 10));
        Assert.Equal(road.Cells[ClientGame.CellOf(road, 0, 0, 8)], UvAt(frame, 14, 10));
        // Чей бы ни был фундамент, соседние плиты стыкуются.
        Assert.Equal(foundation.Cells[ClientGame.CellOf(foundation, 0, 0, 2)], UvAt(frame, 12, 12));
        Assert.Equal(foundation.Cells[ClientGame.CellOf(foundation, 0, 0, 8)], UvAt(frame, 13, 12));

        // Дорога по болоту — мост, стыковка та же.
        sim.Paint(14, 10, 1, new Brush { Terrain = (int)TerrainType.Swamp, Shape = "square" });
        sim.Lay("road", 14, 10);
        sim.Advance(1);
        var swamp = Frame(viewer, _ => { });
        Assert.SkipWhen(viewer.Game.Land?.TerrainAt(14, 10) != TerrainType.Swamp, "кисть не сделала болото у клиента");
        var bridge = Named("paving/bridge");
        Assert.Equal(bridge.Cells[ClientGame.CellOf(bridge, 0, 0, 8)], UvAt(swamp, 14, 10));
        Assert.Equal(road.Cells[ClientGame.CellOf(road, 0, 0, 2 | 8)], UvAt(swamp, 13, 10));

        var gpu = fixture.Require();
        var paved = viewer.Render(gpu);
        Assert.Contains(paved.Where((_, i) => i % 4 == 0), r => r > 0);
    }
}
