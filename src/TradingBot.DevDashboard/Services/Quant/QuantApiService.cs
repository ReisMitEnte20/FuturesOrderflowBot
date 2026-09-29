using TradingBot.Backtesting.Ohlc;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Models;
using TradingBot.Quant.Benchmark;
using TradingBot.Quant.DataQuality;
using TradingBot.Quant.Metrics;
using TradingBot.Quant.MonteCarlo;
using TradingBot.Quant.Overfitting;
using TradingBot.Quant.Registry;
using TradingBot.Quant.Research;
using TradingBot.Quant.Robustness;
using TradingBot.Quant.Series;
using TradingBot.Quant.Statistics;
using TradingBot.Quant.Validation;

namespace TradingBot.DevDashboard.Services.Quant;

/// <summary>
/// Bindeglied zwischen der bestehenden OHLC-Backtest-Engine und der Quant-Auswertung.
/// Alle Berechnungen laufen im Backend; das Dashboard stellt ausschließlich echte Ergebnisse dar.
///
/// Es werden keine Broker-Verbindungen aufgebaut und keine Orders erzeugt — die Engine bleibt
/// reine Simulation.
/// </summary>
public sealed class QuantApiService
{
    private readonly BacktestApiService _backtest;
    private readonly string _repoRoot;
    private readonly IExperimentStore _store;
    private readonly JsonPaperResearchStore _papers;
    private readonly IBenchmarkDataSource _benchmarks;
    private readonly string _benchmarkDir;
    private readonly string _registryDir;

    public QuantApiService(BacktestApiService backtest, string repoRoot)
    {
        _backtest = backtest ?? throw new ArgumentNullException(nameof(backtest));
        _repoRoot = repoRoot;
        _registryDir = Path.Combine(repoRoot, "artifacts", "quant", "registry");
        _benchmarkDir = Path.Combine(repoRoot, "data", "benchmarks");
        _store = new JsonExperimentStore(_registryDir);
        _papers = new JsonPaperResearchStore(Path.Combine(repoRoot, "artifacts", "quant", "papers"));
        // assumeTotalReturn bleibt false: ob eine Datei eine Total-Return-Reihe ist, muss der
        // Nutzer belegen. Solange das nicht bestätigt ist, warnt der Vergleich ausdrücklich.
        _benchmarks = new CsvBenchmarkDataSource(_benchmarkDir);
    }

    public IExperimentStore Store => _store;
    public JsonPaperResearchStore Papers => _papers;

    // =========================================================================================
    // Status
    // =========================================================================================

    public async Task<QuantStatusDto> GetStatusAsync(bool rithmicEnabled, CancellationToken ct = default)
    {
        var campaigns = await _store.ListCampaignsAsync(ct);
        var trials = await _store.ListTrialsAsync(null, ct);
        var benchmarks = await _benchmarks.ListAsync(ct);
        var papers = await _papers.ListAsync(ct);
        return new QuantStatusDto(_registryDir, campaigns.Count, trials.Count,
            benchmarks, _benchmarks.SourceName, _benchmarkDir, papers.Count, CodeVersion(), rithmicEnabled);
    }

    /// <summary>Aktueller Git-Commit als Codestand des Versuchs. "unknown", wenn nicht ermittelbar.</summary>
    public string CodeVersion()
    {
        try
        {
            var head = Path.Combine(_repoRoot, ".git", "HEAD");
            if (!File.Exists(head)) return "unknown";
            var content = File.ReadAllText(head).Trim();
            if (!content.StartsWith("ref:", StringComparison.Ordinal)) return content[..Math.Min(12, content.Length)];
            var refPath = Path.Combine(_repoRoot, ".git", content[4..].Trim().Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(refPath)) return File.ReadAllText(refPath).Trim()[..12];
            var packed = Path.Combine(_repoRoot, ".git", "packed-refs");
            if (!File.Exists(packed)) return "unknown";
            var name = content[4..].Trim();
            foreach (var line in File.ReadLines(packed))
                if (line.EndsWith(" " + name, StringComparison.Ordinal)) return line[..12];
            return "unknown";
        }
        catch (IOException) { return "unknown"; }
        catch (UnauthorizedAccessException) { return "unknown"; }
    }

    // =========================================================================================
    // A + B — Einzelauswertung mit Kennzahlen und Benchmark
    // =========================================================================================

    public async Task<QuantAnalyzeResponse> AnalyzeAsync(QuantAnalyzeRequest request,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            progress?.Report(0.05);
            var ctx = await _backtest.LoadContextAsync(request.Run, ct);
            if (ctx.Candles.Count == 0)
                return new QuantAnalyzeResponse { Ok = false, Error = "Keine gültigen OHLC-Bars im gewählten Zeitraum." };

            var quality = QuantDataQualityChecker.Check(ctx.Candles, ctx.Instrument.Symbol, ctx.TimeframeMinutes,
                ctx.LeadingPartial, ctx.TrailingPartial);

            progress?.Report(0.25);
            var strategy = _backtest.CreateStrategy(request.Run, ctx.Instrument);
            var result = _backtest.RunEngine(ctx, strategy, ConfigFrom(request.Run));

            var spec = new QuantContractSpec(ctx.Instrument.TickSize, ctx.Instrument.PointValue, ctx.Instrument.Currency);
            var frequency = ParseFrequency(request.Options.Frequency);
            var curve = ReturnSeriesBuilder.BuildEquityCurve(result, spec, frequency);

            progress?.Report(0.5);
            var realizedBuild = ReturnSeriesBuilder.ToReturnSeries(curve, EquityBasis.Realized);
            var totalBuild = ReturnSeriesBuilder.ToReturnSeries(curve, EquityBasis.Total);
            var options = MetricOptions(request.Options);

            var mRealized = PerformanceMetricsCalculator.Compute(realizedBuild.Series, options);
            var mTotal = PerformanceMetricsCalculator.Compute(totalBuild.Series, options);
            var activity = TradeActivityMetricsCalculator.Compute(result.Trades, curve, spec);

            var notes = new List<string>();
            notes.AddRange(realizedBuild.Notes);
            notes.AddRange(totalBuild.Notes);
            notes.AddRange(mTotal.Notes);
            if (result.Status != Backtesting.BacktestRunStatus.Completed && result.Message is not null)
                notes.Add(result.Message);

            progress?.Report(0.7);
            var rolling = totalBuild.Series.Count >= request.Options.RollingWindow
                ? RollingMetrics.Compute(totalBuild.Series, Math.Max(2, request.Options.RollingWindow), mTotal.PeriodsPerYear)
                : Array.Empty<RollingPoint>();
            if (rolling.Count == 0)
                notes.Add($"Rollierende Kennzahlen nicht berechenbar: weniger Perioden als das Fenster ({request.Options.RollingWindow}).");

            var monthly = RollingMetrics.Monthly(totalBuild.Series);

            // --- Benchmark (nur mit echten Daten) ---
            QuantBenchmarkDto? benchmarkDto = null;
            if (!string.IsNullOrWhiteSpace(request.BenchmarkId))
            {
                var bm = await _benchmarks.GetAsync(request.BenchmarkId!, curve.Start, curve.End, ct);
                var cmp = BenchmarkComparer.Compare(totalBuild.Series, bm, new BenchmarkComparisonOptions
                {
                    AnnualizationBasis = options.AnnualizationBasis,
                    FixedPeriodsPerYear = options.FixedPeriodsPerYear,
                    RiskFreeAnnualRate = options.RiskFreeAnnualRate,
                    StrategyIsFullyFunded = request.StrategyIsFullyFunded
                });
                benchmarkDto = ToDto(cmp);
            }

            progress?.Report(0.9);
            var uwRealized = RollingMetrics.Underwater(curve.Points.Select(p => (double)p.RealizedEquity).ToList());
            var uwTotal = RollingMetrics.Underwater(curve.Points.Select(p => (double)p.TotalEquity).ToList());
            var curveDto = curve.Points.Select((p, i) => new QuantCurvePointDto(
                p.Time.ToUnixTimeMilliseconds(), (double)p.RealizedEquity, (double)p.TotalEquity,
                uwRealized[i], uwTotal[i], p.OpenQuantity)).ToList();

            progress?.Report(1.0);
            return new QuantAnalyzeResponse
            {
                Ok = true,
                Symbol = ctx.Instrument.Symbol,
                Source = ctx.Source,
                TimeframeMinutes = ctx.TimeframeMinutes,
                Currency = ctx.Instrument.Currency,
                Frequency = frequency.ToString(),
                PeriodsPerYear = mTotal.PeriodsPerYear,
                AnnualizationNote = mTotal.AnnualizationNote,
                RiskFreeNote = mTotal.RiskFreeNote,
                MarkToMarketNote = curve.MarkToMarketNote,
                Trades = result.Trades.Count,
                InitialBalance = result.InitialBalance,
                FinalEquityRealized = result.FinalEquity,
                FinalEquityTotal = curve.Points.Count > 0 ? (double)curve.Points[^1].TotalEquity : (double)result.InitialBalance,
                Curve = curveDto,
                MetricsRealized = mRealized.Metrics.Select(QuantMetricDto.From).ToList(),
                MetricsTotal = mTotal.Metrics.Select(QuantMetricDto.From).ToList(),
                Activity = activity.Select(QuantMetricDto.From).ToList(),
                DrawdownRealized = ToDto(mRealized.Drawdown),
                DrawdownTotal = ToDto(mTotal.Drawdown),
                Monthly = monthly.Select(m => new QuantPeriodReturnDto(m.Period, m.Start.ToUnixTimeMilliseconds(), m.Return, m.Observations)).ToList(),
                Rolling = rolling.Select(r => new QuantRollingPointDto(r.Time.ToUnixTimeMilliseconds(), r.Sharpe, r.Volatility, r.Return)).ToList(),
                RollingWindow = request.Options.RollingWindow,
                DataQuality = QuantDataQualityDto.From(quality),
                Benchmark = benchmarkDto,
                Costs = CostDto(ctx, result),
                Notes = notes
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new QuantAnalyzeResponse { Ok = false, Error = ex.Message };
        }
    }

    // =========================================================================================
    // C — Walk-forward
    // =========================================================================================

