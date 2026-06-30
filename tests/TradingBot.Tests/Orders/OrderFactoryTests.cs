using FluentAssertions;
using TradingBot.Application.Orders;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Tests.Pnl;
using Xunit;

namespace TradingBot.Tests.Orders;

public class OrderFactoryTests
{
    private static readonly InstrumentProfile Nq = TestProfiles.Nq(); // TickSize 0.25
    private static readonly DateTimeOffset T = new(2026, 6, 23, 12, 0, 0, TimeSpan.Zero);

    private static TradeSignal Sig(SignalDirection dir) => new()
    {
        StrategyName = "t", Symbol = "NQ", Direction = dir, Timestamp = T, ReferencePrice = 20000m
    };

    // ----------------------------- Entry -------------------------------------

    [Fact]
    public void BuildEntry_long_is_buy_market()
    {
        var o = OrderFactory.BuildEntry(Sig(SignalDirection.Long), Nq, "ACC", 2, OrderType.Market, "k1", T);

        o.Side.Should().Be(OrderSide.Buy);
        o.OrderType.Should().Be(OrderType.Market);
        o.Quantity.Should().Be(2);
        o.BrokerSymbol.Should().Be(Nq.BrokerSymbol);
        o.IdempotencyKey.Should().Be("k1");
    }

    [Fact]
    public void BuildEntry_short_is_sell()
    {
        var o = OrderFactory.BuildEntry(Sig(SignalDirection.Short), Nq, "ACC", 1, OrderType.Market, "k", T);
        o.Side.Should().Be(OrderSide.Sell);
    }

    [Fact]
    public void BuildEntry_limit_carries_limit_price()
    {
        var o = OrderFactory.BuildEntry(Sig(SignalDirection.Long), Nq, "ACC", 1, OrderType.Limit, "k", T, limitPrice: 19990m);
        o.OrderType.Should().Be(OrderType.Limit);
        o.LimitPrice.Should().Be(19990m);
    }

    [Fact]
    public void BuildEntry_stop_carries_stop_price()
    {
        var o = OrderFactory.BuildEntry(Sig(SignalDirection.Long), Nq, "ACC", 1, OrderType.Stop, "k", T, stopPrice: 20010m);
        o.OrderType.Should().Be(OrderType.Stop);
        o.StopPrice.Should().Be(20010m);
    }

    // ----------------------------- Stop-Loss ---------------------------------

    [Fact]
    public void BuildStopLoss_long_is_sell_stop_below_entry()
    {
        var sl = OrderFactory.BuildStopLoss(PositionSide.Long, Nq, "ACC", "NQ", 20000m, 40, 1, "sl", T);

        sl.Side.Should().Be(OrderSide.Sell);
        sl.OrderType.Should().Be(OrderType.Stop);
        sl.StopPrice.Should().Be(19990m); // 20000 - 40 * 0.25
    }

    [Fact]
    public void BuildStopLoss_short_is_buy_stop_above_entry()
    {
        var sl = OrderFactory.BuildStopLoss(PositionSide.Short, Nq, "ACC", "NQ", 20000m, 40, 1, "sl", T);

        sl.Side.Should().Be(OrderSide.Buy);
        sl.StopPrice.Should().Be(20010m); // 20000 + 40 * 0.25
    }

    // ----------------------------- Take-Profit -------------------------------

    [Fact]
    public void BuildTakeProfit_long_is_sell_limit_above_entry()
    {
        var tp = OrderFactory.BuildTakeProfit(PositionSide.Long, Nq, "ACC", "NQ", 20000m, 60, 1, "tp", T);

        tp.Side.Should().Be(OrderSide.Sell);
        tp.OrderType.Should().Be(OrderType.Limit);
        tp.LimitPrice.Should().Be(20015m); // 20000 + 60 * 0.25
    }

    [Fact]
    public void BuildTakeProfit_short_is_buy_limit_below_entry()
    {
        var tp = OrderFactory.BuildTakeProfit(PositionSide.Short, Nq, "ACC", "NQ", 20000m, 60, 1, "tp", T);

        tp.Side.Should().Be(OrderSide.Buy);
        tp.LimitPrice.Should().Be(19985m); // 20000 - 60 * 0.25
    }

    // ----------------------------- Bracket / OCO -----------------------------

    [Fact]
    public void BuildBracket_links_entry_sl_tp_with_same_group()
    {
        var b = OrderFactory.BuildBracket(Sig(SignalDirection.Long), Nq, "ACC", 2, 20000m, 40, 60, "base", T);

        b.Entry.BracketGroupId.Should().NotBeNull();
        b.StopLoss.BracketGroupId.Should().Be(b.Entry.BracketGroupId);
        b.TakeProfit.BracketGroupId.Should().Be(b.Entry.BracketGroupId);

        b.StopLoss.StopPrice.Should().Be(19990m);
        b.TakeProfit.LimitPrice.Should().Be(20015m);
        b.Entry.Quantity.Should().Be(2);
    }

    // ----------------------------- Break-Even --------------------------------

    [Fact]
    public void BuildBreakEven_moves_stop_to_entry()
    {
        var sl = OrderFactory.BuildStopLoss(PositionSide.Long, Nq, "ACC", "NQ", 20000m, 40, 1, "sl", T);
        var be = OrderFactory.BuildBreakEven(sl, PositionSide.Long, Nq, entryPrice: 20000m);

        be.StopPrice.Should().Be(20000m);
        be.Side.Should().Be(OrderSide.Sell); // bleibt Exit-Seite
    }

    [Fact]
    public void BuildBreakEven_with_offset_covers_fees_long()
    {
        var sl = OrderFactory.BuildStopLoss(PositionSide.Long, Nq, "ACC", "NQ", 20000m, 40, 1, "sl", T);
        var be = OrderFactory.BuildBreakEven(sl, PositionSide.Long, Nq, entryPrice: 20000m, offsetTicks: 2);

        be.StopPrice.Should().Be(20000.50m); // 20000 + 2 * 0.25
    }

    // ----------------------------- Trailing-Stop -----------------------------

    [Fact]
    public void Trailing_long_moves_up_on_favorable_price()
    {
        // existing stop 19990, price moves to 20020 -> candidate 20010 -> stop moves up
        var newStop = OrderFactory.ComputeTrailingStop(PositionSide.Long, 19990m, 20020m, 40, Nq);
        newStop.Should().Be(20010m);
    }

    [Fact]
    public void Trailing_long_does_not_loosen_on_adverse_price()
    {
        // existing stop 20010, price drops to 20005 -> candidate 19995 -> stop stays
        var newStop = OrderFactory.ComputeTrailingStop(PositionSide.Long, 20010m, 20005m, 40, Nq);
        newStop.Should().Be(20010m);
    }

    [Fact]
    public void Trailing_short_moves_down_on_favorable_price()
    {
        // short: price falls to 19980 -> candidate 19990 -> stop moves down from 20010
        var newStop = OrderFactory.ComputeTrailingStop(PositionSide.Short, 20010m, 19980m, 40, Nq);
        newStop.Should().Be(19990m);
    }

    [Fact]
    public void BuildTrailingStop_returns_updated_stop_order()
    {
        var sl = OrderFactory.BuildStopLoss(PositionSide.Long, Nq, "ACC", "NQ", 20000m, 40, 1, "sl", T); // stop 19990
        var trailed = OrderFactory.BuildTrailingStop(sl, PositionSide.Long, 20020m, 40, Nq);

        trailed.StopPrice.Should().Be(20010m);
        trailed.OrderType.Should().Be(OrderType.Stop);
    }
}
