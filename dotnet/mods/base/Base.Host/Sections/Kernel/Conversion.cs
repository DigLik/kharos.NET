using Base.Host.Kernel;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Kernel;

/// <summary>
/// Превращения MCV (conversion.ts): разворачивание в главное здание — на месте или доехав до места, — и сворачивание
/// обратно. Пока идёт превращение, у сущности Converting, у машины тело вросло (Body.rooted). Очередь производства и
/// прочность переезжают вместе с превращением.
/// </summary>
internal static class Conversion
{
    /// <summary>Главное здание (CORE).</summary>
    public const string Command = "command";

    /// <summary>Сколько секунд MCV разворачивается и сколько здание сворачивается (DEPLOY_SECONDS, PACK_SECONDS).</summary>
    public const double DeploySeconds = 3, PackSeconds = 10;

    /// <summary>Раз во сколько тиков недоехавший MCV пробует снова; с каждым промахом — вдвое реже (DEPLOY_RETRY_TICKS).</summary>
    private const int RetryTicks = 20;

    /// <summary>После скольких промахов подряд MCV бросает поездку (DEPLOY_MISSES).</summary>
    private const int MissesLimit = 5;

    /// <summary>Раз во сколько тиков разворачивающийся MCV просит своих уйти из-под будущего здания (EVICT_TICKS).</summary>
    private const int EvictTicks = 20;

    private static (int W, int H) CoreSize
    {
        get
        {
            var spec = Factory.Building(Command)!;
            return ((int)spec.Width, (int)spec.Height);
        }
    }

    /// <summary>Где встанет здание, если MCV развернётся на месте: левый верхний тайл (siteUnder).</summary>
    public static (int X, int Y) SiteUnder(Call call, uint entity)
    {
        var (x, y) = call.At(entity);
        var (w, h) = CoreSize;
        return ((int)Math.Floor(x) - w / 2, (int)Math.Floor(y) - h / 2);
    }

    /// <summary>Куда MCV послан разворачиваться, а не послан — место под ним (deploySite).</summary>
    public static (int X, int Y) DeploySite(Call call, uint entity) =>
        call.Has(entity, S.Deploys) ? ((int)call.Num(entity, S.Deploys, S.DeploysX), (int)call.Num(entity, S.Deploys, S.DeploysY)) : SiteUnder(call, entity);

    /// <summary>Можно ли послать MCV разворачиваться: это MCV игрока, и он ещё не разворачивается (canSendDeploy).</summary>
    public static bool CanSend(Call call, uint player, uint entity) =>
        call.UnitType(entity) == "mcv" && call.IsOwn(player, entity) && !call.Has(entity, S.Converting);

    /// <summary>Может ли MCV развернуться левым верхним тайлом в (x, y): свободная скала или фундамент, не чужая зона (canDeployAt).</summary>
    public static bool CanDeployAt(Call call, uint player, uint entity, int x, int y)
    {
        if (!CanSend(call, player, entity) || !Placement.CanPlace(call, Command, x, y))
            return false;
        var (w, h) = CoreSize;
        return !Placement.InForeignZone(call, player, x, y, w, h);
    }

    /// <summary>Может ли игрок развернуть MCV прямо на месте (canDeploy).</summary>
    public static bool CanDeploy(Call call, uint player, uint entity)
    {
        if (!CanSend(call, player, entity))
            return false;
        var (x, y) = SiteUnder(call, entity);
        return CanDeployAt(call, player, entity, x, y);
    }

    /// <summary>Стоит ли MCV на основании будущего здания с левым верхним тайлом (x, y).</summary>
    private static bool StandsOn(Call call, uint entity, int x, int y)
    {
        var (px, py) = call.At(entity);
        var (w, h) = CoreSize;
        return px >= x && px < x + w && py >= y && py < y + h;
    }

    /// <summary>Середина основания: туда едет MCV.</summary>
    private static (int X, int Y) Middle(int x, int y)
    {
        var (w, h) = CoreSize;
        return (x + w / 2, y + h / 2);
    }

