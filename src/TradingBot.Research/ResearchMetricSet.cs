using TradingBot.Backtesting;

namespace TradingBot.Research;

/// <summary>
/// Normalisiertes Kennzahlen-Bündel einer Strategie-Auswertung – Basis für Ranking/Robustness.
/// Alles NetPnL-basiert (nach Fees/Slippage). Optionale Felder (MonteCarlo/OOS/Stabilität)
/// werden ergänzt, wenn die jeweilige Analyse gelaufen ist.
/// </summary>
public sealed record ResearchMetricSet
{
    public int TradeCount { get; init; }
    public decimal NetProfit { get; init; }
    public decimal GrossProfit { get; init; }
    public decimal TotalFees { get; init; }
    public decimal TotalSlippage { get; init; }
    public decimal MaxDrawdown { get; init; }          // ≥ 0
    public decimal? ProfitFactor { get; init; }
    public decimal Expectancy { get; init; }
    public decimal AverageNetPnLPerTrade { get; init; }
    public decimal WinRate { get; init; }              // 0..1
    public int MaxLosingStreak { get; init; }

    // Optional aus weiteren Analysen:
    public decimal? MonteCarloWorstDrawdown5 { get; init; }
    public decimal? MonteCarloMedianNetProfit { get; init; }
    public decimal? MonteCarloProbabilityOfLoss { get; init; }
    public decimal? OutOfSampleNetProfit { get; init; }
    public decimal? WalkForwardEfficiency { get; init; }
    public decimal? ParameterStability { get; init; }  // 0..1, höher = stabiler

    // Datenbasis:
    public bool DataQualityOk { get; init; } = true;
    public bool CapabilitiesSufficient { get; init; } = true;

    public static ResearchMetricSet FromBacktest(
        BacktestStatistics s, bool dataQualityOk = true, bool capabilitiesSufficient = true) => new()
    {
        TradeCount = s.TotalTrades,
        NetProfit = s.NetProfit,
        GrossProfit = s.GrossProfit,
        TotalFees = s.TotalFees,
        TotalSlippage = s.TotalSlippage,
        MaxDrawdown = s.MaxDrawdown,
        ProfitFactor = s.ProfitFactor,
        Expectancy = s.Expectancy,
        AverageNetPnLPerTrade = s.AverageNetPnLPerTrade,
        WinRate = s.WinRate,
        MaxLosingStreak = s.MaxLosingStreak,
        DataQualityOk = dataQualityOk,
        CapabilitiesSufficient = capabilitiesSufficient
    };
}
