using Base.Host.Kernel;
using Base.Host.Sections.Energy;
using Base.Host.Sections.Mining;
using Base.Shared;
using Base.Shared.Rules;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Missiles;

/// <summary>
/// Система scan (radar.ts): разведка радара. Постоянно радар видит недалеко, как обычное здание повыше, а дальше —
/// разведывает вспышками: раз в <see cref="MissileRules.ScanEvery"/> секунд открывает круг в <see cref="MissileRules.ScanSize"/>
/// тайлов где-то в своём радиусе на <see cref="MissileRules.ScanTicks"/> тиков. Вспышка — сущность <c>Reveal</c> хозяина с
/// Position в этой точке (обзор считает движок), её убирает launchMissiles, когда срок вышел. Место берётся там, где ещё
/// не разведано, а когда разведано всё — где придётся. Радиус растёт с уровнем и потребление с ним. При нехватке энергии
/// вспышки реже, а если её меньше <see cref="MissileRules.RadarPower"/> — радар и постоянно видит не дальше обычного здания
/// (Scan.dark: обзор в Sight, как у выключенного, см. <see cref="Energy.Switches"/>); без энергии вовсе и выключенный
/// радар не разведывает. Кадр — все здания: система идёт раз в <see cref="MissileRules.ScanCheckTicks"/> тиков.
/// </summary>
internal sealed class Radar : ISystem
{
    /// <summary>Сколько мест радар пробует, ища неразведанное (ATTEMPTS), и соль случайных (SALT).</summary>
    private const int Attempts = 8, Salt = 40487;

    private Column _proto, _level;

    public static SystemDeclaration Declare(SystemDeclaration declaration) => declaration
        .Query(Names.Building.Name, Names.Owner.Name, Names.Position.Name, Names.Proto.Name).Every(MissileRules.ScanCheckTicks);

    public void Bind(SystemBinding binding)
    {
        Refs.Load(binding.Component);
        _proto = binding.Column(Names.Proto.Name, Names.Proto.Id);
        _level = binding.Column(Names.Building.Name, Names.Building.Level);
    }

    public void Run(SystemContext ctx)
    {
        var protos = ctx.Column(_proto);
        var levels = ctx.Column(_level);
        Call? call = null;
        List<(uint Entity, uint Player, double X, double Y, double Radius)>? due = null;
        for (var row = 0; row < ctx.Count; row++)
        {
            if (Core.ProtoName((int)protos[row], "buildings") is not { } type || Factory.Building(type) is not { Scan: > 0 } spec)
                continue;
            var entity = ctx.Entities[row];
            call ??= Call.Of(ctx);
            var player = call.OwnerOf(entity);
            if (call.Has(entity, S.Site) || call.Has(entity, Refs.R.Off))
                continue;
            if (!call.Has(entity, Refs.R.Scan))
            {
                // Первая вспышка — через полный срок: радары, поставленные разом, всё равно расходятся — места у них разные.
                call.Add(entity, Refs.R.Scan).Number(Refs.R.ScanWait, MissileRules.ScanEvery / Core.Step);
                continue;
            }
            // Энергии не хватает — счёт идёт медленнее.
            var share = Books.ShareOf(call, entity);
            var dark = share < MissileRules.RadarPower;
            if (dark != call.Flag(entity, Refs.R.Scan, Refs.R.ScanDark))
            {
                call.Set(entity, Refs.R.Scan).Flag(Refs.R.ScanDark, dark);
                // Обзор радара без тока — как у обычного здания (buildingSight с Scan.dark в vision.ts).
                call.Set(entity, S.Sight).Number(S.SightRadius, Factory.BuildingSight(type, false, dark));
            }
            var wait = call.Num(entity, Refs.R.Scan, Refs.R.ScanWait) - MissileRules.ScanCheckTicks * share;
            call.Set(entity, Refs.R.Scan).Number(Refs.R.ScanWait, wait);
            if (wait > 0)
                continue;
            var (x, y) = call.At(entity);
            (due ??= []).Add((entity, player, x + spec.Width / 2, y + spec.Height / 2, MissileRules.ScanRadius(spec, levels[row])));
        }
        if (due is null)
            return;
        foreach (var (entity, player, x, y, radius) in due)
            Flash(call!, entity, player, x, y, radius);
    }

    /// <summary>Вспышка радара: место, где ещё не разведано (до <see cref="Attempts"/> проб), и сущность-вспышка для обзора.</summary>
    private static void Flash(Call call, uint entity, uint player, double x, double y, double radius)
    {
        (double X, double Y)? spot = null;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var key = (int)call.Tick * Attempts + attempt;
            var turn = Generator.Hash((int)entity, key, Salt) * Math.PI * 2;
            // Корень — чтобы места ложились по кругу ровно, а не кучей у радара.
            var reach = Math.Sqrt(Generator.Hash((int)entity, key, Salt + 1)) * radius;
            var tx = x + DetMath.Cos(turn) * reach;
            var ty = y + DetMath.Sin(turn) * reach;
            if (!Placement.InBounds((int)Math.Floor(tx), (int)Math.Floor(ty)))
                continue;
            spot = (Math.Floor(tx * 10 + 0.5) / 10, Math.Floor(ty * 10 + 0.5) / 10);
            if (Tools.Sees(player, tx, ty) == 0)
                break;
        }
        call.Set(entity, Refs.R.Scan).Number(Refs.R.ScanWait, MissileRules.ScanEvery / Core.Step);
        if (spot is not { } place)
            return;
        call.Set(entity, Refs.R.Scan).Number(Refs.R.ScanX, place.X).Number(Refs.R.ScanY, place.Y).Number(Refs.R.ScanTick, call.Tick);
        var flash = call.Spawn();
        call.Add(flash, S.Position).Number(S.PositionX, place.X).Number(S.PositionY, place.Y);
        call.Add(flash, S.Owner).Number(S.OwnerPlayer, player);
        call.Add(flash, Refs.R.Reveal).Number(Refs.R.RevealRadius, MissileRules.ScanSize).Number(Refs.R.RevealUntil, call.Tick + MissileRules.ScanTicks);
        call.Add(flash, Refs.R.Sighting).Number(Refs.R.SightingRule, Refs.Allies);
    }
}
