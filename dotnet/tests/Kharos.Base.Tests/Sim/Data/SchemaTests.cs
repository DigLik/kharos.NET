using System.Reflection;
using Base.Shared;
using Base.Shared.Data;
using Kharos.Content;
using Kharos.Engine.World;

namespace Kharos.Base.Tests.Data;

/// <summary>Схема base (schema/*.json), имена Base.Shared, партии и ходовые из данных.</summary>
public sealed class SchemaTests
{
    private static readonly Lazy<GameContent> Content = new(BaseGame.Content);

    private static Schema Build()
    {
        var schema = new Schema();
        Content.Value.DeclareComponents(schema);
        return schema;
    }

    [Fact]
    public void BaseDeclaresTsComponentModelAfterEngineBuiltins()
    {
        var schema = Build();
        Assert.Equal(
            ["Position", "Facing", "Owner", "Proto", "Sight", "Footprint", "Body", "Mover", "Path", "Mount", "Team", "Sighting", "Reveal"],
            schema.Components.Take(Builtin.Count).Select(c => c.Name));
        // 50 компонентов мода: 54 компонента TS без Position, Owner, Path (движок), Turret, Attached, Carrier (Mount и
        // Proto), Glare (Reveal), Ghost (клиент), плюс Carry, Limits, Science, Diplomacy.
        Assert.Equal(Builtin.Count + 50, schema.Count);
        Assert.Equal(14, Content.Value.Mods.Single().Schema.Count);
        foreach (var gone in (string[])["Drive", "Turret", "Attached", "Carrier", "Glare", "Ghost", "Inventory"])
            Assert.Null(schema.Find(gone));

        // Метки — без полей.
        foreach (var tag in (string[])["Unit", "Drop", "Off", "Busy"])
            Assert.True(schema.Get(tag).Tag, tag);
        Assert.False(schema.Get("Building").Tag);

        // Что знает только хозяин (PRIVATE в wire.ts) — @net owner; скрытые поля — @private; авария не уходит вовсе.
        string[] owner = ["Stock", "Producer", "Hauler", "Assembly", "Trade", "Tactics", "Builds", "Fetch", "Deploys", "Repair", "Scan", "Accumulator", "Reactor", "Interceptor", "Unreachable", "Magazine", "Wanted"];
        foreach (var name in owner)
            Assert.Equal((name, NetMode.Owner), (name, schema.Get(name).Net));
        Assert.Equal(NetMode.None, schema.Get("Burst").Net);
        Assert.Equal(NetMode.Owner, schema[Builtin.Path].Net);
        Assert.Equal(NetMode.None, schema[Builtin.Sighting].Net);
        Assert.Equal(NetMode.All, schema.Get("Health").Net);
        Assert.Equal(["target"], schema.Get("Armed").Private);
        Assert.Equal(1UL << schema.Get("Armed").FieldIndex("target"), schema.Get("Armed").PrivateMask);
        Assert.Equal(["progress", "ticks", "cost", "ready", "toX", "toY"], schema.Get("Silo").Private);
        Assert.Equal(["toX", "toY", "mark", "hunted"], schema.Get("Missile").Private);
        Assert.All(schema.Components, c => Assert.True(c.Save, c.Name));

        // Поля по видам: число, флаг, строка, байты.
        var hauler = schema.Get("Hauler");
        Assert.Equal((14, 2), (hauler.Columns, hauler.BytesFields));
        Assert.Equal(FieldKind.Flag, hauler.Field("trapped")!.Kind);
        Assert.Equal(FieldKind.String, schema.Get("Tactics").Field("stance")!.Kind);
        Assert.Equal(FieldKind.Bytes, schema.Get("Producer").Field("queue")!.Kind);
        Assert.Equal(18, schema.Get("Stock").Columns);
    }

