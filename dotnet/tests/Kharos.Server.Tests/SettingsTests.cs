using System.Text.Json.Nodes;

namespace Kharos.Server.Tests;

/// <summary>settings.json: умолчания, слияние, проверка видов, переменные окружения (apps/server/settings.ts).</summary>
public class SettingsTests
{
    private static string? Env(string name) => null;

    [Fact]
    public void DefaultsHaveSameKeysAsServer()
    {
        var defaults = ServerSettings.Defaults(seed: 1);
        Assert.Equal(
            ["port", "lang", "password", "admin", "save", "size", "fog", "generator", "weather", "rules", "tls", "limits", "sendRate", "mods"],
            defaults.Select(pair => pair.Key));
        var settings = ServerSettings.From(defaults, defaults, Env);
        Assert.Equal(8787, settings.Port);
        Assert.Equal("save.kharos", settings.Save);
        Assert.Equal(new ConnectLimits(8, 30, 5), settings.Limits);
        Assert.Equal(256, settings.SendRate);
        Assert.Equal("off", settings.Tls.Mode);
        Assert.Equal(1UL, settings.Seed);
        Assert.Empty(settings.Mods);
        Assert.Empty(settings.Notes);
    }

    [Fact]
    public void FileOverridesDefaultsDeeply()
    {
        var warnings = new List<string>();
        var merged = ServerSettings.Merge(ServerSettings.Defaults(1), JsonNode.Parse("""
            { "port": 9000, "password": "x", "limits": { "connections": 2 }, "tls": { "mode": "files", "cert": "a.pem", "key": "a.key" },
              "mods": ["m1", "m2"], "rules": { "protection": 0 }, "fog": false }
            """), warnings);
        Assert.Empty(warnings);
        var settings = ServerSettings.From(merged, ServerSettings.Defaults(1), Env);
        Assert.Equal(9000, settings.Port);
        Assert.Equal("x", settings.Password);
        Assert.Equal(new ConnectLimits(2, 30, 5), settings.Limits);
        Assert.Equal(("files", "a.pem", "a.key"), (settings.Tls.Mode, settings.Tls.Cert, settings.Tls.Key));
        Assert.Equal(["m1", "m2"], settings.Mods);
        Assert.Equal(0, (int)merged["rules"]!["protection"]!);
        Assert.False(settings.Fog);
    }

    [Fact]
    public void UnknownKeysAndWrongTypesAreSkippedWithWarnings()
    {
        var warnings = new List<string>();
        var merged = ServerSettings.Merge(ServerSettings.Defaults(1), JsonNode.Parse("""
            { "port": "80", "colour": 1, "limits": { "connections": true, "nope": 1 }, "mods": [1], "tls": 5, "rules": { "armyLimit": null }, "password": 12 }
            """), warnings);
        Assert.Contains("port: ожидалось number", warnings);
        Assert.Contains("colour: неизвестный параметр", warnings);
        Assert.Contains("limits.connections: ожидалось number", warnings);
        Assert.Contains("limits.nope: неизвестный параметр", warnings);
        Assert.Contains("mods: ожидался список строк", warnings);
        Assert.Contains("tls: ожидался объект", warnings);
        Assert.Contains("rules.armyLimit: ожидалось number", warnings);
        Assert.Contains("password: ожидалось string", warnings);
        // Значения не тронуты.
        var settings = ServerSettings.From(merged, ServerSettings.Defaults(1), Env);
        Assert.Equal(8787, settings.Port);
        Assert.Equal(8, settings.Limits.Connections);
        Assert.Equal("off", settings.Tls.Mode);
        Assert.Equal("", settings.Password);
    }

    [Fact]
    public void NonObjectRootIsRejected()
    {
        var warnings = new List<string>();
        var merged = ServerSettings.Merge(ServerSettings.Defaults(1), JsonNode.Parse("[1]"), warnings);
        Assert.Equal(["настройки: ожидался объект"], warnings);
        Assert.Equal(8787, (int)merged["port"]!);
    }

