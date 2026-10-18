using Kharos.Engine.Movement;
using Kharos.Engine.Terrain;

namespace Kharos.Engine.Tests.Movement;

/// <summary>terrainSpeed.test.ts: доли скорости по местности — из ходовых moves.json.</summary>
public sealed class TerrainSpeedTests
{
    private static (int X, int Y) TileOf(MoveWorld world, TerrainType terrain, Biome biome = Biome.Marsh)
    {
        for (var y = -500; y < 500; y++)
            // Не у подножия обрыва: там своё замедление.
            for (var x = -500; x < 500; x++)
            {
                var (kind, at, foot) = world.Nav.TileAt(x, y);
                if (kind == terrain && at == biome && !foot)
                    return (x, y);
            }
        throw new InvalidOperationException("не нашлось местности");
    }

    private static double Speed(MoveWorld world, string type, (int X, int Y) tile)
    {
        var spec = MoveWorld.Spec(type);
        return world.Nav.TerrainSpeed(world.Nav.Chassis.Of(spec.Chassis), spec.Radius, tile.X, tile.Y);
    }

    [Fact(DisplayName = "колёсная техника на песке теряет 10% скорости, на болоте — 80%; гусеницы и пехота — меньше; авиации всё равно")]
    public void Classes()
    {
        var world = new MoveWorld();
        var sand = TileOf(world, TerrainType.Sand, Biome.Erg);
        var swamp = TileOf(world, TerrainType.Swamp);
        var rock = TileOf(world, TerrainType.Rock, Biome.Erg);
        Assert.True(world.Nav.IsWalkable(swamp.X, swamp.Y));
        Assert.Equal(1, Speed(world, "truck", rock));
        Assert.Equal(0.9, Speed(world, "truck", sand), 9);
        Assert.Equal(0.2, Speed(world, "truck", swamp), 9);
        Assert.Equal(0.5, Speed(world, "tank", swamp), 9);
        Assert.Equal(1, Speed(world, "infantry", sand));
        Assert.Equal(1, Speed(world, "drone", swamp));
        // Правила меняются данными: колёса вязнут в болоте вдвое (rules.vehicleSwamp в TS — теперь moves.json).
        var softer = new MoveWorld(chassis: MoveWorld.MovesWith((name, _) => name == "wheels"
            ? new Chassis { Name = "wheels", Sand = 0.9, Swamp = 0.5, Brine = 0.875, Toxic = 0.5, Paved = true, Road = 1.5, Foundation = 1.2 }
            : null));
        Assert.Equal(0.5, Speed(softer, "truck", swamp), 9);
    }

    [Fact(DisplayName = "на солончаках болото промёрзло и вязнут меньше, в красных пустошах оно жжёт наземных", Skip = "ждёт С5: урон местности (harm) — в бою мода; доли скорости — в соседнем тесте")]
    public void FrozenAndToxic()
    {
    }

    [Fact(DisplayName = "на солончаках болото промёрзло: колёса вязнут меньше, в красных пустошах — как в болоте")]
    public void FrozenSwamp()
    {
        var world = new MoveWorld();
        Assert.Equal(0.8, Speed(world, "truck", TileOf(world, TerrainType.Swamp, Biome.SaltFlats)), 9);
        Assert.Equal(0.2, Speed(world, "truck", TileOf(world, TerrainType.Swamp, Biome.RedWastes)), 9);
        Assert.Equal(0.875, Speed(world, "infantry", TileOf(world, TerrainType.Swamp, Biome.SaltFlats)), 9);
    }

    [Fact(DisplayName = "дорога ускоряет колёса и пехоту, гусеницам только снимает замедление; узкая — не крупным")]
    public void Roads()
    {
        var world = new MoveWorld();
        var swamp = TileOf(world, TerrainType.Swamp, Biome.Erg);
        var paving = world.Layers.Id("paving");
        // Код покрытия: хозяин·2 + вид + 2 (paveCode): 3 — дорога игрока 0, 2 — фундамент.
        world.Layers.Set(paving, Kharos.Engine.Layers.LayerOwner.World, swamp.X, swamp.Y, 0, 3);
        Assert.Equal(1.5, Speed(world, "infantry", swamp));
        Assert.Equal(1, Speed(world, "tank", swamp));
        // Карриер шире тайла: полосы в один тайл ему мало.
        Assert.Equal(1, Speed(world, "carrier", swamp));
        world.Layers.Set(paving, Kharos.Engine.Layers.LayerOwner.World, swamp.X + 1, swamp.Y, 0, 3);
        world.Layers.Set(paving, Kharos.Engine.Layers.LayerOwner.World, swamp.X, swamp.Y + 1, 0, 2);
        world.Layers.Set(paving, Kharos.Engine.Layers.LayerOwner.World, swamp.X + 1, swamp.Y + 1, 0, 2);
        Assert.Equal(1.5, Speed(world, "carrier", swamp));
        Assert.Equal(1.2, Speed(world, "buggy", (swamp.X, swamp.Y + 1)));
    }
}
