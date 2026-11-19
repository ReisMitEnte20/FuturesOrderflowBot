using System.Globalization;
using TradingBot.Backtesting.Ohlc;
using TradingBot.Core.Interfaces;
using TradingBot.DevDashboard.Services.Quant.Report;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Quant.MonteCarlo;
using TradingBot.Quant.Registry;
using TradingBot.Quant.Report;
using TradingBot.Quant.Validation;

namespace TradingBot.DevDashboard.Services.Quant;

/// <summary>
/// Ein-Knopf-Bericht ("full"): EIN Auftrag führt alle anwendbaren Analysen im Backend aus. Die Kerzen werden je Datenanfrage
/// genau einmal gelesen (Zwischenspeicher im <see cref="BacktestApiService"/>) und von allen Abschnitten wiederverwendet.
/// Vorhandene Bausteine werden angebunden statt nachgebaut: Basisanalyse/Kennzahlen (<see cref="QuantApiService.AnalyzeAsync"/>),
/// Walk-forward mit Versuchsregister (<see cref="QuantApiService.WalkForwardAsync"/>), Kosten-/Verzögerungsstress
/// (<see cref="QuantApiService.RobustnessAsync"/>), PBO/PSR/DSR und die Holdout-Vorbereitung. Jeder Abschnitt wird nach
/// seiner Berechnung gespeichert; scheitert einer, bleiben die übrigen nutzbar. Der reservierte finale Holdout wird nie
/// automatisch ausgewertet.
/// </summary>
public sealed partial class ResearchOrchestrator
{
    private static readonly (string Key, string Label)[] FullStepDefs =
    {
        ("data", "01 Datenqualität & zeitliche Korrektheit"),
        ("result", "02 Basis-Backtest & Ergebnisrechnung"),
        ("equity", "03 Equity, Drawdown & Verlustphasen"),
        ("risk", "04 Rendite im Verhältnis zum Risiko"),
        ("sensitivity", "05 Parametersensitivität & Wechselwirkungen"),
        ("oos", "06 Walk-forward-Testdaten (Out-of-Sample)"),
        ("simulation", "07 Monte Carlo & Bootstrap"),
        ("costs", "08 Kosten-, Slippage- & Ausführungsstress"),
        ("stability", "09 Stabilität über Zeit & Marktbedingungen"),
        ("concentration", "10 Konzentration & Ausreißer"),
        ("controls", "11 Benchmarks, Kontrollläufe & Overfitting"),
        ("trades", "12 Einzeltrade-Prüfung (Journal, Replay, MAE/MFE-Schätzung)"),
        ("holdout", "Finaler Holdout – unbenutzt (reserviert)")
    };

    internal const string FillModelText =
        "Bar-basiert: Signal am Kerzenschluss, Ausführung frühestens am Open der Folgekerze (Market ± Slippage). " +
        "Stop = Market (Gap: Fill zum schlechteren Open), Ziel = Limit (nie schlechter als die Grenze, keine Slippage). " +
        "Stop und Ziel in derselben Kerze ⇒ konservativ Stop (als mehrdeutig markiert). Slippage steckt nur in den Fill-Preisen.";

    private sealed class FullCtx
    {
        public required ResearchStartRequest Cfg;
        public required FullReportOptions Opt;
        public required BacktestRunRequest DevRun;
        public required BacktestApiService.RunContext Dev;
        public required OhlcBacktestResult Base;
        public required List<ReportTrade> BaseTrades;
        public required List<string> DevSessions;
        public QuantWalkForwardResponse? PrimaryWf;
        public QuantWalkForwardRequest? PrimaryWfRequest;
        public OosVariant? PrimaryOos;
        public List<ReportTrade> OosTrades = new();
        public List<SessionResult> OosSessions = new();
        public QuantRobustnessResponse? Robustness;
        public int SensitivityRuns;
        public int SensitivityVariants;
        public int WfSelectionAttempts;
        public int WfTrials;
        public int WfVariantsRun;
    }

