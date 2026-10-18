using Kharos.Mod.Sdk.Client;

namespace Base.Client;

/// <summary>Чем юнит ходит: от этого анимация хода.</summary>
internal enum Gait { Tracks, Wheels, Legs, Air }

/// <summary>Следы юнита на земле (effects/traces): где они лежат (пиксели спрайта вправо от оси) и какой ширины; legs — отпечатки ног.</summary>
internal sealed record TraceSpec(double[] Sides, double Width, bool Legs);

/// <summary>Турель на корпусе: вид из turrets/ и место — вперёд и вправо от центра, в тайлах (data.mounts).</summary>
internal sealed record Mount(string Turret, double Along, double Across);

/// <summary>Юнит в таблице чертежей: чертёж, походка, следы и огни.</summary>
internal sealed record UnitSpec(string Name, UnitDraw.Art Draw, Gait Gait, TraceSpec? Trace, Lamp[] Lamps, Beam Beam);

/// <summary>
/// Облики юнитов и турелей — перенос lib/units.luau: таблицы походки, следов и света по видам и сборка LookSpec.
/// Чертежи — <see cref="UnitDraw"/>.
/// </summary>
internal static class UnitArt
{
    /// <summary>Сторона кадра в пикселях: два тайла.</summary>
    public const int Frame = 32;

    /// <summary>Кадров поворота: соседние отличаются меньше чем на пиксель-два, поворот выглядит непрерывным.</summary>
    public const int Directions = 64;

    /// <summary>Угол портрета: юнит смотрит вправо вверх.</summary>
    private const double PortraitAngle = -Math.PI / 4;

    /// <summary>Тайлов пути на кадр хода: колёса и гусеницы — пиксель за пиксель пути, ноги шагают реже.</summary>
    private static float Step(Gait gait) => gait switch { Gait.Legs => 0.2f, _ => 1 / 16f };

    private static Beam Light(float along, float length, float near, float spread, float level) => new(along, length, near, spread, level);

    private static TraceSpec Trace(double side, double width, bool legs = false) => new([-side, side], width, legs);

    /// <summary>Все юниты по порядку lib/units.luau.</summary>
    public static readonly IReadOnlyList<UnitSpec> Units =
    [
        new("mcv", UnitDraw.Mcv, Gait.Tracks, Trace(8, 5), [new(12, -4, 3), new(12, 4, 3)], Light(14, 72, 7, 0.3f, 1)),
        new("builder", UnitDraw.Builder, Gait.Wheels, Trace(4, 3), [new(6, -2, 2), new(6, 2, 2)], Light(10, 52, 4, 0.3f, 1)),
        new("infantry", UnitDraw.Infantry, Gait.Legs, Trace(1.5, 1.4, true), [new(5, 2.5f, 1.5f)], Light(7, 36, 1.5f, 0.22f, 0.9f)),
        new("truck", UnitDraw.Truck, Gait.Wheels, Trace(4.5, 3), [new(8, -2.5f, 2), new(8, 2.5f, 2)], Light(11, 56, 4.5f, 0.3f, 1)),
        new("rocketeer", UnitDraw.Rocketeer, Gait.Legs, Trace(1.5, 1.4, true), [new(4, 2.5f, 1.5f)], Light(7, 36, 1.5f, 0.22f, 0.9f)),
        new("flamer", UnitDraw.Flamer, Gait.Legs, Trace(1.5, 1.4, true), [new(6, 1.5f, 1.5f)], Light(7, 30, 1.5f, 0.25f, 0.8f)),
        new("buggy", UnitDraw.Buggy, Gait.Wheels, Trace(4.5, 3), [new(7, -2, 2), new(7, 2, 2)], Light(10, 60, 4, 0.28f, 1)),
        new("flak", UnitDraw.Flak, Gait.Wheels, Trace(4.5, 3), [new(7, -2.5f, 2), new(7, 2.5f, 2)], Light(10, 56, 4, 0.3f, 1)),
        new("lancer", UnitDraw.Lancer, Gait.Wheels, Trace(4.5, 3), [new(6, -2.5f, 2), new(6, 2.5f, 2)], Light(10, 52, 4, 0.3f, 1)),
        new("tank", UnitDraw.Tank, Gait.Tracks, Trace(7.5, 4), [new(10, -4, 2.5f), new(10, 4, 2.5f)], Light(15, 60, 6, 0.3f, 1)),
        new("artillery", UnitDraw.Artillery, Gait.Wheels, Trace(6, 3), [new(6, -3, 2), new(6, 3, 2)], Light(9, 52, 4, 0.3f, 1)),
        new("tesla", UnitDraw.Tesla, Gait.Tracks, Trace(7.5, 4), [new(10, -4, 2.5f), new(10, 4, 2.5f)], Light(13, 60, 6, 0.3f, 1)),
        new("carrier", UnitDraw.Carrier, Gait.Wheels, Trace(9, 3.2), [new(12, -4.5f, 2.5f), new(12, 4.5f, 2.5f)], Light(15, 60, 6, 0.3f, 1)),
        new("supplier", UnitDraw.Supplier, Gait.Wheels, Trace(7, 3.4), [new(10, -3.5f, 2.5f), new(10, 3.5f, 2.5f)], Light(13, 60, 5, 0.3f, 1)),
        new("drone", UnitDraw.Drone, Gait.Air, null, [new(5, 0, 1.5f)], Light(7, 40, 2, 0.3f, 0.8f)),
        new("airTruck", UnitDraw.AirTruck, Gait.Air, null, [new(7, 0, 1.5f)], Light(8, 44, 2, 0.3f, 0.8f)),
        new("gunship", UnitDraw.Gunship, Gait.Air, null, [new(10, 0, 2.5f)], Light(12, 64, 4, 0.35f, 1)),
        new("bomber", UnitDraw.Bomber, Gait.Air, null, [new(9, -6, 2), new(9, 6, 2)], Light(13, 56, 4, 0.32f, 0.9f)),
    ];

