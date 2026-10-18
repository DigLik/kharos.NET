using System.Text.Json.Nodes;
using Base.Host.Kernel;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Mining;

/// <summary>
/// Генератор месторождений (deposits.ts): в каждой ячейке 40×40 — не больше одного, с шансом depositChance местности. Место —
/// первое из шести пробных, где 2×2 тайла скалы не у подножия обрыва; вид — по биому (доли в deposits.json), запас — от min
/// до max вида, умноженный на depositRichness. Всё считается из сида местности хешем координат, как terrain.ts, поэтому
/// мир один и тот же у всех. В TS клетки брались у генератора лениво; здесь он проходит все ячейки карты один раз, при
/// начале партии (событие <c>start</c>), и дальше слой — данные мира.
/// </summary>
internal static class Generator
{
    /// <summary>Сколько мест в ячейке пробуется, прежде чем решить, что скалы под месторождение в ней нет (ATTEMPTS).</summary>
    private const int Attempts = 6;

    /// <summary>Сдвиг сида, чтобы месторождения не повторяли узор местности (SALT).</summary>
    private const int Salt = 17011;

    /// <summary>Сид, доля клеток с месторождением и богатство местности партии (GeneratorConfig); карта: полусторона и туман.</summary>
    private static int _seed = 1337;
    private static double _chance = 0.95, _richness = 1;
    private static int _half;
    private static bool _fog = true;

    /// <summary>Сид местности партии.</summary>
    public static int Seed => _seed;

    /// <summary>Туман войны в партии.</summary>
    public static bool Fog => _fog;

    /// <summary>Границы карты: [left, right) × [top, bottom) тайлов; карты нет — все нули.</summary>
    public static (int Left, int Top, int Right, int Bottom) Bounds => (-_half, -_half, _half, _half);

    /// <summary>Читает местность и размер из записи партии (с настройками): land — имя из lands.json или объект.</summary>
    public static void Load(LoadContext ctx)
    {
        (_seed, _chance, _richness, _half, _fog) = (1337, 0.95, 1, Core.Game.Size ?? 0, Core.Game.Fog);
        if (ctx.GameData.IsEmpty)
            return;
        JsonNode? game;
        try
        {
            game = JsonNode.Parse(ctx.GameData);
        }
        catch (System.Text.Json.JsonException)
        {
            return;
        }
        if (game is not JsonObject record)
            return;
        // Запись читается напрямую: land-объект настроек партии типизированная запись игры не разберёт.
        if (record["size"] is JsonValue size && size.TryGetValue<int>(out var half))
            _half = half;
        if (record["fog"] is JsonValue fog && fog.TryGetValue<bool>(out var haze))
            _fog = haze;
        var land = record["land"];
        if (land is JsonValue name && name.TryGetValue<string>(out var key) && ctx.TryData("lands", out var bytes)
            && JsonNode.Parse(bytes) is JsonObject lands)
            land = lands[key];
        if (land is not JsonObject config)
            return;
        if (config["seed"] is JsonValue seed && seed.TryGetValue<int>(out var number))
            _seed = number;
        if (config["depositChance"] is JsonValue chance && chance.TryGetValue<double>(out var share))
            _chance = share;
        if (config["depositRichness"] is JsonValue richness && richness.TryGetValue<double>(out var factor))
            _richness = factor;
    }

    // ===== Хеш (hash в terrain.ts) =====

    /// <summary>Случайное число от 0 до 1, одно и то же для одних и тех же координат и сида: целочисленная арифметика JS.</summary>
    public static double Hash(int x, int y, int seed)
    {
        unchecked
        {
            var h = x * 0x27d4eb2d ^ y * 0x165667b1 ^ seed;
            h = (h ^ (int)((uint)h >> 15)) * (int)0x85ebca6bu;
            h = (h ^ (int)((uint)h >> 13)) * (int)0xc2b2ae35u;
            return (uint)(h ^ (int)((uint)h >> 16)) / 4294967296.0;
        }
    }

    // ===== Вид и место =====

