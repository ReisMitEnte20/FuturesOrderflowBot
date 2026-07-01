namespace TradingBot.Backtesting;

/// <summary>
/// Aggregierte Kennzahlen eines Backtests. Geld/PnL durchgängig <see cref="decimal"/>.
/// GrossProfit = Σ GrossPnL, NetProfit = GrossProfit − TotalFees. Drawdown auf NetPnL.
/// TotalSlippage ist informativ (bereits in den Fill-Preisen/GrossPnL enthalten, NICHT doppelt abgezogen).
/// </summary>
public sealed record BacktestStatistics
{
    public int TotalTrades { get; init; }
    public int WinningTrades { get; init; }
    public int LosingTrades { get; init; }
    public int BreakEvenTrades { get; init; }

    public decimal GrossProfit { get; init; }
    public decimal NetProfit { get; init; }
    public decimal TotalFees { get; init; }
    public decimal TotalSlippage { get; init; }

    public decimal MaxDrawdown { get; init; }
    /// <summary>Σ Gewinner-Net / |Σ Verlierer-Net|; null wenn keine Verlierer (undefiniert).</summary>
    public decimal? ProfitFactor { get; init; }
    public decimal WinRate { get; init; }            // 0..1

    public decimal AverageWinner { get; init; }
    public decimal AverageLoser { get; init; }       // <= 0
    public decimal Expectancy { get; init; }         // NetProfit / TotalTrades
    public decimal AverageNetPnLPerTrade { get; init; }

    public int MaxLosingStreak { get; init; }
    public int MaxWinningStreak { get; init; }
    public decimal TradesPerDay { get; init; }

    public decimal LargestWin { get; init; }
    public decimal LargestLoss { get; init; }        // <= 0

    public static readonly BacktestStatistics Empty = new();
}
