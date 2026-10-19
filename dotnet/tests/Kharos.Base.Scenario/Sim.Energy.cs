using Base.Shared.Orders;
using Base.Shared.Queries;
using Base.Shared.Signals;
using MemoryPack;

namespace Kharos.Base.Scenario;

// Энергия (раздел Energy, С3): хозяйства сетей, доли энергии, выключатель, кто в сети — запросы energy.* и правки.
public sealed partial class Sim
{
    /// <summary>Итог по всем сетям игрока (economyOf).</summary>
    public EconomyInfo Economy(uint player) => Query<EconomyInfo>(EnergyQueries.Economy, new PlayerArgs { Player = player });

    /// <summary>Хозяйства сетей игрока по порядку (zoneEconomies).</summary>
    public EconomyInfo[] ZoneEconomies(uint player) => Query<EconomyList>(EnergyQueries.Zones, new PlayerArgs { Player = player }).Items;

    /// <summary>Доли энергии потребителей (powerSupply): нет потребителя — его нет в словаре.</summary>
    public Dictionary<uint, double> PowerSupply()
    {
        var list = Query<SupplyList>(EnergyQueries.Supply);
        return list.Entities.Zip(list.Shares).ToDictionary(pair => pair.First, pair => pair.Second);
    }

    /// <summary>Здания без энергии (powerStates): «overload» или «starved».</summary>
    public Dictionary<uint, string> PowerStates()
    {
        var list = Query<PowerStateList>(EnergyQueries.States);
        return list.Entities.Zip(list.States).ToDictionary(pair => pair.First, pair => pair.Second);
    }

    /// <summary>Станции под нагрузкой по порядку сетей (loadedPlants).</summary>
    public uint[] LoadedPlants() => Query<EntityList>(EnergyQueries.Loaded).Items;

    /// <summary>Станции сетей, где потребителей больше, чем потянут целые станции (overbuiltPlants).</summary>
    public uint[] OverbuiltPlants() => Query<EntityList>(EnergyQueries.Overbuilt).Items;

    /// <summary>Доходные здания в сетях без главного (coreless).</summary>
    public uint[] Coreless() => Query<EntityList>(EnergyQueries.Coreless).Items;

    /// <summary>Горит ли в реакторе стержень (isBurning).</summary>
    public bool Burning(uint reactor) => Query<bool>(EnergyQueries.Burning, new EntityArgs { Entity = reactor });

    /// <summary>
    /// Включить или выключить здание сразу, без тика (setWorking): приказ work за игрока правкой edit.order. Возвращает,
    /// изменилось ли что-то.
    /// </summary>
    public bool SetWorking(uint player, uint building, bool on)
    {
        TakeSignals(RefusedSignal.Name);
        OrderNow(player, new WorkOrder { Building = building, On = on });
        return !TakeSignals(RefusedSignal.Name).Any(signal => MemoryPackSerializer.Deserialize<RefusedSignal>(signal.Payload)?.Order == EditOrderOrder.Name);
    }

    /// <summary>
    /// Кто в сети (<c>sim.online</c> в TS): игроки из списка — в сети, остальные вошедшие — нет (события player_online и
    /// player_left). Пока не звали, в сети все.
    /// </summary>
    public void SetOnline(params uint[] players)
    {
        foreach (var player in Options.Players.Concat(players).Distinct())
        {
            if (players.Contains(player))
                Host.Online(player);
            else
            {
                // Вышел — только тот, кто был в сети: отметим вход, чтобы выход дошёл.
                Host.Online(player);
                Host.Offline(player);
            }
        }
        Drain();
        Throw();
    }
}
