using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Application.MarketData;

/// <summary>
/// Aggregiert je N Ticks zu einer <see cref="OrderFlowBar"/> mit Bid-/Ask-Volumen, Delta und
/// kumulativem Delta. ERFORDERT klassifizierte Ticks (<see cref="MarketTick.Aggressor"/> != Unknown);
/// fehlt die Klassifikation, wird <see cref="OrderFlowUnavailableException"/> geworfen –
/// es werden KEINE Orderflow-Daten erfunden.
/// </summary>
public static class OrderFlowBarAggregator
{
    public static IReadOnlyList<OrderFlowBar> AggregateByTicks(IEnumerable<MarketTick> ticks, int ticksPerBar)
    {
        ArgumentNullException.ThrowIfNull(ticks);
        if (ticksPerBar <= 0)
            throw new ArgumentOutOfRangeException(nameof(ticksPerBar), "ticksPerBar muss > 0 sein.");

        var list = ticks as IReadOnlyList<MarketTick> ?? ticks.ToList();
        if (list.Count == 0) return Array.Empty<OrderFlowBar>();

        var symbol = BarMath.RequireSingleSymbol(list);
        var result = new List<OrderFlowBar>();
        decimal cumulativeDelta = 0m;

        for (int start = 0; start < list.Count; start += ticksPerBar)
        {
            int count = Math.Min(ticksPerBar, list.Count - start);
            var bar = new List<MarketTick>(count);
            for (int i = 0; i < count; i++) bar.Add(list[start + i]);
            result.Add(BuildBar(symbol, bar, ref cumulativeDelta));
        }

        return result;
    }

    /// <summary>Baut eine einzelne OrderFlowBar; aktualisiert das laufende kumulative Delta.</summary>
    public static OrderFlowBar BuildBar(string symbol, IReadOnlyList<MarketTick> bar, ref decimal cumulativeDelta)
    {
        decimal high = bar[0].Price, low = bar[0].Price;
        decimal total = 0m, bidVolume = 0m, askVolume = 0m;

        foreach (var t in bar)
        {
            if (t.Aggressor == AggressorSide.Unknown)
                throw new OrderFlowUnavailableException(
                    $"Tick @ {t.Timestamp:O} ohne Aggressor-Klassifikation – Orderflow kann nicht berechnet werden.");

            if (t.Price > high) high = t.Price;
            if (t.Price < low) low = t.Price;
            total += t.Volume;
            if (t.Aggressor == AggressorSide.Buy) askVolume += t.Volume;
            else bidVolume += t.Volume; // Sell
        }

        cumulativeDelta += askVolume - bidVolume;

        return new OrderFlowBar
        {
            Symbol = symbol,
            OpenTime = bar[0].Timestamp,
            CloseTime = bar[^1].Timestamp,
            Open = bar[0].Price,
            High = high,
            Low = low,
            Close = bar[^1].Price,
            TotalVolume = total,
            BidVolume = bidVolume,
            AskVolume = askVolume,
            CumulativeDelta = cumulativeDelta
        };
    }
}
