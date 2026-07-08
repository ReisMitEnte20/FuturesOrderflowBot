using FluentAssertions;
using TradingBot.Application.Strategies;
using TradingBot.Core.Interfaces;
using TradingBot.Research;
using TradingBot.Research.Runner;
using TradingBot.Tests.Backtesting;
using Xunit;
using static TradingBot.Tests.Research.ResearchTestData;

namespace TradingBot.Tests.Research;

public class ResearchEngineTests
{
    [Fact]
    public async Task Runs_candidates_adds_monte_carlo_and_ranks()
    {
        var runner = new FakeBacktestRunner(inputs =>
        {
            // "B" hat weniger Net, aber deutlich kleineren Drawdown.
            decimal net = inputs.Candidate.Name == "A" ? 2000m : 1200m;
            decimal dd = inputs.Candidate.Name == "A" ? 1500m : 300m;
            var trades = Trades(Enumerable.Range(0, 40).Select(i => i % 3 == 0 ? -20m : (net / 40m)).ToArray());
            return RunResult(inputs.Candidate.Name, net, 40, dd, config: inputs.Config, trades: trades);
        });

        var request = new ResearchRequest
        {
            Candidates = new[] { Candidate("A"), Candidate("B") },
            Ticks = BacktestTestData.RisingTicks(10), Symbol = "NQ",
            Instrument = BacktestTestData.Instrument(), Fee = BacktestTestData.Fee(),
            Broker = BacktestTestData.Broker(), Risk = BacktestTestData.Risk(), Account = BacktestTestData.Account()
        };

        var result = await new ResearchEngine(runner).RunAsync(request);

        result.Status.Should().Be(ResearchRunStatus.Completed);
        result.Runs.Should().HaveCount(2);
        result.Runs.Should().OnlyContain(r => r.MonteCarlo != null && r.Robustness != null);
        result.Ranking.Should().HaveCount(2);
        result.Ranking[0].Rank.Should().Be(1);
    }

    [Fact]
    public async Task Real_backtest_runner_produces_metrics_end_to_end()
    {
        // Integration: echte BacktestEngine über den Runner (keine Fakes).
        var candidate = new StrategyCandidate
        {
            Name = "TestSignal",
            BaseConfig = StratConfig("TestSignal"),
            CreateStrategy = _ => new TestSignalStrategy(intervalTicks: 2)
        };
        var inputs = Template(BacktestTestData.RisingTicks(14)) with { Candidate = candidate, Config = candidate.BaseConfig };

        var run = await new BacktestStrategyRunner().RunAsync(inputs);

        run.StrategyName.Should().Be("TestSignal");
        run.Statistics.TotalTrades.Should().BeGreaterThan(0);
        run.Metrics.TradeCount.Should().Be(run.Statistics.TotalTrades);
        // NetPnL nach Fees korrekt weitergereicht:
        (run.Statistics.GrossProfit - run.Statistics.TotalFees).Should().Be(run.Statistics.NetProfit);
    }

    [Fact]
    public async Task Cancellation_is_respected()
    {
        var runner = new FakeBacktestRunner(inputs => RunResult(inputs.Candidate.Name, 100m, 40));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var request = new ResearchRequest
        {
            Candidates = new[] { Candidate("A") },
            Ticks = BacktestTestData.RisingTicks(10), Symbol = "NQ",
            Instrument = BacktestTestData.Instrument(), Fee = BacktestTestData.Fee(),
            Broker = BacktestTestData.Broker(), Risk = BacktestTestData.Risk(), Account = BacktestTestData.Account()
        };

        var result = await new ResearchEngine(runner).RunAsync(request, cts.Token);

        result.Status.Should().Be(ResearchRunStatus.Cancelled);
    }

    // ----------------------------- Safety by construction --------------------

    [Fact]
    public void Research_assembly_has_no_execution_or_network_references()
    {
        var referenced = typeof(ResearchEngine).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        referenced.Should().NotContain(n => n.Contains("TradingBot.Execution"));
        referenced.Should().NotContain(n => n.StartsWith("System.Net"));
    }

    [Fact]
    public void ResearchEngine_only_depends_on_runner_abstraction()
    {
        typeof(ResearchEngine).GetConstructors()[0].GetParameters()[0].ParameterType
            .Should().Be(typeof(IStrategyBacktestRunner));
    }
}
