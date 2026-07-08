using System.Reflection;
using FluentAssertions;
using TradingBot.DevDashboard.Services;
using Xunit;

namespace TradingBot.Tests.DevDashboard;

/// <summary>
/// Tests für das Research Dashboard (Phase 12D). Sichern: deterministische Demo-Erzeugung,
/// Ranking/Monte-Carlo/Robustness/Overfitting-Ausgaben und die Safety-Grenzen (kein Execution-/
/// Broker-/Netzwerk-Bezug). RESEARCH / SIMULATION ONLY.
/// </summary>
public class ResearchDemoServiceTests
{
    private static ResearchDashboardData Data() => new ResearchDemoService().GetDemoData();

    private static ResearchStrategyView ByNamePart(ResearchDashboardData d, string part)
        => d.Strategies.Single(s => s.Name.Contains(part, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Produces_three_ranked_strategies_with_best_first()
    {
        var d = Data();

        d.Strategies.Should().HaveCount(3);
        d.Ranking.Should().HaveCount(3);

        d.Ranking.Select(r => r.Rank).Should().Equal(1, 2, 3);
        d.Strategies.Select(s => s.Rank).Should().BeInAscendingOrder();
        d.Ranking[0].StrategyName.Should().Be(d.Best.Name);
        d.Best.Rank.Should().Be(1);
    }

    [Fact]
    public void Each_strategy_has_montecarlo_robustness_and_walkforward()
    {
        var d = Data();

        foreach (var v in d.Strategies)
        {
            v.MonteCarlo.Should().NotBeNull();
            v.MonteCarlo!.Statistics.Simulations.Should().Be(1000);           // Monte-Carlo-Demo-Werte erzeugt
            v.Robustness.Should().NotBeNull();
            v.Robustness!.Score.Value.Should().BeInRange(0m, 100m);           // Robustness Score erzeugt
            v.WalkForward.Segments.Should().NotBeEmpty();                     // Walk-Forward-Segmente erzeugt
            v.Run.Trades.Should().NotBeEmpty();
            v.EquityCurve.Should().HaveCount(v.Metrics.TradeCount);
        }
    }

    [Fact]
    public void Strategy_ranking_prefers_robust_over_overfit()
    {
        var d = Data();

        var robust = ByNamePart(d, "Delta-Reversal");
        var overfit = ByNamePart(d, "überoptimiert");

        robust.Rank.Should().BeLessThan(overfit.Rank);
        overfit.Rank.Should().Be(3);                    // überoptimierter Kandidat rankt zuletzt
    }

    [Fact]
    public void Best_candidate_is_profitable_and_not_overfit()
    {
        var d = Data();

        d.Best.Metrics.NetProfit.Should().BeGreaterThan(0m);
        d.Best.Robustness!.HasCritical.Should().BeFalse();
        d.Best.WalkForward.OverfittingSuspected.Should().BeFalse();
        d.Best.WalkForward.WalkForwardEfficiency.Should().NotBeNull();
        d.Best.WalkForward.WalkForwardEfficiency!.Value.Should().BeGreaterThanOrEqualTo(0.5m);
    }

    [Fact]
    public void Overfitting_candidate_is_flagged()
    {
        var d = Data();
        var overfit = ByNamePart(d, "überoptimiert");

        overfit.Metrics.NetProfit.Should().BeGreaterThan(0m);                 // In-Sample profitabel
        overfit.Metrics.OutOfSampleNetProfit.Should().BeLessThanOrEqualTo(0m); // Out-of-Sample nicht
        overfit.WalkForward.OverfittingSuspected.Should().BeTrue();
        overfit.Robustness!.HasCritical.Should().BeTrue();
        overfit.Robustness!.Contains("OosNegativeWhileIsPositive").Should().BeTrue();
        overfit.WarningCount.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void Cost_fragile_candidate_flags_fee_and_slippage_fragility()
    {
        var d = Data();
        var fragile = ByNamePart(d, "kostenfragil");

        fragile.Metrics.NetProfit.Should().BeGreaterThan(0m);                 // bei niedrigen Kosten profitabel
        fragile.Slippage.FragileToSlippage.Should().BeTrue();
        fragile.Slippage.BreakEvenSlippageTicks.Should().NotBeNull();
        fragile.Fees.FragileToFees.Should().BeTrue();
        fragile.Fees.BreakEvenFeeMultiplier.Should().NotBeNull();
    }

    [Fact]
    public void MonteCarlo_demo_values_are_produced_for_best()
    {
        var d = Data();
        var mc = d.Best.MonteCarlo!.Statistics;

        mc.Simulations.Should().Be(d.MonteCarloSimulations);
        mc.WorstDrawdown5Percent.Should().BeGreaterThanOrEqualTo(0m);
        mc.ProbabilityOfLoss.Should().BeInRange(0m, 1m);
        mc.ProbabilityOfDrawdownExceeding.Should().NotBeNull();              // Schwelle gesetzt → Wert vorhanden
        mc.NetProfitP5.Should().BeLessThanOrEqualTo(mc.NetProfitP95);
    }

    [Fact]
    public void Equity_and_drawdown_curves_are_consistent()
    {
        var d = Data();
        var v = d.Best;

        v.EquityCurve.Should().HaveCount(v.Metrics.TradeCount);
        v.DrawdownCurve.Should().HaveCount(v.Metrics.TradeCount);
        v.DrawdownCurve.Should().OnlyContain(x => x >= 0m);                  // Drawdown ≥ 0
        v.FinalEquity.Should().Be(v.Metrics.NetProfit);                     // Endwert der Kurve == NetProfit
    }

    [Fact]
    public void Demo_is_deterministic_across_instances()
    {
        var a = new ResearchDemoService().GetDemoData();
        var b = new ResearchDemoService().GetDemoData();

        a.Best.Name.Should().Be(b.Best.Name);
        a.Ranking.Select(r => r.StrategyName).Should().Equal(b.Ranking.Select(r => r.StrategyName));
        a.Best.Metrics.NetProfit.Should().Be(b.Best.Metrics.NetProfit);
        a.Best.MonteCarlo!.Statistics.MedianNetProfit.Should().Be(b.Best.MonteCarlo!.Statistics.MedianNetProfit);
        a.Best.EquityCurve.Should().Equal(b.Best.EquityCurve);
    }

    [Fact]
    public void GetDemoData_is_cached_within_instance()
    {
        var service = new ResearchDemoService();
        var first = service.GetDemoData();
        var second = service.GetDemoData();

        ReferenceEquals(first, second).Should().BeTrue();   // read-only: einmal berechnet, gecached
    }

    [Fact]
    public void Dashboard_does_not_reference_execution_or_broker_sdks()
    {
        var referenced = typeof(ResearchDemoService).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .ToList();

        referenced.Should().NotContain(n => n.Contains("TradingBot.Execution"));
        referenced.Should().NotContain(n =>
            n.Contains("Rithmic", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("CQG", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Tradovate", StringComparison.OrdinalIgnoreCase));
        referenced.Should().Contain(n => n == "TradingBot.Research"); // nutzt den Research-Layer
    }

    [Fact]
    public void Research_layer_transitively_has_no_execution_reference()
    {
        // Der komplette Research-Referenzbaum (Research → Backtesting/Application/…) enthält
        // NICHT TradingBot.Execution — sonst könnte das Dashboard indirekt Broker-Code laden.
        var research = typeof(ResearchEngineMarker).Assembly;
        var toVisit = new Queue<Assembly>();
        var seen = new HashSet<string>();
        toVisit.Enqueue(research);

        while (toVisit.Count > 0)
        {
            var asm = toVisit.Dequeue();
            foreach (var name in asm.GetReferencedAssemblies())
            {
                if (!name.Name!.StartsWith("TradingBot", StringComparison.Ordinal)) continue;
                name.Name.Should().NotContain("TradingBot.Execution");
                if (seen.Add(name.Name))
                    toVisit.Enqueue(Assembly.Load(name));
            }
        }
    }

    // Marker, um die Research-Assembly zuverlässig zu laden.
    private sealed class ResearchEngineMarker : TradingBot.Research.IResearchEngine
    {
        public Task<TradingBot.Research.ResearchResult> RunAsync(
            TradingBot.Research.ResearchRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
