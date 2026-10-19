using Base.Client;
using Base.Shared.Data;
using Base.Shared.Orders;
using Kharos.I18n;

namespace Kharos.Base.Tests.Client.Hud;

/// <summary>Сетка команд HUD как чистая функция состояния (Commands — commandsOf из ui/commands.ts) на русском.</summary>
public sealed class RequestButtonsTests
{
    /// <summary>Таблицы base из data/*.json.</summary>
    internal static readonly Lazy<BaseTables> Tables = new(() =>
        BaseTables.Load(name => Path.Combine(Sim.ModDir, "data", name + ".json") is var path && File.Exists(path) ? File.ReadAllBytes(path) : null));

    /// <summary>Словари: оболочка и base (i18n/ru.json).</summary>
    private static readonly Lazy<Texts> Dictionary = new(() =>
    {
        var texts = Texts.Shell();
        texts.Add("ru", File.ReadAllBytes(Path.Combine(Sim.ModDir, "i18n", "ru.json")), "base");
        return texts;
    });

    /// <summary>Текст на русском: подстановки парами «имя, значение», как инструмент Text.</summary>
    internal static string T(string key, params string[] args)
    {
        var pairs = new (string Name, object Value)[args.Length / 2];
        for (var i = 0; i < pairs.Length; i++)
            pairs[i] = (args[2 * i], args[2 * i + 1]);
        return Dictionary.Value.T("ru", key, pairs);
    }

    [Fact(DisplayName = "кнопки заказа: груз, потом количество шагами, не больше склада за вычетом других заказов")]
    public void OrderButtons()
    {
        var request = new RequestState([5], new Dictionary<string, double> { ["ammo"] = 60 }, new Dictionary<string, double> { ["ammo"] = 20 }, 100);
        var state = new HudState { Request = request, Credits = 0, Building = "requestStation" };
        Deal? deal = null;
        var sent = new List<object>();
        var actions = new GridActions { Send = sent.Add, Pick = next => deal = next };
        Slot?[] Grid() => Commands.Of(state, "request", actions, Tables.Value, T, deal);

        var root = Commands.Of(state, "root", actions, Tables.Value, T)[0];
        Assert.NotNull(root);
        Assert.Equal("Заказ: 1", root.Label);
        Assert.True(root.Active);
        Assert.Contains(Grid(), slot => slot?.Label == "Боеприпасы ×60");
        Grid().First(slot => slot?.Label == "Металл")!.Run();
        Assert.Equal(new Deal("metal", 0), deal);
        var less = Grid()[4]!;
        Assert.Equal("−10", less.Label);
        Assert.True(less.Disabled);
        Grid()[1]!.Run();
        Grid()[2]!.Run();
        Assert.Equal(2, sent.Count);
        foreach (var order in sent)
        {
            var want = Assert.IsType<WantOrder>(order);
            Assert.Equal([5u], want.Buildings);
            Assert.Equal("metal", want.Resource);
            Assert.Equal(40, want.Amount);
        }
    }
}
