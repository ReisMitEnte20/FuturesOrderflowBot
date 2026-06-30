using FluentAssertions;
using TradingBot.Application.Fees;
using TradingBot.Application.Pnl;
using TradingBot.Application.Positions;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Tests.Pnl;
using Xunit;

namespace TradingBot.Tests.Positions;

public class PositionManagerTests
{
    private static PositionManager NewSut()
    {
        var fee = new FeeCalculator();
        return new PositionManager(fee, new PnLCalculator(fee));
    }

    private static readonly InstrumentProfile Nq = TestProfiles.Nq();
    private static readonly FeeProfile NqFee = TestProfiles.NqFees();
    private static readonly DateTimeOffset T = new(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);

    private static FillEvent Fill(int qty, decimal price, OrderSide side) => new()
    {
        OrderId = Guid.NewGuid(), Symbol = "NQ", Side = side, Quantity = qty, FillPrice = price, Timestamp = T
    };

    private static Position Apply(PositionManager pm, int qty, decimal price, OrderSide side)
        => pm.ApplyFill(Fill(qty, price, side), Nq, NqFee);

    // ----------------------------- Aufbau / Average --------------------------

    [Fact]
    public void Full_fill_opens_long_position()
    {
        var pm = NewSut();
        var p = Apply(pm, 3, 20000m, OrderSide.Buy);

        p.Side.Should().Be(PositionSide.Long);
        p.Quantity.Should().Be(3);
        p.AverageEntryPrice.Should().Be(20000m);
    }

    [Fact]
    public void Long_average_entry_is_weighted()
    {
        var pm = NewSut();
        Apply(pm, 2, 20000m, OrderSide.Buy);
        var p = Apply(pm, 2, 20010m, OrderSide.Buy);

        p.Quantity.Should().Be(4);
        p.AverageEntryPrice.Should().Be(20005m); // (2*20000 + 2*20010)/4
    }

    [Fact]
    public void Short_average_entry_is_weighted()
    {
        var pm = NewSut();
        Apply(pm, 2, 20000m, OrderSide.Sell);
        var p = Apply(pm, 2, 20010m, OrderSide.Sell);

        p.Side.Should().Be(PositionSide.Short);
        p.Quantity.Should().Be(4);
        p.AverageEntryPrice.Should().Be(20005m);
    }

    [Fact]
    public void Partial_fills_accumulate_into_position()
    {
        var pm = NewSut();
        Apply(pm, 1, 20000m, OrderSide.Buy);
        var p = Apply(pm, 2, 20000m, OrderSide.Buy);

        p.Quantity.Should().Be(3);
        p.Side.Should().Be(PositionSide.Long);
    }

    [Fact]
    public void Fees_accumulate_per_side_per_fill()
    {
        var pm = NewSut();
        var p = Apply(pm, 1, 20000m, OrderSide.Buy);
        p.Fees.TotalCommissionsAndFees.Should().Be(2.15m); // eine Seite, 1 Kontrakt
        p.Fees.SlippageCost.Should().Be(0m);               // echte Fill-Preise -> keine separate Slippage
    }

    // ----------------------------- Teilverkauf -------------------------------

    [Fact]
    public void Partial_close_long_realizes_pnl_and_keeps_remainder()
    {
        var pm = NewSut();
        Apply(pm, 3, 20000m, OrderSide.Buy);
        var p = Apply(pm, 1, 20010m, OrderSide.Sell);

        p.Side.Should().Be(PositionSide.Long);
        p.Quantity.Should().Be(2);
        p.AverageEntryPrice.Should().Be(20000m);
        p.RealizedGrossPnL.Should().Be(200m); // 40 ticks * 5 * 1
    }

    [Fact]
    public void Partial_close_short_realizes_pnl_and_keeps_remainder()
    {
        var pm = NewSut();
        Apply(pm, 3, 20000m, OrderSide.Sell);
        var p = Apply(pm, 1, 19990m, OrderSide.Buy);

        p.Side.Should().Be(PositionSide.Short);
        p.Quantity.Should().Be(2);
        p.RealizedGrossPnL.Should().Be(200m); // short profits as price falls
    }

    // ----------------------------- Glattstellung -----------------------------

