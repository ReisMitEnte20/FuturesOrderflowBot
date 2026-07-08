using TradingBot.Backtesting;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Research;
using TradingBot.Research.MonteCarlo;
using TradingBot.Research.Ranking;
using TradingBot.Research.Robustness;
using TradingBot.Research.Sensitivity;
using TradingBot.Research.WalkForward;

namespace TradingBot.DevDashboard.Services;

/// <summary>
/// RESEARCH / SIMULATION ONLY. Erzeugt eine DETERMINISTISCHE Demo-Auswertung für das Research
/// Dashboard (Phase 12D) über die echten <c>TradingBot.Research</c>-Klassen
/// (<see cref="MonteCarloSimulator"/>, <see cref="StrategyRankingService"/>,
/// <see cref="RobustnessAnalyzer"/>, <see cref="BacktestStatisticsCalculator"/>).
///
/// WICHTIG:
/// - Keine Broker-API, keine Live-Execution, keine Netzwerkcalls, keine echten Orders.
/// - Der Service referenziert NICHT <c>TradingBot.Execution</c>.
/// - Alle Zahlen stammen aus KÜNSTLICHEN, klar als DEMO markierten Trade-Listen (fester Seed),
///   NICHT aus echten Backtests — kein Anspruch auf reale Strategie-Performance.
/// - Read-only: berechnet einmal, cached das Ergebnis; keine Buttons/Controls, keine Side-Effects.
/// </summary>
public sealed class ResearchDemoService
{
    // --- Demo-Konstanten (klar als Annahmen markiert, KEINE echten Broker-Gebühren/Tick-Werte) ---
    private const int MonteCarloSims = 1000;
    private const int MonteCarloSeed = 12345;               // deterministisch (siehe MonteCarloSimulator)
    private const decimal DemoFeePerTrade = 4.20m;          // Demo-Kostenannahme je Round-Turn (kein Config-Wert)
    private const decimal DemoSlippageUsdPerTick = 0.50m;   // Demo-Kostenannahme je Tick/Seite
    private const decimal WalkForwardEfficiencyWarn = 0.5m; // identisch zur WalkForwardAnalyzer-Schwelle

    // Slippage-Level (zusätzliche Ticks) und Fee-Multiplikatoren der Sensitivitäts-Demo:
    private static readonly decimal[] SlippageTickLevels = { 0m, 1m, 2m, 3m, 4m, 6m };
    private static readonly decimal[] FeeMultipliers = { 1.0m, 1.25m, 1.5m, 2.0m, 3.0m };

    private readonly MonteCarloSimulator _monteCarlo = new();
    private readonly RobustnessAnalyzer _robustness = new();
    private readonly StrategyRankingService _ranking = new();

    private readonly object _sync = new();
    private ResearchDashboardData? _cached;

    /// <summary>
    /// Liefert die (gecachte) deterministische Demo-Auswertung. Mehrfachaufrufe geben dasselbe
    /// Ergebnis (gleicher Seed → gleiche Zahlen).
    /// </summary>
    public ResearchDashboardData GetDemoData()
    {
        lock (_sync)
            return _cached ??= Build();
    }

    private ResearchDashboardData Build()
    {
        var shapes = DemoShapes();

        // 1) Je Kandidat: Trades erzeugen → echte Statistik/Metriken → Walk-Forward-Demo →
        //    Monte Carlo (echt) → Robustness (echt) → Sensitivitäts-Demo.
        var views = new List<ResearchStrategyView>();
        foreach (var shape in shapes)
            views.Add(BuildStrategyView(shape));

        // 2) Gesamtranking über die echten Metriken (echter StrategyRankingService).
        var ranking = _ranking.Rank(views.Select(v => (v.Name, v.Metrics)).ToList());
        var rankByName = ranking.ToDictionary(r => r.StrategyName, r => r.Rank);

        // Rang zuweisen und in Ranking-Reihenfolge sortieren.
        var ranked = views
            .Select(v => v with { Rank = rankByName[v.Name] })
            .OrderBy(v => v.Rank)
            .ToList();

        // Monte-Carlo-Drawdown-Schwelle (Demo-X) für "Probability of Drawdown > X": aus dem
        // Backtest-Drawdown des Bestkandidaten abgeleitet, damit die Zahl zum Kandidaten passt.
        var best = ranked[0];
        decimal ddThreshold = Math.Round(best.Metrics.MaxDrawdown * 1.5m, 0);

        return new ResearchDashboardData
        {
            Strategies = ranked,
            Ranking = ranking,
            Best = best,
            MonteCarloSimulations = MonteCarloSims,
            MonteCarloSeed = MonteCarloSeed,
            MonteCarloDrawdownThreshold = ddThreshold
        };
    }

