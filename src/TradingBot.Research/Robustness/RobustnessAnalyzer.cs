using TradingBot.Backtesting;
using TradingBot.Domain.Enums;
using TradingBot.Research.MonteCarlo;

namespace TradingBot.Research.Robustness;

/// <summary>Schwellen der Robustheitsprüfung (überschreibbar, keine Magic Numbers).</summary>
public sealed record RobustnessThresholds
{
    public int MinTrades { get; init; } = 30;
    public decimal TopWinnersShareWarn { get; init; } = 0.5m;   // Top 3 Trades > 50 % des Netto → Warnung
    public int TopWinnersCount { get; init; } = 3;
    public int MaxLosingStreakWarn { get; init; } = 6;
    public decimal DrawdownToNetWarn { get; init; } = 1.0m;     // MaxDD > NetProfit → Warnung
    public decimal WalkForwardEfficiencyWarn { get; init; } = 0.5m;
    public decimal MonteCarloWorstDdToNetWarn { get; init; } = 2.0m; // MC-Worst-DD > 2× NetProfit → Warnung
}

/// <summary>
/// Erkennt typische Overfitting-/Robustheitsprobleme aus Trades + Metriken + optionalem
/// Monte-Carlo/OOS-Kontext. Meldet Befunde, verkauft aber nichts als "Wahrheit"
/// (der resultierende <see cref="RobustnessScore"/> ist eine Hilfsmetrik).
/// </summary>
public sealed class RobustnessAnalyzer
{
    private readonly RobustnessThresholds _t;

    public RobustnessAnalyzer(RobustnessThresholds? thresholds = null) => _t = thresholds ?? new RobustnessThresholds();

    public OverfittingReport Analyze(
        ResearchMetricSet metrics,
        IReadOnlyList<BacktestTrade>? trades = null,
        MonteCarloResult? monteCarlo = null,
        decimal? slippageBreakEvenNet = null)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        var findings = new List<RobustnessFinding>();

        if (!metrics.DataQualityOk)
            findings.Add(Error("PoorDataQuality", "Datensatz hat Qualitätsfehler – Ergebnisse unzuverlässig."));
        if (!metrics.CapabilitiesSufficient)
            findings.Add(Error("InsufficientCapabilities",
                "Datenlevel deckt die benötigte Orderflow-Analyse nicht ab (z. B. kein Footprint/Aggressor)."));

        if (metrics.TradeCount < _t.MinTrades)
            findings.Add(Warning("TooFewTrades",
                $"Nur {metrics.TradeCount} Trades (< {_t.MinTrades}) – statistisch wenig aussagekräftig."));

        if (trades is { Count: > 0 } && metrics.NetProfit > 0m)
        {
            var topWinners = trades.Where(t => t.NetPnL > 0m)
                .OrderByDescending(t => t.NetPnL).Take(_t.TopWinnersCount).Sum(t => t.NetPnL);
            if (topWinners > _t.TopWinnersShareWarn * metrics.NetProfit)
                findings.Add(Warning("ConcentratedInFewWinners",
                    $"Top {_t.TopWinnersCount} Gewinner tragen {topWinners:F2} von {metrics.NetProfit:F2} NetPnL " +
                    $"(> {_t.TopWinnersShareWarn:P0})."));
        }

        if (metrics.MaxLosingStreak >= _t.MaxLosingStreakWarn)
            findings.Add(Warning("HighLosingStreak",
                $"Max. Verlustserie {metrics.MaxLosingStreak} (≥ {_t.MaxLosingStreakWarn})."));

        if (metrics.ProfitFactor is > 1.5m && metrics.NetProfit > 0m
            && metrics.MaxDrawdown > _t.DrawdownToNetWarn * metrics.NetProfit)
            findings.Add(Warning("GoodPfButHighDrawdown",
                $"Profit Factor gut ({metrics.ProfitFactor}), aber MaxDrawdown {metrics.MaxDrawdown:F2} " +
                $"> NetProfit {metrics.NetProfit:F2}."));

        if (slippageBreakEvenNet is decimal breakEven && metrics.NetProfit > 0m && breakEven <= 0m)
            findings.Add(Error("FragileToSlippage",
                "NetPnL verschwindet bereits bei moderat höherer Slippage."));

        if (metrics.WalkForwardEfficiency is decimal wfe && wfe < _t.WalkForwardEfficiencyWarn)
            findings.Add(Warning("LowWalkForwardEfficiency",
                $"Walk-Forward-Efficiency {wfe:F2} < {_t.WalkForwardEfficiencyWarn} (OOS deutlich schlechter als IS)."));

        if (metrics.OutOfSampleNetProfit is decimal oos && metrics.NetProfit > 0m && oos <= 0m)
            findings.Add(Error("OosNegativeWhileIsPositive",
                "In-Sample profitabel, Out-of-Sample nicht – klassisches Overfitting-Signal."));

        if (metrics.ParameterStability is decimal stab && stab < 0.5m)
            findings.Add(Warning("NarrowParameterRobustness",
                $"Parameter nur in engem Bereich gut (Stabilität {stab:F2} < 0.5)."));

        var worstDd = monteCarlo?.Statistics.WorstDrawdown5Percent ?? metrics.MonteCarloWorstDrawdown5;
        if (worstDd is decimal mcDd && metrics.NetProfit > 0m
            && mcDd > _t.MonteCarloWorstDdToNetWarn * metrics.NetProfit)
            findings.Add(Warning("MonteCarloWorstCaseTooBad",
                $"Monte-Carlo-Worst-5%-Drawdown {mcDd:F2} > {_t.MonteCarloWorstDdToNetWarn}× NetProfit."));

        return new OverfittingReport { Findings = findings, Score = RobustnessScore.FromFindings(findings) };
    }

    private static RobustnessFinding Error(string code, string message)
        => new() { Severity = DataQualitySeverity.Error, Code = code, Message = message };
    private static RobustnessFinding Warning(string code, string message)
        => new() { Severity = DataQualitySeverity.Warning, Code = code, Message = message };
}
