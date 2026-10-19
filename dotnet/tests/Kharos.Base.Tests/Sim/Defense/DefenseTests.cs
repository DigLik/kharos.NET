using Base.Shared;
using Base.Shared.Orders;
using Base.Shared.Rules;
using Kharos.Base.Tests.Kernel;
using Kharos.Engine.Terrain;
using static Kharos.Base.Tests.Combat.Arena;
using Tables = Kharos.Base.Tests.Kernel.Data;

namespace Kharos.Base.Tests.Defense;

/// <summary>Оборона (packages/tests/defense.test.ts): стены и турели на песке и вне зоны, три турели зданий, стена на линии огня.</summary>
public sealed class DefenseTests
{
    private static readonly object Rules = new { techTree = false };

    private static readonly string[] Defenses = ["wall", "turret", "rocketTurret", "cannonTurret"];

    /// <summary>Радиус зоны главного здания (CONTROL_RADIUS).</summary>
    private static double ControlRadius => BuildRules.ZoneRadius(Tables.Buildings["command"]);

    /// <summary>Скальное место под Core и песчаный тайл в его будущей зоне (frontier).</summary>
    private static ((int X, int Y) Core, (int X, int Y) Sand) Frontier(Sim sim)
    {
        for (var y = 0; y < 400; y++)
            for (var x = 0; x < 400; x++)
            {
                if (!Scene.Rock(sim, x, y, 3, 3) || !Scene.CanPlace(sim, "command", x, y))
                    continue;
                var (centerX, centerY) = (x + 1.5, y + 1.5);
                for (var tileY = y - 10; tileY <= y + 12; tileY++)
                    for (var tileX = x - 10; tileX <= x + 12; tileX++)
                    {
                        if (sim.Land!.TerrainAt(tileX, tileY) != TerrainType.Sand)
                            continue;
                        if (Math.Sqrt((tileX + 0.5 - centerX) * (tileX + 0.5 - centerX) + (tileY + 0.5 - centerY) * (tileY + 0.5 - centerY)) > 12)
                            continue;
                        if (Scene.CanPlace(sim, "wall", tileX, tileY))
                            return ((x, y), (tileX, tileY));
                    }
            }
        throw new InvalidOperationException("Не нашлось границы скалы с песком");
    }

    /// <summary>Скальное место под здание не ближе min тайлов от точки: там ещё ничьей зоны нет (farSpot).</summary>
    private static (int X, int Y) FarSpot(Sim sim, double fromX, double fromY, double min)
    {
        for (var y = 0; y < 400; y++)
            for (var x = 0; x < 400; x++)
            {
                if (!Scene.Rock(sim, x, y, 2, 2) || !Scene.CanPlace(sim, "generator", x, y))
                    continue;
                if (Math.Sqrt((x + 0.5 - fromX) * (x + 0.5 - fromX) + (y + 0.5 - fromY) * (y + 0.5 - fromY)) < min)
                    continue;
                return (x, y);
            }
        throw new InvalidOperationException("Не нашлось скалы вдали от базы");
    }

    /// <summary>Проходимый тайл рядом с (x, y), не он сам: где поставить строителя.</summary>
    private static (int X, int Y) BuilderTile(Sim sim, int x, int y)
    {
        for (var radius = 1; radius <= 3; radius++)
            for (var dy = -radius; dy <= radius; dy++)
                for (var dx = -radius; dx <= radius; dx++)
                    if ((dx != 0 || dy != 0) && sim.Walkable(x + dx, y + dy))
                        return (x + dx, y + dy);
        throw new InvalidOperationException("Не нашлось места строителю");
    }

    [Fact(DisplayName = "стены и турели ставятся на песке, обычные здания — нет; песок оставляет 70% прочности")]
    public void DefensesStandOnSand()
    {
        using var sim = Create(Rules);
        var (_, sand) = Frontier(sim);
        foreach (var type in Defenses)
        {
            Assert.True(Scene.CanPlace(sim, type, sand.X, sand.Y));
            Assert.Equal(BuildRules.SandDurability, sim.Durability(type, sand.X, sand.Y));
        }
        Assert.False(Scene.CanPlace(sim, "generator", sand.X, sand.Y));
        Assert.Equal(1, sim.Durability("generator", sand.X, sand.Y));
    }