    private async Task<ResearchRunRecord> RunFullReportAsync(ResearchRunRecord record, IProgress<double>? progress, CancellationToken ct)
    {
        var cfg = record.Config;
        var opt = cfg.Report ?? new FullReportOptions();
        var bt = _quant.Backtest;
        var report = new BacktestReport();

        async Task Save() { record = record with { Report = report }; record = await _store.UpdateAsync(record, ct); }
        async Task SetStep(string key, ResearchStepStatus status, string? reason = null, string? basis = null, bool start = false, bool done = false)
        {
            var steps = record.Steps.Select(s => s.Key == key
                ? s with
                {
                    Status = status, Reason = reason ?? s.Reason, DataBasis = basis ?? s.DataBasis,
                    StartedUtc = start ? DateTimeOffset.UtcNow : s.StartedUtc,
                    CompletedUtc = done ? DateTimeOffset.UtcNow : s.CompletedUtc
                } : s).ToList();
            record = record with { Steps = steps, Report = report };
            record = await _store.UpdateAsync(record, ct);
        }
        // Führt einen Abschnitt aus; ein technischer Fehler markiert NUR diesen Abschnitt (Rest bleibt nutzbar).
        async Task Section(string key, double p0, double p1, Func<Task<(ResearchStepStatus Status, string? Reason, string? Basis)>> body)
        {
            await SetStep(key, ResearchStepStatus.Running, start: true);
            progress?.Report(p0);
            (ResearchStepStatus Status, string? Reason, string? Basis) r;
            try { r = await body(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { r = (ResearchStepStatus.Failed, "Technisch fehlgeschlagen: " + ex.Message, null); }
            await SetStep(key, r.Status, r.Reason, r.Basis, done: true);
            progress?.Report(p1);
        }

        try
        {
            // --- 0: Entwicklungs-/Holdout-Split (wie die klassische Kette): alle Analysen nur VOR dem Holdout ---
            // Session-ausgerichtet: dieselbe Holdout-Grenze wie der Walk-forward-Planer (ganze Sessions).
            var split = await _quant.ComputeHoldoutSplitAsync(cfg.Run, cfg.HoldoutFraction, cfg.SessionAligned ? cfg.SessionCalendar : null, ct);
            var devLoadEnd = split.DevelopmentLoadToUtc ?? split.DevelopmentToUtc;
            var devRun = devLoadEnd is { } devEnd ? cfg.Run with { ToUtc = devEnd.ToString("o") } : cfg.Run;
            record = record with
            {
                DataFrom = split.DataFrom, DataTo = split.DataTo, DevelopmentToUtc = split.DevelopmentToUtc,
                HoldoutFrom = split.HoldoutFrom, HoldoutTo = split.HoldoutTo, TotalBars = split.TotalBars,
                DevelopmentBars = split.UsableBars, HoldoutBars = split.HoldoutBars, DevRun = devRun
            };
            record = await _store.UpdateAsync(record, ct);
            string devBasis = split.HoldoutBars > 0 ? "Basislauf (Entwicklungsbereich, ohne reservierten Holdout)" : "Basislauf (gesamter Zeitraum, kein Holdout reserviert)";

            var dev = await bt.LoadContextAsync(devRun, ct);
            var full = await bt.LoadContextAsync(cfg.Run, ct);
            if (dev.Candles.Count < 2)
            {
                foreach (var s in FullStepDefs)
                    await SetStep(s.Key, ResearchStepStatus.InsufficientData, $"Zu wenige Kerzen im Entwicklungsbereich ({dev.Candles.Count}).", done: true);
                return await Finish(record, ResearchStepStatus.Failed, "Keine auswertbaren Kerzen im gewählten Zeitraum.", ct);
            }

            // Basislauf: exakt derselbe Engine-Aufruf wie ein Einzel-Backtest mit dieser Konfiguration.
            var baseRes = bt.RunEngine(dev, bt.CreateStrategy(devRun, dev.Instrument), QuantApiService.ConfigFrom(devRun));
            var baseTrades = ToReportTrades(baseRes, dev);
            var devSessions = dev.Candles.Select(c => TradeDateCalendar.Key(c.OpenTime)).Distinct().ToList();
            var fc = new FullCtx
            {
                Cfg = cfg, Opt = opt, DevRun = devRun, Dev = dev, Base = baseRes, BaseTrades = baseTrades, DevSessions = devSessions
            };
            int fullSessions = full.Candles.Select(c => TradeDateCalendar.Key(c.OpenTime)).Distinct().Count();

            report = report with { Provenance = BuildProvenance(record, fc, split, fullSessions - devSessions.Count) };
            await Save();

            // --- 01 Daten ---
            QuantAnalyzeResponse? analysis = null;
            await Section("data", 0.02, 0.10, async () =>
            {
                analysis = await _quant.AnalyzeAsync(new QuantAnalyzeRequest
                {
                    Run = devRun, Options = cfg.Options, BenchmarkId = cfg.BenchmarkId, StrategyIsFullyFunded = cfg.StrategyIsFullyFunded
                }, null, ct);
                record = record with { Analysis = analysis };
                report = report with { Data = BuildDataSection(fc, analysis) };
                bool err = analysis.DataQuality?.Issues.Any(i => i.Severity == "Error") == true;
                bool leakFail = report.Data!.TemporalChecks.Any(c => c.Passed == false);
                return (leakFail || err ? ResearchStepStatus.Completed : ResearchStepStatus.Completed,
                    leakFail ? "Mindestens eine Zeitprüfung ist NICHT bestanden — Ergebnisse mit Vorsicht lesen." :
                    err ? "Datenfehler gefunden (siehe Liste)." : null, devBasis);
            });

            if (baseRes.Status != Backtesting.BacktestRunStatus.Completed)
            {
                string why = baseRes.Message ?? "Basislauf fehlgeschlagen.";
                foreach (var s in FullStepDefs.Skip(1))
                    await SetStep(s.Key, ResearchStepStatus.Failed, "Basislauf fehlgeschlagen: " + why, done: true);
                return await Finish(record, ResearchStepStatus.Failed, why, ct);
            }

            // --- 02 Ergebnis ---
            await Section("result", 0.10, 0.13, () =>
            {
                var stats = TradeStatistics.Compute(baseTrades, baseRes.Data.EvaluatedBars);
                var notes = new List<string>
                {
                    "Brutto = Kursergebnis aus den Fill-Preisen; die modellierte Slippage steckt bereits darin und wird NICHT zusätzlich abgezogen.",
                    "Netto = Brutto − Gebühren (genau einmal je Seite und Kontrakt)."
                };
                if (stats.OpenAtEnd > 0) notes.Add($"{stats.OpenAtEnd} Position(en) waren am Datenende offen und sind zum letzten Schlusskurs BEWERTET (unrealisiert, getrennt ausgewiesen).");
                if (baseRes.IncompleteSessionCloses > 0) notes.Add(baseRes.Message ?? "Unvollständige Sessions.");
                if (stats.Trades == 0) notes.Add("Keine Trades im Basislauf.");
                report = report with
                {
                    Result = new ReportResultSection
                    {
                        Basis = devBasis, Stats = stats, Costs = analysis?.Costs, EvaluatedBars = baseRes.Data.EvaluatedBars,
                        SignalsGenerated = baseRes.SignalsGenerated, MissedEntrySkips = baseRes.MissedEntrySkips,
                        InvalidStopSkips = baseRes.InvalidStopSkips, Notes = notes
                    }
                };
                return Task.FromResult<(ResearchStepStatus, string?, string?)>(
                    (stats.Trades == 0 ? ResearchStepStatus.InsufficientData : ResearchStepStatus.Completed,
                     stats.Trades == 0 ? "Keine Trades im Basislauf." : null, devBasis));
            });

            // --- 03 Equity/Drawdown ---
            await Section("equity", 0.13, 0.15, () =>
            {
                var start = dev.Candles[0].OpenTime;
                double cap = (double)devRun.InitialBalance;
                var closed = DrawdownAnalysis.Analyze(DrawdownAnalysis.ClosedTradeCurve(baseTrades, cap, start), "Abgeschlossene Trades");
                var mtmCurve = new List<EquitySample> { new(start, cap) };
                mtmCurve.AddRange(baseRes.Equity.Select(p => new EquitySample(p.Time, (double)(p.Equity + p.OpenPnL))));
                var mtm = DrawdownAnalysis.Analyze(mtmCurve, "Mark-to-Market je Kerzenschluss");
                report = report with
                {
                    Equity = new ReportEquitySection
                    {
                        Basis = devBasis, ClosedTrades = closed, MarkToMarket = mtm, LosingStreak = DrawdownAnalysis.LosingStreak(baseTrades),
                        Notes = new[]
                        {
                            "Durchgehende Kapitalkurve eines EINZIGEN Laufs mit fester Kontraktzahl — kein tägliches Zurücksetzen des Kapitals.",
                            "Mark-to-Market bewertet offene Positionen zum Kerzenschluss (ohne Exit-Kosten); der zwischenzeitliche Drawdown kann daher größer sein als der aus abgeschlossenen Trades.",
                            "Ein bis Datenende nicht aufgeholter Drawdown ist als „offen“ markiert."
                        }
                    }
                };
                return Task.FromResult<(ResearchStepStatus, string?, string?)>((ResearchStepStatus.Completed, null, devBasis));
            });

            // --- 04 Risiko-Kennzahlen (aus der vorhandenen Kennzahlen-Bibliothek) ---
            await Section("risk", 0.15, 0.17, () =>
            {
                if (analysis is null || !analysis.Ok)
                    return Task.FromResult<(ResearchStepStatus, string?, string?)>((ResearchStepStatus.Failed, analysis?.Error ?? "Basisanalyse fehlt.", null));
                var keys = new[] { "sharpe", "sortino", "calmar", "cagr", "volatility_annual", "max_drawdown", "expected_shortfall" };
                var metrics = analysis.MetricsTotal.Where(m => keys.Contains(m.Key)).OrderBy(m => Array.IndexOf(keys, m.Key)).ToList();
                bool anyMissing = metrics.Any(m => m.Value is null);
                report = report with
                {
                    Risk = new ReportRiskSection
                    {
                        Basis = devBasis, Frequency = analysis.Frequency, PeriodsPerYear = analysis.PeriodsPerYear,
                        AnnualizationNote = analysis.AnnualizationNote,
                        CapitalBase = $"Periodenrendite = Veränderung des Gesamtkapitals (inkl. Bewertung offener Positionen) gegenüber dem Kapital am Ende der Vorperiode; Startkapital {devRun.InitialBalance:0} {analysis.Currency}, feste Kontraktzahl {devRun.Quantity}.",
                        Metrics = metrics,
                        Notes = analysis.Notes.Where(n => n.Contains("Annual", StringComparison.OrdinalIgnoreCase) || n.Contains("Teilperiode", StringComparison.OrdinalIgnoreCase)).Take(4).ToList()
                    }
                };
                return Task.FromResult<(ResearchStepStatus, string?, string?)>(
                    (anyMissing && metrics.All(m => m.Value is null) ? ResearchStepStatus.InsufficientData : ResearchStepStatus.Completed,
                     anyMissing ? "Einzelne Kennzahlen nicht definiert (Grund je Kennzahl)." : null, devBasis + ", Tagesrenditen"));
            });

            // --- 05 Sensitivität ---
            await Section("sensitivity", 0.17, 0.32, async () =>
            {
                var sec = await BuildSensitivityAsync(fc, ct);
                report = report with { Sensitivity = sec };
                return (sec.Applicable ? ResearchStepStatus.Completed : ResearchStepStatus.NotApplicable, sec.Reason, devBasis);
            });

            // --- 06 OOS: fester Split, rollierender und anwachsender Walk-forward ---
            await Section("oos", 0.32, 0.60, async () =>
            {
                var sec = await BuildOosAsync(fc, record, full, ct);
                record = record with { WalkForward = fc.PrimaryWf, CampaignId = fc.PrimaryWf?.CampaignId ?? record.CampaignId };
                report = report with { Oos = sec };
                var completed = sec.Variants.Where(v => v.Status == "Completed").ToList();
                if (completed.Count == 0)
                {
                    bool insufficient = sec.Variants.All(v => v.Status == "InsufficientData");
                    return (insufficient ? ResearchStepStatus.InsufficientData : ResearchStepStatus.Failed,
                        string.Join(" · ", sec.Variants.Select(v => $"{v.Label}: {v.Reason}")), null);
                }
                string? partial = sec.Variants.Any(v => v.Status != "Completed")
                    ? string.Join(" · ", sec.Variants.Where(v => v.Status != "Completed").Select(v => $"{v.Label}: {v.Reason}")) : null;
                return (ResearchStepStatus.Completed, partial, "Walk-forward-Testdaten: verkettete Testfenster (Auswahl nur im Training)");
            });

            // Kosten-/Verzögerungsstress über die vorhandene Robustheitsprüfung (Engine-Neuläufe mit geänderten Kosten).
            try
            {
                fc.Robustness = await _quant.RobustnessAsync(new QuantRobustnessRequest
                {
                    Run = devRun, Options = cfg.Options, Metric = "netprofit",
                    FeeMultipliers = new[] { 1.0, 1.5, 2.0, 3.0 }, SlippageMultipliers = new[] { 1.0, 2.0, 3.0 },
                    ExecutionDelays = new[] { 0, 1, 2 }, ParameterOffsets = Array.Empty<int>()
                }, null, ct);
                record = record with { Robustness = fc.Robustness };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { fc.Robustness = null; }

            // --- 07 Simulation ---
            await Section("simulation", 0.60, 0.72, () =>
            {
                var sec = BuildSimulation(fc, ct);
                report = report with { Simulation = sec };
                bool any = sec.Methods.Any(m => m.UnavailableReason is null);
                return Task.FromResult<(ResearchStepStatus, string?, string?)>(
                    (any ? ResearchStepStatus.Completed : ResearchStepStatus.InsufficientData,
                     any ? null : sec.Methods.FirstOrDefault()?.UnavailableReason, "Simulation: " + sec.Basis));
            });

            // --- 08 Kosten ---
            await Section("costs", 0.72, 0.75, () =>
            {
                report = report with { Costs = BuildCosts(fc) };
                return Task.FromResult<(ResearchStepStatus, string?, string?)>(
                    (baseTrades.Count == 0 ? ResearchStepStatus.InsufficientData : ResearchStepStatus.Completed,
                     baseTrades.Count == 0 ? "Keine Trades." : null, devBasis));
            });

            // --- 09 Stabilität ---
            await Section("stability", 0.75, 0.79, () =>
            {
                report = report with { Stability = BuildStability(fc) };
                return Task.FromResult<(ResearchStepStatus, string?, string?)>(
                    (baseTrades.Count == 0 ? ResearchStepStatus.InsufficientData : ResearchStepStatus.Completed,
                     baseTrades.Count == 0 ? "Keine Trades." : null, devBasis));
            });

            // --- 10 Konzentration ---
            await Section("concentration", 0.79, 0.81, () =>
            {
                var res = ConcentrationAnalysis.Compute(baseTrades);
                report = report with { Concentration = new ReportConcentrationSection { Basis = devBasis, Result = res } };
                return Task.FromResult<(ResearchStepStatus, string?, string?)>(
                    (baseTrades.Count == 0 ? ResearchStepStatus.InsufficientData : ResearchStepStatus.Completed,
                     baseTrades.Count == 0 ? "Keine Trades." : res.LeaveOneOutReason is null ? null : "Weglassen je Handelstag: " + res.LeaveOneOutReason, devBasis));
            });

            // --- 11 Kontrollen & Overfitting ---
            await Section("controls", 0.81, 0.92, async () =>
            {
                var sec = await BuildControlsAsync(fc, analysis, ct);
                record = record with { Overfitting = sec.Overfitting };
                report = report with { Controls = sec };
                return (ResearchStepStatus.Completed, null, devBasis + " + OOS");
            });

            // --- 12 Einzeltrades ---
            await Section("trades", 0.92, 0.96, () =>
            {
                report = report with { Trades = BuildTrades(fc) };
                return Task.FromResult<(ResearchStepStatus, string?, string?)>(
                    (baseTrades.Count == 0 ? ResearchStepStatus.InsufficientData : ResearchStepStatus.Completed,
                     baseTrades.Count == 0 ? "Keine Trades." : null, devBasis));
            });

            // --- Holdout: nur vorbereiten, NIE automatisch auswerten ---
            await Section("holdout", 0.96, 0.98, async () =>
            {
                if (fc.PrimaryWf is null)
                    return (split.HoldoutBars > 0 ? ResearchStepStatus.NotComputable : ResearchStepStatus.NotApplicable,
                        split.HoldoutBars > 0 ? "Kein Walk-forward-Kandidat — Holdout bleibt reserviert und unberührt." : "Kein Holdout reserviert (Holdout-Anteil 0).", null);
                var proposal = await BuildHoldoutProposalAsync(cfg, fc.PrimaryWf, ct);
                record = record with { HoldoutProposal = proposal };
                return (proposal.Available ? ResearchStepStatus.Completed : ResearchStepStatus.NotComputable,
                    proposal.Available ? "Reserviert — Auswertung nur nach ausdrücklicher Bestätigung (Schlussprüfung)." : proposal.UnavailableReason,
                    "Reservierter finaler Holdout");
            });

            report = report with { Overview = BuildOverview(fc, report), Provenance = BuildProvenance(record, fc, split, fullSessions - devSessions.Count) };
            await Save();
            progress?.Report(1.0);
            return await Finish(record, ResearchStepStatus.Completed, null, ct);
        }
        catch (OperationCanceledException)
        {
            var steps = record.Steps.Select(s => s.Status is ResearchStepStatus.Running or ResearchStepStatus.Pending
                ? s with { Status = ResearchStepStatus.Cancelled, Reason = "Abgebrochen — fertige Abschnitte bleiben erhalten." } : s).ToList();
            record = record with { Steps = steps, Report = report with { Overview = SafeOverview(report) } };
            await Finish(record, ResearchStepStatus.Cancelled, "Der Lauf wurde abgebrochen. Fertige Teilergebnisse bleiben sichtbar.", ct: CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            var steps = record.Steps.Select(s => s.Status is ResearchStepStatus.Running or ResearchStepStatus.Pending
                ? s with { Status = ResearchStepStatus.Failed, Reason = ex.Message } : s).ToList();
            record = record with { Steps = steps, Report = report with { Overview = SafeOverview(report) } };
            return await Finish(record, ResearchStepStatus.Failed, ex.Message, ct: CancellationToken.None);
        }
    }

    private static ReportOverview SafeOverview(BacktestReport r)
    {
        try { return BuildOverviewStatic(r, null); } catch { return r.Overview; }
    }

    // =============================================================================================
    // Hilfen
    // =============================================================================================

    internal static List<ReportTrade> ToReportTrades(OhlcBacktestResult r, BacktestApiService.RunContext ctx)
    {
        decimal slipCost = r.EffectiveSlippageTicks * ctx.Instrument.TickValue;
        return r.Trades.Select((t, i) =>
        {
            int marketSides = t.ExitReason == OhlcExitReason.TakeProfit ? 1 : 2;
            return new ReportTrade
            {
                Index = i,
                EntryTime = t.EntryTime, ExitTime = t.ExitTime, IsLong = t.Side == PositionSide.Long, Quantity = t.Quantity,
                EntryPrice = t.EntryPrice, ExitPrice = t.ExitPrice, EntryBarIndex = t.EntryBarIndex, ExitBarIndex = t.ExitBarIndex,
                GrossPnL = t.GrossPnL, Fees = t.Fees, NetPnL = t.NetPnL,
                SlippageCost = slipCost * t.Quantity * marketSides, MarketSides = marketSides,
                ExitReason = t.ExitReason.ToString(), Ambiguous = t.Ambiguous,
                ClosedAtDataEnd = t.ExitReason == OhlcExitReason.EndOfData,
                SessionKey = TradeDateCalendar.Key(t.EntryTime)
            };
        }).ToList();
    }

    private OhlcBacktestResult? TryRun(BacktestApiService.RunContext ctx, BacktestRunRequest run, IReadOnlyList<Candle>? slice,
        int warmupBars, out string? error)
    {
        try
        {
            IStrategy s = _quant.Backtest.CreateStrategy(run, ctx.Instrument);
            if (warmupBars > 0) s = new WarmupGuardStrategy(s, warmupBars);
            var res = _quant.Backtest.RunEngine(ctx, s, QuantApiService.ConfigFrom(run), candlesOverride: slice);
            if (res.Status != Backtesting.BacktestRunStatus.Completed) { error = res.Message ?? "Lauf fehlgeschlagen."; return null; }
            if (res.IncompleteSessionCloses > 0) { error = $"{res.IncompleteSessionCloses} unvollständige Session(s) — nicht belastbar."; return null; }
            error = null;
            return res;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message;
            return null;
        }
    }

    private static string Inv(double v) => v.ToString(CultureInfo.InvariantCulture);

    private ReportProvenance BuildProvenance(ResearchRunRecord record, FullCtx fc, HoldoutSplit split, int holdoutSessions)
    {
        var cfg = fc.Cfg;
        var strategies = _quant.Backtest.GetStrategies();
        var def = strategies.FirstOrDefault(s => string.Equals(s.Id, cfg.Run.Strategy, StringComparison.OrdinalIgnoreCase));
        string code = _quant.CodeVersion();
        var fp = DataFingerprint.Compute(fc.Dev.Candles, fc.Dev.Instrument.Symbol, fc.Dev.TimeframeMinutes, fc.Dev.Source);
        var statsDev = _quant.Backtest.GetLoadStats(fc.DevRun);
        var statsFull = _quant.Backtest.GetLoadStats(cfg.Run);
        var effective = new Dictionary<string, string>(cfg.Run.Params ?? new Dictionary<string, string>());
        if (def?.UsesFixedStops != false)
        {
            effective["StopLossTicks"] = (cfg.Run.StopLossTicks ?? fc.Dev.Instrument.DefaultStopLossTicks).ToString(CultureInfo.InvariantCulture);
            effective["TakeProfitTicks"] = (cfg.Run.TakeProfitTicks ?? fc.Dev.Instrument.DefaultTakeProfitTicks).ToString(CultureInfo.InvariantCulture);
        }
        return new ReportProvenance
        {
            RunId = record.RunId,
            StrategyId = cfg.Run.Strategy,
            StrategyName = def?.Name ?? cfg.Run.Strategy,
            StrategyVersion = string.IsNullOrEmpty(fc.Opt.StrategyVersion) ? $"{cfg.Run.Strategy}@{code}" : fc.Opt.StrategyVersion,
            EngineVersion = $"OhlcBacktestEngine (bar-basiert) @ {code}",
            CodeVersion = code,
            DataSha256 = fp.Sha256,
            DataSource = fc.Dev.Source,
            DataKind = DataKindOf(fc.Dev.Source).Kind,
            Symbol = fc.Dev.Instrument.Symbol,
            TimeframeMinutes = fc.Dev.TimeframeMinutes,
            DataFrom = split.DataFrom, DataTo = split.DataTo, DevelopmentTo = split.DevelopmentToUtc,
            HoldoutFrom = split.HoldoutFrom, HoldoutTo = split.HoldoutTo,
            SessionsUsed = fc.DevSessions, HoldoutSessions = Math.Max(0, holdoutSessions),
            Parameters = effective,
            Quantity = cfg.Run.Quantity,
            InitialBalance = cfg.Run.InitialBalance,
            CostModel = new CostProfileDto(fc.Dev.Instrument.TickSize, fc.Dev.Instrument.TickValue, fc.Dev.Instrument.PointValue,
                fc.Dev.Instrument.Currency, fc.Base.FeePerSide, fc.Base.FeePerSide * 2, fc.Base.EffectiveSlippageTicks,
                fc.Base.EffectiveSlippageTicks * fc.Dev.Instrument.TickValue, cfg.Run.ApplyFees, fc.Dev.InstrumentIsExample, fc.Dev.FeeIsExample),
            FillModel = new[] { FillModelText },
            Seeds = new Dictionary<string, int>
            {
                ["Simulation (Monte Carlo/Bootstrap)"] = cfg.Seed,
                ["Verpasste Fills"] = cfg.Seed + 1,
                ["Zufallskontrolle"] = cfg.Seed + 2
            },
            AnalysisProfile = new Dictionary<string, string>
            {
                ["Profil"] = "Vollständiger Bericht (Ein-Knopf)",
                ["Holdout-Anteil"] = Inv(Math.Round(cfg.HoldoutFraction, 4)),
                ["Walk-forward Training/Test (Sessions)"] = $"{cfg.TrainSessions}/{cfg.TestSessions}",
                ["Session-Kalender"] = cfg.SessionCalendar,
                ["Auswahlkriterium"] = cfg.SelectionMetric,
                ["Mindesttrades je Training"] = fc.Opt.MinTrainTrades.ToString(CultureInfo.InvariantCulture),
                ["Mindesttrades (Einordnung)"] = fc.Opt.MinTrades.ToString(CultureInfo.InvariantCulture),
                ["Warmup (Kerzen)"] = cfg.WarmupBars.ToString(CultureInfo.InvariantCulture),
                ["Simulationen je Verfahren"] = fc.Opt.SimulationIterations.ToString(CultureInfo.InvariantCulture),
                ["Zufallskontrolle (Läufe)"] = fc.Opt.RandomControlIterations.ToString(CultureInfo.InvariantCulture),
                ["Verpasste Fills (Anteil)"] = Inv(fc.Opt.MissedFillShare),
                ["Kennzahlen-Frequenz"] = cfg.Options.Frequency,
                ["PBO-Blöcke (CSCV)"] = $"größte gerade Zahl ≤ OOS-Perioden/2, höchstens {cfg.OverfittingBlocks}, mindestens 4",
                ["Parallelität (Engine-Läufe)"] = fc.Opt.MaxParallelism.ToString(CultureInfo.InvariantCulture)
            },
            DataFileReads = statsDev.Loads + statsFull.Loads,
            DataCacheHits = statsDev.Hits + statsFull.Hits
        };
    }

    private static (string Kind, string[] Notes) DataKindOf(string source)
    {
        if (source.StartsWith("Sierra", StringComparison.OrdinalIgnoreCase))
            return ("OHLC-Zeitkerzen aus echten Trade-Ticks (Last-Preis)", new[]
            {
                "Die Kerzen sind aus den gehandelten Preisen (Last) der lokalen Sierra-Tickdatei gebildet — KEINE Bid/Ask-Quotes, keine Orderbuchtiefe.",
                "Jeder Tick-Datensatz zählt; identische echte Trades werden NICHT dedupliziert.",
                "Der Backtest rechnet auf Kerzen: ein exakter Intrabar-Verlauf oder Tick-Fill wird daraus NICHT abgeleitet."
            });
        return ("OHLC-Kerzen aus CSV", new[]
        {
            "Herkunft der Kerzen: CSV-Datei — ob Trade-Ticks oder Quotes zugrunde liegen, ist nicht bekannt.",
            "Aus OHLC allein wird kein Intrabar-Verlauf und kein Tick-Fill abgeleitet."
        });
    }

    // ---- 01 ----
    private ReportDataSection BuildDataSection(FullCtx fc, QuantAnalyzeResponse analysis)
    {
        var candles = fc.Dev.Candles;
        int tf = fc.Dev.TimeframeMinutes;
        var sessions = new List<SessionInfo>();
        int gaps = 0; double largest = 0;
        foreach (var g in candles.Select((c, i) => (c, i)).GroupBy(x => TradeDateCalendar.Key(x.c.OpenTime)))
        {
            var l = g.ToList();
            for (int k = 1; k < l.Count; k++)
            {
                double m = (l[k].c.OpenTime - l[k - 1].c.OpenTime).TotalMinutes;
                if (m > tf + 0.001) { gaps++; largest = Math.Max(largest, m); }
            }
            sessions.Add(new SessionInfo(g.Key, l.Count, l[0].c.OpenTime.ToUnixTimeMilliseconds(), l[^1].c.CloseTime.ToUnixTimeMilliseconds(), false));
        }
        double median = sessions.Count == 0 ? 0 : TradingBot.Quant.Statistics.Stats.Median(sessions.Select(s => (double)s.Bars).ToList());
        sessions = sessions.Select(s => s with { Short = s.Bars < 0.5 * median }).ToList();

        var dst = TradeDateCalendar.DstTransitions(candles[0].OpenTime, candles[^1].CloseTime)
            .Select(d => $"{d.Date:yyyy-MM-dd}: New York UTC{(d.Before.TotalHours >= 0 ? "+" : "")}{d.Before.TotalHours:0} → UTC{(d.After.TotalHours >= 0 ? "+" : "")}{d.After.TotalHours:0} — Session-Zeiten in UTC verschieben sich um eine Stunde; die Zuordnung zu Handelstagen erfolgt in Ortszeit (Zeitzonen-Datenbank).")
            .ToList();

        var checks = new List<CheckResult>();
        // (a) Abschneidetest: Lauf nur bis zur Mitte der Daten. Alle Trades, die VOR dieser Grenze abgeschlossen wurden, müssen
        //     identisch sein. Sähe die Strategie zukünftige Kerzen, würden sich frühe Signale/Trades ändern.
        int mid = candles.Count / 2;
        if (mid >= 20)
        {
            var prefix = candles.Take(mid).ToList();
            var cut = TryRun(fc.Dev, fc.DevRun, prefix, 0, out var err);
            if (cut is null) checks.Add(new CheckResult("Keine Zukunftsdaten in Signalen (Abschneidetest)", null, "Nicht ausführbar: " + err));
            else
            {
                var bound = prefix[^1].OpenTime;
                var a = fc.Base.Trades.Where(t => t.ExitTime < bound && t.ExitReason != OhlcExitReason.EndOfData).ToList();
                var b = cut.Trades.Where(t => t.ExitTime < bound && t.ExitReason != OhlcExitReason.EndOfData).ToList();
                bool same = a.Count == b.Count && a.Zip(b).All(p => p.First.EntryTime == p.Second.EntryTime && p.First.ExitTime == p.Second.ExitTime
                    && p.First.EntryPrice == p.Second.EntryPrice && p.First.ExitPrice == p.Second.ExitPrice && p.First.NetPnL == p.Second.NetPnL);
                checks.Add(new CheckResult("Keine Zukunftsdaten in Signalen (Abschneidetest)", same,
                    same ? $"Lauf auf den ersten {mid} von {candles.Count} Kerzen: alle {a.Count} vor {bound:yyyy-MM-dd HH:mm} UTC abgeschlossenen Trades sind identisch zum Gesamtlauf."
                         : $"ABWEICHUNG: Gesamtlauf {a.Count} vs. abgeschnittener Lauf {b.Count} Trades vor {bound:yyyy-MM-dd HH:mm} UTC — Verdacht auf Blick nach vorn."));
            }
        }
        else checks.Add(new CheckResult("Keine Zukunftsdaten in Signalen (Abschneidetest)", null, "Zu wenige Kerzen für den Abschneidetest (mind. 40)."));

        // (b) Keine rückwirkende Füllung: Einstieg frühestens am Open der Kerze NACH dem Signal-Kerzenschluss.
        int bad = 0;
        decimal slip = fc.Base.EffectiveSlippageTicks * fc.Dev.Instrument.TickSize;
        foreach (var t in fc.Base.Trades)
        {
            var eb = t.EntryBarIndex >= 0 && t.EntryBarIndex < candles.Count ? candles[t.EntryBarIndex] : null;
            bool ok = eb is not null && t.EntryBarIndex >= 1 && t.EntryTime == eb.OpenTime && t.EntryTime >= candles[t.EntryBarIndex - 1].CloseTime
                      && t.ExitTime >= t.EntryTime
                      && Math.Abs(t.EntryPrice - (t.Side == PositionSide.Long ? eb.Open + slip : eb.Open - slip)) <= fc.Dev.Instrument.TickSize / 2;
            if (!ok) bad++;
        }
        checks.Add(new CheckResult("Order nicht rückwirkend gefüllt", fc.Base.Trades.Count == 0 ? null : bad == 0,
            fc.Base.Trades.Count == 0 ? "Keine Trades zu prüfen." :
            bad == 0 ? $"Alle {fc.Base.Trades.Count} Einstiege liegen am Open der Kerze nach dem Signal-Kerzenschluss (Open ± Slippage)."
                     : $"{bad} von {fc.Base.Trades.Count} Einstiegen verletzen die Regel."));

        var q = analysis.DataQuality;
        bool dup = q?.Issues.Any(i => i.Code is "DUPLICATE_TIMESTAMP" or "UNORDERED_TIMESTAMP") == true;
        checks.Add(new CheckResult("Zeitstempel eindeutig und aufsteigend", q is null ? null : !dup,
            q is null ? "Datenqualität nicht verfügbar." : dup ? "Doppelte/ungeordnete Kerzen-Zeitstempel gefunden (siehe Datenfehler)." :
            "Kerzen streng aufsteigend, keine doppelten Zeitstempel. Gleiche Tick-Zeitstempel bleiben in der Aggregation in Dateireihenfolge erhalten."));
        int amb = fc.Base.AmbiguousTrades;
        checks.Add(new CheckResult("OHLC-Mehrdeutigkeit (Stop und Ziel in derselben Kerze)", null,
            amb == 0 ? "Keine mehrdeutigen Fälle im Basislauf." : $"{amb} Trade(s): Reihenfolge aus OHLC unbekannt, konservativ als Stop gewertet (Wirkung in Abschnitt 08)."));

        var (kind, notes) = DataKindOf(fc.Dev.Source);
        var limits = new List<string>
        {
            $"Historie im Bericht: {candles[0].OpenTime:yyyy-MM-dd HH:mm} – {candles[^1].CloseTime:yyyy-MM-dd HH:mm} UTC ({sessions.Count} Handelstage).",
            fc.Dev.Source.StartsWith("Sierra", StringComparison.OrdinalIgnoreCase)
                ? "Ein einzelner Kontrakt (lokale Datei MESM26): kein Kontraktwechsel innerhalb der Daten. Vor dem Front-Month-Wechsel ist der Kontrakt dünn gehandelt (Dateianfang)."
                : "Kontrakt/Rollwechsel aus der CSV nicht bekannt — Preissprünge zwischen Sessions werden unten als Verdacht gemeldet.",
            "Bid/Ask-Quotes und Orderbuch sind nicht vorhanden: Spread, Queue-Position, Latenz und verfügbare Größe sind NICHT beobachtbar."
        };
        if (q?.Issues.Any(i => i.Code == "ROLL_SUSPECTED") == true) limits.Add("Verdacht auf Kontraktwechsel/Preissprung (siehe Datenfehler).");
        int shortSessions = sessions.Count(s => s.Short);
        if (shortSessions > 0) limits.Add($"{shortSessions} Handelstag(e) mit weniger als der Hälfte der üblichen Kerzen (Feiertag, verkürzter Handel oder Datenlücke).");

        return new ReportDataSection
        {
            Quality = q,
            DataKind = kind,
            DataKindNotes = notes,
            SessionDefinition = "Handelstag = CME-Trade-Date: Beginn 17:00 Chicago-Zeit des Vortags, Ende 16:00 Chicago-Zeit; Sommerzeit über die Zeitzonen-Datenbank.",
            Sessions = sessions,
            MedianBarsPerSession = median,
            DstTransitions = dst,
            IntraSessionGaps = gaps,
            LargestGapMinutes = largest,
            TemporalChecks = checks,
            HistoryLimits = limits
        };
    }

    // ---- 05 ----
    private async Task<ReportSensitivitySection> BuildSensitivityAsync(FullCtx fc, CancellationToken ct)
    {
        var p = fc.Opt.SensitivityParameters;
        if (p.Count == 0)
            return new ReportSensitivitySection
            {
                Basis = "Basislauf", Applicable = false, MinTrades = fc.Opt.MinTrades,
                Reason = "Die Strategie hat keine einstellbaren, wirksamen numerischen Parameter — kein Parametersweep nötig."
            };

        double BaseOf(string name) => name switch
        {
            "StopLossTicks" => fc.DevRun.StopLossTicks ?? fc.Dev.Instrument.DefaultStopLossTicks,
            "TakeProfitTicks" => fc.DevRun.TakeProfitTicks ?? fc.Dev.Instrument.DefaultTakeProfitTicks,
            _ => fc.DevRun.Params.TryGetValue(name, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN
        };
        bool IsInt(string name) => name is "StopLossTicks" or "TakeProfitTicks" ||
                                   (fc.DevRun.Params.TryGetValue(name, out var v) && int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _));
        BacktestRunRequest With(BacktestRunRequest r, string name, double value)
        {
            string s = IsInt(name) ? ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture) : value.ToString("0.######", CultureInfo.InvariantCulture);
            return name switch
            {
                "StopLossTicks" => r with { StopLossTicks = (int)Math.Round(value) },
                "TakeProfitTicks" => r with { TakeProfitTicks = (int)Math.Round(value) },
                _ => r with { Params = new Dictionary<string, string>(r.Params) { [name] = s } }
            };
        }

        var sessions = fc.DevSessions;
        var jobs = new List<(string Kind, int A, int B, BacktestRunRequest Run)>();
        var paramValues = new List<(string Name, double Base, IReadOnlyList<double> Values)>();
        foreach (var name in p)
        {
            double b = BaseOf(name);
            if (double.IsNaN(b)) continue;
            var vals = SensitivityRules.Neighbors(b, IsInt(name));
            paramValues.Add((name, b, vals));
        }
        var pairValues = new List<(string X, string Y, IReadOnlyList<double> Xs, IReadOnlyList<double> Ys, double Bx, double By)>();
        foreach (var pair in fc.Opt.ParameterPairs)
        {
            var px = paramValues.FirstOrDefault(v => v.Name == pair[0]);
            var py = paramValues.FirstOrDefault(v => v.Name == pair[1]);
            if (px.Name is null || py.Name is null) continue;
            pairValues.Add((px.Name, py.Name, px.Values, py.Values, px.Base, py.Base));
        }

        // Alle Varianten einmal sammeln (identische Parametersätze werden nur einmal gerechnet).
        var cache = new System.Collections.Concurrent.ConcurrentDictionary<string, (bool Valid, string? Err, OhlcBacktestResult? Res)>();
        string KeyOf(BacktestRunRequest r) => string.Join(";", r.Params.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + "=" + k.Value))
                                              + $";SL={r.StopLossTicks};TP={r.TakeProfitTicks}";
        var runs = new List<BacktestRunRequest>();
        foreach (var (name, _, vals) in paramValues) foreach (var v in vals) runs.Add(With(fc.DevRun, name, v));
        foreach (var pv in pairValues) foreach (var x in pv.Xs) foreach (var y in pv.Ys) runs.Add(With(With(fc.DevRun, pv.X, x), pv.Y, y));
        var unique = runs.GroupBy(KeyOf).Select(g => g.First()).ToList();

        await Parallel.ForEachAsync(unique, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, fc.Opt.MaxParallelism), CancellationToken = ct },
            (run, token) =>
            {
                token.ThrowIfCancellationRequested();
                var res = TryRun(fc.Dev, run, null, 0, out var err);
                cache[KeyOf(run)] = (res is not null, err, res);
                return ValueTask.CompletedTask;
            });
        fc.SensitivityRuns = unique.Count;
        fc.SensitivityVariants = unique.Count;

        SensitivityPoint Point(BacktestRunRequest run, double value, bool isBase)
        {
            var (valid, err, res) = cache[KeyOf(run)];
            if (!valid || res is null) return new SensitivityPoint(value, isBase, false, err, 0, 0, null, 0, 0, sessions.Count, Array.Empty<decimal>());
            var trades = ToReportTrades(res, fc.Dev);
            var bySession = trades.GroupBy(t => t.SessionKey).ToDictionary(g => g.Key, g => g.Sum(t => t.NetPnL));
            var nets = sessions.Select(s => bySession.TryGetValue(s, out var v) ? v : 0m).ToList();
            var st = TradeStatistics.Compute(trades, res.Data.EvaluatedBars);
            var dd = DrawdownAnalysis.Analyze(DrawdownAnalysis.ClosedTradeCurve(trades, (double)run.InitialBalance, fc.Dev.Candles[0].OpenTime), "");
            return new SensitivityPoint(value, isBase, true, null, st.NetPnL, st.Trades, st.ProfitFactor.Value, dd.MaxDrawdown,
                nets.Count(n => n > 0), sessions.Count, nets);
        }

        var parms = new List<SensitivityParam>();
        foreach (var (name, b, vals) in paramValues)
        {
            var pts = vals.Select(v => Point(With(fc.DevRun, name, v), v, Math.Abs(v - b) < 1e-9)).ToList();
            int bi = pts.FindIndex(x => x.IsBase);
            var cls = SensitivityRules.Classify(pts.Select(x => (x.Value, x.Net, x.Trades, x.Valid)).ToList(), bi, fc.Opt.MinTrades);
            parms.Add(new SensitivityParam(name, b, pts, cls));
        }
        var pairs = new List<SensitivityPair>();
        foreach (var pv in pairValues)
        {
            var cells = new List<SensitivityCell>();
            foreach (var x in pv.Xs)
                foreach (var y in pv.Ys)
                {
                    var (valid, err, res) = cache[KeyOf(With(With(fc.DevRun, pv.X, x), pv.Y, y))];
                    cells.Add(new SensitivityCell(x, y, valid, res?.Statistics.NetProfit ?? 0, res?.Trades.Count ?? 0, err));
                }
            pairs.Add(new SensitivityPair(pv.X, pv.Y, pv.Xs, pv.Ys, cells, pv.Bx, pv.By));
        }

        return new ReportSensitivitySection
        {
            Basis = "Basislauf (Entwicklungsbereich) — je Variante ein vollständiger Engine-Lauf auf denselben Kerzen",
            Applicable = parms.Count > 0,
            Reason = parms.Count > 0 ? null : "Keine numerisch auswertbaren Parameter.",
            MinTrades = fc.Opt.MinTrades,
            SessionKeys = sessions,
            Parameters = parms,
            Pairs = pairs,
            EngineRuns = unique.Count
        };
    }

