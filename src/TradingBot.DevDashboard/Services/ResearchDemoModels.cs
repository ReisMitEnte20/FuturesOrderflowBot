using TradingBot.Research;
using TradingBot.Research.MonteCarlo;
using TradingBot.Research.Ranking;
using TradingBot.Research.Robustness;
using TradingBot.Research.Sensitivity;
using TradingBot.Research.WalkForward;

namespace TradingBot.DevDashboard.Services;

/// <summary>
/// Read-only View-Model für das Research Dashboard (Phase 12D). Bündelt die Ergebnisse einer
/// DETERMINISTISCHEN Demo über die echten <c>TradingBot.Research</c>-Klassen. Enthält KEINE
/// Live-/Broker-/Order-Daten — alle Zahlen stammen aus künstlichen, klar als DEMO markierten
/// Trade-Listen (siehe <see cref="ResearchDemoService"/>). Kein Anspruch auf echte Performance.
/// </summary>
public sealed record ResearchDashboardData
{
    /// <summary>Strategie-Sichten in Ranking-Reihenfolge (Rang 1 zuerst).</summary>
    public required IReadOnlyList<ResearchStrategyView> Strategies { get; init; }

    /// <summary>Gesamtranking (aus <see cref="StrategyRankingService"/>).</summary>
    public required IReadOnlyList<StrategyRankedResult> Ranking { get; init; }

    /// <summary>Bestplatzierter Kandidat (Rang 1) — Grundlage der Summary-Cards/Detailbereiche.</summary>
    public required ResearchStrategyView Best { get; init; }

    // Kontext der Monte-Carlo-Demo (für Anzeige):
    public int MonteCarloSimulations { get; init; }
    public int MonteCarloSeed { get; init; }
    public decimal MonteCarloDrawdownThreshold { get; init; }

    /// <summary>Findet die Strategie-Sicht zu einem Ranking-Eintrag (per Name).</summary>
    public ResearchStrategyView ViewFor(StrategyRankedResult ranked)
        => Strategies.First(s => s.Name == ranked.StrategyName);
}

/// <summary>Alle Demo-Auswertungen EINER Strategie, gebündelt für die Anzeige.</summary>
public sealed record ResearchStrategyView
{
    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary>Backtest + Metriken + Monte Carlo + Robustness (aus echten Research-Klassen).</summary>
    public required StrategyRunResult Run { get; init; }

    /// <summary>Walk-Forward-Demo (IS/OOS-Segmente, WFE, Overfitting-Verdacht).</summary>
    public required WalkForwardResult WalkForward { get; init; }

    /// <summary>Slippage-Sensitivität (NetPnL bei steigender Slippage).</summary>
    public required SlippageSensitivityResult Slippage { get; init; }

    /// <summary>Fee-Sensitivität (NetPnL bei steigenden Gebühren).</summary>
    public required FeeSensitivityResult Fees { get; init; }

    /// <summary>Kumulierte NetPnL-Equity-Kurve (je abgeschlossenem Trade).</summary>
    public required IReadOnlyList<decimal> EquityCurve { get; init; }

    /// <summary>Drawdown je Punkt der Equity-Kurve (≥ 0).</summary>
    public required IReadOnlyList<decimal> DrawdownCurve { get; init; }

    /// <summary>1-basierter Rang aus dem Gesamtranking.</summary>
    public int Rank { get; init; }

    // Bequeme Kurzzugriffe für die Razor-Seite:
    public ResearchMetricSet Metrics => Run.Metrics;
    public MonteCarloResult? MonteCarlo => Run.MonteCarlo;
    public OverfittingReport? Robustness => Run.Robustness;

    /// <summary>Anzahl der Robustheits-/Overfitting-Warnungen (0, wenn keine Analyse lief).</summary>
    public int WarningCount => Robustness?.Findings.Count ?? 0;

    /// <summary>Höhe des Equity-Kurven-Endwerts (== NetProfit).</summary>
    public decimal FinalEquity => EquityCurve.Count > 0 ? EquityCurve[^1] : 0m;
}
