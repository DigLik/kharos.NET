using Kharos.Engine.Layers;
using Kharos.Engine.World;
using Kharos.Protocol;

namespace Kharos.Host;

/// <summary>
/// Сохранение хоста (<see cref="SaveFile"/>, «KHSV»): из него хост продолжает партию тем же миром и с теми же
/// игроками. Разделы:
/// <code>
/// META game:str seed:varint dt:f64 tick:varint random:u64 checksum:blob format:varint [settings:blob — JSON настроек]
/// WRLD WorldSave («KHWD»): сущности, компоненты, строки, память модов (Storage)
/// LAYR LayerCodec: сохраняемые слои мира и игроков (туман «разведано» и слои модов)
/// LAND Land.Pack — только если карта правлена (иначе её повторяет генератор по зерну)
/// RCLL память игроков (Д3): исходные тайлы правленой карты, что игроки знают о карте и о покрытии (PlayerMemory.Encode);
///      только при тумане
/// BMEM память о чужих зданиях (Д4, <see cref="BuildMemory.Save"/>) — если кто-то что-то помнит
/// IMEM память предметов (Д6, ItemMemory.Encode): какими игроки знают запоминаемые предметные слои (месторождения);
///      только при тумане. Нет раздела (прежние сохранения) — игрок знает предметы, место которых разведал
/// MOVE память хода (Д1, Navigation.SaveMotion): места тел на начало прошлого хода — скорость для объезда; если есть
/// PLRS игроки: next:varint count:varint (id:varint token:str name:str)× joined:varint id:varint×
/// </code>
/// Версия сохранения своя — <see cref="SaveFile.Version"/> (в заголовке файла и в format раздела META), от версии
/// протокола (<see cref="Welcome.ProtocolVersion"/>) не зависит: протокол растёт, сохранения читаются. В файлах до Н6
/// (волна 1) в format стояла версия протокола (2, 3) — чтение её не сверяет.
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

    /// <summary>Память игроков (раздел RCLL); null — не было.</summary>
    public byte[]? Recall { get; init; }

    /// <summary>Память о чужих зданиях (раздел BMEM); null — не было.</summary>
    public byte[]? Buildings { get; init; }

    /// <summary>Память предметов (раздел IMEM); null — не было.</summary>
    public byte[]? Items { get; init; }

    /// <summary>Память хода (раздел MOVE); null — не было.</summary>
    public byte[]? Motion { get; init; }
    public required byte[] Players { get; init; }

    /// <summary>Настройки партии (UTF-8 JSON, HostOptions.Settings); пусто — нет.</summary>
    public byte[] Settings { get; init; } = [];

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
            Recall = host.Recall?.Encode(),
            Buildings = host.Buildings.Players.Any() ? host.Buildings.Save(host.World) : null,
            Items = host.ItemMemory?.Encode(),
            Motion = host.Tools.Navigation?.SaveMotion(),
            Players = players.ToArray(),
            Settings = host.Settings ?? [],
        };
    }

    /// <summary>Файл сохранения.</summary>
    public byte[] Encode()
    {
        var meta = new WireWriter(128).Text(Game).Varint(Seed).F64(Dt).Varint(Tick);
        Span<byte> random = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(random, Random);
        meta.Raw(random).Blob(Checksum).Varint(SaveFile.Version);
        if (Settings.Length > 0)
            meta.Blob(Settings);
        var sections = new List<(string, byte[])> { ("META", meta.ToArray()), ("WRLD", World), ("LAYR", Layers) };
        if (Land is { } land)
            sections.Add(("LAND", land));
        if (Recall is { } memory)
            sections.Add(("RCLL", memory));
        if (Buildings is { } buildings)
            sections.Add(("BMEM", buildings));
        if (Items is { } items)
            sections.Add(("IMEM", items));
        if (Motion is { } motion)
            sections.Add(("MOVE", motion));
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
        // format: версия сохранения сверена заголовком файла (SaveFile.Unpack); прежние файлы держали здесь версию протокола.
        r.Varint();
        var settings = r.End ? [] : r.Blob().ToArray();
        if (!(dt > 0 && dt < 10))
            throw new FormatException($"сохранение: длина тика {dt}");
        return new HostSave
        {
            Game = game, Seed = seed, Dt = dt, Tick = tick, Random = random, Checksum = checksum,
            World = Section("WRLD"), Layers = Section("LAYR"), Land = sections.GetValueOrDefault("LAND"), Recall = sections.GetValueOrDefault("RCLL"), Players = Section("PLRS"), Settings = settings,
            Buildings = sections.GetValueOrDefault("BMEM"), Motion = sections.GetValueOrDefault("MOVE"), Items = sections.GetValueOrDefault("IMEM"),
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