    [Fact(DisplayName = "строитель возводит стену на песке только до её песчаного предела прочности")]
    public void BuilderRaisesAWallOnSandToItsLimit()
    {
        using var sim = Create(Rules);
        var (core, sand) = Frontier(sim);
        Place(sim, "command", core.X, core.Y, 1);
        Assert.True(sim.CanBuild(1, "wall", sand.X, sand.Y));

        var tile = BuilderTile(sim, sand.X, sand.Y);
        var builder = Spawn(sim, "builder", 1, tile.X, tile.Y);
        sim.SetCredits(1, 100);
        sim.Send(1, new BuildOrder { Building = "wall", X = sand.X, Y = sand.Y, Builders = [builder] });
        sim.Advance();
        var wall = sim.SiteAt(sand.X, sand.Y);
        Assert.NotEqual(0u, wall);

        sim.Seconds(3);
        Assert.True(sim.Has(wall, Names.Building.Name));
        Assert.False(sim.Has(wall, Names.Site.Name));
        Assert.Equal(BuildRules.SandDurability, sim.Num(wall, "Health.value"));
        Assert.Equal(BuildRules.SandDurability, sim.Num(wall, "Health.max"));
        Assert.False(sim.Walkable(sand.X, sand.Y));
        // Стена в сеть не входит и зону не расширяет.
        Assert.Equal(0u, sim.NetworkOf(wall));
    }

    [Fact(DisplayName = "стены и турели ставятся вне зоны строительства, обычные здания — нет")]
    public void DefensesStandOutsideTheZone()
    {
        using var sim = Create(Rules);
        var (core, _) = Frontier(sim);
        Place(sim, "command", core.X, core.Y, 1);
        var spot = FarSpot(sim, core.X + 1.5, core.Y + 1.5, ControlRadius + 8);

        Assert.False(sim.CanBuild(1, "generator", spot.X, spot.Y));
        foreach (var type in Defenses)
            Assert.True(sim.CanBuild(1, type, spot.X, spot.Y));
    }

    [Fact(DisplayName = "в чужой зоне не ставится и оборона")]
    public void NoDefensesInAForeignZone()
    {
        using var sim = Create(Rules);
        var (core, _) = Frontier(sim);
        Place(sim, "command", core.X, core.Y, 1);
        var spot = FarSpot(sim, core.X + 1.5, core.Y + 1.5, 2 * ControlRadius + 4);

        // Главное здание второго игрока неподалёку: место входит в его зону (12 тайлов), само здание его не занимает,
        // а до зоны первого далеко.
        (int X, int Y)? foe = null;
        for (var dy = -10; dy <= 10 && foe is null; dy++)
            for (var dx = -10; dx <= 10; dx++)
            {
                var distance = Math.Sqrt((dx + 1) * (dx + 1) + (dy + 1) * (dy + 1));
                if (distance < 6 || distance > 10)
                    continue;
                if (Scene.Rock(sim, spot.X + dx, spot.Y + dy, 3, 3) && Scene.CanPlace(sim, "command", spot.X + dx, spot.Y + dy))
                {
                    foe = (spot.X + dx, spot.Y + dy);
                    break;
                }
            }
        Assert.NotNull(foe);
        Place(sim, "command", foe!.Value.X, foe.Value.Y, 2);

        // Второй игрок здесь строить может — место в его зоне; первый не может: рядом чужая зона.
        Assert.True(sim.CanBuild(2, "generator", spot.X, spot.Y));
        foreach (var type in Defenses)
            Assert.False(sim.CanBuild(1, type, spot.X, spot.Y));
    }

    [Fact(DisplayName = "стройка обороны вне зоны идёт своим ходом: свободный строитель доводит стену до конца")]
    public void DefenseSiteOutsideTheZoneIsFinished()
    {
        using var sim = Create(Rules);
        var (core, _) = Frontier(sim);
        Place(sim, "command", core.X, core.Y, 1);
        var spot = FarSpot(sim, core.X + 1.5, core.Y + 1.5, ControlRadius + 8);
        Assert.True(sim.CanBuild(1, "wall", spot.X, spot.Y));

        var tile = BuilderTile(sim, spot.X, spot.Y);
        Spawn(sim, "builder", 1, tile.X, tile.Y);
        sim.SetCredits(1, 100);
        // Строителей не называем: за площадку берётся свободный — та же проверка зоны, что и у стройки.
        sim.Send(1, new BuildOrder { Building = "wall", X = spot.X, Y = spot.Y, Builders = [] });
        sim.Advance();
        var wall = sim.SiteAt(spot.X, spot.Y);
        Assert.NotEqual(0u, wall);

        sim.Seconds(3);
        Assert.True(sim.Has(wall, Names.Building.Name));
        Assert.False(sim.Has(wall, Names.Site.Name));
    }

