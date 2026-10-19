using Base.Host.Kernel;
using Base.Host.Sections.Energy;
using Base.Host.Sections.Mining;
using Base.Shared;
using Base.Shared.Rules;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Research;

/// <summary>
/// Система research (research.ts): раз в тик лаборатории ведут нынешнюю тему своего хозяина — головную тему плана или
/// ту, что её подменяет (<see cref="Picks"/>), — и платят за сделанное. Цену темы платят по ходу работы: за долю
/// исследования лаборатория берёт ту же долю кредитов со счёта и сырья — со своего склада и складов своей сети труб.
/// Чего в её сети нет, без того она стоит; при нехватке энергии работает и тратит медленнее, выключенная стоит.
/// Состояние лаборатории — Research (speed до сотых, lack: idle, off, power, credits или груз).
/// <para>
/// Главный запрос — лаборатории (Research, без Site): компонент ставит им снаряжение готового здания. Таблица кадра —
/// здания со складом: по ней сеть отбрасывает трубы и станции без вызовов.
/// </para>
/// </summary>
internal sealed class Labs : ISystem
{
    private Column _owner;

    public void Bind(SystemBinding binding)
    {
        R.Load(binding.Component);
        _owner = binding.Column(Names.Owner.Name, Names.Owner.Player);
    }

    public void Run(SystemContext ctx)
    {
        if (ctx.Count == 0)
            return;
        var call = Call.Of(ctx);
        var owners = ctx.Column(_owner);
        var rows = new List<(uint Entity, uint Player)>(ctx.Count);
        for (var row = 0; row < ctx.Count; row++)
            rows.Add((ctx.Entities[row], (uint)owners[row]));
        rows.Sort((a, b) => a.Entity.CompareTo(b.Entity));
        // Лаборатории по игрокам — в порядке первой встречи.
        var players = new List<uint>();
        var labs = new Dictionary<uint, List<uint>>();
        foreach (var (entity, player) in rows)
        {
            if (Factory.Building(call.BuildingType(entity)) is not { Researches: true })
                continue;
            if (!labs.TryGetValue(player, out var list))
            {
                labs[player] = list = [];
                players.Add(player);
            }
            list.Add(entity);
        }
        if (players.Count == 0)
            return;
        var holders = new HashSet<uint>(ctx.Table(0).Entities.ToArray());
        Dictionary<uint, double>? supply = null;
        // Что отдают склады сети лаборатории: считается раз на сеть и груз, взятое вычитается.
        var stocks = new Dictionary<object, Dictionary<string, double>>(ReferenceEqualityComparer.Instance);
        var step = ctx.Dt > 0 ? ctx.Dt : Core.Step;
        foreach (var player in players)
        {
            var list = labs[player];
            var account = Accounts.Find(call, player);
            // План сначала сбрасывает то, что вести нельзя; какую из оставшихся тем вести, решает выбор.
            var topic = account == 0 || Knowledge.Next(call, account) is null
                ? null
                : Picks.Pick(call, player, account, [.. list.Where(lab => !call.Has(lab, R.Off))]);
            if (account == 0 || topic is null)
            {
                foreach (var lab in list)
                    Report(call, lab, 0, "idle");
                continue;
            }
            supply ??= Books.Supply(call);
            var networks = new Networks(call, holders);
            var price = ResearchRules.Price(Knowledge.Topics, topic, Core.Rules);
            var ticks = Math.Max(1, price.Seconds / step);
            var done = Knowledge.ProgressOf(call, account, topic);
            foreach (var lab in list)
            {
                // Выключенная энергию не берёт, но стоит не от нехватки: игрок видит «выключена», а не «нет энергии».
                if (call.Has(lab, R.Off))
                {
                    Report(call, lab, 0, "off");
                    continue;
                }
                var power = supply.GetValueOrDefault(lab);
                var share = Math.Min(power / ticks, 1 - done);
                if (share <= ResearchRules.Epsilon)
                {
                    Report(call, lab, 0, power > 0 ? "idle" : "power");
                    continue;
                }
                // Какую долю своей работы лаборатория может оплатить: всю или сколько хватает самого скудного.
                var part = 1.0;
                var lack = power < 1 ? "power" : "";
                var credits = call.Num(account, S.Player, S.PlayerCredits);
                var earned = call.Num(account, S.Player, S.PlayerEarned);
                var cost = price.Cost * share;
                if (cost > ResearchRules.Epsilon && credits + earned < cost)
                {
                    part = Math.Max(0, (credits + earned) / cost);
                    lack = "credits";
                }
                var key = (object?)Construction.Zones.NetworkOf(lab) ?? lab;
                if (!stocks.TryGetValue(key, out var network))
                    stocks[key] = network = [];
                var needs = new List<(string Good, double Amount)>();
                foreach (var (good, amount) in price.Materials)
                {
                    var need = amount * share;
                    // Сначала — привезённое на склад лаборатории, потом — что отдают склады её сети.
                    if (!network.TryGetValue(good, out var given))
                        network[good] = given = networks.Stock(lab, good);
                    var have = Stocks.Amount(call, lab, good) + given;
                    if (have + ResearchRules.Epsilon < need * part)
                    {
                        part = Math.Max(0, have / need);
                        lack = good;
                    }
                    needs.Add((good, need));
                }
                if (part <= ResearchRules.Epsilon)
                {
                    Report(call, lab, 0, lack.Length > 0 ? lack : "credits");
                    continue;
                }
                for (var i = 0; i < needs.Count; i++)
                    needs[i] = (needs[i].Good, needs[i].Amount * part);
                if (cost > ResearchRules.Epsilon && !Accounts.Spend(call, player, Math.Min(cost * part, credits + earned)))
                {
                    Report(call, lab, 0, "credits");
                    continue;
                }
                if (call.Has(lab, S.Stock))
                    for (var i = 0; i < needs.Count; i++)
                        needs[i] = (needs[i].Good, needs[i].Amount - Stocks.Take(call, lab, needs[i].Good, needs[i].Amount));
                networks.Take(lab, needs);
                foreach (var (good, amount) in needs)
                    if (network.TryGetValue(good, out var given))
                        network[good] = Math.Max(0, given - amount);
                done += share * part;
                Report(call, lab, power * part, lack);
            }
            Knowledge.Finish(call, account, topic, done);
        }
    }

    /// <summary>Ставит лаборатории её состояние, только если оно изменилось (report): компонент уходит в сеть.</summary>
    private static void Report(Call call, uint lab, double speed, string lack)
    {
        var rounded = Math.Floor(speed * 100 + 0.5) / 100;
        // То же числом для клиента (строку мира он не читает): причина и Proto груза.
        var cause = LabLack.Of(lack);
        var good = cause == LabLack.Material && Logistics.Cargo.Index(lack) is var index and >= 0 ? Logistics.Cargo.Proto(index) : -1;
        if (!call.Has(lab, R.Research))
            call.Add(lab, R.Research).Number(R.ResearchSpeed, rounded).Text(R.ResearchLack, lack).Number(R.ResearchCause, cause).Number(R.ResearchGood, good);
        else if (call.Num(lab, R.Research, R.ResearchSpeed) != rounded || (call.Text(lab, R.Research, R.ResearchLack) ?? "") != lack)
            call.Set(lab, R.Research).Number(R.ResearchSpeed, rounded).Text(R.ResearchLack, lack).Number(R.ResearchCause, cause).Number(R.ResearchGood, good);
    }
}
