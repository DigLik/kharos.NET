using Kharos.Engine.Layers;
using Kharos.Engine.Terrain;
using Kharos.Engine.World;

namespace Kharos.Engine.Recall;

/// <summary>
/// Память игроков партии с туманом: правки земли и покрытие (<c>engine.recallLand</c>, <c>engine.recallPaving</c>).
/// Живёт в сохранении хоста отдельным разделом (<see cref="Encode"/>): исходные тайлы правленой земли и то, что знает
/// каждый игрок. Нет земли или слоя покрытия — этой памяти нет (null).
/// </summary>
public sealed class PlayerMemory
{
    private const byte Version = 1;
    private readonly Land? _land;

    public PlayerMemory(Land? land, LayerSet layers)
    {
        _land = land;
        if (land is not null)
        {
            Land = new LandMemory(land);
            land.TrackEdits = true;
        }
        Paving = PavingMemory.Create(layers);
    }

    public LandMemory? Land { get; }
    public PavingMemory? Paving { get; }

    /// <summary>Правки земли из журнала — в память: исходный тайл запоминается, тайл ждёт сверки.</summary>
    public void Intake()
    {
        if (_land is null || Land is null)
            return;
        foreach (var edit in _land.TakeEdits())
            Land.Note(edit);
    }

    /// <summary>Система <c>engine.recallLand</c>: после обзора игроки узнают правки земли, которые видят.</summary>
    public void UpdateLand(IRecallVision vision, GameWorld world, ulong tick)
    {
        Intake();
        Land?.Update(vision, world, tick);
    }

    /// <summary>Система <c>engine.recallPaving</c>: после обзора игроки узнают покрытие, которое видят.</summary>
    public void UpdatePaving(IRecallVision vision, GameWorld world, ulong tick)
    {
        if (Paving is null)
            return;
        Paving.Intake();
        Paving.Update(vision, world, tick);
    }

    /// <summary>
    /// Карта, какой её знают все до правок: сохранённая карта с изменёнными тайлами в исходном виде, сжатая как
    /// <see cref="Terrain.Land.Pack"/>. Правок земли не было — null (клиент повторит карту генератором).
    /// </summary>
    public byte[]? PristineLand()
    {
        if (_land is null || Land is null)
            return null;
        Intake();
        if (Land.Original.Count == 0)
            return null;
        var bytes = _land.Save();
        foreach (var (tile, value) in Land.Original)
            Terrain.Land.PatchSavedTile(bytes, tile.X, tile.Y, LandMemory.Unpack(value));
        return Terrain.Land.PackBytes(bytes);
    }

    /// <summary>Что игрок знает о правках земли иначе, чем исходная карта: поверх <see cref="PristineLand"/> при подключении.</summary>
    public List<LandTile> KnownLand(uint player)
    {
        var edits = new List<LandTile>();
        if (Land is null)
            return edits;
        foreach (var tile in Land.KnownTiles(player))
        {
            var value = Land.KnownValue(player, tile);
            if (Land.Original.TryGetValue(tile, out var original) && original == value)
                continue;
            edits.Add(new LandTile(tile.X, tile.Y, LandMemory.Unpack(value)));
        }
        return edits;
    }

    /// <summary>Что игрок узнал о земле с прошлого раза, по игрокам.</summary>
    public Dictionary<uint, List<LandTile>> TakeLearnedLand()
    {
        var result = new Dictionary<uint, List<LandTile>>();
        if (Land is null)
            return result;
        foreach (var (player, tiles) in Land.TakeLearned())
            result[player] = [.. tiles.Select(tile => new LandTile(tile.X, tile.Y, Land.KnownTile(player, tile)))];
        return result;
    }

    /// <summary>Что игрок узнал о покрытии с прошлого раза: чанки слоя, как их знает игрок, по игрокам.</summary>
    public Dictionary<uint, List<LayerChange>> TakeLearnedPaving()
    {
        var result = new Dictionary<uint, List<LayerChange>>();
        if (Paving is null)
            return result;
        foreach (var (player, tiles) in Paving.TakeLearned())
            result[player] = Paving.Chunks(player, tiles);
        return result;
    }

    /// <summary>Покрытие, как его знает игрок: все чанки со знанием — при подключении.</summary>
    public List<LayerChange> KnownPaving(uint player) => Paving?.Chunks(player, Paving.KnownTiles(player)) ?? [];

    /// <summary>Игрок начинает заново: что он знал, забыто.</summary>
    public void ForgetPlayer(uint player)
    {
        Land?.ForgetPlayer(player);
        Paving?.ForgetPlayer(player);
    }

    // Сохранение.

    /// <summary>Память байтами для раздела сохранения: исходные тайлы земли, знание игроков о земле и о покрытии.</summary>
    public byte[] Encode()
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Version);
            w.Write((byte)((Land is not null ? 1 : 0) | (Paving is not null ? 2 : 0)));
            Intake();
            Land?.WriteOriginal(w);
            Land?.WriteKnown(w);
            Paving?.WriteKnown(w);
        }
        return stream.ToArray();
    }

    /// <summary>Память из <see cref="Encode"/>; неверное или не той партии — <see cref="FormatException"/>.</summary>
    public void Decode(ReadOnlySpan<byte> bytes)
    {
        using var stream = new MemoryStream(bytes.ToArray());
        using var r = new BinaryReader(stream);
        try
        {
            if (r.ReadByte() != Version)
                throw new FormatException("память игроков: версия");
            var parts = r.ReadByte();
            if (parts != ((Land is not null ? 1 : 0) | (Paving is not null ? 2 : 0)))
                throw new FormatException("память игроков: набор слоёв другой партии");
            Land?.ReadOriginal(r);
            Land?.ReadKnown(r);
            Paving?.ReadKnown(r);
            if (stream.Position != stream.Length)
                throw new FormatException("память игроков: лишние байты");
        }
        catch (EndOfStreamException)
        {
            throw new FormatException("память игроков обрывается");
        }
    }
}
