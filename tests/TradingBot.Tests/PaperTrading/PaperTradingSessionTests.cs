using FluentAssertions;
using TradingBot.Backtesting.Strategies;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.PaperTrading;
using TradingBot.PaperTrading.Risk;
using TradingBot.Tests.Backtesting;
using Xunit;
using static TradingBot.Tests.PaperTrading.PaperTestData;

namespace TradingBot.Tests.PaperTrading;

public class PaperTradingSessionTests
{
    private static readonly IPaperTradingEngine Engine = new PaperTradingEngine();

    // ----------------------------- Lifecycle ---------------------------------

    [Fact]
    public async Task Starts_and_completes_with_replay_data()
    {
        var session = Engine.Start(Request(new TestSignalStrategy(intervalTicks: 2), BacktestTestData.RisingTicks(12)));
        var result = await session.Completion;

        result.Status.Should().Be(PaperTradingRunStatus.Completed);
        result.TicksProcessed.Should().Be(12);
        session.IsRunning.Should().BeFalse();
        result.FinalState.TradingMode.Should().Be(TradingMode.Paper);
    }

    [Fact]
    public async Task Stops_cleanly_via_StopAsync()
    {
        // RealTime + endlose Verzögerung: nach Tick 1 blockiert der Feed, bis Stop abbricht.
        var session = Engine.Start(Request(
            new TestSignalStrategy(intervalTicks: 2), BacktestTestData.RisingTicks(10),
            replay: ReplayOptions.Realtime,
            delay: (_, ct) => Task.Delay(Timeout.Infinite, ct)));

        await WaitUntilAsync(() => session.GetState().TicksProcessed >= 1, "Tick 1 verarbeitet");
        var result = await session.StopAsync();

        result.Status.Should().Be(PaperTradingRunStatus.Stopped);
        result.TicksProcessed.Should().Be(1);
        session.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task External_cancellation_token_cancels_session()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var session = Engine.Start(
            Request(new TestSignalStrategy(intervalTicks: 2), BacktestTestData.RisingTicks(10)), cts.Token);
        var result = await session.Completion;

        result.Status.Should().Be(PaperTradingRunStatus.Cancelled);
        result.OrdersSubmitted.Should().Be(0);
    }

    [Fact]
    public async Task Pause_skips_new_signals_and_resume_continues()
    {
        var gate = new SemaphoreSlim(0);
        var strategy = new CountingStrategy(new TestSignalStrategy(intervalTicks: 1));
        var session = Engine.Start(Request(
            strategy, BacktestTestData.RisingTicks(3),
            replay: ReplayOptions.Realtime,
            delay: async (_, ct) => await gate.WaitAsync(ct)));

        await WaitUntilAsync(() => session.GetState().TicksProcessed >= 1, "Tick 1");
        strategy.TickCalls.Should().Be(1);

        session.Pause();
        session.IsPaused.Should().BeTrue();
        gate.Release(); // Tick 2 fließt WÄHREND Pause
        await WaitUntilAsync(() => session.GetState().TicksProcessed >= 2, "Tick 2");
        strategy.TickCalls.Should().Be(1); // Strategie wurde NICHT aufgerufen

        session.Resume();
        gate.Release(); // Tick 3 nach Resume
        var result = await session.Completion;

        strategy.TickCalls.Should().Be(2); // Tick 1 + Tick 3
        result.TicksProcessed.Should().Be(3);
        result.Status.Should().Be(PaperTradingRunStatus.Completed);
    }

    // ----------------------------- Fail-closed -------------------------------

    [Fact]
    public async Task Feed_disconnected_blocks_all_orders()
    {
        var session = Engine.Start(Request(
            new TestSignalStrategy(intervalTicks: 2), BacktestTestData.RisingTicks(10),
            safety: new PaperSafetyMonitor(marketDataConnected: false)));
        var result = await session.Completion;

        result.OrdersSubmitted.Should().Be(0);
        result.OrdersRejectedByRisk.Should().BeGreaterThan(0);
        result.FinalState.RiskStatus!.RejectionReason.Should().Be(RiskRejectionReason.MarketDataDisconnected);
    }

