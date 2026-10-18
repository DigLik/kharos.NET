using System.Buffers.Binary;
using System.Text;
using Kharos.Mod.Sdk;

namespace Base.Host;

/// <summary>Номера видов (Proto.id) по «раздел/имя»: спрашиваются у движка один раз и помнятся до конца экземпляра.</summary>
internal static class Protos
{
    private static readonly Dictionary<string, int> Ids = new(StringComparer.Ordinal);

    public static int Id(string path)
    {
        if (!Ids.TryGetValue(path, out var id))
            Ids[path] = id = Tools.ProtoId(path);
        return id;
    }

    /// <summary>«Раздел/имя» вида сущности (по Proto.id); у сущности нет вида — null.</summary>
    public static string? PathOf(Parts parts, uint entity) =>
        Tools.Get(entity, parts.Proto) is { } proto ? Tools.ProtoName((int)proto.Number(parts.Proto["id"])) : null;
}

/// <summary>Параметры нового юнита: место, угол, хозяин, ходовая (цель и едет ли).</summary>
internal readonly record struct UnitSpawn(double X, double Y, double Angle, uint Player, double Tx = 0, double Ty = 0, bool Moving = false);

/// <summary>
/// Из чего сделана сущность вида (поле <c>entity</c> видов пилота). Данные берутся из вида, поэтому правки других
/// модов доходят до новых сущностей; поля вызывающего (<c>ctx:spawn(вид, поля)</c>) — параметры.
/// </summary>
internal static class Entities
{
    /// <summary>Новая сущность вида с компонентом Proto.</summary>
    private static uint Begin(Parts p, CommandWriter commands, string path)
    {
        var entity = commands.Spawn();
        commands.Add(entity, p.Proto).Number(p.Proto["id"], Protos.Id(path));
        return entity;
    }

    /// <summary>units/buggy: машина на колёсах с корпусом и обзором. Турель ей ставит mounts по data.mounts.</summary>
    public static uint Buggy(Parts p, CommandWriter commands, UnitKind kind, UnitSpawn at)
    {
        var entity = Begin(p, commands, "units/" + kind.Name);
        commands.Add(entity, p.Position).Number(p.Position["x"], at.X).Number(p.Position["y"], at.Y);
        commands.Add(entity, p.Facing).Number(p.Facing["angle"], at.Angle);
        commands.Add(entity, p.Owner).Number(p.Owner["player"], at.Player);
        commands.Add(entity, p.Drive)
            .Text(p.Drive["move"], "wheels")
            .Number(p.Drive["speed"], kind.Speed)
            .Number(p.Drive["turn"], kind.Turn)
            .Number(p.Drive["tx"], at.Tx)
            .Number(p.Drive["ty"], at.Ty)
            .Flag(p.Drive["moving"], at.Moving);
        commands.Add(entity, p.Unit).Number(p.Unit["radius"], kind.Radius);
        commands.Add(entity, p.Sight).Number(p.Sight["radius"], kind.Sight);
        return entity;
    }

    /// <summary>Юнит вида kind: у Kharos все наземные юниты сделаны как багги.</summary>
    public static uint Unit(Parts p, CommandWriter commands, UnitKind kind, UnitSpawn at) => Buggy(p, commands, kind, at);

    /// <summary>buildings/factory: завод с производством, размером и обзором.</summary>
    public static uint Factory(Parts p, CommandWriter commands, BuildingKind kind, double x, double y, uint player)
    {
        var entity = Begin(p, commands, "buildings/" + kind.Name);
        commands.Add(entity, p.Position).Number(p.Position["x"], x).Number(p.Position["y"], y);
        commands.Add(entity, p.Owner).Number(p.Owner["player"], player);
        commands.Add(entity, p.Producer);
        commands.Add(entity, p.Building).Number(p.Building["width"], kind.Width).Number(p.Building["height"], kind.Height);
        commands.Add(entity, p.Sight).Number(p.Sight["radius"], kind.Sight);
        return entity;
    }

    /// <summary>turrets/gunner: турель, закреплённая на носителе (mechanics/mounts).</summary>
    public static uint Turret(Parts p, CommandWriter commands, string turret, double x, double y, double angle, double player, uint parent, Mount mount)
    {
        var entity = Begin(p, commands, "turrets/" + turret);
        commands.Add(entity, p.Position).Number(p.Position["x"], x).Number(p.Position["y"], y);
        commands.Add(entity, p.Facing).Number(p.Facing["angle"], angle);
        commands.Add(entity, p.Owner).Number(p.Owner["player"], player);
        commands.Add(entity, p.Turret);
        commands.Add(entity, p.Attached)
            .Number(p.Attached["parent"], parent)
            .Number(p.Attached["along"], mount.Along)
            .Number(p.Attached["across"], mount.Across);
        return entity;
    }

    /// <summary>deposits/*: месторождение с запасом.</summary>
    public static uint Deposit(Parts p, CommandWriter commands, string kind, double x, double y, double reserve)
    {
        var entity = Begin(p, commands, "deposits/" + kind);
        commands.Add(entity, p.Position).Number(p.Position["x"], x).Number(p.Position["y"], y);
        commands.Add(entity, p.Deposit).Number(p.Deposit["reserve"], reserve).Number(p.Deposit["total"], reserve);
        return entity;
    }
}

/// <summary>Очередь заказов здания (Producer.queue — байты): имена видов построчно, UTF-8.</summary>
internal static class ProducerQueue
{
    public static List<string> Read(ReadOnlySpan<byte> bytes) =>
        bytes.IsEmpty ? [] : [.. Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries)];

    public static byte[] Write(List<string> queue) => Encoding.UTF8.GetBytes(string.Join('\n', queue));
}

/// <summary>Разбор payload события движка spawned: сущность и номер вида (-1 — без вида).</summary>
internal static class SpawnedEvent
{
    public static (uint Entity, int Proto) Read(ReadOnlySpan<byte> payload) =>
        payload.Length >= 12
            ? (BinaryPrimitives.ReadUInt32LittleEndian(payload), (int)BinaryPrimitives.ReadDoubleLittleEndian(payload[4..]))
            : (0, -1);
}
