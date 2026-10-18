using Kharos.Engine.Layers;
using Kharos.Engine.World;
using Kharos.Protocol;

namespace Kharos.Host;

/// <summary>
/// Сохранение хоста (<see cref="SaveFile"/>, «KHSV»): из него хост продолжает партию тем же миром и с теми же
/// игроками. Разделы:
/// <code>
/// META game:str seed:varint dt:f64 tick:varint random:u64 checksum:blob protocol:varint
/// WRLD WorldSave («KHWD»): сущности, компоненты, строки, память модов (Storage)
/// LAYR LayerCodec: сохраняемые слои мира и игроков (туман «разведано» и слои модов)
/// LAND Land.Pack — только если карта правлена (иначе её повторяет генератор по зерну)
/// PLRS игроки: next:varint count:varint (id:varint token:str name:str)× joined:varint id:varint×
/// </code>
/// <see cref="Capture"/> — на потоке хоста (копии, быстро), <see cref="Encode"/> (сжатие) — где угодно.
/// </summary>
public sealed class HostSave
{
    public required string Game { get; init; }
    public ulong Seed { get; init; }
    public double Dt { get; init; }
    public ulong Tick { get; init; }
    public ulong Random { get; init; }
    public byte[] Checksum { get; init; } = [];
    public required byte[] World { get; init; }
    public required byte[] Layers { get; init; }
    public byte[]? Land { get; init; }
    public required byte[] Players { get; init; }

    /// <summary>Состояние хоста сейчас (на его потоке).</summary>
    public static HostSave Capture(Host host)
    {
        var blocks = host.Layers.Save(LayerOwner.World);
        foreach (var player in host.Layers.Players())
            blocks.AddRange(host.Layers.Save(LayerOwner.Player(player)));
        var players = new WireWriter(256);
        host.Roster.Write(players);
        players.Varint((ulong)host.Players.Count);
        foreach (var player in host.Players)
            players.Varint(player);
        return new HostSave
        {
            Game = host.Game.Name,
            Seed = host.Seed,
            Dt = host.Dt,
            Tick = host.Tick,
            Random = host.Scheduler.RandomState,
            Checksum = host.Content.Checksum,
            World = WorldSave.Save(host.World),
            Layers = LayerCodec.Encode(blocks),
            Land = host.LandEdited ? host.Land?.Pack() : null,
            Players = players.ToArray(),
        };
    }

    /// <summary>Файл сохранения.</summary>
    public byte[] Encode()
    {
        var meta = new WireWriter(128).Text(Game).Varint(Seed).F64(Dt).Varint(Tick);
        Span<byte> random = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(random, Random);
        meta.Raw(random).Blob(Checksum).Varint(Welcome.ProtocolVersion);
        var sections = new List<(string, byte[])> { ("META", meta.ToArray()), ("WRLD", World), ("LAYR", Layers) };
        if (Land is { } land)
            sections.Add(("LAND", land));
        sections.Add(("PLRS", Players));
        return SaveFile.Pack(sections);
    }

    /// <summary>Сохранение из файла; другая версия — <see cref="SaveVersionException"/>, мусор — <see cref="FormatException"/>.</summary>
    public static HostSave Decode(ReadOnlySpan<byte> file)
    {
        var sections = SaveFile.Unpack(file);
        byte[] Section(string tag) => sections.TryGetValue(tag, out var bytes) ? bytes : throw new FormatException($"в сохранении нет раздела {tag}");
        var r = new WireReader(Section("META"));
        var game = r.Text();
        var seed = r.Varint();
        var dt = r.F64();
        var tick = r.Varint();
        var random = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(r.Take(8));
        var checksum = r.Blob().ToArray();
        r.Varint();
        if (!(dt > 0 && dt < 10))
            throw new FormatException($"сохранение: длина тика {dt}");
        return new HostSave
        {
            Game = game, Seed = seed, Dt = dt, Tick = tick, Random = random, Checksum = checksum,
            World = Section("WRLD"), Layers = Section("LAYR"), Land = sections.GetValueOrDefault("LAND"), Players = Section("PLRS"),
        };
    }

    /// <summary>Игроки из раздела PLRS: реестр и кто уже входил.</summary>
    internal List<uint> ReadPlayers(PlayerRoster roster)
    {
        var r = new WireReader(Players);
        roster.Read(ref r);
        var joined = new List<uint>();
        for (var count = r.Count(); count > 0; count--)
            joined.Add(r.U32() is var player && player > 0 ? player : throw new FormatException("игрок 0 в сохранении"));
        return r.End ? joined : throw new FormatException("лишние байты в разделе игроков");
    }
}
