using System.Diagnostics;
using System.Globalization;
using System.Text;
using FluentAssertions;
using TradingBot.DevDashboard.Services;
using TradingBot.DevDashboard.Services.Quant;
using TradingBot.Quant.Registry;
using Xunit;

namespace TradingBot.Tests.DevDashboard;

/// <summary>
/// Nachprüfung des finalen Holdout-Auswertungspfads: tatsächlicher Servicepfad mit deterministischen
/// synthetischen OHLC-Daten bis zum dauerhaft gespeicherten Ergebnis, einmalige Ausführung, Neustart-
/// Sicherheit, keine automatische Freigabe bei Fehler, Ablehnung abweichender Angaben, kein Look-ahead
/// und Trennung von der Optimierungsrückkopplung.
/// </summary>
public class HoldoutEvaluationTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 2, 14, 0, 0, TimeSpan.Zero);   // Montag
    private const int Tf = 5;
    private readonly List<string> _dirs = new();
    private readonly List<string> _files = new();

    public void Dispose()
    {
        foreach (var d in _dirs) try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { /* egal */ }
        foreach (var f in _files) try { if (File.Exists(f)) File.Delete(f); } catch { /* egal */ }
        GC.SuppressFinalize(this);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradingBot.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo-Wurzel (TradingBot.sln) nicht gefunden.");
    }

    private QuantApiService ServiceOn(string registry) => new(new BacktestApiService(RepoRoot()), registry);

    private (QuantApiService svc, string registry) NewService()
    {
        var registry = Path.Combine(Path.GetTempPath(), "holdout-tests-" + Guid.NewGuid().ToString("N"));
        _dirs.Add(registry);
        return (ServiceOn(registry), registry);
    }

    /// <summary>Deterministische, OHLC-konsistente Sinus-Kerzen — genug Schwankung für SMA-Crossovers.</summary>
    private string WriteCsv(int bars)
    {
        var path = Path.Combine(Path.GetTempPath(), "holdout-ohlc-" + Guid.NewGuid().ToString("N") + ".csv");
        _files.Add(path);
        var sb = new StringBuilder("timestamp,open,high,low,close,volume\n");
        double Price(int i) => 5000 + 40 * Math.Sin(2 * Math.PI * i / 40.0);
        for (int i = 0; i < bars; i++)
        {
            var t = T0.AddMinutes(Tf * i).UtcDateTime;
            double o = Price(i - 1), c = Price(i), h = Math.Max(o, c) + 1, l = Math.Min(o, c) - 1;
            sb.Append(string.Format(CultureInfo.InvariantCulture,
                "{0:yyyy-MM-ddTHH:mm:ss}Z,{1:F2},{2:F2},{3:F2},{4:F2},100\n", t, o, h, l, c));
        }
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    private static Dictionary<string, string> CandidateParams() => new() { ["FastPeriod"] = "9", ["SlowPeriod"] = "21" };

    private async Task<(string campaignId, BacktestRunRequest run, DateTimeOffset hFrom, DateTimeOffset hTo)> SeedCampaign(
        QuantApiService svc, string csv, int bars, int holdoutCount,
        string? dataSha = null, bool addCandidateTrial = true, Dictionary<string, string>? candidateParams = null)
    {
        var pars = candidateParams ?? CandidateParams();
        int usable = bars - holdoutCount;
        var hFrom = T0.AddMinutes(Tf * (usable + 1));   // Close der ersten Holdout-Kerze
        var hTo = T0.AddMinutes(Tf * bars);             // Close der letzten Kerze
        string cid = "camp-" + Guid.NewGuid().ToString("N")[..8];

        await svc.Store.CreateCampaignAsync(new CampaignRecord
        {
            Id = cid, Name = "Test", Hypothesis = "Test ohne Edge-Behauptung.",
            SearchSpace = "FastPeriod, SlowPeriod", SelectionMetric = "sharpe", TrialBudget = 4,
            DataSha = dataSha, HoldoutFrom = hFrom, HoldoutTo = hTo
        });

        if (addCandidateTrial)
            await svc.Store.AddTrialAsync(new TrialRecord
            {
                Id = "wf-" + Guid.NewGuid().ToString("N")[..8], CampaignId = cid,
                StrategyId = "movingaverage", StrategyVersion = "test", CodeVersion = "test",
                Parameters = new Dictionary<string, string>(pars), PeriodRole = "walkforward",
                PeriodFrom = T0, PeriodTo = hFrom,     // endet am Holdout-Beginn → keine Überschneidung
                Data = new DataFingerprint { Symbol = "MES", TimeframeMinutes = Tf, Source = "test", Sha256 = "x", BarCount = usable }
            });

        var run = new BacktestRunRequest
        {
            DataSourceId = "csv", Path = csv, Symbol = "MES", TimeframeMinutes = Tf,
            Strategy = "movingaverage", Params = new Dictionary<string, string>(pars),
            Quantity = 1, InitialBalance = 10_000m, ApplyFees = false, SlippageTicks = 0m,
            FeePerSideOverride = 0m, ExcludePartialEdges = true
        };
        return (cid, run, hFrom, hTo);
    }

    private static HoldoutEvaluateRequest EvalReq(BacktestRunRequest run, string candidateRef = "Kandidat A", int warmup = 21, bool confirm = true)
        => new() { Run = run, Options = new QuantEvaluationOptions { Frequency = "Bar" }, CandidateReference = candidateRef, WarmupBars = warmup, Confirm = confirm };

    private static async Task<HoldoutEvaluationRecord> WaitTerminal(QuantApiService svc, string cid, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var r = (await svc.GetHoldoutAsync(cid)).Evaluation;
            if (r is { Status: HoldoutEvaluationStatus.Completed or HoldoutEvaluationStatus.Failed or HoldoutEvaluationStatus.Cancelled })
                return r;
            await Task.Delay(50);
        }
        throw new TimeoutException("Holdout-Auswertung wurde nicht rechtzeitig terminal.");
    }

    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Full_service_path_evaluates_the_holdout_and_persists_a_result()
    {
        var (svc, _) = NewService();
        var jobs = new QuantJobManager();
        var csv = WriteCsv(260);
        var (cid, run, hFrom, hTo) = await SeedCampaign(svc, csv, bars: 260, holdoutCount: 60);

        var start = await svc.EvaluateHoldoutAsync(cid, EvalReq(run), jobs);
        start.Ok.Should().BeTrue();
        start.State.Should().Be("Reserved");
        start.JobId.Should().NotBeNullOrEmpty();

        var rec = await WaitTerminal(svc, cid, TimeSpan.FromSeconds(30));
        rec.Status.Should().Be(HoldoutEvaluationStatus.Completed);

        // Nur der Holdout-Zeitraum wird bewertet (kein Warmup in den Kennzahlen).
        rec.Equity.Should().NotBeEmpty();
        rec.Equity.Count.Should().Be(60);
        rec.Equity.First().TimeMs.Should().Be(hFrom.ToUnixTimeMilliseconds());
        rec.Equity.Last().TimeMs.Should().BeLessThanOrEqualTo(hTo.ToUnixTimeMilliseconds());
        rec.WarmupBarsUsed.Should().Be(21);
        rec.HoldoutBars.Should().Be(60);

        // Kein Look-ahead: kein Trade beginnt vor dem OPEN der ersten Holdout-Kerze (= hFrom − 1 Bar).
        long earliestEntry = hFrom.AddMinutes(-Tf).ToUnixTimeMilliseconds();
        rec.Trades.Should().OnlyContain(t => t.EntryTimeMs >= earliestEntry);

        // Unabhängige Gegenrechnung: NetProfit == Σ Trade-NetPnL; FinalEquity == Start + NetProfit.
        rec.NetProfit.Should().BeApproximately(rec.Trades.Sum(t => t.NetPnL), 1e-6);
        rec.FinalEquity.Should().BeApproximately(10_000 + (rec.NetProfit ?? 0), 1e-6);
        rec.MaxDrawdown.Should().BeGreaterThanOrEqualTo(0);

        // Eingefrorene Konfiguration und Datenbezug festgehalten.
        rec.Config.HoldoutFrom.Should().Be(hFrom);
        rec.Config.Parameters["FastPeriod"].Should().Be("9");
        rec.Config.CandidateReference.Should().Be("Kandidat A");
        rec.UsedDataSha.Should().NotBeNullOrEmpty();
        rec.Metrics.Should().ContainKey("netprofit");
    }

    [Fact]
    public async Task Two_parallel_starts_run_exactly_once()
    {
        var (svc, _) = NewService();
        var jobs = new QuantJobManager();
        var csv = WriteCsv(220);
        var (cid, run, _, _) = await SeedCampaign(svc, csv, bars: 220, holdoutCount: 50);

        var a = Task.Run(() => svc.EvaluateHoldoutAsync(cid, EvalReq(run), jobs));
        var b = Task.Run(() => svc.EvaluateHoldoutAsync(cid, EvalReq(run), jobs));
        var results = await Task.WhenAll(a, b);

        results.Should().OnlyContain(r => r.Ok);
        results.Count(r => !r.AlreadyExisted).Should().Be(1);   // genau ein frisch gestarteter Lauf
        results.Count(r => r.AlreadyExisted).Should().Be(1);

        var rec = await WaitTerminal(svc, cid, TimeSpan.FromSeconds(30));
        rec.Status.Should().Be(HoldoutEvaluationStatus.Completed);
    }

    [Fact]
    public async Task Repeated_request_and_reload_after_restart_do_not_recompute()
    {
        var (svc, registry) = NewService();
        var csv = WriteCsv(240);
        var (cid, run, _, _) = await SeedCampaign(svc, csv, bars: 240, holdoutCount: 55);

        await svc.EvaluateHoldoutAsync(cid, EvalReq(run), new QuantJobManager());
        var first = await WaitTerminal(svc, cid, TimeSpan.FromSeconds(30));
        first.Status.Should().Be(HoldoutEvaluationStatus.Completed);

        // „Neustart": frischer Service auf demselben Register-Verzeichnis.
        var svc2 = ServiceOn(registry);
        var reloaded = (await svc2.GetHoldoutAsync(cid)).Evaluation;
        reloaded.Should().NotBeNull();
        reloaded!.Status.Should().Be(HoldoutEvaluationStatus.Completed);
        reloaded.CompletedUtc.Should().Be(first.CompletedUtc);        // identisch → nicht neu gerechnet
        reloaded.NetProfit.Should().Be(first.NetProfit);
        reloaded.Trades.Count.Should().Be(first.Trades.Count);

        // Erneuter Request nach „Neustart": kein neuer Lauf, gleiches Ergebnis.
        var again = await svc2.EvaluateHoldoutAsync(cid, EvalReq(run), new QuantJobManager());
        again.AlreadyExisted.Should().BeTrue();
        again.JobId.Should().BeNull();
        again.Evaluation!.CompletedUtc.Should().Be(first.CompletedUtc);
    }

    [Fact]
    public async Task A_failed_evaluation_after_reservation_stays_durable_and_is_not_auto_released()
    {
        var (svc, _) = NewService();
        var csv = WriteCsv(200);
        var (cid, run, hFrom, hTo) = await SeedCampaign(svc, csv, bars: 200, holdoutCount: 40);

        // Reservierung + simulierter Fehlschlag direkt im Store (deterministisch, ohne echten Engine-Fehler).
        var frozen = new HoldoutFrozenConfig
        {
            CampaignId = cid, CandidateReference = "Kandidat A", StrategyId = "movingaverage",
            Parameters = CandidateParams(), Symbol = "MES", TimeframeMinutes = Tf, InitialCapital = 10_000m,
            Quantity = 1, DataSha = "x", HoldoutFrom = hFrom, HoldoutTo = hTo, CodeVersion = "test"
        };
        var reserved = await svc.Store.ReserveHoldoutEvaluationAsync(cid, new HoldoutEvaluationRecord
        {
            CampaignId = cid, RunId = "run-x", Status = HoldoutEvaluationStatus.Reserved, Config = frozen
        });
        await svc.Store.UpdateHoldoutEvaluationAsync(reserved with { Status = HoldoutEvaluationStatus.Failed, StatusReason = "Simulierter Fehler" });

        // Keine automatische Freigabe: erneuter Start liefert den Failed-Zustand, KEIN neuer Lauf.
        var resp = await svc.EvaluateHoldoutAsync(cid, EvalReq(run), new QuantJobManager());
        resp.AlreadyExisted.Should().BeTrue();
        resp.State.Should().Be("Failed");
        resp.JobId.Should().BeNull();
        (await svc.Store.GetCampaignAsync(cid))!.HoldoutConsumed.Should().BeTrue();   // bleibt verbraucht

        var status = await svc.GetHoldoutAsync(cid);
        status.State.Should().Be("Failed");
        status.Evaluation!.StatusReason.Should().Contain("Simulierter Fehler");
    }

    [Fact]
    public async Task A_deviating_data_fingerprint_is_rejected_without_reserving()
    {
        var (svc, _) = NewService();
        var csv = WriteCsv(200);
        var (cid, run, _, _) = await SeedCampaign(svc, csv, bars: 200, holdoutCount: 40, dataSha: "deadbeefdeadbeef");

        var resp = await svc.EvaluateHoldoutAsync(cid, EvalReq(run), new QuantJobManager());

        resp.Ok.Should().BeFalse();
        resp.Error.Should().Contain("DATA_MISMATCH");
        (await svc.Store.GetHoldoutEvaluationAsync(cid)).Should().BeNull();          // nichts reserviert
        (await svc.Store.GetCampaignAsync(cid))!.HoldoutConsumed.Should().BeFalse();  // nicht verbraucht
    }

    [Fact]
    public async Task A_candidate_that_is_not_part_of_the_campaign_is_rejected()
    {
        var (svc, _) = NewService();
        var csv = WriteCsv(200);
        // Kampagne OHNE erfassten Kandidaten-Trial: der Parametersatz hat keinen Bezug zur Suche.
        var (cid, run, _, _) = await SeedCampaign(svc, csv, bars: 200, holdoutCount: 40, addCandidateTrial: false);

        var resp = await svc.EvaluateHoldoutAsync(cid, EvalReq(run), new QuantJobManager());

        resp.Ok.Should().BeFalse();
        resp.Error.Should().Contain("CANDIDATE_NOT_IN_CAMPAIGN");
        (await svc.Store.GetCampaignAsync(cid))!.HoldoutConsumed.Should().BeFalse();
    }

    [Fact]
    public async Task Confirmation_is_required_before_consuming_the_holdout()
    {
        var (svc, _) = NewService();
        var csv = WriteCsv(200);
        var (cid, run, _, _) = await SeedCampaign(svc, csv, bars: 200, holdoutCount: 40);

        var resp = await svc.EvaluateHoldoutAsync(cid, EvalReq(run, confirm: false), new QuantJobManager());

        resp.Ok.Should().BeFalse();
        resp.Error.Should().Contain("CONFIRM_REQUIRED");
        (await svc.Store.GetCampaignAsync(cid))!.HoldoutConsumed.Should().BeFalse();
    }

    [Fact]
    public async Task Holdout_result_never_enters_the_trial_register_for_optimization_feedback()
    {
        var (svc, _) = NewService();
        var csv = WriteCsv(240);
        var (cid, run, _, _) = await SeedCampaign(svc, csv, bars: 240, holdoutCount: 55);

        int trialsBefore = (await svc.Store.ListTrialsAsync(cid)).Count;   // der eine Walk-forward-Kandidat

        await svc.EvaluateHoldoutAsync(cid, EvalReq(run), new QuantJobManager());
        await WaitTerminal(svc, cid, TimeSpan.FromSeconds(30));

        // Die Holdout-Auswertung liegt in EINEM eigenen Satz, NICHT als Trial — sie kann nicht in die
        // PBO-Kandidatenmatrix oder die DSR-Versuchsgrundlage (ListTrials) gelangen.
        (await svc.Store.ListTrialsAsync(cid)).Count.Should().Be(trialsBefore);
        (await svc.Store.GetHoldoutEvaluationAsync(cid)).Should().NotBeNull();
    }
}
