namespace TradingBot.Domain.Models;

/// <summary>
/// Orderflow-angereicherte Bar mit Bid-/Ask-Volumen und Delta.
/// Grundlage für Orderflow-Konzepte (Delta-Divergenz, Absorption, CVD …).
/// Die eigentliche Strategie-Logik kommt erst in späteren Phasen.
/// </summary>
public sealed record OrderFlowBar
{
    public required string Symbol { get; init; }
    public DateTimeOffset OpenTime { get; init; }
    public DateTimeOffset CloseTime { get; init; }

    public decimal Open { get; init; }
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public decimal Close { get; init; }

    public decimal TotalVolume { get; init; }

    /// <summary>An den Bid gehandeltes Volumen (aggressive Verkäufer).</summary>
    public decimal BidVolume { get; init; }

    /// <summary>An den Ask gehandeltes Volumen (aggressive Käufer).</summary>
    public decimal AskVolume { get; init; }

    /// <summary>Delta dieser Bar (Ask- minus Bid-Volumen). Berechnete Eigenschaft.</summary>
    public decimal Delta => AskVolume - BidVolume;

    /// <summary>Kumulatives Delta bis einschließlich dieser Bar (vom Aggregator gesetzt).</summary>
    public decimal CumulativeDelta { get; init; }

    public bool IsBullish => Close > Open;
}
