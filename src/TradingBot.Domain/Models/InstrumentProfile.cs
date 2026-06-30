namespace TradingBot.Domain.Models;

/// <summary>
/// Kontrakt-/Instrument-Spezifikation. Trennt internes <see cref="Symbol"/> vom
/// broker-spezifischen <see cref="BrokerSymbol"/>. Tick-Werte kommen ausschließlich
/// aus der Config – niemals hardcoded.
/// </summary>
public sealed record InstrumentProfile
{
    public required string Symbol { get; init; }
    public required string BrokerSymbol { get; init; }
    public required string Exchange { get; init; }
    public string Currency { get; init; } = "USD";

    /// <summary>Kleinste Preisbewegung (z. B. 0.25 für NQ).</summary>
    public decimal TickSize { get; init; }

    /// <summary>Geldwert eines Ticks pro Kontrakt.</summary>
    public decimal TickValue { get; init; }

    /// <summary>Geldwert eines vollen Punkts pro Kontrakt.</summary>
    public decimal PointValue { get; init; }

    public decimal ContractMultiplier { get; init; }

    public string TradingTimezone { get; init; } = "UTC";
    public TimeOnly SessionStart { get; init; }
    public TimeOnly SessionEnd { get; init; }

    public int MaxContracts { get; init; }
    public int DefaultStopLossTicks { get; init; }
    public int DefaultTakeProfitTicks { get; init; }
    public int DefaultTrailingStopTicks { get; init; }
}