    private ResearchStrategyView BuildStrategyView(DemoStrategyShape shape)
    {
        // --- Haupt-"Backtest" (Demo-Trades, deterministisch) ---
        var trades = GenerateTrades(shape.Seed, shape.TradeCount, shape.WinRate, shape.AvgWin, shape.AvgLoss);
        var stats = BacktestStatisticsCalculator.Compute(trades, totalSlippage: 0m);
        var baseMetrics = ResearchMetricSet.FromBacktest(stats, shape.DataQualityOk, shape.CapabilitiesSufficient);

        // --- Walk-Forward-Demo (echte Fenster + echte Statistik pro Segment) ---
        var walkForward = BuildWalkForward(shape);

        // --- Monte Carlo (ECHTER Simulator auf den NetPnLs) ---
        var netPnls = trades.Select(t => t.NetPnL).ToList();
        decimal ddThreshold = Math.Round(stats.MaxDrawdown * 1.5m, 0);
        // Bootstrap (Ziehen MIT Zurücklegen): Endgewinn UND Drawdown variieren → aussagekräftige
        // Probability-of-Loss / Confidence-Interval-Werte (Reshuffle ließe den Endgewinn konstant).
        var mc = _monteCarlo.Run(new MonteCarloRequest
        {
            TradeNetPnLs = netPnls,
            Simulations = MonteCarloSims,
            Seed = MonteCarloSeed,
            Method = MonteCarloMethod.BootstrapWithReplacement,
            DrawdownThreshold = ddThreshold > 0m ? ddThreshold : null
        });

        // Metriken um Monte-Carlo- + Walk-Forward-Ergebnisse anreichern (wie in der ResearchEngine).
        var metrics = baseMetrics with
        {
            MonteCarloWorstDrawdown5 = mc.Statistics.WorstDrawdown5Percent,
            MonteCarloMedianNetProfit = mc.Statistics.MedianNetProfit,
            MonteCarloProbabilityOfLoss = mc.Statistics.ProbabilityOfLoss,
            OutOfSampleNetProfit = walkForward.OutOfSampleNetProfit,
            WalkForwardEfficiency = walkForward.WalkForwardEfficiency,
            ParameterStability = shape.ParameterStability
        };

        // --- Robustness/Overfitting (ECHTER Analyzer aus den angereicherten Metriken + Trades + MC) ---
        var robustness = _robustness.Analyze(metrics, trades, mc);

        // --- Sensitivitäts-Demo (Slippage/Fees) ---
        var slippage = BuildSlippageSensitivity(stats);
        var fees = BuildFeeSensitivity(stats);

        // --- Equity-/Drawdown-Kurve aus den Demo-Trades ---
        var (equity, drawdown) = BuildEquityAndDrawdown(netPnls);

        var run = new StrategyRunResult
        {
            StrategyName = shape.Name,
            Config = DemoConfig(shape),
            Statistics = stats,
            Trades = trades,
            Metrics = metrics,
            MonteCarlo = mc,
            Robustness = robustness
        };

        return new ResearchStrategyView
        {
            Name = shape.Name,
            Description = shape.Description,
            Run = run,
            WalkForward = walkForward,
            Slippage = slippage,
            Fees = fees,
            EquityCurve = equity,
            DrawdownCurve = drawdown
        };
    }