    // ---- 06 ----
    private async Task<ReportOosSection> BuildOosAsync(FullCtx fc, ResearchRunRecord record, BacktestApiService.RunContext full, CancellationToken ct)
    {
        var cfg = fc.Cfg;
        int S = fc.DevSessions.Count;
        var grid = cfg.Candidates.Select(c => (IReadOnlyDictionary<string, string>)c).ToList();
        var idToParams = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        for (int i = 0; i < cfg.Candidates.Count; i++) idToParams[QuantApiService.CandidateId(cfg.Candidates[i], i)] = cfg.Candidates[i];

        int splitTrain = Math.Max(fc.Opt.FixedSplitMinTrainSessions, (int)Math.Floor(S * fc.Opt.FixedSplitTrainShare));
        int splitTest = Math.Max(0, S - splitTrain);
        var specs = new List<(string Key, string Label, string Mode, int Train, int Test, int Needed, string Suffix)>
        {
            ("split", "Fester Train/Test-Split", "Rolling", splitTrain, splitTest,
                fc.Opt.FixedSplitMinTrainSessions + fc.Opt.FixedSplitMinTestSessions, "-split"),
            ("rolling", "Rollierender Walk-forward", "Rolling", cfg.TrainSessions, cfg.TestSessions, cfg.TrainSessions + cfg.TestSessions, ""),
            ("anchored", "Anwachsender Walk-forward", "Anchored", cfg.TrainSessions, cfg.TestSessions, cfg.TrainSessions + cfg.TestSessions, "-anchored")
        };
        if (splitTest < fc.Opt.FixedSplitMinTestSessions) splitTest = fc.Opt.FixedSplitMinTestSessions;

        var variants = new List<OosVariant>();
        foreach (var sp in specs)
        {
            ct.ThrowIfCancellationRequested();
            var baseVariant = new OosVariant
            {
                Key = sp.Key, Label = sp.Label, SessionsNeeded = sp.Needed, SessionsAvailable = S, TrainSessions = sp.Train, TestSessions = sp.Test
            };
            if (S < sp.Needed)
            {
                variants.Add(baseVariant with { Status = "InsufficientData", Reason = $"{sp.Needed} Sessions benötigt, {S} vorhanden." });
                continue;
            }
            var wfReq = BuildWalkForward(cfg) with
            {
                Mode = sp.Mode, TrainSessions = sp.Train, TestSessions = sp.Key == "split" ? S - sp.Train : sp.Test,
                Campaign = cfg.Campaign with { Id = cfg.Campaign.Id + sp.Suffix, Name = cfg.Campaign.Name + " · " + sp.Label }
            };
            var wf = await _quant.WalkForwardAsync(wfReq, null, ct);
            fc.WfVariantsRun++;
            if (!wf.Ok)
            {
                bool notEnough = wf.Error?.Contains("Kein vollständiges", StringComparison.OrdinalIgnoreCase) == true;
                variants.Add(baseVariant with
                {
                    Status = notEnough ? "InsufficientData" : "Failed",
                    Reason = (wf.Error ?? "Walk-forward fehlgeschlagen.") + (wf.Notes.Count > 0 ? " " + string.Join(" ", wf.Notes.Take(2)) : "")
                });
                continue;
            }
            fc.WfTrials += wf.TrialsRecorded;
            fc.WfSelectionAttempts += wf.Folds.Count;
            if (sp.Key == "rolling") { fc.PrimaryWf = wf; fc.PrimaryWfRequest = wfReq; }

            // OOS je Fenster in GELD nachrechnen: gewählte Parameter und unveränderte Basisstrategie auf DENSELBEN Testkerzen
            // (gleiche Slicing-/Warmup-Regel wie der Walk-forward-Evaluator).
            var folds = new List<OosFold>();
            var oosTrades = new List<ReportTrade>();
            var baseTradesSame = new List<ReportTrade>();
            var oosSessions = new List<SessionResult>();
            var baseSessions = new List<SessionResult>();
            foreach (var f in wf.Folds)
            {
                var slice = full.Candles.Where(c => c.CloseTime.ToUnixTimeMilliseconds() >= f.TestFromT && c.CloseTime.ToUnixTimeMilliseconds() <= f.TestToT).ToList();
                var trainKeys = full.Candles.Where(c => c.CloseTime.ToUnixTimeMilliseconds() >= f.TrainFromT && c.CloseTime.ToUnixTimeMilliseconds() <= f.TrainToT)
                    .Select(c => TradeDateCalendar.Key(c.OpenTime)).Distinct().Count();
                var testKeys = slice.Select(c => TradeDateCalendar.Key(c.OpenTime)).Distinct().ToList();
                Dictionary<string, string>? selParams = f.SelectedCandidate is not null && idToParams.TryGetValue(f.SelectedCandidate, out var sp2) ? sp2 : null;
                decimal? testNet = null; int testTrades = 0;
                List<ReportTrade> selTrades = new();
                if (selParams is not null)
                {
                    var r = TryRun(full, cfg.Run with { Params = new Dictionary<string, string>(selParams) }, slice, cfg.WarmupBars, out _);
                    if (r is not null) { selTrades = ToReportTrades(r, full); testNet = r.Statistics.NetProfit; testTrades = r.Trades.Count; }
                }
                var rb = TryRun(full, cfg.Run, slice, cfg.WarmupBars, out _);
                var bTrades = rb is null ? new List<ReportTrade>() : ToReportTrades(rb, full);
                oosTrades.AddRange(selTrades);
                baseTradesSame.AddRange(bTrades);
                foreach (var k in testKeys)
                {
                    oosSessions.Add(new SessionResult(k, selTrades.Where(t => t.SessionKey == k).Sum(t => t.NetPnL), selTrades.Count(t => t.SessionKey == k)));
                    baseSessions.Add(new SessionResult(k, bTrades.Where(t => t.SessionKey == k).Sum(t => t.NetPnL), bTrades.Count(t => t.SessionKey == k)));
                }
                folds.Add(new OosFold
                {
                    Index = f.Index, TrainFromT = f.TrainFromT, TrainToT = f.TrainToT, TrainSessions = trainKeys,
                    TestFromT = f.TestFromT, TestToT = f.TestToT, TestSessions = testKeys.Count,
                    SelectedCandidate = f.SelectedCandidate, SelectedParameters = selParams, TrainValue = f.TrainValue,
                    TestNet = testNet, TestTrades = testTrades, BaseNet = bTrades.Sum(t => t.NetPnL), BaseTrades = bTrades.Count, Note = f.Note
                });
            }
            var t0 = oosTrades.Count > 0 ? oosTrades.Min(t => t.EntryTime) : DateTimeOffset.UtcNow;
            var oosDd = DrawdownAnalysis.Analyze(DrawdownAnalysis.ClosedTradeCurve(oosTrades, (double)cfg.Run.InitialBalance, t0), "OOS");
            var baseDd = DrawdownAnalysis.Analyze(DrawdownAnalysis.ClosedTradeCurve(baseTradesSame, (double)cfg.Run.InitialBalance, t0), "Basis");
            var v = baseVariant with
            {
                Status = "Completed",
                TestSessions = sp.Key == "split" ? S - sp.Train : sp.Test,
                CampaignId = wf.CampaignId, TrialsRecorded = wf.TrialsRecorded, Folds = folds,
                OosNet = oosTrades.Sum(t => t.NetPnL), OosTrades = oosTrades.Count, OosSessions = oosSessions.Select(s => s.Key).Distinct().Count(),
                BaseNetSameSessions = baseTradesSame.Sum(t => t.NetPnL), BaseTradesSameSessions = baseTradesSame.Count,
                OosMaxDrawdown = oosDd.MaxDrawdown, BaseMaxDrawdownSameSessions = baseDd.MaxDrawdown,
                OosSessionResults = oosSessions, BaseSessionResults = baseSessions,
                Reason = wf.Folds.Any(f => f.SelectedCandidate is null) ? "In mindestens einem Fenster erfüllte kein Kandidat die Auswahlregel (kein OOS-Ergebnis für dieses Fenster)." : null
            };
            variants.Add(v);
            if (sp.Key == "rolling") { fc.PrimaryOos = v; fc.OosTrades = oosTrades; fc.OosSessions = oosSessions; }
        }
        // Fällt der rollierende Walk-forward aus, dient die erste erfolgreiche Variante als Primärbasis.
        if (fc.PrimaryOos is null)
        {
            var alt = variants.FirstOrDefault(x => x.Status == "Completed");
            if (alt is not null) { fc.PrimaryOos = alt; fc.OosSessions = alt.OosSessionResults.ToList(); }
        }

        return new ReportOosSection
        {
            SelectionRule = $"Je Fenster wird ausschließlich im TRAINING der Kandidat mit dem höchsten Wert von „{cfg.SelectionMetric}“ gewählt, " +
                            $"eingefroren und unverändert auf die folgenden ungesehenen Sessions angewendet. Kandidaten mit weniger als {fc.Opt.MinTrainTrades} Trades im Training nehmen nicht teil.",
            TieRule = "Gleichstand: lexikografisch kleinste Kandidaten-Id (deterministisch). Kandidaten ohne gültigen Wert zählen nicht als schlechtestes Ergebnis.",
            MinTrainTrades = fc.Opt.MinTrainTrades,
            Grid = grid,
            WarmupBars = cfg.WarmupBars,
            LeakageRules = $"Jedes Trainings- und Testfenster wird getrennt simuliert; offene Positionen werden am Fensterende geschlossen — kein Trade überlappt eine Grenze. " +
                           $"Warm-up: die ersten {cfg.WarmupBars} Kerzen jedes Fensters dienen nur der Indikator-Initialisierung (keine Ausführung) und nutzen ausschließlich Daten dieses Fensters. " +
                           "Fenstergrenzen liegen auf Handelstag-Anfängen; ein Sperrabstand ist dadurch nicht nötig (Purge/Embargo = 0, gespeichert).",
            HoldoutNote = record.HoldoutBars > 0
                ? $"Der finale Holdout ({record.HoldoutFrom:yyyy-MM-dd} – {record.HoldoutTo:yyyy-MM-dd}) ist reserviert und wird in diesem Bericht NICHT verwendet. Er bleibt der bewussten Schlussprüfung einer eingefrorenen Strategieversion vorbehalten."
                : "Kein finaler Holdout reserviert (Holdout-Anteil 0) — alle Daten dienen der Entwicklung.",
            Variants = variants,
            PrimaryVariant = fc.PrimaryOos?.Key
        };
    }

