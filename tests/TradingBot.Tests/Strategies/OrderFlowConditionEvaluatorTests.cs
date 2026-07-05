using FluentAssertions;
using TradingBot.Application.Strategies.OrderFlow;
using TradingBot.Domain.Enums;
using TradingBot.Tests.Backtesting;
using Xunit;
using static TradingBot.Tests.Strategies.OrderFlowTemplateTestData;

namespace TradingBot.Tests.Strategies;

public class OrderFlowConditionEvaluatorTests
{
    private static OrderFlowConditionEvaluator NewEvaluator(
        OrderFlowTemplateParameters? p = null, bool withInstrument = true)
        => new(p ?? new OrderFlowTemplateParameters(),
               withInstrument ? BacktestTestData.Instrument() : null);

    // ----------------------------- Delta Divergence --------------------------

    [Fact]
    public void DeltaDivergence_met_on_new_low_with_rising_delta()
    {
        var sut = NewEvaluator();
        foreach (var b in LongSetupBars()) sut.Add(b);

        var result = sut.DeltaDivergence(SignalDirection.Long);

        result.Status.Should().Be(ConditionStatus.Met);
        result.Detail.Should().Contain("neues Tief");
    }

    [Fact]
    public void DeltaDivergence_not_met_without_new_extreme()
    {
        var sut = NewEvaluator();
        sut.Add(Bar(0, 20000m, 20010m, 19990m, 19995m, 60m, 40m, -20m));
        sut.Add(Bar(1, 19995m, 20005m, 19992m, 20000m, 40m, 60m, 0m)); // kein neues Tief

        sut.DeltaDivergence(SignalDirection.Long).Status.Should().Be(ConditionStatus.NotMet);
    }

    [Fact]
    public void DeltaDivergence_insufficient_with_single_bar()
    {
        var sut = NewEvaluator();
        sut.Add(LongSetupBars()[0]);

        sut.DeltaDivergence(SignalDirection.Long).Status.Should().Be(ConditionStatus.InsufficientData);
    }

    // ----------------------------- Absorption --------------------------------

    [Fact]
    public void Absorption_met_on_high_volume_small_range_held_close()
    {
        var sut = NewEvaluator();
        sut.Add(Bar(0, 20000m, 20005m, 19995m, 20000m, 50m, 50m, 0m));               // Ø-Volumen 100
        // Bar: Range 1 Punkt (= 4 Ticks ≤ 8 Ticks), Volumen 250 ≥ 2×100, Delta -50 (Verkäufer), Close obere Hälfte.
        sut.Add(Bar(1, 20000m, 20001m, 20000m, 20000.75m, 150m, 100m, -50m));

        var result = sut.Absorption(SignalDirection.Long);

        result.Status.Should().Be(ConditionStatus.Met);
    }

    [Fact]
    public void Absorption_insufficient_without_instrument()
    {
        var sut = NewEvaluator(withInstrument: false);
        foreach (var b in LongSetupBars()) sut.Add(b);

        var result = sut.Absorption(SignalDirection.Long);

        result.Status.Should().Be(ConditionStatus.InsufficientData);
        result.Detail.Should().Contain("InstrumentProfile");
    }

    // ----------------------------- Liquidity Sweep ---------------------------

    [Fact]
    public void LiquiditySweep_met_when_session_low_swept_and_reclaimed()
    {
        var sut = NewEvaluator();
        foreach (var b in LongSetupBars()) sut.Add(b);

        var result = sut.LiquiditySweep(SignalDirection.Long);

        result.Status.Should().Be(ConditionStatus.Met);
        result.Detail.Should().Contain("Session-Tief");
    }

    [Fact]
    public void LiquiditySweep_not_met_when_close_stays_below()
    {
        var sut = NewEvaluator();
        sut.Add(Bar(0, 20000m, 20010m, 19990m, 19995m, 60m, 40m, -20m));
        sut.Add(Bar(1, 19995m, 19996m, 19980m, 19982m, 80m, 20m, -80m)); // bricht Tief, bleibt unten

        sut.LiquiditySweep(SignalDirection.Long).Status.Should().Be(ConditionStatus.NotMet);
    }

    // ----------------------------- Ehrliche Platzhalter ----------------------

    [Fact]
    public void StackedImbalances_reports_insufficient_footprint_data()
    {
        var sut = NewEvaluator();
        foreach (var b in LongSetupBars()) sut.Add(b);

        var result = sut.StackedImbalances();

        result.Status.Should().Be(ConditionStatus.InsufficientData);
        result.Detail.Should().Contain("Footprint");
    }

    [Fact]
    public void HvnLvnFilter_reports_insufficient_volume_profile_data()
    {
        var sut = NewEvaluator();
        var result = sut.HvnLvnFilter();

        result.Status.Should().Be(ConditionStatus.InsufficientData);
        result.Detail.Should().Contain("Volume-Profile");
    }

    // ----------------------------- Filter -------------------------------------

    [Fact]
    public void VwapDistance_insufficient_without_instrument()
    {
        var sut = NewEvaluator(withInstrument: false);
        foreach (var b in LongSetupBars()) sut.Add(b);

        sut.VwapDistance().Status.Should().Be(ConditionStatus.InsufficientData);
    }

    [Fact]
    public void VwapDistance_not_met_when_too_far_from_vwap()
    {
        var p = new OrderFlowTemplateParameters { MaxDistanceFromVwapTicks = 1 }; // 0.25 Punkte
        var sut = NewEvaluator(p);
        sut.Add(Bar(0, 20000m, 20010m, 19990m, 19995m, 60m, 40m, -20m));
        sut.Add(Bar(1, 19995m, 20050m, 19990m, 20050m, 40m, 60m, 0m)); // weit weg vom VWAP

        sut.VwapDistance().Status.Should().Be(ConditionStatus.NotMet);
    }

    [Fact]
    public void BarImbalance_insufficient_without_classification()
    {
        var sut = NewEvaluator();
        sut.Add(UnclassifiedBar());

        var result = sut.BarImbalance(SignalDirection.Long);

        result.Status.Should().Be(ConditionStatus.InsufficientData);
        result.Detail.Should().Contain("Klassifikation");
    }

    [Fact]
    public void CvdConfirmation_follows_cumulative_delta_direction()
    {
        var sut = NewEvaluator();
        foreach (var b in LongSetupBars()) sut.Add(b); // CVD -20 -> +60

        sut.CvdConfirmation(SignalDirection.Long).Status.Should().Be(ConditionStatus.Met);
        sut.CvdConfirmation(SignalDirection.Short).Status.Should().Be(ConditionStatus.NotMet);
    }
}
