using FluentAssertions;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Quant.Registry;
using TradingBot.Quant.Robustness;
using TradingBot.Quant.Series;
using TradingBot.Quant.Validation;

namespace TradingBot.Tests.Quant;

/// <summary>
/// Tests der zeitlich unabhängigen Validierung: Fensteraufteilung, Purging/Embargo, Holdout und
/// die Regel, dass die Parameterauswahl ausschließlich im Training stattfindet.
/// </summary>
public class QuantValidationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static List<DateTimeOffset> Times(int n) => Enumerable.Range(0, n).Select(i => T0.AddMinutes(5 * i)).ToList();

    [Fact]
    public void Rolling_windows_are_chronological_disjoint_and_test_always_follows_train()
    {
        var plan = WalkForwardPlanner.Plan(Times(1000),
            new WalkForwardOptions { Mode = WalkForwardMode.Rolling, TrainBars = 400, TestBars = 200 });

        plan.Folds.Should().HaveCount(3);   // Trainingsenden 400, 600, 800
        foreach (var f in plan.Folds)
        {
            f.Train.Should().ContainSingle();
            f.Train[0].End.Should().Be(f.Test.Start);           // Test folgt unmittelbar
            f.Train[0].Count.Should().Be(400);                  // rollend: feste Länge
            f.Test.Count.Should().Be(200);
        }
        // Testfenster überschneiden sich nicht.
        plan.Folds.Select(f => f.Test.Start).Should().Equal(400, 600, 800);
    }

    [Fact]
    public void Anchored_windows_grow_from_the_start_of_the_data()
    {
        var plan = WalkForwardPlanner.Plan(Times(1000),
            new WalkForwardOptions { Mode = WalkForwardMode.Anchored, TrainBars = 400, TestBars = 200 });

        plan.Folds.Select(f => f.Train[0].Start).Should().AllBeEquivalentTo(0);
        plan.Folds.Select(f => f.Train[0].End).Should().Equal(400, 600, 800);
    }

    [Fact]
    public void Purging_removes_training_bars_whose_label_reaches_into_the_test_window()
    {
        var plan = WalkForwardPlanner.Plan(Times(1000),
            new WalkForwardOptions { TrainBars = 400, TestBars = 200, LabelSpanBars = 50 });

        var first = plan.Folds[0];
        first.PurgedBars.Should().Be(50);
        first.Train[0].End.Should().Be(350);    // 400 − 50
        first.Test.Start.Should().Be(400);      // Testfenster bleibt unverändert
    }

    [Fact]
    public void Purge_and_embargo_rule_excludes_the_right_indices()
    {
        var train = Enumerable.Range(0, 30).ToList();

        var kept = WalkForwardPlanner.ApplyPurgeAndEmbargo(train, testStart: 10, testEnd: 20,
            labelSpanBars: 3, embargoBars: 4);

        // Gesperrt: 7,8,9 (Purging) + 10..19 (Test) + 20..23 (Embargo)
        kept.Should().Equal(0, 1, 2, 3, 4, 5, 6, 24, 25, 26, 27, 28, 29);
    }

    [Fact]
    public void Holdout_is_reserved_at_the_end_and_never_enters_a_fold()
    {
        var plan = WalkForwardPlanner.Plan(Times(1000),
            new WalkForwardOptions { TrainBars = 300, TestBars = 100, HoldoutFraction = 0.2 });

        plan.Holdout.Should().NotBeNull();
        plan.Holdout!.Start.Should().Be(800);
        plan.Holdout.End.Should().Be(1000);
        plan.Holdout.Role.Should().Be(SplitRole.Holdout);

        plan.Folds.Should().NotBeEmpty();
        plan.Folds.Max(f => f.Test.End).Should().BeLessThanOrEqualTo(800);
    }

    [Fact]
    public void Too_little_data_is_reported_instead_of_producing_a_degenerate_fold()
    {
        var plan = WalkForwardPlanner.Plan(Times(100),
            new WalkForwardOptions { TrainBars = 400, TestBars = 200 });

        plan.IsEmpty.Should().BeTrue();
        plan.Notes.Should().Contain(n => n.Contains("Zu wenige Bars"));
    }

    [Fact]
    public async Task Parameter_selection_uses_only_training_data()
    {
        // Kandidat "B" ist im Training am besten, "A" im Test. Gewählt werden MUSS B.
        var candidates = new[]
        {
            new ParameterCandidate("A", new Dictionary<string, string> { ["p"] = "1" }),
            new ParameterCandidate("B", new Dictionary<string, string> { ["p"] = "2" })
        };

        var plan = WalkForwardPlanner.Plan(Times(600), new WalkForwardOptions { TrainBars = 400, TestBars = 200 });

        Task<SegmentOutcome> Evaluate(IReadOnlyDictionary<string, string> p, IReadOnlyList<DataSplit> segs, SplitRole role, CancellationToken ct)
        {
            bool isA = p["p"] == "1";
            double value = role == SplitRole.Train ? (isA ? 0.1 : 0.9) : (isA ? 5.0 : 0.2);
            return Task.FromResult(new SegmentOutcome
            {
                SelectionValue = value,
                TradeCount = 10,
                Returns = new ReturnSeries
                {
                    Name = "seg",
                    Timestamps = new[] { segs[0].ToTime },
                    Returns = new[] { value / 100.0 },
                    EquityLevels = new[] { 1.0 + value / 100.0 }
                }
            });
        }

        var result = await WalkForwardRunner.RunAsync(plan, candidates, Evaluate, "dummy");

        result.Folds.Should().ContainSingle();
        result.Folds[0].Selected!.Id.Should().Be("B");
        result.Folds[0].TrainSelectionValue.Should().Be(0.9);
        result.Folds[0].Test!.SelectionValue.Should().Be(0.2);      // das schlechtere OOS-Ergebnis zählt
        // Die Testwerte ALLER Kandidaten werden für die spätere Overfitting-Analyse mitgeführt.
        result.Folds[0].TestValues["A"].Should().Be(5.0);
    }

    [Fact]
    public async Task Candidates_without_a_valid_training_value_are_skipped_not_treated_as_worst()
    {
        var candidates = new[]
        {
            new ParameterCandidate("broken", new Dictionary<string, string> { ["p"] = "1" }),
            new ParameterCandidate("ok", new Dictionary<string, string> { ["p"] = "2" })
        };
        var plan = WalkForwardPlanner.Plan(Times(600), new WalkForwardOptions { TrainBars = 400, TestBars = 200 });

        Task<SegmentOutcome> Evaluate(IReadOnlyDictionary<string, string> p, IReadOnlyList<DataSplit> s, SplitRole r, CancellationToken ct)
            => Task.FromResult(p["p"] == "1"
                ? SegmentOutcome.Failed("keine Trades")
                : new SegmentOutcome { SelectionValue = -0.5, TradeCount = 3 });

        var result = await WalkForwardRunner.RunAsync(plan, candidates, Evaluate, "dummy");

        result.Folds[0].Selected!.Id.Should().Be("ok");
        result.Folds[0].TrainValues["broken"].Should().BeNull();
    }

    [Fact]
    public void Selection_is_deterministic_when_candidates_tie()
    {
        var candidates = new[]
        {
            new ParameterCandidate("zeta", new Dictionary<string, string>()),
            new ParameterCandidate("alpha", new Dictionary<string, string>()),
            new ParameterCandidate("mike", new Dictionary<string, string>())
        };
        var values = new Dictionary<string, double?> { ["zeta"] = 1.0, ["alpha"] = 1.0, ["mike"] = 1.0 };

        for (int i = 0; i < 5; i++)
            WalkForwardRunner.SelectBest(candidates, values, SelectionDirection.HigherIsBetter)!.Id
                .Should().Be("alpha");
    }

    // ---------------------------------------------------------------------------------------
    // Ausführungsverzögerung
    // ---------------------------------------------------------------------------------------

    private sealed class SignalOnBarsStrategy : IStrategy
    {
        private readonly HashSet<int> _bars;
        private int _index = -1;
        public SignalOnBarsStrategy(params int[] bars) => _bars = new HashSet<int>(bars);
        public string Name => "SignalOnBars";
        public TradeSignal? OnCandle(Candle candle)
        {
            _index++;
            return _bars.Contains(_index)
                ? new TradeSignal { StrategyName = Name, Symbol = candle.Symbol, Direction = SignalDirection.Long, Timestamp = candle.CloseTime }
                : null;
        }
    }

    private static Candle Bar(int i) => new()
    {
        Symbol = "MES",
        OpenTime = T0.AddMinutes(5 * i),
        CloseTime = T0.AddMinutes(5 * (i + 1)),
        Open = 5000, High = 5001, Low = 4999, Close = 5000, Volume = 10
    };

    [Fact]
    public void Delayed_strategy_shifts_signals_by_the_configured_number_of_bars()
    {
        var strategy = new DelayedSignalStrategy(new SignalOnBarsStrategy(0, 2), delayBars: 1);

        var emitted = Enumerable.Range(0, 6).Select(i => strategy.OnCandle(Bar(i)) is not null).ToList();

        // Signale an Bar 0 und 2 → nach einer Bar Verzögerung an Bar 1 und 3.
        emitted.Should().Equal(false, true, false, true, false, false);
    }

    [Fact]
    public void Delay_of_zero_passes_signals_through_unchanged()
    {
        var strategy = new DelayedSignalStrategy(new SignalOnBarsStrategy(0, 2), delayBars: 0);
        var emitted = Enumerable.Range(0, 4).Select(i => strategy.OnCandle(Bar(i)) is not null).ToList();
        emitted.Should().Equal(true, false, true, false);
    }

    [Fact]
    public void Stress_summary_ignores_invalid_scenarios_instead_of_counting_them_as_bad()
    {
        var scenarios = StressAnalysis.CostGrid(new[] { 1.0, 2.0 }, new[] { 1.0 });
        var outcomes = new List<StressOutcome>
        {
            new() { Scenario = scenarios[0], Value = 1.0, TradeCount = 10 },
            new() { Scenario = scenarios[1], Value = 0.4, TradeCount = 10 },
            new() { Scenario = scenarios[1] with { Id = "broken" }, Value = null, Error = "keine Trades" }
        };

        var summary = StressAnalysis.Summarize("sharpe", StressDimension.Costs, outcomes, baselineValue: 1.0);

        summary.Worst.Should().Be(0.4);
        summary.ShareBelowBaseline.Should().BeApproximately(0.5, 1e-12);   // 1 von 2 gültigen
        summary.RelativeDegradation.Should().BeApproximately(0.6, 1e-12);
        summary.Notes.Should().Contain(n => n.Contains("keinen gültigen Wert"));
    }

    [Fact]
    public void Parameter_neighborhood_varies_one_integer_parameter_at_a_time()
    {
        var baseParams = new Dictionary<string, string> { ["FastPeriod"] = "9", ["SlowPeriod"] = "21", ["Mode"] = "text" };

        var scenarios = StressAnalysis.ParameterNeighborhood(baseParams, new[] { -2, 2 });

        scenarios[0].Id.Should().Be("param-base");
        scenarios.Should().HaveCount(5);   // Basis + 2 Varianten je numerischem Parameter
        scenarios.Should().Contain(s => s.Parameters["FastPeriod"] == "11" && s.Parameters["SlowPeriod"] == "21");
        scenarios.Should().Contain(s => s.Parameters["SlowPeriod"] == "19" && s.Parameters["FastPeriod"] == "9");
        scenarios.Should().OnlyContain(s => s.Parameters["Mode"] == "text");
    }

    // -------------------------------------------------------------------------------------------
    // Befund 4: Überlappende Testfenster (StepBars < TestBars) werden abgelehnt, statt Perioden
    // doppelt zu zählen oder rückwärts laufende Zeitstempel zu erzeugen.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Overlapping_test_windows_are_rejected_with_a_clear_note()
    {
        var plan = WalkForwardPlanner.Plan(Times(1000),
            new WalkForwardOptions { TrainBars = 200, TestBars = 100, StepBars = 50 });

        plan.IsEmpty.Should().BeTrue();
        plan.Folds.Should().BeEmpty();
        plan.Notes.Should().Contain(n => n.Contains("überlappen") && n.Contains("StepBars ≥ TestBars"));
    }

    [Fact]
    public void Step_equal_to_test_length_stays_disjoint_and_is_accepted()
    {
        // Gegenprobe: StepBars == TestBars ist der lückenlose, disjunkte Standardfall und bleibt gültig.
        var plan = WalkForwardPlanner.Plan(Times(1000),
            new WalkForwardOptions { TrainBars = 200, TestBars = 100, StepBars = 100 });

        plan.IsEmpty.Should().BeFalse();
        // Testfenster laufen streng vorwärts und überschneiden sich nicht.
        var starts = plan.Folds.Select(f => f.Test.Start).ToList();
        starts.Should().BeInAscendingOrder();
        for (int i = 1; i < starts.Count; i++)
            (starts[i] - starts[i - 1]).Should().BeGreaterThanOrEqualTo(100);
    }

    // -------------------------------------------------------------------------------------------
    // Befund 6: PBO-Matrix wird ZEITLICH ausgerichtet. Bricht ein Kandidat in einem frühen Fold
    // vorzeitig ab, dürfen seine späteren Werte nicht gegen frühere Zeiträume anderer verglichen werden.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Candidate_matrix_aligns_by_timestamp_and_drops_early_terminated_periods()
    {
        var t = Times(3);                         // t0,t1,t2 im Fold 0
        var u = Enumerable.Range(0, 2).Select(i => T0.AddDays(1).AddMinutes(5 * i)).ToList(); // u0,u1 im Fold 1

        // Fold 0: Kandidat A liefert 3 Perioden, Kandidat B bricht nach 2 ab (Kapital ≤ 0).
        // Fold 1: beide liefern 2 Perioden.
        var folds = new List<IReadOnlyList<CandidateFoldReturns>>
        {
            new List<CandidateFoldReturns>
            {
                new("A", t, new[] { 0.01, 0.02, 0.03 }),
                new("B", new[] { t[0], t[1] }, new[] { 0.05, 0.06 })
            },
            new List<CandidateFoldReturns>
            {
                new("A", u, new[] { 0.10, 0.20 }),
                new("B", u, new[] { 0.30, 0.40 })
            }
        };

        var (series, notes, dropped) = CandidateMatrixAligner.AlignByCommonTimestamps(new[] { "A", "B" }, folds);

        // Beide Reihen sind gleich lang und zeitlich ausgerichtet (t0,t1 | u0,u1).
        series["A"].Should().Equal(0.01, 0.02, 0.10, 0.20);
        series["B"].Should().Equal(0.05, 0.06, 0.30, 0.40);
        // A's überzählige frühe Periode (0.03 bei t2) wird NICHT gegen B's spätere Fold-1-Werte gestellt.
        series["A"].Should().NotContain(0.03);
        notes.Should().Contain(n => n.Contains("Fold 0") && n.Contains("nicht bei allen Kandidaten"));
        // Die verworfene Periode wird strukturiert gezählt, damit die PBO-Stufe sie erkennen kann.
        dropped.Should().Be(1);
    }

    // -------------------------------------------------------------------------------------------
    // Befund 6 (Nachprüfung): Verkürzt die Zeitstempel-Ausrichtung die gemeinsame Datenbasis, weil ein
    // Kandidat vorzeitig abbrach (Kapital ≤ 0) oder mit einem Fehler ausfiel, MUSS PBO als nicht
    // berechenbar gemeldet werden — statt einen Wert auf stillschweigend gekürzter Basis auszuweisen.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Pbo_is_computable_when_no_period_was_dropped_and_no_candidate_failed()
    {
        CandidateMatrixAligner.PboBlockedReason(0, Array.Empty<string>(), Array.Empty<string>()).Should().BeNull();
    }

    [Fact]
    public void Pbo_is_blocked_when_the_alignment_had_to_shorten_the_basis()
    {
        var reason = CandidateMatrixAligner.PboBlockedReason(2, Array.Empty<string>(), Array.Empty<string>());

        reason.Should().NotBeNull();
        reason.Should().Contain("nicht berechenbar");
        reason.Should().Contain("2 Periode");        // die verworfenen Perioden werden benannt
    }

    [Fact]
    public void Pbo_is_blocked_when_a_candidate_failed_with_an_error()
    {
        var reason = CandidateMatrixAligner.PboBlockedReason(0,
            new[] { "Kandidat 'c1' in Fold 0: Auswertungsfehler (x)." }, Array.Empty<string>());

        reason.Should().NotBeNull();
        reason.Should().Contain("nicht berechenbar");
        reason.Should().Contain("Fehler");
    }

    // -------------------------------------------------------------------------------------------
    // Befund 1 (Nachprüfung): Das Truncated-Flag aus ToReturnSeries wird ausgewertet. Brechen ALLE
    // Kandidaten im SELBEN Fold zum GLEICHEN Zeitpunkt ab (Kapital ≤ 0), sind die Reihen gleich lang,
    // die Zeitstempel-Schnittmenge verwirft nichts (DroppedPeriods == 0) — nur das Truncated-Flag deckt
    // den Abbruch auf. PBO MUSS dann als nicht berechenbar erscheinen.
    // -------------------------------------------------------------------------------------------

    /// <summary>Kapitalkurve, deren Gesamtkapital nach der ersten Periode auf ≤ 0 fällt (echter Abbruchfall).</summary>
    private static QuantEquityCurve CurveThatHitsZero()
    {
        QuantEquityPoint P(int min, decimal total) => new()
        {
            Time = T0.AddMinutes(min), BarIndex = min / 5, RealizedEquity = total, TotalEquity = total, OpenQuantity = 0
        };
        // 100 -> -10 (Kapital ≤ 0) -> 50: ToReturnSeries misst die erste Rendite und bricht bei der NÄCHSTEN
        // Periode ab, weil das Vorperiodenkapital ≤ 0 ist (Truncated == true).
        return new QuantEquityCurve
        {
            Name = "abbruch", InitialCapital = 100m, Frequency = ReturnFrequency.Bar,
            Points = new[] { P(0, 100m), P(5, -10m), P(10, 50m) }
        };
    }

    [Fact]
    public void ToReturnSeries_flags_truncation_when_capital_hits_zero()
    {
        var built = ReturnSeriesBuilder.ToReturnSeries(CurveThatHitsZero(), EquityBasis.Total);
        built.Truncated.Should().BeTrue();     // das Flag, das der PBO-Servicepfad erhalten muss
    }

    [Fact]
    public void Pbo_is_not_computable_when_all_candidates_truncate_at_the_same_time_in_the_same_fold()
    {
        // Echtes ToReturnSeries-Ergebnis (kein vorgegebener Zahlenwert): Kurve fällt auf Kapital ≤ 0.
        var built = ReturnSeriesBuilder.ToReturnSeries(CurveThatHitsZero(), EquityBasis.Total);
        built.Truncated.Should().BeTrue();

        // Symmetrisch: BEIDE Kandidaten liefern dieselbe (abgebrochene) Reihe im selben Fold.
        var fold = new List<CandidateFoldSeries>
        {
            new("A", built.Series.Timestamps, built.Series.Returns, built.Truncated),
            new("B", built.Series.Timestamps, built.Series.Returns, built.Truncated)
        };
        var res = CandidateMatrixAligner.AlignAndDetectAborts(
            new[] { "A", "B" }, new[] { (IReadOnlyList<CandidateFoldSeries>)fold });

        // Gleiche Reihenlänge ⇒ die Schnittmenge verwirft NICHTS: die Drop-Zahl allein würde den Abbruch
        // nicht bemerken. Erst das Truncated-Flag deckt ihn auf.
        res.DroppedPeriods.Should().Be(0);
        res.Truncations.Should().NotBeEmpty();

        // Genau dieser Fall muss PBO als nicht berechenbar melden — trotz DroppedPeriods == 0 und ohne Fehler.
        var reason = CandidateMatrixAligner.PboBlockedReason(res.DroppedPeriods, Array.Empty<string>(), res.Truncations);
        reason.Should().NotBeNull();
        reason.Should().Contain("nicht berechenbar");
        reason.Should().Contain("Kapital ≤ 0");
    }
}
