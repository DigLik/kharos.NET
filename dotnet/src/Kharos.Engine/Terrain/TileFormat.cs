namespace Kharos.Engine.Terrain;

/// <summary>Тип местности тайла. Номера — те же, что в TS и в шейдере земли.</summary>
public enum TerrainType : byte
{
    /// <summary>Болото: проходимо, но сильно замедляет наземных.</summary>
    Swamp = 0,
    /// <summary>Пустыня.</summary>
    Sand = 1,
    /// <summary>Скальное плато: только здесь строят основные здания.</summary>
    Rock = 2,
    /// <summary>Горы: непроходимые вершины посреди плато, одиночные или по две-три.</summary>
    Mountain = 3,
}

/// <summary>Биом — большая область со своим соотношением зон, палитрой и деталями. Номера — как в шейдере.</summary>
public enum Biome : byte
{
    /// <summary>Эрг: пустыня с полями барханов.</summary>
    Erg = 0,
    /// <summary>Солончаки: мало болот и скал, вместо болот — рассол.</summary>
    SaltFlats = 1,
    /// <summary>Красные пустоши: много скал и гор, почти нет болот.</summary>
    RedWastes = 2,
    /// <summary>Топи: много болот, мало скал.</summary>
    Marsh = 3,
}

/// <summary>Барханы на песке (два бита рельефа с <see cref="TileFormat.DuneShift"/>).</summary>
public enum Dunes : byte
{
    /// <summary>Как решит шейдер по шуму и глубине песков.</summary>
    Natural = 0,
    None = 1,
    Many = 2,
}

/// <summary>
/// Раскладка тайла — та же, что в TS и в шейдере. Четыре байта: тип, биом (2 бита основной, 2 бита соседний, 4 бита
/// доля соседнего), глубина в песках 0..255, рельеф. У гор третий байт — радиус вершины, четвёртый — смещение до её
/// центра. Рельеф остальных: 2 младших бита — ярус, <see cref="CliffBit"/>, <see cref="StepBit"/>, <see cref="FootBit"/>,
/// барханы с <see cref="DuneShift"/>.
/// </summary>
public static class TileFormat
{
    public const int ChunkSize = 32;
    /// <summary>log₂ <see cref="ChunkSize"/>: номер чанка тайла — сдвиг вправо (он же floor деления).</summary>
    public const int ChunkShift = 5;
    public const int TileBytes = 4;
    public const int ChunkBytes = ChunkSize * ChunkSize * TileBytes;
    /// <summary>Ярусов плато над песками.</summary>
    public const int MaxTier = 3;
    /// <summary>Кромка тайла к нижним соседям обрывистая, а не пологая.</summary>
    public const byte CliffBit = 4;
    /// <summary>Рядом перепад ярусов (выводится из соседей, не хранится).</summary>
    public const byte StepBit = 8;
    /// <summary>Подножие обрыва: технике нельзя, пехота лезет медленно (выводится, не хранится).</summary>
    public const byte FootBit = 16;
    public const int DuneShift = 5;
    /// <summary>Что в байте рельефа выводится из соседей и не хранится.</summary>
    public const byte DerivedBits = StepBit | FootBit;
    /// <summary>Наибольший радиус вершины у генератора: шейдер ищет вершины только в соседних тайлах.</summary>
    public const double PeakRadiusLimit = 2;
    /// <summary>Радиус хранится в байте как доля от этого значения.</summary>
    public const double PeakRadiusScale = 4;
    /// <summary>Наибольший радиус вершины, при котором её тайлы укладываются в смещение до 4 тайлов.</summary>
    public const double MaxPeakRadius = 2.75;

    public static bool IsBuildable(TerrainType terrain) => terrain == TerrainType.Rock;

    /// <summary>Наземный юнит пройдёт всюду, кроме гор.</summary>
    public static bool IsPassable(TerrainType terrain) => terrain != TerrainType.Mountain;
}

/// <summary>Вершина горы: центр в тайлах и радиус.</summary>
public readonly record struct Peak(double X, double Y, double Radius);

/// <summary>Каким становится тайл: тип (кроме гор), биом в упаковке генератора, ярус, обрывистость кромки, барханы.</summary>
public readonly record struct TileEdit(TerrainType Terrain, byte Biome, int Tier, bool Cliff, Dunes Dunes = Dunes.Natural);

/// <summary>Правка тайла в журнале <see cref="Land.TakeEdits"/>: каким тайл был и каким стал (хранимые байты).</summary>
public readonly record struct TileChange(int X, int Y, StoredTile Before, StoredTile After);

/// <summary>Тайл карты хранимыми байтами: правка, как её знает игрок и как она идёт клиентам.</summary>
public readonly record struct LandTile(int X, int Y, StoredTile Tile);