    // ---------------------------------------------------------------------------------------------
    // Demo-Trade-Erzeugung (deterministisch)
    // ---------------------------------------------------------------------------------------------

    private static readonly DateTimeOffset DemoStart = new(2025, 1, 2, 14, 30, 0, TimeSpan.Zero);

    /// <summary>
    /// Erzeugt deterministisch eine Liste künstlicher Round-Turn-Trades. Die EXAKTE Gewinnzahl
    /// (= round(count × winRate)) ist fest — nur die Reihenfolge wird per Seed gemischt (realistische
    /// Cluster/Drawdowns), plus deterministischer Betrags-Jitter. So bleibt die Kennzahl stabil und
    /// bewusst gesteuert (Demo). Fees sind eine klar markierte Demo-Kostenannahme.
    /// NetPnL == GrossPnL − Fees (Backtest-Konvention).
    /// </summary>
    private static List<BacktestTrade> GenerateTrades(
        int seed, int count, decimal winRate, decimal avgWin, decimal avgLoss)
    {
        int wins = Math.Clamp((int)Math.Round(count * (double)winRate, MidpointRounding.AwayFromZero), 0, count);
        var outcomes = new bool[count];
        for (int i = 0; i < wins; i++) outcomes[i] = true;

        var rng = new Random(seed);
        for (int i = count - 1; i > 0; i--) // Fisher-Yates: mischt Gewinner/Verlierer (deterministisch)
        {
            int j = rng.Next(i + 1);
            (outcomes[i], outcomes[j]) = (outcomes[j], outcomes[i]);
        }

        var list = new List<BacktestTrade>(count);
        for (int i = 0; i < count; i++)
        {
            decimal jitter = 0.8m + (decimal)rng.NextDouble() * 0.4m; // 0.8..1.2, deterministisch
            decimal net = Math.Round((outcomes[i] ? avgWin : avgLoss) * jitter, 2);
            list.Add(new BacktestTrade
            {
                Symbol = "MNQ",
                Side = PositionSide.Long,
                Quantity = 1,
                EntryTime = DemoStart.AddMinutes(i * 3),
                ExitTime = DemoStart.AddMinutes(i * 3 + 2),
                EntryPrice = 20000m,
                ExitPrice = 20000m + net,           // rein kosmetisch für die Anzeige
                GrossPnL = net + DemoFeePerTrade,   // Gross = Net + Fees
                Fees = DemoFeePerTrade,
                NetPnL = net
            });
        }
        return list;
    }

    // ---------------------------------------------------------------------------------------------
    // Walk-Forward-Demo: echte Fenster (WalkForwardWindows) + echte Statistik je IS/OOS-Segment.
    // Selektion/OOS-Degradation ist DEMO (künstlich), die Aggregation nutzt dieselbe WFE-Formel
    // wie der WalkForwardAnalyzer.
    // ---------------------------------------------------------------------------------------------

    private static WalkForwardResult BuildWalkForward(DemoStrategyShape shape)
    {
        // Echte Fenster-Geometrie (1200 Demo-"Ticks", IS 400 / OOS 200 / Step 200, rollend).
        var windows = WalkForwardWindows.Generate(1200, 400, 200, 200, WalkForwardMode.Rolling);
        var config = DemoConfig(shape);

        const int isTradesPerWindow = 60;
        const int oosTradesPerWindow = 30;
        decimal oosWinRate = Math.Clamp(shape.WinRate * shape.OosWinRateFactor, 0.05m, 0.95m);

        var segments = new List<WalkForwardSegmentResult>();
        foreach (var w in windows)
        {
            var isTrades = GenerateTrades(shape.Seed + 1000 + w.Index * 10, isTradesPerWindow,
                shape.WinRate, shape.AvgWin, shape.AvgLoss);
            var oosTrades = GenerateTrades(shape.Seed + 2000 + w.Index * 10, oosTradesPerWindow,
                oosWinRate, shape.AvgWin, shape.AvgLoss);

            var isMetrics = ResearchMetricSet.FromBacktest(
                BacktestStatisticsCalculator.Compute(isTrades, 0m));
            var oosMetrics = ResearchMetricSet.FromBacktest(
                BacktestStatisticsCalculator.Compute(oosTrades, 0m));

            segments.Add(new WalkForwardSegmentResult
            {
                Window = w,
                SelectedConfig = config,
                InSample = isMetrics,
                OutOfSample = oosMetrics
            });
        }

        return AggregateWalkForward(segments);
    }

