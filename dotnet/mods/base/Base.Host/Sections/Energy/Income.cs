using System.Buffers.Binary;
using Base.Host.Kernel;
using Base.Shared;
using Base.Shared.Rules;
using Kharos.Mod.Sdk;
using static Base.Host.Sections.Energy.Refs;

namespace Base.Host.Sections.Energy;

/// <summary>
/// Доход (earn в income.ts): раз в тик игроки получают доход своих сетей, доли кредита копятся в Player.earned. Игроку
/// не в сети — доля дохода (rules.offlineIncome), и за одно отсутствие не больше потолка (rules.offlineMinutes минут
/// обычного дохода): Player.away — сколько уже набрано. В конце тика перегруженные станции изнашиваются.
/// <para>
/// Кто в сети — Player.online по событиям движка player_online и player_left (их ведёт раздел игроков, Players/Presence).
/// Пока хост ни разу не сказал, кто в сети (сценарии, тесты), в сети все: так в TS при <c>sim.online</c> = undefined.
/// Сказал — помнит это память мода (<see cref="Key"/>).
/// </para>
/// </summary>
internal static class Income
{
    /// <summary>Ключ памяти раздела: хост сообщает, кто в сети (байт 1).</summary>
    public const string Key = "energy.online";

    /// <summary>Системы openBooks и closeBooks: книги тика (<see cref="Books"/>).</summary>
    public sealed class OpenBooks : ISystem
    {
        public void Bind(SystemBinding binding) => Refs.Load(binding.Component);

        public void Run(SystemContext ctx) => Books.Open(ctx.Tick);
    }

    public sealed class CloseBooks : ISystem
    {
        public void Bind(SystemBinding binding) => Refs.Load(binding.Component);

        public void Run(SystemContext ctx) => Books.Close();
    }

    /// <summary>Система earn: по счетам игроков (Player).</summary>
    public sealed class Earn : ISystem
    {
        private Column _id, _credits, _earned, _away, _online;

        public void Bind(SystemBinding binding)
        {
            Refs.Load(binding.Component);
            _id = binding.Column(Names.Player.Name, Names.Player.Id);
            _credits = binding.Column(Names.Player.Name, Names.Player.Credits);
            _earned = binding.Column(Names.Player.Name, Names.Player.Earned);
            _away = binding.Column(Names.Player.Name, Names.Player.Away);
            _online = binding.Column(Names.Player.Name, Names.Player.Online);
        }

        public void Run(SystemContext ctx)
        {
            var call = Call.Of(ctx);
            var all = Books.All(call);
            if (all.Count == 0)
                return;
            var incomes = new Dictionary<uint, double>();
            foreach (var (player, _, economy) in all)
                incomes[player] = incomes.GetValueOrDefault(player) + economy.Income;
            var tracked = Tracked(call);
            var id = ctx.Column(_id);
            var credits = ctx.Column(_credits);
            var earned = ctx.Column(_earned);
            var away = ctx.Column(_away);
            var online = ctx.Column(_online);
            var rules = Core.Rules;
            for (var row = 0; row < ctx.Count; row++)
            {
                var here = !tracked || online[row] != 0;
                var income = incomes.GetValueOrDefault((uint)id[row]);
                var gain = income * ctx.Dt;
                if (!here)
                    gain = Math.Max(0, Math.Min(gain * rules.OfflineIncome, income * rules.OfflineMinutes * 60 - away[row]));
                // В сети — отсутствие кончилось, и потолок снова полный.
                var gone = here ? 0 : away[row] + gain;
                if (gain <= 0 && gone == away[row])
                    continue;
                var total = earned[row] + gain;
                var whole = Math.Floor(total);
                call.Set(ctx.Entities[row], S.Player).Number(S.PlayerCredits, credits[row] + whole).Number(S.PlayerEarned, total - whole).Number(R.PlayerAway, gone);
            }
            // Износ — в самом конце: разрушенное здание исчезает из мира, а сети этого тика о нём ещё помнят.
            Wear(call, all, ctx.Dt);
        }
    }

    /// <summary>Перегруженные станции теряют прочность и в нуле разрушаются (wear); сами не восстанавливаются.</summary>
    private static void Wear(Call call, List<Books.Book> all, double dt)
    {
        var ruined = new List<uint>();
        foreach (var (_, zone, economy) in all)
        {
            var overload = economy.Overload;
            if (overload <= 0)
                continue;
            foreach (var entity in zone.Buildings)
            {
                if (Books.PowerAt(call, entity) <= 0 || !call.Has(entity, S.Health))
                    continue;
                var health = call.Num(entity, S.Health, S.HealthValue) - PowerRules.OverloadDamage * overload * dt;
                call.Set(entity, S.Health).Number(S.HealthValue, health);
                if (health <= 0)
                    ruined.Add(entity);
            }
        }
        foreach (var entity in ruined)
        {
            Reactors.Meltdown(call, entity);
            call.Destroy(entity);
        }
    }

    /// <summary>Говорил ли хост, кто в сети (пишет раздел игроков, Players/Presence).</summary>
    public static bool Tracked(Call call) => Memory.Of(call).Get(Key) is { Length: > 0 } bytes && bytes[0] != 0;
}
