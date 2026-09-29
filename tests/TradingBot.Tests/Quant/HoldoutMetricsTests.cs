using FluentAssertions;
using TradingBot.Quant.Series;
using Xunit;

namespace TradingBot.Tests.Quant;

/// <summary>
/// Befund 1 (Holdout): Das Startkapital muss bei Renditen UND Drawdown erhalten bleiben. Die erste
/// (ggf. aggregierte) Rendite wird gegen das Startkapital gemessen; der absolute Drawdown beginnt am
/// Startkapital. Getestet auf dem tatsächlich verwendeten Rechenpfad (HoldoutMetrics.RealizedFromHoldout),
/// den der Holdout-Service aufruft.
/// </summary>
public class HoldoutMetricsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static QuantEquityPoint P(DateTimeOffset t, int i, decimal realized) =>
        new() { Time = t, BarIndex = i, RealizedEquity = realized, TotalEquity = realized, OpenQuantity = 0 };

    [Fact]
    public void First_aggregated_return_is_measured_against_the_start_capital_and_drawdown_starts_there()
    {
        // Start 100 → Tagesschlüsse 90 → 95 (Daily-Frequenz). Anker liegt VOR dem ersten Holdout-Punkt.
        var curve = new QuantEquityCurve
        {
            Name = "holdout",
            InitialCapital = 100m,
            Frequency = ReturnFrequency.Daily,
            StartTime = T0,                       // vor dem ersten Tagesschluss → Anker am Startkapital
            Points = new[]
            {
                P(T0.AddDays(1), 0, 90m),
                P(T0.AddDays(2), 1, 95m),
            }
        };

        var (series, maxDd, netProfit, finalEquity, truncated) = HoldoutMetrics.RealizedFromHoldout(curve, 100.0);

        series.Returns.Should().HaveCount(2);
        series.Returns[0].Should().BeApproximately(-0.10, 1e-9);     // erste Rendite −10 % (100 → 90)

        double total = series.Returns.Aggregate(1.0, (acc, r) => acc * (1 + r)) - 1.0;
        total.Should().BeApproximately(-0.05, 1e-9);                 // Gesamtrendite −5 % (100 → 95)

        maxDd.Should().BeGreaterThanOrEqualTo(10);                    // absoluter Drawdown mindestens 10 (100 → 90)
        netProfit.Should().BeApproximately(-5.0, 1e-9);
        finalEquity.Should().BeApproximately(95.0, 1e-9);
        truncated.Should().BeFalse();
    }

    [Fact]
    public void Absolute_results_use_the_full_run_even_when_the_return_series_is_truncated_at_capital_below_zero()
    {
        // Start 100 → −10 (Kapital ≤ 0, Renditereihe bricht ab) → 50 (Erholung im weiteren Lauf).
        // Chart/Journal enthalten den VOLLSTÄNDIGEN Lauf; die Zusammenfassung muss dieselbe Grenze nutzen:
        // End-Equity 50, Netto −50, absoluter Drawdown 110 (100 → −10). Der Renditeabbruch wird ausgewiesen.
        var curve = new QuantEquityCurve
        {
            Name = "holdout",
            InitialCapital = 100m,
            Frequency = ReturnFrequency.Daily,
            StartTime = T0,                       // Anker am Startkapital, vor dem ersten Holdout-Punkt
            Points = new[]
            {
                P(T0.AddDays(1), 0, -10m),
                P(T0.AddDays(2), 1, 50m),
            }
        };

        var (series, maxDd, netProfit, finalEquity, returnsTruncated) = HoldoutMetrics.RealizedFromHoldout(curve, 100.0);

        finalEquity.Should().BeApproximately(50.0, 1e-9);     // aus dem vollständigen Lauf, nicht aus der verkürzten Reihe
        netProfit.Should().BeApproximately(-50.0, 1e-9);      // 50 − 100
        maxDd.Should().BeApproximately(110.0, 1e-9);          // Peak 100 → Tal −10
        returnsTruncated.Should().BeTrue();                   // Prozentkennzahlen ausdrücklich als abgebrochen markiert
        // Die Renditereihe endet beim ersten nicht positiven Kapitalstand (nur die erste Periode 100 → −10).
        series.Returns.Should().HaveCount(1);
    }

    [Fact]
    public void Max_drawdown_uses_the_start_capital_as_the_first_peak()
    {
        // Ohne Startkapital-Anker bliebe der Drawdown der steigenden Reihe 90→95 bei 0 (genau der Befund).
        // Mit dem Startkapital 100 als erstem Peak ist er korrekt 10.
        HoldoutMetrics.MaxDrawdownAbs(new[] { 90.0, 95.0 }, startPeak: 100.0).Should().BeApproximately(10.0, 1e-9);
        HoldoutMetrics.MaxDrawdownAbs(new[] { 90.0, 95.0 }, startPeak: double.NegativeInfinity).Should().Be(0.0);
    }
}
