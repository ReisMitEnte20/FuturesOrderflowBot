using FluentAssertions;
using TradingBot.Research;
using TradingBot.Research.Sweep;
using Xunit;
using static TradingBot.Tests.Research.ResearchTestData;

namespace TradingBot.Tests.Research;

public class ParameterSweepTests
{
    // ----------------------------- Grid --------------------------------------

    [Fact]
    public void Grid_produces_expected_cartesian_combinations()
    {
        var grid = new ParameterGrid(new[]
        {
            ParameterRange.Ints("Fast", 1, 3, 1),      // 1,2,3
            ParameterRange.Booleans("UseVwap")         // true,false
        });

        grid.TotalCombinations.Should().Be(6);
        var expansion = grid.Expand(maxRuns: 100);

        expansion.Combinations.Should().HaveCount(6);
        expansion.Truncated.Should().BeFalse();
        expansion.Combinations[0].Values.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["Fast"] = "1", ["UseVwap"] = "true"
        });
        expansion.Combinations[1].Values["UseVwap"].Should().Be("false"); // Odometer: letzte Dim zuerst
    }

    [Fact]
    public void Decimal_and_explicit_ranges_generate_values()
    {
        ParameterRange.Decimals("Ratio", 1.0m, 2.0m, 0.5m).Values.Should().BeEquivalentTo("1.0", "1.5", "2.0");
        ParameterRange.Explicit("Mode", "a", "b").Values.Should().BeEquivalentTo("a", "b");
    }

    [Fact]
    public void MaxRuns_truncates_the_sweep()
    {
        var grid = new ParameterGrid(new[] { ParameterRange.Ints("X", 1, 100, 1) }); // 100 Kombinationen
        var expansion = grid.Expand(maxRuns: 10);

        expansion.Combinations.Should().HaveCount(10);
        expansion.TotalPossible.Should().Be(100);
        expansion.Truncated.Should().BeTrue();
    }

    [Fact]
    public void Duplicate_parameter_names_are_rejected()
    {
        var act = () => new ParameterGrid(new[] { ParameterRange.Ints("X", 1, 2), ParameterRange.Ints("X", 3, 4) });
        act.Should().Throw<ArgumentException>();
    }

    // ----------------------------- Sweep run ---------------------------------

    [Fact]
    public async Task Sweep_runs_each_combination_and_is_deterministic()
    {
        // Fake-Runner: NetProfit hängt deterministisch vom Parameter "X" ab.
        var runner = new FakeBacktestRunner(inputs =>
        {
            int x = int.Parse(inputs.Config.Parameters["X"]);
            return RunResult(inputs.Candidate.Name, net: x * 10m, tradeCount: 40);
        });
        var candidate = Candidate("SweepCand");
        var grid = new ParameterGrid(new[] { ParameterRange.Ints("X", 1, 5, 1) });

        var report1 = await new ParameterSweepRunner(runner).RunAsync(candidate, grid, Template(), maxRuns: 100);
        var runner2 = new FakeBacktestRunner(inputs => RunResult(inputs.Candidate.Name,
            net: int.Parse(inputs.Config.Parameters["X"]) * 10m, tradeCount: 40));
        var report2 = await new ParameterSweepRunner(runner2).RunAsync(candidate, grid, Template(), maxRuns: 100);

        report1.Results.Should().HaveCount(5);
        report1.Ranking.Should().HaveCount(5);
        // Bestes X=5 (net 50) rankt zuerst.
        report1.Ranking[0].Metrics.NetProfit.Should().Be(50m);
        // Deterministisch:
        report1.Ranking.Select(r => r.CompositeScore).Should().Equal(report2.Ranking.Select(r => r.CompositeScore));
    }

    [Fact]
    public async Task Sweep_respects_max_runs()
    {
        var runner = new FakeBacktestRunner(inputs => RunResult(inputs.Candidate.Name, 10m, 40));
        var grid = new ParameterGrid(new[] { ParameterRange.Ints("X", 1, 50, 1) });

        var report = await new ParameterSweepRunner(runner).RunAsync(Candidate(), grid, Template(), maxRuns: 7);

        report.Results.Should().HaveCount(7);
        runner.Calls.Should().HaveCount(7);
        report.Truncated.Should().BeTrue();
    }
}