    [Fact(DisplayName = "три оборонительные турели получают своё оружие, сами стреляют и гибнут вместе с основанием")]
    public void ThreeTurretsGetTheirGunsAndDieWithTheBase()
    {
        using var sim = Create(Rules);
        (string Building, string Turret)[] types = [("turret", "gunner"), ("rocketTurret", "rocket"), ("cannonTurret", "cannon")];
        var buildings = new List<uint>();
        var mounted = new List<uint>();
        for (var i = 0; i < types.Length; i++)
        {
            var building = Place(sim, types[i].Building, i * 10, 0, 1);
            // Достроенная турель пуста: патроны ей подвозят.
            sim.SetStock(building, "ammo", 100);
            var turrets = sim.TurretsOf(building);
            Assert.Single(turrets);
            Assert.Equal(types[i].Turret, Scene.TypeOf(sim, turrets[0]));
            Assert.True(sim.Has(turrets[0], Names.Armed.Name));
            buildings.Add(building);
            mounted.Add(turrets[0]);
        }

        var foe = Spawn(sim, "infantry", 2, 4, 0);
        sim.Seconds(1);
        Assert.True((sim.Alive(foe) ? Health(sim, foe) : 0) < 1);

        // У прикреплённой турели нет своей прочности: разрушается основание — следующий тик убирает сироту.
        Assert.False(sim.Has(mounted[0], Names.Health.Name));
        sim.Erase(buildings[0]);
        sim.Advance();
        Assert.False(sim.Alive(mounted[0]));
    }

    [Fact(DisplayName = "турель здания не пытается гнаться за дальним стрелком и перестаёт работать под разбором")]
    public void BuildingTurretDoesNotChaseAndStopsUnderDemolition()
    {
        using var sim = Create(Rules);
        var defense = Place(sim, "turret", 0, 0, 1);
        var gun = sim.TurretsOf(defense)[0];
        // Правка юнита места под зданием не даёт: ставим рядом, а точное место задаём ниже (в TS spawnUnit без проверок).
        var lancer = Spawn(sim, "lancer", 8, 8, 0);
        // Между дальностью лазера до края здания и дальностью пулемёта турели с надбавкой TOWER_RANGE.
        sim.Set(lancer, "Position.x", 8.2);
        sim.Set(lancer, "Position.y", 0.5);

        // Лазер достаёт до здания, а его пулемёт в ответ — нет. Здание не должно попадать в логику движения юнита.
        sim.Send(2, new AttackOrder { Units = [lancer], Target = defense });
        sim.Seconds(2);
        Assert.True(Health(sim, defense) < 1);
        Assert.Equal(0, sim.Num(gun, "Armed.chase"));
        Assert.Equal(0u, sim.TargetOf(gun));

        sim.Erase(lancer);
        var tank = Spawn(sim, "tank", 2, 4, 0);
        sim.Seconds(0.5);
        sim.Add(defense, Names.Site.Name);
        sim.Set(defense, "Site.progress", 1);
        sim.Set(defense, "Site.demolish", 1);
        // Даём уже выпущенным пулям долететь, затем проверяем, что новых выстрелов нет.
        sim.Seconds(0.5);
        var stopped = Health(sim, tank);
        sim.Seconds(1);
        Assert.Equal(stopped, Health(sim, tank));
    }

    /// <summary>Стена игрока 1 поперёк пути с запада на восток, стрелок игрока 2 к западу от неё, цель игрока 3 — к востоку (behindWall).</summary>
    private static (Sim Sim, uint Wall, uint Gun, uint Foe) BehindWall(string shooter, string target)
    {
        var sim = Create(Rules);
        var wall = Place(sim, "wall", 2, 0, 1);
        var gun = Spawn(sim, shooter, 2, 0, 0);
        var foe = Spawn(sim, target, 3, 4, 0);
        sim.Send(2, new AttackOrder { Units = [gun], Target = foe });
        return (sim, wall, gun, foe);
    }

