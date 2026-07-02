using FluentAssertions;
using TradingBot.Application.Simulation;
using TradingBot.Backtesting.Execution;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using Xunit;

namespace TradingBot.Tests.Backtesting;

public class BacktestExecutionAdapterTests
{
    private static readonly InstrumentProfile Nq = BacktestTestData.Instrument();

    private static BacktestExecutionAdapter New(decimal slippage = 0m, Func<OrderRequest, bool>? reject = null)
        => new(new FillModel(), Nq, slippage, reject);

    private static OrderRequest MarketOrder(OrderSide side = OrderSide.Buy) => new()
    {
        IdempotencyKey = "k", AccountId = "A", Symbol = "NQ", BrokerSymbol = "NQ",
        Side = side, OrderType = OrderType.Market, Quantity = 1
    };

    [Fact]
    public async Task Market_order_is_pending_until_next_tick_then_fills()
    {
        var sut = New();
        FillEvent? fill = null;
        sut.Filled += (_, f) => fill = f;

        var result = await sut.SubmitOrderAsync(MarketOrder());
        result.Status.Should().Be(OrderStatus.New);
        sut.PendingCount.Should().Be(1);
        fill.Should().BeNull(); // noch nicht gefüllt

        sut.ProcessTick(BacktestTestData.Tick(0, 20000m));
        fill.Should().NotBeNull();
        fill!.FillPrice.Should().Be(20000m);
        sut.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task Reject_predicate_produces_rejected_and_no_pending()
    {
        var sut = New(reject: _ => true);
        var result = await sut.SubmitOrderAsync(MarketOrder());

        result.Status.Should().Be(OrderStatus.Rejected);
        result.IsRejected.Should().BeTrue();
        sut.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task Limit_order_fills_only_when_touched()
    {
        var sut = New();
        var order = MarketOrder() with { OrderType = OrderType.Limit, LimitPrice = 20000m };
        await sut.SubmitOrderAsync(order);

        sut.ProcessTick(BacktestTestData.Tick(0, 20001m)); // nicht berührt (buy limit)
        sut.PendingCount.Should().Be(1);

        sut.ProcessTick(BacktestTestData.Tick(1, 19999m)); // berührt
        sut.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task Cancel_removes_pending_order()
    {
        var sut = New();
        var order = MarketOrder();
        await sut.SubmitOrderAsync(order);
        await sut.CancelOrderAsync(order.OrderId);
        sut.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task Slippage_cost_accumulates_on_market_fill()
    {
        var sut = New(slippage: 2m);
        await sut.SubmitOrderAsync(MarketOrder());
        sut.ProcessTick(BacktestTestData.Tick(0, 20000m));
        sut.TotalSlippageCost.Should().Be(10m); // 2 * 5 * 1
    }

    [Fact]
    public async Task GetBrokerPosition_returns_null_no_external_call()
    {
        var sut = New();
        (await sut.GetBrokerPositionAsync("NQ")).Should().BeNull();
    }

    [Fact]
    public async Task Connect_and_disconnect_toggle_local_state_only()
    {
        var sut = New();
        sut.IsConnected.Should().BeFalse();
        await sut.ConnectAsync();
        sut.IsConnected.Should().BeTrue();
        await sut.DisconnectAsync();
        sut.IsConnected.Should().BeFalse();
    }
}
