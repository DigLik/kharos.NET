using System.Runtime.CompilerServices;
using Kharos.Engine.Numerics;
using static Kharos.Engine.Terrain.TileFormat;

namespace Kharos.Engine.Terrain;

/// <summary>
/// Генератор земли — перенос <c>packages/shared/src/terrain.ts</c> до байта (эталоны — <c>dotnet/tools/golden</c>).
/// Арифметика по правилам JS (<see cref="JsMath"/>), синус, косинус и гипотенуза — из <see cref="DetMath"/>: так земля
/// одна и та же на любой ОС и процессоре и совпадает с TS.
/// </summary>
public static class TerrainGenerator
{
    /// <summary>Мир поделён на клетки такого размера; в каждой не больше одной группы вершин. Делит ChunkSize нацело.</summary>
    private const int PeakCell = 16;
    private const int PeakCellShift = 4;
    private const int CellsPerChunk = ChunkSize / PeakCell;
    /// <summary>Вершины-спутники меньше главной.</summary>
    private const double SatelliteMinRadius = 0.7;
    private const double SatelliteMaxRadius = 1.4;
    /// <summary>Центры вершин группы не ближе этого, иначе сольются в одну.</summary>
    private const double PeakMinSpacing = 1.5;
    /// <summary>Тайл — гора, если вершина задевает его хотя бы краем: половина диагонали (Math.SQRT1_2).</summary>
    internal const double TileReach = 0.7071067811865476;
    private const double Pi = 3.141592653589793;
    private const double TwoPowMinus32 = 2.3283064365386963e-10;

    private const int HashX = 0x27d4eb2d;
    private const int HashY = 0x165667b1;

    /// <summary>Случайное число от 0 до 1, одно и то же для одних координат и зерна (<c>hash</c>, умножение как Math.imul).</summary>
    public static double Hash(int x, int y, int seed) => Mix(unchecked(x * HashX ^ y * HashY ^ seed));

    /// <summary>Вторая половина hash: перемешать и привести к [0, 1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Mix(int h)
    {
        unchecked
        {
            h = (h ^ (h >>> 15)) * (int)0x85ebca6b;
            h = (h ^ (h >>> 13)) * (int)0xc2b2ae35;
            // Деление на 2³² — то же умножение на 2⁻³², бит в бит.
            return (uint)(h ^ (h >>> 16)) * TwoPowMinus32;
        }
    }

    /// <summary>Зона и основной биом тайла без гор и чанков — дёшево, для схемы всей карты (<c>sampleTerrain</c>).</summary>
    public static (TerrainType Terrain, Biome Biome) Sample(GeneratorConfig config, double x, double y)
    {
        if (config.Blank) return (TerrainType.Sand, Biome.Erg);
        var weights = BiomeWeights(x, y, config);
        return (Classify(ElevationAt(x, y, config), ZoneLevels(weights, config)), (Biome)(PackBiome(weights) >> 6));
    }

    // Шум без таблиц: значение в любой точке — из координат и зерна, поэтому у мира нет краёв.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Noise(double x, double y, int seed)
    {
        double floorX = Math.Floor(x);
        double floorY = Math.Floor(y);
        int ix = JsMath.ToInt32(floorX);
        int iy = JsMath.ToInt32(floorY);
        double fx = x - floorX;
        double fy = y - floorY;
        fx = fx * fx * (3 - 2 * fx);
        fy = fy * fy * (3 - 2 * fy);
        // hash в четырёх углах: (ix + 1)·HashX = ix·HashX + HashX по модулю 2³².
        int x0 = unchecked(ix * HashX);
        int x1 = unchecked(x0 + HashX);
        int y0 = unchecked(iy * HashY ^ seed);
        int y1 = unchecked((iy + 1) * HashY ^ seed);
        double a = Mix(x0 ^ y0);
        double b = Mix(x1 ^ y0);
        double c = Mix(x0 ^ y1);
        double d = Mix(x1 ^ y1);
        double top = a + (b - a) * fx;
        return top + (c + (d - c) * fx - top) * fy;
    }