    [Fact]
    public async Task Active_kill_switch_blocks_all_orders()
    {
        var session = Engine.Start(Request(
            new TestSignalStrategy(intervalTicks: 2), BacktestTestData.RisingTicks(10),
            killSwitch: new PaperKillSwitch(active: true)));
        var result = await session.Completion;

        result.OrdersSubmitted.Should().Be(0);
        result.FinalState.RiskStatus!.RejectionReason.Should().Be(RiskRejectionReason.KillSwitchActive);
        result.FinalState.KillSwitchActive.Should().BeTrue();
    }

    [Fact]
    public async Task Missing_instrument_profile_blocks_all_orders()
    {
        var session = Engine.Start(Request(
            new TestSignalStrategy(intervalTicks: 2), BacktestTestData.RisingTicks(10),
            includeInstrument: false));
        var result = await session.Completion;

        result.OrdersSubmitted.Should().Be(0);
        result.FinalState.RiskStatus!.RejectionReason.Should().Be(RiskRejectionReason.MissingInstrumentProfile);
    }

    [Fact]
    public async Task Missing_fee_profile_blocks_all_orders()
    {
        var session = Engine.Start(Request(
            new TestSignalStrategy(intervalTicks: 2), BacktestTestData.RisingTicks(10),
            includeFee: false));
        var result = await session.Completion;

        result.OrdersSubmitted.Should().Be(0);
        result.FinalState.RiskStatus!.RejectionReason.Should().Be(RiskRejectionReason.MissingFeeProfile);
    }

    [Fact]
    public async Task Risk_rejection_prevents_order()
    {
        // 10 Kontrakte angefragt > MaxContracts 5 -> jede Order abgelehnt.
        var session = Engine.Start(Request(
            new TestSignalStrategy(intervalTicks: 2, quantity: 10), BacktestTestData.RisingTicks(10)));
        var result = await session.Completion;

        result.OrdersSubmitted.Should().Be(0);
        result.FinalState.RiskStatus!.RejectionReason.Should().Be(RiskRejectionReason.MaxContractsExceeded);
    }

    [Fact]
    public async Task Broker_rejected_order_creates_no_position()
    {
        var session = Engine.Start(Request(
            new TestSignalStrategy(intervalTicks: 2), BacktestTestData.RisingTicks(10),
            rejectOrder: _ => true));
        var result = await session.Completion;

        result.OrdersRejectedByBroker.Should().BeGreaterThan(0);
        result.ClosedTrades.Should().BeEmpty();
        (result.FinalState.CurrentPosition?.Quantity ?? 0).Should().Be(0);
    }

    // ----------------------------- PnL / Fills -------------------------------

