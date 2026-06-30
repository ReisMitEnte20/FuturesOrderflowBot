using TradingBot.Domain.Models;

namespace TradingBot.Application.MarketData;

/// <summary>
/// Aggregiert je N Ticks zu einer Bar. Eine unvollständige letzte Bar wird mit aufgenommen.
/// OpenTime/CloseTime = Zeitstempel des ersten/letzten Ticks der Bar.
/// </summary>
public static class TickBarAggregator
{
    public static IReadOnlyList<Candle> Aggregate(IEnumerable<MarketTick> ticks, int ticksPerBar)
    {
        ArgumentNullException.ThrowIfNull(ticks);
        if (ticksPerBar <= 0)
            throw new ArgumentOutOfRangeException(nameof(ticksPerBar), "ticksPerBar muss > 0 sein.");

        var list = ticks as IReadOnlyList<MarketTick> ?? ticks.ToList();
        if (list.Count == 0) return Array.Empty<Candle>();

        var symbol = BarMath.RequireSingleSymbol(list);
        var result = new List<Candle>();

        for (int start = 0; start < list.Count; start += ticksPerBar)
        {
            int count = Math.Min(ticksPerBar, list.Count - start);
            var bar = new List<MarketTick>(count);
            for (int i = 0; i < count; i++) bar.Add(list[start + i]);
            result.Add(BarMath.BuildCandle(symbol, bar[0].Timestamp, bar[^1].Timestamp, bar));
        }

        return result;
    }
}