    /// <summary>Aggregation identisch zu <see cref="WalkForwardAnalyzer"/> (IS/OOS strikt getrennt).</summary>
    private static WalkForwardResult AggregateWalkForward(IReadOnlyList<WalkForwardSegmentResult> segments)
    {
        decimal isNet = segments.Sum(s => s.InSample.NetProfit);
        decimal oosNet = segments.Sum(s => s.OutOfSample.NetProfit);
        int isTrades = segments.Sum(s => s.InSample.TradeCount);
        int oosTrades = segments.Sum(s => s.OutOfSample.TradeCount);

        decimal? wfe = null;
        if (isTrades > 0 && oosTrades > 0)
        {
            decimal isPerTrade = isNet / isTrades;
            decimal oosPerTrade = oosNet / oosTrades;
            if (isPerTrade > 0m) wfe = oosPerTrade / isPerTrade;
        }

        bool overfit = (isNet > 0m && oosNet <= 0m) || (wfe is decimal e && e < WalkForwardEfficiencyWarn);
        string? note = overfit
            ? $"IS NetPnL {isNet:F2} vs OOS NetPnL {oosNet:F2}"
              + (wfe is decimal w ? $", WFE {w:F2}" : "") + " – Overfitting-Verdacht."
            : null;

        return new WalkForwardResult
        {
            Segments = segments,
            InSampleNetProfit = isNet,
            OutOfSampleNetProfit = oosNet,
            InSampleTrades = isTrades,
            OutOfSampleTrades = oosTrades,
            WalkForwardEfficiency = wfe,
            OverfittingSuspected = overfit,
            OverfittingNote = note
        };
    }

    // ---------------------------------------------------------------------------------------------
    // Sensitivitäts-Demo (Slippage/Fees). NetPnL immer NACH Kosten.
    // ---------------------------------------------------------------------------------------------

    private static SlippageSensitivityResult BuildSlippageSensitivity(BacktestStatistics stats)
    {
        decimal baseNet = stats.NetProfit;
        // Zusätzliche Slippage von L Ticks kostet je Trade 2 Seiten × L × DemoSlippageUsdPerTick.
        decimal costPerTickAllTrades = 2m * DemoSlippageUsdPerTick * stats.TotalTrades;

        var points = new List<SlippageSensitivityPoint>();
        decimal? breakEven = null;
        foreach (var level in SlippageTickLevels)
        {
            decimal net = Math.Round(baseNet - level * costPerTickAllTrades, 2);
            decimal dd = Math.Round(stats.MaxDrawdown + level * costPerTickAllTrades * 0.25m, 2);
            points.Add(new SlippageSensitivityPoint(level, net, dd, stats.TotalTrades));
            if (breakEven is null && net <= 0m) breakEven = level;
        }

        bool fragile = baseNet > 0m && breakEven is not null;
        return new SlippageSensitivityResult
        {
            Points = points,
            BreakEvenSlippageTicks = breakEven,
            FragileToSlippage = fragile
        };
    }