    /// <summary>Турели по порядку lib/units.luau (mcv несёт ту же пушку, что core).</summary>
    public static readonly IReadOnlyList<(string Name, UnitDraw.Art Draw)> Turrets =
    [
        ("gunner", UnitDraw.GunnerTurret),
        ("arc", UnitDraw.ArcTurret),
        ("cannon", UnitDraw.CannonTurret),
        ("rocket", UnitDraw.RocketTurret),
        ("repair", UnitDraw.RepairTurret),
        ("laser", UnitDraw.LaserTurret),
        ("core", UnitDraw.CoreTurret),
        ("mcv", UnitDraw.CoreTurret),
    ];

    public static UnitSpec Unit(string name) => Units.First(unit => unit.Name == name);

    public static UnitDraw.Art Turret(string name) => Turrets.First(turret => turret.Name == name).Draw;

    /// <summary>Следы юнитов по виду («units/имя»).</summary>
    public static Dictionary<string, TraceSpec> TraceByPath() =>
        Units.Where(unit => unit.Trace is not null).ToDictionary(unit => "units/" + unit.Name, unit => unit.Trace!);

    /// <summary>
    /// Облик юнита вида name: кадр, направления, фазы хода, огни. mounts — турели на корпусе (data.mounts): на портрете
    /// они рисуются на своих местах, как drawMounts в portraits.ts; список берётся при сборке облика, после загрузки данных.
    /// </summary>
    public static LookSpec Look(string name, Func<IReadOnlyList<Mount>>? mounts = null)
    {
        var unit = Unit(name);
        var air = unit.Gait == Gait.Air;
        return new LookSpec((canvas, angle, phase) => unit.Draw(canvas, angle, UnitDraw.MaskTeam, phase))
        {
            Width = Frame,
            Height = Frame,
            Directions = Directions,
            Phases = air ? 1 : UnitDraw.Phases,
            Step = air ? null : Step(unit.Gait),
            Layer = air ? LookLayer.Air : LookLayer.Ground,
            Shadow = air ? (10, 0.25f) : (2, 0.4f),
            Lamps = unit.Lamps,
            Beam = unit.Beam,
            Portrait = new PortraitSpec(Frame, Frame, canvas =>
            {
                unit.Draw(canvas, PortraitAngle, UnitDraw.MaskTeam, 0);
                double cos = Math.Cos(PortraitAngle), sin = Math.Sin(PortraitAngle);
                foreach (var mount in mounts?.Invoke() ?? [])
                {
                    var turret = Turrets.FirstOrDefault(t => t.Name == mount.Turret).Draw;
                    if (turret is null)
                        continue;
                    var x = (cos * mount.Along - sin * mount.Across) * 16;
                    var y = (sin * mount.Along + cos * mount.Across) * 16;
                    canvas.Translate(x, y);
                    turret(canvas, PortraitAngle, UnitDraw.MaskTeam, 0);
                    canvas.Translate(-x, -y);
                }
            }),
        };
    }

    /// <summary>Облик турели: тень — вдвое ближе, чем у носителя.</summary>
    public static LookSpec TurretLook(string name)
    {
        var draw = Turret(name);
        return new LookSpec((canvas, angle, phase) => draw(canvas, angle, UnitDraw.MaskTeam, 0))
        {
            Width = Frame,
            Height = Frame,
            Directions = Directions,
            Shadow = (1, 0.4f),
        };
    }
}
