using Base.Host.Kernel;

namespace Base.Host.Sections.Logistics;

/// <summary>
/// Ручной приказ грузовикам (carrying.ts): перевезти Amount груза со склада From на склад To — «взять здесь, отвезти
/// туда», рейсами, на всех поровну; потом снова работают сами. Возят по воле игрока: берут всё, кроме товара открытой
/// продажи, и кладут в любой его склад, где груз помещается. From 0 — груз уже в кузове. Приказ кончается сам: всё
/// перевезено, источник опустел и кузов пуст, получателю некуда класть или склад пропал; чужой груз в кузове — сразу.
/// </summary>
internal static class Carrying
{
    private const double Epsilon = Cargo.Epsilon;

    /// <summary>Годится ли склад отдать по приказу: своё здание или стройка со складом, или дроп (isCarrySource).</summary>
    public static bool IsSource(View view, uint player, uint entity) =>
        view.HasStock(entity) && (view.IsDrop(entity) || (view.IsOwn(player, entity) && !view.IsUnit(entity)));

    /// <summary>Годится ли склад принять груз по приказу: своё здание, стройка или юнит со складом, не грузовик (isCarryTarget).</summary>
    public static bool IsTarget(View view, uint player, uint entity, int good)
    {
        if (!view.HasStock(entity) || !view.IsOwn(player, entity) || view.Call.Has(entity, S.Hauler))
            return false;
        // Стройке — только её материалы: прочее ей некуда деть.
        if (view.SiteSpec(entity) is { } site && !site.Materials.ContainsKey(Cargo.Name(good)))
            return false;
        return view.Room(entity, good) > Epsilon;
    }

    /// <summary>Приказ грузовикам перевезти amount груза из from в to (setCarry); прежние приказы снимаются. false — не годится.</summary>
    public static bool SetCarry(Pass pass, uint player, IEnumerable<uint> units, uint from, uint to, string resource, double amount)
    {
        var view = pass.V;
        var good = Cargo.Index(resource);
        if (good < 0 || !double.IsFinite(amount) || amount <= 0 || from == to)
            return false;
        if (from != 0 && !IsSource(view, player, from))
            return false;
        if (!IsTarget(view, player, to, good))
            return false;
        var trucks = Hauling.Own(pass, player, units);
        if (trucks.Count == 0)
            return false;
        var share = amount / trucks.Count;
        foreach (var truck in trucks)
        {
            truck.Release();
            var carried = view.Amount(truck.Entity, good);
            truck.Order = new CarryJob { From = from, To = to, Resource = good, Left = from == 0 ? Math.Min(share, carried) : share, Full = from == 0 };
            pass.Call.Remove(truck.Entity, S.Path);
            truck.Save(pass.Call);
        }
        return true;
    }

    /// <summary>Раз в тик, после маршрутов (runCarry): грузовики с ручным приказом возят по нему.</summary>
    public static void Run(Pass pass)
    {
        var view = pass.V;
        var seeking = new List<(Truck Truck, uint Building)>();
        foreach (var truck in pass.Trucks)
        {
            if (truck.Order is not { } order || truck.Moving || truck.Converting)
                continue;
            void End()
            {
                truck.Order = null;
                truck.Waiting = false;
                truck.Loading = false;
            }
            var (to, from) = (order.To, order.From);
            // Чужой груз в кузове приказ не возит.
            if (!view.Alive(to) || !view.HasStock(to) || view.Carried(truck.Entity, order.Resource) >= 0)
            {
                End();
                continue;
            }
            var carried = view.Amount(truck.Entity, order.Resource);
            if (!order.Full)
            {
                var want = Math.Min(order.Left, carried + view.Room(truck.Entity, order.Resource)) - carried;
                var spare = from != 0 && view.Alive(from) && view.HasStock(from) ? Demand.Spare(view, from, order.Resource) : 0;
                if (want > Epsilon && spare > Epsilon)
                {
                    if (!Beams.Reaches(view, from, truck.Entity) && !Beams.Reaches(view, truck.Entity, from))
                    {
                        if (Hauling.SeekDue(pass.Tick, truck))
                            seeking.Add((truck, from));
                        truck.Waiting = true;
                        continue;
                    }
                    truck.Waiting = false;
                    var loaded = Beams.Transfer(pass, from, truck.Entity, order.Resource, Math.Min(want, spare));
                    truck.Loading = loaded > Epsilon;
                    if (loaded > Epsilon)
                        continue;
                }
                // Набрал, сколько нужно и влезает, или брать больше нечего: везёт, что есть; пустой — приказ кончен.
                if (carried <= Epsilon)
                {
                    End();
                    continue;
                }
                order.Full = true;
            }
            if (carried <= Epsilon)
            {
                if (order.Left <= Epsilon || order.From == 0)
                    End();
                else
                    order.Full = false;
                continue;
            }
            if (!Beams.Reaches(view, truck.Entity, to) && !Beams.Reaches(view, to, truck.Entity))
            {
                if (Hauling.SeekDue(pass.Tick, truck))
                    seeking.Add((truck, to));
                truck.Waiting = true;
                continue;
            }
            truck.Waiting = false;
            var moved = Beams.Transfer(pass, truck.Entity, to, order.Resource, Math.Min(carried, order.Left));
            truck.Loading = moved > Epsilon;
            order.Left -= moved;
            // Получателю некуда класть — приказ кончен: остаток грузовик отвезёт в хранилище сам.
            if (moved <= Epsilon)
                End();
        }
        foreach (var (truck, building) in seeking)
            Hauling.SeekBeam(pass, truck, building, Beams.BeamFor(view, truck.Entity, building) is var beam and not 0 ? beam : Beams.BeamFor(view, building, truck.Entity));
    }
}