    // ---- 07 ----
    private static ReportSimulationSection BuildSimulation(FullCtx fc, CancellationToken ct)
    {
        bool useOos = fc.OosTrades.Count >= fc.Opt.MinOosTradesForSimulation;
        var trades = useOos ? fc.OosTrades : fc.BaseTrades;
        string basis = useOos ? $"OOS-Trades des rollierenden Walk-forward ({trades.Count})" : $"Basislauf-Trades ({trades.Count})";
        string why = useOos ? "OOS-Daten werden bevorzugt (genug OOS-Trades)."
            : $"OOS-Daten bevorzugt, aber nur {fc.OosTrades.Count} OOS-Trades (mind. {fc.Opt.MinOosTradesForSimulation}) — Simulation auf dem Basislauf.";
        var obs = trades.OrderBy(t => t.ExitTime).ThenBy(t => t.Index).Select(t => (double)t.NetPnL).ToList();
        double cap = (double)fc.DevRun.InitialBalance;
        int it = fc.Opt.SimulationIterations, seed = fc.Cfg.Seed;
        var methods = new List<SimulationResult>
        {
            TradeResampling.Run("permutation", "Umordnung der Netto-Trades",
                "Wie stark hängt der Drawdown von der zufälligen Reihenfolge derselben Trades ab? (End-Netto bleibt bei fester Größe identisch.)",
                "Netto je Trade", obs, ResamplingMethod.Permutation, null, it, seed, cap, ct),
            TradeResampling.Run("bootstrap", "Bootstrap mit Zurücklegen",
                "Wie breit streuen End-Netto und Erwartungswert, wenn man erneut aus der beobachteten Stichprobe zieht?",
                "Netto je Trade", obs, ResamplingMethod.MovingBlock, 1, it, seed, cap, ct)
        };
        foreach (var bl in TradeResampling.BlockLengths(obs.Count))
            methods.Add(TradeResampling.Run($"block{bl}", $"Block-Bootstrap (Blocklänge {bl} Trades)",
                "Bleiben lokale Verlustphasen erhalten, wenn zusammenhängende Tradeblöcke gezogen werden? (Sensitivität über mehrere Blocklängen)",
                "Netto je Trade", obs, ResamplingMethod.MovingBlock, bl, it, seed, cap, ct));

        // Ganze Sessions ziehen (inkl. Tage ohne Trade = 0).
        var sessionKeys = useOos ? fc.OosSessions.Select(s => s.Key).Distinct().ToList() : fc.DevSessions;
        var bySession = trades.GroupBy(t => t.SessionKey).ToDictionary(g => g.Key, g => (double)g.Sum(t => t.NetPnL));
        var sessionObs = sessionKeys.Select(k => bySession.TryGetValue(k, out var v) ? v : 0.0).ToList();
        methods.Add(TradeResampling.Run("sessions", "Session-Bootstrap (ganze Handelstage)",
            "Wie streut das Ergebnis, wenn ganze Handelstage gezogen werden — Verlustcluster innerhalb eines Tages bleiben erhalten?",
            "Netto je Handelstag", sessionObs, ResamplingMethod.MovingBlock, 1, it, seed, cap, ct));
        int sb = TradingBot.Quant.MonteCarlo.Resampling.DefaultBlockLength(Math.Max(1, sessionObs.Count));
        if (sb >= 2 && sb < sessionObs.Count)
            methods.Add(TradeResampling.Run("sessionblocks", $"Session-Block-Bootstrap (Blöcke à {sb} Handelstage)",
                "Bleiben mehrtägige Verlustphasen erhalten, wenn zusammenhängende Handelstage gezogen werden?",
                "Netto je Handelstag", sessionObs, ResamplingMethod.MovingBlock, sb, it, seed, cap, ct));

        // Ausführungsstress: zusätzliche Slippage (deterministisch, Originalreihenfolge) + verpasste Fills (Simulation).
        decimal tv = fc.Dev.Instrument.TickValue;
        var stress = new List<StressRow>();
        foreach (var (id, label, ticks) in new[] { ("orig", "Original", 0m), ("slip1", "+1 Tick je Market-Fill", 1m), ("slip2", "+2 Ticks je Market-Fill", 2m), ("slip4", "+4 Ticks je Market-Fill", 4m) })
        {
            var adj = trades.OrderBy(t => t.ExitTime).ThenBy(t => t.Index)
                .Select(t => t with { NetPnL = t.NetPnL - ticks * tv * t.Quantity * t.MarketSides }).ToList();
            var dd = DrawdownAnalysis.Analyze(DrawdownAnalysis.ClosedTradeCurve(adj, cap, adj.Count > 0 ? adj[0].EntryTime : DateTimeOffset.UtcNow), "");
            stress.Add(new StressRow(id, label, adj.Sum(t => t.NetPnL), dd.MaxDrawdown, adj.Count, ticks == 0 ? "Original" : "Hypothetisch"));
        }
        var missed = TradeResampling.RunMissedFills(obs, fc.Opt.MissedFillShare, it, seed + 1, cap, ct);
        var delay = fc.Robustness?.Blocks.FirstOrDefault(b => b.Dimension == "ExecutionDelay");

        return new ReportSimulationSection
        {
            Basis = basis, BasisReason = why, Iterations = it, Seed = seed, Methods = methods,
            ExecutionStress = stress, MissedFills = missed, DelayedFills = delay,
            Notes = new[]
            {
                "Feste Kontraktzahl: Umordnen und Ziehen fertiger Dollar-Ergebnisse ist zulässig, weil kein Trade die Größe eines späteren beeinflusst.",
                "Ein simuliertes Perzentil (z. B. 95 % Drawdown) ist KEINE Verlustobergrenze — es beschreibt nur die beobachtete Stichprobe unter den Resampling-Annahmen.",
                "Verzögerte Fills: Engine-Neuläufe mit um n Kerzen verzögerter Ausführung (vorhandene Robustheitsprüfung, Kennzahl Netto)."
            }
        };
    }

