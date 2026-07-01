using FluentAssertions;
using TradingBot.Application.Fees;
using TradingBot.Application.Pnl;
using TradingBot.Application.Positions;
using TradingBot.Backtesting.Positions;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using Xunit;

namespace TradingBot.Tests.Backtesting;

public class RecordingPositionManagerTests
{
    private static readonly InstrumentProfile Nq = BacktestTestData.Instrument();
    private static readonly FeeProfile Fee = BacktestTestData.Fee(); // per-side/contract 2.15
    private static readonly DateTimeOffset T = BacktestTestData.T0;

    private static RecordingPositionManager NewSut()
    {
        var fc = new FeeCalculator();
        return new RecordingPositionManager(new PositionManager(fc, new PnLCalculator(fc)));
    }

    private static void Apply(RecordingPositionManager pm, OrderSide side, int qty, decimal price, int sec)
        => pm.ApplyFill(new FillEvent
        {
            OrderId = Guid.NewGuid(), Symbol = "NQ", Side = side, Quantity = qty, FillPrice = price,
            Timestamp = T.AddSeconds(sec)
        }, Nq, Fee);

    [Fact]
    public void Open_then_close_records_one_trade()
    {
        var pm = NewSut();
        Apply(pm, OrderSide.Buy, 1, 20000m, 0);
        Apply(pm, OrderSide.Sell, 1, 20010m, 1);

        pm.Trades.Should().HaveCount(1);
        var t = pm.Trades[0];
        t.Side.Should().Be(PositionSide.Long);
        t.GrossPnL.Should().Be(200m);       // 40 ticks * 5
        t.Fees.Should().Be(4.30m);          // 2 sides * 2.15
        t.NetPnL.Should().Be(195.70m);
        t.EntryPrice.Should().Be(20000m);
        t.ExitPrice.Should().Be(20010m);
    }

    [Fact]
    public void Scale_in_and_partial_exit_is_one_trade_until_flat()
    {
        var pm = NewSut();
        Apply(pm, OrderSide.Buy, 2, 20000m, 0);   // long 2
        Apply(pm, OrderSide.Sell, 1, 20010m, 1);  // partial close -> still long 1
        pm.Trades.Should().BeEmpty();             // noch nicht flat
        Apply(pm, OrderSide.Sell, 1, 20020m, 2);  // flat

        pm.Trades.Should().HaveCount(1);
        var t = pm.Trades[0];
        t.GrossPnL.Should().Be(600m);       // 200 + 400
        t.Fees.Should().Be(8.60m);          // entry(2)=4.30 + 2 exits(1 each)=4.30
        t.NetPnL.Should().Be(591.40m);
    }

    [Fact]
    public void Flip_records_two_trades_without_double_counting_fees()
    {
        var pm = NewSut();
        Apply(pm, OrderSide.Buy, 2, 20000m, 0);   // long 2
        Apply(pm, OrderSide.Sell, 3, 20010m, 1);  // close long 2, open short 1
        Apply(pm, OrderSide.Buy, 1, 20005m, 2);   // close short 1

        pm.Trades.Should().HaveCount(2);

        var longTrade = pm.Trades[0];
        longTrade.Side.Should().Be(PositionSide.Long);
        longTrade.GrossPnL.Should().Be(400m);
        longTrade.Fees.Should().Be(10.75m);       // entry(2)=4.30 + flip(3)=6.45
        longTrade.NetPnL.Should().Be(389.25m);

        var shortTrade = pm.Trades[1];
        shortTrade.Side.Should().Be(PositionSide.Short);
        shortTrade.GrossPnL.Should().Be(100m);    // (20010-20005)/0.25*5
        shortTrade.Fees.Should().Be(2.15m);       // nur der Close-Fill
        shortTrade.NetPnL.Should().Be(97.85m);

        // Summe der Trade-Fees == gesamte gezahlte Fees (keine Doppelzählung)
        (longTrade.Fees + shortTrade.Fees).Should().Be(12.90m);
    }

    [Fact]
    public void TradeClosed_callback_fires_per_closed_trade()
    {
        var pm = NewSut();
        int closed = 0;
        pm.TradeClosed = _ => closed++;

        Apply(pm, OrderSide.Buy, 1, 20000m, 0);
        Apply(pm, OrderSide.Sell, 1, 20010m, 1);

        closed.Should().Be(1);
    }

    [Fact]
    public void Has_no_execution_dependency()
    {
        var ctorParams = typeof(RecordingPositionManager).GetConstructors()[0].GetParameters();
        ctorParams.Should().NotContain(p => p.ParameterType == typeof(TradingBot.Core.Interfaces.IBrokerExecutionAdapter));
    }
}
