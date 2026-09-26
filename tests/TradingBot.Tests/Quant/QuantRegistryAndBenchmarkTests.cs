using FluentAssertions;
using TradingBot.Domain.Models;
using TradingBot.Quant.Benchmark;
using TradingBot.Quant.DataQuality;
using TradingBot.Quant.Registry;
using TradingBot.Quant.Series;

namespace TradingBot.Tests.Quant;

/// <summary>
/// Tests des Versuchsregisters (Budget, Holdout-Schutz, vollständige Erfassung auch negativer
/// Versuche), des Datenfingerabdrucks, der Datenqualitätsprüfung und des Benchmarkvergleichs.
/// </summary>
public class QuantRegistryAndBenchmarkTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "quant-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private JsonExperimentStore Store() => new(_dir);

    private static CampaignRecord Campaign(string id = "c1", int budget = 3,
        DateTimeOffset? holdoutFrom = null, DateTimeOffset? holdoutTo = null) => new()
    {
        Id = id,
        Name = "Testkampagne",
        Hypothesis = "Teststrategie ohne Edge-Behauptung.",
        SearchSpace = "FastPeriod ∈ {5,9}, SlowPeriod ∈ {21,50}",
        SelectionMetric = "sharpe",
        TrialBudget = budget,
        HoldoutFrom = holdoutFrom,
        HoldoutTo = holdoutTo
    };

    private static TrialRecord Trial(string id, string campaignId = "c1", string role = "train",
        DateTimeOffset? from = null, DateTimeOffset? to = null) => new()
    {
        Id = id,
        CampaignId = campaignId,
        StrategyId = "movingaverage",
        StrategyVersion = "1.0.0",
        CodeVersion = "abc1234",
        Seed = 42,
        PeriodRole = role,
        PeriodFrom = from,
        PeriodTo = to,
        Data = new DataFingerprint { Symbol = "MES", TimeframeMinutes = 5, Source = "test", Sha256 = "deadbeef", BarCount = 10 }
    };

    // -------------------------------------------------------------------------------------
    // Datenfingerabdruck
    // -------------------------------------------------------------------------------------

    private static List<Candle> Candles(int n, decimal start = 5000m, int stepMinutes = 5)
        => Enumerable.Range(0, n).Select(i => new Candle
        {
            Symbol = "MES",
            OpenTime = T0.AddMinutes(stepMinutes * i),
            CloseTime = T0.AddMinutes(stepMinutes * (i + 1)),
            Open = start + i, High = start + i + 2, Low = start + i - 2, Close = start + i + 1, Volume = 100
        }).ToList();

    [Fact]
    public void Data_fingerprint_is_deterministic_and_changes_with_the_data()
    {
        var a = DataFingerprint.Compute(Candles(50), "MES", 5, "test");
        var b = DataFingerprint.Compute(Candles(50), "MES", 5, "test");
        a.Sha256.Should().Be(b.Sha256);
        a.Sha256.Should().HaveLength(64);

        var changed = Candles(50);
        changed[10] = changed[10] with { Close = changed[10].Close + 0.25m };
        DataFingerprint.Compute(changed, "MES", 5, "test").Sha256.Should().NotBe(a.Sha256);

        // Auch Symbol und Zeitrahmen gehen in den Fingerabdruck ein.
        DataFingerprint.Compute(Candles(50), "MNQ", 5, "test").Sha256.Should().NotBe(a.Sha256);
        DataFingerprint.Compute(Candles(50), "MES", 15, "test").Sha256.Should().NotBe(a.Sha256);
    }

    // -------------------------------------------------------------------------------------
    // Versuchsregister
    // -------------------------------------------------------------------------------------

    [Fact]
    public async Task Campaign_requires_budget_search_space_and_criterion_and_is_locked_on_creation()
    {
        var store = Store();

        var created = await store.CreateCampaignAsync(Campaign());
        created.Locked.Should().BeTrue();

        await Assert.ThrowsAsync<ExperimentRegistryException>(() =>
            store.CreateCampaignAsync(Campaign(id: "c2", budget: 0)));
        await Assert.ThrowsAsync<ExperimentRegistryException>(() =>
            store.CreateCampaignAsync(Campaign(id: "c3") with { SearchSpace = "  " }));
        await Assert.ThrowsAsync<ExperimentRegistryException>(() =>
            store.CreateCampaignAsync(Campaign(id: "c4") with { SelectionMetric = "" }));
    }

    [Fact]
    public async Task Trial_budget_fixed_before_the_campaign_is_enforced()
    {
        var store = Store();
        await store.CreateCampaignAsync(Campaign(budget: 2));

        await store.AddTrialAsync(Trial("t1"));
        await store.AddTrialAsync(Trial("t2"));

        var ex = await Assert.ThrowsAsync<ExperimentRegistryException>(() => store.AddTrialAsync(Trial("t3")));
        ex.Code.Should().Be("BUDGET_EXCEEDED");
    }

    [Fact]
    public async Task Trials_cannot_be_added_to_an_unknown_campaign()
    {
        var ex = await Assert.ThrowsAsync<ExperimentRegistryException>(() => Store().AddTrialAsync(Trial("t1")));
        ex.Code.Should().Be("CAMPAIGN_UNKNOWN");
    }

    [Fact]
    public async Task Negative_and_discarded_trials_are_persisted_with_a_mandatory_reason()
    {
        var store = Store();
        await store.CreateCampaignAsync(Campaign());
        await store.AddTrialAsync(Trial("t1"));

        // Ohne Begründung nicht erlaubt.
        var ex = await Assert.ThrowsAsync<ExperimentRegistryException>(() =>
            store.UpdateTrialAsync(Trial("t1") with { Status = TrialStatus.Discarded }));
        ex.Code.Should().Be("STATUS_REASON_MISSING");

        await store.UpdateTrialAsync(Trial("t1") with
        {
            Status = TrialStatus.Failed,
            StatusReason = "Keine Trades im Zeitraum.",
            Metrics = new Dictionary<string, double?> { ["sharpe"] = null }
        });

        // Gescheiterte Versuche bleiben im Register — sie zählen für die Mehrfachtest-Korrektur.
        var all = await store.ListTrialsAsync("c1");
        all.Should().ContainSingle();
        all[0].Status.Should().Be(TrialStatus.Failed);
        all[0].StatusReason.Should().Contain("Keine Trades");
        all[0].Metrics["sharpe"].Should().BeNull();
    }

    [Fact]
    public async Task Holdout_is_protected_against_leakage_and_can_only_be_consumed_once()
    {
        var store = Store();
        var from = T0.AddDays(300);
        var to = T0.AddDays(365);
        await store.CreateCampaignAsync(Campaign(holdoutFrom: from, holdoutTo: to));

        // Ein Trainingszeitraum, der in den Holdout hineinragt, wird abgelehnt.
        var leak = await Assert.ThrowsAsync<ExperimentRegistryException>(() =>
            store.AddTrialAsync(Trial("t1", role: "train", from: T0, to: T0.AddDays(310))));
        leak.Code.Should().Be("HOLDOUT_LEAKAGE");

        // Sauberes Training davor ist erlaubt.
        await store.AddTrialAsync(Trial("t1", role: "train", from: T0, to: from));

        // Der Holdout darf genau einmal ausgewertet werden.
        await store.AddTrialAsync(Trial("t2", role: "holdout", from: from, to: to));
        await store.ConsumeHoldoutAsync("c1");

        var again = await Assert.ThrowsAsync<ExperimentRegistryException>(() =>
            store.AddTrialAsync(Trial("t3", role: "holdout", from: from, to: to)));
        again.Code.Should().Be("HOLDOUT_CONSUMED");
    }

    [Fact]
    public async Task Registry_survives_a_restart_because_it_is_persisted()
    {
        await Store().CreateCampaignAsync(Campaign());
        await Store().AddTrialAsync(Trial("t1", role: "train") with
        {
            Origin = StrategyOrigin.Paper,
            OriginReference = "doi:10.0000/example",
            Metrics = new Dictionary<string, double?> { ["sharpe"] = 1.25, ["pbo"] = null }
        });

        var reopened = await Store().ListTrialsAsync("c1");

        reopened.Should().ContainSingle();
        reopened[0].Origin.Should().Be(StrategyOrigin.Paper);
        reopened[0].OriginReference.Should().Be("doi:10.0000/example");
        reopened[0].Metrics["sharpe"].Should().Be(1.25);
        reopened[0].Metrics["pbo"].Should().BeNull();
    }

    // -------------------------------------------------------------------------------------
    // Datenqualität
    // -------------------------------------------------------------------------------------

    [Fact]
    public void Data_quality_reports_gaps_duplicates_and_integrity_violations_without_repairing_them()
    {
        var candles = Candles(20);
        candles[5] = candles[5] with { High = 0m };                            // Integritätsverstoß
        candles[10] = candles[10] with { OpenTime = candles[9].OpenTime };      // Dublette
        candles[15] = candles[15] with { OpenTime = candles[14].OpenTime.AddMinutes(60) };  // Lücke

        var report = QuantDataQualityChecker.Check(candles, "MES", 5);

        report.BarCount.Should().Be(20);          // nichts wurde entfernt oder ergänzt
        report.Issues.Should().Contain(i => i.Code == "OHLC_INTEGRITY");
        report.Issues.Should().Contain(i => i.Code == "DUPLICATE_TIMESTAMP");
        report.Issues.Should().Contain(i => i.Code == "INTRADAY_GAP");
        report.HasErrors.Should().BeTrue();
    }

    [Fact]
    public void Weekend_gaps_are_classified_as_expected_session_breaks()
    {
        // 2026-01-02 ist ein Freitag; der nächste Bar liegt am Montag.
        var friday = new DateTimeOffset(2026, 1, 2, 20, 0, 0, TimeSpan.Zero);
        var candles = new List<Candle>
        {
            new() { Symbol = "MES", OpenTime = friday, CloseTime = friday.AddMinutes(5), Open = 5000, High = 5002, Low = 4998, Close = 5001, Volume = 10 },
            new() { Symbol = "MES", OpenTime = friday.AddDays(3), CloseTime = friday.AddDays(3).AddMinutes(5), Open = 5001, High = 5003, Low = 4999, Close = 5002, Volume = 10 }
        };

        var report = QuantDataQualityChecker.Check(candles, "MES", 5);

        report.Issues.Should().Contain(i => i.Code == "SESSION_GAP");
        report.Issues.Should().NotContain(i => i.Code == "INTRADAY_GAP");
    }

    [Fact]
    public void A_large_jump_across_a_gap_is_reported_as_a_roll_suspicion_only()
    {
        var candles = Candles(30);
        // Lücke plus großer Preissprung: typisch für einen Kontraktwechsel.
        candles[20] = candles[20] with { OpenTime = candles[19].OpenTime.AddHours(8), Open = 5500m, High = 5502m, Low = 5498m, Close = 5501m };

        var report = QuantDataQualityChecker.Check(candles, "MES", 5);

        var roll = report.Issues.FirstOrDefault(i => i.Code == "ROLL_SUSPECTED");
        roll.Should().NotBeNull();
        roll!.Message.Should().Contain("VERDACHT");
        roll.Severity.Should().Be(QuantIssueSeverity.Warning);
    }

    [Fact]
    public void Empty_data_is_an_error_not_an_empty_result()
    {
        var report = QuantDataQualityChecker.Check(Array.Empty<Candle>(), "MES", 5);
        report.HasErrors.Should().BeTrue();
        report.Issues.Should().ContainSingle(i => i.Code == "NO_DATA");
    }

    // -------------------------------------------------------------------------------------
    // Benchmark
    // -------------------------------------------------------------------------------------

    private static ReturnSeries Series(string name, IReadOnlyList<double> returns)
    {
        var times = Enumerable.Range(1, returns.Count).Select(i => T0.AddDays(i)).ToList();
        var levels = new List<double>();
        double e = 100;
        foreach (var r in returns) { e *= 1 + r; levels.Add(e); }
        return new ReturnSeries
        {
            Name = name, Timestamps = times, Returns = returns, EquityLevels = levels,
            InitialCapital = 100, Basis = EquityBasis.Total, Frequency = ReturnFrequency.Daily
        };
    }

    private static BenchmarkSeries BenchmarkFrom(IReadOnlyList<double> returns, bool totalReturn = true)
    {
        var obs = new List<BenchmarkObservation> { new(T0, 100.0) };
        double lvl = 100.0;
        for (int i = 0; i < returns.Count; i++)
        {
            lvl *= 1 + returns[i];
            obs.Add(new BenchmarkObservation(T0.AddDays(i + 1), lvl));
        }
        return new BenchmarkSeries
        {
            Id = "bm", Name = "Testbenchmark", Observations = obs,
            IsTotalReturn = totalReturn, Provenance = "Test"
        };
    }

    [Fact]
    public void Without_benchmark_data_no_comparison_curve_is_invented()
    {
        var result = BenchmarkComparer.Compare(Series("S", new[] { 0.01, 0.02, -0.01 }), benchmark: null);

        result.Available.Should().BeFalse();
        result.UnavailableReason.Should().Contain("keine Vergleichskurve");
        result.Metrics.Should().BeEmpty();
    }

    [Fact]
    public void Identical_series_give_beta_one_alpha_zero_and_an_undefined_information_ratio()
    {
        var r = new[] { 0.01, -0.02, 0.03, 0.00, 0.015, -0.005, 0.02, 0.01 };

        var result = BenchmarkComparer.Compare(Series("S", r), BenchmarkFrom(r),
            new BenchmarkComparisonOptions { StrategyIsFullyFunded = true });

        result.Available.Should().BeTrue();
        result.CommonPeriods.Should().Be(r.Length);
        result.Metrics.First(m => m.Key == "beta").Value!.Value.Should().BeApproximately(1.0, 1e-9);
        result.Metrics.First(m => m.Key == "alpha_annual").Value!.Value.Should().BeApproximately(0.0, 1e-9);
        result.Metrics.First(m => m.Key == "correlation").Value!.Value.Should().BeApproximately(1.0, 1e-9);
        result.Metrics.First(m => m.Key == "r_squared").Value!.Value.Should().BeApproximately(1.0, 1e-9);

        var ir = result.Metrics.First(m => m.Key == "information_ratio");
        ir.IsAvailable.Should().BeFalse();
        ir.UnavailableReason.Should().Contain("Tracking Error = 0");
    }

    [Fact]
    public void Beta_of_a_doubled_series_is_two()
    {
        var bench = new[] { 0.01, -0.02, 0.03, 0.00, 0.015, -0.005, 0.02, 0.01 };
        var strat = bench.Select(v => 2.0 * v).ToArray();

        var result = BenchmarkComparer.Compare(Series("S", strat), BenchmarkFrom(bench),
            new BenchmarkComparisonOptions { StrategyIsFullyFunded = true });

        result.Metrics.First(m => m.Key == "beta").Value!.Value.Should().BeApproximately(2.0, 1e-9);
    }

    [Fact]
    public void Missing_confirmations_produce_explicit_warnings()
    {
        var r = new[] { 0.01, -0.02, 0.03, 0.00, 0.015, -0.005 };

        var result = BenchmarkComparer.Compare(Series("S", r), BenchmarkFrom(r, totalReturn: false));

        result.Warnings.Should().Contain(w => w.Contains("Total Return"));
        result.Warnings.Should().Contain(w => w.Contains("Margin"));
        result.Assumptions.Should().Contain(a => a.Contains("Kapitalbasis"));
    }

    [Fact]
    public void Comparison_uses_only_overlapping_periods()
    {
        var strat = Series("S", new[] { 0.01, 0.02, 0.03, 0.04, 0.05 });
        // Benchmark beginnt später → nur die gemeinsamen Tage zählen.
        var obs = new List<BenchmarkObservation>
        {
            new(T0.AddDays(2), 100), new(T0.AddDays(3), 101), new(T0.AddDays(4), 102), new(T0.AddDays(9), 103)
        };
        var bm = new BenchmarkSeries { Id = "bm", Name = "bm", Observations = obs, IsTotalReturn = true, Provenance = "Test" };

        var result = BenchmarkComparer.Compare(strat, bm, new BenchmarkComparisonOptions { StrategyIsFullyFunded = true });

        result.CommonPeriods.Should().Be(2);   // Tage 3 und 4
        result.AlignedStrategy.Timestamps.Should().Equal(T0.AddDays(3), T0.AddDays(4));
    }
}
