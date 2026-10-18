using Kharos.Plugins.Game;

namespace Kharos.Plugins.Tests;

/// <summary>Белый список импортов игровых модов — на модулях WAT, без сборки модов.</summary>
public class GameSandboxTests
{
    [Theory]
    [InlineData("env", "host_get_timestamp")]
    [InlineData("wasi_snapshot_preview1", "path_open")]
    [InlineData("wasi_snapshot_preview1", "sock_accept")]
    [InlineData("kh", "clock")]
    public void ForbiddenImportIsRejected(string module, string name)
    {
        var wat = $$"""
            (module
              (import "{{module}}" "{{name}}" (func $f (result i64)))
              (memory (export "memory") 1))
            """;
        using var host = new GameModuleHost();
        var error = Assert.Throws<ModImportException>(() => host.Load("bad", Wasmtime.Module.ConvertText(wat), HelloModule.LoadInfo()));
        Assert.Equal($"{module}.{name}", Assert.Single(error.Forbidden));
    }

    [Fact]
    public void ImportedMemoryIsRejected()
    {
        const string wat = """(module (import "env" "memory" (memory 1)))""";
        using var host = new GameModuleHost();
        Assert.Throws<ModImportException>(() => host.Load("bad", Wasmtime.Module.ConvertText(wat), HelloModule.LoadInfo()));
    }

    [Fact]
    public void GameImportsHaveNoClockOfHost()
    {
        Assert.DoesNotContain(GameModuleHost.AllowedImports, i => i.Contains("timestamp", StringComparison.Ordinal));
        Assert.Contains("kh.random", GameModuleHost.AllowedImports);
        Assert.Contains("kh.log", GameModuleHost.AllowedImports);
    }

    [Fact]
    public void ModuleWithoutAbiIsRejected()
    {
        const string wat = """(module (memory (export "memory") 1))""";
        using var host = new GameModuleHost();
        using var module = host.Load("empty", Wasmtime.Module.ConvertText(wat), HelloModule.LoadInfo());
        Assert.Throws<ModAbiException>(() => module.CreateInstance());
    }
}