    // ---- 08 ----
    private static ReportCostSection BuildCosts(FullCtx fc)
    {
        var be = CostAnalysis.Compute(fc.BaseTrades, fc.Dev.Instrument.TickValue, fc.Base.FeePerSide);
        var amb = new List<AmbiguityRow>();
        decimal pv = fc.Dev.Instrument.PointValue;
        foreach (var t in fc.Base.Trades.Select((t, i) => (t, i)).Where(x => x.t.Ambiguous))
        {
            decimal dir = t.t.Side == PositionSide.Long ? 1 : -1;
            decimal altGross = (t.t.TakeProfitPrice - t.t.EntryPrice) * dir * pv * t.t.Quantity;
            amb.Add(new AmbiguityRow(t.i, t.t.NetPnL, altGross - t.t.Fees));
        }
        return new ReportCostSection
        {
            Basis = "Basislauf (Entwicklungsbereich)",
            Breakeven = be,
            CostGrid = fc.Robustness?.Blocks.FirstOrDefault(b => b.Dimension == "Costs"),
            AmbiguousTrades = amb.Count,
            AmbiguousAssumedNet = amb.Sum(a => a.AssumedNet),
            AmbiguousAlternativeNet = amb.Sum(a => a.AlternativeNet),
            AmbiguousRows = amb,
            DataLimits = new[]
            {
                "Spread, Latenz und Liquidität sind mit Kerzen aus Trade-Ticks NICHT beobachtbar — zusätzliche Slippage ist ein ausdrücklich hypothetisches Szenario.",
                "Es wird keine Orderbuchposition, kein garantierter Limit-Fill und keine verfügbare Größe behauptet.",
                "Gebühren-Szenarien skalieren das Gebührenprofil aus config/fees; Slippage-Szenarien betreffen nur Market-Fills (Limit-Ziele bleiben ohne Slippage)."
            }
        };
    }

