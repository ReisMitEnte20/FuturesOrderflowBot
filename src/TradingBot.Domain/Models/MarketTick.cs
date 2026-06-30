namespace TradingBot.Domain.Models;

/// <summary>Einzelner Markt-Tick (Trade/Quote-Snapshot).</summary>
public sealed record MarketTick
{
    public required string Symbol { get; init; }
    public DateTimeOffset Timestamp { get; init; }

    public decimal Price { get; init; }
    public decimal Bid { get; init; }
    public decimal Ask { get; init; }

    public decimal BidSize { get; init; }
    public decimal AskSize { get; init; }

    /// <summary>Gehandeltes Volumen dieses Ticks.</summary>
    public decimal Volume { get; init; }

    /// <summary>Mittelpreis aus Bid/Ask. Einfache berechnete Eigenschaft.</summary>
    public decimal Mid => (Bid + Ask) / 2m;
}
