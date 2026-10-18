using Base.Shared.Orders;
using Kharos.Content;
using Kharos.Engine.World;
using MemoryPack;
using KHost = Kharos.Host.Host;
using KHostOptions = Kharos.Host.HostOptions;

namespace Kharos.Base.Tests;

/// <summary>Мод base на хосте: пути, содержимое, хост, приказы и чтение мира. Без собранного host.wasm тесты с ним пропускаются.</summary>
public static class BaseGame
{
    public const double Dt = 0.05;

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

    public static string ModDir => Path.Combine(DotnetRoot, "mods", "base");

    public static bool Built => File.Exists(Path.Combine(ModDir, "host.wasm"));

    public const string NotBuilt = "mods/base/host.wasm не собран: pwsh dotnet/tools/build-mods.ps1 -Mod base";

    public static GameContent Content() => GameContent.Load([ModPackage.Read(ModDir)]);

    /// <summary>Хост партии skirmish, ещё не начатой (пропуск теста, если мод не собран).</summary>
    public static KHost Create(ulong seed = 1, GameContent? content = null)
    {
        Assert.SkipUnless(Built, NotBuilt);
        return new KHost(content ?? Content(), new KHostOptions { Modules = new Kharos.Plugins.Game.GameModuleOptions { CallTimeoutMs = 1000 }, Seed = seed });
    }

    /// <summary>Хост с начатой партией и вошедшими игроками.</summary>
    public static KHost Started(params uint[] players)
    {
        var host = Create();
        host.Start();
        foreach (var player in players)
            host.Join(player);
        return host;
    }

    // ===== Чтение мира =====

    public static int Id(KHost host, string component) => host.World.Schema.Id(component);

    public static double Num(KHost host, uint entity, string component, string field)
    {
        var id = Id(host, component);
        return host.World.Number(entity, id, host.World.Schema[id].FieldIndex(field));
    }

    public static string Str(KHost host, uint entity, string component, string field) =>
        host.World.Get(entity, Id(host, component), field).Text ?? "";

    public static byte[] Bytes(KHost host, uint entity, string component, string field)
    {
        var id = Id(host, component);
        return host.World.GetBytes(entity, id, host.World.Schema[id].FieldIndex(field)).ToArray();
    }

    public static void SetNum(KHost host, uint entity, string component, string field, double value) =>
        host.World.Set(entity, Id(host, component), field, FieldValue.Of(value));

    public static void SetPosition(KHost host, uint entity, double x, double y)
    {
        host.World.SetNumber(entity, Builtin.Position, 0, x);
        host.World.SetNumber(entity, Builtin.Position, 1, y);
    }

    /// <summary>Сущности с компонентами, по возрастанию номеров.</summary>
    public static List<uint> With(KHost host, params string[] components)
    {
        var list = new List<uint>();
        host.World.Query([.. components.Select(c => Id(host, c))], list);
        list.Sort();
        return list;
    }

    /// <summary>Сущности с компонентом, чьё числовое поле равно value.</summary>
    public static List<uint> With(KHost host, string component, string field, double value) =>
        [.. With(host, component).Where(e => Num(host, e, component, field) == value)];

    /// <summary>Юниты (Unit и Mover) игрока или всех.</summary>
    public static List<uint> Units(KHost host, uint? player = null) =>
        [.. With(host, "Unit", "Mover").Where(e => player is null || Num(host, e, "Owner", "player") == player)];

    /// <summary>Турели: сущности с Mount (место на носителе).</summary>
    public static List<uint> Turrets(KHost host) => With(host, "Mount");

    /// <summary>Производитель игрока: в начале партии это MCV (свёрнутая база), потом — главное здание и прочие.</summary>
    public static uint Factory(KHost host, uint player) =>
        With(host, "Producer").First(e => Num(host, e, "Owner", "player") == player);

    /// <summary>Правка редактора (edit.*): приказ с правами редактора и администратора, как у /editor.</summary>
    public static void Edit<T>(KHost host, string name, T order) where T : class =>
        host.Send(1, name, MemoryPackSerializer.Serialize(order), Kharos.Engine.World.OrderRights.Editor | Kharos.Engine.World.OrderRights.Admin);

    public static (double X, double Y) At(KHost host, uint entity) =>
        (host.World.Number(entity, Builtin.Position, 0), host.World.Number(entity, Builtin.Position, 1));

    public static double Angle(KHost host, uint entity) => host.World.Number(entity, Builtin.Facing, 0);

    /// <summary>Кошелёк игрока: сущность с Player.id == player.</summary>
    public static uint Wallet(KHost host, uint player) => With(host, "Player", "id", player).Single();

    public static double Credits(KHost host, uint player) => Num(host, Wallet(host, player), "Player", "credits");

    public static double Distance((double X, double Y) a, (double X, double Y) b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // ===== Приказы =====

    public static void Move(KHost host, uint player, double x, double y, params uint[] units) =>
        host.Send(player, MoveOrder.Name, MemoryPackSerializer.Serialize(new MoveOrder { Units = units, X = (int)Math.Floor(x), Y = (int)Math.Floor(y) }));

    public static void Stop(KHost host, uint player, params uint[] units) =>
        host.Send(player, StopOrder.Name, MemoryPackSerializer.Serialize(new StopOrder { Units = units }));

    public static void Produce(KHost host, uint player, uint building, string unit = "buggy") =>
        host.Send(player, ProduceOrder.Name, MemoryPackSerializer.Serialize(new ProduceOrder { Producer = building, Unit = unit }));

    public static void Cancel(KHost host, uint player, uint building) =>
        host.Send(player, CancelProductionOrder.Name, MemoryPackSerializer.Serialize(new CancelProductionOrder { Producer = building }));

    public static void Rally(KHost host, uint player, uint building, double x, double y) =>
        host.Send(player, RallyOrder.Name, MemoryPackSerializer.Serialize(new RallyOrder { Building = building, X = (int)Math.Floor(x), Y = (int)Math.Floor(y) }));

    // ===== Ход =====

    /// <summary>Тик; сигналы клиентам этого тика — в список.</summary>
    public static void Step(KHost host, List<(string Name, byte[] Payload)>? signals = null)
    {
        host.Step();
        var delta = host.TakeDelta();
        if (signals is not null)
            foreach (var signal in delta.Signals)
                signals.Add((signal.Name, signal.Payload));
    }

    public static void Run(KHost host, int ticks, List<(string Name, byte[] Payload)>? signals = null)
    {
        for (var tick = 0; tick < ticks; tick++)
            Step(host, signals);
    }

    /// <summary>Тиков, за которые накопится <paramref name="seconds"/> сложением dt: так считает система производства.</summary>
    public static int TicksFor(double seconds)
    {
        double progress = 0;
        var ticks = 0;
        do
        {
            progress += Dt;
            ticks++;
        }
        while (progress < seconds);
        return ticks;
    }
}