    [Fact]
    public void Long_to_flat_realizes_gross_and_net()
    {
        var pm = NewSut();
        Apply(pm, 2, 20000m, OrderSide.Buy);
        var p = Apply(pm, 2, 20010m, OrderSide.Sell);

        p.Side.Should().Be(PositionSide.Flat);
        p.Quantity.Should().Be(0);
        p.RealizedGrossPnL.Should().Be(400m);             // 40 ticks * 5 * 2
        p.Fees.TotalFees.Should().Be(8.60m);              // 2.15 * 2 sides * 2 contracts
        p.RealizedNetPnL.Should().Be(391.40m);            // 400 - 8.60
    }

    [Fact]
    public void Short_to_flat_realizes_gross()
    {
        var pm = NewSut();
        Apply(pm, 2, 20010m, OrderSide.Sell);
        var p = Apply(pm, 2, 20000m, OrderSide.Buy);

        p.Side.Should().Be(PositionSide.Flat);
        p.RealizedGrossPnL.Should().Be(400m);
    }

    [Fact]
    public void Closed_position_is_not_in_open_positions()
    {
        var pm = NewSut();
        Apply(pm, 1, 20000m, OrderSide.Buy);
        Apply(pm, 1, 20010m, OrderSide.Sell);

        pm.OpenPositions.Should().BeEmpty();
        pm.GetPosition("NQ")!.RealizedGrossPnL.Should().Be(200m); // Flat-Snapshot behält realized PnL
    }

    // ----------------------------- Flip --------------------------------------

    [Fact]
    public void Flip_long_to_short_closes_and_reverses()
    {
        var pm = NewSut();
        Apply(pm, 2, 20000m, OrderSide.Buy);
        var p = Apply(pm, 3, 20010m, OrderSide.Sell); // close 2 long, open 1 short

        p.Side.Should().Be(PositionSide.Short);
        p.Quantity.Should().Be(1);
        p.AverageEntryPrice.Should().Be(20010m);
        p.RealizedGrossPnL.Should().Be(400m); // closed 2 long @ +40 ticks
    }

    [Fact]
    public void Flip_short_to_long_closes_and_reverses()
    {
        var pm = NewSut();
        Apply(pm, 2, 20000m, OrderSide.Sell);
        var p = Apply(pm, 3, 19990m, OrderSide.Buy); // close 2 short, open 1 long

        p.Side.Should().Be(PositionSide.Long);
        p.Quantity.Should().Be(1);
        p.AverageEntryPrice.Should().Be(19990m);
        p.RealizedGrossPnL.Should().Be(400m);
    }

    // ----------------------------- Unrealized --------------------------------

    [Fact]
    public void Unrealized_pnl_marks_to_market()
    {
        var pm = NewSut();
        Apply(pm, 2, 20000m, OrderSide.Buy);
        var p = pm.MarkToMarket("NQ", 20010m, Nq);

        p.UnrealizedGrossPnL.Should().Be(400m); // 40 ticks * 5 * 2
    }

    [Fact]
    public void Unrealized_is_zero_when_flat()
    {
        var pm = NewSut();
        Apply(pm, 1, 20000m, OrderSide.Buy);
        Apply(pm, 1, 20010m, OrderSide.Sell);
        var p = pm.MarkToMarket("NQ", 20050m, Nq);

        p.UnrealizedGrossPnL.Should().Be(0m);
    }

    // ----------------------------- Reconciliation ----------------------------

    [Fact]
    public void Reconcile_true_when_local_matches_broker()
    {
        var pm = NewSut();
        Apply(pm, 2, 20000m, OrderSide.Buy);
        var broker = new Position { AccountId = "B", Symbol = "NQ", Side = PositionSide.Long, Quantity = 2 };

        pm.Reconcile("NQ", broker).Should().BeTrue();
    }

    [Fact]
    public void Reconcile_false_on_quantity_mismatch()
    {
        var pm = NewSut();
        Apply(pm, 2, 20000m, OrderSide.Buy);
        var broker = new Position { AccountId = "B", Symbol = "NQ", Side = PositionSide.Long, Quantity = 1 };

        pm.Reconcile("NQ", broker).Should().BeFalse();
    }

    [Fact]
    public void Reconcile_true_when_both_flat()
    {
        var pm = NewSut();
        pm.Reconcile("NQ", null).Should().BeTrue();
    }

    // ----------------------------- Gatekeeper-only ---------------------------

    [Fact]
    public void PositionManager_has_no_execution_dependency()
    {
        var ctorParams = typeof(PositionManager).GetConstructors()[0].GetParameters();
        ctorParams.Should().NotContain(p => p.ParameterType == typeof(IBrokerExecutionAdapter));
    }
}
