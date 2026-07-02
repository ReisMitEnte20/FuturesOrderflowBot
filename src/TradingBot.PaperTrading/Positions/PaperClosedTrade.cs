using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.PaperTrading.Positions;

/// <summary>
/// Ein abgeschlossener Round-Turn-Trade (flat → flat bzw. Flip) in der Paper-Session.
/// GrossPnL vor Gebühren, NetPnL nach Gebühren; NetPnL == GrossPnL − Fees.TotalFees per Konstruktion.
/// </summary>
public sealed record PaperClosedTrade
{
    public required string Symbol { get; init; }
    public PositionSide Side { get; init; }
    public int Quantity { get; init; }

    public DateTimeOffset EntryTime { get; init; }
    public DateTimeOffset ExitTime { get; init; }
    public decimal EntryPrice { get; init; }
    public decimal ExitPrice { get; init; }

    public decimal GrossPnL { get; init; }
    public FeeBreakdown Fees { get; init; } = FeeBreakdown.Zero;
    public decimal NetPnL { get; init; }

    public bool IsWinner => NetPnL > 0m;
    public bool IsLoser => NetPnL < 0m;
}
