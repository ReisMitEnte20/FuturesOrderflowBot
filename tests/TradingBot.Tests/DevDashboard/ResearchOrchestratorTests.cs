using System.Diagnostics;
using System.Globalization;
using System.Text;
using FluentAssertions;
using TradingBot.DevDashboard.Services;
using TradingBot.DevDashboard.Services.Quant;
using Xunit;

namespace TradingBot.Tests.DevDashboard;

/// <summary>Diese integrationsnahen Läufe (mehrere vollständige Backtests + Monte Carlo je Test) laufen NICHT
/// parallel zu den übrigen Tests — sonst kann Threadpool-Kontention einzelne Hintergrund-Jobs ausbremsen.</summary>
[CollectionDefinition("ResearchSerial", DisableParallelization = true)]
public class ResearchSerialCollection { }

/// <summary>
/// Nachprüfung der Research-Ablaufsteuerung: ein Start = genau ein Lauf, Walk-forward-Wiederverwendung (keine
/// doppelten Trials/kein doppeltes Budget), sichtbares Überspringen unabhängiger Schritte (fehlende Benchmark /
/// nicht berechenbare PBO), Reload ohne Neuberechnung, kein automatischer Holdout-Verbrauch, keine Vermischung
/// verschiedener Läufe und ehrliche Darstellung eines unterbrochenen Laufs.
/// </summary>
[Collection("ResearchSerial")]
public class ResearchOrchestratorTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 3, 2, 14, 0, 0, TimeSpan.Zero);
    private const int Tf = 5;
    private readonly List<string> _dirs = new();
    private readonly List<string> _files = new();

    public void Dispose()
    {
        foreach (var d in _dirs) try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
        foreach (var f in _files) try { if (File.Exists(f)) File.Delete(f); } catch { }
        GC.SuppressFinalize(this);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "TradingBot.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repo-Wurzel nicht gefunden.");
    }

    private (ResearchOrchestrator orch, QuantApiService quant) NewOrchestrator()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "research-tests-" + Guid.NewGuid().ToString("N"));
        _dirs.Add(tempRoot);
        var quant = new QuantApiService(new BacktestApiService(RepoRoot()), tempRoot);
        var store = new ResearchRunStore(quant.RegistryDir);
        return (new ResearchOrchestrator(quant, new QuantJobManager(), store), quant);
    }

    private string WriteCsv(int bars)
    {
        var path = Path.Combine(Path.GetTempPath(), "research-ohlc-" + Guid.NewGuid().ToString("N") + ".csv");
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

    private static BacktestRunRequest Run(string csv) => new()
    {
        DataSourceId = "csv", Path = csv, Symbol = "MES", TimeframeMinutes = Tf,
        Strategy = "movingaverage", Params = new() { ["FastPeriod"] = "9", ["SlowPeriod"] = "21" },
        Quantity = 1, InitialBalance = 10_000m, ApplyFees = false, ExcludePartialEdges = true
    };

    private static ResearchStartRequest StartReq(string csv, string campaign,
        IReadOnlyList<Dictionary<string, string>>? candidates = null, string? benchmarkId = null, bool demo = false)
        => new()
        {
            Run = Run(csv),
            Options = new QuantEvaluationOptions { Frequency = "Bar" },
            Campaign = new CampaignInput { Id = campaign, Name = campaign, Hypothesis = "h", SearchSpace = "Fast,Slow", SelectionMetric = "sharpe", HoldoutFraction = 0.2 },
            Candidates = candidates ?? new[]
            {
                new Dictionary<string, string> { ["FastPeriod"] = "9", ["SlowPeriod"] = "21" },
                new Dictionary<string, string> { ["FastPeriod"] = "6", ["SlowPeriod"] = "34" },
            },
            SelectionMetric = "sharpe", Mode = "Rolling", TrainBars = 200, TestBars = 80, WarmupBars = 10,
            HoldoutFraction = 0.2, MonteCarloSource = "trades", MonteCarloMethod = "Permutation",
            MonteCarloIterations = 200, Seed = 7, RobustnessMetric = "sharpe", OverfittingBlocks = 4,
            EstimateEffectiveTrials = true, BenchmarkId = benchmarkId, IsDemo = demo,
        };

    private static async Task<ResearchRunRecord> WaitTerminal(ResearchOrchestrator orch, string runId, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var res = await orch.GetAsync(runId);
            var r = res.Run!;
            if (r.Status is ResearchStepStatus.Completed or ResearchStepStatus.Failed or ResearchStepStatus.Cancelled)
                return r;
            await Task.Delay(60);
        }
        throw new TimeoutException("Research-Lauf wurde nicht rechtzeitig terminal.");
    }

    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_single_start_produces_exactly_one_completed_run()
    {
        var (orch, _) = NewOrchestrator();
        var csv = WriteCsv(600);

        var res = await orch.StartAsync(StartReq(csv, "single"));
        res.Ok.Should().BeTrue(res.Error);
        res.Run.Should().NotBeNull();

        var run = await WaitTerminal(orch, res.Run!.RunId, TimeSpan.FromSeconds(60));
        run.Status.Should().Be(ResearchStepStatus.Completed,
            "Reason={0}; steps={1}", run.StatusReason ?? "-", string.Join(",", run.Steps.Select(s => $"{s.Key}:{s.Status}:{s.Reason}")));
        run.Analysis!.Ok.Should().BeTrue();
        run.WalkForward!.Ok.Should().BeTrue();
        run.MonteCarlo!.Ok.Should().BeTrue();

        (await orch.ListAsync()).Should().HaveCount(1);   // genau ein Lauf
    }

    [Fact]
    public async Task Reloading_with_the_dev_load_bound_reproduces_exactly_the_development_bars_no_holdout()
    {
        // Befund A / Grenze: Erneutes Laden mit der Entwicklungs-Ladegrenze muss GENAU die Entwicklungsbars
        // [0..usable) ergeben — dieselben OpenTimes wie der In-Memory-Ausschnitt des vollen Datensatzes,
        // und KEINE Bar mit OpenTime >= Holdout-Start.
        var bt = new BacktestApiService(RepoRoot());
        var tempRoot = Path.Combine(Path.GetTempPath(), "research-tests-" + Guid.NewGuid().ToString("N"));
        _dirs.Add(tempRoot);
        var quant = new QuantApiService(bt, tempRoot);
        var csv = WriteCsv(500);
        var fullRun = Run(csv);

        var full = await bt.LoadContextAsync(fullRun);
        var split = await quant.ComputeHoldoutSplitAsync(fullRun, 0.2);

        split.TotalBars.Should().Be(full.Candles.Count);
        split.HoldoutBars.Should().Be((int)Math.Floor(full.Candles.Count * 0.2));
        split.UsableBars.Should().Be(full.Candles.Count - split.HoldoutBars);
        split.DevelopmentToUtc.Should().NotBeNull();
        split.DevelopmentLoadToUtc.Should().Be(split.DevelopmentToUtc!.Value.AddTicks(-1),
            "die Ladegrenze liegt genau einen Tick vor dem Holdout-Start");
        split.HoldoutFrom.Should().Be(split.DevelopmentToUtc);

        var devRun = fullRun with { ToUtc = split.DevelopmentLoadToUtc!.Value.ToString("o") };
        var dev = await bt.LoadContextAsync(devRun);

        dev.Candles.Should().HaveCount(split.UsableBars);
        dev.Candles.Select(c => c.OpenTime)
            .Should().Equal(full.Candles.Take(split.UsableBars).Select(c => c.OpenTime),
                "die Entwicklungs-Reihe ist bit-genau der vordere Ausschnitt des vollen Datensatzes");
        dev.Candles.Should().OnlyContain(c => c.OpenTime < split.HoldoutFrom!.Value,
            "keine Bar mit OpenTime >= Holdout-Start darf in die Vorprüfungen gelangen");
        // Eine Entwicklungsbar DARF am Holdout-Start schließen (contiguous Bars).
        dev.Candles[^1].CloseTime.Should().Be(split.HoldoutFrom!.Value);
    }

    private static ResearchCampaignStartRequest CampaignReq(string csv, string campaign, bool demo = false)
        => new()
        {
            Run = Run(csv),
            Options = new QuantEvaluationOptions { Frequency = "Bar" },
            Campaign = new CampaignInput { Id = campaign, Name = campaign, Hypothesis = "h", SearchSpace = "family", SelectionMetric = "sharpe", HoldoutFraction = 0.2 },
            Families = new[]
            {
                new ResearchFamilyInput
                {
                    Key = "movingaverage", StrategyId = "movingaverage", Name = "SMA-Crossover",
                    Candidates = new[]
                    {
                        new Dictionary<string, string> { ["FastPeriod"] = "9", ["SlowPeriod"] = "21" },
                        new Dictionary<string, string> { ["FastPeriod"] = "6", ["SlowPeriod"] = "34" },
                    }
                },
                new ResearchFamilyInput
                {
                    Key = "donchian", StrategyId = "donchian", Name = "Donchian-Ausbruch",
                    Candidates = new[]
                    {
                        new Dictionary<string, string> { ["Channel"] = "20" },
                        new Dictionary<string, string> { ["Channel"] = "30" },
                    }
                },
            },
            SelectionMetric = "sharpe", Mode = "Rolling", TrainBars = 200, TestBars = 80, WarmupBars = 10,
            HoldoutFraction = 0.2, MonteCarloSource = "returns", MonteCarloMethod = "MovingBlock",
            MonteCarloIterations = 150, Seed = 7, RobustnessMetric = "sharpe", OverfittingBlocks = 4,
            EstimateEffectiveTrials = true, IsDemo = demo,
        };

    [Fact]
    public async Task Two_families_run_without_mixing_and_produce_a_paired_comparison()
    {
        var (orch, quant) = NewOrchestrator();
        var csv = WriteCsv(600);

        var start = await orch.StartCampaignAsync(CampaignReq(csv, "vergleich"));
        start.Ok.Should().BeTrue(start.Error);
        start.GroupId.Should().NotBeNull();
        start.Families.Should().HaveCount(2);
        // Jede Familie hat ihre EIGENE Kampagne (kein geteiltes Budget/Holdout).
        start.Families.Select(f => f.CampaignId).Distinct().Should().HaveCount(2);
        start.Families.Select(f => f.Config.Run.Strategy).Should().BeEquivalentTo(new[] { "movingaverage", "donchian" });

        foreach (var f in start.Families)
            await WaitTerminal(orch, f.RunId, TimeSpan.FromSeconds(90));

        var camp = await orch.GetCampaignAsync(start.GroupId!);
        camp.Ok.Should().BeTrue();
        camp.Families.Should().HaveCount(2);
        camp.Families.Should().OnlyContain(r => r.Status == ResearchStepStatus.Completed,
            "beide Familien-Läufe sollten abgeschlossen sein");

        var cmp = camp.Comparison!;
        cmp.Available.Should().BeTrue(cmp.UnavailableReason);
        cmp.Families.Should().HaveCount(2);
        cmp.CommonObservations.Should().BeGreaterThan(0);
        cmp.Differences.Should().ContainSingle(); // genau ein Paar bei zwei Familien
        cmp.Differences[0].Left.Should().NotBe(cmp.Differences[0].Right);
        // Gepaarte MC-Kennzahlen sind für beide Familien vorhanden.
        cmp.Families.Should().OnlyContain(r => r.McMedianFinal != null);

        // Keine Vermischung: die Trials der einen Familie tauchen nicht unter der Kampagne der anderen auf.
        var maCampaign = start.Families.First(f => f.Config.Run.Strategy == "movingaverage").CampaignId!;
        var doCampaign = start.Families.First(f => f.Config.Run.Strategy == "donchian").CampaignId!;
        var maTrials = await quant.Store.ListTrialsAsync(maCampaign);
        var doTrials = await quant.Store.ListTrialsAsync(doCampaign);
        maTrials.Should().NotBeEmpty();
        doTrials.Should().NotBeEmpty();
        maTrials.Select(t => t.Id).Should().NotIntersectWith(doTrials.Select(t => t.Id));

        // Reload der Kampagne startet KEINE neuen Trials.
        int before = maTrials.Count + doTrials.Count;
        await orch.GetCampaignAsync(start.GroupId!);
        int after = (await quant.Store.ListTrialsAsync(maCampaign)).Count + (await quant.Store.ListTrialsAsync(doCampaign)).Count;
        after.Should().Be(before);

        // Kein finaler Holdout-Verbrauch: beide Familien haben nur einen VORBEREITETEN Holdout.
        camp.Families.Should().OnlyContain(r => r.HoldoutProposal == null || r.HoldoutProposal.Existing == null);
    }

    [Fact]
    public async Task Grouped_family_campaign_is_group_governed_standalone_is_not()
    {
        var (orch, _) = NewOrchestrator();
        var csv = WriteCsv(600);

        var start = await orch.StartCampaignAsync(CampaignReq(csv, "governed"));
        foreach (var f in start.Families) await WaitTerminal(orch, f.RunId, TimeSpan.FromSeconds(90));
        var standalone = await orch.StartAsync(StartReq(csv, "solo"));
        var soloRun = await WaitTerminal(orch, standalone.Run!.RunId, TimeSpan.FromSeconds(60));

        foreach (var f in start.Families)
            (await orch.IsGroupGovernedCampaignAsync(f.CampaignId!)).Should().BeTrue();
        (await orch.IsGroupGovernedCampaignAsync(soloRun.CampaignId!)).Should().BeFalse();
    }

    [Fact]
    public async Task Group_holdout_lock_binds_one_family_blocks_others_and_survives_restart()
    {
        var (orch, quant) = NewOrchestrator();
        var csv = WriteCsv(600);
        var start = await orch.StartCampaignAsync(CampaignReq(csv, "grouplock"));
        foreach (var f in start.Families) await WaitTerminal(orch, f.RunId, TimeSpan.FromSeconds(90));
        var groupId = start.GroupId!;
        var famA = start.Families.First(f => f.FamilyKey == "movingaverage");
        var famB = start.Families.First(f => f.FamilyKey == "donchian");

        // Gruppe fest an Familie A binden (Reservierung simulieren, ohne echten Holdout zu verbrauchen).
        var groups = new ResearchGroupStore(quant.RegistryDir);
        await groups.WithLockAsync<bool>(groupId, _ => Task.FromResult<(ResearchGroupHoldout?, bool)>((
            new ResearchGroupHoldout { GroupId = groupId, Reserved = true, SelectedFamilyKey = "movingaverage",
                SelectedCampaignId = famA.CampaignId, ReservedUtc = DateTimeOffset.UtcNow }, true)));

        var jobs = new QuantJobManager();
        HoldoutEvaluateRequest Req(ResearchRunRecord r) => new()
        {
            Run = r.Config.Run, Options = r.Config.Options, WarmupBars = 0, Confirm = false,
            CandidateReference = "x", CandidateTrialId = "t"
        };

        // Andere Familie (B) ist gesperrt — ein Holdout je Gruppe, kein Kandidatenwechsel.
        var blocked = await orch.EvaluateGroupHoldoutAsync(groupId, "donchian", Req(famB), jobs);
        blocked.Ok.Should().BeFalse();
        blocked.Error.Should().Contain("gesperrt");
        blocked.Holdout!.SelectedFamilyKey.Should().Be("movingaverage");

        // Dieselbe Familie (A) ist NICHT durch die Gruppensperre blockiert (scheitert allenfalls an der Bestätigung).
        var sameFamily = await orch.EvaluateGroupHoldoutAsync(groupId, "movingaverage", Req(famA), jobs);
        (sameFamily.Error ?? "").Should().NotContain("bereits für Familie");

        // Neustart: neuer Orchestrator auf DEMSELBEN Register — Reservierung bleibt, B bleibt gesperrt.
        var store2 = new ResearchRunStore(quant.RegistryDir);
        var orch2 = new ResearchOrchestrator(quant, new QuantJobManager(), store2);
        var camp = await orch2.GetCampaignAsync(groupId);
        camp.Holdout!.Reserved.Should().BeTrue();
        camp.Holdout.SelectedFamilyKey.Should().Be("movingaverage");
        var blocked2 = await orch2.EvaluateGroupHoldoutAsync(groupId, "donchian", Req(famB), new QuantJobManager());
        blocked2.Ok.Should().BeFalse();
    }

    [Fact]
    public async Task Parallel_group_reservations_yield_at_most_one_written_reservation()
    {
        var (_, quant) = NewOrchestrator();
        var groups = new ResearchGroupStore(quant.RegistryDir);
        const string groupId = "parallel-grp";

        // Fünf gleichzeitige „reserviere, falls frei"-Versuche verschiedener Familien.
        async Task<bool> TryReserve(string family)
            => await groups.WithLockAsync<bool>(groupId, current =>
            {
                if (current is { Reserved: true }) return Task.FromResult<(ResearchGroupHoldout?, bool)>((null, false));
                return Task.FromResult<(ResearchGroupHoldout?, bool)>((
                    new ResearchGroupHoldout { GroupId = groupId, Reserved = true, SelectedFamilyKey = family, ReservedUtc = DateTimeOffset.UtcNow }, true));
            });

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(i => TryReserve("fam" + i)));
        results.Count(ok => ok).Should().Be(1, "höchstens eine Reservierung trotz paralleler Aufrufe");
        (await groups.GetAsync(groupId))!.Reserved.Should().BeTrue();
    }

    // Baut die finale Holdout-Anfrage aus dem vorbereiteten Kandidaten des Laufs (wie das Frontend).
    private static HoldoutEvaluateRequest HoldoutReq(ResearchRunRecord r)
    {
        var p = r.HoldoutProposal!;
        return new HoldoutEvaluateRequest
        {
            Run = r.Config.Run with { Params = new Dictionary<string, string>(p.Parameters) },
            Options = r.Config.Options,
            CandidateReference = p.CandidateReference,
            CandidateTrialId = p.CandidateTrialId,
            WarmupBars = p.WarmupBars,
            Confirm = true,
        };
    }

    [Fact]
    public async Task Parallel_final_evaluations_in_a_group_allow_exactly_one_and_stay_locked_after_restart()
    {
        var (orch, quant) = NewOrchestrator();
        var csv = WriteCsv(600);
        var start = await orch.StartCampaignAsync(CampaignReq(csv, "reuse-parallel"));
        var groupId = start.GroupId!;
        var fams = new List<ResearchRunRecord>();
        foreach (var f in start.Families) fams.Add(await WaitTerminal(orch, f.RunId, TimeSpan.FromSeconds(90)));
        fams.Should().OnlyContain(r => r.HoldoutProposal != null && r.HoldoutProposal.Available,
            "beide Familien müssen einen vorbereiteten Holdout-Kandidaten haben");

        var jobs = new QuantJobManager();
        // Zwei verschiedene Familien starten GLEICHZEITIG die finale Auswertung derselben Gruppe.
        var tasks = fams.Select(f => orch.EvaluateGroupHoldoutAsync(groupId, f.FamilyKey!, HoldoutReq(f), jobs)).ToArray();
        var results = await Task.WhenAll(tasks);

        results.Count(r => r.Ok).Should().Be(1, "genau eine finale Auswertung wird zugelassen: " +
            string.Join(" | ", results.Select(r => (r.Ok ? "OK" : "BLOCK:") + (r.Error ?? ""))));
        var winner = results.First(r => r.Ok).Holdout!.SelectedFamilyKey!;
        var loserFamily = fams.First(f => f.FamilyKey != winner).FamilyKey!;

        // Nach Neuerstellung der Services bleibt die andere Familie gesperrt.
        var orch2 = new ResearchOrchestrator(quant, new QuantJobManager(), new ResearchRunStore(quant.RegistryDir));
        var loserRun = fams.First(f => f.FamilyKey == loserFamily);
        var again = await orch2.EvaluateGroupHoldoutAsync(groupId, loserFamily, HoldoutReq(loserRun), new QuantJobManager());
        again.Ok.Should().BeFalse("die zweite Familie bleibt auch nach Service-Neustart gesperrt");
    }

    [Fact]
    public async Task A_new_group_cannot_reevaluate_the_same_already_used_holdout()
    {
        var (orch, quant) = NewOrchestrator();
        var csv = WriteCsv(600);

        // Gruppe 1: eine Familie wertet den finalen Holdout tatsächlich aus (reserviert im Register).
        var g1 = await orch.StartCampaignAsync(CampaignReq(csv, "reuse-g1"));
        var g1fams = new List<ResearchRunRecord>();
        foreach (var f in g1.Families) g1fams.Add(await WaitTerminal(orch, f.RunId, TimeSpan.FromSeconds(90)));
        var famA = g1fams.First(f => f.HoldoutProposal is { Available: true });
        var evalA = await orch.EvaluateGroupHoldoutAsync(g1.GroupId!, famA.FamilyKey!, HoldoutReq(famA), new QuantJobManager());
        evalA.Ok.Should().BeTrue("die erste finale Auswertung muss zugelassen sein: " + (evalA.Error ?? ""));

        // Gruppe 2: NEUE Gruppe/IDs auf DENSELBEN Daten → derselbe Holdout-Zeitraum darf nicht erneut ausgewertet werden.
        var g2 = await orch.StartCampaignAsync(CampaignReq(csv, "reuse-g2"));
        var g2fams = new List<ResearchRunRecord>();
        foreach (var f in g2.Families) g2fams.Add(await WaitTerminal(orch, f.RunId, TimeSpan.FromSeconds(90)));
        var famX = g2fams.First(f => f.HoldoutProposal is { Available: true });
        var evalX = await orch.EvaluateGroupHoldoutAsync(g2.GroupId!, famX.FamilyKey!, HoldoutReq(famX), new QuantJobManager());

        evalX.Ok.Should().BeFalse("gruppenübergreifende Wiederverwendung desselben Holdouts ist gesperrt");
        (evalX.Error ?? "").Should().Contain("bereits");
        // Das bereits gespeicherte Ergebnis von Gruppe 1 bleibt lesbar.
        var still = await quant.GetHoldoutAsync(famA.CampaignId!);
        still.Evaluation.Should().NotBeNull();
    }

    [Fact]
    public async Task The_walk_forward_runs_once_no_duplicate_trials_or_budget()
    {
        var (orch, quant) = NewOrchestrator();
        var csv = WriteCsv(600);
        var candidates = new[]
        {
            new Dictionary<string, string> { ["FastPeriod"] = "9", ["SlowPeriod"] = "21" },
            new Dictionary<string, string> { ["FastPeriod"] = "6", ["SlowPeriod"] = "34" },
            new Dictionary<string, string> { ["FastPeriod"] = "12", ["SlowPeriod"] = "48" },
        };
        var res = await orch.StartAsync(StartReq(csv, "wf-once", candidates));
        var run = await WaitTerminal(orch, res.Run!.RunId, TimeSpan.FromSeconds(60));
        run.Status.Should().Be(ResearchStepStatus.Completed);

        // Genau EIN Walk-forward-Trial je Kandidat — kein zweiter WF-Lauf durch die Overfitting-Stufe.
        var trials = await quant.Store.ListTrialsAsync(run.CampaignId!);
        trials.Count(t => t.PeriodRole == "walkforward").Should().Be(candidates.Length);
        // Overfitting hat das vorhandene WF-Ergebnis wiederverwendet (kein erneuter Lauf).
        run.Overfitting!.WalkForward.Should().NotBeNull();
    }

    [Fact]
    public async Task Missing_benchmark_and_uncomputable_pbo_are_skipped_without_blocking_others()
    {
        var (orch, _) = NewOrchestrator();
        var csv = WriteCsv(600);
        // Nur EIN Kandidat → PBO nicht berechenbar; kein Benchmark hinterlegt.
        var one = new[] { new Dictionary<string, string> { ["FastPeriod"] = "9", ["SlowPeriod"] = "21" } };
        var res = await orch.StartAsync(StartReq(csv, "skip", one, benchmarkId: null));
        var run = await WaitTerminal(orch, res.Run!.RunId, TimeSpan.FromSeconds(60));

        run.Status.Should().Be(ResearchStepStatus.Completed);   // Gesamtlauf bleibt erfolgreich
        Step(run, "benchmark").Status.Should().Be(ResearchStepStatus.NotComputable);
        run.Overfitting!.Pbo.Should().BeNull();                 // PBO ausdrücklich nicht berechenbar
        // Unabhängige Schritte bleiben erhalten:
        Step(run, "walkforward").Status.Should().Be(ResearchStepStatus.Completed);
        Step(run, "montecarlo").Status.Should().Be(ResearchStepStatus.Completed);
    }

    [Fact]
    public async Task Reload_returns_the_same_run_without_recomputing()
    {
        var (orch, _) = NewOrchestrator();
        var csv = WriteCsv(600);
        var res = await orch.StartAsync(StartReq(csv, "reload"));
        var run = await WaitTerminal(orch, res.Run!.RunId, TimeSpan.FromSeconds(60));

        var reloaded = (await orch.GetAsync(run.RunId)).Run!;
        reloaded.RunId.Should().Be(run.RunId);
        reloaded.CompletedUtc.Should().Be(run.CompletedUtc);        // nicht neu gerechnet
        reloaded.CampaignId.Should().Be(run.CampaignId);
        reloaded.WalkForward!.TrialsRecorded.Should().Be(run.WalkForward!.TrialsRecorded);
    }

    [Fact]
    public async Task The_run_prepares_but_never_consumes_the_holdout()
    {
        var (orch, quant) = NewOrchestrator();
        var csv = WriteCsv(600);
        var res = await orch.StartAsync(StartReq(csv, "holdout-prep"));
        var run = await WaitTerminal(orch, res.Run!.RunId, TimeSpan.FromSeconds(60));

        run.HoldoutProposal.Should().NotBeNull();
        run.HoldoutProposal!.Available.Should().BeTrue();                  // Kandidat vorbereitet …
        run.HoldoutProposal.CandidateTrialId.Should().NotBeNullOrEmpty();

        // … aber NICHT ausgewertet/verbraucht.
        (await quant.Store.GetHoldoutEvaluationAsync(run.CampaignId!)).Should().BeNull();
        (await quant.Store.GetCampaignAsync(run.CampaignId!))!.HoldoutConsumed.Should().BeFalse();
    }

    [Fact]
    public async Task Two_runs_do_not_mix_results()
    {
        var (orch, quant) = NewOrchestrator();
        var csv = WriteCsv(600);
        var a = await WaitTerminal(orch, (await orch.StartAsync(StartReq(csv, "camp-a"))).Run!.RunId, TimeSpan.FromSeconds(60));
        var b = await WaitTerminal(orch, (await orch.StartAsync(StartReq(csv, "camp-b"))).Run!.RunId, TimeSpan.FromSeconds(60));

        a.RunId.Should().NotBe(b.RunId);
        a.CampaignId.Should().NotBe(b.CampaignId);
        (await orch.ListAsync()).Should().HaveCount(2);

        var trialsA = await quant.Store.ListTrialsAsync(a.CampaignId!);
        trialsA.Should().OnlyContain(t => t.CampaignId == a.CampaignId);   // keine Vermischung
    }

    [Fact]
    public async Task An_interrupted_running_record_is_reported_honestly_and_not_repeated()
    {
        // Simuliert einen Prozessausfall: ein als "Running" gespeicherter Lauf, dessen Job nach einem "Neustart"
        // nicht mehr existiert (frischer Orchestrator/leerer JobManager). GetAsync darf ihn nicht heimlich
        // fortsetzen, sondern muss ihn ehrlich als unterbrochen darstellen.
        var tempRoot = Path.Combine(Path.GetTempPath(), "research-tests-" + Guid.NewGuid().ToString("N"));
        _dirs.Add(tempRoot);
        var quant = new QuantApiService(new BacktestApiService(RepoRoot()), tempRoot);
        var store = new ResearchRunStore(quant.RegistryDir);
        var stale = new ResearchRunRecord
        {
            RunId = "stale-1", CampaignId = "c", Config = StartReq(WriteCsv(50), "c"),
            Status = ResearchStepStatus.Running, JobId = "job-that-no-longer-exists",
            Steps = new[] { new ResearchStepState { Key = "data", Label = "Daten", Status = ResearchStepStatus.Running } }
        };
        await store.CreateAsync(stale);

        var orch = new ResearchOrchestrator(quant, new QuantJobManager(), store);
        var res = await orch.GetAsync("stale-1");

        res.Run!.Status.Should().Be(ResearchStepStatus.Cancelled);
        res.Run.StatusReason.Should().Contain("Neustart");
        res.Run.Steps.Single().Status.Should().Be(ResearchStepStatus.Cancelled);
    }

    private static ResearchStepState Step(ResearchRunRecord run, string key) => run.Steps.Single(s => s.Key == key);

    /// <summary>CSV, in dem der ENTWICKLUNGSbereich [0..usable) für alle Varianten identisch ist und nur der
    /// Holdout-Schwanz [usable..total) variiert — für den Leakage-Nachweis (Befund A).</summary>
    private string WriteCsvHoldoutVariant(int total, int usable, double holdoutShift)
    {
        var path = Path.Combine(Path.GetTempPath(), "research-split-" + Guid.NewGuid().ToString("N") + ".csv");
        _files.Add(path);
        var sb = new StringBuilder("timestamp,open,high,low,close,volume\n");
        double Price(int i) => 5000 + 40 * Math.Sin(2 * Math.PI * i / 40.0);
        for (int i = 0; i < total; i++)
        {
            var t = T0.AddMinutes(Tf * i).UtcDateTime;
            double shift = i >= usable ? holdoutShift : 0.0;   // NUR der Holdout-Bereich ändert sich
            double o = Price(i - 1) + shift, c = Price(i) + shift, h = Math.Max(o, c) + 1, l = Math.Min(o, c) - 1;
            sb.Append(string.Format(CultureInfo.InvariantCulture,
                "{0:yyyy-MM-ddTHH:mm:ss}Z,{1:F2},{2:F2},{3:F2},{4:F2},100\n", t, o, h, l, c));
        }
        File.WriteAllText(path, sb.ToString());
        return path;
    }

    [Fact]
    public async Task Pre_checks_use_only_the_development_partition_and_changing_only_holdout_data_does_not_change_them()
    {
        var (orch, quant) = NewOrchestrator();
        // total 500, HoldoutFraction 0.2 → holdoutBars 100, usable 400.
        var csvA = WriteCsvHoldoutVariant(total: 500, usable: 400, holdoutShift: 0.0);
        var csvB = WriteCsvHoldoutVariant(total: 500, usable: 400, holdoutShift: 250.0); // NUR Holdout verändert

        var reqA = StartReq(csvA, "leak-a"); reqA = reqA with { Run = reqA.Run with { Path = csvA } };
        var a = await WaitTerminal(orch, (await orch.StartAsync(reqA)).Run!.RunId, TimeSpan.FromSeconds(60));
        var reqB = StartReq(csvB, "leak-b"); reqB = reqB with { Run = reqB.Run with { Path = csvB } };
        var b = await WaitTerminal(orch, (await orch.StartAsync(reqB)).Run!.RunId, TimeSpan.FromSeconds(60));

        a.Status.Should().Be(ResearchStepStatus.Completed, "A reason={0} steps={1}", a.StatusReason ?? "-", string.Join(",", a.Steps.Select(s => $"{s.Key}:{s.Status}:{s.Reason}")));
        b.Status.Should().Be(ResearchStepStatus.Completed, "B reason={0} steps={1}", b.StatusReason ?? "-", string.Join(",", b.Steps.Select(s => $"{s.Key}:{s.Status}:{s.Reason}")));

        // Split korrekt reserviert.
        a.HoldoutBars.Should().Be(100);
        a.DevelopmentBars.Should().Be(400);
        a.DevelopmentToUtc.Should().NotBeNull();

        // KEINE Holdout-Zeit in der Vorprüfung: die Basis-Backtest-Kurve endet vor dem Holdout-Start.
        // Die letzte Entwicklungs-Kerze schließt exakt am Holdout-Start (contiguous), reicht aber nicht darüber
        // hinaus. Eine Holdout-Kerze hätte einen Close STRIKT nach dieser Grenze → wäre Leakage.
        long devEndMs = a.DevelopmentToUtc!.Value.ToUnixTimeMilliseconds();
        a.Analysis!.Curve.Should().NotBeEmpty();
        a.Analysis.Curve.Max(p => p.T).Should().BeLessThanOrEqualTo(devEndMs);

        // Nur die Holdout-Daten unterscheiden sich → Entwicklungsergebnisse müssen IDENTISCH sein.
        b.Analysis!.FinalEquityRealized.Should().Be(a.Analysis.FinalEquityRealized);
        b.Analysis.Trades.Should().Be(a.Analysis.Trades);
        b.WalkForward!.OosEquity.Should().Equal(a.WalkForward!.OosEquity);
        b.MonteCarlo!.FinalCapital!.Median.Should().Be(a.MonteCarlo!.FinalCapital!.Median);
        b.Robustness!.Baseline.Should().Be(a.Robustness!.Baseline);

        // Der Gesamt-Datenfingerabdruck DARF sich unterscheiden (Holdout hat sich geändert).
        var campA = await quant.Store.GetCampaignAsync(a.CampaignId!);
        var campB = await quant.Store.GetCampaignAsync(b.CampaignId!);
        campA!.DataSha.Should().NotBe(campB!.DataSha);
    }
}
