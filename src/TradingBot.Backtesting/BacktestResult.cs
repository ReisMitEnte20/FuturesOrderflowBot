using TradingBot.Domain.Models;

namespace TradingBot.Backtesting;

/// <summary>Ergebnis eines Backtest-Laufs.</summary>
public sealed record BacktestResult
{
    public BacktestRunStatus Status { get; init; } = BacktestRunStatus.NotStarted;
    public string? Message { get; init; }

    public BacktestStatistics Statistics { get; init; } = BacktestStatistics.Empty;
    public IReadOnlyList<BacktestTrade> Trades { get; init; } = Array.Empty<BacktestTrade>();

    public int TicksProcessed { get; init; }
    public int SignalsGenerated { get; init; }
    public int OrdersSubmitted { get; init; }
    public int OrdersRejectedByRisk { get; init; }
    public int OrdersRejectedByBroker { get; init; }
    public int OrdersUnfilledAtEnd { get; init; }

    /// <summary>Feed-Zustand am Ende (informativ, aus dem FeedHealthMonitor).</summary>
    public MarketDataConnectionState FinalFeedState { get; init; } = MarketDataConnectionState.Unknown;
}