    private static FeeSensitivityResult BuildFeeSensitivity(BacktestStatistics stats)
    {
        // Echte Beziehung: NetPnL(mult) = GrossProfit − mult × TotalFees (Gross bleibt konstant).
        var points = new List<FeeSensitivityPoint>();
        decimal? breakEven = null;
        foreach (var mult in FeeMultipliers)
        {
            decimal net = Math.Round(stats.GrossProfit - mult * stats.TotalFees, 2);
            points.Add(new FeeSensitivityPoint(mult, net, stats.TotalTrades));
            if (breakEven is null && net <= 0m) breakEven = mult;
        }

        decimal baseNet = points.First(p => p.FeeMultiplier == 1.0m).NetProfit;
        bool fragile = baseNet > 0m && breakEven is not null;
        return new FeeSensitivityResult
        {
            Points = points,
            BreakEvenFeeMultiplier = breakEven,
            FragileToFees = fragile
        };
    }

    // ---------------------------------------------------------------------------------------------
    // Equity-/Drawdown-Kurve (kumulierte NetPnL; Drawdown = Peak − Equity, ≥ 0).
    // ---------------------------------------------------------------------------------------------

    private static (IReadOnlyList<decimal> Equity, IReadOnlyList<decimal> Drawdown) BuildEquityAndDrawdown(
        IReadOnlyList<decimal> netPnls)
    {
        var equity = new List<decimal>(netPnls.Count);
        var drawdown = new List<decimal>(netPnls.Count);
        decimal running = 0m, peak = 0m;
        foreach (var pnl in netPnls)
        {
            running += pnl;
            if (running > peak) peak = running;
            equity.Add(Math.Round(running, 2));
            drawdown.Add(Math.Round(peak - running, 2));
        }
        return (equity, drawdown);
    }

    // ---------------------------------------------------------------------------------------------
    // Demo-Kandidaten (klar als DEMO benannt). Bewusst: 1 robuster, 1 mittelmäßiger, 1 überoptimierter.
    // ---------------------------------------------------------------------------------------------

    private static StrategyConfig DemoConfig(DemoStrategyShape shape) => new()
    {
        Name = shape.Name,
        Symbol = "MNQ",
        Enabled = false,                 // Demo — nicht aktiv, sendet nie Signale/Orders
        SuggestedContracts = 1,
        Parameters = new Dictionary<string, string> { ["Demo"] = "true" }
    };

    private static IReadOnlyList<DemoStrategyShape> DemoShapes() => new[]
    {
        new DemoStrategyShape(
            Name: "MNQ Delta-Reversal (Demo)",
            Description: "Robuster Demo-Kandidat: klar positives OOS, hohe Walk-Forward-Efficiency, breite Parameter-Stabilität, unempfindlich gegen höhere Kosten.",
            Seed: 101, TradeCount: 220, WinRate: 0.48m, AvgWin: 230m, AvgLoss: -150m,
            OosWinRateFactor: 0.96m, ParameterStability: 0.80m),

        new DemoStrategyShape(
            Name: "MNQ Absorption-Breakout (Demo, kostenfragil)",
            Description: "Dünne-Marge-Demo-Kandidat: nur bei niedrigen Kosten profitabel — verschwindet bei höheren Fees/Slippage.",
            Seed: 202, TradeCount: 160, WinRate: 0.50m, AvgWin: 130m, AvgLoss: -118m,
            OosWinRateFactor: 0.98m, ParameterStability: 0.55m),

        new DemoStrategyShape(
            Name: "MNQ Sweep-Scalper (Demo, überoptimiert)",
            Description: "Überoptimierter Demo-Kandidat: In-Sample profitabel, Out-of-Sample negativ — klassisches Overfitting-Signal.",
            Seed: 303, TradeCount: 90, WinRate: 0.60m, AvgWin: 190m, AvgLoss: -230m,
            OosWinRateFactor: 0.70m, ParameterStability: 0.35m)
    };

    /// <summary>Parametrisierte Beschreibung eines Demo-Kandidaten (steuert die Trade-Erzeugung).</summary>
    private sealed record DemoStrategyShape(
        string Name, string Description, int Seed, int TradeCount,
        decimal WinRate, decimal AvgWin, decimal AvgLoss,
        decimal OosWinRateFactor, decimal ParameterStability,
        bool DataQualityOk = true, bool CapabilitiesSufficient = true);
}
