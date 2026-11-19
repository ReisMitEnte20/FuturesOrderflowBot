using FluentAssertions;
using TradingBot.Quant.MonteCarlo;
using TradingBot.Quant.Report;
using Xunit;

namespace TradingBot.Tests.Quant;

/// <summary>Von Hand nachrechenbare Prüfungen der Berichtsbausteine (kleine, feste Tradelisten).</summary>
public class ReportCalculatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 15, 14, 0, 0, TimeSpan.Zero);

    private static ReportTrade Tr(int i, decimal net, decimal fees = 2m, decimal slip = 1.25m, string session = "2026-06-15", bool isLong = true, int hold = 2)
        => new()
        {
            Index = i, EntryTime = T0.AddMinutes(10 * i), ExitTime = T0.AddMinutes(10 * i + 5 * hold), IsLong = isLong, Quantity = 1,
            EntryBarIndex = 2 * i, ExitBarIndex = 2 * i + hold, GrossPnL = net + fees, Fees = fees, NetPnL = net, SlippageCost = slip,
            MarketSides = 2, SessionKey = session, ExitReason = "StopLoss"
        };

    [Fact]
    public void Trade_statistics_match_hand_calculation()
    {
        // +100, −50, +30 → Netto 80; Trefferquote 2/3; Ø Gewinn 65; Ø Verlust −50; Payoff 1,3; Erwartung 26,67; PF 130/50 = 2,6.
        var t = new[] { Tr(0, 100), Tr(1, -50), Tr(2, 30) };
        var s = TradeStatistics.Compute(t, evaluatedBars: 20);
        s.NetPnL.Should().Be(80m);
        s.Fees.Should().Be(6m);
        s.GrossPnL.Should().Be(86m);                       // Brutto = Netto + Gebühren (Slippage steckt bereits im Brutto)
        s.PreCostPnL.Should().Be(80m + 6m + 3.75m);         // vor allen Kosten: Gebühren und Slippage genau einmal zurückgerechnet
        s.WinRate.Value.Should().BeApproximately(2 / 3.0, 1e-12);
        s.AverageWin.Value.Should().Be(65);
        s.AverageLoss.Value.Should().Be(-50);
        s.PayoffRatio.Value.Should().BeApproximately(1.3, 1e-12);
        s.Expectancy.Value.Should().BeApproximately(80 / 3.0, 1e-9);
        s.ProfitFactor.Value.Should().BeApproximately(2.6, 1e-12);
        s.Exposure.Value.Should().BeApproximately(9 / 20.0, 1e-12);   // je Trade 3 Kerzen im Markt
    }

    [Fact]
    public void No_losses_is_undefined_not_infinite()
    {
        var s = TradeStatistics.Compute(new[] { Tr(0, 10), Tr(1, 20) }, 10);
        s.ProfitFactor.Value.Should().BeNull();
        s.ProfitFactor.Reason.Should().Contain("Keine Verlusttrades");
        s.PayoffRatio.Value.Should().BeNull();
    }

    [Fact]
    public void Drawdown_counts_a_loss_directly_after_start()
    {
        // Start 10 000; erster Trade −50 → Drawdown 50 (0,5 %) vom Startkapital, danach +100 erholt.
        var trades = new[] { Tr(0, -50), Tr(1, 100) };
        var dd = DrawdownAnalysis.Analyze(DrawdownAnalysis.ClosedTradeCurve(trades, 10_000, T0), "test");
        dd.MaxDrawdown.Should().Be(50);
        dd.MaxDrawdownFraction.Should().BeApproximately(0.005, 1e-12);
        dd.PeakTime.Should().Be(T0);
        dd.MaxDrawdownOpen.Should().BeFalse();
        dd.RecoveryTime.Should().Be(trades[1].ExitTime);
    }

    [Fact]
    public void Unrecovered_drawdown_is_marked_open()
    {
        var dd = DrawdownAnalysis.Analyze(DrawdownAnalysis.ClosedTradeCurve(new[] { Tr(0, 40), Tr(1, -100), Tr(2, 20) }, 1000, T0), "x");
        dd.MaxDrawdown.Should().Be(100);
        dd.MaxDrawdownOpen.Should().BeTrue();
        dd.UnderwaterAtEnd.Should().BeTrue();
        dd.CurrentDrawdown.Should().Be(80);
    }

    [Fact]
    public void Losing_streak_counts_consecutive_losses()
    {
        var r = DrawdownAnalysis.LosingStreak(new[] { Tr(0, -1), Tr(1, -2), Tr(2, 5), Tr(3, -1), Tr(4, -1), Tr(5, -1) });
        r.LongestCount.Should().Be(3);
        r.LongestSum.Should().Be(-3);
    }

    [Fact]
    public void Same_seed_same_simulation_and_permutation_keeps_final_net()
    {
        var obs = new double[] { 120, -40, -60, 30, 15, -80, 200, -10, 5, -25, 60, -30 };
        var a = TradeResampling.Run("p", "p", "q", "u", obs, ResamplingMethod.Permutation, null, 2000, 42, 10_000);
        var b = TradeResampling.Run("p", "p", "q", "u", obs, ResamplingMethod.Permutation, null, 2000, 42, 10_000);
        a.MaxDrawdown!.P95.Should().Be(b.MaxDrawdown!.P95);
        a.MaxDrawdown.Median.Should().Be(b.MaxDrawdown.Median);
        a.FinalNetInvariant.Should().BeTrue();
        a.FinalNet!.Min.Should().BeApproximately(obs.Sum(), 1e-9);
        a.FinalNet.Max.Should().BeApproximately(obs.Sum(), 1e-9);

        var boot1 = TradeResampling.Run("b", "b", "q", "u", obs, ResamplingMethod.MovingBlock, 1, 2000, 7, 10_000);
        var boot2 = TradeResampling.Run("b", "b", "q", "u", obs, ResamplingMethod.MovingBlock, 1, 2000, 7, 10_000);
        var boot3 = TradeResampling.Run("b", "b", "q", "u", obs, ResamplingMethod.MovingBlock, 1, 2000, 8, 10_000);
        boot1.FinalNet!.P5.Should().Be(boot2.FinalNet!.P5);
        boot1.FinalNet.P5.Should().NotBe(boot3.FinalNet!.P5);
        boot1.FinalNetInvariant.Should().BeNull();
    }

    [Fact]
    public void Cost_breakeven_is_the_precost_expectancy()
    {
        // Vor Kosten je Trade: (100+2+1,25) = 103,25 und (−50+2+1,25) = −46,75 → Ø 28,25 = Gewinnschwelle je Round-Turn.
        var t = new[] { Tr(0, 100), Tr(1, -50) };
        var r = CostAnalysis.Compute(t, tickValue: 1.25m, feePerSide: 1m);
        r.PreCostExpectancy.Should().BeApproximately(28.25, 1e-12);
        r.BreakevenCostPerTrade.Value.Should().BeApproximately(28.25, 1e-12);
        r.BreakevenTicksPerSide.Value.Should().BeApproximately(28.25 / 2.5, 1e-12);
        r.CostPerTrade.Should().BeApproximately(3.25, 1e-12);
        r.Scenarios.First(s => s.Id == "slip+1").Net.Should().Be(50m - 2 * 2 * 1.25m);   // je Trade 2 Market-Seiten à 1 Tick
    }

    [Fact]
    public void Concentration_and_leave_one_day_out()
    {
        var t = new[]
        {
            Tr(0, 500, session: "2026-06-15"), Tr(1, -20, session: "2026-06-16"), Tr(2, 10, session: "2026-06-17"),
            Tr(3, -30, session: "2026-06-18"), Tr(4, 40, session: "2026-06-19")
        };
        var c = ConcentrationAnalysis.Compute(t);
        c.TotalNet.Should().Be(500m);
        c.BestDay.Should().Be("2026-06-15");
        c.BestDayShare.Value.Should().Be(1.0);
        c.NetWithoutBestDay.Should().Be(0m);
        c.LeaveOneOutAvailable.Should().BeTrue();
        c.LeaveOneOut.Should().HaveCount(5);
        c.SignFlippingDays.Should().Be(1);   // ohne besten Tag bleibt nichts übrig (0) — zählt als „Ergebnis hängt an diesem Tag“
    }

    [Fact]
    public void Leave_one_out_needs_enough_sessions()
    {
        var c = ConcentrationAnalysis.Compute(new[] { Tr(0, 5), Tr(1, 6, session: "2026-06-16") });
        c.LeaveOneOutAvailable.Should().BeFalse();
        c.LeaveOneOutReason.Should().Be("5 Handelstage benötigt, 2 vorhanden.");
    }

    [Fact]
    public void Sensitivity_classification_distinguishes_peak_and_plateau()
    {
        var plateau = new List<(double, decimal, int, bool)> { (8, 10, 40, true), (9, 20, 40, true), (10, 30, 40, true), (11, 25, 40, true), (12, 5, 40, true) };
        SensitivityRules.Classify(plateau, 2, 30).Should().Be("stabil positiv");
        var peak = new List<(double, decimal, int, bool)> { (8, -10, 40, true), (9, -5, 40, true), (10, 30, 40, true), (11, -2, 40, true), (12, -8, 40, true) };
        SensitivityRules.Classify(peak, 2, 30).Should().Be("einzelne Spitze");
        var loss = new List<(double, decimal, int, bool)> { (8, -10, 40, true), (9, -5, 40, true), (10, -3, 40, true) };
        SensitivityRules.Classify(loss, 1, 30).Should().Be("stabiler Verlust");
        var thin = new List<(double, decimal, int, bool)> { (8, 10, 3, true), (9, 50, 3, true), (10, 10, 3, true) };
        SensitivityRules.Classify(thin, 1, 30).Should().Be("zu wenige Trades");
    }

    [Fact]
    public void Trade_date_calendar_rolls_at_17_chicago()
    {
        // 16. Juni 2026 21:59 UTC = 16:59 CDT → Trade-Date 16.06.; 22:00 UTC = 17:00 CDT → Trade-Date 17.06.
        TradeDateCalendar.Key(new DateTimeOffset(2026, 6, 16, 21, 59, 0, TimeSpan.Zero)).Should().Be("2026-06-16");
        TradeDateCalendar.Key(new DateTimeOffset(2026, 6, 16, 22, 0, 0, TimeSpan.Zero)).Should().Be("2026-06-17");
        // Winterzeit: 23:00 UTC = 17:00 CST
        TradeDateCalendar.Key(new DateTimeOffset(2026, 1, 12, 22, 59, 0, TimeSpan.Zero)).Should().Be("2026-01-12");
        TradeDateCalendar.Key(new DateTimeOffset(2026, 1, 12, 23, 0, 0, TimeSpan.Zero)).Should().Be("2026-01-13");
        TradeDateCalendar.DstTransitions(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 3, 20, 0, 0, 0, TimeSpan.Zero))
            .Should().ContainSingle().Which.Date.Should().Be(new DateOnly(2026, 3, 8));
    }
}
