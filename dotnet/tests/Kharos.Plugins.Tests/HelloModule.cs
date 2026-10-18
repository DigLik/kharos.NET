using Kharos.Mod.Abi;

namespace Kharos.Plugins.Tests;

/// <summary>
/// Мод hello для тестов и замера (файл подключён и в tools/Kharos.Plugins.Bench): путь к собранному host.wasm,
/// схема мира как у движка (компоненты Builtin, Drive и Unit из mods/hello/mod.json) и ModLoadInfo.
/// </summary>
public static class HelloModule
{
    /// <summary>Номера компонентов схемы.</summary>
    public const int Position = 0, Facing = 1, Owner = 2, Proto = 3, Sight = 4, Drive = 5, Unit = 6;

    /// <summary>Столбцов в кадре hello.drive: Position(x, y), Facing(angle), Drive(speed, turn, tx, ty).</summary>
    public const int Width = 7;

    /// <summary>Номера столбцов кадра hello.drive.</summary>
    public const int X = 0, Y = 1, Angle = 2, Speed = 3, Turn = 4, Tx = 5, Ty = 6;

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

    /// <summary>mods/hello/host.wasm (или KHAROS_HELLO_WASM).</summary>
    public static string WasmPath => Environment.GetEnvironmentVariable("KHAROS_HELLO_WASM") is { Length: > 0 } path
        ? path
        : Path.Combine(DotnetRoot, "mods", "hello", "host.wasm");

    /// <summary>Собран ли мод.</summary>
    public static bool Built => File.Exists(WasmPath);

    /// <summary>Почему тесты с модом пропущены.</summary>
    public const string NotBuilt = "mods/hello/host.wasm не собран: pwsh dotnet/tools/build-mods.ps1 (нужен WASI SDK — tools/wasi-sdk.ps1)";

    /// <summary>Схема и данные мода, как их отдаст движок.</summary>
    public static ModLoadInfo LoadInfo(ulong seed = 42)
    {
        var data = new List<ModDataFile>();
        var dataDir = Path.Combine(DotnetRoot, "mods", "hello", "data");
        if (Directory.Exists(dataDir))
            foreach (var file in Directory.GetFiles(dataDir, "*.json"))
                data.Add(new ModDataFile { Name = Path.GetFileNameWithoutExtension(file), Bytes = File.ReadAllBytes(file) });
        return new ModLoadInfo
        {
            Abi = 1,
            Side = "host",
            Seed = seed,
            Components =
            [
                Component("Position", Position, "x", "y"),
                Component("Facing", Facing, "angle"),
                Component("Owner", Owner, "player"),
                Component("Proto", Proto, "id"),
                Component("Sight", Sight, "radius"),
                Component("Drive", Drive, "speed", "turn", "tx", "ty"),
                Component("Unit", Unit, "radius"),
            ],
            Data = [.. data],
        };
    }

    /// <summary>Мир на n багги: блок столбцов (ширина 7 × n, как QueryColumns.Data) и номера сущностей с 1.</summary>
    public static (uint[] Entities, double[] Data) Units(int n, int seed = 1)
    {
        var entities = new uint[n];
        var data = new double[Width * n];
        static double Spread(long i, long salt) => (i * 2654435761 + salt) % 500 + 0.5;
        for (var i = 0; i < n; i++)
        {
            entities[i] = (uint)(i + 1);
            data[X * n + i] = Spread(i, 1 + seed);
            data[Y * n + i] = Spread(i, 7 + seed);
            data[Angle * n + i] = 0;
            data[Speed * n + i] = 4;
            data[Turn * n + i] = 3;
            data[Tx * n + i] = Spread(i, 13 + seed);
            data[Ty * n + i] = Spread(i, 29 + seed);
        }
        return (entities, data);
    }

    private static ModComponentInfo Component(string name, int id, params string[] fields) => new()
    {
        Name = name,
        Id = id,
        Fields = [.. fields.Select((field, index) => new ModFieldInfo { Name = field, Kind = 0 /* число */, Index = index, Slot = index })],
    };
}
