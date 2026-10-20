using Base.Host.Kernel;
using Base.Shared;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Tactics;

/// <summary>
/// Система patrol (tactics.ts): раз в тик, после боя, вставшие юниты без цели продолжают патруль или возвращаются на
/// место, откуда ушли в погоню. Дошедший до точки патруля идёт к следующей.
/// <para>
/// Доступ к чужому (Н5-3): кадр — юниты с тактикой; идёт ли юнит (Path) и есть ли цель у стрелка (Armed) — побочными
/// таблицами; турели — одним вызовом Children у тех, кто дошёл до проверки. Патруль — байты пачкой у тех, кого вообще
/// разбирают. Поиск путей — с нормой на тик (<see cref="SearchTiles"/>).
/// </para>
/// </summary>
internal sealed class Patrol : ISystem
{
    private Column _resupply, _leg, _away, _homeX, _homeY, _x, _y, _target;

    /// <summary>
    /// Норма поиска путей патруля и возвращения домой на тик — тайлов на всех (pool инструмента Plan; не из TS: там ищут
    /// все разом). После большого боя домой разом собираются сотни, и до дома, обступленного своими же, путь не находится
    /// долго: такие поиски — пик тика в десятки миллисекунд. Первый поиск тика идёт без нормы: недостижимое место надо
    /// уметь признать недостижимым (долгий поиск больше нормы), остальные делят норму. Кому она не досталась, тот ждёт
    /// своей очереди (раз в RESUME_TICKS), как если бы она ещё не пришла.
    /// </summary>
    private const uint SearchPool = 0x7A7801, SearchTiles = 15000;

    public static SystemDeclaration Declare(SystemDeclaration declaration) => declaration
        .Query(Names.Tactics.Name, Names.Position.Name)
        .Table(Names.Path.Name)
        .Table(Names.Armed.Name);

    public void Bind(SystemBinding binding)
    {
        T.Load();
        _resupply = binding.Column(Names.Tactics.Name, Names.Tactics.Resupply);
        _leg = binding.Column(Names.Tactics.Name, Names.Tactics.Leg);
        _away = binding.Column(Names.Tactics.Name, Names.Tactics.Away);
        _homeX = binding.Column(Names.Tactics.Name, Names.Tactics.HomeX);
        _homeY = binding.Column(Names.Tactics.Name, Names.Tactics.HomeY);
        _x = binding.Column(Names.Position.Name, Names.Position.X);
        _y = binding.Column(Names.Position.Name, Names.Position.Y);
        _target = binding.Table(1).Column(Names.Armed.Name, Names.Armed.Target);
    }

    /// <summary>Что делать юниту: идти в точку; leg — это точка патруля, иначе место, откуда ушёл в погоню.</summary>
    private sealed class Move(uint entity, int x, int y, bool leg, int count, double legNow)
    {
        public readonly uint Entity = entity;
        public readonly int X = x, Y = y, Count = count;
        public readonly bool Leg = leg;
        public double LegNow = legNow;
    }

