namespace TradingBot.Backtesting;

/// <summary>Endzustand eines Backtest-Laufs.</summary>
public enum BacktestRunStatus
{
    NotStarted = 0,
    Running = 1,
    Completed = 2,
    Cancelled = 3,
    Failed = 4
}
