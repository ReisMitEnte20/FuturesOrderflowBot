using TradingBot.Backtesting;
using TradingBot.Domain.Enums;
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

    /// <summary>Lauf-Diagnostik (offene Position/Pending-Orders am Datenende, Signal-/Order-Zaehler). Optional.</summary>
    public StrategyRunDiagnostics? Diagnostics { get; init; }

    // Optional, wenn die jeweilige Analyse gelaufen ist:
    public MonteCarloResult? MonteCarlo { get; init; }
    public OverfittingReport? Robustness { get; init; }
}

/// <summary>Sichtbarmachung des Laufendes: offene Restposition und ausstehende Orders bleiben im Ergebnis.</summary>
public sealed record StrategyRunDiagnostics
{
    public string Status { get; init; } = string.Empty;
    public PositionSide FinalPositionSide { get; init; } = PositionSide.Flat;
    public int FinalPositionQuantity { get; init; }
    public decimal FinalPositionAvgPrice { get; init; }
    public decimal FinalPositionUnrealizedGross { get; init; }
    public int SignalsGenerated { get; init; }
    public int OrdersSubmitted { get; init; }
    public int OrdersRejectedByRisk { get; init; }
    public int OrdersRejectedByBroker { get; init; }
    public int OrdersUnfilledAtEnd { get; init; }
}