    public async Task<QuantWalkForwardResponse> WalkForwardAsync(QuantWalkForwardRequest request,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            if (request.Candidates.Count == 0)
                return new QuantWalkForwardResponse { Ok = false, Error = "Keine Kandidaten angegeben." };
            if (request.Campaign is null)
                return new QuantWalkForwardResponse
                {
                    Ok = false,
                    Error = "Ohne Kampagne keine Suche: Versuchsbudget, Suchraum und Auswahlkriterium müssen VOR der Kampagne gespeichert werden."
                };

            // Auswahlkriterium verbindlich an das gesperrte Kampagnenkriterium koppeln — VOR Datenladen,
            // Reservierung und Auswertung. Beide Kriterien werden einheitlich normalisiert (getrimmt,
            // Kleinbuchstaben). Null/leer/Whitespace UND unbekannte Kriterien werden ausdrücklich abgelehnt
            // (kein stiller Rückfall auf Sharpe). Bei einer BEREITS gespeicherten Kampagne wird das tatsächlich
            // verwendete Kriterium DIREKT mit dem gespeicherten, gesperrten Wert verglichen — ein leeres oder
            // abweichendes request.Campaign.SelectionMetric kann den Schutz dann nicht mehr umgehen.
            QuantWalkForwardResponse Reject(string code, string message) =>
                new() { Ok = false, Error = $"Register [{code}]: {message}" };

            var allowed = string.Join(", ", KnownSelectionMetrics.OrderBy(m => m, StringComparer.Ordinal));

            var requestedMetric = NormalizeMetric(request.SelectionMetric);
            if (requestedMetric is null)
                return Reject("SELECTION_METRIC_MISSING", "Kein Auswahlkriterium im Request angegeben (leer/Whitespace).");
            if (!KnownSelectionMetrics.Contains(requestedMetric))
                return Reject("SELECTION_METRIC_UNKNOWN", $"Unbekanntes Auswahlkriterium '{requestedMetric}'. Erlaubt: {allowed}.");

            var existingCampaign = await _store.GetCampaignAsync(request.Campaign.Id, ct);
            string lockedMetric;
            if (existingCampaign is not null)
            {
                var stored = NormalizeMetric(existingCampaign.SelectionMetric);
                if (stored is null || !KnownSelectionMetrics.Contains(stored))
                    return Reject("SELECTION_METRIC_INVALID",
                        $"Die gespeicherte Kampagne '{existingCampaign.Id}' hat kein gültiges gesperrtes Auswahlkriterium " +
                        $"('{existingCampaign.SelectionMetric}').");
                lockedMetric = stored;
            }
            else
            {
                var campaignMetric = NormalizeMetric(request.Campaign.SelectionMetric);
                if (campaignMetric is null)
                    return Reject("SELECTION_METRIC_MISSING", "Kein Kampagnen-Auswahlkriterium angegeben (leer/Whitespace).");
                if (!KnownSelectionMetrics.Contains(campaignMetric))
                    return Reject("SELECTION_METRIC_UNKNOWN", $"Unbekanntes Kampagnen-Auswahlkriterium '{campaignMetric}'. Erlaubt: {allowed}.");
                lockedMetric = campaignMetric;
            }

            if (!string.Equals(requestedMetric, lockedMetric, StringComparison.Ordinal))
                return Reject("SELECTION_METRIC_MISMATCH",
                    $"Das tatsächlich angeforderte Auswahlkriterium '{requestedMetric}' widerspricht dem " +
                    $"{(existingCampaign is not null ? "gespeicherten, gesperrten" : "angegebenen")} Kampagnenkriterium '{lockedMetric}'. " +
                    "Die Suche wird nicht gestartet (keine Versuche reserviert, keine Auswertung) — das Kriterium wird vor der " +
                    "Kampagne festgelegt und nicht stillschweigend geändert.");

            // Ab hier ausschließlich die normalisierten Werte verwenden — die tatsächlich ausgeführte Auswahl
            // entspricht exakt dem verglichenen/gesperrten Kriterium (kein stiller Rückfall auf Sharpe).
            request = request with
            {
                SelectionMetric = requestedMetric,
                Campaign = request.Campaign with { SelectionMetric = lockedMetric }
            };

            var ctx = await _backtest.LoadContextAsync(request.Run, ct);
            if (ctx.Candles.Count == 0)
                return new QuantWalkForwardResponse { Ok = false, Error = "Keine gültigen OHLC-Bars im gewählten Zeitraum." };

            var barTimes = ctx.Candles.Select(c => c.CloseTime).ToList();
            var plan = WalkForwardPlanner.Plan(barTimes, new WalkForwardOptions
            {
                Mode = string.Equals(request.Mode, "Anchored", StringComparison.OrdinalIgnoreCase)
                    ? TradingBot.Quant.Validation.WalkForwardMode.Anchored
                    : TradingBot.Quant.Validation.WalkForwardMode.Rolling,
                TrainBars = request.TrainBars,
                TestBars = request.TestBars,
                StepBars = request.StepBars,
                LabelSpanBars = request.LabelSpanBars,
                EmbargoBars = request.EmbargoBars,
                WarmupBars = request.WarmupBars,
                HoldoutFraction = request.HoldoutFraction
            });

            if (plan.IsEmpty)
                return new QuantWalkForwardResponse
                {
                    Ok = false,
                    Error = "Kein vollständiges Walk-forward-Fenster möglich.",
                    Notes = plan.Notes,
                    TotalBars = plan.TotalBars
                };

            var spec = new QuantContractSpec(ctx.Instrument.TickSize, ctx.Instrument.PointValue, ctx.Instrument.Currency);
            var candidates = request.Candidates
                .Select((p, i) => new ParameterCandidate(CandidateId(p, i), p))
                .ToList();

            var evalOptions = MetricOptions(request.Options);
            var frequency = ParseFrequency(request.Options.Frequency);

            // Datenbezug und Kostenprofil VOR der Ausführung bestimmen — sie sperren die Kampagne und
            // beschreiben jeden Versuch reproduzierbar.
            var fingerprint = DataFingerprint.Compute(ctx.Candles, ctx.Instrument.Symbol, ctx.TimeframeMinutes, ctx.Source);
            var costs = CostSnapshot(ctx, request);
            string code = CodeVersion();

            // Die Kampagne wird erst NACH der Aufteilung angelegt, damit der reservierte Holdout-Zeitraum
            // im Register steht. Existiert sie bereits, werden gesperrte Angaben (Suchraum, Auswahlkriterium,
            // Datenbezug, Holdout, Budget) geprüft und Abweichungen NICHT stillschweigend übernommen.
            CampaignRecord campaign;
            try
            {
                campaign = await EnsureCampaignAsync(request.Campaign, request.Candidates.Count,
                    plan.Holdout?.FromTime, plan.Holdout?.ToTime, fingerprint.Sha256, ct);
            }
            catch (ExperimentRegistryException ex)
            {
                return new QuantWalkForwardResponse { Ok = false, Error = $"Register [{ex.Code}]: {ex.Message}" };
            }

            // Versuche ATOMAR und mit EINDEUTIGER Ausführungs-Id VOR dem Lauf reservieren. Eine neue
            // Ausführung überschreibt keine abgeschlossenen Ergebnisse; ein erschöpftes Budget verhindert
            // den Start (der Registerfehler wird sichtbar gemeldet).
            string runId = $"run{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}".Substring(0, 28);
            var trialByCandidate = candidates.ToDictionary(c => c.Id, c => $"{campaign.Id}-{runId}-{c.Id}");
            var reservations = candidates.Select(c => new TrialRecord
            {
                Id = trialByCandidate[c.Id],
                CampaignId = campaign.Id,
                StrategyId = request.Run.Strategy,
                StrategyVersion = code,
                Origin = StrategyOrigin.Manual,
                OriginReference = $"Walk-forward-Kampagne im Dashboard (Ausführung {runId})",
                Parameters = c.Parameters,
                Data = fingerprint,
                Costs = costs,
                // Geprüfte Ausführungskonfiguration dauerhaft festhalten — die finale Holdout-Auswertung bindet
                // sich später vollständig an diesen Snapshot (keine stille Ergänzung aus UI-Werten). SL/TP werden
                // EFFEKTIV eingefroren (Profil-Default bereits aufgelöst), nicht als nullable Request-Wert: sonst
                // würde ein später geänderter InstrumentProfile-Default die Holdout-Ausführung unbemerkt verändern.
                Execution = new ExecutionConfigSnapshot
                {
                    Quantity = request.Run.Quantity,
                    InitialCapital = request.Run.InitialBalance,
                    StopLossTicks = request.Run.StopLossTicks ?? ctx.Instrument.DefaultStopLossTicks,
                    TakeProfitTicks = request.Run.TakeProfitTicks ?? ctx.Instrument.DefaultTakeProfitTicks,
                    ApplyFees = request.Run.ApplyFees,
                    TimeframeMinutes = ctx.TimeframeMinutes
                },
                CodeVersion = code,
                Seed = 0,
                PeriodFrom = ctx.Candles[0].OpenTime,
                PeriodTo = plan.Holdout?.FromTime ?? ctx.Candles[^1].CloseTime,
                PeriodRole = "walkforward",
                Status = TrialStatus.Running,
                Tags = new[] { "walkforward", request.SelectionMetric }
            }).ToList();

            try
            {
                await _store.ReserveTrialsAsync(campaign.Id, reservations, ct);
            }
            catch (ExperimentRegistryException ex)
            {
                return new QuantWalkForwardResponse { Ok = false, Error = $"Register [{ex.Code}]: {ex.Message} — der Lauf wurde nicht gestartet." };
            }

            Task<SegmentOutcome> Evaluate(IReadOnlyDictionary<string, string> parameters,
                IReadOnlyList<DataSplit> segments, SplitRole role, CancellationToken token)
            {
                try
                {
                    var series = new List<ReturnSeries>();
                    int trades = 0;
                    decimal net = 0;
                    foreach (var seg in segments)
                    {
                        token.ThrowIfCancellationRequested();
                        if (seg.Count < 2) continue;
                        var slice = ctx.Candles.Skip(seg.Start).Take(seg.Count).ToList();
                        IStrategy strat = _backtest.CreateStrategy(request.Run with { Params = new Dictionary<string, string>(parameters) }, ctx.Instrument);
                        // Ausgewiesener Warmup wird durchgesetzt: in den ersten Bars jedes Abschnitts keine Ausführung.
                        if (request.WarmupBars > 0) strat = new WarmupGuardStrategy(strat, request.WarmupBars);
                        var res = _backtest.RunEngine(ctx, strat, ConfigFrom(request.Run), candlesOverride: slice);
                        trades += res.Trades.Count;
                        net += res.Statistics.NetProfit;
                        var curve = ReturnSeriesBuilder.BuildEquityCurve(res, spec, frequency);
                        var built = ReturnSeriesBuilder.ToReturnSeries(curve, EquityBasis.Total);
                        if (built.Series.Count > 0) series.Add(built.Series);
                    }

                    if (series.Count == 0)
                        return Task.FromResult(SegmentOutcome.Failed("Keine auswertbaren Perioden im Abschnitt."));

                    var combined = Concat(series);
                    var metrics = PerformanceMetricsCalculator.Compute(combined, evalOptions);
                    double? selection = SelectionValue(request.SelectionMetric, metrics, net);

                    return Task.FromResult(new SegmentOutcome
                    {
                        SelectionValue = selection,
                        TradeCount = trades,
                        Returns = combined,
                        Metrics = metrics.Metrics.ToDictionary(m => m.Key, m => m.IsAvailable ? m.Value : null)
                    });
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { return Task.FromResult(SegmentOutcome.Failed(ex.Message)); }
            }

            WalkForwardRunResult run;
            try
            {
                run = await WalkForwardRunner.RunAsync(plan, candidates, Evaluate, request.SelectionMetric,
                    SelectionDirection.HigherIsBetter, progress, ct);
            }
            catch (OperationCanceledException)
            {
                // Abbruch dauerhaft erfassen: die reservierten Versuche werden als Failed (Abbruch) finalisiert.
                await MarkReservationsCancelledAsync(campaign.Id, trialByCandidate.Values, ct);
                throw;
            }

            // --- Reservierte Versuche mit Ergebnissen finalisieren (auch die schlechten) ---
            var finalizeNotes = new List<string>();
            int recorded = await FinalizeTrialsAsync(campaign.Id, trialByCandidate, request, candidates, run, finalizeNotes, ct);

            var oosMetrics = PerformanceMetricsCalculator.Compute(run.OutOfSampleReturns, evalOptions);

            var candidateSharpes = new Dictionary<string, double?>();
            foreach (var c in candidates)
            {
                var series = run.CandidateTestReturns.TryGetValue(c.Id, out var r) ? r : Array.Empty<double>();
                candidateSharpes[c.Id] = CscvPbo.SharpePerPeriod(series);
            }

            return new QuantWalkForwardResponse
            {
                Ok = true,
                SelectionMetric = request.SelectionMetric,
                Mode = plan.Options.Mode.ToString(),
                TotalBars = plan.TotalBars,
                Folds = run.Folds.Select(f => new QuantFoldDto(
                    f.Fold.Index,
                    f.Fold.Train.Count > 0 ? f.Fold.Train[0].FromTime.ToUnixTimeMilliseconds() : 0,
                    f.Fold.Train.Count > 0 ? f.Fold.Train[^1].ToTime.ToUnixTimeMilliseconds() : 0,
                    f.Fold.TrainBars,
                    f.Fold.Test.FromTime.ToUnixTimeMilliseconds(),
                    f.Fold.Test.ToTime.ToUnixTimeMilliseconds(),
                    f.Fold.Test.Count,
                    f.Fold.PurgedBars, f.Fold.EmbargoBars,
                    f.Selected?.Id, f.TrainSelectionValue, f.Test?.SelectionValue, f.Test?.TradeCount ?? 0, f.Note)).ToList(),
                HoldoutFromT = plan.Holdout?.FromTime.ToUnixTimeMilliseconds(),
                HoldoutToT = plan.Holdout?.ToTime.ToUnixTimeMilliseconds(),
                HoldoutEvaluated = false,
                OosT = run.OutOfSampleReturns.Timestamps.Select(t => t.ToUnixTimeMilliseconds()).ToList(),
                OosEquity = run.OutOfSampleReturns.EquityLevels,
                OosMetrics = oosMetrics.Metrics.Select(QuantMetricDto.From).ToList(),
                CandidateSharpes = candidateSharpes,
                CampaignId = campaign.Id,
                TrialsRecorded = recorded,
                Notes = run.Notes.Concat(finalizeNotes).ToList()
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new QuantWalkForwardResponse { Ok = false, Error = ex.Message };
        }
    }

    // =========================================================================================
    // D — Monte Carlo
    // =========================================================================================

    public async Task<QuantMonteCarloResponse> MonteCarloAsync(QuantMonteCarloRequest request,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var ctx = await _backtest.LoadContextAsync(request.Run, ct);
            if (ctx.Candles.Count == 0)
                return new QuantMonteCarloResponse { Ok = false, Error = "Keine gültigen OHLC-Bars im gewählten Zeitraum." };

            var strategy = _backtest.CreateStrategy(request.Run, ctx.Instrument);
            var result = _backtest.RunEngine(ctx, strategy, ConfigFrom(request.Run));
            var spec = new QuantContractSpec(ctx.Instrument.TickSize, ctx.Instrument.PointValue, ctx.Instrument.Currency);
            var frequency = ParseFrequency(request.Options.Frequency);
            var curve = ReturnSeriesBuilder.BuildEquityCurve(result, spec, frequency);
            var totalSeries = ReturnSeriesBuilder.ToReturnSeries(curve, EquityBasis.Total).Series;

            bool useTrades = string.Equals(request.Source, "trades", StringComparison.OrdinalIgnoreCase);
            var observations = useTrades
                ? result.Trades.Select(t => (double)t.NetPnL).ToList()
                : totalSeries.Returns.ToList();

            var options = new MonteCarloOptions
            {
                Method = ParseMethod(request.Method),
                Accumulation = useTrades ? AccumulationMode.Additive : AccumulationMode.Multiplicative,
                Iterations = Math.Clamp(request.Iterations, 1, 100_000),
                Seed = request.Seed,
                BlockLength = request.BlockLength,
                Horizon = request.Horizon,
                InitialCapital = (double)result.InitialBalance,
                CapitalBarrier = request.CapitalBarrier,
                TimeLimit = TimeSpan.FromMinutes(5)
            };

            var notes = new List<string>();
            MonteCarloResult mc;

            if (!string.IsNullOrWhiteSpace(request.JointBenchmarkId) && !useTrades)
            {
                var bm = await _benchmarks.GetAsync(request.JointBenchmarkId!, curve.Start, curve.End, ct);
                if (bm is null)
                {
                    notes.Add($"Benchmark '{request.JointBenchmarkId}' nicht gefunden — gemeinsames Resampling entfällt, " +
                              "es wird ausschließlich die Strategiereihe simuliert.");
                    mc = MonteCarloEngine.Run(observations, options, progress, ct);
                }
                else
                {
                    var bmSeries = BenchmarkComparer.ToReturnSeries(bm, frequency);
                    var aligned = ReturnSeriesBuilder.AlignOnCommonTimestamps(new[] { totalSeries, bmSeries });
                    if (aligned[0].Count < 2)
                    {
                        notes.Add("Zu wenige gemeinsame Perioden mit der Benchmark — gemeinsames Resampling entfällt.");
                        mc = MonteCarloEngine.Run(observations, options, progress, ct);
                    }
                    else
                    {
                        var results = MonteCarloEngine.RunJointly(
                            new IReadOnlyList<double>[] { aligned[0].Returns, aligned[1].Returns },
                            new[] { "Strategie", bm.Name }, options, progress, ct);
                        mc = results[0];
                        notes.Add($"Gemeinsames Resampling mit '{bm.Name}' über {aligned[0].Count} gemeinsame Perioden — " +
                                  "die Abhängigkeit zwischen Strategie und Benchmark bleibt dabei erhalten.");
                    }
                }
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(request.JointBenchmarkId) && useTrades)
                    notes.Add("Gemeinsames Resampling mit einer Benchmark ist nur auf zeitlich ausgerichteten " +
                              "Renditereihen sinnvoll, nicht auf Trade-Beträgen — es wurde nicht durchgeführt.");
                mc = MonteCarloEngine.Run(observations, options, progress, ct);
            }

            notes.AddRange(mc.Notes);

            return new QuantMonteCarloResponse
            {
                Ok = true,
                Method = MonteCarloEngine.MethodLabel(options.Method),
                SourceLabel = useTrades
                    ? $"NetPnL von {observations.Count} abgeschlossenen Trades (additiv)"
                    : $"{observations.Count} {PerformanceMetricsCalculator.FrequencyLabel(frequency)}-Renditen (multiplikativ)",
                Iterations = mc.CompletedIterations,
                Seed = options.Seed,
                BlockLength = mc.EffectiveBlockLength,
                Horizon = mc.EffectiveHorizon,
                Observations = mc.ObservationCount,
                FinalCapital = ToDto(mc.FinalCapital),
                MaxDrawdown = ToDto(mc.MaxDrawdown),
                LosingStreak = ToDto(mc.LongestLosingStreak),
                ShareOfRunsBelowStart = mc.ShareOfRunsBelowStart,
                ShareOfRunsBreachingBarrier = mc.ShareOfRunsBreachingBarrier,
                CapitalBarrier = request.CapitalBarrier,
                Assumptions = mc.Assumptions,
                Notes = notes
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new QuantMonteCarloResponse { Ok = false, Error = ex.Message };
        }
    }

