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
/// Sicherheit, keine automatische Freigabe bei Fehler, vollständige Bindung an den gespeicherten
/// Kandidaten-Snapshot (Ablehnung bei Abweichung), kein Look-ahead und Trennung von der Optimierung.
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

    private QuantApiService ServiceWithConfig(string registry, string configRoot) => new(new BacktestApiService(configRoot), registry);

    /// <summary>
    /// Baut eine temporäre Repo-Wurzel mit kopierten config/instruments + config/fees, wobei die MES-
    /// Default-SL/TP frei gewählt werden. Damit lässt sich ein GEÄNDERTER Profildefault zwischen Training
    /// und Holdout real nachstellen (nur diese beiden Zahlen unterscheiden sich).
    /// </summary>
    private string BuildConfigRoot(int stopLossTicks, int takeProfitTicks)
    {
        var root = Path.Combine(Path.GetTempPath(), "holdout-cfg-" + Guid.NewGuid().ToString("N"));
        _dirs.Add(root);
        var instr = Path.Combine(root, "config", "instruments");
        var fees = Path.Combine(root, "config", "fees");
        Directory.CreateDirectory(instr);
        Directory.CreateDirectory(fees);

        var realInstr = Path.Combine(RepoRoot(), "config", "instruments");
        foreach (var f in Directory.EnumerateFiles(realInstr, "*.json"))
            File.Copy(f, Path.Combine(instr, Path.GetFileName(f)));
        var realFees = Path.Combine(RepoRoot(), "config", "fees");
        if (Directory.Exists(realFees))
            foreach (var f in Directory.EnumerateFiles(realFees, "*.json"))
                File.Copy(f, Path.Combine(fees, Path.GetFileName(f)));

        var mes = Path.Combine(instr, "mes.example.json");
        var json = File.ReadAllText(mes);
        json = System.Text.RegularExpressions.Regex.Replace(json, "\"defaultStopLossTicks\"\\s*:\\s*\\d+", $"\"defaultStopLossTicks\": {stopLossTicks}");
        json = System.Text.RegularExpressions.Regex.Replace(json, "\"defaultTakeProfitTicks\"\\s*:\\s*\\d+", $"\"defaultTakeProfitTicks\": {takeProfitTicks}");
        File.WriteAllText(mes, json);
        return root;
    }

    private (QuantApiService svc, string registry) NewService()
    {
        var registry = Path.Combine(Path.GetTempPath(), "holdout-tests-" + Guid.NewGuid().ToString("N"));
        _dirs.Add(registry);
        return (ServiceOn(registry), registry);
    }

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

    /// <summary>Identische Run-Konfiguration für Walk-forward UND Holdout (sonst greift die Snapshot-Bindung).</summary>
    private static BacktestRunRequest BaseRun(string csv) => new()
    {
        DataSourceId = "csv", Path = csv, Symbol = "MES", TimeframeMinutes = Tf,
        Strategy = "movingaverage", Params = CandidateParams(),
        Quantity = 1, InitialBalance = 10_000m, ApplyFees = false, ExcludePartialEdges = true
    };

    /// <summary>Legt eine echte Kampagne inkl. reserviertem Holdout und Kandidaten-Trials über einen Walk-forward an.</summary>
    private async Task<(string campaignId, BacktestRunRequest holdoutRun, string trialId)> SeedViaWalkForward(QuantApiService svc, string csv)
    {
        string cid = "wf-" + Guid.NewGuid().ToString("N")[..8];
        var wf = await svc.WalkForwardAsync(new QuantWalkForwardRequest
        {
            Run = BaseRun(csv),
            Options = new QuantEvaluationOptions { Frequency = "Bar" },
            Mode = "Rolling", TrainBars = 60, TestBars = 30, HoldoutFraction = 0.2, WarmupBars = 10,
            Candidates = new[] { CandidateParams(), new Dictionary<string, string> { ["FastPeriod"] = "5", ["SlowPeriod"] = "34" } },
            SelectionMetric = "sharpe",
            Campaign = new CampaignInput { Id = cid, Name = "wf", Hypothesis = "h", SearchSpace = "Fast, Slow", SelectionMetric = "sharpe", TrialBudget = 8, HoldoutFraction = 0.2 }
        });
        wf.Ok.Should().BeTrue(wf.Error);

        var trials = await svc.Store.ListTrialsAsync(cid);
        var trial = trials.First(t => t.Parameters.TryGetValue("FastPeriod", out var v) && v == "9");
        return (cid, BaseRun(csv) with { Params = new Dictionary<string, string>(trial.Parameters) }, trial.Id);
    }

    /// <summary>Leichte Kampagne (nur Store) für Ablehnungspfade, die den Kandidaten gar nicht erreichen.</summary>
    private async Task<(string campaignId, BacktestRunRequest run)> SeedCampaign(QuantApiService svc, string csv, int bars, int holdoutCount, string? dataSha = null)
    {
        int usable = bars - holdoutCount;
        string cid = "camp-" + Guid.NewGuid().ToString("N")[..8];
        await svc.Store.CreateCampaignAsync(new CampaignRecord
        {
            Id = cid, Name = "Test", Hypothesis = "Test", SearchSpace = "Fast, Slow", SelectionMetric = "sharpe",
            TrialBudget = 4, DataSha = dataSha, HoldoutFrom = T0.AddMinutes(Tf * (usable + 1)), HoldoutTo = T0.AddMinutes(Tf * bars)
        });
        return (cid, BaseRun(csv));
    }

    private static HoldoutEvaluateRequest EvalReq(BacktestRunRequest run, string? trialId = null, string candidateRef = "Kandidat A", int warmup = 10, bool confirm = true)
        => new() { Run = run, Options = new QuantEvaluationOptions { Frequency = "Bar" }, CandidateReference = candidateRef, CandidateTrialId = trialId, WarmupBars = warmup, Confirm = confirm };

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
        var (cid, run, trialId) = await SeedViaWalkForward(svc, csv);

        var start = await svc.EvaluateHoldoutAsync(cid, EvalReq(run, trialId), jobs);
        start.Ok.Should().BeTrue(start.Error);
        start.State.Should().Be("Reserved");
        start.JobId.Should().NotBeNullOrEmpty();

        var rec = await WaitTerminal(svc, cid, TimeSpan.FromSeconds(30));
        rec.Status.Should().Be(HoldoutEvaluationStatus.Completed);

        rec.Equity.Should().NotBeEmpty();
        rec.Equity.First().TimeMs.Should().Be(rec.Config.HoldoutFrom.ToUnixTimeMilliseconds());
        rec.Config.CandidateTrialId.Should().Be(trialId);

        // Kein Look-ahead: kein Trade beginnt vor dem OPEN der ersten Holdout-Kerze.
        long earliestEntry = rec.Config.HoldoutFrom.AddMinutes(-Tf).ToUnixTimeMilliseconds();
        rec.Trades.Should().OnlyContain(t => t.EntryTimeMs >= earliestEntry);

        // Unabhängige Gegenrechnung.
        rec.NetProfit.Should().BeApproximately(rec.Trades.Sum(t => t.NetPnL), 1e-6);
        rec.FinalEquity.Should().BeApproximately(10_000 + (rec.NetProfit ?? 0), 1e-6);
        rec.MaxDrawdown.Should().BeGreaterThanOrEqualTo(0);

        // Ausführungsoptionen persistiert (nicht nur Frequency).
        rec.Config.Frequency.Should().Be("Bar");
        rec.Config.MinimumPeriods.Should().Be(new QuantEvaluationOptions().MinimumPeriods);
        rec.UsedDataSha.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Two_parallel_starts_run_exactly_once()
    {
        var (svc, _) = NewService();
        var jobs = new QuantJobManager();
        var csv = WriteCsv(260);
        var (cid, run, trialId) = await SeedViaWalkForward(svc, csv);

        var a = Task.Run(() => svc.EvaluateHoldoutAsync(cid, EvalReq(run, trialId), jobs));
        var b = Task.Run(() => svc.EvaluateHoldoutAsync(cid, EvalReq(run, trialId), jobs));
        var results = await Task.WhenAll(a, b);

        results.Should().OnlyContain(r => r.Ok);
        results.Count(r => !r.AlreadyExisted).Should().Be(1);
        results.Count(r => r.AlreadyExisted).Should().Be(1);

        (await WaitTerminal(svc, cid, TimeSpan.FromSeconds(30))).Status.Should().Be(HoldoutEvaluationStatus.Completed);
    }

    [Fact]
    public async Task Repeated_request_and_reload_after_restart_do_not_recompute()
    {
        var (svc, registry) = NewService();
        var csv = WriteCsv(260);
        var (cid, run, trialId) = await SeedViaWalkForward(svc, csv);

        await svc.EvaluateHoldoutAsync(cid, EvalReq(run, trialId), new QuantJobManager());
        var first = await WaitTerminal(svc, cid, TimeSpan.FromSeconds(30));
        first.Status.Should().Be(HoldoutEvaluationStatus.Completed);

        var svc2 = ServiceOn(registry);   // „Neustart"
        var reloaded = (await svc2.GetHoldoutAsync(cid)).Evaluation;
        reloaded!.Status.Should().Be(HoldoutEvaluationStatus.Completed);
        reloaded.CompletedUtc.Should().Be(first.CompletedUtc);       // nicht neu gerechnet
        reloaded.NetProfit.Should().Be(first.NetProfit);

        var again = await svc2.EvaluateHoldoutAsync(cid, EvalReq(run, trialId), new QuantJobManager());
        again.AlreadyExisted.Should().BeTrue();
        again.JobId.Should().BeNull();
        again.Evaluation!.CompletedUtc.Should().Be(first.CompletedUtc);
    }

    [Fact]
    public async Task A_changed_trading_config_or_costs_is_rejected_without_consuming()
    {
        var (svc, _) = NewService();
        var csv = WriteCsv(260);
        var (cid, run, trialId) = await SeedViaWalkForward(svc, csv);

        // Geänderte Handelskonfiguration (Menge) → Ablehnung, kein Verbrauch.
        var changedQty = await svc.EvaluateHoldoutAsync(cid, EvalReq(run with { Quantity = 2 }, trialId), new QuantJobManager());
        changedQty.Ok.Should().BeFalse();
        changedQty.Error.Should().Contain("CANDIDATE_CONFIG_MISMATCH");
        changedQty.Error.Should().Contain("Menge");

        // Geänderte Kostenbehandlung (Gebühren-Flag) → Ablehnung.
        var changedFees = await svc.EvaluateHoldoutAsync(cid, EvalReq(run with { ApplyFees = true }, trialId), new QuantJobManager());
        changedFees.Ok.Should().BeFalse();
        changedFees.Error.Should().Contain("CANDIDATE_CONFIG_MISMATCH");

        // Geänderte Strategie → Ablehnung.
        var changedStrat = await svc.EvaluateHoldoutAsync(cid, EvalReq(run with { Strategy = "sma-other" }, trialId), new QuantJobManager());
        changedStrat.Ok.Should().BeFalse();
        changedStrat.Error.Should().Contain("CANDIDATE_CONFIG_MISMATCH");

        // Nichts davon hat den Holdout verbraucht oder eine Auswertung angelegt.
        (await svc.Store.GetHoldoutEvaluationAsync(cid)).Should().BeNull();
        (await svc.Store.GetCampaignAsync(cid))!.HoldoutConsumed.Should().BeFalse();

        // Gegenprobe: unveränderter Kandidat → erfolgreicher Lauf.
        var ok = await svc.EvaluateHoldoutAsync(cid, EvalReq(run, trialId), new QuantJobManager());
        ok.Ok.Should().BeTrue(ok.Error);
        (await WaitTerminal(svc, cid, TimeSpan.FromSeconds(30))).Status.Should().Be(HoldoutEvaluationStatus.Completed);
    }

    [Fact]
    public async Task A_failed_evaluation_after_reservation_stays_durable_and_is_not_auto_released()
    {
        var (svc, _) = NewService();
        var csv = WriteCsv(200);
        var (cid, run) = await SeedCampaign(svc, csv, bars: 200, holdoutCount: 40);

        var frozen = new HoldoutFrozenConfig
        {
            CampaignId = cid, CandidateReference = "Kandidat A", StrategyId = "movingaverage",
            Parameters = CandidateParams(), Symbol = "MES", TimeframeMinutes = Tf, InitialCapital = 10_000m,
            Quantity = 1, DataSha = "x", HoldoutFrom = T0.AddMinutes(Tf * 161), HoldoutTo = T0.AddMinutes(Tf * 200), CodeVersion = "test"
        };
        var reserved = await svc.Store.ReserveHoldoutEvaluationAsync(cid, new HoldoutEvaluationRecord
        {
            CampaignId = cid, RunId = "run-x", Status = HoldoutEvaluationStatus.Reserved, Config = frozen
        });
        await svc.Store.UpdateHoldoutEvaluationAsync(reserved with { Status = HoldoutEvaluationStatus.Failed, StatusReason = "Simulierter Fehler" });

        var resp = await svc.EvaluateHoldoutAsync(cid, EvalReq(run, "irrelevant"), new QuantJobManager());
        resp.AlreadyExisted.Should().BeTrue();
        resp.State.Should().Be("Failed");
        resp.JobId.Should().BeNull();
        (await svc.Store.GetCampaignAsync(cid))!.HoldoutConsumed.Should().BeTrue();
        (await svc.GetHoldoutAsync(cid)).Evaluation!.StatusReason.Should().Contain("Simulierter Fehler");
    }

    [Fact]
    public async Task A_deviating_data_fingerprint_is_rejected_without_reserving()
    {
        var (svc, _) = NewService();
        var csv = WriteCsv(200);
        var (cid, run) = await SeedCampaign(svc, csv, bars: 200, holdoutCount: 40, dataSha: "deadbeefdeadbeef");

        var resp = await svc.EvaluateHoldoutAsync(cid, EvalReq(run, "any"), new QuantJobManager());

        resp.Ok.Should().BeFalse();
        resp.Error.Should().Contain("DATA_MISMATCH");
        (await svc.Store.GetHoldoutEvaluationAsync(cid)).Should().BeNull();
        (await svc.Store.GetCampaignAsync(cid))!.HoldoutConsumed.Should().BeFalse();
    }

    [Fact]
    public async Task A_missing_or_unknown_candidate_trial_is_rejected()
    {
        var (svc, _) = NewService();
        var csv = WriteCsv(200);
        var (cid, run) = await SeedCampaign(svc, csv, bars: 200, holdoutCount: 40);

        var missing = await svc.EvaluateHoldoutAsync(cid, EvalReq(run, trialId: null), new QuantJobManager());
        missing.Ok.Should().BeFalse();
        missing.Error.Should().Contain("CANDIDATE_TRIAL_REQUIRED");

        var bogus = await svc.EvaluateHoldoutAsync(cid, EvalReq(run, trialId: "does-not-exist"), new QuantJobManager());
        bogus.Ok.Should().BeFalse();
        bogus.Error.Should().Contain("CANDIDATE_NOT_IN_CAMPAIGN");

        (await svc.Store.GetCampaignAsync(cid))!.HoldoutConsumed.Should().BeFalse();
    }

    [Fact]
    public async Task Confirmation_is_required_before_consuming_the_holdout()
    {
        var (svc, _) = NewService();
        var csv = WriteCsv(200);
        var (cid, run) = await SeedCampaign(svc, csv, bars: 200, holdoutCount: 40);

        var resp = await svc.EvaluateHoldoutAsync(cid, EvalReq(run, "any", confirm: false), new QuantJobManager());

        resp.Ok.Should().BeFalse();
        resp.Error.Should().Contain("CONFIRM_REQUIRED");
        (await svc.Store.GetCampaignAsync(cid))!.HoldoutConsumed.Should().BeFalse();
    }

    [Fact]
    public async Task Holdout_result_never_enters_the_trial_register_for_optimization_feedback()
    {
        var (svc, _) = NewService();
        var csv = WriteCsv(260);
        var (cid, run, trialId) = await SeedViaWalkForward(svc, csv);

        int trialsBefore = (await svc.Store.ListTrialsAsync(cid)).Count;

        await svc.EvaluateHoldoutAsync(cid, EvalReq(run, trialId), new QuantJobManager());
        await WaitTerminal(svc, cid, TimeSpan.FromSeconds(30));

        (await svc.Store.ListTrialsAsync(cid)).Count.Should().Be(trialsBefore);   // kein neues (Holdout-)Trial
        (await svc.Store.GetHoldoutEvaluationAsync(cid)).Should().NotBeNull();
    }

    [Fact]
    public async Task Snapshot_freezes_effective_sl_tp_so_a_later_profile_default_change_cannot_silently_alter_the_holdout()
    {
        var csv = WriteCsv(260);
        var registry = Path.Combine(Path.GetTempPath(), "holdout-tests-" + Guid.NewGuid().ToString("N"));
        _dirs.Add(registry);

        // Training mit null-SL/TP → Profildefaults greifen (MES: SL 40, TP 60).
        var svcTrain = ServiceWithConfig(registry, BuildConfigRoot(stopLossTicks: 40, takeProfitTicks: 60));
        var (cid, run, trialId) = await SeedViaWalkForward(svcTrain, csv);

        // Der Request selbst trägt KEIN explizites SL/TP …
        run.StopLossTicks.Should().BeNull();
        run.TakeProfitTicks.Should().BeNull();
        // … aber der Snapshot hält die EFFEKTIVEN (aufgelösten) Werte, nicht null.
        var trial = (await svcTrain.Store.ListTrialsAsync(cid)).First(t => t.Id == trialId);
        trial.Execution.Should().NotBeNull();
        trial.Execution!.StopLossTicks.Should().Be(40);
        trial.Execution.TakeProfitTicks.Should().Be(60);

        // Profildefault WIRD GEÄNDERT (SL 40 → 80). Derselbe null-Request löste jetzt effektiv 80 auf ≠ 40 →
        // Ablehnung VOR dem Verbrauch, statt die Holdout-Ausführung still zu verändern.
        var svcChanged = ServiceWithConfig(registry, BuildConfigRoot(stopLossTicks: 80, takeProfitTicks: 60));
        var rejected = await svcChanged.EvaluateHoldoutAsync(cid, EvalReq(run, trialId), new QuantJobManager());
        rejected.Ok.Should().BeFalse();
        rejected.Error.Should().Contain("CANDIDATE_CONFIG_MISMATCH");
        rejected.Error.Should().Contain("Stop-Loss");
        (await svcChanged.Store.GetHoldoutEvaluationAsync(cid)).Should().BeNull();
        (await svcChanged.Store.GetCampaignAsync(cid))!.HoldoutConsumed.Should().BeFalse();

        // Unveränderte Profildefaults → gleiche effektive Auflösung → Lauf nutzt die gespeicherten effektiven Werte.
        var svcSame = ServiceWithConfig(registry, BuildConfigRoot(stopLossTicks: 40, takeProfitTicks: 60));
        var ok = await svcSame.EvaluateHoldoutAsync(cid, EvalReq(run, trialId), new QuantJobManager());
        ok.Ok.Should().BeTrue(ok.Error);
        var rec = await WaitTerminal(svcSame, cid, TimeSpan.FromSeconds(30));
        rec.Status.Should().Be(HoldoutEvaluationStatus.Completed);
        rec.Config.StopLossTicks.Should().Be(40);      // eingefrorene effektive Werte
        rec.Config.TakeProfitTicks.Should().Be(60);
    }
}
