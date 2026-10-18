using System.Text;
using Kharos.Content;
using Kharos.Engine.World;

namespace Kharos.Host.Tests;

/// <summary>Kharos.Content: порядок модов, патчи данных, виды, компоненты, партии, сумма.</summary>
public class ContentTests
{
    private static ModPackage Mod(string name, string manifest, Dictionary<string, string>? data = null, byte[]? host = null, byte[]? client = null) =>
        new(name, Encoding.UTF8.GetBytes(manifest), (data ?? []).ToDictionary(pair => pair.Key, pair => Encoding.UTF8.GetBytes(pair.Value)), host, client);

    private static Dictionary<string, IReadOnlyList<string>> Requires(params (string Name, string[] Needs)[] mods) =>
        mods.ToDictionary(mod => mod.Name, mod => (IReadOnlyList<string>)mod.Needs);

    [Fact]
    public void DependenciesGoFirstTiesByName()
    {
        Assert.Equal(["base", "beta", "zeta", "alpha"], ModPackage.Order(Requires(("zeta", ["base"]), ("alpha", ["zeta"]), ("base", []), ("beta", ["base", "?missing"]))));
        Assert.Contains("по кругу", Assert.Throws<ContentException>(() => ModPackage.Order(Requires(("a", ["b"]), ("b", ["a"])))).Message);
        Assert.Contains("nope", Assert.Throws<ContentException>(() => ModPackage.Order(Requires(("a", ["nope"])))).Message);
    }

    [Fact]
    public void LaterModPatchesDataByKind()
    {
        var core = Mod("core", """{ "name": "core", "version": "1.0" }""", new()
        {
            ["units"] = """{ "buggy": { "speed": 4, "turn": 3, "cost": { "metal": 10, "kharite": 2 } }, "tank": { "speed": 2 } }""",
            ["rules"] = """{ "credits": 1000 }""",
        });
        var extra = Mod("extra", """{ "name": "extra", "requires": ["core"] }""", new()
        {
            ["units"] = """{ "buggy": { "speed": 5, "cost": { "kharite": null } }, "tank": null, "jeep": { "speed": 6 } }""",
        });

        // Порядок пакетов на входе не важен: extra зависит от core.
        var content = GameContent.Load([extra, core]);

        Assert.Equal(["core", "extra"], content.Mods.Select(mod => mod.Name));
        var units = content.Data("units")!.Value;
        Assert.Equal(5, units.GetProperty("buggy").GetProperty("speed").GetDouble());
        Assert.Equal(3, units.GetProperty("buggy").GetProperty("turn").GetDouble());
        Assert.Equal(10, units.GetProperty("buggy").GetProperty("cost").GetProperty("metal").GetDouble());
        Assert.False(units.GetProperty("buggy").GetProperty("cost").TryGetProperty("kharite", out _));
        Assert.False(units.TryGetProperty("tank", out _));
        Assert.Equal(6, units.GetProperty("jeep").GetProperty("speed").GetDouble());
        // Виды — записи-объекты по разделам и именам; числа в rules — не виды.
        Assert.Equal(["units/buggy", "units/jeep"], content.Protos.Select(proto => proto.Path));
        Assert.Equal(1, content.FindProto("units/jeep")!.Id);
        Assert.Null(content.FindProto("units/tank"));
    }

    [Fact]
    public void ComponentsFromAllModsMergeIntoSchema()
    {
        var core = Mod("core", """{ "name": "core", "components": { "Drive": { "speed": 0, "move": "" }, "Unit": { "radius": 0.5 } } }""");
        var extra = Mod("extra", """{ "name": "extra", "requires": ["core"], "components": { "Drive": { "moving": false, "queue": [] } } }""");
        var schema = new Schema();
        GameContent.Load([core, extra]).DeclareComponents(schema);

        var drive = schema.Get("Drive");
        Assert.Equal(["speed", "move", "moving", "queue"], drive.Fields.Select(f => f.Name));
        Assert.Equal([FieldKind.Number, FieldKind.String, FieldKind.Flag, FieldKind.Bytes], drive.Fields.Select(f => f.Kind));
        Assert.Equal(0.5, schema.Get("Unit").Fields[0].Default.Number);

        var clash = Mod("clash", """{ "name": "clash", "requires": ["core"], "components": { "Drive": { "speed": "fast" } } }""");
        Assert.Contains("clash", Assert.Throws<ContentException>(() => GameContent.Load([core, clash]).DeclareComponents(new Schema())).Message);
    }

    [Fact]
    public void ChecksumCoversDataComponentsAndClientCode()
    {
        GameContent Load(string speed = "4", byte[]? host = null, byte[]? client = null) => GameContent.Load(
        [
            Mod("core", """{ "name": "core", "version": "1", "components": { "Unit": { "radius": 0.5 } } }""", new() { ["units"] = $$"""{ "buggy": { "speed": {{speed}} } }""" }, host, client),
        ]);

        var reference = Load().Checksum;
        Assert.Equal(32, reference.Length);
        Assert.Equal(reference, Load().Checksum);
        Assert.NotEqual(reference, Load(speed: "5").Checksum);
        Assert.NotEqual(reference, Load(client: [1, 2, 3]).Checksum);
        // Код хоста клиенту не нужен: его разница суммы не меняет.
        Assert.Equal(reference, Load(host: [1, 2, 3]).Checksum);
    }

    [Fact]
    public void BadManifestsAreRejected()
    {
        Assert.Contains("незнакомое поле", Assert.Throws<ContentException>(() => Mod("x", """{ "name": "x", "sistems": [] }""")).Message);
        Assert.Throws<ContentException>(() => Mod("x", """{ "name": "x", "requires": "base" }"""));
        Assert.Throws<ContentException>(() => Mod("x", "не json"));
        Assert.Contains("два мода", Assert.Throws<ContentException>(() => GameContent.Load([Mod("a", "{}"), Mod("b", """{ "name": "a" }""")])).Message);
    }

    [Fact]
    public void HelloFolderDescribesItsGame()
    {
        var content = HelloGame.Content();
        var hello = Assert.Single(content.Mods);
        Assert.Equal(("hello", "0.2.0"), (hello.Name, hello.Version));
        Assert.Equal(HelloGame.Built, hello.HostWasm is not null);
        Assert.Equal("hello", content.DefaultGame);

        var game = content.Game();
        Assert.Equal(("hello", 256, true, "dunes"), (game.Name, game.Size, game.Fog, game.LandName));
        Assert.Equal((-256, -256, 256, 256), game.Bounds);
        Assert.Equal(7, game.Land!.Seed);
        Assert.Equal(56, game.Land.ZoneScale); // по умолчанию
        Assert.Throws<ContentException>(() => content.Game("nope"));
        Assert.Contains(content.Protos, proto => proto.Path == "games/hello");
    }

    [Fact]
    public void LayersComeFromData()
    {
        var content = GameContent.Load([Mod("core", """{ "name": "core" }""", new()
        {
            ["layers"] = """{ "paved": { "scope": "world", "shape": "grid", "channels": ["u16"], "save": true } }""",
        })]);
        var layer = Assert.Single(content.Layers);
        Assert.Equal(("paved", true), (layer.Name, layer.Save));
    }
}
