using System.Buffers.Binary;
using System.Text;
using Kharos.Mod.Sdk;

namespace Base.Host;

/// <summary>Месторождение, которое кладёт генератор: левый верхний тайл, вид, запас.</summary>
internal readonly record struct DepositSpot(int X, int Y, string Kind, double Reserve);

/// <summary>
/// lib/deposits (generate): как месторождения кладутся на карту — перенос packages/sim-ts/src/deposits.ts. В каждой
/// клетке CELL × CELL тайлов не больше одного месторождения SIZE × SIZE на скале, где можно строить; вид — по биому,
/// запас — по виду.
/// </summary>
internal static class DepositMap
{
    /// <summary>Сторона клетки и месторождения, тайлы.</summary>
    public const int Cell = 40, Size = 2;

    /// <summary>В какой доле клеток месторождение есть, если генератор не задал своей.</summary>
    public const double Chance = 0.95;

    private const int Attempts = 6;
    private const int Salt = 17011;

    /// <summary>Виды по порядку DEPOSIT_TYPES: от него зависит, какой вид выпадет по биому.</summary>
    public static readonly string[] Types = ["metal", "silicon", "fuel", "kharite", "lithium", "uranium"];

    /// <summary>Биомы по номерам местности.</summary>
    public static readonly string[] Biomes = ["erg", "saltFlats", "redWastes", "marsh"];

    /// <summary>Случайное в [0, 1) от координат и зерна — hash генератора местности (Math.imul, перемешивание murmur).</summary>
    public static double Hash(int x, int y, int seed)
    {
        unchecked
        {
            var h = x * 0x27d4eb2d ^ y * 0x165667b1 ^ seed;
            h = (h ^ (int)((uint)h >> 15)) * (int)0x85ebca6b;
            h = (h ^ (int)((uint)h >> 13)) * (int)0xc2b2ae35;
            return (uint)(h ^ (int)((uint)h >> 16)) / 4294967296.0;
        }
    }

    /// <summary>Вид месторождения по биому и случайному числу 0..1: доли — biomes видов раздела deposits.</summary>
    private static string KindIn(BaseData data, int biome, double pick)
    {
        var index = (uint)biome < (uint)Biomes.Length ? biome : 0;
        foreach (var kind in Types)
            if (data.Deposits.TryGetValue(kind, out var spec))
            {
                pick -= spec.Biomes[index];
                if (pick < 0)
                    return kind;
            }
        return "metal";
    }

    /// <summary>
    /// Месторождение клетки (cellX, cellY) по генератору (generatedIn и placeIn); null — нет. Местность — инструментом
    /// движка; bounds — карта [left, right) × [top, bottom).
    /// </summary>
    public static DepositSpot? Generate(BaseData data, LandRules land, int size, int cellX, int cellY)
    {
        var seed = (int)land.Seed + Salt;
        if (Hash(cellX, cellY, seed) >= land.DepositChance)
            return null;
        var room = Cell - Size;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var x = cellX * Cell + (int)Math.Floor(Hash(cellX, cellY, seed + 1 + attempt * 2) * room);
            var y = cellY * Cell + (int)Math.Floor(Hash(cellX, cellY, seed + 2 + attempt * 2) * room);
            if (x < -size || y < -size || x + Size > size || y + Size > size)
                continue;
            // Шахта встаёт на месторождение: тайлы те же, что годятся под здание, — скала не у подножия обрыва.
            var fits = true;
            for (var tileY = y; tileY < y + Size && fits; tileY++)
                for (var tileX = x; tileX < x + Size && fits; tileX++)
                    fits = Tools.Terrain(tileX, tileY, out var terrain) && terrain.Terrain == 2 && !terrain.Foot;
            if (!fits)
                continue;
            Tools.Terrain(x, y, out var corner);
            var kind = KindIn(data, corner.Biome, Hash(cellX, cellY, seed + 53));
            if (!data.Deposits.TryGetValue(kind, out var spec))
                continue;
            var reserve = Math.Round((spec.Min + Hash(cellX, cellY, seed + 97) * (spec.Max - spec.Min)) * land.DepositRichness, MidpointRounding.AwayFromZero);
            return new DepositSpot(x, y, kind, reserve);
        }
        return null;
    }
}

/// <summary>
/// mechanics/deposits: месторождения на карте. При старте партии генератор раскладывает их по клеткам, каждое —
/// сущность своего вида из deposits/ с запасом (<c>Deposit</c>). Добыча — дело шахт, когда они появятся.
/// Клетки раскладываются отдельными событиями <c>deposits.cell</c>: первый взгляд на местность запускает её
/// генерацию, а на вызов мода отведено 50 мс, так что одним обработчиком всё поле не пройти.
/// </summary>
internal sealed class Deposits(BaseMod mod)
{
    public const string CellEvent = "deposits.cell";

    /// <summary>start { game }: у партии есть поле и местность — по событию на каждую клетку.</summary>
    public void OnStart(EventContext ctx)
    {
        var name = Encoding.UTF8.GetString(ctx.Payload);
        if (!mod.D.Games.TryGetValue(name, out var game) || game.Size <= 0 || game.Land is not { } landName || !mod.D.Lands.ContainsKey(landName))
            return;
        var size = (int)game.Size;
        var from = (int)Math.Floor((double)-size / DepositMap.Cell);
        var to = (int)Math.Floor((double)(size - 1) / DepositMap.Cell);
        Span<byte> cell = stackalloc byte[8];
        for (var cellY = from; cellY <= to; cellY++)
            for (var cellX = from; cellX <= to; cellX++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(cell, cellX);
                BinaryPrimitives.WriteInt32LittleEndian(cell[4..], cellY);
                ctx.Commands.Emit(CellEvent, cell);
            }
    }

    /// <summary>deposits.cell { cellX:i32, cellY:i32 }: месторождение клетки, если генератор его кладёт.</summary>
    public void OnCell(EventContext ctx)
    {
        if (ctx.Payload.Length < 8 || !mod.D.Games.TryGetValue(mod.Game, out var game) || game.Land is not { } landName || !mod.D.Lands.TryGetValue(landName, out var land))
            return;
        var cellX = BinaryPrimitives.ReadInt32LittleEndian(ctx.Payload);
        var cellY = BinaryPrimitives.ReadInt32LittleEndian(ctx.Payload[4..]);
        if (DepositMap.Generate(mod.D, land, (int)game.Size, cellX, cellY) is { } spot)
            Entities.Deposit(mod.P, ctx.Commands, spot.Kind, spot.X + DepositMap.Size / 2.0, spot.Y + DepositMap.Size / 2.0, spot.Reserve);
    }
}
