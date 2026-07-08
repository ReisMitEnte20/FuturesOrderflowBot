namespace TradingBot.Research.Ranking;

/// <summary>
/// Gewichte des Strategie-Rankings. Bewusst so gesetzt, dass ROBUSTHEIT (Drawdown, Monte-Carlo-
/// Worst-Case, Out-of-Sample, Parameter-Stabilität = 0.55) höher zählt als roher NetProfit (0.15).
/// So kann eine weniger profitable, aber stabilere Strategie höher ranken als eine überoptimierte.
/// Winrate ist bewusst niedrig gewichtet. Summe = 1.0.
/// </summary>
public sealed record RankingWeights
{
    public decimal NetProfit { get; init; } = 0.15m;
    public decimal MaxDrawdown { get; init; } = 0.15m;         // niedriger = besser
    public decimal ProfitFactor { get; init; } = 0.10m;
    public decimal Expectancy { get; init; } = 0.10m;
    public decimal AverageNetPnLPerTrade { get; init; } = 0.05m;
    public decimal WinRate { get; init; } = 0.05m;
    public decimal MonteCarloWorstDrawdown { get; init; } = 0.15m; // niedriger = besser
    public decimal OutOfSampleNetProfit { get; init; } = 0.15m;
    public decimal ParameterStability { get; init; } = 0.10m;

    /// <summary>Minimale Trade-Anzahl; darunter wird der Score stark abgewertet.</summary>
    public int MinTrades { get; init; } = 30;

    public static readonly RankingWeights Default = new();
}

/// <summary>Ein gerankter Strategie-Eintrag.</summary>
public sealed record StrategyRankedResult
{
    public int Rank { get; init; }                    // 1-basiert
    public required string StrategyName { get; init; }
    public decimal CompositeScore { get; init; }      // 0..1 (nach Penalty)
    public required ResearchMetricSet Metrics { get; init; }
    public IReadOnlyList<string> Penalties { get; init; } = Array.Empty<string>();
}