    [Fact]
    public void NamesMatchSchema()
    {
        var schema = Build();
        var classes = typeof(Names).GetNestedTypes(BindingFlags.Public | BindingFlags.Static).ToDictionary(type => type.Name);
        Assert.Equal(schema.Components.Select(c => c.Name).Order(StringComparer.Ordinal), classes.Keys.Order(StringComparer.Ordinal));
        foreach (var component in schema.Components)
        {
            var constants = classes[component.Name].GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => field.IsLiteral)
                .ToDictionary(field => field.Name, field => (string)field.GetRawConstantValue()!);
            Assert.Equal(component.Name, constants["Name"]);
            constants.Remove("Name");
            Assert.Equal(
                component.Fields.Select(field => $"{component.Name}.{char.ToUpperInvariant(field.Name[0])}{field.Name[1..]}={field.Name}").Order(StringComparer.Ordinal),
                constants.Select(pair => $"{component.Name}.{pair.Key}={pair.Value}").Order(StringComparer.Ordinal));
        }
    }

    [Fact]
    public void GamesAsInTs()
    {
        var content = Content.Value;
        Assert.Equal("skirmish", content.DefaultGame);
        var skirmish = content.Game("skirmish");
        Assert.Equal((-512, -512, 512, 512), skirmish.Bounds);
        Assert.True(skirmish.Fog);
        Assert.Equal("default", skirmish.LandName);
        Assert.Equal(1337, skirmish.Land!.Seed);
        var tables = BaseTables.Load(name => content.DataBytes(name));
        var spec = tables.Games["skirmish"];
        Assert.Equal((true, 1500.0), (spec.Start, spec.Credits));
        Assert.Equal(["mcv", "buggy", "buggy", "infantry", "infantry", "infantry"], spec.Units);
        Assert.Equal(["Building"], spec.Remember);
        Assert.Equal((96.0, 24.0, 8.0), (spec.Spawn.Apart, spec.Spawn.Radius, spec.Spawn.Slots));
        Assert.True(tables.Games["sandbox"].Sandbox);
        Assert.False(tables.Games["test"].Start);
        Assert.Equal((-128, -128, 128, 128), content.Game("test").Bounds);
        Assert.Equal(600, tables.Rules.ArmyLimit);
        Assert.True(tables.Rules.UnitAmmo);
    }

    [Fact]
    public void ContentTurnsRecordsIntoProtosAndLayers()
    {
        var content = Content.Value;
        foreach (var path in (string[])["units/mcv", "buildings/command", "weapons/rifle", "turrets/gunner", "resources/metal", "ores/metalOre", "products/blocks", "research/vehicles", "moves/wheels", "games/skirmish"])
            Assert.NotNull(content.FindProto(path));
        Assert.Equal(["deposits", "explored", "land", "occupancy", "paving", "sight", "traces"], content.Layers.Select(layer => layer.Name).Order(StringComparer.Ordinal));
        var tables = BaseTables.Load(name => content.DataBytes(name));
        Assert.Equal(18, tables.Units.Count);
        Assert.Equal(16, tables.Goods.Count());
        Assert.All(tables.Units, pair => Assert.True(tables.Moves.ContainsKey(pair.Value.Chassis), pair.Key));
        Assert.Equal("petrol", tables.Units["buggy"].Look!.Motor);
        // Чего нет в записи — значение по умолчанию класса, а не null.
        Assert.Empty(tables.Units["infantry"].Mounts);
        Assert.True(tables.Games["test"].Fog);
        Assert.Equal([0, 2.2], tables.Buildings["factory"].Look!.Exit!);
    }

    [Fact]
    public void BaseLoadsIntoHostOnTestGame()
    {
        Assert.SkipUnless(BaseGame.Built, BaseGame.NotBuilt);
        using var host = new Kharos.Host.Host(Content.Value, new Kharos.Host.HostOptions { Game = "test", Seed = 1, Modules = new Kharos.Plugins.Game.GameModuleOptions { CallTimeoutMs = 1000 } });
        host.Start();
        host.Join(1);
        BaseGame.Run(host, 20);
        Assert.Empty(host.Errors);
        Assert.Equal(Builtin.Count + 50 + 1, host.World.Schema.Count);
        Assert.NotNull(host.Layers.Find("deposits"));
        Assert.NotNull(host.Layers.Find("traces"));
        // Стартового набора у партии test нет: у вошедшего — только сущность счёта (ядро Б1), без места на карте.
        Assert.Single(BaseGame.With(host, "Owner"));
        Assert.Empty(BaseGame.With(host, "Owner", "Position"));
    }

    [Fact]
    public void ChassisTerrainIsTerrainSpeedOfDefaultRules()
    {
        // terrainSpeed (units.ts) по DEFAULT_RULES: max(SLOWEST, 1 − замедление), на солончаках болото ×frozenSwamp.
        const double slowest = 0.05, frozen = 0.25;
        var moves = BaseTables.Load(name => Content.Value.DataBytes(name)).Moves;
        void Check(string chassis, double sand, double swamp, double? cliff)
        {
            var terrain = moves[chassis].Terrain;
            Assert.Equal(Math.Max(slowest, 1 - sand), terrain["sand"]);
            Assert.Equal(Math.Max(slowest, 1 - swamp), terrain["swamp"]);
            Assert.Equal(Math.Max(slowest, 1 - swamp * frozen), terrain["brine"]);
            Assert.Equal(Math.Max(slowest, 1 - swamp), terrain["toxic"]);
            Assert.Equal(cliff is { } c ? Math.Max(slowest, 1 - c) : 0, terrain["cliff"]);
            Assert.Equal((1.0, 0.0), (terrain["rock"], terrain["mountain"]));
            Assert.Equal(0.01, moves[chassis].Harm["toxic"]);
        }
        Check("legs", 0, 0.5, 0.7);
        Check("wheels", 0.1, 0.8, null);
        Check("tracks", 0, 0.5, null);
        Assert.Equal((1.5, 1.2), (moves["wheels"].Paved["road"], moves["wheels"].Paved["foundation"]));
        Assert.Equal((1.0, 1.0), (moves["tracks"].Paved["road"], moves["tracks"].Paved["foundation"]));
        Assert.Equal("air", moves["air"].Layer);
        Assert.Empty(moves["air"].Terrain);
    }
}
