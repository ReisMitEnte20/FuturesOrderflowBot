using FluentAssertions;
using TradingBot.Application.Fees;
using Xunit;

namespace TradingBot.Tests.Pnl;

public class FeeCalculatorTests
{
    private readonly FeeCalculator _sut = new();

    [Fact]
    public void CalculatePerSide_scales_each_component_by_quantity_and_has_no_slippage()
    {
        var fees = _sut.CalculatePerSide(TestProfiles.NqFees(), quantity: 2);

        fees.Commission.Should().Be(1.70m);   // 0.85 * 2
        fees.ExchangeFee.Should().Be(2.36m);   // 1.18 * 2
        fees.ClearingFee.Should().Be(0.20m);   // 0.10 * 2
        fees.NfaFee.Should().Be(0.04m);        // 0.02 * 2
        fees.RoutingFee.Should().Be(0.00m);
        fees.OtherFee.Should().Be(0.00m);
        fees.SlippageCost.Should().Be(0.00m);
        fees.TotalCommissionsAndFees.Should().Be(4.30m); // 2.15 * 2
        fees.TotalFees.Should().Be(4.30m);
    }

    [Fact]
    public void CalculateRoundTurn_doubles_per_side_fees_and_has_no_slippage()
    {
        var fees = _sut.CalculateRoundTurn(TestProfiles.NqFees(), quantity: 1);

        fees.Commission.Should().Be(1.70m);              // 0.85 * 2 sides
        fees.TotalCommissionsAndFees.Should().Be(4.30m); // 2.15 * 2 sides
        fees.SlippageCost.Should().Be(0.00m);
    }

    [Fact]
    public void CalculateSlippageCost_is_per_side_ticks_times_tickvalue_times_quantity()
    {
        var nq = _sut.CalculateSlippageCost(TestProfiles.NqFees(), TestProfiles.Nq(), quantity: 2);
        var mnq = _sut.CalculateSlippageCost(TestProfiles.MnqFees(), TestProfiles.Mnq(), quantity: 2);

        nq.Should().Be(10.00m);  // 1.0 tick * 5.00 * 2
        mnq.Should().Be(1.00m);  // 1.0 tick * 0.50 * 2
    }

    [Fact]
    public void CalculateRoundTurnWithSlippage_adds_double_per_side_slippage()
    {
        var fees = _sut.CalculateRoundTurnWithSlippage(TestProfiles.NqFees(), TestProfiles.Nq(), quantity: 1);

        fees.TotalCommissionsAndFees.Should().Be(4.30m); // round turn commissions
        fees.SlippageCost.Should().Be(10.00m);           // 5.00 per side * 2 sides
        fees.TotalFees.Should().Be(14.30m);              // 4.30 + 10.00
    }

    [Fact]
    public void TotalFees_contains_all_seven_components_including_slippage()
    {
        var f = _sut.CalculateRoundTurnWithSlippage(TestProfiles.NqFees(), TestProfiles.Nq(), quantity: 2);

        var manualSum = f.Commission + f.ExchangeFee + f.ClearingFee
                      + f.RoutingFee + f.NfaFee + f.OtherFee + f.SlippageCost;

        f.TotalFees.Should().Be(manualSum);
        f.SlippageCost.Should().BeGreaterThan(0m);            // Slippage ist enthalten
        f.TotalFees.Should().Be(f.TotalCommissionsAndFees + f.SlippageCost);
    }

    [Fact]
    public void Zero_or_negative_quantity_is_rejected()
    {
        var act = () => _sut.CalculatePerSide(TestProfiles.NqFees(), quantity: 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
