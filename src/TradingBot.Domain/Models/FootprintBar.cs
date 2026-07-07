namespace TradingBot.Domain.Models;

/// <summary>
/// Ein Preislevel innerhalb einer Footprint-Bar: echtes Bid-/Ask-Volumen AN diesem Preis.
/// Grundlage für Stacked-Imbalance-Analysen (nur mit echten Footprint-Daten).
/// </summary>
public sealed record FootprintPriceLevel
{
    public decimal PriceLevel { get; init; }
    public decimal BidVolumeAtPrice { get; init; }
    public decimal AskVolumeAtPrice { get; init; }

    /// <summary>Gesamtvolumen am Level (aus Import; typisch Bid+Ask).</summary>
    public decimal TotalVolumeAtPrice { get; init; }

    public decimal DeltaAtPrice => AskVolumeAtPrice - BidVolumeAtPrice;

    /// <summary>Optionales, vom Datenlieferanten berechnetes Imbalance-Verhältnis.</summary>
    public decimal? ImbalanceRatio { get; init; }

    /// <summary>Optionales Flag des Datenlieferanten (nicht selbst erfunden).</summary>
    public bool? IsStackedImbalance { get; init; }
}

/// <summary>
/// Footprint-Bar: Orderflow-Bar MIT Bid/Ask-Volumen je Preislevel. OHLC ist optional
/// (0, wenn der Export sie nicht liefert – dann als Warning im Quality-Report vermerkt).
/// </summary>
public sealed record FootprintBar
{
    public required string Symbol { get; init; }
    public DateTimeOffset OpenTime { get; init; }
    public DateTimeOffset CloseTime { get; init; }

    public decimal Open { get; init; }
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public decimal Close { get; init; }

    public decimal TotalVolume { get; init; }
    public decimal BidVolume { get; init; }
    public decimal AskVolume { get; init; }
    public decimal Delta => AskVolume - BidVolume;
    public decimal CumulativeDelta { get; init; }

    public IReadOnlyList<FootprintPriceLevel> Levels { get; init; } = Array.Empty<FootprintPriceLevel>();
}
