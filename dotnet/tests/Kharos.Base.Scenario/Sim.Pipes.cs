using System.Buffers.Binary;
using Base.Shared;
using Base.Shared.Orders;
using Base.Shared.Queries;

namespace Kharos.Base.Scenario;

/// <summary>Пачка в трубах (Batch) глазами теста: груз — имя; путь — левые верхние тайлы узлов.</summary>
public sealed record BatchState(uint Entity, uint From, uint To, string Resource, double Amount, double Sent, double Arrive, bool Push, (int X, int Y)[] Path);

/// <summary>Колодец, с которым свяжется новый (wellPartners): тайл и готов ли.</summary>
public sealed record WellSpot(int X, int Y, bool Ready);

// Трубы (раздел Pipes, С2c): пачки, сети с рёбрами, протяжка, трассы без стройки — запросы pipes.* и правки edit.connect*.
public sealed partial class Sim
{
    /// <summary>Пачки мира по возрастанию номеров (world.query(Batch)).</summary>
    public List<BatchState> Batches() => [.. With(Names.Batch.Name).Select(BatchOf)];

    /// <summary>Поля пачки.</summary>
    public BatchState BatchOf(uint entity)
    {
        double N(string field) => Num(entity, Names.Batch.Name, field);
        var bytes = Bytes(entity, Names.Batch.Name + "." + Names.Batch.Path);
        var path = new (int, int)[bytes.Length / 8];
        for (var i = 0; i < path.Length; i++)
            path[i] = (BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * 8)), BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(i * 8 + 4)));
        return new BatchState(entity, (uint)N(Names.Batch.From), (uint)N(Names.Batch.To), GoodName(N(Names.Batch.Resource)), N(Names.Batch.Amount),
            N(Names.Batch.Sent), N(Names.Batch.Arrive), N(Names.Batch.Push) != 0, path);
    }

    /// <summary>Сеть здания, трубы или подключённой стройки (networkOf); не в сети — пустая.</summary>
    public NetworkInfo Network(uint entity) => Query<NetworkInfo>(PipesQueries.Network, new EntityArgs { Entity = entity });

    /// <summary>Чем сеть узнать (для сравнения networkOf(a) === networkOf(b)): её первое здание; не в сети — 0.</summary>
    public uint NetworkOf(uint entity) => Network(entity).Buildings.FirstOrDefault();

    /// <summary>Есть ли в сети сущности a ребро между a и b: вплотную — стык, поодаль — через трубу.</summary>
    public bool Joined(uint a, uint b)
    {
        var edges = Network(a).Edges;
        for (var i = 0; i + 2 < edges.Length; i += 3)
            if ((edges[i] == a && edges[i + 1] == b) || (edges[i] == b && edges[i + 1] == a))
                return true;
        return false;
    }

    /// <summary>Какие тайлы протяжки можно заложить (pipeStroke).</summary>
    public bool[] PipeStroke(uint player, int[] tiles, string kind = "pipe") =>
        Query<FlagList>(PipesQueries.Stroke, new PipeStrokeArgs { Player = player, Tiles = tiles, Kind = kind }).Items;

    /// <summary>Своя труба другого вида в тайле, вместо которой ляжет новая (pipeSwap); нет — 0.</summary>
    public uint PipeSwap(uint player, string kind, int x, int y) => Query<uint>(PipesQueries.Swap, new PipeTileArgs { Player = player, Kind = kind, X = x, Y = y });

    /// <summary>С какими своими колодцами свяжется колодец в тайле (wellPartners).</summary>
    public List<WellSpot> WellPartners(uint player, int x, int y)
    {
        var list = Query<WellList>(PipesQueries.Partners, new PipeTileArgs { Player = player, X = x, Y = y });
        return [.. list.X.Select((tx, i) => new WellSpot(tx, list.Y[i], list.Ready[i]))];
    }

    /// <summary>
    /// Подключает готовое здание к ближайшей другой своей сети готовой трубой (connectBuilding, правка edit.connect).
    /// Возвращает уложенные трубы.
    /// </summary>
    public List<uint> ConnectBuilding(uint building)
    {
        var before = World.NextEntity;
        Edit(new EditConnectOrder { Building = building });
        return [.. With(Names.Building.Name).Where(entity => entity >= before)];
    }

    /// <summary>
    /// Соединяет трубами все сети игрока с сетью главного здания (connectAll): правка edit.connectAll соединяет за раз одну
    /// сеть — повторяется, пока появляются трубы.
    /// </summary>
    public void ConnectAll(uint player)
    {
        for (var guard = 0; guard < 256; guard++)
        {
            var before = World.NextEntity;
            Edit(new EditConnectAllOrder { Player = player });
            if (World.NextEntity == before)
                return;
        }
    }
}
