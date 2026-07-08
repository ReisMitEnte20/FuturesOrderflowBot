using FluentAssertions;
using TradingBot.Research.MonteCarlo;
using TradingBot.Research.Statistics;
using Xunit;
using static TradingBot.Tests.Research.ResearchTestData;

namespace TradingBot.Tests.Research;

public class EquityAndMonteCarloTests
{
    // ----------------------------- Percentile / Drawdown ---------------------

    [Fact]
    public void Percentile_uses_linear_interpolation()
    {
        var values = Enumerable.Range(1, 10).Select(i => (decimal)(i * 10)).ToList(); // 10..100

        EquityStatistics.Percentile(values, 0.95m).Should().Be(95.5m); // rank 8.55 -> 90 + 0.55*10
        EquityStatistics.Median(new[] { 1m, 2m, 3m, 4m }).Should().Be(2.5m);
        EquityStatistics.Percentile(new[] { 42m }, 0.9m).Should().Be(42m);
    }

    [Fact]
    public void MaxDrawdown_matches_backtest_definition()
    {
        EquityStatistics.MaxDrawdown(new[] { -100m, -50m, 80m }).Should().Be(150m);
        EquityStatistics.MaxDrawdown(new[] { 100m, -50m, 200m, -30m, -20m }).Should().Be(50m);
    }

    // ----------------------------- Monte Carlo -------------------------------

    [Fact]
    public void Reshuffle_preserves_total_but_varies_drawdown()
    {
        var request = new MonteCarloRequest
        {
            TradeNetPnLs = new[] { 100m, -50m, 100m, -50m, 100m, -50m },
            Simulations = 200, Seed = 7, Method = MonteCarloMethod.Reshuffle
        };

        var result = new MonteCarloSimulator().Run(request);

        // Reihenfolge ändert den Endgewinn NICHT (Summe = 150).
        result.Statistics.WorstNetProfit.Should().Be(150m);
        result.Statistics.BestNetProfit.Should().Be(150m);
        // Aber der Drawdown-Pfad variiert.
        result.Statistics.WorstMaxDrawdown.Should().BeGreaterThan(result.Statistics.MedianMaxDrawdown);
        result.Runs.Should().HaveCount(200);
    }

    [Fact]
    public void Same_seed_is_reproducible()
    {
        var request = MonteCarloRequest.FromTrades(
            Trades(30m, -10m, 25m, -40m, 60m), simulations: 500, seed: 999,
            method: MonteCarloMethod.BootstrapWithReplacement);

        var r1 = new MonteCarloSimulator().Run(request);
        var r2 = new MonteCarloSimulator().Run(request);

        r1.Statistics.Should().Be(r2.Statistics);
        r1.Runs.Should().BeEquivalentTo(r2.Runs, o => o.WithStrictOrdering());
    }

    [Fact]
    public void Bootstrap_with_equal_pool_is_deterministic_sum()
    {
        var request = new MonteCarloRequest
        {
            TradeNetPnLs = new[] { 5m, 5m, 5m }, Simulations = 100, Seed = 1,
            Method = MonteCarloMethod.BootstrapWithReplacement
        };

        var result = new MonteCarloSimulator().Run(request);

        result.Runs.Should().OnlyContain(r => r.FinalNetProfit == 15m); // jeder Sample = 3 * 5
    }

    [Fact]
    public void Bootstrap_with_mixed_pool_varies_final_profit()
    {
        var request = new MonteCarloRequest
        {
            TradeNetPnLs = new[] { 100m, -100m, 50m, -50m }, Simulations = 500, Seed = 3,
            Method = MonteCarloMethod.BootstrapWithReplacement
        };

        var result = new MonteCarloSimulator().Run(request);

        result.Statistics.WorstNetProfit.Should().BeLessThan(result.Statistics.BestNetProfit);
    }

    [Fact]
    public void Worst_5_percent_drawdown_equals_95th_percentile_of_distribution()
    {
        var request = new MonteCarloRequest
        {
            TradeNetPnLs = new[] { 20m, -30m, 40m, -25m, 15m, -35m, 50m, -20m },
            Simulations = 400, Seed = 42, Method = MonteCarloMethod.Reshuffle
        };

        var result = new MonteCarloSimulator().Run(request);

        var drawdowns = result.Runs.Select(r => r.MaxDrawdown).ToList();
        result.Statistics.WorstDrawdown5Percent.Should().Be(EquityStatistics.Percentile(drawdowns, 0.95m));
        result.Statistics.WorstDrawdown5Percent.Should().BeLessThanOrEqualTo(result.Statistics.WorstMaxDrawdown);
        result.Statistics.WorstDrawdown5Percent.Should().BeGreaterThanOrEqualTo(result.Statistics.MedianMaxDrawdown);
    }

    [Fact]
    public void Probability_of_drawdown_and_ruin_are_computed_when_thresholds_given()
    {
        var request = new MonteCarloRequest
        {
            TradeNetPnLs = new[] { 100m, -200m, 50m, -150m }, Simulations = 300, Seed = 5,
            Method = MonteCarloMethod.BootstrapWithReplacement,
            DrawdownThreshold = 100m, RuinThreshold = 300m
        };

        var result = new MonteCarloSimulator().Run(request);

        result.Statistics.ProbabilityOfDrawdownExceeding.Should().NotBeNull();
        result.Statistics.ProbabilityOfDrawdownExceeding.Should().BeInRange(0m, 1m);
        result.Statistics.ProbabilityOfRuin.Should().NotBeNull();
        result.Statistics.ProbabilityOfLoss.Should().BeInRange(0m, 1m);
    }

    [Fact]
    public void Empty_trades_yield_empty_result()
    {
        var result = new MonteCarloSimulator().Run(new MonteCarloRequest { TradeNetPnLs = Array.Empty<decimal>() });
        result.HasData.Should().BeFalse();
    }
}
