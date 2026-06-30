using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models;

/// <summary>
/// Abgeschlossener Trade (Entry bis Exit). GrossPnL = vor Gebühren, NetPnL = nach Gebühren.
/// </summary>
public sealed record Trade
{
    public Guid TradeId { get; init; } = Guid.NewGuid();
    public required string AccountId { get; init; }
    public required string Symbol { get; init; }

    /// <summary>Richtung des Trades (Long/Short).</summary>
    public PositionSide Side { get; init; }
    public int Quantity { get; init; }

    public decimal EntryPrice { get; init; }
    public decimal ExitPrice { get; init; }
    public DateTimeOffset EntryTime { get; init; }
    public DateTimeOffset ExitTime { get; init; }

    public decimal GrossPnL { get; init; }
    public decimal NetPnL { get; init; }
    public FeeBreakdown Fees { get; init; } = FeeBreakdown.Zero;

    public string StrategyName { get; init; } = string.Empty;
    public Guid? SourceSignalId { get; init; }

    public bool IsWinner => NetPnL > 0m;
    public TimeSpan Duration => ExitTime - EntryTime;
}
