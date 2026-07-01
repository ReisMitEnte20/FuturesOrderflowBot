using TradingBot.Domain.Enums;

namespace TradingBot.Backtesting;

/// <summary>
/// Ein abgeschlossener Round-Turn-Trade (flat → flat bzw. Flip) im Backtest.
/// GrossPnL vor Gebühren, NetPnL nach Gebühren; NetPnL == GrossPnL − Fees per Konstruktion.
/// EntryPrice ist der Average Entry bei Trade-Eröffnung.
/// </summary>
public sealed record BacktestTrade
{
    public required string Symbol { get; init; }
    public PositionSide Side { get; init; }
    public int Quantity { get; init; }

    public DateTimeOffset EntryTime { get; init; }
    public DateTimeOffset ExitTime { get; init; }
    public decimal EntryPrice { get; init; }
    public decimal ExitPrice { get; init; }

    public decimal GrossPnL { get; init; }
    public decimal Fees { get; init; }
    public decimal NetPnL { get; init; }

    public bool IsWinner => NetPnL > 0m;
    public bool IsLoser => NetPnL < 0m;
    public bool IsBreakEven => NetPnL == 0m;
}
