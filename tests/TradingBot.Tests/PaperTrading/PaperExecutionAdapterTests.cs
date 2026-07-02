using FluentAssertions;
using TradingBot.Application.Simulation;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.PaperTrading.Execution;
using TradingBot.Tests.Backtesting;
using Xunit;

namespace TradingBot.Tests.PaperTrading;

public class PaperExecutionAdapterTests
{
    private static readonly InstrumentProfile Nq = BacktestTestData.Instrument();

    private static PaperExecutionAdapter New(
        decimal slippage = 0m, Func<OrderRequest, bool>? reject = null, InstrumentProfile? instrument = null,
        bool includeInstrument = true)
        => new(new FillModel(), includeInstrument ? (instrument ?? Nq) : null, slippage, reject);

    private static OrderRequest Order(
        OrderSide side = OrderSide.Buy, OrderType type = OrderType.Market,
        decimal? limit = null, decimal? stop = null) => new()
    {
        IdempotencyKey = Guid.NewGuid().ToString("N"), AccountId = "A", Symbol = "NQ", BrokerSymbol = "NQ",
        Side = side, OrderType = type, Quantity = 1, LimitPrice = limit, StopPrice = stop
    };

    [Fact]
    public async Task Market_order_pending_until_next_tick_then_fills()
    {
        var sut = New();
        FillEvent? fill = null;
        sut.Filled += (_, f) => fill = f;

        var result = await sut.SubmitOrderAsync(Order());
        result.Status.Should().Be(OrderStatus.New);
        sut.PendingCount.Should().Be(1);
        fill.Should().BeNull();

        sut.ProcessTick(BacktestTestData.Tick(0, 20000m));

        fill.Should().NotBeNull();
        fill!.FillPrice.Should().Be(20000m);
        sut.PendingCount.Should().Be(0);
        sut.FilledCount.Should().Be(1);
    }

    [Fact]
    public async Task Reject_predicate_rejects_without_pending()
    {
        var sut = New(reject: _ => true);
        var result = await sut.SubmitOrderAsync(Order());

        result.Status.Should().Be(OrderStatus.Rejected);
        sut.PendingCount.Should().Be(0);
        sut.RejectedCount.Should().Be(1);
    }

    [Fact]
    public async Task Missing_instrument_rejects_submit_failclosed()
    {
        var sut = New(includeInstrument: false);
        var result = await sut.SubmitOrderAsync(Order());

        result.Status.Should().Be(OrderStatus.Rejected);
        result.Message.Should().Contain("InstrumentProfile");
        sut.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task Limit_order_fills_only_on_touch()
    {
        var sut = New();
        await sut.SubmitOrderAsync(Order(type: OrderType.Limit, limit: 20000m)); // buy limit

        sut.ProcessTick(BacktestTestData.Tick(0, 20001m)); // nicht berührt
        sut.PendingCount.Should().Be(1);

        sut.ProcessTick(BacktestTestData.Tick(1, 19999m)); // berührt
        sut.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task Stop_order_fills_only_on_trigger()
    {
        var sut = New();
        await sut.SubmitOrderAsync(Order(type: OrderType.Stop, stop: 20010m)); // buy stop

        sut.ProcessTick(BacktestTestData.Tick(0, 20005m)); // nicht ausgelöst
        sut.PendingCount.Should().Be(1);

        sut.ProcessTick(BacktestTestData.Tick(1, 20010m)); // ausgelöst
        sut.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task Cancel_removes_pending_order()
    {
        var sut = New();
        var order = Order();
        await sut.SubmitOrderAsync(order);

        var result = await sut.CancelOrderAsync(order.OrderId);

        result.Status.Should().Be(OrderStatus.Cancelled);
        sut.PendingCount.Should().Be(0);
        sut.CancelledCount.Should().Be(1);
    }

    [Fact]
    public async Task Replace_updates_pending_order()
    {
        var sut = New();
        var order = Order(type: OrderType.Limit, limit: 19000m); // weit weg -> füllt nicht
        await sut.SubmitOrderAsync(order);

        await sut.ReplaceOrderAsync(order with { LimitPrice = 20000m });
        sut.ProcessTick(BacktestTestData.Tick(0, 19999m)); // berührt neues Limit

        sut.PendingCount.Should().Be(0);
        sut.FilledCount.Should().Be(1);
    }

    [Fact]
    public async Task Slippage_cost_accumulates_on_market_fills()
    {
        var sut = New(slippage: 2m);
        await sut.SubmitOrderAsync(Order());
        sut.ProcessTick(BacktestTestData.Tick(0, 20000m));

        sut.TotalSlippageCost.Should().Be(10m); // 2 Ticks * 5.00 * 1
    }

    [Fact]
    public async Task No_external_broker_position_and_local_connect_state()
    {
        var sut = New();
        (await sut.GetBrokerPositionAsync("NQ")).Should().BeNull();

        sut.IsConnected.Should().BeFalse();
        await sut.ConnectAsync();
        sut.IsConnected.Should().BeTrue();
        await sut.DisconnectAsync();
        sut.IsConnected.Should().BeFalse();
    }

    [Fact]
    public void Adapter_has_no_network_or_broker_dependencies()
    {
        var ctorParams = typeof(PaperExecutionAdapter).GetConstructors()[0].GetParameters();
        ctorParams.Should().NotContain(p => p.ParameterType.Namespace!.StartsWith("System.Net"));
        ctorParams.Should().NotContain(p => p.ParameterType == typeof(HttpClient));
    }
}
