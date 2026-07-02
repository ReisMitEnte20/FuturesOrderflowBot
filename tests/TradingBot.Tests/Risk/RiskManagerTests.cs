using FluentAssertions;
using Moq;
using TradingBot.Application.Risk;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using Xunit;
using static TradingBot.Tests.RiskManagement.RiskTestData;

namespace TradingBot.Tests.RiskManagement;

public class RiskManagerTests
{
    private static RiskManager Sut(
        Mock<IKillSwitchService>? killSwitch = null,
        Mock<ISafetyMonitor>? safety = null,
        Mock<IClock>? clock = null)
        => new((killSwitch ?? KillSwitch()).Object,
               (safety ?? Safety()).Object,
               (clock ?? Clock()).Object);

    // ----------------------------- Approval ----------------------------------

    [Fact]
    public void Approves_valid_setup()
    {
        var d = Sut().Evaluate(Request());

        d.Approved.Should().BeTrue();
        d.RejectionReason.Should().Be(RiskRejectionReason.None);
        d.ApprovedContracts.Should().Be(1);
        d.RemainingDailyLoss.Should().Be(1000m);
        d.RemainingTrades.Should().Be(10);
        d.RiskUtilizationPercent.Should().Be(0m);
    }

    // ----------------------------- Hard stops --------------------------------

