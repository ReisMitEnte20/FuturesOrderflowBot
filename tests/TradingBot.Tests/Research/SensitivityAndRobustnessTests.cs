using FluentAssertions;
using TradingBot.Domain.Enums;
using TradingBot.Research;
using TradingBot.Research.MonteCarlo;
using TradingBot.Research.Ranking;
using TradingBot.Research.Robustness;
using TradingBot.Research.Sensitivity;
using TradingBot.Tests.Backtesting;
using Xunit;
using static TradingBot.Tests.Research.ResearchTestData;

namespace TradingBot.Tests.Research;

public class SensitivityAndRobustnessTests
{
    // ----------------------------- Sensitivity -------------------------------

    [Fact]
    public async Task Higher_slippage_worsens_net_and_flags_fragility()
    {
        // Fake: NetProfit sinkt mit Slippage: 500 - 100*slip.
        var runner = new FakeBacktestRunner(inputs =>
        {
            decimal slip = inputs.SlippageTicksOverride ?? 0m;
            return RunResult("s", net: 500m - 100m * slip, tradeCount: 40, maxDd: 100m + 20m * slip);
        });

        var result = await new SensitivityAnalyzer(runner)
            .AnalyzeSlippageAsync(Template(), new[] { 0m, 1m, 2m, 4m, 6m });

        result.Points.Should().HaveCount(5);
        result.Points[0].NetProfit.Should().BeGreaterThan(result.Points[^1].NetProfit); // schlechter bei mehr Slippage
        result.BreakEvenSlippageTicks.Should().Be(6m); // 500 - 100*6 = -100 <= 0
        result.FragileToSlippage.Should().BeTrue();
    }

    [Fact]
    public async Task Higher_fees_reduce_net_profit()
    {
        // Fake: NetProfit sinkt mit skalierter Commission (Basis 0.85).
        var baseCommission = BacktestTestData.Fee().CommissionPerSide;
        var runner = new FakeBacktestRunner(inputs =>
        {
            decimal mult = inputs.Fee.CommissionPerSide / baseCommission;
            return RunResult("f", net: 400m - 300m * (mult - 1m), tradeCount: 40);
        });

        var result = await new SensitivityAnalyzer(runner)
            .AnalyzeFeesAsync(Template(), new[] { 1m, 1.5m, 2m, 3m });

        result.Points.First(p => p.FeeMultiplier == 1m).NetProfit.Should()
            .BeGreaterThan(result.Points.First(p => p.FeeMultiplier == 3m).NetProfit);
        result.BreakEvenFeeMultiplier.Should().Be(3m); // 400 - 300*2 = -200
        result.FragileToFees.Should().BeTrue();
    }

    [Fact]
    public async Task Parameter_sensitivity_reports_stability()
    {
        // Stabil: alle Werte -> gleicher Net -> Stabilität 1.
        var stableRunner = new FakeBacktestRunner(_ => RunResult("p", 300m, 40));
        var stable = await new SensitivityAnalyzer(stableRunner)
            .AnalyzeParameterAsync(Candidate(), Template(), "X", new[] { "1", "2", "3" });
        stable.Stability.Should().Be(1m);
        stable.StdDevNetProfit.Should().Be(0m);

        // Instabil: stark schwankender Net -> Stabilität < 1.
        var wobblyRunner = new FakeBacktestRunner(inputs =>
            RunResult("p", net: int.Parse(inputs.Config.Parameters["X"]) % 2 == 0 ? 1000m : -900m, tradeCount: 40));
        var wobbly = await new SensitivityAnalyzer(wobblyRunner)
            .AnalyzeParameterAsync(Candidate(), Template(), "X", new[] { "1", "2", "3", "4" });
        wobbly.Stability.Should().BeLessThan(0.5m);
    }

    // ----------------------------- Robustness --------------------------------

    [Fact]
    public void Reports_too_few_trades()
    {
        var metrics = ResearchMetricSet.FromBacktest(Stats(500m, tradeCount: 5));
        var report = new RobustnessAnalyzer().Analyze(metrics);

        report.Contains("TooFewTrades").Should().BeTrue();
        report.HasWarnings.Should().BeTrue();
    }

