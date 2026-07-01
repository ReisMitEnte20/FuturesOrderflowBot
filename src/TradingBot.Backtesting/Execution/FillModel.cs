using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Backtesting.Execution;

/// <summary>Ergebnis eines Fill-Versuchs: das FillEvent und die (informativen) Slippage-Kosten in Geld.</summary>
public sealed record FillResult(FillEvent Event, decimal SlippageCost);

/// <summary>
/// Simuliert Fills gegen einen einzelnen Tick. Deterministisch, ohne Netzwerk.
///
/// Regeln:
/// - MARKET: füllt am aktuellen Tick-Preis mit ADVERSER Slippage (Buy: Preis + Slippage, Sell: Preis − Slippage).
///   Die "nächster Tick"-Semantik entsteht durch die Aufrufreihenfolge der Engine (offene Orders VOR der Strategie).
/// - LIMIT: füllt nur, wenn der Tick den Limitpreis berührt/übertrifft; Fill zum Limitpreis, KEINE Slippage.
/// - STOP: füllt nur, wenn der Stop ausgelöst wurde; wird dann zur Market-Order (mit adverser Slippage).
/// - STOPLIMIT: ausgelöst UND Limit berührt; Fill zum Limitpreis, keine Slippage.
///
/// Slippage-Preis = slippageTicks × TickSize (aus InstrumentProfile). Slippage-Kosten = slippageTicks × TickValue × Menge.
/// </summary>
public sealed class FillModel
{
    public FillResult? TryFill(OrderRequest order, MarketTick tick, InstrumentProfile instrument, decimal slippageTicks)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(instrument);
        if (slippageTicks < 0m) slippageTicks = 0m;

        decimal slip = slippageTicks * instrument.TickSize;
        bool isBuy = order.Side == OrderSide.Buy;

        switch (order.OrderType)
        {
            case OrderType.Market:
            {
                decimal price = isBuy ? tick.Price + slip : tick.Price - slip;
                return Fill(order, tick, price, slippageTicks, instrument);
            }
            case OrderType.Limit:
            {
                if (order.LimitPrice is not decimal limit) return null;
                bool touched = isBuy ? tick.Price <= limit : tick.Price >= limit;
                return touched ? Fill(order, tick, limit, slippageTicks: 0m, instrument) : null;
            }
            case OrderType.Stop:
            {
                if (order.StopPrice is not decimal stop) return null;
                bool triggered = isBuy ? tick.Price >= stop : tick.Price <= stop;
                if (!triggered) return null;
                decimal price = isBuy ? tick.Price + slip : tick.Price - slip; // wird Market
                return Fill(order, tick, price, slippageTicks, instrument);
            }
            case OrderType.StopLimit:
            {
                if (order.StopPrice is not decimal s || order.LimitPrice is not decimal l) return null;
                bool triggered = isBuy ? tick.Price >= s : tick.Price <= s;
                bool limitOk = isBuy ? tick.Price <= l : tick.Price >= l;
                return triggered && limitOk ? Fill(order, tick, l, slippageTicks: 0m, instrument) : null;
            }
            default:
                return null;
        }
    }

    private static FillResult Fill(
        OrderRequest order, MarketTick tick, decimal price, decimal slippageTicks, InstrumentProfile instrument)
    {
        var ev = new FillEvent
        {
            OrderId = order.OrderId,
            Symbol = order.Symbol,
            Side = order.Side,
            Quantity = order.Quantity,
            FillPrice = price,
            Timestamp = tick.Timestamp,
            IsPartial = false
        };
        decimal slippageCost = slippageTicks * instrument.TickValue * order.Quantity;
        return new FillResult(ev, slippageCost);
    }
}