    [Fact(DisplayName = "стена принимает на себя пули, ядро и лазер: цель за ней остаётся целой")]
    public void WallTakesBulletsShellsAndLasers()
    {
        foreach (var shooter in new[] { "infantry", "tank", "lancer" })
        {
            var (sim, wall, _, foe) = BehindWall(shooter, "builder");
            using var _sim = sim;
            sim.Seconds(2);
            Assert.Equal(1, Health(sim, foe));
            Assert.True(Health(sim, wall) < 1);
        }
    }

    [Fact(DisplayName = "ракета летит поверх стены, разряд бьёт через неё")]
    public void RocketsFlyOverAndArcsGoThrough()
    {
        var rocket = BehindWall("rocketeer", "builder");
        using (rocket.Sim)
        {
            rocket.Sim.Seconds(3);
            Assert.True(Health(rocket.Sim, rocket.Foe) < 1);
            Assert.Equal(1, Health(rocket.Sim, rocket.Wall));
        }

        // Разряд идёт к цели напрямую; стены он не замечает, и в цепь ему попадается разве что сама стена.
        var arc = BehindWall("tesla", "builder");
        using (arc.Sim)
        {
            arc.Sim.Seconds(1.5);
            Assert.True(Health(arc.Sim, arc.Foe) < 1);
        }
    }

    [Fact(DisplayName = "своя стена своим не мешает: через неё стреляют, как через бруствер")]
    public void OwnWallDoesNotGetInTheWay()
    {
        using var sim = Create(Rules);
        var own = Place(sim, "wall", 2, 0, 1);
        var gun = Spawn(sim, "infantry", 1, 0, 0);
        var foe = Spawn(sim, "builder", 2, 4, 0);
        sim.Send(1, new AttackOrder { Units = [gun], Target = foe });
        sim.Seconds(2);
        Assert.True(Health(sim, foe) < 1);
        Assert.Equal(1, Health(sim, own));
    }

    [Fact(DisplayName = "турель здания стоит выше стены и бьёт поверх неё — в отличие от пехоты за той же стеной")]
    public void BuildingTurretShootsOverTheWall()
    {
        using var sim = Create(Rules);
        sim.SetStock(Place(sim, "turret", 0, 0, 1), "ammo", 100);
        var enemyWall = Place(sim, "wall", 2, 0, 2);
        var foe = Spawn(sim, "builder", 2, 4, 0);
        // Та же стена, но стрелок — пехотинец: его пули в стену и попадут.
        var wall = Place(sim, "wall", 2, 5, 2);
        var gun = Spawn(sim, "infantry", 1, 0, 5);
        var behind = Spawn(sim, "builder", 2, 4, 5);
        sim.Send(1, new AttackOrder { Units = [gun], Target = behind });

        sim.Seconds(2);
        Assert.True(Health(sim, foe) < 1);
        Assert.Equal(1, Health(sim, enemyWall));
        Assert.Equal(1, Health(sim, behind));
        Assert.True(Health(sim, wall) < 1);
    }

    [Fact(DisplayName = "чужая стена летающих не укрывает: зенитка бьёт дрон за ней, дрон — пехоту за ней")]
    public void WallDoesNotCoverFlyers()
    {
        using var sim = Create(Rules);
        Spawn(sim, "flak", 1, 0, 0);
        var wall = Place(sim, "wall", 3, 0, 2);
        var drone = Spawn(sim, "drone", 2, 6, 0);
        sim.SetStance("hold", drone);
        sim.Seconds(2);
        Assert.True((sim.Alive(drone) ? Health(sim, drone) : 0) < 1);
        Assert.Equal(1, Health(sim, wall));

        using var other = Create(Rules);
        var gun = Spawn(other, "drone", 1, 0, 0);
        var otherWall = Place(other, "wall", 2, 0, 2);
        var foe = Spawn(other, "builder", 2, 4, 0);
        other.Send(1, new AttackOrder { Units = [gun], Target = foe });
        other.Seconds(2);
        Assert.True(Health(other, foe) < 1);
        Assert.Equal(1, Health(other, otherWall));
    }
}
