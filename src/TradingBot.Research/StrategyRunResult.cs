using TradingBot.Backtesting;
using TradingBot.Domain.Models;
using TradingBot.Research.MonteCarlo;
using TradingBot.Research.Robustness;

namespace TradingBot.Research;

/// <summary>Ergebnis EINES Backtest-Laufs einer Strategie-Konfiguration im Research-Layer.</summary>
public sealed record StrategyRunResult
{
    public required string StrategyName { get; init; }
    public required StrategyConfig Config { get; init; }
    public required BacktestStatistics Statistics { get; init; }
    public IReadOnlyList<BacktestTrade> Trades { get; init; } = Array.Empty<BacktestTrade>();
    public required ResearchMetricSet Metrics { get; init; }

    // Optional, wenn die jeweilige Analyse gelaufen ist:
    public MonteCarloResult? MonteCarlo { get; init; }
    public OverfittingReport? Robustness { get; init; }
}