    public void Run(SystemContext ctx)
    {
        if (ctx.Count == 0)
            return;
        var call = Call.Of(ctx);
        var paths = ctx.Table(0);
        var armed = ctx.Table(1);
        var targets = armed.Column(_target);
        var resupply = ctx.Column(_resupply);
        var legs = ctx.Column(_leg);
        var away = ctx.Column(_away);
        var homeX = ctx.Column(_homeX);
        var homeY = ctx.Column(_homeY);
        var px = ctx.Column(_x);
        var py = ctx.Column(_y);
        var rows = new List<int>();
        // Чьи турели смотреть: юниты без своей цели. Турели — одним вызовом Children на всех (инструмент обходит все
        // турели мира, и вызов на юнит в бою тысяч — пик тика).
        var unsure = new List<int>();
        for (var row = 0; row < ctx.Count; row++)
        {
            var entity = ctx.Entities[row];
            // Идущего за боеприпасами ведёт снабжение: на место он вернётся, когда наполнит магазин.
            if (resupply[row] != 0 || paths.RowOf(entity) >= 0 || !Core.OnTurn(ctx.Tick, entity, Stances.ResumeTicks) || HasTarget(armed, targets, entity))
                continue;
            // Турели смотрим, только если у самого юнита целей нет, а Armed в мире вообще есть.
            if (armed.Count > 0)
                unsure.Add(row);
            else
                rows.Add(row);
        }
        if (unsure.Count > 0)
        {
            var carriers = new uint[unsure.Count];
            for (var i = 0; i < carriers.Length; i++)
                carriers[i] = ctx.Entities[unsure[i]];
            var turrets = Tools.Children(carriers);
            for (var i = 0; i < carriers.Length; i++)
            {
                var fighting = false;
                foreach (var turret in turrets[i])
                    fighting |= HasTarget(armed, targets, turret);
                if (!fighting)
                    rows.Add(unsure[i]);
            }
            rows.Sort();
        }
        if (rows.Count == 0)
            return;
        var entities = new uint[rows.Count];
        for (var i = 0; i < entities.Length; i++)
            entities[i] = ctx.Entities[rows[i]];
        // По возрастанию номеров: поиск пути делит норму тика, порядок не должен зависеть от раскладки кадра.
        var order = rows.Zip(entities).OrderBy(pair => pair.Second).ToArray();
        for (var i = 0; i < entities.Length; i++)
            (rows[i], entities[i]) = order[i];
        var patrols = Tools.BytesMany(entities, S.Tactics, T.Patrol);
        var moves = new List<Move>();
        for (var i = 0; i < entities.Length; i++)
        {
            var row = rows[i];
            var entity = entities[i];
            var patrol = Stances.Unpack(patrols[i]);
            if (patrol.Length > 0)
            {
                var count = patrol.Length / 2;
                var leg = (int)legs[row];
                if (leg < 0 || leg >= count)
                    leg = 0;
                // Дошёл до точки — к следующей; встал, не дойдя (после боя), — снова к ней.
                if (Math.Floor(px[row]) == patrol[leg * 2] && Math.Floor(py[row]) == patrol[leg * 2 + 1])
                    leg = (leg + 1) % count;
                if (leg != (int)legs[row])
                    call.Set(entity, S.Tactics).Number(T.Leg, leg);
                moves.Add(new Move(entity, (int)patrol[leg * 2], (int)patrol[leg * 2 + 1], true, count, leg));
                continue;
            }
            if (away[row] == 0)
                continue;
            // Пришёл — или встал рядом, потому что место заняли. Не пришёл — идёт туда снова: встреченный по дороге враг
            // места назначения не отменяет.
            if (DetMath.Hypot(px[row] - homeX[row] - 0.5, py[row] - homeY[row] - 0.5) <= Stances.HomeReach)
                call.Set(entity, S.Tactics).Flag(T.Away, false);
            else
                moves.Add(new Move(entity, (int)homeX[row], (int)homeY[row], false, 0, 0));
        }
        var first = true;
        foreach (var move in moves)
        {
            var (pool, budget) = first ? (0u, 0u) : (SearchPool, SearchTiles);
            first = false;
            // Норма вышла: ничего не решено, юнит попробует в свою очередь. Разворачивающийся (врос) — как в TS: пути нет.
            if (Moves.Order(call, move.Entity, move.X, move.Y, pool: pool, budget: budget) == PlanStatus.Cut && pool != 0 && !call.Has(move.Entity, S.Converting))
                continue;
            var hasPath = call.Has(move.Entity, S.Path);
            // Ближе к точке патруля не подойти (она на здании, за обрывом): точка засчитана, юнит идёт к следующей. Иначе он
            // искал бы путь к ней каждые RESUME_TICKS.
            if (move.Leg && !hasPath)
                call.Set(move.Entity, S.Tactics).Number(T.Leg, (move.LegNow + 1) % move.Count);
            // До места не добраться — остаётся где стоит.
            if (!move.Leg && !hasPath)
                call.Set(move.Entity, S.Tactics).Flag(T.Away, false);
        }
    }

    /// <summary>Есть ли цель у стрелка: юнит занят боем (fighting), если она есть у него или у его турелей.</summary>
    private static bool HasTarget(TableView armed, Span<double> targets, uint gunner) => armed.RowOf(gunner) is var row and >= 0 && targets[row] != 0;
}
