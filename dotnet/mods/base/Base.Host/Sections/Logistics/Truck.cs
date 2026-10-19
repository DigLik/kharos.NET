using Base.Host.Kernel;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Logistics;

/// <summary>Ручной приказ грузовика (CarryOrder в carrying.ts, компонент Carry): from 0 — груз уже в кузове.</summary>
internal sealed class CarryJob
{
    public uint From, To;
    public int Resource = -1;
    public double Left;
    public bool Full;

    public CarryJob Copy() => (CarryJob)MemberwiseClone();

    public bool Same(CarryJob other) => From == other.From && To == other.To && Resource == other.Resource && Left == other.Left && Full == other.Full;
}

/// <summary>
/// Грузовик (Hauler в TS) на время вызова: поля читаются раз, правятся в памяти, как объект TS, и пишутся командами
/// в конце (<see cref="Save"/>) — только изменившиеся. Номера сущностей: 0 — нет (NONE в TS); груз — номер в
/// <see cref="Cargo"/> (−1 — нет), в компоненте — номер Proto.
/// </summary>
internal sealed class Truck
{
    public uint Entity, Player;
    public uint Mine, From, To, Pickup, Supply;
    public int Resource = -1;
    public double Amount, Misses, Stop;
    public bool Full, Loading, Waiting, Trapped;
    public int Filter;
    public List<uint> Route = [], Serve = [];
    public CarryJob? Order;

    /// <summary>Едет (есть Path) и превращается (Converting) — на начало вызова.</summary>
    public bool Moving, Converting;

    private Truck? _was;

    /// <summary>Грузовик сущности entity: поля Hauler и Carry; route и serve — готовые байты, если уже прочитаны пачкой.</summary>
    public static Truck Read(View view, uint entity, byte[]? route = null, byte[]? serve = null, bool? moving = null, bool? carrying = null)
    {
        var call = view.Call;
        var truck = new Truck
        {
            Entity = entity,
            Player = view.Owner(entity),
            Mine = (uint)call.Num(entity, S.Hauler, L.HaulerMine),
            From = (uint)call.Num(entity, S.Hauler, L.HaulerFrom),
            To = (uint)call.Num(entity, S.Hauler, L.HaulerTo),
            Pickup = (uint)call.Num(entity, S.Hauler, L.HaulerPickup),
            Supply = (uint)call.Num(entity, S.Hauler, L.HaulerSupply),
            Resource = Cargo.Of(call.Num(entity, S.Hauler, L.HaulerResource, -1)),
            Amount = call.Num(entity, S.Hauler, L.HaulerAmount),
            Misses = call.Num(entity, S.Hauler, L.HaulerMisses),
            Stop = call.Num(entity, S.Hauler, L.HaulerStop),
            Full = call.Flag(entity, S.Hauler, L.HaulerFull),
            Loading = call.Flag(entity, S.Hauler, L.HaulerLoading),
            Waiting = call.Flag(entity, S.Hauler, L.HaulerWaiting),
            Trapped = call.Flag(entity, S.Hauler, L.HaulerTrapped),
            Filter = (int)call.Num(entity, S.Hauler, L.HaulerFilter),
            Route = Cargo.Entities(route ?? call.Bytes(entity, S.Hauler, L.HaulerRoute)),
            Serve = Cargo.Entities(serve ?? call.Bytes(entity, S.Hauler, L.HaulerServe)),
            Moving = moving ?? call.Has(entity, S.Path),
            Converting = view.Converting(entity),
        };
        if (carrying ?? call.Has(entity, L.Carry))
            truck.Order = new CarryJob
            {
                From = (uint)call.Num(entity, L.Carry, L.CarryFrom),
                To = (uint)call.Num(entity, L.Carry, L.CarryTo),
                Resource = Cargo.Of(call.Num(entity, L.Carry, L.CarryResource, -1)),
                Left = call.Num(entity, L.Carry, L.CarryLeft),
                Full = call.Flag(entity, L.Carry, L.CarryFull),
            };
        truck._was = truck.Clone();
        return truck;
    }

    private Truck Clone()
    {
        var copy = (Truck)MemberwiseClone();
        copy.Route = [.. Route];
        copy.Serve = [.. Serve];
        copy.Order = Order?.Copy();
        copy._was = null;
        return copy;
    }

    /// <summary>Пишет изменившиеся поля командами вызова (Hauler, Carry).</summary>
    public void Save(Call call)
    {
        var was = _was!;
        CommandWriter? writer = null;
        CommandWriter W() => writer ??= call.Set(Entity, S.Hauler);
        if (Mine != was.Mine) W().Number(L.HaulerMine, Mine);
        if (From != was.From) W().Number(L.HaulerFrom, From);
        if (To != was.To) W().Number(L.HaulerTo, To);
        if (Pickup != was.Pickup) W().Number(L.HaulerPickup, Pickup);
        if (Supply != was.Supply) W().Number(L.HaulerSupply, Supply);
        if (Resource != was.Resource) W().Number(L.HaulerResource, Cargo.Proto(Resource));
        if (Amount != was.Amount) W().Number(L.HaulerAmount, Amount);
        if (Misses != was.Misses) W().Number(L.HaulerMisses, Misses);
        if (Stop != was.Stop) W().Number(L.HaulerStop, Stop);
        if (Full != was.Full) W().Flag(L.HaulerFull, Full);
        if (Loading != was.Loading) W().Flag(L.HaulerLoading, Loading);
        if (Waiting != was.Waiting) W().Flag(L.HaulerWaiting, Waiting);
        if (Trapped != was.Trapped) W().Flag(L.HaulerTrapped, Trapped);
        if (Filter != was.Filter) W().Number(L.HaulerFilter, Filter);
        if (!Route.SequenceEqual(was.Route)) W().Bytes(L.HaulerRoute, Cargo.Pack(Route));
        if (!Serve.SequenceEqual(was.Serve)) W().Bytes(L.HaulerServe, Cargo.Pack(Serve));
        if (Order is null && was.Order is not null)
            call.Commands.Remove(Entity, L.Carry);
        else if (Order is not null && (was.Order is null || !Order.Same(was.Order)))
        {
            var carry = was.Order is null ? call.Add(Entity, L.Carry) : call.Set(Entity, L.Carry);
            carry.Number(L.CarryFrom, Order.From).Number(L.CarryTo, Order.To).Number(L.CarryResource, Cargo.Proto(Order.Resource))
                .Number(L.CarryLeft, Order.Left).Flag(L.CarryFull, Order.Full);
        }
        _was = Clone();
    }

    /// <summary>Бросает работу; груз остаётся в кузове (dropJob).</summary>
    public void DropJob()
    {
        From = To = 0;
        Amount = 0;
        Full = Loading = Waiting = false;
    }

    /// <summary>Снимает с работы, шахты, дропа, назначения, обеспечения, маршрута и ручного приказа (releaseHauler).</summary>
    public void Release()
    {
        Mine = Pickup = Supply = 0;
        Serve = [];
        Route = [];
        Stop = 0;
        Order = null;
        DropJob();
    }

    /// <summary>Возит ли этот груз: фильтр пуст или груз в нём (carries).</summary>
    public bool Carries(int good) => Cargo.Carries(Filter, good);
}

/// <summary>Поручение строителя везти материал (Fetch): что едет, учитывают заявки.</summary>
internal readonly record struct FetchJob(uint Entity, uint From, uint To, int Resource, double Amount, bool Full);