    [Fact]
    public void OutOfRangeNumbersFallBackToDefaults()
    {
        var tree = ServerSettings.Defaults(1);
        tree["port"] = 70000;
        ((JsonObject)tree["limits"]!)["connections"] = -1;
        ((JsonObject)tree["limits"]!)["connectsPerMinute"] = 1.5;
        var log = new List<string>();
        var settings = ServerSettings.From(tree, ServerSettings.Defaults(1), Env, log.Add);
        Assert.Equal(8787, settings.Port);
        Assert.Equal(new ConnectLimits(8, 30, 5), settings.Limits);
        Assert.Equal(3, log.Count);
    }

    [Fact]
    public void EnvironmentOverridesFile()
    {
        var tree = ServerSettings.Defaults(1);
        tree["port"] = 9000;
        tree["save"] = "from-file";
        var settings = ServerSettings.From(tree, ServerSettings.Defaults(1), name => name switch { "PORT" => "9100", "SAVE" => "from-env", _ => null });
        Assert.Equal(9100, settings.Port);
        Assert.Equal("from-env", settings.Save);
        // Нечисловой PORT — как в TS (Number(...) || порт файла): файл остаётся в силе.
        Assert.Equal(9000, ServerSettings.From(tree, ServerSettings.Defaults(1), name => name == "PORT" ? "abc" : null).Port);
    }

    [Fact]
    public void WrongTlsModeIsAnError()
    {
        var tree = ServerSettings.Defaults(1);
        ((JsonObject)tree["tls"]!)["mode"] = "quic";
        Assert.Contains("tls.mode", Assert.Throws<SettingsException>(() => ServerSettings.From(tree, ServerSettings.Defaults(1), Env)).Message);
    }

    [Fact]
    public void ChangedButUnsupportedKeysAreNoted()
    {
        var tree = ServerSettings.Defaults(1);
        tree["size"] = 512;
        ((JsonObject)tree["rules"]!)["protection"] = 0;
        ((JsonObject)tree["generator"]!)["zoneScale"] = 99;
        ((JsonObject)tree["generator"]!)["seed"] = 5;
        var notes = ServerSettings.From(tree, ServerSettings.Defaults(1), Env).Notes;
        Assert.Equal(3, notes.Count);
        Assert.Contains(notes, note => note.StartsWith("size:"));
        Assert.Contains(notes, note => note.StartsWith("rules:"));
        Assert.Contains(notes, note => note.StartsWith("generator:"));
    }

    [Fact]
    public void FirstRunWritesFileWithAllDefaults()
    {
        using var temp = new Env.Temp();
        var path = temp.File("settings.json");
        var log = new List<string>();
        var first = ServerSettings.Load(path, Env, log.Add);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Contains(log, line => line.Contains(path));
        var written = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(ServerSettings.Defaults(1).Select(pair => pair.Key), written.Select(pair => pair.Key));
        // Второй запуск читает записанное, в том числе то же зерно.
        var second = ServerSettings.Load(path, Env, log.Add);
        Assert.Equal(first.Seed, second.Seed);
        Assert.Equal(first.Port, second.Port);
    }

    [Fact]
    public void LoadMergesFileAndWarns()
    {
        using var temp = new Env.Temp();
        var path = temp.File("settings.json");
        File.WriteAllText(path, """{ "port": 9001, "oops": 1, "limits": { "connections": "many" } }""");
        var log = new List<string>();
        var settings = ServerSettings.Load(path, Env, log.Add);
        Assert.Equal(9001, settings.Port);
        Assert.Equal(8, settings.Limits.Connections);
        Assert.Contains(log, line => line.Contains("oops: неизвестный параметр") && line.EndsWith("пропущено"));
        Assert.Contains(log, line => line.Contains("limits.connections: ожидалось number"));
    }

    [Fact]
    public void SettingsEnvVariableChoosesFile()
    {
        using var temp = new Env.Temp();
        var path = temp.File("other.json");
        File.WriteAllText(path, """{ "port": 9002 }""");
        var settings = ServerSettings.Load(temp.File("ignored.json"), name => name == "SETTINGS" ? path : null, _ => { });
        Assert.Equal(9002, settings.Port);
        Assert.False(File.Exists(temp.File("ignored.json")));
    }

    [Fact]
    public void BrokenJsonIsAnError()
    {
        using var temp = new Env.Temp();
        var path = temp.File("settings.json");
        File.WriteAllText(path, "{ port: ");
        Assert.Contains("не JSON", Assert.Throws<SettingsException>(() => ServerSettings.Load(path, Env, _ => { })).Message);
    }
}
