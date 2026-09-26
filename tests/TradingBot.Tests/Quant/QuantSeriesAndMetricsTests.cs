using FluentAssertions;
using TradingBot.Backtesting;
using TradingBot.Backtesting.Ohlc;
using TradingBot.Domain.Enums;
using TradingBot.Quant.Metrics;
using TradingBot.Quant.Series;

namespace TradingBot.Tests.Quant;

/// <summary>
/// Referenztests der Renditereihen-Bildung und der Kennzahlen. Alle Sollwerte sind von Hand
/// gerechnet (im Kommentar dokumentiert) und nicht aus der Implementierung abgeleitet.
/// </summary>
public class QuantSeriesAndMetricsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly QuantContractSpec Mes = new(TickSize: 0.25m, PointValue: 5.00m);

    private static OhlcBacktestResult BuildResult()
    {
        // Ein Trade: Entry an Bar 1, Exit an Bar 3, 2 Kontrakte, NetPnL 50.
        // Offen ist die Position an den Bars 1 und 2 (Exit-Bar zählt bereits als realisiert).
        var trade = new OhlcBacktestTrade
        {
            Symbol = "MES", Side = PositionSide.Long, Quantity = 2,
            EntryTime = T0.AddMinutes(5), ExitTime = T0.AddMinutes(15),
            EntryPrice = 5000m, ExitPrice = 5005m,
            EntryBarIndex = 1, ExitBarIndex = 3,
            GrossPnL = 51.56m, Fees = 1.56m, NetPnL = 50m,
            ExitReason = OhlcExitReason.TakeProfit, StopLossPrice = 4990m, TakeProfitPrice = 5005m
        };

        var equity = new List<OhlcEquityPoint>
        {
            new() { BarIndex = 0, Time = T0.AddMinutes(5),  RealizedNetPnL = 0m,  Equity = 10_000m, OpenPnL = 0m },
            new() { BarIndex = 1, Time = T0.AddMinutes(10), RealizedNetPnL = 0m,  Equity = 10_000m, OpenPnL = 20m },
            new() { BarIndex = 2, Time = T0.AddMinutes(15), RealizedNetPnL = 0m,  Equity = 10_000m, OpenPnL = -30m },
            new() { BarIndex = 3, Time = T0.AddMinutes(20), RealizedNetPnL = 50m, Equity = 10_050m, OpenPnL = 0m }
        };

        return new OhlcBacktestResult
        {
            Status = BacktestRunStatus.Completed,
            Trades = new[] { trade },
            Equity = equity,
            Data = new OhlcDataInfo { Source = "test", Symbol = "MES", TimeframeMinutes = 5, BarCount = 4, EvaluatedBars = 4 },
            StrategyName = "Referenz",
            Config = new OhlcBacktestConfig { ApplyFees = true, InitialBalance = 10_000m },
            EffectiveSlippageTicks = 1m,
            FeePerSide = 0.39m,
            InitialBalance = 10_000m,
            FinalEquity = 10_050m
        };
    }

    [Fact]
    public void Total_equity_marks_open_positions_to_market_and_deducts_the_exit_cost()
    {
        // Glattstellungskosten je Kontrakt = Slippage 1 Tick × 0,25 × PointValue 5 = 1,25
        //                                  + Round-Turn-Gebühr 2 × 0,39          = 0,78
        //                                  = 2,03 ; bei 2 Kontrakten = 4,06
        var curve = ReturnSeriesBuilder.BuildEquityCurve(BuildResult(), Mes, ReturnFrequency.Bar);

        curve.Points.Should().HaveCount(4);
        curve.Points[0].TotalEquity.Should().Be(10_000m);                 // flat
        curve.Points[1].TotalEquity.Should().Be(10_000m + 20m - 4.06m);   // 10.015,94
        curve.Points[2].TotalEquity.Should().Be(10_000m - 30m - 4.06m);   //  9.965,94
        curve.Points[3].TotalEquity.Should().Be(10_050m);                 // realisiert, flat

        // Realisierte Kurve bleibt davon unberührt.
        curve.Points.Select(p => p.RealizedEquity)
            .Should().Equal(10_000m, 10_000m, 10_000m, 10_050m);

        curve.Points.Select(p => p.OpenQuantity).Should().Equal(0, 2, 2, 0);
    }

    [Fact]
    public void Realized_and_total_basis_produce_different_series_and_are_never_mixed()
    {
        var curve = ReturnSeriesBuilder.BuildEquityCurve(BuildResult(), Mes, ReturnFrequency.Bar);

        var realized = ReturnSeriesBuilder.ToReturnSeries(curve, EquityBasis.Realized).Series;
        var total = ReturnSeriesBuilder.ToReturnSeries(curve, EquityBasis.Total).Series;

        realized.Basis.Should().Be(EquityBasis.Realized);
        total.Basis.Should().Be(EquityBasis.Total);

        // Realisiert: erst am letzten Bar passiert etwas.
        realized.Returns.Should().HaveCount(3);
        realized.Returns[0].Should().Be(0.0);
        realized.Returns[1].Should().Be(0.0);
        realized.Returns[2].Should().BeApproximately(50.0 / 10_000.0, 1e-12);

        // Gesamt: bewegt sich bereits mit der offenen Position.
        total.Returns[0].Should().NotBe(0.0);
    }

    [Fact]
    public void Resampling_only_keeps_observed_periods_and_never_invents_any()
    {
        var result = BuildResult();
        // Bars an zwei Kalendertagen, dazwischen ein Tag ohne Daten.
        var equity = new List<OhlcEquityPoint>
        {
            new() { BarIndex = 0, Time = T0,                 RealizedNetPnL = 0m,  Equity = 10_000m },
            new() { BarIndex = 1, Time = T0.AddHours(1),     RealizedNetPnL = 10m, Equity = 10_010m },
            new() { BarIndex = 2, Time = T0.AddDays(2),      RealizedNetPnL = 20m, Equity = 10_020m },
            new() { BarIndex = 3, Time = T0.AddDays(2).AddHours(3), RealizedNetPnL = 30m, Equity = 10_030m }
        };
        var withGap = result with { Trades = Array.Empty<OhlcBacktestTrade>(), Equity = equity };

        var daily = ReturnSeriesBuilder.BuildEquityCurve(withGap, Mes, ReturnFrequency.Daily);

        // Zwei beobachtete Tage → zwei Punkte. Der fehlende Tag wird NICHT ergänzt.
        daily.Points.Should().HaveCount(2);
        daily.Points[0].RealizedEquity.Should().Be(10_010m);   // letzter Bar des ersten Tages
        daily.Points[1].RealizedEquity.Should().Be(10_030m);   // letzter Bar des dritten Tages
    }

    [Fact]
    public void Return_series_stops_instead_of_inventing_a_return_on_non_positive_capital()
    {
        var curve = new QuantEquityCurve
        {
            Name = "Ruin",
            InitialCapital = 100m,
            Frequency = ReturnFrequency.Bar,
            Points = new List<QuantEquityPoint>
            {
                new() { Time = T0,               RealizedEquity = 100m, TotalEquity = 100m },
                new() { Time = T0.AddMinutes(5), RealizedEquity = 0m,   TotalEquity = 0m },
                new() { Time = T0.AddMinutes(10),RealizedEquity = 10m,  TotalEquity = 10m }
            }
        };

        var built = ReturnSeriesBuilder.ToReturnSeries(curve, EquityBasis.Realized);

        built.Truncated.Should().BeTrue();
        built.Series.Returns.Should().HaveCount(1);          // nur 100 → 0
        built.Notes.Should().ContainSingle(n => n.Contains("Kapital ≤ 0"));
    }

    // -------------------------------------------------------------------------------------------
    // Kennzahlen-Referenz. Kapitalkurve (Tagesfrequenz), Startkapital 100:
    //   100 → 110 → 99 → 123,75 → 123,75 → 136,125
    //   Renditen: +0,10 ; −0,10 ; +0,25 ; 0,00 ; +0,10
    //   Mittelwert = 0,35/5 = 0,07
    //   Σ(r−r̄)²  = 0,0009+0,0289+0,0324+0,0049+0,0009 = 0,068 → Varianz 0,068/4 = 0,017
    //   Zeitstempel: 5 aufeinanderfolgende Tage → Perioden/Jahr (empirisch) = 4 / (4/365,25) = 365,25
    //   Max. Drawdown = (110 − 99)/110 = 0,10 ; absolut 11
    //   Unterwasserphase: Hoch am Tag 1, Erholung am Tag 3 → 2 Tage
    // -------------------------------------------------------------------------------------------
    private static ReturnSeries ReferenceSeries()
    {
        var times = Enumerable.Range(1, 5).Select(i => T0.AddDays(i)).ToList();
        return new ReturnSeries
        {
            Name = "Referenz",
            Timestamps = times,
            Returns = new[] { 0.10, -0.10, 0.25, 0.00, 0.10 },
            EquityLevels = new[] { 110.0, 99.0, 123.75, 123.75, 136.125 },
            InitialCapital = 100.0,
            Basis = EquityBasis.Total,
            Frequency = ReturnFrequency.Daily
        };
    }

    [Fact]
    public void Observed_annualization_factor_is_derived_from_the_data()
        => ReferenceSeries().PeriodsPerYear(AnnualizationBasis.Observed)!.Value
            .Should().BeApproximately(365.25, 1e-9);

    [Fact]
    public void Core_metrics_match_hand_computed_reference_values()
    {
        var m = PerformanceMetricsCalculator.Compute(ReferenceSeries(),
            new PerformanceMetricsOptions { MinimumPeriods = 0 });

        double ppy = 365.25;
        double sd = Math.Sqrt(0.017);

        m.Value("mean_return")!.Value.Should().BeApproximately(0.07, 1e-12);
        m.Value("volatility_annual")!.Value.Should().BeApproximately(sd * Math.Sqrt(ppy), 1e-9);
        m.Value("sharpe")!.Value.Should().BeApproximately(0.07 / sd * Math.Sqrt(ppy), 1e-9);

        // Sortino: nur eine Periode unter 0 → Downside-Deviation = √(0,01/5) = √0,002
        m.Value("sortino")!.Value.Should().BeApproximately(0.07 / Math.Sqrt(0.002) * Math.Sqrt(ppy), 1e-9);

        // CAGR über 5 Tage (Startpunkt liegt eine Periode vor dem ersten Renditezeitstempel).
        // Die Hochrechnung eines 5-Tage-Zeitraums auf ein Jahr ergibt eine riesige Zahl — hier wird
        // deshalb RELATIV verglichen; die absolute Größe ist rechnerisch korrekt, aber fachlich
        // nicht interpretierbar (die Kennzahl weist das über ihre Limitation-Angabe aus).
        double years = 5.0 / 365.25;
        double expectedCagr = Math.Pow(136.125 / 100.0, 1.0 / years) - 1.0;
        m.Value("cagr")!.Value.Should().BeApproximately(expectedCagr, Math.Abs(expectedCagr) * 1e-12);
        m.Get("cagr")!.Limitation.Should().Contain("unter einem Jahr");

        m.Value("max_drawdown")!.Value.Should().BeApproximately(0.10, 1e-12);
        m.Value("max_drawdown_abs")!.Value.Should().BeApproximately(11.0, 1e-12);
        m.Value("drawdown_duration_days")!.Value.Should().BeApproximately(2.0, 1e-9);

        m.Drawdown.PeakTime.Should().Be(T0.AddDays(1));
        m.Drawdown.TroughTime.Should().Be(T0.AddDays(2));
        m.Drawdown.RecoveryTime.Should().Be(T0.AddDays(3));
        m.Drawdown.UnderwaterAtEnd.Should().BeFalse();

        // Calmar = CAGR / MaxDD
        m.Value("calmar")!.Value.Should().BeApproximately(m.Value("cagr")!.Value / 0.10, 1e-6);
    }

    [Fact]
    public void Expected_shortfall_reports_missing_basis_for_too_few_tail_observations()
    {
        // α·n = 0,05·5 = 0,25 < 1 → keine einzige Beobachtung im Verlustende.
        var m = PerformanceMetricsCalculator.Compute(ReferenceSeries(),
            new PerformanceMetricsOptions { ExpectedShortfallAlpha = 0.05, MinimumPeriods = 0 });
        var es = m.Get("expected_shortfall")!;
        es.IsAvailable.Should().BeFalse();
        es.UnavailableReason.Should().Contain("Zu wenige Perioden");

        // α = 0,2 → genau eine Beobachtung: die schlechteste Rendite −0,10.
        var m2 = PerformanceMetricsCalculator.Compute(ReferenceSeries(),
            new PerformanceMetricsOptions { ExpectedShortfallAlpha = 0.20, MinimumPeriods = 0 });
        m2.Value("expected_shortfall")!.Value.Should().BeApproximately(-0.10, 1e-12);
    }

    [Fact]
    public void Constant_returns_make_sharpe_and_sortino_undefined_rather_than_zero()
    {
        var flat = ReferenceSeries() with
        {
            Returns = new[] { 0.0, 0.0, 0.0, 0.0, 0.0 },
            EquityLevels = new[] { 100.0, 100.0, 100.0, 100.0, 100.0 }
        };

        var m = PerformanceMetricsCalculator.Compute(flat, new PerformanceMetricsOptions { MinimumPeriods = 0 });

        m.Get("sharpe")!.IsAvailable.Should().BeFalse();
        m.Get("sharpe")!.UnavailableReason.Should().Contain("Standardabweichung = 0");
        m.Get("sortino")!.IsAvailable.Should().BeFalse();
        m.Get("calmar")!.IsAvailable.Should().BeFalse();
        m.Get("calmar")!.UnavailableReason.Should().Contain("Drawdown = 0");
    }

    [Fact]
    public void Small_samples_are_flagged_and_every_metric_carries_method_and_limits()
    {
        var m = PerformanceMetricsCalculator.Compute(ReferenceSeries());

        m.Notes.Should().Contain(n => n.Contains("Renditeperioden"));
        m.AnnualizationNote.Should().Contain("empirisch");
        m.RiskFreeNote.Should().Contain("0 %");
        m.Metrics.Should().OnlyContain(x =>
            !string.IsNullOrWhiteSpace(x.Method) && !string.IsNullOrWhiteSpace(x.Inputs));
    }

    [Fact]
    public void Risk_free_rate_lowers_sharpe_and_is_converted_geometrically()
    {
        var withRf = PerformanceMetricsCalculator.Compute(ReferenceSeries(),
            new PerformanceMetricsOptions { RiskFreeAnnualRate = 0.04, MinimumPeriods = 0 });
        var withoutRf = PerformanceMetricsCalculator.Compute(ReferenceSeries(),
            new PerformanceMetricsOptions { MinimumPeriods = 0 });

        withRf.Value("sharpe")!.Value.Should().BeLessThan(withoutRf.Value("sharpe")!.Value);

        // Geometrische Umrechnung: (1+0,04)^(1/365,25) − 1
        double rfPeriod = Math.Pow(1.04, 1.0 / 365.25) - 1.0;
        double expected = (0.07 - rfPeriod) / Math.Sqrt(0.017) * Math.Sqrt(365.25);
        withRf.Value("sharpe")!.Value.Should().BeApproximately(expected, 1e-9);
    }

    [Fact]
    public void Trade_activity_metrics_expose_exposure_and_gain_concentration()
    {
        var curve = ReturnSeriesBuilder.BuildEquityCurve(BuildResult(), Mes, ReturnFrequency.Bar);
        var metrics = TradeActivityMetricsCalculator.Compute(BuildResult().Trades, curve, Mes);

        // 2 von 4 Perioden mit offener Position.
        metrics.First(x => x.Key == "exposure").Value!.Value.Should().BeApproximately(0.5, 1e-12);
        metrics.First(x => x.Key == "trade_count").Value!.Value.Should().Be(1);
        // Ein einziger Gewinntrade → gesamte Gewinnsumme konzentriert sich darauf.
        metrics.First(x => x.Key == "gain_concentration_top5").Value!.Value.Should().BeApproximately(1.0, 1e-12);
        metrics.First(x => x.Key == "gain_herfindahl").Value!.Value.Should().BeApproximately(1.0, 1e-12);
    }

    [Fact]
    public void Aligning_series_keeps_only_common_timestamps()
    {
        var a = ReferenceSeries();
        var b = ReferenceSeries() with
        {
            Name = "B",
            Timestamps = new[] { T0.AddDays(2), T0.AddDays(3), T0.AddDays(9) },
            Returns = new[] { 1.0, 2.0, 3.0 },
            EquityLevels = new[] { 1.0, 2.0, 3.0 }
        };

        var aligned = ReturnSeriesBuilder.AlignOnCommonTimestamps(new[] { a, b });

        aligned[0].Timestamps.Should().Equal(T0.AddDays(2), T0.AddDays(3));
        aligned[0].Returns.Should().Equal(-0.10, 0.25);
        aligned[1].Returns.Should().Equal(1.0, 2.0);
    }
}