    /// <summary>Вид месторождения по биому и случайному числу от 0 до 1 (kindIn): доли видов в биоме идут по порядку видов.</summary>
    private static int KindIn(int biome, double pick)
    {
        var name = biome switch { 1 => "saltFlats", 2 => "redWastes", 3 => "marsh", _ => "erg" };
        for (var kind = 0; kind < Deposits.Kinds.Count; kind++)
        {
            pick -= Core.Tables.Deposits[Deposits.Kinds[kind]].Biomes.GetValueOrDefault(name);
            if (pick < 0)
                return kind;
        }
        return 0;
    }

    /// <summary>
    /// Месторождение в ячейке (cellX, cellY) по случайным числам random(k): место — первое из <see cref="Attempts"/>, где
    /// можно строить и где fits не против; вид — по биому; запас — по виду. Нет такого места — null (placeIn).
    /// </summary>
    public static Spot? Place(int cellX, int cellY, Func<int, double> random, Func<int, int, bool>? fits = null)
    {
        var (left, top, right, bottom) = Bounds;
        var room = Deposits.Cell - Deposits.Size;
        Span<double> xs = stackalloc double[4];
        Span<double> ys = stackalloc double[4];
        Span<TerrainInfo> terrain = stackalloc TerrainInfo[4];
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var x = cellX * Deposits.Cell + (int)Math.Floor(random(1 + attempt * 2) * room);
            var y = cellY * Deposits.Cell + (int)Math.Floor(random(2 + attempt * 2) * room);
            if (x < left || y < top || x + Deposits.Size > right || y + Deposits.Size > bottom)
                continue;
            // Шахта встаёт на месторождение: тайлы те же, что годятся под здание, — скала не у подножия обрыва.
            for (var i = 0; i < 4; i++)
            {
                xs[i] = x + i % Deposits.Size + 0.5;
                ys[i] = y + i / Deposits.Size + 0.5;
            }
            if (Tools.Terrain(xs, ys, terrain))
            {
                var buildable = true;
                foreach (var tile in terrain)
                    buildable &= tile.Terrain == RockTerrain && !tile.Foot;
                if (!buildable)
                    continue;
            }
            if (fits is not null && !fits(x, y))
                continue;
            var biome = terrain[0].Biome;
            var kind = KindIn(biome, random(53));
            var spec = Core.Tables.Deposits[Deposits.Kinds[kind]];
            return new Spot(x, y, kind, Math.Floor((spec.Min + random(97) * (spec.Max - spec.Min)) * _richness + 0.5));
        }
        return null;
    }

    /// <summary>Скала (Terrain.Rock): единственная местность, где можно строить.</summary>
    private const byte RockTerrain = 2;

    // ===== Начало партии =====

    /// <summary>Месторождение, которое генератор кладёт в ячейку (generatedIn): по сиду и шансу клетки.</summary>
    private static Spot? Generated(int cellX, int cellY)
    {
        var seed = _seed + Salt;
        return Hash(cellX, cellY, seed) >= _chance ? null : Place(cellX, cellY, k => Hash(cellX, cellY, seed + k));
    }

    /// <summary>
    /// Событие <c>start</c>: генератор проходит все ячейки карты (fillDeposits) и кладёт месторождения в слой. Слой уже
    /// не пуст — партия продолжена из сохранения или месторождения положил редактор — ничего не делает.
    /// </summary>
    public static void OnStart(EventContext ctx)
    {
        if (S.Deposits is not { } layer || _half <= 0)
            return;
        var call = Call.Of(ctx);
        var (left, top, right, bottom) = Bounds;
        if (Tools.Items(layer, 0, 0, 0, _half * 2.0 + Deposits.Cell).Length > 0)
            return;
        for (var cellY = FloorDiv(top, Deposits.Cell); cellY <= FloorDiv(bottom - 1, Deposits.Cell); cellY++)
            for (var cellX = FloorDiv(left, Deposits.Cell); cellX <= FloorDiv(right - 1, Deposits.Cell); cellX++)
                if (Generated(cellX, cellY) is { } spot)
                    Deposits.Add(call, spot);
    }

    /// <summary>Целочисленное деление вниз (Math.floor(a / b)).</summary>
    public static int FloorDiv(int a, int b) => (int)Math.Floor((double)a / b);
}