    [Fact]
    public void Reports_is_oos_divergence()
    {
        var metrics = ResearchMetricSet.FromBacktest(Stats(1000m, tradeCount: 50))
            with { OutOfSampleNetProfit = -100m };

        var report = new RobustnessAnalyzer().Analyze(metrics);

        report.Contains("OosNegativeWhileIsPositive").Should().BeTrue();
        report.HasCritical.Should().BeTrue();
    }

    [Fact]
    public void Reports_data_quality_problems()
    {
        var metrics = ResearchMetricSet.FromBacktest(Stats(500m, tradeCount: 50), dataQualityOk: false,
            capabilitiesSufficient: false);

        var report = new RobustnessAnalyzer().Analyze(metrics);

        report.Contains("PoorDataQuality").Should().BeTrue();
        report.Contains("InsufficientCapabilities").Should().BeTrue();
        report.HasCritical.Should().BeTrue();
    }

    [Fact]
    public void Reports_monte_carlo_worst_case_too_bad()
    {
        var metrics = ResearchMetricSet.FromBacktest(Stats(1000m, tradeCount: 50));
        var mc = new MonteCarloResult
        {
            Method = MonteCarloMethod.Reshuffle,
            Statistics = MonteCarloStatistics.Empty with { WorstDrawdown5Percent = 3000m } // > 2x Net
        };

        var report = new RobustnessAnalyzer().Analyze(metrics, monteCarlo: mc);

        report.Contains("MonteCarloWorstCaseTooBad").Should().BeTrue();
    }

    [Fact]
    public void Clean_metrics_score_high()
    {
        var metrics = ResearchMetricSet.FromBacktest(Stats(2000m, tradeCount: 100, maxDd: 300m, pf: 2.0m))
            with { OutOfSampleNetProfit = 1500m, ParameterStability = 0.9m, MonteCarloWorstDrawdown5 = 500m };

        var report = new RobustnessAnalyzer().Analyze(metrics, Trades(100m, 100m, -50m));

        report.HasCritical.Should().BeFalse();
        report.Score.Value.Should().BeGreaterThanOrEqualTo(80m);
    }

    // ----------------------------- Ranking -----------------------------------

    [Fact]
    public void Ranking_prefers_robust_over_overfit_strategy()
    {
        // Overfit: riesiger Net, aber miese OOS, riesiger Drawdown/MC-Worst, instabil.
        var overfit = ResearchMetricSet.FromBacktest(Stats(10000m, tradeCount: 100, maxDd: 8000m, pf: 3m))
            with { OutOfSampleNetProfit = -500m, MonteCarloWorstDrawdown5 = 9000m, ParameterStability = 0.1m };

        // Robust: moderater Net, gute OOS, kleiner Drawdown/MC-Worst, stabil.
        var robust = ResearchMetricSet.FromBacktest(Stats(3000m, tradeCount: 100, maxDd: 800m, pf: 1.8m))
            with { OutOfSampleNetProfit = 2500m, MonteCarloWorstDrawdown5 = 900m, ParameterStability = 0.9m };

        var ranking = new StrategyRankingService().Rank(new[] { ("Overfit", overfit), ("Robust", robust) });

        ranking[0].StrategyName.Should().Be("Robust");   // stabil schlägt überoptimiert
        ranking[0].Rank.Should().Be(1);
        ranking[1].StrategyName.Should().Be("Overfit");
    }

    [Fact]
    public void Ranking_penalizes_too_few_trades_and_bad_data()
    {
        var good = ResearchMetricSet.FromBacktest(Stats(1000m, tradeCount: 100, maxDd: 200m));
        var fewTrades = ResearchMetricSet.FromBacktest(Stats(1200m, tradeCount: 5, maxDd: 150m)); // mehr Net, aber 5 Trades

        var ranking = new StrategyRankingService().Rank(new[] { ("Few", fewTrades), ("Good", good) });

        ranking[0].StrategyName.Should().Be("Good");
        ranking.First(r => r.StrategyName == "Few").Penalties.Should().Contain(p => p.Contains("TooFewTrades"));
    }
}