    /// <summary>
    /// Посылает MCV развернуться левым верхним тайлом в (x, y) (orderDeploy): стоит на месте — сразу, иначе едет. Годится
    /// ли место, решается на месте: в тумане там может стоять чужое, и отказ сразу выдал бы это.
    /// </summary>
    public static bool OrderDeploy(Call call, uint player, uint entity, int x, int y)
    {
        if (!CanSend(call, player, entity))
            return false;
        var (w, h) = CoreSize;
        if (!Placement.InBounds(x, y, w, h))
            return false;
        if (StandsOn(call, entity, x, y))
        {
            if (!CanDeployAt(call, player, entity, x, y))
                return false;
            call.Add(entity, S.Deploys).Number(S.DeploysX, x).Number(S.DeploysY, y).Number(S.DeploysMisses, 0);
            Start(call, entity, DeploySeconds);
            return true;
        }
        call.Add(entity, S.Deploys).Number(S.DeploysX, x).Number(S.DeploysY, y).Number(S.DeploysMisses, 0);
        var (mx, my) = Middle(x, y);
        Moves.Order(call, entity, mx, my);
        return true;
    }

    /// <summary>Начинает превращение на seconds секунд: путь снят, машина врастает (startConverting).</summary>
    public static void Start(Call call, uint entity, double seconds)
    {
        var ticks = Core.Ticks(seconds);
        call.Remove(entity, S.Path);
        call.Add(entity, S.Converting).Number(S.ConvertingLeft, ticks).Number(S.ConvertingTotal, ticks);
        if (call.Has(entity, S.Body))
            call.Set(entity, S.Body).Flag(S.BodyRooted, true);
    }

    /// <summary>Снимает превращение: машина снова ходит.</summary>
    private static void Stop(Call call, uint entity)
    {
        call.Remove(entity, S.Converting);
        if (call.Has(entity, S.Body))
            call.Set(entity, S.Body).Flag(S.BodyRooted, false);
    }

    /// <summary>Может ли игрок свернуть здание: это его главное здание, и оно не сворачивается (canPack).</summary>
    public static bool CanPack(Call call, uint player, uint entity) =>
        call.Has(entity, S.Building) && call.BuildingType(entity) == Command && call.IsOwn(player, entity) && !call.Has(entity, S.Converting);

    /// <summary>Отменяет разворачивание или поездку на него: MCV остаётся машиной и слушается приказов (cancelDeploy).</summary>
    public static bool CancelDeploy(Call call, uint player, uint entity)
    {
        if (!call.Has(entity, S.Unit) || !call.IsOwn(player, entity))
            return false;
        var converting = call.Has(entity, S.Converting);
        if (!converting && !call.Has(entity, S.Deploys))
            return false;
        if (!converting)
            call.Remove(entity, S.Path);
        Stop(call, entity);
        call.Remove(entity, S.Deploys);
        return true;
    }

    /// <summary>Ждёт ли MCV, пока из-под здания уйдут: время вышло, а место занято (isDeployBlocked).</summary>
    public static bool Blocked(Call call, uint entity) =>
        call.Has(entity, S.Converting) && call.Has(entity, S.Unit) && call.Num(entity, S.Converting, S.ConvertingLeft) <= 0;

    /// <summary>
    /// Раз в тик (deployOrders): посланные разворачиваться, доехав, разворачиваются; место негодно — приказ снят. Не
    /// доехал — пробует снова, всё реже, и после пяти промахов бросает.
    /// </summary>
    public sealed class DeployOrders : ISystem
    {
        public void Bind(SystemBinding binding)
        {
        }

