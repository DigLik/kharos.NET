using Base.Host.Kernel;
using Base.Shared;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Tactics;

/// <summary>
/// Поля схемы, нужные тактике сверх <see cref="S"/>: Tactics, цель и приказ стрелка, дела грузовика. Берутся привязкой
/// системы (<see cref="Load"/> — до первого тика и приказа).
/// </summary>
internal static class T
{
    public static FieldRef Stance, Leg, Away, HomeX, HomeY, Resupply, Patrol;
    public static FieldRef ArmedTarget, ArmedChase, ArmedOrdered;
    public static FieldRef HaulerMine, HaulerPickup, HaulerSupply, HaulerRoute, HaulerServe;

    public static void Load()
    {
        Stance = S.Tactics[Names.Tactics.Stance];
        Leg = S.Tactics[Names.Tactics.Leg];
        Away = S.Tactics[Names.Tactics.Away];
        HomeX = S.Tactics[Names.Tactics.HomeX];
        HomeY = S.Tactics[Names.Tactics.HomeY];
        Resupply = S.Tactics[Names.Tactics.Resupply];
        Patrol = S.Tactics[Names.Tactics.Patrol];
        ArmedTarget = S.Armed[Names.Armed.Target];
        ArmedChase = S.Armed[Names.Armed.Chase];
        ArmedOrdered = S.Armed[Names.Armed.Ordered];
        HaulerMine = S.Hauler[Names.Hauler.Mine];
        HaulerPickup = S.Hauler[Names.Hauler.Pickup];
        HaulerSupply = S.Hauler[Names.Hauler.Supply];
        HaulerRoute = S.Hauler[Names.Hauler.Route];
        HaulerServe = S.Hauler[Names.Hauler.Serve];
    }
}
