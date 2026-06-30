using FluentAssertions;
using TradingBot.Application.Fees;
using TradingBot.Application.Pnl;
using TradingBot.Domain.Enums;
using Xunit;

namespace TradingBot.Tests.Pnl;

public class PnLCalculatorTests
{
    private readonly PnLCalculator _sut = new(new FeeCalculator());

    [Fact]
    public void OneContract_Long_Win_NQ()
    {
        var r = _sut.CalculateTrade(TestProfiles.Nq(), TestProfiles.NqFees(),
            PositionSide.Long, entryPrice: 20000m, exitPrice: 20010m, quantity: 1);

        r.Ticks.Should().Be(40m);          // 10 points / 0.25
        r.GrossPnL.Should().Be(200.00m);   // 40 * 5.00 * 1
        r.Fees.TotalFees.Should().Be(14.30m);
        r.NetPnL.Should().Be(185.70m);     // 200 - 14.30
    }

    [Fact]
    public void OneContract_Short_Win_NQ()
    {
        var r = _sut.CalculateTrade(TestProfiles.Nq(), TestProfiles.NqFees(),
            PositionSide.Short, entryPrice: 20010m, exitPrice: 20000m, quantity: 1);

        r.GrossPnL.Should().Be(200.00m);   // short: (entry - exit) = 10 points
        r.NetPnL.Should().Be(185.70m);
    }

    [Fact]
    public void Losing_Trade_Long_NQ_has_negative_gross_and_still_subtracts_fees()
    {
        var r = _sut.CalculateTrade(TestProfiles.Nq(), TestProfiles.NqFees(),
            PositionSide.Long, entryPrice: 20000m, exitPrice: 19990m, quantity: 1);

        r.GrossPnL.Should().Be(-200.00m);
        r.NetPnL.Should().Be(-214.30m);    // -200 - 14.30
    }

    [Fact]
    public void MultiContract_Long_NQ()
    {
        var r = _sut.CalculateTrade(TestProfiles.Nq(), TestProfiles.NqFees(),
            PositionSide.Long, entryPrice: 20000m, exitPrice: 20005m, quantity: 3);

        r.GrossPnL.Should().Be(300.00m);   // 20 ticks * 5.00 * 3
        r.Fees.TotalCommissionsAndFees.Should().Be(12.90m); // 2.15 * 3 * 2
        r.Fees.SlippageCost.Should().Be(30.00m);            // 5.00 * 3 * 2
        r.Fees.TotalFees.Should().Be(42.90m);
        r.NetPnL.Should().Be(257.10m);     // 300 - 42.90
    }

    [Fact]
    public void NQ_and_MNQ_differ_only_by_tickvalue()
    {
        var nq = _sut.CalculateGrossPnL(TestProfiles.Nq(), PositionSide.Long, 20000m, 20010m, 1);
        var mnq = _sut.CalculateGrossPnL(TestProfiles.Mnq(), PositionSide.Long, 20000m, 20010m, 1);

        nq.Should().Be(200.00m);   // 40 ticks * 5.00
        mnq.Should().Be(20.00m);   // 40 ticks * 0.50
        nq.Should().Be(mnq * 10m); // tick value ratio 5.00 / 0.50
    }

    [Fact]
    public void OneContract_Short_Loss_NQ()
    {
        // Short verliert, wenn der Preis steigt: priceMove = entry - exit = -10 Punkte.
        var r = _sut.CalculateTrade(TestProfiles.Nq(), TestProfiles.NqFees(),
            PositionSide.Short, entryPrice: 20000m, exitPrice: 20010m, quantity: 1);

        r.GrossPnL.Should().Be(-200.00m);
        r.NetPnL.Should().Be(-214.30m);    // -200 - 14.30
    }

    [Fact]
    public void OneContract_Long_Loss_NQ()
    {
        var r = _sut.CalculateTrade(TestProfiles.Nq(), TestProfiles.NqFees(),
            PositionSide.Long, entryPrice: 20010m, exitPrice: 20000m, quantity: 1);

        r.GrossPnL.Should().Be(-200.00m);
        r.NetPnL.Should().Be(-214.30m);
    }

    [Fact]
    public void NetPnL_equals_Gross_minus_Fees_minus_Slippage()
    {
        var r = _sut.CalculateTrade(TestProfiles.Nq(), TestProfiles.NqFees(),
            PositionSide.Long, 20000m, 20010m, 1);

        var expected = r.GrossPnL - r.Fees.TotalCommissionsAndFees - r.Fees.SlippageCost;
        r.NetPnL.Should().Be(expected);
        r.NetPnL.Should().Be(185.70m);
    }

    [Fact]
    public void Decimal_math_has_no_rounding_error()
    {
        // 0.39 (MNQ per-side) * 3 Kontrakte * 2 Seiten = 2.34 EXAKT (float würde driften).
        var r = _sut.CalculateTrade(TestProfiles.Mnq(), TestProfiles.MnqFees(),
            PositionSide.Long, entryPrice: 20000m, exitPrice: 20003m, quantity: 3);

        r.GrossPnL.Should().Be(18.00m);                      // 12 ticks * 0.50 * 3
        r.Fees.TotalCommissionsAndFees.Should().Be(2.34m);   // 0.39 * 3 * 2
        r.Fees.SlippageCost.Should().Be(3.00m);              // 0.50 * 3 * 2
        r.Fees.TotalFees.Should().Be(5.34m);
        r.NetPnL.Should().Be(12.66m);                        // 18.00 - 5.34
    }

    [Fact]
    public void GrossPnL_never_includes_fees()
    {
        // Gross hängt nur vom Preis/Instrument ab, NIE von Gebühren.
        var pureGross = _sut.CalculateGrossPnL(TestProfiles.Nq(), PositionSide.Long, 20000m, 20010m, 1);

        var withCheapFees = _sut.CalculateTrade(TestProfiles.Nq(), TestProfiles.NqFees(),
            PositionSide.Long, 20000m, 20010m, 1);
        var withExpensiveFees = _sut.CalculateTrade(
            TestProfiles.Nq(),
            TestProfiles.NqFees() with { CommissionPerSide = 50m },
            PositionSide.Long, 20000m, 20010m, 1);

        // Identischer GrossPnL trotz unterschiedlicher Gebühren.
        withCheapFees.GrossPnL.Should().Be(pureGross);
        withExpensiveFees.GrossPnL.Should().Be(pureGross);
        withCheapFees.GrossPnL.Should().Be(200.00m);

        // Aber unterschiedlicher NetPnL.
        withExpensiveFees.NetPnL.Should().BeLessThan(withCheapFees.NetPnL);
    }

    [Theory]
    [InlineData(0.0, 5.0)]   // ungültige TickSize
    [InlineData(0.25, 0.0)]  // ungültiger TickValue
    public void Invalid_tick_values_are_rejected(double tickSize, double tickValue)
    {
        var bad = TestProfiles.Nq() with { TickSize = (decimal)tickSize, TickValue = (decimal)tickValue };

        var act = () => _sut.CalculateGrossPnL(bad, PositionSide.Long, 20000m, 20010m, 1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Zero_quantity_is_rejected()
    {
        var act = () => _sut.CalculateGrossPnL(TestProfiles.Nq(), PositionSide.Long, 20000m, 20010m, 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Flat_side_is_rejected()
    {
        var act = () => _sut.CalculateGrossPnL(TestProfiles.Nq(), PositionSide.Flat, 20000m, 20010m, 1);
        act.Should().Throw<ArgumentException>();
    }
}
