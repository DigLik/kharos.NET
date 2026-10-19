using Base.Shared;
using Base.Shared.Queries;

namespace Kharos.Base.Scenario;

// Оборона (раздел Defense, С5b): магазины юнитов, купола щитов — запросы defense.* и запись состояния куполов.
public sealed partial class Sim
{
    /// <summary>Сколько боеприпасов в магазине юнита (Magazine.ammo).</summary>
    public double Rounds(uint unit) => Num(unit, Names.Magazine.Name, Names.Magazine.Ammo);

    /// <summary>Сколько боеприпасов в полном магазине юнита вида (magazineOf); ноль — стреляет бесплатно.</summary>
    public double MagazineOf(string unit) => Query<double>(DefenseQueries.Magazine, new UnitArgs { Unit = unit });

    /// <summary>Сколько боеприпасов тратит выстрел юнита вида оружием (shotCost).</summary>
    public double ShotCost(string unit, string weapon) => Query<double>(DefenseQueries.ShotCost, new ShotCostArgs { Unit = unit, Weapon = weapon });

    /// <summary>Пуст ли магазин юнита (isEmpty): не хватает на самый дешёвый выстрел.</summary>
    public bool IsEmpty(uint unit) => Query<bool>(DefenseQueries.Empty, new EntityArgs { Entity = unit });

    /// <summary>Купола, которые держатся сейчас (domes).</summary>
    public DomeInfo[] Domes() => Query<DomeList>(DefenseQueries.Domes).Items;

    /// <summary>Доля прочности здания вида на месте (durabilityOf): оборона на песке без фундамента держит меньше.</summary>
    public double Durability(string type, int x, int y) => Query<double>(DefenseQueries.Durability, new BuildArgs { Type = type, X = x, Y = y });

    /// <summary>Запас щита генератора (Shield.charge).</summary>
    public double Charge(uint shield) => Num(shield, Names.Shield.Name, Names.Shield.Charge);

    /// <summary>
    /// Гасит урон куполами (absorb в shields.ts) и записывает в мир, что стало с куполами: запрос мир не меняет, он считает
    /// попадания по порядку и отвечает, сколько урона прошло и каким стал каждый купол. Возвращает прошедший урон по каждому
    /// попаданию.
    /// </summary>
    public double[] Absorb(params AbsorbHit[] hits)
    {
        var result = Query<AbsorbResult>(DefenseQueries.Absorb, new AbsorbArgs { Hits = hits });
        foreach (var dome in result.Domes)
        {
            Set(dome.Entity, "Shield.charge", dome.Charge);
            Set(dome.Entity, "Shield.hit", dome.Hit);
            Set(dome.Entity, "Shield.down", dome.Down);
        }
        return result.Passed;
    }

    /// <summary>Одно попадание (absorb) по цели игрока в (x, y), пришедшее из (fromX, fromY).</summary>
    public double Absorb(uint player, double x, double y, double damage, double fromX, double fromY, bool fromAir = false) =>
        Absorb(new AbsorbHit { Player = player, X = x, Y = y, Damage = damage, FromX = fromX, FromY = fromY, FromAir = fromAir })[0];
}
