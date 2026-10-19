using Base.Host.Kernel;
using Base.Host.Sections.Logistics;
using Base.Host.Sections.Mining;
using Base.Shared;
using Base.Shared.Rules;
using Kharos.Mod.Sdk;

namespace Base.Host.Sections.Trade;

/// <summary>Система trade (trade.ts): корабли с полным грузом улетают, за долетевшие платят; закупка выгружается в склад порта.</summary>
internal sealed class TradeFlights : ISystem
{
    public void Bind(SystemBinding binding) => T.Load(binding.Component);

    public void Run(SystemContext ctx)
    {
        if (ctx.Count == 0)
            return;
        var call = Call.Of(ctx);
        var done = new List<uint>();
        foreach (var port in ctx.Entities.ToArray())
        {
            if (call.Num(port, T.Trade, T.TradeTotal) > 0)
            {
                var left = call.Num(port, T.Trade, T.TradeLeft) - 1;
                call.Set(port, T.Trade).Number(T.TradeLeft, left);
                if (left <= 0)
                    done.Add(port);
            }
            else if (Trading.Delivered(call, port) >= call.Num(port, T.Trade, T.TradeWanted) - 1e-9)
            {
                var ticks = Trading.FlightTicks(call, port, TradeRules.SellSeconds);
                call.Set(port, T.Trade).Number(T.TradeLeft, ticks).Number(T.TradeTotal, ticks);
            }
        }
        // Корабль улетает с товаром заявки; остальное в складе космопорта остаётся. Закупка — наоборот, выгружается.
        foreach (var port in done)
        {
            var good = Cargo.Of(call.Num(port, T.Trade, T.TradeResource, -1));
            var wanted = call.Num(port, T.Trade, T.TradeWanted);
            var buy = call.Flag(port, T.Trade, T.TradeBuy);
            call.Remove(port, T.Trade);
            if (good < 0)
                continue;
            var name = Cargo.Name(good);
            if (buy)
            {
                if (call.Has(port, S.Stock))
                    Stocks.Put(call, port, name, wanted);
                continue;
            }
            var player = call.OwnerOf(port);
            var sold = Stocks.Take(call, port, name, wanted);
            Accounts.Add(call, player, BuildRules.Round(sold * Core.Tables.Resources[name].Price));
            if (sold > 0)
                Accounts.Reward(call, player, "sale");
        }
    }
}

/// <summary>Система ferry (ferry.ts): корабли с подарками долетают и выгружаются.</summary>
internal sealed class FerryFlights : ISystem
{
    public void Bind(SystemBinding binding) => T.Load(binding.Component);

    public void Run(SystemContext ctx)
    {
        if (ctx.Count == 0)
            return;
        var call = Call.Of(ctx);
        Holds? holds = null;
        foreach (var port in ctx.Entities.ToArray())
        {
            if (call.Flag(port, T.Transfer, T.TransferDone))
                continue;
            var left = call.Num(port, T.Transfer, T.TransferLeft) - 1;
            call.Set(port, T.Transfer).Number(T.TransferLeft, left);
            if (left > 0)
                continue;
            holds ??= Holds.Read(call);
            Ferry.Arrive(call, port, holds);
        }
        holds?.Write(call);
    }
}
