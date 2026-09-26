using FluentAssertions;
using TradingBot.Quant.Generation;
using TradingBot.Quant.Metrics;
using TradingBot.Quant.Registry;
using TradingBot.Quant.Research;
using TradingBot.Quant.Series;

namespace TradingBot.Tests.Quant;

/// <summary>
/// Rollierende Kennzahlen, Kalenderaggregate sowie die Trennung von Generator und Evaluator
/// samt Holdout-Schutz und Versuchsbudget.
/// </summary>
public class QuantRollingAndGenerationTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "quant-gen-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static ReturnSeries Series(IReadOnlyList<DateTimeOffset> times, IReadOnlyList<double> returns)
    {
        var levels = new List<double>();
        double e = 100;
        foreach (var r in returns) { e *= 1 + r; levels.Add(e); }
        return new ReturnSeries
        {
            Name = "s", Timestamps = times, Returns = returns, EquityLevels = levels,
            InitialCapital = 100, Basis = EquityBasis.Total, Frequency = ReturnFrequency.Daily
        };
    }

    [Fact]
    public void Underwater_curve_matches_hand_computed_values()
    {
        var uw = RollingMetrics.Underwater(new[] { 100.0, 110.0, 99.0, 123.75 });

        uw[0].Should().BeApproximately(0.0, 1e-12);
        uw[1].Should().BeApproximately(0.0, 1e-12);
        uw[2].Should().BeApproximately(99.0 / 110.0 - 1.0, 1e-12);   // −10 %
        uw[3].Should().BeApproximately(0.0, 1e-12);
    }

    [Fact]
    public void Monthly_returns_compound_within_observed_months_only()
    {
        var times = new[]
        {
            T0.AddDays(1), T0.AddDays(2),      // Januar
            T0.AddMonths(1).AddDays(1)         // Februar
        };
        var monthly = RollingMetrics.Monthly(Series(times, new[] { 0.10, -0.10, 0.25 }));

        monthly.Should().HaveCount(2);
        monthly[0].Period.Should().Be("2026-01");
        monthly[0].Return.Should().BeApproximately(1.10 * 0.90 - 1.0, 1e-12);   // −1 %
        monthly[0].Observations.Should().Be(2);
        monthly[1].Period.Should().Be("2026-02");
        monthly[1].Return.Should().BeApproximately(0.25, 1e-12);
        // Kein März, kein Auffüllen leerer Monate.
        monthly.Select(m => m.Period).Should().Equal("2026-01", "2026-02");
    }

    [Fact]
    public void Rolling_window_starts_at_the_first_complete_window()
    {
        var times = Enumerable.Range(1, 5).Select(i => T0.AddDays(i)).ToList();
        var series = Series(times, new[] { 0.10, -0.10, 0.25, 0.00, 0.10 });

        var points = RollingMetrics.Compute(series, window: 3, periodsPerYear: 365.25);

        points.Should().HaveCount(3);                       // Fenster-Enden bei Index 2,3,4
        points[0].Time.Should().Be(times[2]);

        // Erstes Fenster [0,10; −0,10; 0,25]:
        //   Mittel = 0,25/3 = 1/12
        //   Abweichungen 1/60, −11/60, 10/60 → Σ(r−r̄)² = (1+121+100)/3600 = 222/3600
        //   Stichprobenvarianz = 111/3600 → SD = √(111/3600)
        double mean = 0.25 / 3.0;
        double sd = Math.Sqrt(111.0 / 3600.0);
        points[0].Sharpe!.Value.Should().BeApproximately(mean / sd * Math.Sqrt(365.25), 1e-9);
        points[0].Volatility!.Value.Should().BeApproximately(sd * Math.Sqrt(365.25), 1e-9);
        points[0].Return!.Value.Should().BeApproximately(1.10 * 0.90 * 1.25 - 1.0, 1e-12);
    }

    [Fact]
    public void Rolling_window_larger_than_the_series_returns_nothing_instead_of_estimating()
    {
        var times = Enumerable.Range(1, 3).Select(i => T0.AddDays(i)).ToList();
        RollingMetrics.Compute(Series(times, new[] { 0.01, 0.02, 0.03 }), window: 10, periodsPerYear: 252)
            .Should().BeEmpty();
    }

    // -----------------------------------------------------------------------------------------
    // Generator / Evaluator
    // -----------------------------------------------------------------------------------------

    private sealed class CountingGenerator : IStrategyGenerator
    {
        public int Calls;
        public List<EvaluationFeedback> LastFeedback = new();
        public string Name => "Test";
        public Task<IReadOnlyList<StrategyProposal>> ProposeAsync(CampaignRecord campaign,
            IReadOnlyList<EvaluationFeedback> previousFeedback, int count, CancellationToken ct = default)
        {
            Calls++;
            LastFeedback = previousFeedback.ToList();
            IReadOnlyList<StrategyProposal> list = Enumerable.Range(0, count).Select(i => new StrategyProposal
            {
                Id = $"p{Calls}-{i}",
                StrategyId = "movingaverage",
                Parameters = new Dictionary<string, string> { ["FastPeriod"] = (5 + i).ToString() },
                Rationale = "Test"
            }).ToList();
            return Task.FromResult(list);
        }
    }

    /// <summary>Gibt absichtlich auch Holdout-Kennzahlen zurück — der Runner muss sie entfernen.</summary>
    private sealed class LeakyEvaluator : IStrategyEvaluator
    {
        public int Calls;
        public Task<EvaluationFeedback> EvaluateAsync(CampaignRecord campaign, StrategyProposal proposal, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(new EvaluationFeedback
            {
                ProposalId = proposal.Id,
                TrialId = "t" + Calls,
                Status = TrialStatus.Completed,
                Metrics = new Dictionary<string, double?>
                {
                    ["train.sharpe"] = 0.5,
                    ["holdout.sharpe"] = 2.5   // darf den Generator NICHT erreichen
                }
            });
        }
    }

    private async Task<CampaignRecord> CampaignAsync(JsonExperimentStore store, int budget) =>
        await store.CreateCampaignAsync(new CampaignRecord
        {
            Id = "gen1", Name = "Generatorlauf",
            Hypothesis = "Test", SearchSpace = "FastPeriod ∈ {5..}", SelectionMetric = "sharpe",
            TrialBudget = budget
        });

    [Fact]
    public void Holdout_metrics_are_stripped_from_generator_feedback()
    {
        var feedback = new EvaluationFeedback
        {
            ProposalId = "p", TrialId = "t",
            Metrics = new Dictionary<string, double?> { ["train.sharpe"] = 1.0, ["holdout.cagr"] = 0.3 }
        };

        var (clean, removed) = GenerationCampaignRunner.SanitizeFeedback(feedback);

        removed.Should().BeTrue();
        clean.Metrics.Should().ContainKey("train.sharpe");
        clean.Metrics.Should().NotContainKey("holdout.cagr");
    }

    [Fact]
    public async Task Generation_campaign_respects_the_budget_stored_in_advance()
    {
        var store = new JsonExperimentStore(_dir);
        await CampaignAsync(store, budget: 3);

        var generator = new CountingGenerator();
        var evaluator = new LeakyEvaluator();
        var runner = new GenerationCampaignRunner(store);

        var history = await runner.RunAsync("gen1", generator, evaluator, batchSize: 5);

        history.Should().HaveCount(3);          // Budget 3 — nicht mehr
        evaluator.Calls.Should().Be(3);
        history.Should().OnlyContain(f => !f.Metrics.ContainsKey("holdout.sharpe"));
        history.Should().OnlyContain(f => f.Note != null && f.Note.Contains("Holdout-Kennzahlen"));
    }

    [Fact]
    public async Task Generation_campaign_refuses_to_start_without_a_registered_campaign()
    {
        var runner = new GenerationCampaignRunner(new JsonExperimentStore(_dir));

        var ex = await Assert.ThrowsAsync<ExperimentRegistryException>(() =>
            runner.RunAsync("unbekannt", new CountingGenerator(), new LeakyEvaluator()));

        ex.Code.Should().Be("CAMPAIGN_UNKNOWN");
        ex.Message.Should().Contain("VOR der Suche");
    }

    [Fact]
    public async Task Paper_entries_persist_source_rules_and_documented_deviations()
    {
        var store = new JsonPaperResearchStore(Path.Combine(_dir, "papers"));

        await store.SaveAsync(new PaperResearchEntry
        {
            Id = "tsmom-2012",
            Title = "Time Series Momentum",
            Authors = "Moskowitz, Ooi, Pedersen",
            Year = 2012,
            Source = "doi:10.1016/j.jfineco.2011.11.003",
            Hypothesis = "Vergangene 12-Monats-Rendite sagt die künftige Richtung an.",
            DataRequirements = "Monatliche Futures-Renditen über mehrere Anlageklassen.",
            Rules = "Long bei positiver 12-Monats-Rendite, sonst Short; Volatilitätsskalierung.",
            Deviations = new[]
            {
                new PaperDeviation("Universum", "58 Futures-Kontrakte", "nur MES",
                    "Lokal liegen nur MES-Daten vor — das Ergebnis ist damit KEINE Replikation des Papers.")
            },
            Status = PaperStatus.Registered
        });

        var all = await store.ListAsync();
        all.Should().ContainSingle();
        all[0].Deviations.Should().ContainSingle(d => d.Aspect == "Universum");
        all[0].Source.Should().StartWith("doi:");
    }

    [Fact]
    public async Task Paper_entry_marked_not_applicable_requires_a_reason()
    {
        var store = new JsonPaperResearchStore(Path.Combine(_dir, "papers2"));

        await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(new PaperResearchEntry
        {
            Id = "x", Title = "T", Source = "s", Hypothesis = "h",
            DataRequirements = "d", Rules = "r", Status = PaperStatus.NotApplicable
        }));
    }
}