    private static double Fbm(double x, double y, int seed, int octaves)
    {
        double sum = 0;
        double amplitude = 0.5;
        double total = 0;
        for (int octave = 0; octave < octaves; octave++)
        {
            sum += amplitude * Noise(x, y, unchecked(seed + octave * 1013));
            total += amplitude;
            amplitude *= 0.5;
            x *= 2;
            y *= 2;
        }
        return sum / total;
    }

    private static double ElevationAt(double x, double y, GeneratorConfig config)
    {
        double scale = config.ZoneScale;
        // Искажаем координаты, чтобы плато и болота были неправильной формы.
        double warpX = (Fbm(x / scale, y / scale, unchecked(config.Seed + 7001), 2) - 0.5) * config.ZoneWarp;
        double warpY = (Fbm(x / scale, y / scale, unchecked(config.Seed + 9001), 2) - 0.5) * config.ZoneWarp;
        return Fbm((x + warpX) / scale, (y + warpY) / scale, config.Seed, 4);
    }

    private static double Smoothstep(double from, double to, double value)
    {
        double t = Math.Max(0, Math.Min(1, (value - from) / (to - from)));
        return t * t * (3 - 2 * t);
    }

    /// <summary>Доли биомов в точке; в сумме 1, на стыках плавно перетекают.</summary>
    private readonly record struct Weights(double Erg, double Salt, double Red, double Marsh)
    {
        public double this[int biome] => biome switch { 0 => Erg, 1 => Salt, 2 => Red, _ => Marsh };
    }

    private static Weights BiomeWeights(double x, double y, GeneratorConfig config)
    {
        double scale = config.BiomeScale;
        double heat = Fbm(x / scale, y / scale, unchecked(config.Seed + 11003), 2);
        double wetness = Fbm(x / scale, y / scale, unchecked(config.Seed + 13007), 2);
        double marsh = Smoothstep(0.56, 0.64, wetness);
        double red = (1 - marsh) * Smoothstep(0.56, 0.64, heat);
        double salt = (1 - marsh) * Smoothstep(0.44, 0.36, heat);
        return new(1 - marsh - red - salt, salt, red, marsh);
    }

    /// <summary>Уровни болот и скал с поправкой на биом.</summary>
    private static (double Swamp, double Rock) ZoneLevels(Weights w, GeneratorConfig config) =>
        (config.SwampLevel + w.Salt * config.SaltSwampShift + w.Red * config.RedSwampShift + w.Marsh * config.MarshSwampShift,
         config.RockLevel + w.Salt * config.SaltRockShift + w.Red * config.RedRockShift + w.Marsh * config.MarshRockShift);

    private static double PeakChance(Weights w, GeneratorConfig config) =>
        config.PeakChance * (w.Erg + w.Salt * config.SaltPeakFactor + w.Red * config.RedPeakFactor + w.Marsh * config.MarshPeakFactor);

    /// <summary>Байт биома: основной (2 бита), соседний (2 бита) и доля соседнего 0..15 — это 0..50%.</summary>
    private static byte PackBiome(Weights w)
    {
        int primary = 0;
        for (int i = 1; i < 4; i++) if (w[i] > w[primary]) primary = i;
        int secondary = primary == 0 ? 1 : 0;
        for (int i = 0; i < 4; i++) if (i != primary && w[i] > w[secondary]) secondary = i;
        double share = w[secondary] / (w[primary] + w[secondary]);
        return (byte)(primary << 6 | secondary << 4 | JsMath.ToInt32(Math.Min(15, JsMath.Round(share * 30))));
    }

    private static TerrainType Classify(double elevation, (double Swamp, double Rock) levels) =>
        elevation < levels.Swamp ? TerrainType.Swamp : elevation > levels.Rock ? TerrainType.Rock : TerrainType.Sand;