    [Fact]
    public void Rejects_when_kill_switch_active()
    {
        var d = Sut(killSwitch: KillSwitch(active: true)).Evaluate(Request());

        d.Approved.Should().BeFalse();
        d.RejectionReason.Should().Be(RiskRejectionReason.KillSwitchActive);
        d.ApprovedContracts.Should().Be(0);
        d.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Rejects_when_broker_disconnected()
    {
        var d = Sut(safety: Safety(broker: false)).Evaluate(Request());
        d.RejectionReason.Should().Be(RiskRejectionReason.BrokerDisconnected);
    }

    [Fact]
    public void Rejects_when_market_data_disconnected()
    {
        var d = Sut(safety: Safety(feed: false)).Evaluate(Request());
        d.RejectionReason.Should().Be(RiskRejectionReason.MarketDataDisconnected);
    }

    [Fact]
    public void Rejects_on_position_mismatch()
    {
        var d = Sut().Evaluate(Request() with { PositionReconciled = false });
        d.RejectionReason.Should().Be(RiskRejectionReason.PositionMismatch);
    }

    // ----------------------------- Missing profiles --------------------------

    [Fact]
    public void Rejects_when_risk_config_missing()
    {
        var d = Sut().Evaluate(Request() with { Risk = null });
        d.RejectionReason.Should().Be(RiskRejectionReason.MissingRiskConfig);
    }

    [Fact]
    public void Rejects_when_instrument_profile_missing()
    {
        var d = Sut().Evaluate(Request() with { Instrument = null });
        d.RejectionReason.Should().Be(RiskRejectionReason.MissingInstrumentProfile);
    }

    [Fact]
    public void Rejects_when_fee_profile_missing()
    {
        var d = Sut().Evaluate(Request() with { Fee = null });
        d.RejectionReason.Should().Be(RiskRejectionReason.MissingFeeProfile);
    }

    [Fact]
    public void Rejects_when_broker_profile_missing()
    {
        var d = Sut().Evaluate(Request() with { Broker = null });
        d.RejectionReason.Should().Be(RiskRejectionReason.MissingBrokerProfile);
    }

    // ----------------------------- Symbol & session --------------------------

    [Fact]
    public void Rejects_when_symbol_does_not_match_instrument()
    {
        var d = Sut().Evaluate(Request() with { Signal = Signal(symbol: "ES") });
        d.RejectionReason.Should().Be(RiskRejectionReason.SymbolNotAllowed);
    }

    [Fact]
    public void Rejects_outside_session()
    {
        var risk = Risk() with { EnforceSession = true };
        var atNight = Clock(new DateTimeOffset(2026, 6, 23, 20, 0, 0, TimeSpan.Zero));

        var d = Sut(clock: atNight).Evaluate(Request() with { Risk = risk });

        d.RejectionReason.Should().Be(RiskRejectionReason.OutsideTradingSession);
    }

    [Fact]
    public void Approves_inside_session()
    {
        var risk = Risk() with { EnforceSession = true };
        var atNoon = Clock(Noon);

        var d = Sut(clock: atNoon).Evaluate(Request() with { Risk = risk });

        d.Approved.Should().BeTrue();
    }

    // ----------------------------- Daily limits ------------------------------

    [Fact]
    public void Rejects_when_max_daily_loss_reached()
    {
        var state = DailyRiskState.Start(Date) with { NetPnL = -1000m };
        var d = Sut().Evaluate(Request() with { DailyState = state });
        d.RejectionReason.Should().Be(RiskRejectionReason.MaxDailyLossReached);
    }

    [Fact]
    public void Rejects_when_max_trades_per_day_reached()
    {
        var state = DailyRiskState.Start(Date) with { TradesTaken = 10 };
        var d = Sut().Evaluate(Request() with { DailyState = state });
        d.RejectionReason.Should().Be(RiskRejectionReason.MaxTradesPerDayReached);
    }

    [Fact]
    public void Rejects_after_consecutive_losses()
    {
        var state = DailyRiskState.Start(Date) with { ConsecutiveLosses = 3 };
        var d = Sut().Evaluate(Request() with { DailyState = state });
        d.RejectionReason.Should().Be(RiskRejectionReason.ConsecutiveLossLimitReached);
    }

    [Fact]
    public void Rejects_when_max_orders_per_minute_reached()
    {
        var state = DailyRiskState.Start(Date) with { OrdersThisMinute = 10 };
        var d = Sut().Evaluate(Request() with { DailyState = state });
        d.RejectionReason.Should().Be(RiskRejectionReason.MaxOrdersPerMinuteExceeded);
    }

    [Fact]
    public void Rejects_when_max_open_positions_reached()
    {
        var d = Sut().Evaluate(Request() with { OpenPositionsCount = 1 });
        d.RejectionReason.Should().Be(RiskRejectionReason.MaxOpenPositionsReached);
    }

    // ----------------------------- Contracts ---------------------------------

    [Fact]
    public void Rejects_when_requested_contracts_exceed_max()
    {
        var d = Sut().Evaluate(Request() with { RequestedContracts = 10 });
        d.RejectionReason.Should().Be(RiskRejectionReason.MaxContractsExceeded);
        d.ApprovedContracts.Should().Be(0);
    }

    [Fact]
    public void Rejects_when_loss_per_contract_exceeds_max_loss_per_trade()
    {
        // Stop 300 Ticks * TickValue 5 = 1500 > MaxLossPerTrade 1000.
        var signal = Signal() with { SuggestedStopLossTicks = 300 };
        var d = Sut().Evaluate(Request() with { Signal = signal });
        d.RejectionReason.Should().Be(RiskRejectionReason.MaxLossPerTradeExceeded);
    }

    [Fact]
    public void Caps_ApprovedContracts_by_max_loss_per_trade()
    {
        // Stop 40 Ticks * 5 = 200/Kontrakt, MaxLossPerTrade 300 -> floor(300/200) = 1.
        var risk = Risk() with { MaxLossPerTrade = 300m };
        var d = Sut().Evaluate(Request() with { Risk = risk, RequestedContracts = 2 });

        d.Approved.Should().BeTrue();
        d.RequestedContracts.Should().Be(2);
        d.ApprovedContracts.Should().Be(1); // korrekt begrenzt
    }

    // ----------------------------- Metrics -----------------------------------

    [Fact]
    public void RemainingDailyLoss_is_correct()
    {
        var state = DailyRiskState.Start(Date) with { NetPnL = -300m };
        var d = Sut().Evaluate(Request() with { DailyState = state });
        d.RemainingDailyLoss.Should().Be(700m); // 1000 - 300
    }

    [Fact]
    public void RemainingTrades_is_correct()
    {
        var state = DailyRiskState.Start(Date) with { TradesTaken = 3 };
        var d = Sut().Evaluate(Request() with { DailyState = state });
        d.RemainingTrades.Should().Be(7); // 10 - 3
    }

    [Fact]
    public void RiskUtilizationPercent_reflects_daily_loss_budget()
    {
        var state = DailyRiskState.Start(Date) with { NetPnL = -300m };
        var d = Sut().Evaluate(Request() with { DailyState = state });
        d.RiskUtilizationPercent.Should().Be(30m); // 300/1000
    }

    // ----------------------------- Gatekeeper-only ---------------------------

    [Fact]
    public void Does_not_execute_or_flatten_anything()
    {
        var killSwitch = KillSwitch();
        var safety = Safety();

        Sut(killSwitch: killSwitch, safety: safety).Evaluate(Request());

        // Der RiskManager darf niemals ausführen oder flatten.
        killSwitch.Verify(k => k.EmergencyFlattenAsync(It.IsAny<CancellationToken>()), Times.Never);
        killSwitch.Verify(k => k.ActivateAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void RiskManager_has_no_execution_dependency()
    {
        var ctorParams = typeof(RiskManager).GetConstructors()[0].GetParameters();
        ctorParams.Should().NotContain(p => p.ParameterType == typeof(IBrokerExecutionAdapter));
        ctorParams.Should().NotContain(p => p.ParameterType == typeof(IOrderManager));
    }

    // ----------------------------- Exit-aware Risk ---------------------------

    [Fact]
    public void Entry_is_still_blocked_by_max_daily_loss()
    {
        var state = DailyRiskState.Start(Date) with { IsDailyLossLimitHit = true };
        var d = Sut().Evaluate(Request() with { Intent = OrderIntent.Entry, DailyState = state });

        d.Approved.Should().BeFalse();
        d.RejectionReason.Should().Be(RiskRejectionReason.MaxDailyLossReached);
    }

    [Fact]
    public void Close_is_allowed_despite_max_daily_loss_when_position_open()
    {
        var state = DailyRiskState.Start(Date) with { NetPnL = -1000m, IsDailyLossLimitHit = true };
        var d = Sut().Evaluate(Request() with
        {
            Intent = OrderIntent.Close, CurrentOpenContracts = 1, DailyState = state
        });

        d.Approved.Should().BeTrue();
        d.ApprovedContracts.Should().Be(1);
    }

    [Fact]
    public void Reduce_is_allowed_despite_max_trades_reached()
    {
        var state = DailyRiskState.Start(Date) with { TradesTaken = 10 }; // MaxTradesPerDay = 10
        var d = Sut().Evaluate(Request() with
        {
            Intent = OrderIntent.Reduce, CurrentOpenContracts = 2, RequestedContracts = 1, DailyState = state
        });

        d.Approved.Should().BeTrue();
        d.ApprovedContracts.Should().Be(1);
    }

    [Fact]
    public void Close_is_allowed_despite_max_open_positions_reached()
    {
        var d = Sut().Evaluate(Request() with
        {
            Intent = OrderIntent.Close, OpenPositionsCount = 1, CurrentOpenContracts = 1 // MaxOpenPositions = 1
        });

        d.Approved.Should().BeTrue();
    }

    [Fact]
    public void Exit_is_not_blocked_by_consecutive_losses()
    {
        var state = DailyRiskState.Start(Date) with { ConsecutiveLosses = 3 }; // MaxConsecutiveLosses = 3
        var d = Sut().Evaluate(Request() with
        {
            Intent = OrderIntent.Close, CurrentOpenContracts = 1, DailyState = state
        });

        d.Approved.Should().BeTrue();
    }

    [Fact]
    public void Close_without_open_position_is_treated_as_entry()
    {
        var state = DailyRiskState.Start(Date) with { IsDailyLossLimitHit = true };
        var d = Sut().Evaluate(Request() with
        {
            Intent = OrderIntent.Close, CurrentOpenContracts = 0, DailyState = state
        });

        d.Approved.Should().BeFalse();
        d.RejectionReason.Should().Be(RiskRejectionReason.MaxDailyLossReached);
    }

    [Fact]
    public void Exit_is_still_blocked_by_kill_switch()
    {
        var d = Sut(killSwitch: KillSwitch(active: true))
            .Evaluate(Request() with { Intent = OrderIntent.Close, CurrentOpenContracts = 1 });

        d.RejectionReason.Should().Be(RiskRejectionReason.KillSwitchActive);
    }

    [Fact]
    public void Exit_is_still_blocked_by_broker_disconnect()
    {
        var d = Sut(safety: Safety(broker: false))
            .Evaluate(Request() with { Intent = OrderIntent.Close, CurrentOpenContracts = 1 });

        d.RejectionReason.Should().Be(RiskRejectionReason.BrokerDisconnected);
    }

    [Fact]
    public void Exit_caps_approved_contracts_to_open_position()
    {
        var d = Sut().Evaluate(Request() with
        {
            Intent = OrderIntent.Close, CurrentOpenContracts = 2, RequestedContracts = 5
        });

        d.Approved.Should().BeTrue();
        d.ApprovedContracts.Should().Be(2); // nie mehr schließen als offen
    }

    // ----------------------------- Misuse throws -----------------------------

    [Fact]
    public void Throws_on_flat_signal()
    {
        var act = () => Sut().Evaluate(Request() with { Signal = Signal(dir: SignalDirection.Flat) });
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Throws_on_non_positive_requested_contracts()
    {
        var act = () => Sut().Evaluate(Request() with { RequestedContracts = 0 });
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