    // ---- 09 ----
    private static ReportStabilitySection BuildStability(FullCtx fc)
    {
        var tr = fc.BaseTrades;
        int min = fc.Opt.MinTradesPerGroup;
        string Wd(ReportTrade t)
        {
            var d = DateOnly.Parse(t.SessionKey, CultureInfo.InvariantCulture).DayOfWeek;
            int n = d == DayOfWeek.Sunday ? 7 : (int)d;
            return $"{n} {CultureInfo.GetCultureInfo("de-DE").DateTimeFormat.GetAbbreviatedDayName(d)}";
        }
        var breakdowns = new List<GroupBreakdown>
        {
            new("Tag", "Handelstag (CME-Trade-Date) des Einstiegs", StabilityAnalysis.Group(tr, t => t.SessionKey, min)),
            new("Woche", "ISO-Kalenderwoche des Handelstags", StabilityAnalysis.Group(tr, t => TradeDateCalendar.WeekKey(DateOnly.Parse(t.SessionKey, CultureInfo.InvariantCulture)), min)),
            new("Monat", "Kalendermonat des Handelstags", StabilityAnalysis.Group(tr, t => TradeDateCalendar.MonthKey(DateOnly.Parse(t.SessionKey, CultureInfo.InvariantCulture)), min)),
            new("Wochentag", "Wochentag des Handelstags", StabilityAnalysis.Group(tr, Wd, min)),
            new("Uhrzeit", "Stunde des Einstiegs in New-York-Zeit", StabilityAnalysis.Group(tr, t => $"{TradeDateCalendar.ToNewYork(t.EntryTime).Hour:00}:00 ET", min)),
            new("Session", "Sessionteil des Einstiegs (New-York-Zeit)", StabilityAnalysis.Group(tr, t => TradeDateCalendar.SessionPart(t.EntryTime), min)),
            new("Richtung", "Long/Short", StabilityAnalysis.Group(tr, t => t.IsLong ? "Long" : "Short", min))
        };
        var regimes = tr.Select(t => (t, r: RegimeClassifier.Classify(fc.Dev.Candles, t.EntryBarIndex))).ToList();
        var regimeBreakdowns = new List<GroupBreakdown>
        {
            new("Volatilität", RegimeClassifier.VolDefinition, StabilityAnalysis.Group(regimes.Select(x => x.t).ToList(),
                t => regimes.First(x => x.t.Index == t.Index).r.Vol, min)),
            new("Trend/Seitwärts", RegimeClassifier.TrendDefinition, StabilityAnalysis.Group(regimes.Select(x => x.t).ToList(),
                t => regimes.First(x => x.t.Index == t.Index).r.Trend, min))
        };
        return new ReportStabilitySection
        {
            Basis = "Basislauf (Entwicklungsbereich)",
            MinTradesPerGroup = min,
            Breakdowns = breakdowns,
            Regimes = regimeBreakdowns,
            RollingWindow = fc.Opt.RollingWindowTrades,
            Rolling = StabilityAnalysis.Rolling(tr, fc.Opt.RollingWindowTrades),
            Notes = new[] { $"Gruppen mit weniger als {min} Trades sind als „dünn“ markiert und erlauben keine Aussage." }
        };
    }

