using System.Text;
using Kharos.Content;
using Kharos.Engine.World;

namespace Kharos.Host.Tests;

/// <summary>Kharos.Content: объявления schema/*.json, метки, атрибуты @net, @private, @save и ошибки с модом и файлом.</summary>
public class SchemaContentTests
{
    private static Dictionary<string, byte[]> Files(params (string Name, string Json)[] files) =>
        files.ToDictionary(file => file.Name, file => Encoding.UTF8.GetBytes(file.Json));

    private static ModPackage Mod(string name, string manifest, params (string Name, string Json)[] schema) =>
        new(name, Encoding.UTF8.GetBytes(manifest), new Dictionary<string, byte[]>(), schema: Files(schema));

    private static Schema Declare(params ModPackage[] mods)
    {
        var schema = new Schema();
        GameContent.Load(mods).DeclareComponents(schema);
        return schema;
    }

    [Fact]
    public void SchemaFilesBySectionMergeWithManifestTagsAndAttributes()
    {
        var core = Mod("core", """{ "name": "core", "components": { "Extra": { "x": 0 } } }""",
            ("kernel", """
                // Ядро: комментарии можно.
                { "Unit": {}, "Health": { "value": 1, "hit": -1 }, "Producer": { "queue": null, "progress": 0, "@net": "owner" } }
                """),
            ("combat", """{ "Armed": { "target": 0, "chase": false, "@private": ["target"] }, "Burst": { "radius": 0, "@net": "none", "@save": false } }"""));
        // Другой мод добавляет поле и делает его скрытым.
        var extra = Mod("extra", """{ "name": "extra", "requires": ["core"] }""", ("more", """{ "Health": { "regen": 0, "@private": ["regen"] } }"""));
        var schema = Declare(core, extra);

        // Порядок: schema/*.json по именам файлов, затем mod.json, затем следующий мод.
        Assert.Equal(["Armed", "Burst", "Unit", "Health", "Producer", "Extra"], schema.Components.Skip(Builtin.Count).Select(c => c.Name));
        Assert.True(schema.Get("Unit").Tag);
        Assert.Equal(["value", "hit", "regen"], schema.Get("Health").Fields.Select(f => f.Name));
        Assert.Equal(["regen"], schema.Get("Health").Private);
        Assert.Equal(NetMode.Owner, schema.Get("Producer").Net);
        Assert.Equal(["target"], schema.Get("Armed").Private);
        Assert.Equal((NetMode.None, false), (schema.Get("Burst").Net, schema.Get("Burst").Save));
        Assert.Equal((NetMode.All, true), (schema.Get("Health").Net, schema.Get("Health").Save));
    }

    [Fact]
    public void TagFromManifestIsDeclared()
    {
        var schema = Declare(Mod("core", """{ "name": "core", "components": { "Drop": {} } }"""));
        Assert.True(schema.Get("Drop").Tag);
    }

    [Fact]
    public void ErrorsNameModAndFile()
    {
        string Error(params (string, string)[] schema) =>
            Assert.Throws<ContentException>(() => Declare(Mod("core", """{ "name": "core" }""", schema))).Message;
        Assert.Contains("core/schema/combat.json", Error(("combat", """{ "Armed": { "@net": "friends" } }""")));
        Assert.Contains("@save", Error(("combat", """{ "Armed": { "@save": 1 } }""")));
        Assert.Contains("@hidden", Error(("combat", """{ "Armed": { "@hidden": [] } }""")));
        Assert.Contains("core/schema/combat.json", Error(("combat", """{ "Armed": [] }""")));
        Assert.Contains("core/schema/combat.json", Error(("combat", """{ "Armed": { "x": 0 """)));
        // Скрытое поле, которого нет ни у кого, и поле с разными видами в двух файлах.
        var missing = Error(("combat", """{ "Armed": { "target": 0, "@private": ["aim"] } }"""));
        Assert.Contains("core/schema/combat.json", missing);
        Assert.Contains("aim", missing);
        Assert.Contains("core/schema/b.json", Error(("a", """{ "Armed": { "target": 0 } }"""), ("b", """{ "Armed": { "target": "x" } }""")));
    }

    [Fact]
    public void SchemaFilesAreHostOnlyAndOutOfChecksum()
    {
        var plain = GameContent.Load([Mod("core", """{ "name": "core" }""")]);
        var declared = GameContent.Load([Mod("core", """{ "name": "core" }""", ("kernel", """{ "Unit": {}, "Health": { "value": 1 } }"""))]);
        // Клиент получает схему в приветствии, а пакет мода — без schema/: сумма у него та же, что у хоста.
        Assert.Equal(plain.Checksum, declared.Checksum);
        Assert.Single(declared.Mods.Single().Schema);
    }

    [Fact]
    public void ClientRebuildsSchemaWithTagsFromWelcome()
    {
        var schema = Declare(Mod("core", """{ "name": "core" }""", ("kernel", """{ "Unit": {}, "Health": { "value": 1 }, "Drop": {} }""")));
        var welcome = new Kharos.Protocol.Welcome { Player = 1, Game = "g", Components = Kharos.Protocol.Welcome.Describe(schema) };
        var rebuilt = Kharos.Protocol.Welcome.Decode(welcome.Encode()).BuildSchema();
        Assert.Equal(schema.Components.Select(c => (c.Name, c.Fields.Count)), rebuilt.Components.Select(c => (c.Name, c.Fields.Count)));
        Assert.True(rebuilt.Get("Drop").Tag);
    }

    [Fact]
    public void ReadsSchemaFolder()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "kharos-schema-" + Guid.NewGuid().ToString("N")[..8]));
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "mod.json"), """{ "name": "core" }""");
            Directory.CreateDirectory(Path.Combine(dir.FullName, "schema"));
            File.WriteAllText(Path.Combine(dir.FullName, "schema", "mining.json"), """{ "Deposit": { "mined": 0, "spent": false }, "Drop": {} }""");
            var package = ModPackage.Read(dir.FullName);
            Assert.Equal(["mining"], package.Schema.Keys);
            Assert.Equal(["Deposit", "Drop"], package.Declarations.Select(d => d.Component));
            Assert.Equal("schema/mining.json", package.Declarations[0].File);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
