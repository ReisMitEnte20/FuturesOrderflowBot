using System.Collections.Concurrent;

namespace TradingBot.DevDashboard.Services.Quant;

/// <summary>
/// Backend-Ablaufsteuerung eines Research-Laufs: kettet die vorhandenen Quant-Schritte (Datenqualität +
/// Basis-Backtest + Benchmark → Walk-forward → Robustheit → Monte Carlo → Overfitting → Holdout-Vorbereitung)
/// zu EINEM Lauf mit eindeutiger Run-Id. Jeder Schritt bekommt einen dauerhaften Status; Teilergebnisse werden
/// nach jedem Schritt persistiert (neustart-fähig). Der Walk-forward läuft GENAU EINMAL — Overfitting nutzt sein
/// Ergebnis wieder, statt ihn erneut zu starten (keine doppelten Trials, kein doppeltes Budget). Der finale
/// Holdout wird NICHT automatisch verbraucht: der Lauf bereitet nur den Kandidaten vor.
/// </summary>
public sealed class ResearchOrchestrator
{
    private readonly QuantApiService _quant;
    private readonly QuantJobManager _jobs;
    private readonly ResearchRunStore _store;
    private readonly ResearchGroupStore _groups;
    /// <summary>Läufe, deren Job in DIESEM Prozess läuft (runId → jobId). Für Abbruch und Dedupe.</summary>
    private readonly ConcurrentDictionary<string, string> _activeJobs = new();
    /// <summary>Läufe, die DIESER Prozess gestartet hat. Nach einem Neustart leer — so wird ein persistiert als
    /// "Running" hinterlegter Lauf, den dieser Prozess nicht kennt, zuverlässig als unterbrochen erkannt.</summary>
    private readonly ConcurrentDictionary<string, byte> _knownRuns = new();

    public ResearchOrchestrator(QuantApiService quant, QuantJobManager jobs, ResearchRunStore store)
    {
        _quant = quant ?? throw new ArgumentNullException(nameof(quant));
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _groups = new ResearchGroupStore(_quant.RegistryDir);
    }

    private static readonly (string Key, string Label)[] StepDefs =
    {
        ("data", "Daten & Qualität"),
        ("backtest", "Basis-Backtest"),
        ("benchmark", "Benchmark-Vergleich"),
        ("walkforward", "Walk-forward & Kandidatenauswahl"),
        ("robustness", "Robustheit (Kosten/Slippage/Parameter/Verzögerung)"),
        ("montecarlo", "Monte Carlo / Bootstrap"),
        ("overfitting", "Overfitting (PBO/PSR/DSR)"),
        ("holdout", "Finaler Holdout (Vorbereitung)")
    };

    public ResearchRunStore Store => _store;

    // ---------------------------------------------------------------------------------------------

    /// <summary>Zuordnung eines Familien-Laufs zu einer übergreifenden Vergleichs-Kampagne.</summary>
    public sealed record ResearchGroupContext(string GroupId, string GroupKey, string FamilyKey, string? FamilyName);

    public async Task<ResearchRunResponse> StartAsync(ResearchStartRequest request, CancellationToken ct = default)
        => await StartAsync(request, null, ct);

    public async Task<ResearchRunResponse> StartAsync(ResearchStartRequest request, ResearchGroupContext? group, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Run is null) return Fail("Keine Backtest-Konfiguration (Run) angegeben.");
        if (request.Campaign is null || string.IsNullOrWhiteSpace(request.Campaign.Id))
            return Fail("Kampagne mit Id ist erforderlich.");
        if (request.Candidates.Count == 0)
            return Fail("Mindestens ein Kandidaten-Parametersatz ist erforderlich (vorab festgelegt, keine Generierung).");

        // Mehrfachklick/Reload-Schutz: Läuft bereits ein Lauf derselben (Basis-)Kampagne mit aktivem Job, wird
        // dieser zurückgegeben, statt einen zweiten zu starten.
        var existingRuns = await _store.ListAsync(null, ct);
        var active = existingRuns.FirstOrDefault(r =>
            r.Status == ResearchStepStatus.Running &&
            string.Equals(r.Config.Campaign.Id, request.Campaign.Id, StringComparison.Ordinal) &&
            IsLive(r.RunId));
        if (active is not null)
            return new ResearchRunResponse { Ok = true, AlreadyRunning = true, JobId = active.JobId, Run = active };

        string runId = "research-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "-" + Guid.NewGuid().ToString("N")[..8];
        _knownRuns[runId] = 1;
        // Eigene, eindeutige Kampagne je Lauf — so verbraucht kein zweiter Lauf das Budget/den Holdout eines früheren.
        string campaignId = Sanitize(request.Campaign.Id) + "-" + Guid.NewGuid().ToString("N")[..6];
        var effectiveCampaign = request.Campaign with { Id = campaignId };