    // ---- 11 ----
    private async Task<ReportControlSection> BuildControlsAsync(FullCtx fc, QuantAnalyzeResponse? analysis, CancellationToken ct)
    {
        var rows = new List<ControlRow>();
        double cap = (double)fc.DevRun.InitialBalance;
        var start = fc.Dev.Candles[0].OpenTime;
        double Dd(IReadOnlyList<ReportTrade> t) => DrawdownAnalysis.Analyze(DrawdownAnalysis.ClosedTradeCurve(t, cap, start), "").MaxDrawdown;

        rows.Add(new ControlRow("none", "Kein Trade", 0, 0, 0, "Basislauf-Tage", "Nullreferenz: kein Risiko, keine Kosten."));
        rows.Add(new ControlRow("base", "Strategie mit Basisparametern (nicht optimiert)", fc.Base.Statistics.NetProfit, fc.BaseTrades.Count,
            Dd(fc.BaseTrades), "Basislauf", "Unveränderte Eingabeparameter, keine Auswahl."));

        string refId = fc.Opt.ReferenceStrategy;
        if (refId == "auto") refId = string.Equals(fc.Cfg.Run.Strategy, "donchian", StringComparison.OrdinalIgnoreCase) ? "movingaverage" : "donchian";
        var refDef = _quant.Backtest.GetStrategies().FirstOrDefault(s => s.Id == refId);
        if (refDef is not null)
        {
            var refRun = fc.DevRun with { Strategy = refId, Params = refDef.Params.ToDictionary(p => p.Key, p => p.Default) };
            var rr = TryRun(fc.Dev, refRun, null, 0, out var err);
            rows.Add(rr is null
                ? new ControlRow("reference", $"Referenz: {refDef.Name} (Standardparameter)", null, null, null, "Basislauf-Tage", "Nicht berechenbar: " + err)
                : new ControlRow("reference", $"Referenz: {refDef.Name} (Standardparameter)", rr.Statistics.NetProfit, rr.Trades.Count,
                    Dd(ToReportTrades(rr, fc.Dev)), "Basislauf-Tage", "Feste, nicht optimierte Referenz mit denselben Kosten und derselben Kontraktzahl."));
        }
        if (fc.PrimaryOos is { } oos)
        {
            rows.Add(new ControlRow("oos", "OOS: im Training gewählte Parameter", oos.OosNet, oos.OosTrades, oos.OosMaxDrawdown,
                $"OOS ({oos.OosSessions} Testsessions)", "Verkettete Testfenster."));
            rows.Add(new ControlRow("oosbase", "OOS-Tage: unveränderte Basisstrategie", oos.BaseNetSameSessions, oos.BaseTradesSameSessions,
                oos.BaseMaxDrawdownSameSessions, $"Dieselben {oos.OosSessions} Testsessions", "Zeigt, ob die Auswahl gegenüber der festen Strategie etwas bringt."));
        }

        decimal slip = fc.Base.EffectiveSlippageTicks * fc.Dev.Instrument.TickSize;
        var random = RandomEntryControl.Run(fc.Dev.Candles, 0, fc.Dev.Candles.Count - 1, fc.BaseTrades, slip, fc.Base.FeePerSide,
            fc.Dev.Instrument.PointValue, fc.DevRun.ApplyFees, fc.Opt.RandomControlIterations, fc.Cfg.Seed + 2, ct);

        QuantOverfittingResponse? of = null;
        if (fc.PrimaryWf is not null && fc.PrimaryWfRequest is not null)
            of = await _quant.ComputeOverfittingFromWalkForwardAsync(fc.PrimaryWf, fc.PrimaryWfRequest,
                PboBlocks(fc.PrimaryWf.OosT.Count, fc.Cfg.OverfittingBlocks), fc.Cfg.EstimateEffectiveTrials, ct);

        int wfCandidates = fc.Cfg.Candidates.Count;
        var trials = new TrialHistory(
            VariantsTested: fc.SensitivityVariants + wfCandidates,
            SelectionAttempts: fc.WfSelectionAttempts,
            RegistryTrials: fc.WfTrials,
            Breakdown: new[]
            {
                $"Sensitivität: {fc.SensitivityVariants} Parametervarianten (je ein Engine-Lauf).",
                $"Walk-forward-Raster: {wfCandidates} Kandidaten in {fc.WfVariantsRun} Varianten (fester Split/rollierend/anwachsend).",
                $"Auswahlentscheidungen (Fenster): {fc.WfSelectionAttempts}; im Versuchsregister erfasst: {fc.WfTrials} Trials."
            });

        var notes = new List<string>();
        if (analysis?.Benchmark is null) notes.Add("Keine externe Benchmark-Reihe gewählt — es wird keine Kurve erfunden.");
        if (of is null) notes.Add("PBO/PSR/DSR nicht berechnet: kein erfolgreicher rollierender Walk-forward.");
        else
        {
            if (of.Pbo is null) notes.Add("Probability of Backtest Overfitting nicht ausgewiesen: " + (of.PboUnavailableReason ?? "Voraussetzungen nicht erfüllt."));
            if (of.Dsr is null) notes.Add("Deflated Sharpe Ratio nicht ausgewiesen: " + (of.DsrUnavailableReason ?? "Voraussetzungen nicht erfüllt."));
        }
        notes.Add("Purged/embargoed Cross-Validation wird nicht zusätzlich angeboten: die Fenster sind bereits zeitlich getrennt und Positionen enden an jeder Fenstergrenze.");

        return new ReportControlSection
        {
            Basis = "Basislauf-Tage und OOS-Tage (je Zeile angegeben)",
            Rows = rows, Random = random, Benchmark = analysis?.Benchmark, Trials = trials, Overfitting = of, Notes = notes
        };
    }

