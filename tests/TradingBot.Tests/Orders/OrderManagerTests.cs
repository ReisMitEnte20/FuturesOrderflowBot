using FluentAssertions;
using Moq;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using Xunit;

namespace TradingBot.Tests.Orders;

public class OrderManagerTests
{
    // ----------------------------- Approval / submit -------------------------

    [Fact]
    public async Task Approved_signal_submits_one_order()
    {
        var h = new OrderManagerHarness();
        using var sut = h.Build();

        var state = await sut.ProcessSignalAsync(h.Signal());

        state.Should().NotBeNull();
        state!.Lifecycle.Should().Be(OrderLifecycleState.Accepted);
        h.Adapter.Verify(a => a.SubmitOrderAsync(It.IsAny<OrderRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        sut.Orders.Should().HaveCount(1);
    }

    [Fact]
    public async Task Rejected_risk_creates_no_order()
    {
        var h = new OrderManagerHarness();
        h.Decision = new RiskDecision
        {
            Approved = false, RejectionReason = RiskRejectionReason.KillSwitchActive,
            Message = "Kill Switch", ApprovedContracts = 0
        };
        using var sut = h.Build();

        var state = await sut.ProcessSignalAsync(h.Signal());

        state.Should().BeNull();
        h.Adapter.Verify(a => a.SubmitOrderAsync(It.IsAny<OrderRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        sut.Orders.Should().BeEmpty();
        h.Logger.Warnings.Should().Contain(w => w.Contains("KillSwitchActive"));
    }

    [Fact]
    public async Task ApprovedContracts_is_respected_for_order_quantity()
    {
        var h = new OrderManagerHarness();
        h.Decision = h.Decision with { RequestedContracts = 3, ApprovedContracts = 1 };
        using var sut = h.Build();

        await sut.ProcessSignalAsync(h.Signal(qty: 3));

        h.LastSubmitted.Should().NotBeNull();
        h.LastSubmitted!.Quantity.Should().Be(1); // reduziert auf ApprovedContracts
    }

    // ----------------------------- Idempotency -------------------------------

    [Fact]
    public async Task Duplicate_signal_does_not_submit_twice()
    {
        var h = new OrderManagerHarness();
        using var sut = h.Build();
        var signal = h.Signal();

        var first = await sut.ProcessSignalAsync(signal);
        var second = await sut.ProcessSignalAsync(signal); // gleiche SignalId -> gleicher Key

        h.Adapter.Verify(a => a.SubmitOrderAsync(It.IsAny<OrderRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        sut.Orders.Should().HaveCount(1);
        second!.OrderId.Should().Be(first!.OrderId);
        h.Logger.Warnings.Should().Contain(w => w.Contains("Doppeltes Signal"));
    }

    // ----------------------------- Lifecycle ---------------------------------

    [Fact]
    public async Task Market_order_lifecycle_reaches_filled()
    {
        var h = new OrderManagerHarness();
        using var sut = h.Build();

        var state = await sut.ProcessSignalAsync(h.Signal());
        state!.Lifecycle.Should().Be(OrderLifecycleState.Accepted);

        h.RaiseFill(h.Fill(state.OrderId, 1, 20000m));

        sut.GetOrder(state.OrderId)!.Lifecycle.Should().Be(OrderLifecycleState.Filled);
        sut.GetOrder(state.OrderId)!.FilledQuantity.Should().Be(1);
    }

    [Fact]
    public async Task Limit_order_lifecycle()
    {
        var h = new OrderManagerHarness();
        using var sut = h.Build();

        var state = await sut.ProcessSignalAsync(h.Signal(), OrderType.Limit, limitPrice: 19990m);

        h.LastSubmitted!.OrderType.Should().Be(OrderType.Limit);
        h.LastSubmitted.LimitPrice.Should().Be(19990m);
        state!.Lifecycle.Should().Be(OrderLifecycleState.Accepted);

        h.RaiseFill(h.Fill(state.OrderId, 1, 19990m));
        sut.GetOrder(state.OrderId)!.Lifecycle.Should().Be(OrderLifecycleState.Filled);
    }

    [Fact]
    public async Task Stop_order_lifecycle()
    {
        var h = new OrderManagerHarness();
        using var sut = h.Build();

        var state = await sut.ProcessSignalAsync(h.Signal(), OrderType.Stop, stopPrice: 20010m);

        h.LastSubmitted!.OrderType.Should().Be(OrderType.Stop);
        h.LastSubmitted.StopPrice.Should().Be(20010m);
        state!.Lifecycle.Should().Be(OrderLifecycleState.Accepted);
    }

    [Fact]
    public async Task Limit_entry_without_price_is_not_submitted()
    {
        var h = new OrderManagerHarness();
        using var sut = h.Build();

        var state = await sut.ProcessSignalAsync(h.Signal(), OrderType.Limit, limitPrice: null);

        state.Should().BeNull();
        h.Adapter.Verify(a => a.SubmitOrderAsync(It.IsAny<OrderRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ----------------------------- Fills -> position -------------------------

    [Fact]
    public async Task Full_fill_updates_position_once()
    {
        var h = new OrderManagerHarness();
        using var sut = h.Build();

        var state = await sut.ProcessSignalAsync(h.Signal());
        var fill = h.Fill(state!.OrderId, 1, 20000m);
        h.RaiseFill(fill);

        h.Positions.Verify(p => p.ApplyFill(fill, It.IsAny<InstrumentProfile>(), It.IsAny<FeeProfile>()), Times.Once);
    }

    [Fact]
    public async Task Partial_fills_track_and_update_position_each_time()
    {
        var h = new OrderManagerHarness();
        h.Decision = h.Decision with { RequestedContracts = 2, ApprovedContracts = 2 };
        using var sut = h.Build();

        var state = await sut.ProcessSignalAsync(h.Signal(qty: 2));

        h.RaiseFill(h.Fill(state!.OrderId, 1, 20000m));
        sut.GetOrder(state.OrderId)!.Lifecycle.Should().Be(OrderLifecycleState.PartiallyFilled);
        sut.GetOrder(state.OrderId)!.FilledQuantity.Should().Be(1);

        h.RaiseFill(h.Fill(state.OrderId, 1, 20002m));
        var final = sut.GetOrder(state.OrderId)!;
        final.Lifecycle.Should().Be(OrderLifecycleState.Filled);
        final.FilledQuantity.Should().Be(2);
        final.AverageFillPrice.Should().Be(20001m); // (20000 + 20002)/2

        h.Positions.Verify(p => p.ApplyFill(It.IsAny<FillEvent>(), It.IsAny<InstrumentProfile>(), It.IsAny<FeeProfile>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Rejected_order_produces_no_fill_and_no_position()
    {
        var h = new OrderManagerHarness();
        h.OnSubmit = r => new OrderResult { OrderId = r.OrderId, Status = OrderStatus.Rejected, Message = "rej", Timestamp = OrderManagerHarness.T };
        using var sut = h.Build();

        var state = await sut.ProcessSignalAsync(h.Signal());

        state!.Lifecycle.Should().Be(OrderLifecycleState.Rejected);
        h.Positions.Verify(p => p.ApplyFill(It.IsAny<FillEvent>(), It.IsAny<InstrumentProfile>(), It.IsAny<FeeProfile>()), Times.Never);
    }

    [Fact]
    public async Task Submit_exception_marks_order_failed()
    {
        var h = new OrderManagerHarness();
        h.OnSubmit = _ => throw new InvalidOperationException("network down");
        using var sut = h.Build();

        var state = await sut.ProcessSignalAsync(h.Signal());

        state!.Lifecycle.Should().Be(OrderLifecycleState.Failed);
        h.Logger.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public void Fill_for_unknown_order_is_ignored()
    {
        var h = new OrderManagerHarness();
        using var sut = h.Build();

        h.RaiseFill(h.Fill(Guid.NewGuid(), 1, 20000m)); // keine passende Order

        h.Positions.Verify(p => p.ApplyFill(It.IsAny<FillEvent>(), It.IsAny<InstrumentProfile>(), It.IsAny<FeeProfile>()), Times.Never);
        h.Logger.Warnings.Should().Contain(w => w.Contains("unbekannte Order"));
    }

    // ----------------------------- Cancel / Replace --------------------------

    [Fact]
    public async Task Cancel_marks_order_cancelled()
    {
        var h = new OrderManagerHarness();
        using var sut = h.Build();

        var state = await sut.ProcessSignalAsync(h.Signal());
        var cancelled = await sut.CancelAsync(state!.OrderId);

        cancelled!.Lifecycle.Should().Be(OrderLifecycleState.Cancelled);
    }

    [Fact]
    public async Task Replace_updates_order_without_creating_a_second()
    {
        var h = new OrderManagerHarness();
        using var sut = h.Build();

        var state = await sut.ProcessSignalAsync(h.Signal());
        var replaced = await sut.ReplaceAsync(state!.OrderId, newQuantity: 2);

        replaced!.RequestedQuantity.Should().Be(2);
        sut.Orders.Should().HaveCount(1); // keine doppelte offene Order
        h.Adapter.Verify(a => a.ReplaceOrderAsync(It.IsAny<OrderRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Cancel_unknown_order_returns_null()
    {
        var h = new OrderManagerHarness();
        using var sut = h.Build();

        var result = await sut.CancelAsync(Guid.NewGuid());
        result.Should().BeNull();
    }

    // ----------------------------- Intent classification ---------------------

    [Fact]
    public async Task Signal_from_flat_is_classified_as_entry()
    {
        var h = new OrderManagerHarness(); // CurrentPosition == null (flat)
        using var sut = h.Build();

        await sut.ProcessSignalAsync(h.Signal());

        h.LastRiskRequest!.Intent.Should().Be(OrderIntent.Entry);
    }

    [Fact]
    public async Task Opposite_signal_while_long_is_classified_as_close()
    {
        var h = new OrderManagerHarness
        {
            CurrentPosition = new Position
            {
                AccountId = "ACC1", Symbol = "NQ", Side = PositionSide.Long,
                Quantity = 1, AverageEntryPrice = 20000m
            }
        };
        using var sut = h.Build();

        var shortSignal = new TradeSignal
        {
            StrategyName = "t", Symbol = "NQ", Direction = SignalDirection.Short,
            Timestamp = OrderManagerHarness.T, ReferencePrice = 20000m,
            SuggestedStopLossTicks = 40, SuggestedQuantity = 1
        };

        await sut.ProcessSignalAsync(shortSignal);

        h.LastRiskRequest!.Intent.Should().Be(OrderIntent.Close);
    }
}