        public void Run(SystemContext ctx)
        {
            var call = Call.Of(ctx);
            var arrived = new List<uint>();
            var late = new List<uint>();
            foreach (var entity in ctx.Entities)
            {
                var (x, y) = ((int)call.Num(entity, S.Deploys, S.DeploysX), (int)call.Num(entity, S.Deploys, S.DeploysY));
                if (StandsOn(call, entity, x, y))
                    arrived.Add(entity);
                else if (Core.OnTurn(ctx.Tick, entity, RetryTicks << (int)call.Num(entity, S.Deploys, S.DeploysMisses)))
                    late.Add(entity);
            }
            foreach (var entity in arrived)
            {
                var (x, y) = DeploySite(call, entity);
                if (CanDeployAt(call, call.OwnerOf(entity), entity, x, y))
                    Start(call, entity, DeploySeconds);
                else
                    call.Remove(entity, S.Deploys);
            }
            foreach (var entity in late)
            {
                var misses = call.Num(entity, S.Deploys, S.DeploysMisses) + 1;
                if (misses > MissesLimit)
                {
                    call.Remove(entity, S.Deploys);
                    continue;
                }
                call.Set(entity, S.Deploys).Number(S.DeploysMisses, misses);
                var (x, y) = DeploySite(call, entity);
                var (mx, my) = Middle(x, y);
                Moves.Order(call, entity, mx, my);
            }
        }
    }

    /// <summary>
    /// Раз в тик (convert): продвигает превращения и завершает те, чьё время вышло. MCV, под которым стоят юниты, зданием
    /// не становится: свои уходят, пока он разворачивается, чужих он ждёт.
    /// </summary>
    public sealed class Convert : ISystem
    {
        private Column _left;

        public void Bind(SystemBinding binding) => _left = binding.Column(S.Converting.Name, S.ConvertingLeft.Name);

        public void Run(SystemContext ctx)
        {
            var call = Call.Of(ctx);
            var left = ctx.Column(_left);
            var entities = ctx.Entities;
            var ready = new List<uint>();
            var deploying = new List<uint>();
            for (var row = 0; row < entities.Length; row++)
            {
                left[row] = Math.Max(0, left[row] - 1);
                if (call.Has(entities[row], S.Unit))
                    deploying.Add(entities[row]);
                if (left[row] <= 0)
                    ready.Add(entities[row]);
            }
            // Выгонять пробуют не каждый тик: поиск пути недёшев.
            var blocked = new HashSet<uint>();
            var (w, h) = CoreSize;
            foreach (var entity in deploying)
            {
                var (x, y) = DeploySite(call, entity);
                if (!Moves.Clear(call, call.OwnerOf(entity), x, y, w, h, Core.OnTurn(ctx.Tick, entity, EvictTicks), entity))
                    blocked.Add(entity);
            }
            foreach (var entity in ready)
                if (!blocked.Contains(entity))
                    Finish(call, entity);
        }
    }

    /// <summary>Превращение кончилось: MCV — в здание, здание — в MCV; очередь и прочность — с собой.</summary>
    private static void Finish(Call call, uint entity)
    {
        Stop(call, entity);
        var player = call.OwnerOf(entity);
        var production = Production.Take(call, entity);
        var health = call.Num(entity, S.Health, S.HealthValue, 1);
        var (w, h) = CoreSize;
        if (call.Has(entity, S.Unit))
        {
            var (x, y) = DeploySite(call, entity);
            // За время разворачивания место могли занять: тогда MCV остаётся машиной.
            if (!Placement.CanPlace(call, Command, x, y))
            {
                call.Remove(entity, S.Deploys);
                return;
            }
            call.Destroy(entity);
            ClearDrops(call, x, y, w, h);
            var core = Factory.PlaceBuilding(call, Command, x, y, player);
            Production.Put(call, core, production);
            call.Set(core, S.Health).Number(S.HealthValue, health);
            Accounts.Reward(call, player, "deploy");
        }
        else
        {
            var (x, y) = call.At(entity);
            call.Destroy(entity);
            var mcv = Factory.SpawnUnit(call, "mcv", player, Math.Floor(x) + w / 2 + 0.5, Math.Floor(y) + h / 2 + 0.5);
            Production.Put(call, mcv, production);
            call.Set(mcv, S.Health).Number(S.HealthValue, health);
        }
    }

    /// <summary>Дропы под основанием исчезают (clearDrops): здание встаёт поверх.</summary>
    private static void ClearDrops(Call call, int x, int y, int w, int h)
    {
        foreach (var drop in Tools.Near(x + w / 2.0, y + h / 2.0, Math.Max(w, h), S.Drop))
        {
            var (px, py) = call.At(drop);
            if (px >= x && px < x + w && py >= y && py < y + h)
                call.Destroy(drop);
        }
    }
}