    /// <summary>Vorab festgelegte Regel für die CSCV-Blockzahl S: größte gerade Zahl ≤ Perioden/2 (mind. 2 Perioden je
    /// Block), höchstens der konfigurierte Wert. Unter 4 Blöcken bleibt PBO ehrlich „nicht ausgewiesen“.</summary>
    public static int PboBlocks(int periods, int configured)
    {
        int s = (periods / 2) / 2 * 2;
        return Math.Max(4, Math.Min(configured, s));
    }

    // ---- 12 ----
    private static ReportTradeSection BuildTrades(FullCtx fc)
    {
        decimal pv = fc.Dev.Instrument.PointValue;
        var rows = fc.Base.Trades.Select((t, i) =>
        {
            var rt = fc.BaseTrades[i];
            var ex = ExcursionAnalysis.Compute(fc.Dev.Candles, rt, pv);
            return new TradeRow(i, t.Side == PositionSide.Long ? "Long" : "Short", t.EntryTime.ToUnixTimeMilliseconds(), t.ExitTime.ToUnixTimeMilliseconds(),
                t.EntryPrice, t.ExitPrice, t.EntryBarIndex, t.ExitBarIndex, t.GrossPnL, t.Fees, t.NetPnL, t.ExitReason.ToString(), t.Ambiguous,
                t.ExitReason == OhlcExitReason.EndOfData, rt.SessionKey, ex.Mae, ex.Mfe, ex.LowerBound, ex.Basis, t.StopLossPrice, t.TakeProfitPrice);
        }).ToList();
        return new ReportTradeSection
        {
            Basis = "Basislauf (Entwicklungsbereich)",
            CandleRequest = fc.DevRun,
            Trades = rows,
            ExcursionDefinition = $"SCHÄTZUNG aus {fc.Dev.TimeframeMinutes}-Minuten-Kerzen (nicht tickgenau). MAE/MFE = größte Bewegung gegen bzw. für die Position zwischen Ein- und Ausstieg (Geld, inkl. Kontraktzahl). " +
                                  "Nur Kerzen, die vollständig gehalten wurden, plus die tatsächlichen Fill-Preise. Eine nur teilweise gehaltene Ausstiegskerze " +
                                  "wird ausgelassen (Wert = Untergrenze) — nie das Hoch/Tief einer ganzen Kerze, die nur teilweise im Trade lag.",
            TickReplayNote = fc.Dev.Source.StartsWith("Sierra", StringComparison.OrdinalIgnoreCase)
                ? "Tick-genaue Prüfung: Die vorhandene Tick-Replay-Sammlung (ATAS, MNQ New York) deckt dieses Instrument/diese Tage nicht ab — Prüfung im Bar-Replay."
                : "Tick-genaue Prüfung nur mit passender Tick-Sammlung möglich — Prüfung im Bar-Replay."
        };
    }

    // ---- Übersicht ----
    private static ReportOverview BuildOverview(FullCtx fc, BacktestReport r) => BuildOverviewStatic(r, fc);

    private static ReportOverview BuildOverviewStatic(BacktestReport r, FullCtx? fc)
    {
        var figs = new List<OverviewFigure>();
        if (r.Result is { } res)
            figs.Add(new OverviewFigure("net", "Netto nach Kosten", (double)res.Stats.NetPnL, "currency", res.Basis,
                Detail: $"Brutto {res.Stats.GrossPnL:0.00} · Gebühren {res.Stats.Fees:0.00} · enthaltene Slippage {res.Stats.ModeledSlippage:0.00}"));
        else figs.Add(new OverviewFigure("net", "Netto nach Kosten", null, "currency", "Basislauf", "Noch nicht berechnet."));

        if (r.Equity is { } eq)
            figs.Add(new OverviewFigure("dd", "Beobachteter max. Drawdown", eq.ClosedTrades.MaxDrawdown, "currency", "Basislauf (abgeschlossene Trades)",
                Detail: $"Mark-to-Market: {eq.MarkToMarket.MaxDrawdown:0.00}{(eq.ClosedTrades.MaxDrawdownOpen ? " · bis Datenende nicht aufgeholt" : "")}"));
        else figs.Add(new OverviewFigure("dd", "Beobachteter max. Drawdown", null, "currency", "Basislauf", "Noch nicht berechnet."));

        var prim = r.Oos?.Variants.FirstOrDefault(v => v.Key == r.Oos.PrimaryVariant);
        if (prim is not null)
            figs.Add(new OverviewFigure("oos", "Ergebnis auf Walk-forward-Testdaten", (double)prim.OosNet, "currency", $"Walk-forward-Testdaten (OOS) — {prim.Label}",
                Detail: $"{prim.OosTrades} Trades in {prim.OosSessions} Testsessions · Basisstrategie dieselben Tage: {prim.BaseNetSameSessions:0.00}"));
        else
            figs.Add(new OverviewFigure("oos", "Ergebnis auf Walk-forward-Testdaten", null, "currency", "Walk-forward-Testdaten (OOS)",
                r.Oos is null ? "Noch nicht berechnet." : string.Join(" · ", r.Oos.Variants.Select(v => $"{v.Label}: {v.Reason}"))));

        var sim = r.Simulation?.Methods.FirstOrDefault(m => m.Key == "bootstrap" && m.UnavailableReason is null);
        figs.Add(sim?.MaxDrawdown is { } d
            ? new OverviewFigure("simdd", "Max. Drawdown, 95. Perzentil", d.P95, "currency", $"Simulation — Bootstrap ({r.Simulation!.Basis})",
                Detail: $"Median {d.Median:0.00} · {sim.Iterations} Pfade, Seed {sim.Seed} · keine Verlustobergrenze")
            : new OverviewFigure("simdd", "Max. Drawdown, 95. Perzentil", null, "currency", "Simulation",
                r.Simulation is null ? "Noch nicht berechnet." : r.Simulation.Methods.FirstOrDefault()?.UnavailableReason ?? "Nicht verfügbar."));

        figs.Add(new OverviewFigure("trades", "Trades", r.Result?.Stats.Trades, "count", r.Result?.Basis ?? "Basislauf",
            r.Result is null ? "Noch nicht berechnet." : null));
        figs.Add(prim is not null
            ? new OverviewFigure("sessions", "Walk-forward-Testsessions", prim.OosSessions, "count", $"Walk-forward-Testdaten (OOS) — {prim.Label}",
                Detail: $"{prim.Folds.Count} Testfenster, je {prim.TestSessions} Sessions")
            : new OverviewFigure("sessions", "Walk-forward-Testsessions", null, "count", "Walk-forward-Testdaten (OOS)",
                r.Oos?.Variants.FirstOrDefault(v => v.Status == "InsufficientData")?.Reason ?? "Kein OOS-Ergebnis."));
        _ = fc;
        return new ReportOverview { Figures = figs };
    }
}
