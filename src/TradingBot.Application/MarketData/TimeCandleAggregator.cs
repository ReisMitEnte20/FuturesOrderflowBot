using TradingBot.Domain.Models;

namespace TradingBot.Application.MarketData;

/// <summary>
/// Aggregiert Ticks zu Zeit-Kerzen fester Intervalllänge (z. B. 1 Minute).
/// Es werden KEINE leeren Kerzen für ticklose Intervalle erzeugt. Erwartet chronologische Ticks.
/// </summary>
public static class TimeCandleAggregator
{
    public static IReadOnlyList<Candle> Aggregate(IEnumerable<MarketTick> ticks, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(ticks);
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(interval), "Intervall muss > 0 sein.");

        var list = ticks as IReadOnlyList<MarketTick> ?? ticks.ToList();
        if (list.Count == 0) return Array.Empty<Candle>();

        var symbol = BarMath.RequireSingleSymbol(list);
        long step = interval.Ticks;

        var result = new List<Candle>();
        var current = new List<MarketTick>();
        long currentBucket = BucketOf(list[0].Timestamp, step);

        foreach (var t in list)
        {
            long bucket = BucketOf(t.Timestamp, step);
            if (bucket != currentBucket && current.Count > 0)
            {
                result.Add(BuildBar(symbol, currentBucket, step, current));
                current = new List<MarketTick>();
                currentBucket = bucket;
            }
            else if (current.Count == 0)
            {
                currentBucket = bucket;
            }
            current.Add(t);
        }
        if (current.Count > 0)
            result.Add(BuildBar(symbol, currentBucket, step, current));

        return result;
    }

    private static long BucketOf(DateTimeOffset ts, long step) => ts.UtcTicks / step;

    private static Candle BuildBar(string symbol, long bucket, long step, List<MarketTick> bar)
    {
        var openTime = new DateTimeOffset(bucket * step, TimeSpan.Zero);
        var closeTime = openTime + TimeSpan.FromTicks(step);
        return BarMath.BuildCandle(symbol, openTime, closeTime, bar);
    }
}