    /// <summary>Ярус: пески и болота — 0, плато поднимается ступенями по мере роста высоты над уровнем скал.</summary>
    private static int TierOf(double elevation, double rock, GeneratorConfig config) =>
        elevation <= rock ? 0 : (int)Math.Min(MaxTier, 1 + Math.Floor((elevation - rock) / config.TierStep));

    /// <summary>Обрывы и пологие въезды чередуются участками длиной около CliffScale.</summary>
    private static bool IsCliff(double x, double y, GeneratorConfig config) =>
        Fbm(x / config.CliffScale, y / config.CliffScale, unchecked(config.Seed + 17011), 2) < config.CliffShare * 0.5 + 0.25;

    /// <summary>Вершина целиком на плато и не подходит к его краю.</summary>
    private static bool FitsOnRock(Peak peak, GeneratorConfig config)
    {
        double reach = peak.Radius + 1.5;
        return FitsAt(peak.X, peak.Y, config) && FitsAt(peak.X + reach, peak.Y, config) && FitsAt(peak.X + -reach, peak.Y, config)
            && FitsAt(peak.X, peak.Y + reach, config) && FitsAt(peak.X, peak.Y + -reach, config);
    }

    private static bool FitsAt(double x, double y, GeneratorConfig config) =>
        ElevationAt(x, y, config) > ZoneLevels(BiomeWeights(x, y, config), config).Rock;

    /// <summary>Вершины клетки: главная в середине, до двух спутников, наполовину утопленных в неё.</summary>
    internal static Peak[] PeaksInCell(int cellX, int cellY, GeneratorConfig config)
    {
        double Random(int salt) => Hash(cellX, cellY, unchecked(config.Seed + 5003 + salt * 131));

        double centerX = (cellX + 0.5) * PeakCell;
        double centerY = (cellY + 0.5) * PeakCell;
        if (Random(0) >= PeakChance(BiomeWeights(centerX, centerY, config), config)) return [];

        double maxRadius = Math.Min(config.PeakMaxRadius, PeakRadiusLimit);
        double minRadius = Math.Min(config.PeakMinRadius, maxRadius);
        // Главная — в середине клетки, чтобы группа с запасом не вылезала в соседнюю.
        var main = new Peak(
            (double)cellX * PeakCell + 5 + JsMath.Round(Random(1) * 12) / 2,
            (double)cellY * PeakCell + 5 + JsMath.Round(Random(2) * 12) / 2,
            minRadius + Random(3) * (maxRadius - minRadius));
        if (!FitsOnRock(main, config)) return [];

        double sizeRoll = Random(4);
        int count = sizeRoll < config.TripleChance ? 3 : sizeRoll < config.TripleChance + config.DoubleChance ? 2 : 1;
        if (count == 1) return [main];
        var peaks = new List<Peak>(count) { main };
        for (int i = 1; i < count; i++)
        {
            double radius = SatelliteMinRadius + Random(i * 10 + 5) * (SatelliteMaxRadius - SatelliteMinRadius);
            double angle = Random(i * 10 + 6) * Pi * 2;
            double distance = (main.Radius + radius) * 0.6;
            var satellite = new Peak(
                JsMath.Round((main.X + DetMath.Cos(angle) * distance) * 2) / 2,
                JsMath.Round((main.Y + DetMath.Sin(angle) * distance) * 2) / 2,
                radius);
            bool crowded = false;
            foreach (var other in peaks) crowded |= DetMath.Hypot(other.X - satellite.X, other.Y - satellite.Y) < PeakMinSpacing;
            if (!crowded && FitsOnRock(satellite, config)) peaks.Add(satellite);
        }
        return [.. peaks];
    }

