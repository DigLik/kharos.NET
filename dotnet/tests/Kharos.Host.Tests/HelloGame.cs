using Hello;
using Kharos.Content;
using Kharos.Engine.World;
using MemoryPack;

namespace Kharos.Host.Tests;

/// <summary>Мод hello на хосте: пути, содержимое, хост и приказы. Без собранного host.wasm тесты с ним пропускаются.</summary>
public static class HelloGame
{
    /// <summary>Папка dotnet/ (где Kharos.slnx); KHAROS_DOTNET_ROOT — явно.</summary>
    public static string DotnetRoot
    {
        get
        {
            if (Environment.GetEnvironmentVariable("KHAROS_DOTNET_ROOT") is { Length: > 0 } root)
                return root;
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "Kharos.slnx")))
                    return dir.FullName;
            throw new DirectoryNotFoundException("не найден Kharos.slnx выше " + AppContext.BaseDirectory);
        }
    }

    public static string ModDir => Path.Combine(DotnetRoot, "mods", "hello");

    public static bool Built => File.Exists(Path.Combine(ModDir, "host.wasm"));

    public const string NotBuilt = "mods/hello/host.wasm не собран: pwsh dotnet/tools/build-mods.ps1";

    /// <summary>Содержимое из одного мода hello.</summary>
    public static GameContent Content() => GameContent.Load([ModPackage.Read(ModDir)]);

    /// <summary>Хост партии hello (пропуск теста, если мод не собран).</summary>
    public static Host Create(ulong seed = 1, Action<string>? log = null)
    {
        Assert.SkipUnless(Built, NotBuilt);
        return new Host(Content(), new HostOptions { Seed = seed, Log = log });
    }

    public static byte[] Spawn(int count, double x, double y, double spread = 1) =>
        MemoryPackSerializer.Serialize(new SpawnOrder { Count = count, X = x, Y = y, Spread = spread });

    public static byte[] Move(double x, double y, params uint[] entities) =>
        MemoryPackSerializer.Serialize(new MoveOrder { Entities = entities, X = x, Y = y });

    /// <summary>Юниты (сущности с Drive) игрока по возрастанию номеров.</summary>
    public static List<uint> Units(GameWorld world, uint? player = null)
    {
        var drive = world.Schema.Id("Drive");
        var units = new List<uint>();
        world.Query([drive, Builtin.Owner], units);
        if (player is { } owner)
            units.RemoveAll(entity => world.Number(entity, Builtin.Owner, 0) != owner);
        units.Sort();
        return units;
    }

    public static (double X, double Y) Position(GameWorld world, uint entity) =>
        (world.Number(entity, Builtin.Position, 0), world.Number(entity, Builtin.Position, 1));
}
