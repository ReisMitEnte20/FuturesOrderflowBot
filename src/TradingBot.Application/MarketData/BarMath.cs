using TradingBot.Domain.Models;

namespace TradingBot.Application.MarketData;

/// <summary>Geteilte Hilfslogik für die Bar-Aggregation (OHLCV, Symbol-Prüfung).</summary>
internal static class BarMath
{
    /// <summary>Stellt sicher, dass alle Ticks zum selben Symbol gehören; liefert dieses Symbol.</summary>
    public static string RequireSingleSymbol(IReadOnlyList<MarketTick> ticks)
    {
        var symbol = ticks[0].Symbol;
        for (int i = 1; i < ticks.Count; i++)
            if (!string.Equals(ticks[i].Symbol, symbol, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Aggregation erwartet Ticks eines einzigen Symbols.", nameof(ticks));
        return symbol;
    }

    /// <summary>Baut eine OHLCV-Kerze aus einer nicht-leeren, chronologischen Tick-Gruppe.</summary>
    public static Candle BuildCandle(string symbol, DateTimeOffset openTime, DateTimeOffset closeTime, IReadOnlyList<MarketTick> bar)
    {
        decimal high = bar[0].Price, low = bar[0].Price, volume = 0m;
        foreach (var t in bar)
        {
            if (t.Price > high) high = t.Price;
            if (t.Price < low) low = t.Price;
            volume += t.Volume;
        }

        return new Candle
        {
            Symbol = symbol,
            OpenTime = openTime,
            CloseTime = closeTime,
            Open = bar[0].Price,
            High = high,
            Low = low,
            Close = bar[^1].Price,
            Volume = volume
        };
    }
}