    /// <summary>
    /// Тип, ярус и обрывистость тайла ровно такими, какими их соберёт <see cref="GenerateChunk"/>, вместе с горами (у
    /// гор ярус −1). peaks — вершины клеток, уже посчитанные в этом пересчёте.
    /// </summary>
    internal static (TerrainType Terrain, int Tier, bool Cliff) GeneratedTile(GeneratorConfig config, int x, int y, Dictionary<long, Peak[]> peaks)
    {
        if (config.Blank) return (TerrainType.Sand, 0, false);
        double worldX = x;
        double worldY = y;
        double elevation = ElevationAt(worldX, worldY, config);
        var levels = ZoneLevels(BiomeWeights(worldX, worldY, config), config);
        var terrain = Classify(elevation, levels);
        int tier = TierOf(elevation, levels.Rock, config);
        int cellX = x >> PeakCellShift;
        int cellY = y >> PeakCellShift;
        long key = (long)cellY << 32 | (uint)cellX;
        if (!peaks.TryGetValue(key, out var cell)) peaks.Add(key, cell = PeaksInCell(cellX, cellY, config));
        foreach (var peak in cell)
            if (DetMath.Hypot(peak.X - (worldX + 0.5), peak.Y - (worldY + 0.5)) < peak.Radius + TileReach) return (TerrainType.Mountain, -1, false);
        return (terrain, tier, tier > 0 && IsCliff(worldX, worldY, config));
    }

    /// <summary>
    /// Чанк по генератору: тип, биом, рельеф и вершины гор. Глубину в песках и выводимые биты досчитывает
    /// <see cref="Land"/>: они зависят от соседей.
    /// </summary>
    internal static byte[] GenerateChunk(GeneratorConfig config, int chunkX, int chunkY)
    {
        var tiles = new byte[ChunkBytes];
        if (config.Blank)
        {
            // Чистая пустыня: песок эрга, ярус 0.
            for (int i = 0; i < tiles.Length; i += TileBytes) tiles[i] = (byte)TerrainType.Sand;
            return tiles;
        }

        var cells = new Peak[CellsPerChunk * CellsPerChunk][];
        for (int cellY = 0; cellY < CellsPerChunk; cellY++)
            for (int cellX = 0; cellX < CellsPerChunk; cellX++)
                cells[cellY * CellsPerChunk + cellX] = PeaksInCell(chunkX * CellsPerChunk + cellX, chunkY * CellsPerChunk + cellY, config);

        for (int y = 0; y < ChunkSize; y++)
        {
            for (int x = 0; x < ChunkSize; x++)
            {
                double worldX = chunkX * ChunkSize + x;
                double worldY = chunkY * ChunkSize + y;
                double elevation = ElevationAt(worldX, worldY, config);
                int index = (y * ChunkSize + x) * TileBytes;
                var weights = BiomeWeights(worldX, worldY, config);
                var levels = ZoneLevels(weights, config);
                tiles[index] = (byte)Classify(elevation, levels);
                tiles[index + 1] = PackBiome(weights);
                int tier = TierOf(elevation, levels.Rock, config);
                tiles[index + 3] = (byte)(tier | (tier != 0 && IsCliff(worldX, worldY, config) ? CliffBit : 0));

                // В тайл пишется ближайшая из задевающих его вершин; остальные шейдер найдёт в соседних тайлах.
                Peak? nearest = null;
                double nearestDistance = double.PositiveInfinity;
                foreach (var peak in cells[(y >> PeakCellShift) * CellsPerChunk + (x >> PeakCellShift)])
                {
                    double distance = DetMath.Hypot(peak.X - (worldX + 0.5), peak.Y - (worldY + 0.5));
                    if (distance < peak.Radius + TileReach && distance < nearestDistance)
                    {
                        nearest = peak;
                        nearestDistance = distance;
                    }
                }
                if (nearest is { } top)
                {
                    tiles[index] = (byte)TerrainType.Mountain;
                    tiles[index + 2] = JsMath.ToUint8(JsMath.Round(top.Radius / PeakRadiusScale * 255));
                    // Смещение до центра вершины в половинках тайла, по 4 бита на ось.
                    double dx = (top.X - (worldX + 0.5)) * 2 + 8;
                    double dy = (top.Y - (worldY + 0.5)) * 2 + 8;
                    tiles[index + 3] = (byte)(JsMath.ToInt32(dx) << 4 | JsMath.ToInt32(dy));
                }
            }
        }
        return tiles;
    }
}
