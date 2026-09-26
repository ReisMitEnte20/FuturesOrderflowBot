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
}