    [Fact]
    public async Task Approved_signal_produces_simulated_order_and_fill()
    {
        var session = Engine.Start(Request(new TestSignalStrategy(intervalTicks: 2), BacktestTestData.RisingTicks(12)));
        var result = await session.Completion;

        result.OrdersSubmitted.Should().BeGreaterThan(0);
        result.FinalState.FilledOrders.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Gross_and_net_pnl_are_exact_for_known_scenario()
    {
        // 6 Ticks steigend um 5 Punkte: Long@Fill 20010, Close@Fill 20020 -> 40 Ticks * 5.00 = 200 Gross.
        // Fees: 2 Fills * 2.15 = 4.30 -> Net 195.70. Drittes Signal bleibt unfilled.
        var session = Engine.Start(Request(
            new TestSignalStrategy(intervalTicks: 2), BacktestTestData.RisingTicks(6)));
        var result = await session.Completion;

        result.TicksProcessed.Should().Be(6);
        result.SignalsGenerated.Should().Be(3);
        result.OrdersSubmitted.Should().Be(3);
        result.OrdersUnfilledAtEnd.Should().Be(1);

        result.ClosedTrades.Should().HaveCount(1);
        var trade = result.ClosedTrades[0];
        trade.GrossPnL.Should().Be(200.00m);
        trade.Fees.TotalFees.Should().Be(4.30m);
        trade.NetPnL.Should().Be(195.70m);

        result.FinalState.GrossPnL.Should().Be(200.00m);
        result.FinalState.NetPnL.Should().Be(195.70m);
        result.FinalState.TotalFees.Should().Be(4.30m);
        result.FinalState.TradesToday.Should().Be(1);
    }

    [Fact]
    public async Task Net_equals_gross_minus_fees()
    {
        var session = Engine.Start(Request(new TestSignalStrategy(intervalTicks: 2), BacktestTestData.RisingTicks(14)));
        var result = await session.Completion;

        result.FinalState.TotalFees.Should().BeGreaterThan(0m);
        (result.FinalState.GrossPnL - result.FinalState.TotalFees).Should().Be(result.FinalState.NetPnL);
    }

    [Fact]
    public async Task Slippage_from_fee_profile_is_applied_and_reported()
    {
        var session = Engine.Start(Request(
            new TestSignalStrategy(intervalTicks: 2), BacktestTestData.RisingTicks(6),
            fee: BacktestTestData.Fee(slippageTicks: 2m)));
        var result = await session.Completion;

        result.FinalState.TotalSlippage.Should().BeGreaterThan(0m);
        // Slippage steckt im Fill-Preis -> Gross ist schlechter als ohne Slippage (200).
        result.ClosedTrades[0].GrossPnL.Should().BeLessThan(200.00m);
    }

    // ----------------------------- Exit-aware / Determinism ------------------

    [Fact]
    public async Task Exit_aware_risk_allows_closing_with_max_open_positions_one()
    {
        var session = Engine.Start(Request(
            new TestSignalStrategy(intervalTicks: 2), BacktestTestData.RisingTicks(14),
            risk: BacktestTestData.Risk() with { MaxOpenPositions = 1 }));
        var result = await session.Completion;

        result.Status.Should().Be(PaperTradingRunStatus.Completed);
        result.ClosedTrades.Should().NotBeEmpty(); // Positionen konnten trotz Limit geschlossen werden
    }

    [Fact]
    public async Task Session_is_deterministic_for_same_replay_data()
    {
        var ticks = BacktestTestData.RisingTicks(16);

        var r1 = await Engine.Start(Request(new TestSignalStrategy(intervalTicks: 2), ticks)).Completion;
        var r2 = await Engine.Start(Request(new TestSignalStrategy(intervalTicks: 2), ticks)).Completion;

        r1.TicksProcessed.Should().Be(r2.TicksProcessed);
        r1.SignalsGenerated.Should().Be(r2.SignalsGenerated);
        r1.OrdersSubmitted.Should().Be(r2.OrdersSubmitted);
        r1.FinalState.GrossPnL.Should().Be(r2.FinalState.GrossPnL);
        r1.FinalState.NetPnL.Should().Be(r2.FinalState.NetPnL);
        r1.FinalState.TotalFees.Should().Be(r2.FinalState.TotalFees);
        r1.ClosedTrades.Should().BeEquivalentTo(r2.ClosedTrades);
    }

    // ----------------------------- Journal -----------------------------------

    [Fact]
    public async Task Journal_records_closed_trades_with_paper_mode()
    {
        var session = Engine.Start(Request(new TestSignalStrategy(intervalTicks: 2), BacktestTestData.RisingTicks(6)));
        var result = await session.Completion;

        session.JournalEntries.Should().HaveCount(result.ClosedTrades.Count);
        session.JournalEntries[0].Mode.Should().Be(TradingMode.Paper);
        session.JournalEntries[0].Trade.NetPnL.Should().Be(195.70m);
    }

    // ----------------------------- Safety by construction --------------------

    [Fact]
    public void PaperTrading_assembly_has_no_network_references()
    {
        var referenced = typeof(PaperTradingEngine).Assembly.GetReferencedAssemblies();
        referenced.Should().NotContain(a => a.Name!.StartsWith("System.Net"));
    }

    [Fact]
    public void Engine_has_no_broker_or_network_constructor_dependencies()
    {
        typeof(PaperTradingEngine).GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Should().BeEmpty(); // parameterlos: keinerlei externe Abhängigkeiten
    }
}