        var steps = StepDefs.Select(s => new ResearchStepState { Key = s.Key, Label = s.Label, Status = ResearchStepStatus.Pending }).ToList();

        var record = new ResearchRunRecord
        {
            RunId = runId,
            CampaignId = campaignId,
            Config = request with { Campaign = effectiveCampaign },
            IsDemo = request.IsDemo,
            Status = ResearchStepStatus.Running,
            Steps = steps,
            CreatedUtc = DateTimeOffset.UtcNow,
            CampaignGroupId = group?.GroupId,
            GroupKey = group?.GroupKey,
            FamilyKey = group?.FamilyKey,
            FamilyName = group?.FamilyName
        };
        record = await _store.CreateAsync(record, ct);

        // Ein einziger Hintergrund-Job für die gesamte Kette (großzügiges Zeitlimit, da mehrere Schritte).
        string jobId = _jobs.Start("research", async (progress, jct) =>
            (object)await RunPipelineAsync(runId, progress, jct), TimeSpan.FromMinutes(25));
        _activeJobs[runId] = jobId;

        // Kein weiterer Schreibvorgang hier: die (ggf. sehr schnelle) Pipeline besitzt ab jetzt die Persistenz;
        // ein Update von hier könnte einen bereits geschriebenen Endzustand überschreiben. JobId nur in der Antwort.
        return new ResearchRunResponse { Ok = true, JobId = jobId, Run = record with { JobId = jobId } };
    }

    // ---------------------------------------------------------------------------------------------
    // Mehrstrategie-Vergleich: EINE Kampagne, mehrere Strategie-Familien. Jede Familie durchläuft die
    // bestehende Einzel-Pipeline (eigene, eindeutige Kampagne → kein doppeltes Budget, kein geteilter Holdout).

    public async Task<ResearchCampaignResponse> StartCampaignAsync(ResearchCampaignStartRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Run is null) return new ResearchCampaignResponse { Ok = false, Error = "Keine Backtest-Konfiguration (Run) angegeben." };
        if (request.Campaign is null || string.IsNullOrWhiteSpace(request.Campaign.Id))
            return new ResearchCampaignResponse { Ok = false, Error = "Kampagne mit Id ist erforderlich." };
        if (request.Families.Count == 0)
            return new ResearchCampaignResponse { Ok = false, Error = "Mindestens eine Strategie-Familie ist erforderlich." };
        var famKeys = request.Families.Select(f => f.Key).ToList();
        if (famKeys.Distinct(StringComparer.Ordinal).Count() != famKeys.Count)
            return new ResearchCampaignResponse { Ok = false, Error = "Familien-Schlüssel müssen innerhalb der Kampagne eindeutig sein." };
        foreach (var f in request.Families)
            if (f.Candidates.Count == 0)
                return new ResearchCampaignResponse { Ok = false, Error = $"Familie '{f.Key}' hat keinen Kandidaten-Suchraum." };

        string groupKey = request.Campaign.Id;

        // Mehrfachstart-Schutz auf Gruppenebene: läuft bereits eine Gruppe mit demselben GroupKey und aktiven
        // Läufen, wird diese zurückgegeben, statt neue Läufe (und damit neues Budget) zu starten.
        var all = await _store.ListAsync(null, ct);
        var activeGroup = all
            .Where(r => r.GroupKey == groupKey && r.CampaignGroupId is not null &&
                        r.Status == ResearchStepStatus.Running && IsLive(r.RunId))
            .Select(r => r.CampaignGroupId!)
            .FirstOrDefault();
        if (activeGroup is not null)
        {
            var existing = all.Where(r => r.CampaignGroupId == activeGroup).OrderBy(r => r.FamilyKey, StringComparer.Ordinal).ToList();
            return new ResearchCampaignResponse { Ok = true, AlreadyRunning = true, GroupId = activeGroup, Families = existing };
        }

        string groupId = "campaign-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "-" + Guid.NewGuid().ToString("N")[..8];
        var started = new List<ResearchRunRecord>();
        foreach (var fam in request.Families)
        {
            var famRun = request.Run with { Strategy = fam.StrategyId };
            var famCampaign = request.Campaign with
            {
                Id = groupKey + "-" + Sanitize(fam.Key),
                Name = (string.IsNullOrWhiteSpace(request.Campaign.Name) ? groupKey : request.Campaign.Name) + " · " + (fam.Name ?? fam.StrategyId)
            };
            var startReq = new ResearchStartRequest
            {
                Run = famRun,
                Options = request.Options,
                Campaign = famCampaign,
                Candidates = fam.Candidates,
                SelectionMetric = request.SelectionMetric,
                Mode = request.Mode, TrainBars = request.TrainBars, TestBars = request.TestBars, StepBars = request.StepBars,
                LabelSpanBars = request.LabelSpanBars, EmbargoBars = request.EmbargoBars, WarmupBars = request.WarmupBars,
                HoldoutFraction = request.HoldoutFraction,
                MonteCarloSource = request.MonteCarloSource, MonteCarloMethod = request.MonteCarloMethod,
                MonteCarloIterations = request.MonteCarloIterations, Seed = request.Seed, BlockLength = request.BlockLength,
                CapitalBarrier = request.CapitalBarrier, RobustnessMetric = request.RobustnessMetric,
                OverfittingBlocks = request.OverfittingBlocks, EstimateEffectiveTrials = request.EstimateEffectiveTrials,
                BenchmarkId = request.BenchmarkId, StrategyIsFullyFunded = request.StrategyIsFullyFunded, IsDemo = request.IsDemo,
                MinComparisonObservations = request.MinComparisonObservations
            };
            var group = new ResearchGroupContext(groupId, groupKey, fam.Key, fam.Name ?? fam.StrategyId);
            var res = await StartAsync(startReq, group, ct);
            if (!res.Ok || res.Run is null)
                return new ResearchCampaignResponse { Ok = false, Error = $"Familie '{fam.Key}' konnte nicht gestartet werden: {res.Error}", GroupId = groupId, Families = started };
            started.Add(res.Run);
        }

        return new ResearchCampaignResponse { Ok = true, GroupId = groupId, Families = started };
    }

    /// <summary>Status + gemeinsamer Vergleich einer Kampagnen-Gruppe. Der Vergleich wird erst gebildet, wenn
    /// mindestens zwei Familien eine auswertbare OOS-Reihe haben (sonst ehrlich „noch nicht verfügbar").</summary>
    public async Task<ResearchCampaignResponse> GetCampaignAsync(string groupId, CancellationToken ct = default)
    {
        var runs = (await _store.ListAsync(null, ct))
            .Where(r => r.CampaignGroupId == groupId)
            .ToList();
        if (runs.Count == 0)
            return new ResearchCampaignResponse { Ok = false, Error = $"Vergleichs-Kampagne '{groupId}' unbekannt." };

        // Jeden Lauf ehrlich aktualisieren (unterbrochene Läufe werden als solche dargestellt).
        var refreshed = new List<ResearchRunRecord>(runs.Count);
        foreach (var r in runs)
        {
            var got = await GetAsync(r.RunId, ct);
            refreshed.Add(got.Run ?? r);
        }
        refreshed = refreshed.OrderBy(r => r.FamilyKey, StringComparer.Ordinal).ToList();

        var comparison = ResearchComparisonBuilder.Build(refreshed);
        var holdout = await _groups.GetAsync(groupId, ct);
        return new ResearchCampaignResponse { Ok = true, GroupId = groupId, Families = refreshed, Comparison = comparison, Holdout = holdout };
    }

    /// <summary>True, wenn diese Kampagne zu einer Vergleichs-Gruppe gehört (dann darf ihr finaler Holdout NUR über
    /// die Gruppensperre ausgewertet werden — der Familien-/Direkt-Endpunkt darf sie nicht umgehen).</summary>
    public async Task<bool> IsGroupGovernedCampaignAsync(string campaignId, CancellationToken ct = default)
    {
        var runs = await _store.ListAsync(null, ct);
        return runs.Any(r => r.CampaignGroupId is not null &&
                             string.Equals(r.CampaignId, campaignId, StringComparison.Ordinal));
    }

    /// <summary>
    /// Finale Holdout-Auswertung EINER Familie über die GRUPPENSPERRE. Bindet den EINEN gemeinsamen Holdout der
    /// Gruppe an genau eine Familie + Kandidat: eine bereits reservierte Gruppe lässt keine zweite (auch keine
    /// parallele) finale Auswertung einer anderen Familie zu, und der Kandidat wechselt nicht mehr. Der eigentliche
    /// einmalige Verbrauch/Einfrieren erfolgt weiterhin im geprüften Registerpfad (QuantApiService.EvaluateHoldoutAsync).
    /// </summary>
    public async Task<ResearchGroupHoldoutResponse> EvaluateGroupHoldoutAsync(
        string groupId, string familyKey, HoldoutEvaluateRequest request, QuantJobManager jobs, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var runs = (await _store.ListAsync(null, ct)).Where(r => r.CampaignGroupId == groupId).ToList();
        if (runs.Count == 0)
            return new ResearchGroupHoldoutResponse { Ok = false, GroupId = groupId, Error = $"Vergleichs-Kampagne '{groupId}' unbekannt." };
        var famRun = runs.FirstOrDefault(r => string.Equals(r.FamilyKey, familyKey, StringComparison.Ordinal));
        if (famRun is null)
            return new ResearchGroupHoldoutResponse { Ok = false, GroupId = groupId, Error = $"Familie '{familyKey}' gehört nicht zur Gruppe '{groupId}'." };
        var campaignId = famRun.CampaignId;
        if (string.IsNullOrEmpty(campaignId))
            return new ResearchGroupHoldoutResponse { Ok = false, GroupId = groupId, Error = "Der Familien-Lauf hat noch keine Kampagne (nicht abgeschlossen)." };

        // Atomar unter der Gruppensperre: prüfen, ob die Gruppe bereits an eine (andere) Familie gebunden ist,
        // sonst nach erfolgreicher Register-Reservierung die Gruppe binden. Parallele Aufrufe werden serialisiert.
        return await _groups.WithLockAsync<ResearchGroupHoldoutResponse>(groupId, async current =>
        {
            if (current is { Reserved: true } && !string.Equals(current.SelectedFamilyKey, familyKey, StringComparison.Ordinal))
            {
                var blocked = new ResearchGroupHoldoutResponse
                {
                    Ok = false, GroupId = groupId, Holdout = current,
                    Error = $"Der finale Holdout dieser Vergleichs-Gruppe ist bereits für Familie '{current.SelectedFamilyKey}' " +
                            $"reserviert{(current.Consumed ? " und ausgewertet" : "")}. Eine zweite finale Auswertung einer anderen " +
                            "Familie ist gesperrt (ein Holdout je Gruppe, kein Kandidatenwechsel)."
                };
                return (null, blocked);   // nichts schreiben
            }

            // Delegation an den geprüften Registerpfad (Confirm, Snapshot-Bindung, Fingerprint, Einmaligkeit).
            var eval = await _quant.EvaluateHoldoutAsync(campaignId!, request, jobs, ct);
            if (!eval.Ok)
                return (null, new ResearchGroupHoldoutResponse { Ok = false, GroupId = groupId, Error = eval.Error, Evaluation = eval, Holdout = current });

            // Erfolg: Gruppe an diese Familie + Kandidat binden (idempotent bei Wiederholung derselben Familie).
            var now = DateTimeOffset.UtcNow;
            bool consumed = string.Equals(eval.State, "Completed", StringComparison.OrdinalIgnoreCase) || eval.AlreadyExisted;
            var toWrite = new ResearchGroupHoldout
            {
                GroupId = groupId,
                Reserved = true,
                Consumed = current?.Consumed == true || consumed,
                SelectedFamilyKey = familyKey,
                SelectedCampaignId = campaignId,
                CandidateTrialId = request.CandidateTrialId ?? current?.CandidateTrialId,
                CandidateReference = request.CandidateReference ?? current?.CandidateReference,
                EvaluationReference = eval.Evaluation?.RunId ?? current?.EvaluationReference,
                ReservedUtc = current?.ReservedUtc ?? now,
                ConsumedUtc = (current?.Consumed == true) ? current!.ConsumedUtc : (consumed ? now : null)
            };
            return (toWrite, new ResearchGroupHoldoutResponse { Ok = true, GroupId = groupId, Holdout = toWrite, Evaluation = eval });
        }, ct);
    }

    /// <summary>Alle Kampagnen-Gruppen (neueste zuerst), je Gruppe der Sammelstatus. Für die Auswahl im Frontend.</summary>
    public async Task<IReadOnlyList<ResearchCampaignSummary>> ListCampaignsAsync(CancellationToken ct = default)
    {
        var runs = (await _store.ListAsync(null, ct)).Where(r => r.CampaignGroupId is not null).ToList();
        return runs.GroupBy(r => r.CampaignGroupId!)
            .Select(g =>
            {
                var list = g.ToList();
                var status = list.Any(r => r.Status == ResearchStepStatus.Running) ? ResearchStepStatus.Running
                    : list.All(r => r.Status == ResearchStepStatus.Completed) ? ResearchStepStatus.Completed
                    : list.Any(r => r.Status == ResearchStepStatus.Failed) ? ResearchStepStatus.Failed
                    : ResearchStepStatus.Cancelled;
                return new ResearchCampaignSummary(
                    g.Key,
                    list.OrderBy(r => r.CreatedUtc).First().Config.Campaign.Name,
                    list.Min(r => r.CreatedUtc),
                    list.Count,
                    status);
            })
            .OrderByDescending(x => x.CreatedUtc)
            .ToList();
    }

    public async Task<ResearchRunResponse> GetAsync(string runId, CancellationToken ct = default)
    {
        var record = await _store.GetAsync(runId, ct);
        if (record is null) return new ResearchRunResponse { Ok = false, Error = $"Research-Lauf '{runId}' unbekannt." };

        // Ehrliche Darstellung nach einem Neustart: Ein als "Running" gespeicherter Lauf, den DIESER Prozess nicht
        // gestartet hat (nach Neustart leerer Speicher), wird als unterbrochen markiert — NICHT heimlich wiederholt.
        if (record.Status == ResearchStepStatus.Running && !_knownRuns.ContainsKey(runId))
        {
            var interruptedSteps = record.Steps
                .Select(s => s.Status is ResearchStepStatus.Running or ResearchStepStatus.Pending
                    ? s with { Status = ResearchStepStatus.Cancelled, Reason = "Durch Server-Neustart unterbrochen." }
                    : s).ToList();
            record = record with
            {
                Status = ResearchStepStatus.Cancelled,
                StatusReason = "Der Lauf wurde durch einen Server-Neustart unterbrochen und nicht automatisch fortgesetzt.",
                JobId = null,
                Steps = interruptedSteps,
                CompletedUtc = record.CompletedUtc ?? DateTimeOffset.UtcNow
            };
            record = await _store.UpdateAsync(record, ct);
        }
        return new ResearchRunResponse { Ok = true, JobId = record.JobId, Run = record };
    }

    public async Task<IReadOnlyList<ResearchRunRecord>> ListAsync(string? campaignId = null, CancellationToken ct = default)
        => await _store.ListAsync(campaignId, ct);

    public bool Cancel(string runId)
    {
        string? jobId = _activeJobs.TryGetValue(runId, out var j) ? j : _store.GetAsync(runId).GetAwaiter().GetResult()?.JobId;
        return jobId is not null && _jobs.Cancel(jobId);
    }

    /// <summary>True, wenn der Job dieses Laufs in diesem Prozess noch läuft.</summary>
    private bool IsLive(string runId) =>
        _activeJobs.TryGetValue(runId, out var jobId) && _jobs.Get(jobId) is { Status: QuantJobStatus.Running };

    // ---------------------------------------------------------------------------------------------

    private async Task<ResearchRunRecord> RunPipelineAsync(string runId, IProgress<double>? progress, CancellationToken ct)
    {
        var record = await _store.GetAsync(runId, ct)
            ?? throw new InvalidOperationException($"Research-Lauf '{runId}' verschwand vor der Ausführung.");
        var cfg = record.Config;

        async Task SetStep(string key, ResearchStepStatus status, string? reason = null, string? dataBasis = null, bool start = false, bool done = false)
        {
            var steps = record.Steps.Select(s => s.Key == key
                ? s with
                {
                    Status = status,
                    Reason = reason ?? s.Reason,
                    DataBasis = dataBasis ?? s.DataBasis,
                    StartedUtc = start ? DateTimeOffset.UtcNow : s.StartedUtc,
                    CompletedUtc = done ? DateTimeOffset.UtcNow : s.CompletedUtc
                }
                : s).ToList();
            record = record with { Steps = steps };
            record = await _store.UpdateAsync(record, ct);
        }

        try
        {
            // --- 0: Entwicklungs-/Holdout-Split VORAB festlegen. ALLE Vorprüfungen laufen ausschließlich auf dem
            //        Entwicklungsbereich (kein Holdout-Leakage); der reservierte Holdout wird erst am Ende separat
            //        und nur nach ausdrücklicher Bestätigung ausgewertet.
            var split = await _quant.ComputeHoldoutSplitAsync(cfg.Run, cfg.HoldoutFraction, ct);
            // Der Entwicklungs-Backtest lädt bis zur EXKLUSIVEN Ladegrenze (HoldoutStart − 1 Tick), damit auch der
            // Sierra-Tick-Pfad keine Kerze mit OpenTime >= HoldoutStart erzeugt. DevelopmentToUtc bleibt der
            // Anzeigewert (Herkunft/Provenance). Ohne Holdout: unveränderter Original-Run.
            var devLoadEnd = split.DevelopmentLoadToUtc ?? split.DevelopmentToUtc;
            var devRun = devLoadEnd is { } devEnd ? cfg.Run with { ToUtc = devEnd.ToString("o") } : cfg.Run;
            record = record with
            {
                DataFrom = split.DataFrom, DataTo = split.DataTo,
                DevelopmentToUtc = split.DevelopmentToUtc, HoldoutFrom = split.HoldoutFrom, HoldoutTo = split.HoldoutTo,
                TotalBars = split.TotalBars, DevelopmentBars = split.UsableBars, HoldoutBars = split.HoldoutBars,
                DevRun = devRun
            };
            record = await _store.UpdateAsync(record, ct);
            string devBasis = split.HoldoutBars > 0
                ? "Entwicklungsbereich (vor dem reservierten Holdout)"
                : "Vollständige Historie (kein Holdout reserviert)";

            // --- 1/2/7: Daten + Basis-Backtest + Benchmark in einem Analyse-Aufruf (NUR Entwicklungsbereich) ---
            await SetStep("data", ResearchStepStatus.Running, start: true);
            await SetStep("backtest", ResearchStepStatus.Running, start: true);
            progress?.Report(0.05);
            var analysis = await _quant.AnalyzeAsync(new QuantAnalyzeRequest
            {
                Run = devRun, Options = cfg.Options, BenchmarkId = cfg.BenchmarkId, StrategyIsFullyFunded = cfg.StrategyIsFullyFunded
            }, Scaled(progress, 0.05, 0.20), ct);
            record = record with { Analysis = analysis };

            if (!analysis.Ok)
            {
                await SetStep("data", ResearchStepStatus.Failed, analysis.Error, done: true);
                await SetStep("backtest", ResearchStepStatus.Failed, analysis.Error, done: true);
                await SetStep("benchmark", ResearchStepStatus.Skipped, "Basis-Analyse fehlgeschlagen.", done: true);
                return await Finish(record, ResearchStepStatus.Failed, "Basis-Analyse fehlgeschlagen: " + analysis.Error, ct);
            }
            await SetStep("data", ResearchStepStatus.Completed, dataBasis: devBasis, done: true);
            await SetStep("backtest", ResearchStepStatus.Completed, dataBasis: devBasis, done: true);
            bool bmAvailable = analysis.Benchmark?.Available == true;
            await SetStep("benchmark",
                bmAvailable ? ResearchStepStatus.Completed : ResearchStepStatus.NotComputable,
                bmAvailable ? null : (analysis.Benchmark?.UnavailableReason ?? "Keine passende Benchmark-Reihe hinterlegt."),
                dataBasis: bmAvailable ? "Gemeinsamer Zeitraum Strategie/Benchmark" : null, done: true);
            ct.ThrowIfCancellationRequested();

            // --- 3: Walk-forward (genau einmal — legt Kampagne, Trials, Holdout-Fenster an) ---
            await SetStep("walkforward", ResearchStepStatus.Running, start: true);
            var wfReq = BuildWalkForward(cfg);
            var wf = await _quant.WalkForwardAsync(wfReq, Scaled(progress, 0.20, 0.45), ct);
            record = record with { WalkForward = wf, CampaignId = wf.CampaignId ?? record.CampaignId };
            if (wf.Ok)
                await SetStep("walkforward", ResearchStepStatus.Completed, dataBasis: "Walk-forward-Out-of-Sample (Training-only Auswahl)", done: true);
            else
                await SetStep("walkforward", ResearchStepStatus.Failed, wf.Error, done: true);
            ct.ThrowIfCancellationRequested();

            // --- 4: Robustheit (unabhängig; blockiert nicht die anderen) ---
            await SetStep("robustness", ResearchStepStatus.Running, start: true);
            var rob = await _quant.RobustnessAsync(BuildRobustness(cfg, devRun), Scaled(progress, 0.45, 0.60), ct);
            if (rob.Ok)
                await SetStep("robustness", ResearchStepStatus.Completed, dataBasis: devBasis + " unter Stress-Szenarien", done: true);
            else
                await SetStep("robustness", ResearchStepStatus.NotComputable, rob.Error, done: true);
            record = record with { Robustness = rob };
            ct.ThrowIfCancellationRequested();

            // --- 5: Monte Carlo (unabhängig) ---
            await SetStep("montecarlo", ResearchStepStatus.Running, start: true);
            var mc = await _quant.MonteCarloAsync(BuildMonteCarlo(cfg, devRun), Scaled(progress, 0.60, 0.80), ct);
            if (mc.Ok)
                await SetStep("montecarlo", ResearchStepStatus.Completed, dataBasis: "Resampling der Entwicklungs-Backtest-Beobachtungen", done: true);
            else
                await SetStep("montecarlo", ResearchStepStatus.NotComputable, mc.Error, done: true);
            record = record with { MonteCarlo = mc };
            ct.ThrowIfCancellationRequested();

            // --- 6: Overfitting — nutzt das VORHANDENE Walk-forward-Ergebnis wieder (kein zweiter WF, kein Budget) ---
            await SetStep("overfitting", ResearchStepStatus.Running, start: true);
            if (wf.Ok)
            {
                var of = await _quant.ComputeOverfittingFromWalkForwardAsync(wf, wfReq, cfg.OverfittingBlocks, cfg.EstimateEffectiveTrials, ct);
                record = record with { Overfitting = of };
                if (!of.Ok)
                    await SetStep("overfitting", ResearchStepStatus.Failed, of.Error, done: true);
                else if (of.Pbo is null && of.Psr is null && of.Dsr is null)
                    await SetStep("overfitting", ResearchStepStatus.NotComputable,
                        of.PboUnavailableReason ?? "Zu wenige Kandidaten/Perioden für PBO/PSR/DSR.", done: true);
                else
                    await SetStep("overfitting", ResearchStepStatus.Completed,
                        of.Pbo is null ? "PBO nicht berechenbar; PSR/DSR ausgewiesen." : null,
                        dataBasis: "Walk-forward-Kandidatenmatrix (CSCV) + OOS-Reihe", done: true);
            }
            else
            {
                await SetStep("overfitting", ResearchStepStatus.Skipped, "Kein erfolgreicher Walk-forward — PBO/PSR/DSR entfallen.", done: true);
            }
            progress?.Report(0.9);
            ct.ThrowIfCancellationRequested();

            // --- 8: Holdout-Vorbereitung (KEIN automatischer Verbrauch) ---
            await SetStep("holdout", ResearchStepStatus.Running, start: true);
            var proposal = await BuildHoldoutProposalAsync(cfg, wf, ct);
            record = record with { HoldoutProposal = proposal };
            if (proposal.Available)
                await SetStep("holdout", ResearchStepStatus.Completed,
                    "Kandidat vorbereitet — finale Auswertung erst nach ausdrücklicher Bestätigung.",
                    dataBasis: "Reservierter finaler Holdout", done: true);
            else
                await SetStep("holdout", ResearchStepStatus.NotComputable, proposal.UnavailableReason, done: true);

            progress?.Report(1.0);
            return await Finish(record, ResearchStepStatus.Completed, null, ct);
        }
        catch (OperationCanceledException)
        {
            var steps = record.Steps.Select(s => s.Status is ResearchStepStatus.Running or ResearchStepStatus.Pending
                ? s with { Status = ResearchStepStatus.Cancelled, Reason = "Abgebrochen." } : s).ToList();
            record = record with { Steps = steps };
            await Finish(record, ResearchStepStatus.Cancelled, "Der Lauf wurde abgebrochen.", ct: CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            var steps = record.Steps.Select(s => s.Status == ResearchStepStatus.Running
                ? s with { Status = ResearchStepStatus.Failed, Reason = ex.Message } : s).ToList();
            record = record with { Steps = steps };
            return await Finish(record, ResearchStepStatus.Failed, ex.Message, ct: CancellationToken.None);
        }
    }

    private async Task<ResearchRunRecord> Finish(ResearchRunRecord record, ResearchStepStatus status, string? reason, CancellationToken ct)
    {
        record = record with { Status = status, StatusReason = reason, CompletedUtc = DateTimeOffset.UtcNow };
        return await _store.UpdateAsync(record, ct);
    }

    // ---------------------------------------------------------------------------------------------
    // Holdout-Vorbereitung: Kandidat aus den Walk-forward-Fenstern wählen und an einen gespeicherten Trial binden.

    private async Task<ResearchHoldoutProposal> BuildHoldoutProposalAsync(
        ResearchStartRequest cfg, QuantWalkForwardResponse wf, CancellationToken ct)
    {
        if (!wf.Ok || string.IsNullOrEmpty(wf.CampaignId))
            return new ResearchHoldoutProposal { Available = false, UnavailableReason = "Kein erfolgreicher Walk-forward — kein Holdout-Kandidat." };

        // Bereits ausgewerteter/verbrauchter Holdout dieser Kampagne? Vorhandenes Ergebnis anzeigen, nicht neu rechnen.
        var existing = await _quant.GetHoldoutAsync(wf.CampaignId, ct);
        if (existing.Evaluation is not null)
            return new ResearchHoldoutProposal
            {
                Available = false,
                UnavailableReason = "Der Holdout dieser Kampagne wurde bereits ausgewertet.",
                Existing = existing, HoldoutFrom = existing.HoldoutFrom, HoldoutTo = existing.HoldoutTo
            };

        var counts = wf.Folds.Where(f => !string.IsNullOrEmpty(f.SelectedCandidate))
            .GroupBy(f => f.SelectedCandidate!)
            .ToDictionary(g => g.Key, g => g.Count());
        if (counts.Count == 0)
            return new ResearchHoldoutProposal { Available = false, UnavailableReason = "In keinem Walk-forward-Fenster wurde ein Kandidat ausgewählt." };

        string chosenId = counts
            .OrderByDescending(kv => kv.Value)
            .ThenByDescending(kv => wf.CandidateSharpes.TryGetValue(kv.Key, out var v) && v.HasValue ? v.Value : double.MinValue)
            .First().Key;

        var idToParams = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        for (int i = 0; i < cfg.Candidates.Count; i++)
            idToParams[QuantApiService.CandidateId(cfg.Candidates[i], i)] = cfg.Candidates[i];
        if (!idToParams.TryGetValue(chosenId, out var chosenParams))
            return new ResearchHoldoutProposal { Available = false, UnavailableReason = $"Ausgewählter Kandidat '{chosenId}' ist nicht auflösbar." };

        var trials = await _quant.Store.ListTrialsAsync(wf.CampaignId, ct);
        var trial = trials.FirstOrDefault(t => t.PeriodRole == "walkforward" && SameParams(t.Parameters, chosenParams));
        if (trial is null)
            return new ResearchHoldoutProposal { Available = false, UnavailableReason = "Kein gespeicherter Trial-Snapshot für den ausgewählten Kandidaten." };

        double? sharpe = wf.CandidateSharpes.TryGetValue(chosenId, out var s) ? s : null;
        string reason = $"In {counts[chosenId]} von {wf.Folds.Count} Walk-forward-Fenstern out-of-sample ausgewählt " +
                        $"(Kriterium {cfg.SelectionMetric})" +
                        (sharpe.HasValue ? $"; Sharpe/Periode ≈ {sharpe.Value:0.###}." : ".");

        DateTimeOffset? from = wf.HoldoutFromT.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(wf.HoldoutFromT.Value) : null;
        DateTimeOffset? to = wf.HoldoutToT.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(wf.HoldoutToT.Value) : null;
        bool available = from.HasValue && to.HasValue;

        return new ResearchHoldoutProposal
        {
            CandidateTrialId = trial.Id,
            CandidateReference = $"WF-Kandidat {chosenId}",
            Parameters = chosenParams,
            Reason = reason,
            HoldoutFrom = from, HoldoutTo = to,
            WarmupBars = cfg.WarmupBars,
            Available = available,
            UnavailableReason = available ? null : "Kein reservierter Holdout-Zeitraum in den geladenen Daten."
        };
    }

    // ---------------------------------------------------------------------------------------------
    // Teil-Anfragen aus der eingefrorenen Konfiguration ableiten.

    private static QuantWalkForwardRequest BuildWalkForward(ResearchStartRequest cfg) => new()
    {
        Run = cfg.Run, Options = cfg.Options, Mode = cfg.Mode,
        TrainBars = cfg.TrainBars, TestBars = cfg.TestBars, StepBars = cfg.StepBars,
        LabelSpanBars = cfg.LabelSpanBars, EmbargoBars = cfg.EmbargoBars, WarmupBars = cfg.WarmupBars,
        HoldoutFraction = cfg.HoldoutFraction, Candidates = cfg.Candidates, SelectionMetric = cfg.SelectionMetric,
        Campaign = cfg.Campaign
    };

    // Robustheit/Monte Carlo laufen auf dem ENTWICKLUNGS-Run (kein Holdout).
    private static QuantRobustnessRequest BuildRobustness(ResearchStartRequest cfg, BacktestRunRequest devRun) => new()
    {
        Run = devRun, Options = cfg.Options, Metric = cfg.RobustnessMetric
    };

    private static QuantMonteCarloRequest BuildMonteCarlo(ResearchStartRequest cfg, BacktestRunRequest devRun) => new()
    {
        Run = devRun, Options = cfg.Options, Source = cfg.MonteCarloSource, Method = cfg.MonteCarloMethod,
        Iterations = cfg.MonteCarloIterations, Seed = cfg.Seed, BlockLength = cfg.BlockLength, CapitalBarrier = cfg.CapitalBarrier
    };

    private static bool SameParams(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kv in a)
            if (!b.TryGetValue(kv.Key, out var v) || !string.Equals(v, kv.Value, StringComparison.Ordinal)) return false;
        return true;
    }

    private static ResearchRunResponse Fail(string message) => new() { Ok = false, Error = message };

    private static string Sanitize(string id)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) id = id.Replace(c, '_');
        return id.Replace(' ', '-');
    }

    /// <summary>Bildet Teil-Fortschritt [0..1] eines Schritts in das Gesamtband [from..to] ab.</summary>
    private static IProgress<double>? Scaled(IProgress<double>? outer, double from, double to)
        => outer is null ? null : new Progress<double>(p => outer.Report(from + Math.Clamp(p, 0, 1) * (to - from)));
}
