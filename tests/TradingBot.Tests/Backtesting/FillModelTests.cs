using FluentAssertions;
using TradingBot.Application.Simulation;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using Xunit;

namespace TradingBot.Tests.Backtesting;

public class FillModelTests
{
    private readonly FillModel _sut = new();
    private static readonly InstrumentProfile Nq = BacktestTestData.Instrument(); // TickSize 0.25, TickValue 5
    private static readonly MarketTick Tick20000 = BacktestTestData.Tick(0, 20000m);

    private static OrderRequest Order(OrderSide side, OrderType type, decimal? limit = null, decimal? stop = null) => new()
    {
        IdempotencyKey = "k", AccountId = "A", Symbol = "NQ", BrokerSymbol = "NQ",
        Side = side, OrderType = type, Quantity = 1, LimitPrice = limit, StopPrice = stop
    };

    [Fact]
    public void Market_buy_fills_with_adverse_slippage_up()
    {
        var r = _sut.TryFill(Order(OrderSide.Buy, OrderType.Market), Tick20000, Nq, slippageTicks: 2m);
        r.Should().NotBeNull();
        r!.Event.FillPrice.Should().Be(20000.50m); // +2 ticks * 0.25
        r.SlippageCost.Should().Be(10m);           // 2 * 5 * 1
    }

    [Fact]
    public void Market_sell_fills_with_adverse_slippage_down()
    {
        var r = _sut.TryFill(Order(OrderSide.Sell, OrderType.Market), Tick20000, Nq, slippageTicks: 2m);
        r!.Event.FillPrice.Should().Be(19999.50m);
    }

    [Fact]
    public void Market_no_slippage_fills_at_tick_price()
    {
        var r = _sut.TryFill(Order(OrderSide.Buy, OrderType.Market), Tick20000, Nq, slippageTicks: 0m);
        r!.Event.FillPrice.Should().Be(20000m);
        r.SlippageCost.Should().Be(0m);
    }

    [Fact]
    public void Buy_limit_fills_only_when_price_at_or_below_limit()
    {
        var order = Order(OrderSide.Buy, OrderType.Limit, limit: 20000m);
        _sut.TryFill(order, BacktestTestData.Tick(0, 20001m), Nq, 2m).Should().BeNull();   // zu hoch
        var hit = _sut.TryFill(order, BacktestTestData.Tick(0, 19999m), Nq, 2m);
        hit!.Event.FillPrice.Should().Be(20000m); // Fill zum Limit, keine Slippage
        hit.SlippageCost.Should().Be(0m);
    }

    [Fact]
    public void Sell_limit_fills_only_when_price_at_or_above_limit()
    {
        var order = Order(OrderSide.Sell, OrderType.Limit, limit: 20000m);
        _sut.TryFill(order, BacktestTestData.Tick(0, 19999m), Nq, 2m).Should().BeNull();
        _sut.TryFill(order, BacktestTestData.Tick(0, 20001m), Nq, 2m)!.Event.FillPrice.Should().Be(20000m);
    }

    [Fact]
    public void Buy_stop_triggers_only_at_or_above_stop_then_takes_slippage()
    {
        var order = Order(OrderSide.Buy, OrderType.Stop, stop: 20010m);
        _sut.TryFill(order, BacktestTestData.Tick(0, 20005m), Nq, 2m).Should().BeNull();
        var hit = _sut.TryFill(order, BacktestTestData.Tick(0, 20010m), Nq, 2m);
        hit!.Event.FillPrice.Should().Be(20010.50m); // wird Market -> +Slippage
    }

    [Fact]
    public void Sell_stop_triggers_only_at_or_below_stop()
    {
        var order = Order(OrderSide.Sell, OrderType.Stop, stop: 19990m);
        _sut.TryFill(order, BacktestTestData.Tick(0, 19995m), Nq, 2m).Should().BeNull();
        _sut.TryFill(order, BacktestTestData.Tick(0, 19990m), Nq, 2m)!.Event.FillPrice.Should().Be(19989.50m);
    }
}
