using System.Text;
using FluentAssertions;
using TradingBot.Backtesting;
using TradingBot.Backtesting.Strategies;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.MarketData;
using BtKillSwitch = TradingBot.Backtesting.Risk.BacktestKillSwitch;
using Xunit;

namespace TradingBot.Tests.Backtesting;

public class BacktestEngineTests
{
    private static readonly IBacktestEngine Engine = new BacktestEngine();

    [Fact]
    public async Task Runs_with_minimal_tick_csv()
    {
        var csv = new StringBuilder("timestamp,symbol,price,volume\n");
        foreach (var t in BacktestTestData.RisingTicks(12))
            csv.Append($"{t.Timestamp:O},NQ,{t.Price},1\n");

        var ticks = CsvTickReader.Read(new StringReader(csv.ToString()));
        var req = BacktestTestData.Request(new TestSignalStrategy(intervalTicks: 2), ticks);

        var result = await Engine.RunAsync(req);

        result.Status.Should().Be(BacktestRunStatus.Completed);
        result.TicksProcessed.Should().Be(ticks.Count);
    }

    [Fact]
    public async Task Uses_replay_provider_and_produces_trades()
    {
        var req = BacktestTestData.Request(new TestSignalStrategy(intervalTicks: 2), BacktestTestData.RisingTicks(14));
        var result = await Engine.RunAsync(req);

        result.Status.Should().Be(BacktestRunStatus.Completed);
        result.SignalsGenerated.Should().BeGreaterThan(0);
        result.Statistics.TotalTrades.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Killswitch_blocks_all_orders()
    {
        var req = BacktestTestData.Request(
            new TestSignalStrategy(intervalTicks: 2), BacktestTestData.RisingTicks(14),
            killSwitch: new BtKillSwitch(active: true));

        var result = await Engine.RunAsync(req);

        result.OrdersSubmitted.Should().Be(0);
        result.OrdersRejectedByRisk.Should().BeGreaterThan(0);
        result.Statistics.TotalTrades.Should().Be(0);
    }

    [Fact]
    public async Task Approved_setup_submits_orders()
    {
        var req = BacktestTestData.Request(new TestSignalStrategy(intervalTicks: 2), BacktestTestData.RisingTicks(14));
        var result = await Engine.RunAsync(req);
        result.OrdersSubmitted.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Fees_reduce_net_below_gross()
    {
        var req = BacktestTestData.Request(new TestSignalStrategy(intervalTicks: 2), BacktestTestData.RisingTicks(14));
        var result = await Engine.RunAsync(req);

        result.Statistics.TotalFees.Should().BeGreaterThan(0m);
        (result.Statistics.GrossProfit - result.Statistics.TotalFees).Should().Be(result.Statistics.NetProfit);
    }

    [Fact]
    public async Task Slippage_worsens_gross_and_is_reported()
    {
        var ticks = BacktestTestData.RisingTicks(14);
        var noSlip = await Engine.RunAsync(
            BacktestTestData.Request(new TestSignalStrategy(intervalTicks: 2), ticks));
        var withSlip = await Engine.RunAsync(
            BacktestTestData.Request(new TestSignalStrategy(intervalTicks: 2), ticks,
                config: new BacktestConfiguration { SlippageTicksOverride = 4m }));

        withSlip.Statistics.TotalSlippage.Should().BeGreaterThan(0m);
        noSlip.Statistics.TotalSlippage.Should().Be(0m);
        withSlip.Statistics.GrossProfit.Should().BeLessThan(noSlip.Statistics.GrossProfit);
    }

    [Fact]
    public async Task Result_is_deterministic_across_runs()
    {
        var ticks = BacktestTestData.RisingTicks(16);
        var r1 = await Engine.RunAsync(BacktestTestData.Request(new TestSignalStrategy(intervalTicks: 2), ticks));
        var r2 = await Engine.RunAsync(BacktestTestData.Request(new TestSignalStrategy(intervalTicks: 2), ticks));

        r1.Statistics.Should().Be(r2.Statistics);
        r1.Trades.Should().BeEquivalentTo(r2.Trades);
    }

    [Fact]
    public async Task Cancellation_returns_cancelled_status()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var req = BacktestTestData.Request(new TestSignalStrategy(intervalTicks: 2), BacktestTestData.RisingTicks(14));

        var result = await Engine.RunAsync(req, cts.Token);

        result.Status.Should().Be(BacktestRunStatus.Cancelled);
    }

    [Fact]
    public async Task Max_daily_loss_stops_trading()
    {
        // Fallende Preise -> Long-Trades verlieren. Kleiner Tagesverlust-Limit stoppt weitere Orders.
        var req = BacktestTestData.Request(
            new TestSignalStrategy(intervalTicks: 2), BacktestTestData.FallingTicks(10),
            risk: BacktestTestData.Risk(maxDailyLoss: 100m));

        var result = await Engine.RunAsync(req);

        result.Statistics.TotalTrades.Should().Be(1);           // nur der erste Trade schließt
        result.Statistics.NetProfit.Should().BeLessThan(0m);    // Verlust
        result.OrdersRejectedByRisk.Should().BeGreaterThan(0);  // Folge-Signale blockiert
    }

    [Fact]
    public async Task Candle_mode_is_not_supported()
    {
        var req = BacktestTestData.Request(
            new TestSignalStrategy(intervalTicks: 2), BacktestTestData.RisingTicks(6),
            config: new BacktestConfiguration { FillDataMode = FillDataMode.Candle });

        var result = await Engine.RunAsync(req);

        result.Status.Should().Be(BacktestRunStatus.Failed);
        result.Message.Should().Contain("Candle");
    }

    [Fact]
    public async Task Can_close_position_even_with_max_open_positions_one()
    {
        // MaxOpenPositions = 1: der Close (Gegensignal) darf NICHT als Entry blockiert werden.
        var req = BacktestTestData.Request(
            new TestSignalStrategy(intervalTicks: 2), BacktestTestData.RisingTicks(14),
            risk: BacktestTestData.Risk() with { MaxOpenPositions = 1 });

        var result = await Engine.RunAsync(req);

        result.Status.Should().Be(BacktestRunStatus.Completed);
        result.Statistics.TotalTrades.Should().BeGreaterThan(0); // Positionen konnten geschlossen werden
    }

    [Fact]
    public void Engine_has_no_live_execution_reference()
    {
        // BacktestEngine referenziert keinen Live-/echten Broker-Adapter-Typ im Konstruktor.
        typeof(BacktestEngine).GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Should().NotContain(p => p.ParameterType == typeof(IBrokerExecutionAdapter));
    }
}
