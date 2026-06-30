using TradingBot.Domain.Models;

namespace TradingBot.Application.MarketData;

/// <summary>
/// Aggregiert Ticks zu Volumen-Bars: eine Bar wird geschlossen, sobald ihr kumuliertes Volumen
/// die Schwelle erreicht/überschreitet. Der überschreitende Tick gehört VOLLSTÄNDIG zur
/// aktuellen Bar (kein Aufteilen eines einzelnen Trades). Eine restliche letzte Bar wird aufgenommen.
/// </summary>
public static class VolumeBarAggregator
{
    public static IReadOnlyList<Candle> Aggregate(IEnumerable<MarketTick> ticks, decimal volumePerBar)
    {
        ArgumentNullException.ThrowIfNull(ticks);
        if (volumePerBar <= 0m)
            throw new ArgumentOutOfRangeException(nameof(volumePerBar), "volumePerBar muss > 0 sein.");

        var list = ticks as IReadOnlyList<MarketTick> ?? ticks.ToList();
        if (list.Count == 0) return Array.Empty<Candle>();

        var symbol = BarMath.RequireSingleSymbol(list);
        var result = new List<Candle>();

        var current = new List<MarketTick>();
        decimal acc = 0m;

        foreach (var t in list)
        {
            current.Add(t);
            acc += t.Volume;
            if (acc >= volumePerBar)
            {
                result.Add(BarMath.BuildCandle(symbol, current[0].Timestamp, current[^1].Timestamp, current));
                current = new List<MarketTick>();
                acc = 0m;
            }
        }
        if (current.Count > 0)
            result.Add(BarMath.BuildCandle(symbol, current[0].Timestamp, current[^1].Timestamp, current));

        return result;
    }
}