    // =========================================================================================
    // D — Robustheit / Stress
    // =========================================================================================

    public async Task<QuantRobustnessResponse> RobustnessAsync(QuantRobustnessRequest request,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var ctx = await _backtest.LoadContextAsync(request.Run, ct);
            if (ctx.Candles.Count == 0)
                return new QuantRobustnessResponse { Ok = false, Error = "Keine gültigen OHLC-Bars im gewählten Zeitraum." };

            var spec = new QuantContractSpec(ctx.Instrument.TickSize, ctx.Instrument.PointValue, ctx.Instrument.Currency);
            var frequency = ParseFrequency(request.Options.Frequency);
            var evalOptions = MetricOptions(request.Options);

            (double? value, int trades, string? error) Evaluate(StressScenario s)
            {
                try
                {
                    var fee = ctx.Fee with
                    {
                        CommissionPerSide = ctx.Fee.CommissionPerSide * (decimal)s.FeeMultiplier,
                        ExchangeFeePerSide = ctx.Fee.ExchangeFeePerSide * (decimal)s.FeeMultiplier,
                        ClearingFeePerSide = ctx.Fee.ClearingFeePerSide * (decimal)s.FeeMultiplier,
                        RoutingFeePerSide = ctx.Fee.RoutingFeePerSide * (decimal)s.FeeMultiplier,
                        NfaFeePerSide = ctx.Fee.NfaFeePerSide * (decimal)s.FeeMultiplier,
                        OtherFeePerSide = ctx.Fee.OtherFeePerSide * (decimal)s.FeeMultiplier,
                        EstimatedSlippageTicks = ctx.Fee.EstimatedSlippageTicks * (decimal)s.SlippageMultiplier
                    };

                    var runReq = s.Parameters.Count > 0
                        ? request.Run with { Params = new Dictionary<string, string>(s.Parameters) }
                        : request.Run;

                    IStrategy strat = _backtest.CreateStrategy(runReq, ctx.Instrument);
                    if (s.ExecutionDelayBars > 0) strat = new DelayedSignalStrategy(strat, s.ExecutionDelayBars);

                    var res = _backtest.RunEngine(ctx, strat, ConfigFrom(runReq) with { SlippageTicksOverride = null }, feeOverride: fee);
                    var curve = ReturnSeriesBuilder.BuildEquityCurve(res, spec, frequency);
                    var series = ReturnSeriesBuilder.ToReturnSeries(curve, EquityBasis.Total).Series;
                    if (series.Count < 2) return (null, res.Trades.Count, "Zu wenige Perioden für eine Kennzahl.");
                    var metrics = PerformanceMetricsCalculator.Compute(series, evalOptions);
                    return (SelectionValue(request.Metric, metrics, res.Statistics.NetProfit), res.Trades.Count, null);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { return (null, 0, ex.Message); }
            }

            var baselineScenario = new StressScenario { Id = "baseline", Label = "Ausgangsfall", Dimension = StressDimension.Costs };
            var (baseline, baseTrades, baseError) = Evaluate(baselineScenario);

            var blocks = new List<QuantStressBlockDto>();
            var notes = new List<string>();
            if (baseError is not null) notes.Add($"Ausgangsfall nicht auswertbar: {baseError}");

            var costScenarios = StressAnalysis.CostGrid(request.FeeMultipliers, request.SlippageMultipliers);
            var delayScenarios = StressAnalysis.ExecutionDelays(request.ExecutionDelays);
            var paramScenarios = StressAnalysis.ParameterNeighborhood(request.Run.Params ?? new Dictionary<string, string>(), request.ParameterOffsets);
            int total = costScenarios.Count + delayScenarios.Count + paramScenarios.Count;
            int done = 0;

            foreach (var (dimension, scenarios) in new (StressDimension, IReadOnlyList<StressScenario>)[]
                     {
                         (StressDimension.Costs, costScenarios),
                         (StressDimension.ExecutionDelay, delayScenarios),
                         (StressDimension.ParameterNeighborhood, paramScenarios)
                     })
            {
                var outcomes = new List<StressOutcome>();
                foreach (var s in scenarios)
                {
                    ct.ThrowIfCancellationRequested();
                    var (v, t, e) = Evaluate(s);
                    outcomes.Add(new StressOutcome { Scenario = s, Value = v, TradeCount = t, Error = e });
                    progress?.Report(++done / (double)total);
                }
                var summary = StressAnalysis.Summarize(request.Metric, dimension, outcomes, baseline);
                blocks.Add(new QuantStressBlockDto(dimension.ToString(), request.Metric, baseline,
                    summary.Worst, summary.ShareBelowBaseline, summary.RelativeDegradation,
                    outcomes.Select(o => new QuantStressCellDto(o.Scenario.Id, o.Scenario.Label,
                        o.Scenario.FeeMultiplier, o.Scenario.SlippageMultiplier, o.Scenario.ExecutionDelayBars,
                        o.Value, o.TradeCount, o.Error)).ToList(),
                    summary.Notes));
            }

            _ = baseTrades;
            return new QuantRobustnessResponse
            {
                Ok = true, Metric = request.Metric, Baseline = baseline, Blocks = blocks, Notes = notes
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new QuantRobustnessResponse { Ok = false, Error = ex.Message };
        }
    }

    // =========================================================================================
    // E — PBO / PSR / DSR
    // =========================================================================================

    public async Task<QuantOverfittingResponse> OverfittingAsync(QuantOverfittingRequest request,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var wf = await WalkForwardAsync(request.WalkForward, progress, ct);
            if (!wf.Ok)
                return new QuantOverfittingResponse { Ok = false, Error = wf.Error, WalkForward = wf };

            // Renditematrix aus den Out-of-Sample-Reihen ALLER Kandidaten.
            var ctx = await _backtest.LoadContextAsync(request.WalkForward.Run, ct);
            var candidateIds = wf.CandidateSharpes.Keys.ToList();

            var rebuilt = await RebuildCandidateMatrixAsync(request.WalkForward, ctx, ct);
            var wfRun = rebuilt.Series;
            var notes = new List<string>(wf.Notes);
            notes.AddRange(rebuilt.Notes);

            QuantOverfittingResponse WithoutPbo(string reason) => new()
            {
                Ok = true, WalkForward = wf, PboUnavailableReason = reason, Notes = notes,
                Candidates = candidateIds.Count
            };

            // Befund-6-Nachprüfung: Musste die Zeitstempel-Ausrichtung die gemeinsame Datenbasis verkürzen
            // (ein Kandidat brach vorzeitig ab: Kapital ≤ 0) oder fiel ein Kandidat mit einem Fehler aus, so
            // wird PBO NACHVOLLZIEHBAR als nicht berechenbar gemeldet — nicht auf einer stillschweigend
            // verkürzten Basis gerechnet. PSR/DSR bleiben davon unberührt (getrennte Reihe), werden hier aber
            // — wie bei den übrigen „zu wenig Basis"-Fällen — bewusst nicht ausgewiesen.
            var pboBlocked = CandidateMatrixAligner.PboBlockedReason(rebuilt.DroppedPeriods, rebuilt.Errors, rebuilt.Truncations);
            if (pboBlocked is not null) return WithoutPbo(pboBlocked);

            if (wfRun.Count == 0) return WithoutPbo("Keine Kandidaten-Renditereihen verfügbar.");

            int rows = wfRun.Min(c => c.Value.Count);
            if (rows < 4) return WithoutPbo($"Nur {rows} gemeinsame Out-of-Sample-Perioden je Kandidat — zu wenig für CSCV.");

            var ordered = wfRun.OrderBy(k => k.Key, StringComparer.Ordinal).ToList();
            var matrix = new List<IReadOnlyList<double>>(rows);
            for (int i = 0; i < rows; i++)
                matrix.Add(ordered.Select(c => c.Value[i]).ToArray());

            var pbo = CscvPbo.Compute(matrix, request.Blocks, ct: ct);

            // --- PSR / DSR auf der ausgewählten Out-of-Sample-Reihe ---
            var selectedReturns = new List<double>();
            for (int i = 1; i < wf.OosEquity.Count; i++)
                if (wf.OosEquity[i - 1] > 0) selectedReturns.Add(wf.OosEquity[i] / wf.OosEquity[i - 1] - 1.0);
            if (wf.OosEquity.Count > 0) selectedReturns.Insert(0, wf.OosEquity[0] - 1.0);

            var psr = ProbabilisticSharpe.Compute(selectedReturns);

            // --- DSR-Versuchsgrundlage: die VOLLE relevante Kampagnenhistorie, nicht nur der aktuelle Request ---
            IReadOnlyList<TrialRecord> campaignTrials = wf.CampaignId is not null
                ? await _store.ListTrialsAsync(wf.CampaignId, ct)
                : Array.Empty<TrialRecord>();

            List<double> trialSharpes;
            if (campaignTrials.Count > 0)
            {
                trialSharpes = campaignTrials
                    .Select(t => t.Metrics.TryGetValue("test.sharpe_per_period", out var v) ? v : null)
                    .Where(v => v.HasValue).Select(v => v!.Value).ToList();
                notes.Add($"DSR-Versuchsgrundlage: {trialSharpes.Count} von {campaignTrials.Count} im Register erfassten " +
                          $"Versuchen der Kampagne '{wf.CampaignId}' mit gültigem Sharpe (gesamte relevante Historie, " +
                          "nicht nur der aktuelle Request).");
            }
            else
            {
                trialSharpes = wf.CandidateSharpes.Values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
                notes.Add("DSR-Versuchsgrundlage: nur die Kandidaten des aktuellen Requests — keine Kampagnenhistorie " +
                          "im Register gefunden. Die Versuchszahl beschreibt nicht notwendigerweise die vollständige Kampagne.");
            }

            double? effective = null;
            string? rationale = null;
            if (request.EstimateEffectiveTrials)
            {
                // Die Korrelationsheuristik braucht die Renditereihen der Versuche. Sie liegen nur für die
                // Kandidaten des aktuellen Requests vor. Nur wenn die erfasste Versuchszahl exakt diesen
                // Kandidaten entspricht, wird die effektive Zahl darüber reduziert; sonst konservativ
                // die tatsächliche Zahl (keine unbelegte Reduktion über eine unvollständige Korrelationsbasis).
                if (campaignTrials.Count == 0 || campaignTrials.Count == ordered.Count)
                {
                    var (eff, why) = ProbabilisticSharpe.EstimateEffectiveTrials(ordered.Select(o => (IReadOnlyList<double>)o.Value).ToList());
                    effective = eff;
                    rationale = why;
                }
                else
                {
                    rationale = $"Effektive Versuchszahl nicht über die Korrelationsheuristik reduziert: die Kampagne umfasst " +
                                $"{campaignTrials.Count} Versuche, aber nur die {ordered.Count} Kandidaten des aktuellen Requests " +
                                "liegen als Renditereihen für eine Korrelationsschätzung vor — es wird konservativ die tatsächliche Zahl angesetzt.";
                }
            }
            var dsr = ProbabilisticSharpe.ComputeDeflated(selectedReturns, trialSharpes, effective, rationale);

            return new QuantOverfittingResponse
            {
                Ok = true,
                Pbo = pbo.Pbo,
                PboUnavailableReason = pbo.UnavailableReason,
                Candidates = pbo.Candidates,
                Blocks = pbo.Blocks,
                Combinations = pbo.Combinations,
                Observations = pbo.Observations,
                ShareNegativeOutOfSample = pbo.ShareNegativeOutOfSample,
                Pairs = pbo.Pairs.Select(p => new QuantPboPairDto(p.InSample, p.OutOfSample)).ToList(),
                Logits = pbo.Trials.Select(t => t.Logit).ToList(),
                PboDefinitions = pbo.Definitions,
                PboNotes = pbo.Notes,
                Psr = psr.Psr,
                ObservedSharpePerPeriod = psr.ObservedSharpePerPeriod,
                Skewness = psr.Skewness,
                Kurtosis = psr.Kurtosis,
                MinimumTrackRecordLength = psr.MinimumTrackRecordLength,
                PsrUnavailableReason = psr.UnavailableReason,
                PsrDefinitions = psr.Definitions,
                Dsr = dsr.Dsr,
                ExpectedMaxSharpeUnderNull = dsr.ExpectedMaxSharpeUnderNull,
                ActualTrials = dsr.ActualTrials,
                EffectiveTrials = dsr.EffectiveTrials,
                EffectiveTrialsRationale = dsr.EffectiveTrialsRationale,
                DsrUnavailableReason = dsr.UnavailableReason,
                DsrDefinitions = dsr.Definitions,
                DsrWarnings = dsr.Warnings,
                WalkForward = wf,
                Notes = notes
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new QuantOverfittingResponse { Ok = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Ergebnis der Matrix-Rekonstruktion: zeitlich ausgerichtete Kandidatenreihen plus Hinweise.
    /// <see cref="DroppedPeriods"/> zählt die Perioden, die die Zeitstempel-Ausrichtung verwerfen musste
    /// (vorzeitiger Abbruch/Kapital ≤ 0); <see cref="Errors"/> hält Kandidaten fest, die mit einem Fehler
    /// ausfielen. Beide entscheiden, ob PBO überhaupt berechenbar ist (Befund-6-Nachprüfung).
    /// </summary>
    private sealed record CandidateMatrix(Dictionary<string, IReadOnlyList<double>> Series,
        IReadOnlyList<string> Notes, int DroppedPeriods, IReadOnlyList<string> Errors, IReadOnlyList<string> Truncations);

    /// <summary>
    /// Baut die Kandidaten-Renditematrix erneut auf — dieselbe Aufteilung, dieselben Kandidaten.
    /// Deterministisch, deshalb identisch zum Walk-forward-Lauf.
    ///
    /// Wichtig für PBO: Die Reihen werden NICHT positional auf gleiche Länge gekürzt (gleiche Länge ≠
    /// gleiche Beobachtungsintervalle). Stattdessen werden je Fold nur die Zeitstempel behalten, die bei
    /// ALLEN Kandidaten vorkommen. Bricht ein Kandidat in einem Fold vorzeitig ab (Kapital ≤ 0, keine
    /// Perioden), gehen dessen fehlende Perioden für alle Kandidaten dieses Folds nicht ein — und die
    /// späteren Werte anderer Kandidaten werden nie gegen frühere Zeiträume verglichen. Weggefallene
    /// Perioden werden ausdrücklich vermerkt (keine stillschweigende Entfernung).
    /// </summary>
    private async Task<CandidateMatrix> RebuildCandidateMatrixAsync(
        QuantWalkForwardRequest request, BacktestApiService.RunContext ctx, CancellationToken ct)
    {
        var spec = new QuantContractSpec(ctx.Instrument.TickSize, ctx.Instrument.PointValue, ctx.Instrument.Currency);
        var frequency = ParseFrequency(request.Options.Frequency);
        var barTimes = ctx.Candles.Select(c => c.CloseTime).ToList();
        var plan = WalkForwardPlanner.Plan(barTimes, new WalkForwardOptions
        {
            Mode = string.Equals(request.Mode, "Anchored", StringComparison.OrdinalIgnoreCase)
                ? TradingBot.Quant.Validation.WalkForwardMode.Anchored
                : TradingBot.Quant.Validation.WalkForwardMode.Rolling,
            TrainBars = request.TrainBars,
            TestBars = request.TestBars,
            StepBars = request.StepBars,
            LabelSpanBars = request.LabelSpanBars,
            EmbargoBars = request.EmbargoBars,
            WarmupBars = request.WarmupBars,
            HoldoutFraction = request.HoldoutFraction
        });

        var ids = new List<string>();
        for (int i = 0; i < request.Candidates.Count; i++)
            ids.Add(CandidateId(request.Candidates[i], i));

        // Je Fold die zeitstempelbehaftete Renditereihe JEDES Kandidaten sammeln; die zeitliche
        // Ausrichtung übernimmt der CandidateMatrixAligner (getrennt getestet). Ein Kandidat, der in einem
        // Fold mit einem Fehler ausfällt, wird als leere Reihe geführt UND als Fehler vermerkt — beides
        // führt dazu, dass PBO als nicht berechenbar gemeldet wird, statt auf verkürzter Basis zu rechnen.
        var folds = new List<IReadOnlyList<CandidateFoldSeries>>();
        var errors = new List<string>();
        int foldIndex = 0;
        foreach (var fold in plan.Folds)
        {
            ct.ThrowIfCancellationRequested();
            var slice = ctx.Candles.Skip(fold.Test.Start).Take(fold.Test.Count).ToList();

            var perCandidate = new List<CandidateFoldSeries>(ids.Count);
            for (int i = 0; i < request.Candidates.Count; i++)
            {
                try
                {
                    IStrategy strat = _backtest.CreateStrategy(request.Run with { Params = new Dictionary<string, string>(request.Candidates[i]) }, ctx.Instrument);
                    if (request.WarmupBars > 0) strat = new WarmupGuardStrategy(strat, request.WarmupBars);
                    var res = _backtest.RunEngine(ctx, strat, ConfigFrom(request.Run), candlesOverride: slice);
                    var curve = ReturnSeriesBuilder.BuildEquityCurve(res, spec, frequency);
                    // Das Truncated-Flag aus ToReturnSeries wird ERHALTEN und ausgewertet: es ist der einzige
                    // Abbruch-Nachweis, wenn alle Kandidaten eines Folds zum gleichen Zeitpunkt abbrechen.
                    var built = ReturnSeriesBuilder.ToReturnSeries(curve, EquityBasis.Total);
                    perCandidate.Add(new CandidateFoldSeries(ids[i], built.Series.Timestamps, built.Series.Returns, built.Truncated));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    errors.Add($"Kandidat '{ids[i]}' in Fold {foldIndex}: Auswertungsfehler ({ex.Message}).");
                    perCandidate.Add(new CandidateFoldSeries(ids[i], Array.Empty<DateTimeOffset>(), Array.Empty<double>(), Truncated: false));
                }
            }
            folds.Add(perCandidate);
            foldIndex++;
        }

        var (aligned, notes, droppedPeriods, truncations) = CandidateMatrixAligner.AlignAndDetectAborts(ids, folds);
        var allNotes = notes.ToList();
        allNotes.AddRange(errors);
        allNotes.AddRange(truncations);
        return new CandidateMatrix(new Dictionary<string, IReadOnlyList<double>>(aligned), allNotes, droppedPeriods, errors, truncations);
    }

    // =========================================================================================
    // Register / Kampagnen
    // =========================================================================================

    private CostProfileSnapshot CostSnapshot(BacktestApiService.RunContext ctx, QuantWalkForwardRequest request) => new()
    {
        FeePerSide = ctx.Fee.CommissionPerSide + ctx.Fee.ExchangeFeePerSide + ctx.Fee.ClearingFeePerSide
                     + ctx.Fee.RoutingFeePerSide + ctx.Fee.NfaFeePerSide + ctx.Fee.OtherFeePerSide,
        SlippageTicks = ctx.Fee.EstimatedSlippageTicks,
        TickSize = ctx.Instrument.TickSize,
        PointValue = ctx.Instrument.PointValue,
        ApplyFees = request.Run.ApplyFees,
        Currency = ctx.Instrument.Currency,
        IsExampleProfile = ctx.FeeIsExample || ctx.InstrumentIsExample
    };

    private async Task<CampaignRecord> EnsureCampaignAsync(CampaignInput input, int candidateCount,
        DateTimeOffset? holdoutFrom, DateTimeOffset? holdoutTo, string dataSha, CancellationToken ct)
    {
        var existing = await _store.GetCampaignAsync(input.Id, ct);
        if (existing is not null)
        {
            // Gesperrte Kampagnenbedingungen dürfen sich nicht stillschweigend ändern.
            var mismatches = new List<string>();
            if (!string.IsNullOrWhiteSpace(input.SelectionMetric)
                && !string.Equals(existing.SelectionMetric, input.SelectionMetric, StringComparison.OrdinalIgnoreCase))
                mismatches.Add($"Auswahlkriterium (gesperrt: '{existing.SelectionMetric}', angefragt: '{input.SelectionMetric}')");
            if (input.TrialBudget > 0 && existing.TrialBudget != input.TrialBudget)
                mismatches.Add($"Versuchsbudget (gesperrt: {existing.TrialBudget}, angefragt: {input.TrialBudget})");
            if (!string.IsNullOrWhiteSpace(input.SearchSpace)
                && !string.Equals(existing.SearchSpace, input.SearchSpace, StringComparison.Ordinal))
                mismatches.Add("Suchraum");
            if (existing.HoldoutFrom?.UtcDateTime != holdoutFrom?.UtcDateTime
                || existing.HoldoutTo?.UtcDateTime != holdoutTo?.UtcDateTime)
                mismatches.Add("Holdout-Zeitraum");
            if (!string.IsNullOrEmpty(existing.DataSha)
                && !string.Equals(existing.DataSha, dataSha, StringComparison.OrdinalIgnoreCase))
                mismatches.Add("Datenbezug (Fingerabdruck weicht ab)");
            if (mismatches.Count > 0)
                throw new ExperimentRegistryException("CAMPAIGN_LOCKED_MISMATCH",
                    $"Kampagne '{existing.Id}' ist gesperrt; folgende Angaben weichen ab und werden nicht " +
                    $"stillschweigend übernommen: {string.Join("; ", mismatches)}. Für geänderte Bedingungen eine neue Kampagne anlegen.");
            return existing;
        }

        return await _store.CreateCampaignAsync(new CampaignRecord
        {
            Id = input.Id,
            Name = string.IsNullOrWhiteSpace(input.Name) ? input.Id : input.Name,
            Hypothesis = string.IsNullOrWhiteSpace(input.Hypothesis)
                ? "Nicht angegeben — bitte vor der nächsten Kampagne nachtragen."
                : input.Hypothesis,
            SearchSpace = string.IsNullOrWhiteSpace(input.SearchSpace)
                ? $"{candidateCount} explizit übergebene Parametersätze"
                : input.SearchSpace,
            SelectionMetric = input.SelectionMetric,
            TrialBudget = input.TrialBudget > 0 ? input.TrialBudget : candidateCount,
            DataSha = dataSha,
            HoldoutFrom = holdoutFrom,
            HoldoutTo = holdoutTo
        }, ct);
    }

    /// <summary>
    /// Finalisiert die zuvor reservierten Versuche mit den Lauf-Ergebnissen (Completed/Failed, inkl.
    /// Kennzahlen). Registerfehler werden NICHT verschluckt, sondern in <paramref name="notes"/> sichtbar
    /// gemacht. Die reservierten Ids bleiben eindeutig je Ausführung — abgeschlossene Ergebnisse früherer
    /// Läufe werden nicht überschrieben.
    /// </summary>
    private async Task<int> FinalizeTrialsAsync(string campaignId, IReadOnlyDictionary<string, string> trialByCandidate,
        QuantWalkForwardRequest request, IReadOnlyList<ParameterCandidate> candidates,
        WalkForwardRunResult run, List<string> notes, CancellationToken ct)
    {
        int recorded = 0;
        foreach (var c in candidates)
        {
            var trainValues = run.Folds.Select(f => f.TrainValues.TryGetValue(c.Id, out var v) ? v : null)
                .Where(v => v.HasValue).Select(v => v!.Value).ToList();
            var testValues = run.Folds.Select(f => f.TestValues.TryGetValue(c.Id, out var v) ? v : null)
                .Where(v => v.HasValue).Select(v => v!.Value).ToList();
            int selectedIn = run.Folds.Count(f => f.Selected?.Id == c.Id);

            var metrics = new Dictionary<string, double?>
            {
                [$"train.{request.SelectionMetric}.mean"] = trainValues.Count > 0 ? Stats.Mean(trainValues) : null,
                [$"test.{request.SelectionMetric}.mean"] = testValues.Count > 0 ? Stats.Mean(testValues) : null,
                ["test.sharpe_per_period"] = run.CandidateTestReturns.TryGetValue(c.Id, out var series)
                    ? CscvPbo.SharpePerPeriod(series)
                    : null,
                ["folds.selected"] = selectedIn,
                ["folds.total"] = run.Folds.Count
            };

            bool usable = trainValues.Count > 0 || testValues.Count > 0;
            var trialId = trialByCandidate[c.Id];
            var reserved = await _store.GetTrialAsync(trialId, ct);
            if (reserved is null)
            {
                notes.Add($"Versuch '{trialId}' war nach dem Lauf nicht mehr im Register auffindbar — Ergebnis nicht finalisiert.");
                continue;
            }

            var finalized = reserved with
            {
                Status = usable ? TrialStatus.Completed : TrialStatus.Failed,
                StatusReason = usable ? null : "Kein Fenster lieferte eine auswertbare Kennzahl.",
                CompletedUtc = DateTimeOffset.UtcNow,
                Metrics = metrics
            };

            try
            {
                await _store.UpdateTrialAsync(finalized, ct);
                recorded++;
            }
            catch (ExperimentRegistryException ex)
            {
                notes.Add($"Versuch '{trialId}' konnte nicht finalisiert werden — Register [{ex.Code}]: {ex.Message}");
            }
        }
        return recorded;
    }

    /// <summary>Markiert reservierte Versuche nach einem Abbruch dauerhaft als Failed (Abbruch).</summary>
    private async Task MarkReservationsCancelledAsync(string campaignId, IEnumerable<string> trialIds, CancellationToken ct)
    {
        foreach (var id in trialIds)
        {
            try
            {
                var reserved = await _store.GetTrialAsync(id, CancellationToken.None);
                if (reserved is null) continue;
                await _store.UpdateTrialAsync(reserved with
                {
                    Status = TrialStatus.Failed,
                    StatusReason = "Lauf abgebrochen (Cancellation) — Reservierung dauerhaft als abgebrochen erfasst.",
                    CompletedUtc = DateTimeOffset.UtcNow
                }, CancellationToken.None);
            }
            catch (ExperimentRegistryException) { /* Best effort beim Abbruch; Reservierung bleibt sonst 'Running'. */ }
        }
    }

    public async Task<IReadOnlyList<QuantCampaignDto>> ListCampaignsAsync(CancellationToken ct = default)
    {
        var campaigns = await _store.ListCampaignsAsync(ct);
        var result = new List<QuantCampaignDto>();
        foreach (var c in campaigns)
        {
            var trials = await _store.ListTrialsAsync(c.Id, ct);
            result.Add(new QuantCampaignDto(c.Id, c.Name, c.CreatedUtc, c.Hypothesis, c.SearchSpace,
                c.SelectionMetric, c.SelectionDirection.ToString(), c.TrialBudget, trials.Count,
                c.HoldoutFrom, c.HoldoutTo, c.HoldoutConsumed, c.Locked,
                c.HoldoutConsumedUtc, c.HoldoutEvaluatedReference, c.DataSha));
        }
        return result;
    }

    public async Task<IReadOnlyList<QuantTrialDto>> ListTrialsAsync(string? campaignId, CancellationToken ct = default)
        => (await _store.ListTrialsAsync(campaignId, ct)).Select(QuantTrialDto.From).ToList();

    public async Task<QuantTrialDto?> GetTrialAsync(string id, CancellationToken ct = default)
    {
        var t = await _store.GetTrialAsync(id, ct);
        return t is null ? null : QuantTrialDto.From(t);
    }

    public Task<IReadOnlyList<PaperResearchEntry>> ListPapersAsync(CancellationToken ct = default) => _papers.ListAsync(ct);

    public Task<PaperResearchEntry> SavePaperAsync(PaperResearchEntry entry, CancellationToken ct = default)
        => _papers.SaveAsync(entry, ct);

    public Task<IReadOnlyList<string>> ListBenchmarksAsync(CancellationToken ct = default) => _benchmarks.ListAsync(ct);

    /// <summary>
    /// Verbraucht den finalen Holdout einer Kampagne EINMALIG und bindet den Verbrauch an einen
    /// Kandidaten/eine Konfiguration. Prüfen und Reservieren sind atomar (Store). Es gibt bewusst kein
    /// „Nachsehen ohne Verbrauch": jeder erfolgreiche Aufruf verbraucht den Holdout.
    ///
    /// Ehrliche Grenze: Der technische Schutz besteht aus (1) dem reservierten, aus der Suche
    /// ausgeschlossenen Holdout-Zeitraum, (2) dem Leakage-Guard gegen überlappende Nicht-Holdout-Versuche
    /// und (3) diesem einmaligen, gebundenen Verbrauch-Flag. Das ist keine umfassende organisatorische
    /// Garantie gegen Blicke außerhalb dieses Pfads.
    /// </summary>
    public async Task<QuantHoldoutConsumeResponse> ConsumeHoldoutAsync(string campaignId, string? candidateReference, CancellationToken ct = default)
    {
        try
        {
            string reference = string.IsNullOrWhiteSpace(candidateReference)
                ? $"code {CodeVersion()} @ {DateTimeOffset.UtcNow:u}"
                : $"{candidateReference} | code {CodeVersion()} @ {DateTimeOffset.UtcNow:u}";
            var updated = await _store.ConsumeHoldoutAsync(campaignId, reference, ct);
            return new QuantHoldoutConsumeResponse
            {
                Ok = true,
                CampaignId = updated.Id,
                HoldoutConsumed = updated.HoldoutConsumed,
                HoldoutConsumedUtc = updated.HoldoutConsumedUtc,
                EvaluationReference = updated.HoldoutEvaluatedReference,
                HoldoutFrom = updated.HoldoutFrom,
                HoldoutTo = updated.HoldoutTo
            };
        }
        catch (ExperimentRegistryException ex)
        {
            return new QuantHoldoutConsumeResponse { Ok = false, CampaignId = campaignId, Error = $"Register [{ex.Code}]: {ex.Message}" };
        }
    }

    // =========================================================================================
    // Finaler Holdout — einmalige, eingefrorene, dauerhaft gespeicherte Auswertung
    // =========================================================================================

    /// <summary>Liest den dauerhaften Holdout-Zustand einer Kampagne, ohne etwas zu verändern.</summary>
    public async Task<HoldoutEvaluationResponse> GetHoldoutAsync(string campaignId, CancellationToken ct = default)
    {
        var campaign = await _store.GetCampaignAsync(campaignId, ct);
        if (campaign is null)
            return new HoldoutEvaluationResponse { Ok = false, CampaignId = campaignId, State = "UnknownCampaign", Error = $"Kampagne '{campaignId}' existiert nicht." };

        var record = await _store.GetHoldoutEvaluationAsync(campaignId, ct);
        if (record is not null)
            return new HoldoutEvaluationResponse
            {
                Ok = true, CampaignId = campaignId, State = record.Status.ToString(),
                HoldoutFrom = campaign.HoldoutFrom, HoldoutTo = campaign.HoldoutTo, Evaluation = record
            };

        if (campaign.HoldoutFrom is null && campaign.HoldoutTo is null)
            return new HoldoutEvaluationResponse { Ok = true, CampaignId = campaignId, State = "NoHoldout" };

        // Verbraucht, aber ohne gespeicherte Auswertung (z. B. Alt-Verbrauch): Zustand ausdrücklich darstellen,
        // NICHT still zurücksetzen.
        if (campaign.HoldoutConsumed)
            return new HoldoutEvaluationResponse
            {
                Ok = true, CampaignId = campaignId, State = "ConsumedNoResult",
                HoldoutFrom = campaign.HoldoutFrom, HoldoutTo = campaign.HoldoutTo,
                Error = "Der Holdout wurde bereits verbraucht, es liegt aber kein gespeichertes Auswertungsergebnis vor."
            };

        return new HoldoutEvaluationResponse
        {
            Ok = true, CampaignId = campaignId, State = "Available",
            HoldoutFrom = campaign.HoldoutFrom, HoldoutTo = campaign.HoldoutTo
        };
    }

    /// <summary>
    /// Startet die EINMALIGE finale Holdout-Auswertung eines bereits ausgewählten Kandidaten. Konfiguration
    /// wird eingefroren und validiert; die Reservierung ist atomar und verbraucht den Holdout. Ein bereits
    /// vorhandener Auswertungssatz erzeugt KEINEN neuen Lauf — der vorhandene Zustand wird zurückgegeben.
    /// </summary>
    public async Task<HoldoutEvaluationResponse> EvaluateHoldoutAsync(
        string campaignId, HoldoutEvaluateRequest request, QuantJobManager jobs, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(jobs);

        HoldoutEvaluationResponse Reject(string code, string message) =>
            new() { Ok = false, CampaignId = campaignId, State = "Rejected", Error = $"Register [{code}]: {message}" };

        HoldoutEvaluationResponse Existing(CampaignRecord c, HoldoutEvaluationRecord r) =>
            new() { Ok = true, CampaignId = campaignId, State = r.Status.ToString(), AlreadyExisted = true,
                    HoldoutFrom = c.HoldoutFrom, HoldoutTo = c.HoldoutTo, Evaluation = r };

        var campaign = await _store.GetCampaignAsync(campaignId, ct);
        if (campaign is null) return Reject("CAMPAIGN_UNKNOWN", $"Kampagne '{campaignId}' existiert nicht.");
        if (campaign.HoldoutFrom is null || campaign.HoldoutTo is null)
            return Reject("NO_HOLDOUT", "Für diese Kampagne ist kein Holdout definiert.");

        // Wiederholter Request: vorhandenen Zustand/Ergebnis zurückgeben, KEIN neuer Lauf.
        var already = await _store.GetHoldoutEvaluationAsync(campaignId, ct);
        if (already is not null) return Existing(campaign, already);
        if (campaign.HoldoutConsumed)
            return Reject("HOLDOUT_CONSUMED", "Der Holdout wurde bereits verbraucht; eine erneute Auswertung ist gesperrt.");

        if (!request.Confirm)
            return Reject("CONFIRM_REQUIRED", "Die finale Holdout-Auswertung verbraucht den Holdout unwiderruflich und muss ausdrücklich bestätigt werden (Confirm=true).");

        var candidateRef = (request.CandidateReference ?? string.Empty).Trim();
        if (candidateRef.Length == 0)
            return Reject("CANDIDATE_MISSING", "Keine Referenz auf den ausgewählten Kandidaten angegeben.");
        if (string.IsNullOrWhiteSpace(request.Run.Strategy) || request.Run.Params is null || request.Run.Params.Count == 0)
            return Reject("CONFIG_INCOMPLETE", "Strategie und vollständige Parameter des Kandidaten sind erforderlich.");

        BacktestApiService.RunContext ctx;
        try { ctx = await _backtest.LoadContextAsync(request.Run, ct); }
        catch (Exception ex) { return Reject("DATA_LOAD_FAILED", ex.Message); }
        if (ctx.Candles.Count == 0) return Reject("NO_DATA", "Keine gültigen OHLC-Bars im gewählten Zeitraum.");

        var fingerprint = DataFingerprint.Compute(ctx.Candles, ctx.Instrument.Symbol, ctx.TimeframeMinutes, ctx.Source);
        if (!string.IsNullOrEmpty(campaign.DataSha)
            && !string.Equals(campaign.DataSha, fingerprint.Sha256, StringComparison.OrdinalIgnoreCase))
            return Reject("DATA_MISMATCH",
                $"Der Datenbezug weicht vom gesperrten Datenbezug der Kampagne ab (Fingerabdruck '{Short(fingerprint.Sha256)}' ≠ '{Short(campaign.DataSha)}').");

        // Holdout-Fenster deterministisch per Zeitstempel lokalisieren.
        int holdoutStart = -1;
        for (int i = 0; i < ctx.Candles.Count; i++)
            if (ctx.Candles[i].CloseTime >= campaign.HoldoutFrom.Value) { holdoutStart = i; break; }
        int holdoutEnd = -1;
        for (int i = ctx.Candles.Count - 1; i >= 0; i--)
            if (ctx.Candles[i].CloseTime <= campaign.HoldoutTo.Value) { holdoutEnd = i; break; }
        if (holdoutStart < 0 || holdoutEnd < holdoutStart)
            return Reject("HOLDOUT_WINDOW_NOT_FOUND", "Der reservierte Holdout-Zeitraum liegt nicht in den geladenen Daten.");

        // Vollständige Bindung an einen dauerhaft gespeicherten Kandidaten-Snapshot: Strategie, Parameter,
        // Ausführungskonfiguration und Kosten stammen aus dem referenzierten Trial. Abweichende Angaben aus dem
        // Request werden VOR der Reservierung abgelehnt; fehlt der Ausführungs-Snapshot (Alt-Trial), wird der
        // Kandidat abgelehnt, statt Angaben aus aktuellen UI-Werten still zu ergänzen.
        if (string.IsNullOrWhiteSpace(request.CandidateTrialId))
            return Reject("CANDIDATE_TRIAL_REQUIRED",
                "Für die finale Holdout-Auswertung ist die Trial-Id des ausgewählten Kandidaten erforderlich (Bindung an den gespeicherten Snapshot).");

        var campaignTrials = await _store.ListTrialsAsync(campaignId, ct);
        var trial = campaignTrials.FirstOrDefault(t => string.Equals(t.Id, request.CandidateTrialId, StringComparison.Ordinal));
        if (trial is null)
            return Reject("CANDIDATE_NOT_IN_CAMPAIGN", $"Trial '{request.CandidateTrialId}' gehört nicht zur Kampagne '{campaignId}'.");
        if (trial.Execution is null)
            return Reject("CANDIDATE_SNAPSHOT_INCOMPLETE",
                $"Trial '{trial.Id}' enthält keine gespeicherte Ausführungskonfiguration (Alt-Trial). Die Kampagne erneut laufen lassen, " +
                "damit der Kandidat vollständig erfasst wird — es werden keine Angaben aus aktuellen UI-Werten ergänzt.");

        var exec = trial.Execution;
        var reqCosts = CostSnapshotFrom(ctx, request.Run);
        var deviations = new List<string>();
        if (!string.Equals(request.Run.Strategy, trial.StrategyId, StringComparison.OrdinalIgnoreCase)) deviations.Add("Strategie");
        if (!SameParameters(trial.Parameters, request.Run.Params)) deviations.Add("Parameter");
        if (request.Run.Quantity != exec.Quantity) deviations.Add("Menge");
        if (request.Run.InitialBalance != exec.InitialCapital) deviations.Add("Startkapital");
        // SL/TP EFFEKTIV vergleichen (Profil-Default aufgelöst): Der Snapshot hält den zur Trainingszeit
        // effektiv verwendeten Wert. Ein inzwischen geänderter Profildefault ergibt für denselben (ggf. null-)
        // Request eine andere effektive Auflösung → Abweichung, die VOR dem Verbrauch abgelehnt wird, statt die
        // Holdout-Ausführung still zu verändern. Ist der Snapshot bereits effektiv, ist der ??-Fallback ein No-op.
        int reqSl = request.Run.StopLossTicks ?? ctx.Instrument.DefaultStopLossTicks;
        int reqTp = request.Run.TakeProfitTicks ?? ctx.Instrument.DefaultTakeProfitTicks;
        int snapSl = exec.StopLossTicks ?? ctx.Instrument.DefaultStopLossTicks;
        int snapTp = exec.TakeProfitTicks ?? ctx.Instrument.DefaultTakeProfitTicks;
        if (reqSl != snapSl) deviations.Add("Stop-Loss");
        if (reqTp != snapTp) deviations.Add("Take-Profit");
        if (request.Run.ApplyFees != exec.ApplyFees) deviations.Add("Gebühren-Flag");
        if (ctx.TimeframeMinutes != exec.TimeframeMinutes) deviations.Add("Timeframe");
        if (!string.Equals(ctx.Instrument.Symbol, trial.Data.Symbol, StringComparison.OrdinalIgnoreCase)) deviations.Add("Symbol");
        if (reqCosts.FeePerSide != trial.Costs.FeePerSide || reqCosts.SlippageTicks != trial.Costs.SlippageTicks) deviations.Add("Kosten");
        if (deviations.Count > 0)
            return Reject("CANDIDATE_CONFIG_MISMATCH",
                "Abweichung(en) zum gespeicherten Kandidaten-Snapshot: " + string.Join(", ", deviations) +
                ". Der Holdout wird nicht verbraucht — der Kandidat muss unverändert ausgewertet werden.");

        // Konfiguration AUS DEM TRIAL-SNAPSHOT einfrieren (nicht aus dem Request); Auswertungsoptionen persistieren.
        var frozen = new HoldoutFrozenConfig
        {
            CampaignId = campaignId,
            CandidateReference = candidateRef,
            CandidateTrialId = trial.Id,
            StrategyId = trial.StrategyId,
            Parameters = new Dictionary<string, string>(trial.Parameters),
            Symbol = trial.Data.Symbol,
            TimeframeMinutes = exec.TimeframeMinutes,
            InitialCapital = exec.InitialCapital,
            Quantity = exec.Quantity,
            // Effektive (aufgelöste) Werte einfrieren — der Holdout verwendet sie direkt, ohne erneuten Profil-Fallback.
            StopLossTicks = snapSl,
            TakeProfitTicks = snapTp,
            Costs = trial.Costs,
            DataSha = fingerprint.Sha256,
            HoldoutFrom = campaign.HoldoutFrom.Value,
            HoldoutTo = campaign.HoldoutTo.Value,
            WarmupBars = Math.Max(0, request.WarmupBars),
            Frequency = request.Options.Frequency,
            AnnualizationBasis = request.Options.AnnualizationBasis,
            FixedPeriodsPerYear = request.Options.FixedPeriodsPerYear,
            RiskFreeAnnualRate = request.Options.RiskFreeAnnualRate,
            ExpectedShortfallAlpha = request.Options.ExpectedShortfallAlpha,
            MinimumPeriods = request.Options.MinimumPeriods,
            CodeVersion = CodeVersion()
        };

        string runId = ("holdout-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + "-" + Guid.NewGuid().ToString("N"));
        runId = runId[..Math.Min(runId.Length, 40)];
        var reserved = new HoldoutEvaluationRecord
        {
            CampaignId = campaignId, RunId = runId, Status = HoldoutEvaluationStatus.Reserved, Config = frozen,
            HoldoutBars = holdoutEnd - holdoutStart + 1, WarmupBarsUsed = Math.Min(frozen.WarmupBars, holdoutStart)
        };

        HoldoutEvaluationRecord record;
        try { record = await _store.ReserveHoldoutEvaluationAsync(campaignId, reserved, ct); }
        catch (ExperimentRegistryException ex)
        {
            // Paralleler Request hat gewonnen? Vorhandenen Zustand zurückgeben, sonst den Fehler nennen.
            var now = await _store.GetHoldoutEvaluationAsync(campaignId, ct);
            var refreshed = await _store.GetCampaignAsync(campaignId, ct) ?? campaign;
            return now is not null ? Existing(refreshed, now) : Reject(ex.Code, ex.Message);
        }

        int hs = holdoutStart, he = holdoutEnd;
        string jobId = jobs.Start("holdout", async (p, jct) =>
            (object)await RunHoldoutEvaluationAsync(record, ctx, hs, he, p, jct));

        return new HoldoutEvaluationResponse
        {
            Ok = true, CampaignId = campaignId, State = HoldoutEvaluationStatus.Reserved.ToString(), JobId = jobId,
            HoldoutFrom = campaign.HoldoutFrom, HoldoutTo = campaign.HoldoutTo, Evaluation = record
        };
    }

    /// <summary>
    /// Führt die reservierte Holdout-Auswertung aus: bewertet AUSSCHLIESSLICH den Holdout-Zeitraum, nutzt
    /// Warmup nur aus früheren Daten (im Warmup keine Trades/Kennzahlen), speichert das Ergebnis dauerhaft.
    /// Fehler/Abbruch werden dauerhaft erfasst; der Holdout wird NICHT automatisch wieder freigegeben.
    /// </summary>
    private async Task<HoldoutEvaluationRecord> RunHoldoutEvaluationAsync(
        HoldoutEvaluationRecord reserved, BacktestApiService.RunContext ctx, int holdoutStart, int holdoutEnd,
        IProgress<double>? progress, CancellationToken ct)
    {
        var cfg = reserved.Config;
        var startedUtc = DateTimeOffset.UtcNow;
        try
        {
            await _store.UpdateHoldoutEvaluationAsync(reserved with { Status = HoldoutEvaluationStatus.Running, StartedUtc = startedUtc }, ct);
            progress?.Report(0.1);
            ct.ThrowIfCancellationRequested();

            int warmupStart = Math.Max(0, holdoutStart - cfg.WarmupBars);
            int warmupCount = holdoutStart - warmupStart;
            var slice = ctx.Candles.Skip(warmupStart).Take(holdoutEnd - warmupStart + 1).ToList();

            var runReq = new BacktestRunRequest
            {
                Symbol = cfg.Symbol, TimeframeMinutes = cfg.TimeframeMinutes,
                Strategy = cfg.StrategyId, Params = new Dictionary<string, string>(cfg.Parameters),
                Quantity = cfg.Quantity, InitialBalance = cfg.InitialCapital,
                StopLossTicks = cfg.StopLossTicks, TakeProfitTicks = cfg.TakeProfitTicks,
                ApplyFees = cfg.Costs.ApplyFees, ExcludePartialEdges = true
            };
            IStrategy strat = _backtest.CreateStrategy(runReq, ctx.Instrument);
            if (warmupCount > 0) strat = new WarmupGuardStrategy(strat, warmupCount);

            var res = _backtest.RunEngine(ctx, strat, ConfigFrom(runReq), candlesOverride: slice);
            progress?.Report(0.6);
            ct.ThrowIfCancellationRequested();

            var spec = new QuantContractSpec(ctx.Instrument.TickSize, ctx.Instrument.PointValue, ctx.Instrument.Currency);
            var frequency = ParseFrequency(cfg.Frequency);
            var curve = ReturnSeriesBuilder.BuildEquityCurve(res, spec, frequency);

            // NUR der Holdout-Zeitraum: Punkte ab HoldoutFrom (der Warmup ist ausgeschlossen).
            var holdoutPoints = curve.Points.Where(pt => pt.Time >= cfg.HoldoutFrom).ToList();
            var notes = new List<string>();
            int localHoldoutStart = warmupCount;

            // Startkapital erhalten: Anker am OPEN der ersten Holdout-Kerze (Kapital == Startkapital, da der
            // Warmup keine Trades ausführt). So misst die erste (ggf. aggregierte) Rendite gegen das Startkapital
            // und der absolute Drawdown beginnt am Startkapital — nicht erst beim ersten Holdout-Punkt.
            double initial = (double)cfg.InitialCapital;
            var anchored = curve with { Points = holdoutPoints, StartTime = ctx.Candles[holdoutStart].OpenTime };
            var (series, maxDd, netProfit, finalEquity, truncated) = HoldoutMetrics.RealizedFromHoldout(anchored, initial);

            var metricsResult = PerformanceMetricsCalculator.Compute(series, MetricOptionsFrom(cfg));
            var metrics = metricsResult.Metrics.ToDictionary(m => m.Key, m => m.IsAvailable ? m.Value : (double?)null);

            var trades = new List<HoldoutTradeRecord>();
            int idx = 0;
            foreach (var t in res.Trades)
            {
                if (t.EntryBarIndex < localHoldoutStart) continue;   // Warmup blockiert Ausführung — Sicherheitsnetz
                trades.Add(new HoldoutTradeRecord(
                    idx++, t.Side.ToString(), t.Quantity,
                    t.EntryTime.ToUnixTimeMilliseconds(), t.ExitTime.ToUnixTimeMilliseconds(),
                    (double)t.EntryPrice, (double)t.ExitPrice,
                    t.EntryBarIndex - localHoldoutStart, t.ExitBarIndex - localHoldoutStart,
                    (double)t.GrossPnL, (double)t.Fees, (double)t.NetPnL, t.ExitReason.ToString(),
                    (double)t.StopLossPrice, (double)t.TakeProfitPrice, t.Ambiguous, t.Note));
            }

            var equityPoints = holdoutPoints.Select(pt => new HoldoutEquityPoint(
                pt.Time.ToUnixTimeMilliseconds(), pt.BarIndex - localHoldoutStart, (double)pt.RealizedEquity, (double)pt.TotalEquity)).ToList();

            var holdoutCandles = ctx.Candles.Skip(holdoutStart).Take(holdoutEnd - holdoutStart + 1).ToList();
            var quality = QuantDataQualityChecker.Check(holdoutCandles, cfg.Symbol, cfg.TimeframeMinutes);
            if (quality.Issues.Count > 0)
                notes.Add("Datenqualität im Holdout: " + string.Join(", ", quality.Issues.Select(i => i.Code).Distinct()));
            if (truncated)
                notes.Add("Kapital ≤ 0 im Holdout: Die Renditereihe (Prozentkennzahlen wie Sharpe/CAGR/Vola) wurde beim " +
                          "ersten nicht positiven Kapitalstand abgebrochen und gilt nur bis dahin. Absolute Ergebniszahlen " +
                          "(End-Equity, Netto-PnL, absoluter Drawdown), Chart und Journal beziehen sich auf den vollständigen Holdout-Lauf.");
            if (series.Count == 0)
                notes.Add("Zu wenige Holdout-Perioden für belastbare Kennzahlen.");

            metrics["netprofit"] = netProfit;
            metrics["maxdrawdown"] = maxDd;
            metrics["trades"] = trades.Count;

            var completed = reserved with
            {
                Status = HoldoutEvaluationStatus.Completed, StartedUtc = startedUtc, CompletedUtc = DateTimeOffset.UtcNow,
                UsedDataSha = cfg.DataSha, HoldoutBars = holdoutEnd - holdoutStart + 1, WarmupBarsUsed = warmupCount,
                Metrics = metrics, MaxDrawdown = maxDd, NetProfit = netProfit, FinalEquity = finalEquity,
                TradeCount = trades.Count, Equity = equityPoints, Trades = trades, Notes = notes, StatusReason = null
            };
            progress?.Report(1);
            return await _store.UpdateHoldoutEvaluationAsync(completed, ct);
        }
        catch (OperationCanceledException)
        {
            var cancelled = reserved with
            {
                Status = HoldoutEvaluationStatus.Cancelled, StartedUtc = startedUtc, CompletedUtc = DateTimeOffset.UtcNow,
                StatusReason = "Abgebrochen (Nutzer oder Laufzeitgrenze). Der Holdout bleibt verbraucht und wird nicht automatisch freigegeben."
            };
            try { await _store.UpdateHoldoutEvaluationAsync(cancelled, CancellationToken.None); } catch (ExperimentRegistryException) { /* Satz fehlt nicht */ }
            throw;
        }
        catch (Exception ex)
        {
            var failed = reserved with
            {
                Status = HoldoutEvaluationStatus.Failed, StartedUtc = startedUtc, CompletedUtc = DateTimeOffset.UtcNow,
                StatusReason = ex.Message
            };
            try { await _store.UpdateHoldoutEvaluationAsync(failed, CancellationToken.None); } catch (ExperimentRegistryException) { /* Satz fehlt nicht */ }
            throw;
        }
    }

    private static string Short(string sha) => sha.Length > 12 ? sha[..12] + "…" : sha;

    private static bool SameParameters(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b)
    {
        if (a.Count != b.Count) return false;
        foreach (var kv in a)
            if (!b.TryGetValue(kv.Key, out var v) || !string.Equals(v, kv.Value, StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>Baut die Kennzahlenoptionen aus der PERSISTIERTEN Holdout-Konfiguration (reproduzierbar/neustartfest).</summary>
    private static PerformanceMetricsOptions MetricOptionsFrom(HoldoutFrozenConfig cfg) => new()
    {
        AnnualizationBasis = string.Equals(cfg.AnnualizationBasis, "Fixed", StringComparison.OrdinalIgnoreCase)
            ? AnnualizationBasis.Fixed : AnnualizationBasis.Observed,
        FixedPeriodsPerYear = cfg.FixedPeriodsPerYear,
        RiskFreeAnnualRate = cfg.RiskFreeAnnualRate,
        ExpectedShortfallAlpha = cfg.ExpectedShortfallAlpha,
        MinimumPeriods = cfg.MinimumPeriods
    };

    private static CostProfileSnapshot CostSnapshotFrom(BacktestApiService.RunContext ctx, BacktestRunRequest run) => new()
    {
        FeePerSide = ctx.Fee.CommissionPerSide + ctx.Fee.ExchangeFeePerSide + ctx.Fee.ClearingFeePerSide
                     + ctx.Fee.RoutingFeePerSide + ctx.Fee.NfaFeePerSide + ctx.Fee.OtherFeePerSide,
        SlippageTicks = ctx.Fee.EstimatedSlippageTicks,
        TickSize = ctx.Instrument.TickSize,
        PointValue = ctx.Instrument.PointValue,
        ApplyFees = run.ApplyFees,
        Currency = ctx.Instrument.Currency,
        IsExampleProfile = ctx.FeeIsExample || ctx.InstrumentIsExample
    };

    // =========================================================================================
    // Hilfsfunktionen
    // =========================================================================================

    private static OhlcBacktestConfig ConfigFrom(BacktestRunRequest req) => new()
    {
        Quantity = req.Quantity,
        InitialBalance = req.InitialBalance,
        StopLossTicks = req.StopLossTicks,
        TakeProfitTicks = req.TakeProfitTicks,
        SlippageTicksOverride = req.SlippageTicks,
        ApplyFees = req.ApplyFees,
        ExcludePartialEdges = req.ExcludePartialEdges
    };

    private static PerformanceMetricsOptions MetricOptions(QuantEvaluationOptions o) => new()
    {
        AnnualizationBasis = string.Equals(o.AnnualizationBasis, "Fixed", StringComparison.OrdinalIgnoreCase)
            ? AnnualizationBasis.Fixed
            : AnnualizationBasis.Observed,
        FixedPeriodsPerYear = o.FixedPeriodsPerYear,
        RiskFreeAnnualRate = o.RiskFreeAnnualRate,
        ExpectedShortfallAlpha = o.ExpectedShortfallAlpha,
        MinimumPeriods = o.MinimumPeriods
    };

    private static ReturnFrequency ParseFrequency(string? s) => s?.ToLowerInvariant() switch
    {
        "bar" => ReturnFrequency.Bar,
        "weekly" => ReturnFrequency.Weekly,
        "monthly" => ReturnFrequency.Monthly,
        _ => ReturnFrequency.Daily
    };

    private static ResamplingMethod ParseMethod(string? s) => s?.ToLowerInvariant() switch
    {
        "movingblock" => ResamplingMethod.MovingBlock,
        "stationary" => ResamplingMethod.Stationary,
        _ => ResamplingMethod.Permutation
    };

    private static string CandidateId(IReadOnlyDictionary<string, string> p, int index)
    {
        if (p.Count == 0) return $"c{index}";
        return string.Join("_", p.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}{kv.Value}"));
    }

    /// <summary>Die einzigen unterstützten Auswahlkriterien. Alles andere wird abgelehnt, nicht still ersetzt.</summary>
    private static readonly HashSet<string> KnownSelectionMetrics =
        new(StringComparer.Ordinal) { "sharpe", "sortino", "cagr", "calmar", "netprofit" };

    /// <summary>Vereinheitlicht ein Auswahlkriterium (trim + Kleinbuchstaben); <c>null</c> bei leer/Whitespace.</summary>
    private static string? NormalizeMetric(string? metric)
    {
        var trimmed = (metric ?? string.Empty).Trim();
        return trimmed.Length == 0 ? null : trimmed.ToLowerInvariant();
    }

    private static double? SelectionValue(string metric, PerformanceMetricsResult metrics, decimal netProfit) =>
        metric.ToLowerInvariant() switch
        {
            "netprofit" => (double)netProfit,
            "cagr" => metrics.Value("cagr"),
            "calmar" => metrics.Value("calmar"),
            "sortino" => metrics.Value("sortino"),
            _ => metrics.Value("sharpe")
        };

    /// <summary>Verkettet mehrere Renditereihen chronologisch zu einer Reihe (für mehrteilige Trainingsabschnitte).</summary>
    private static ReturnSeries Concat(IReadOnlyList<ReturnSeries> parts)
    {
        if (parts.Count == 1) return parts[0];
        var times = new List<DateTimeOffset>();
        var rets = new List<double>();
        var levels = new List<double>();
        double equity = 1.0;
        foreach (var p in parts.OrderBy(p => p.Timestamps.Count > 0 ? p.Timestamps[0] : DateTimeOffset.MaxValue))
            for (int i = 0; i < p.Count; i++)
            {
                times.Add(p.Timestamps[i]);
                rets.Add(p.Returns[i]);
                equity *= 1.0 + p.Returns[i];
                levels.Add(equity);
            }
        return new ReturnSeries
        {
            Name = "verkettet", Timestamps = times, Returns = rets, EquityLevels = levels,
            InitialCapital = 1.0, Basis = EquityBasis.Total, Frequency = parts[0].Frequency
        };
    }

    private static QuantDrawdownDto ToDto(DrawdownInfo d) => new(
        d.MaxDrawdownFraction, d.MaxDrawdownAbsolute,
        d.PeakTime?.ToUnixTimeMilliseconds(), d.TroughTime?.ToUnixTimeMilliseconds(), d.RecoveryTime?.ToUnixTimeMilliseconds(),
        d.LongestUnderwaterPeriods, d.LongestUnderwaterDays, d.UnderwaterAtEnd);

    private static QuantDistributionDto ToDto(MonteCarloDistribution d, int bins = 40)
    {
        var values = d.Values;
        var hist = new double[bins];
        double min = values.Count == 0 ? 0 : values.Min();
        double max = values.Count == 0 ? 0 : values.Max();
        if (values.Count > 0 && max > min)
            foreach (var v in values)
            {
                int b = (int)((v - min) / (max - min) * (bins - 1));
                hist[Math.Clamp(b, 0, bins - 1)]++;
            }
        else if (values.Count > 0) hist[0] = values.Count;

        return new QuantDistributionDto(d.Key, d.Label, d.Unit, d.Min, d.P5, d.P25, d.Median, d.P75, d.P95, d.Max, d.Mean,
            hist, min, max);
    }

    private static QuantBenchmarkDto ToDto(BenchmarkComparisonResult r)
    {
        var t = new List<long>();
        var s = new List<double>();
        var b = new List<double>();
        if (r.Available)
        {
            double se = 1.0, be = 1.0;
            for (int i = 0; i < r.AlignedStrategy.Count; i++)
            {
                se *= 1.0 + r.AlignedStrategy.Returns[i];
                be *= 1.0 + r.AlignedBenchmark.Returns[i];
                t.Add(r.AlignedStrategy.Timestamps[i].ToUnixTimeMilliseconds());
                s.Add(se);
                b.Add(be);
            }
        }
        return new QuantBenchmarkDto(r.Available, r.UnavailableReason, r.BenchmarkName, r.BenchmarkProvenance,
            r.CommonPeriods, r.Metrics.Select(QuantMetricDto.From).ToList(), t, s, b, r.Assumptions, r.Warnings);
    }

    private static CostProfileDto CostDto(BacktestApiService.RunContext ctx, OhlcBacktestResult result) => new(
        TickSize: ctx.Instrument.TickSize,
        TickValue: ctx.Instrument.TickValue,
        PointValue: ctx.Instrument.PointValue,
        Currency: ctx.Instrument.Currency,
        FeePerSide: result.FeePerSide,
        FeeRoundTrip: result.FeePerSide * 2m,
        SlippageTicks: result.EffectiveSlippageTicks,
        SlippagePerSideDollars: result.EffectiveSlippageTicks * ctx.Instrument.TickValue,
        ApplyFees: result.Config.ApplyFees,
        InstrumentIsExample: ctx.InstrumentIsExample,
        FeeIsExample: ctx.FeeIsExample);
}
