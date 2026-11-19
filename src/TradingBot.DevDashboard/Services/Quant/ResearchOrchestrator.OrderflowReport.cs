using System.Globalization;
using TradingBot.Backtesting;
using TradingBot.Backtesting.Ohlc;
using TradingBot.DevDashboard.Services.Quant.Report;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Quant.Metrics;
using TradingBot.Quant.Report;
using TradingBot.Quant.Series;
using TradingBot.Research.Orderflow;

namespace TradingBot.DevDashboard.Services.Quant;

/// <summary>
/// Ein-Knopf-Bericht für Tick-Orderflow-Strategien (z. B. DeltaFlow). KEINE zweite Engine: Basislauf, Abschneidetest,
/// Sensitivität und Referenzlauf rufen ausschließlich den bestehenden <see cref="OrderflowMultiStrategyRunner"/> mit dem
/// gegateten Kandidaten und denselben Profilen wie die Orderflow-Konsole auf. Die Trade-basierten Abschnitte
/// (Ergebnis, Drawdown, Simulation, Kosten, Stabilität, Konzentration) verwenden dieselben Rechenbausteine wie der
/// OHLC-Bericht. MAE/MFE werden aus den Trade-Ticks GEMESSEN. Was im Tick-Pfad (noch) nicht angebunden ist, steht im
/// Bericht ausdrücklich als „nicht anwendbar“ bzw. „zu wenig Daten“ mit Begründung.
/// </summary>
public sealed partial class ResearchOrchestrator
{
    private async Task<ResearchRunRecord> RunOrderflowReportAsync(ResearchRunRecord record, IProgress<double>? progress, CancellationToken ct)
    {
        var cfg = record.Config;
        var opt = cfg.Report ?? new FullReportOptions();
        var bt = _quant.Backtest;
        var report = new BacktestReport();

        async Task SetStep(string key, ResearchStepStatus status, string? reason = null, string? basis = null, bool start = false, bool done = false)
        {
            var steps = record.Steps.Select(s => s.Key == key
                ? s with
                {
                    Status = status, Reason = reason ?? s.Reason, DataBasis = basis ?? s.DataBasis,
                    StartedUtc = start ? DateTimeOffset.UtcNow : s.StartedUtc, CompletedUtc = done ? DateTimeOffset.UtcNow : s.CompletedUtc
                } : s).ToList();
            record = record with { Steps = steps, Report = report };
            record = await _store.UpdateAsync(record, ct);
        }
        async Task Section(string key, double p0, double p1, Func<Task<(ResearchStepStatus, string?, string?)>> body)
        {
            await SetStep(key, ResearchStepStatus.Running, start: true);
            progress?.Report(p0);
            (ResearchStepStatus S, string? R, string? B) r;
            try { r = await body(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { r = (ResearchStepStatus.Failed, "Technisch fehlgeschlagen: " + ex.Message, null); }
            await SetStep(key, r.S, r.R, r.B, done: true);
            progress?.Report(p1);
        }
        static Task<(ResearchStepStatus, string?, string?)> Done(ResearchStepStatus s, string? r, string? b) => Task.FromResult((s, r, b));

        try
        {
            int tpb = OrderflowTickBacktest.TicksPerBarOf(cfg.Run.Params);
            var lt = bt.Ticks.Load(cfg.Run);
            var checks = OrderflowTickBacktest.Check(lt, cfg.Run.DataSourceId, tpb, null);
            if (checks.Any(c => !c.Met))
            {
                string why = string.Join(" · ", checks.Where(c => !c.Met).Select(c => $"{c.Requirement}: {c.Detail}"));
                foreach (var s in FullStepDefs) await SetStep(s.Key, ResearchStepStatus.InsufficientData, why, done: true);
                return await Finish(record, ResearchStepStatus.Failed, "Datenanforderungen nicht erfüllt: " + why, ct);
            }

            // Entwicklungs-/Holdout-Split TAG-GENAU auf den Ticks: die letzten floor(Handelstage·Anteil) CME-Handelstage sind der
            // reservierte Holdout — dieselbe Regel wie der session-ausgerichtete Walk-forward-Planer. Holdout-Ticks werden ab
            // hier nicht mehr angefasst (kein Lauf, keine Kennzahl).
            var full = await bt.LoadContextAsync(cfg.Run, ct);
            var ranges = OrderflowTickBacktest.SessionRanges(lt.Ticks);
            int hSessions = OrderflowTickBacktest.HoldoutSessions(ranges.Count, cfg.HoldoutFraction);
            int devCount = ranges.Count - hSessions;
            var devTicks = lt.Ticks.Take(devCount > 0 ? ranges[devCount - 1].End : 0).ToList();
            var holdoutIds = ranges.Skip(devCount).Select(r => r.Key).ToList();
            DateTimeOffset? holdoutStart = hSessions > 0 ? lt.Ticks[ranges[devCount].Start].Timestamp : null;
            int BarsOf(int ticks) => ticks / tpb;
            var split = new HoldoutSplit(BarsOf(lt.Ticks.Count), hSessions > 0 ? BarsOf(lt.Ticks.Count - devTicks.Count) : 0, BarsOf(devTicks.Count),
                lt.Ticks.Count > 0 ? lt.Ticks[0].Timestamp : null, lt.Ticks.Count > 0 ? lt.Ticks[^1].Timestamp : null,
                holdoutStart, holdoutStart, hSessions > 0 ? lt.Ticks[^1].Timestamp : null, holdoutStart?.AddTicks(-1));
            var devRun = holdoutStart is { } hs0 ? cfg.Run with { ToUtc = hs0.ToString("o") } : cfg.Run;
            record = record with
            {
                DataFrom = split.DataFrom, DataTo = split.DataTo, DevelopmentToUtc = split.DevelopmentToUtc,
                HoldoutFrom = split.HoldoutFrom, HoldoutTo = split.HoldoutTo, TotalBars = split.TotalBars,
                DevelopmentBars = split.UsableBars, HoldoutBars = split.HoldoutBars, DevRun = devRun
            };
            record = await _store.UpdateAsync(record, ct);
            string devBasis = split.HoldoutBars > 0 ? "Basislauf (Entwicklungsbereich der Trade-Ticks, ohne reservierten Holdout)" : "Basislauf (alle Trade-Ticks, kein Holdout reserviert)";

            // --- Basislauf: der bestehende Orderflow-Pfad, unverändert ---
            var instrument = full.Instrument; var fee = full.Fee;
            var baseReq = bt.Ticks.BuildRequest(cfg.Run, devTicks, instrument, fee, lt.Source, lt.Sha256);
            var baseRun = await OrderflowTickBacktest.RunAsync(baseReq, ct);
            var (devCandles, devBars) = OrderflowTickBacktest.Bars(devTicks, tpb, instrument.Symbol);
            var synth = Synthesize(baseRun, devCandles, baseReq, lt.Source);
            var dev = new BacktestApiService.RunContext(devCandles, instrument, fee, false, false,
                $"{tpb}-Tick-Orderflow-Bars aus {lt.Source}", 0, Array.Empty<OhlcImportIssue>(),
                full.InstrumentIsExample, full.FeeIsExample);
            var baseTrades = ToReportTrades(synth, dev);
            var devSessions = ranges.Take(devCount).Select(r => r.Key).ToList();
            var fc = new FullCtx { Cfg = cfg, Opt = opt, DevRun = devRun, Dev = dev, Base = synth, BaseTrades = baseTrades, DevSessions = devSessions };
            var ws = baseRun.Workspace.Strategies.Single();
            var diag = baseRun.RunResults.Single().Diagnostics;
            int holdoutSessions = hSessions;

            ReportProvenance Prov() => BuildProvenance(record, fc, split, holdoutSessions) with
            {
                DataSha256 = lt.Sha256,
                DataSource = lt.Source,
                DataKind = "Trade-Ticks mit gemessenem Aggressor (Käufer/Verkäufer) je Trade",
                EngineKind = OrderflowTickBacktest.EngineKind,
                StrategyVersion = string.IsNullOrEmpty(opt.StrategyVersion) ? "DeltaFlowStrategy (SinglePositionGate) @ " + _quant.CodeVersion() : opt.StrategyVersion,
                EngineVersion = "BacktestEngine (tick-getrieben) über OrderflowMultiStrategyRunner @ " + _quant.CodeVersion(),
                SignalData = $"{tpb}-Tick-Orderflow-Bars aus {lt.Source}: Delta = Käufer- minus Verkäufer-Aggressorvolumen je Bar (gemessen, nicht geschätzt); Signal auf der geschlossenen Bar.",
                ExecutionData = "Bestehende tick-getriebene BacktestEngine: Market-Order füllt am nächsten Trade-Tick ± Slippage; " +
                                "SinglePositionGate max. 1 Kontrakt; Gebühren aus dem FeeProfile; Risiko config/risk/research-sim.example.json. " +
                                "Keine Quotes/kein Orderbuch: Fill-Preis = Trade-Preis ± Slippage.",
                FillModel = new[] { baseRun.Workspace.Provenance.ExecutionModel }
            };
            report = report with { Provenance = Prov() };
            await SetStep("data", ResearchStepStatus.Pending);

            // --- 01 Daten (Tick-spezifisch) ---
            await Section("data", 0.02, 0.10, async () =>
            {
                report = report with { Data = await BuildTickDataSectionAsync(fc, lt, devTicks, tpb, baseRun, baseReq, ct) };
                bool fail = report.Data!.TemporalChecks.Any(c => c.Passed == false);
                return (ResearchStepStatus.Completed, fail ? "Mindestens eine Zeitprüfung ist NICHT bestanden." : null, devBasis);
            });

            // --- 02 Ergebnis ---
            await Section("result", 0.10, 0.14, () =>
            {
                var stats = TradeStatistics.Compute(baseTrades, synth.Data.EvaluatedBars);
                var openQty = diag?.FinalPositionQuantity ?? 0;
                stats = stats with { UnrealizedAtEnd = openQty > 0 ? diag!.FinalPositionUnrealizedGross : 0m, OpenAtEnd = openQty > 0 ? 1 : 0 };
                var notes = new List<string>
                {
                    "Brutto = Kursergebnis aus den Engine-Fills (Slippage bereits im Fill-Preis); Netto = Brutto − Gebühren (je Seite und Kontrakt genau einmal).",
                    "Signale entstehen auf geschlossenen N-Tick-Bars; die Market-Order füllt am ersten Tick der Folgebar (kein Lookahead)."
                };
                if (openQty > 0)
                    notes.Add($"Am Datenende offene Position ({diag!.FinalPositionSide} {openQty} @ {diag.FinalPositionAvgPrice}) — die Engine stellt NICHT zwangsweise glatt; " +
                              $"unrealisiert (brutto, letzter Tick) {diag.FinalPositionUnrealizedGross:0.00}, getrennt ausgewiesen und nicht im Netto.");
                report = report with
                {
                    Result = new ReportResultSection
                    {
                        Basis = devBasis, Stats = stats, Costs = report.Provenance.CostModel, EvaluatedBars = synth.Data.EvaluatedBars,
                        SignalsGenerated = diag?.SignalsGenerated ?? 0, OrdersSubmitted = diag?.OrdersSubmitted,
                        OpenPositionAtEnd = openQty > 0 ? $"{diag!.FinalPositionSide} {openQty} @ {diag.FinalPositionAvgPrice}" : null,
                        Notes = notes
                    }
                };
                return Done(stats.Trades == 0 ? ResearchStepStatus.InsufficientData : ResearchStepStatus.Completed,
                    stats.Trades == 0 ? "Keine abgeschlossenen Trades im Basislauf." : null, devBasis);
            });

            // --- 03 Equity/Drawdown ---
            await Section("equity", 0.14, 0.16, () =>
            {
                var start = devCandles[0].OpenTime; double cap = (double)cfg.Run.InitialBalance;
                var closed = DrawdownAnalysis.Analyze(DrawdownAnalysis.ClosedTradeCurve(baseTrades, cap, start), "Abgeschlossene Trades");
                var mtmCurve = new List<EquitySample> { new(start, cap) };
                mtmCurve.AddRange(synth.Equity.Select(p => new EquitySample(p.Time, (double)(p.Equity + p.OpenPnL))));
                var mtm = DrawdownAnalysis.Analyze(mtmCurve, "Mark-to-Market je Bar-Schluss");
                report = report with
                {
                    Equity = new ReportEquitySection
                    {
                        Basis = devBasis, ClosedTrades = closed, MarkToMarket = mtm, LosingStreak = DrawdownAnalysis.LosingStreak(baseTrades),
                        Notes = new[]
                        {
                            "Durchgehende Kapitalkurve eines einzigen Tick-Laufs mit fester Kontraktzahl (max. 1).",
                            "Mark-to-Market je Schluss einer N-Tick-Bar (Bars haben keine feste Dauer).",
                            "Eine am Datenende offene, nie geschlossene Position ist im Mark-to-Market nur ab ihrem Einstieg enthalten, sofern dieser Einstieg ein abgeschlossener Trade war; sonst separat in 02 ausgewiesen."
                        }
                    }
                };
                return Done(ResearchStepStatus.Completed, null, devBasis);
            });

            // --- 04 Risiko-Kennzahlen aus der vorhandenen Kennzahlen-Bibliothek ---
            await Section("risk", 0.16, 0.18, () =>
            {
                var spec = new QuantContractSpec(instrument.TickSize, instrument.PointValue, instrument.Currency);
                var freq = QuantApiService.ParseFrequency(cfg.Options.Frequency);
                var curve = ReturnSeriesBuilder.BuildEquityCurve(synth, spec, freq);
                var built = ReturnSeriesBuilder.ToReturnSeries(curve, EquityBasis.Total);
                var m = PerformanceMetricsCalculator.Compute(built.Series, QuantApiService.MetricOptions(cfg.Options));
                var keys = new[] { "sharpe", "sortino", "calmar", "cagr", "volatility_annual", "max_drawdown", "expected_shortfall" };
                var metrics = GateShortHistory(m.Metrics.Where(x => keys.Contains(x.Key)).OrderBy(x => Array.IndexOf(keys, x.Key)).Select(QuantMetricDto.From).ToList(),
                    cfg.Options.MinimumPeriods);
                report = report with
                {
                    Risk = new ReportRiskSection
                    {
                        Basis = devBasis, Frequency = freq.ToString(), PeriodsPerYear = m.PeriodsPerYear, AnnualizationNote = m.AnnualizationNote,
                        CapitalBase = $"Periodenrendite = Veränderung des Gesamtkapitals gegenüber dem Kapital am Ende der Vorperiode; Startkapital {cfg.Run.InitialBalance:0}, feste Kontraktzahl.",
                        Metrics = metrics, Notes = built.Notes.Concat(m.Notes).Take(4).ToList()
                    }
                };
                bool none = metrics.Where(x => x.Key != "max_drawdown").All(x => x.Value is null);
                return Done(none ? ResearchStepStatus.InsufficientData : ResearchStepStatus.Completed,
                    none ? "Nicht auswertbar: " + (metrics.FirstOrDefault()?.UnavailableReason ?? "zu kurze Historie.") : null, devBasis + ", Tagesrenditen");
            });

            // --- 05 Sensitivität über den Tick-Pfad ---
            await Section("sensitivity", 0.18, 0.40, async () =>
            {
                var sec = await BuildTickSensitivityAsync(fc, devTicks, instrument, fee, lt, ct);
                report = report with { Sensitivity = sec };
                return (sec.Applicable ? ResearchStepStatus.Completed : ResearchStepStatus.NotApplicable, sec.Reason, devBasis);
            });

            // --- 06 Walk-forward über den Tick-Pfad: vorhandener Planer + WalkForwardRunner, jeder Kandidat durch den
            //        bestehenden OrderflowMultiStrategyRunner. Holdout-Ticks werden nie übergeben. ---
            await Section("oos", 0.40, 0.55, async () =>
            {
                var sec = await BuildTickOosAsync(fc, lt, ranges, devCount, holdoutIds, instrument, fee, ct);
                report = report with { Oos = sec };
                var completed = sec.Variants.Where(v => v.Status == "Completed").ToList();
                if (completed.Count == 0)
                    return (sec.Variants.All(v => v.Status == "InsufficientData") ? ResearchStepStatus.InsufficientData : ResearchStepStatus.Failed,
                        string.Join(" · ", sec.Variants.Select(v => $"{v.Label}: {v.Reason}")), null);
                string? partial = sec.Variants.Any(v => v.Status != "Completed")
                    ? string.Join(" · ", sec.Variants.Where(v => v.Status != "Completed").Select(v => $"{v.Label}: {v.Reason}")) : null;
                return (ResearchStepStatus.Completed, partial, "Walk-forward-Testdaten: getrennt simulierte Testfenster (Auswahl nur im Training)");
            });

            // --- 07 Simulation (Trade-basiert, gleiche Bausteine) ---
            await Section("simulation", 0.45, 0.60, () =>
            {
                var sec = BuildSimulation(fc, ct);
                report = report with { Simulation = sec with { DelayedFills = null, Notes = sec.Notes.Where(n => !n.StartsWith("Verzögerte Fills", StringComparison.Ordinal))
                    .Append("Verzögerte Fills: im Tick-Pfad nicht angebunden.").ToList() } };
                bool any = sec.Methods.Any(m => m.UnavailableReason is null);
                return Done(any ? ResearchStepStatus.Completed : ResearchStepStatus.InsufficientData,
                    any ? null : "Nicht auswertbar: " + sec.Methods.FirstOrDefault()?.UnavailableReason, "Simulation: " + sec.Basis);
            });

            // --- 08 Kosten ---
            await Section("costs", 0.60, 0.63, () =>
            {
                report = report with
                {
                    Costs = BuildCosts(fc) with
                    {
                        DataLimits = new[]
                        {
                            "Trade-Ticks ohne Quotes: Spread, Queue-Position und verfügbare Größe sind NICHT beobachtbar — Slippage-Szenarien sind hypothetisch.",
                            "Engine-Neulauf mit skalierten Kosten/verzögerter Ausführung ist im Tick-Pfad nicht angebunden; die Szenarien rechnen auf den Trades."
                        }
                    }
                };
                return Done(baseTrades.Count == 0 ? ResearchStepStatus.InsufficientData : ResearchStepStatus.Completed, baseTrades.Count == 0 ? "Keine Trades." : null, devBasis);
            });

            await Section("stability", 0.63, 0.67, () =>
            {
                report = report with { Stability = BuildStability(fc) };
                return Done(baseTrades.Count == 0 ? ResearchStepStatus.InsufficientData : ResearchStepStatus.Completed, baseTrades.Count == 0 ? "Keine Trades." : null, devBasis);
            });

            await Section("concentration", 0.67, 0.70, () =>
            {
                var res = ConcentrationAnalysis.Compute(baseTrades);
                report = report with { Concentration = new ReportConcentrationSection { Basis = devBasis, Result = res } };
                return Done(baseTrades.Count == 0 ? ResearchStepStatus.InsufficientData : ResearchStepStatus.Completed,
                    res.LeaveOneOutReason is null ? null : "Weglassen je Handelstag: " + res.LeaveOneOutReason, devBasis);
            });

            // --- 11 Kontrollen über denselben Tick-Pfad ---
            await Section("controls", 0.70, 0.90, async () =>
            {
                double cap = (double)cfg.Run.InitialBalance; var start = devCandles[0].OpenTime;
                double Dd(IReadOnlyList<ReportTrade> t) => DrawdownAnalysis.Analyze(DrawdownAnalysis.ClosedTradeCurve(t, cap, start), "").MaxDrawdown;
                var rows = new List<ControlRow>
                {
                    new("none", "Kein Trade", 0, 0, 0, "Basislauf-Ticks", "Nullreferenz."),
                    new("base", "DeltaFlow mit Basisparametern (nicht optimiert)", synth.Statistics.NetProfit, baseTrades.Count, Dd(baseTrades), "Basislauf", null)
                };
                var refRun = await OrderflowTickBacktest.RunAsync(bt.Ticks.BuildRequest(cfg.Run, devTicks, instrument, fee, lt.Source, lt.Sha256, strategy: "momentum"), ct);
                var refSynth = Synthesize(refRun, devCandles, baseReq, lt.Source);
                rows.Add(new ControlRow("reference", "Referenz: Momentum(Price) (feste Regeln, gleicher Tick-Pfad)", refSynth.Statistics.NetProfit, refSynth.Trades.Count,
                    Dd(ToReportTrades(refSynth, dev)), "Basislauf-Ticks", "Gleiche Bars, Kosten, Gate und Engine — nur die Signalregel unterscheidet sich (Preis statt Delta)."));
                decimal slip = synth.EffectiveSlippageTicks * instrument.TickSize;
                var random = RandomEntryControl.Run(devCandles, 0, devCandles.Count - 1, baseTrades, slip, synth.FeePerSide, instrument.PointValue,
                    cfg.Run.ApplyFees, opt.RandomControlIterations, cfg.Seed + 2, ct);
                report = report with
                {
                    Controls = new ReportControlSection
                    {
                        Basis = "Basislauf-Ticks", Rows = rows, Random = random,
                        Trials = new TrialHistory(fc.SensitivityVariants + cfg.Candidates.Count, fc.WfSelectionAttempts, 0, new[]
                        {
                            $"Sensitivität: {fc.SensitivityVariants} Varianten über den Tick-Pfad.",
                            $"Walk-forward-Raster: {cfg.Candidates.Count} Delta-Schwellen in {fc.WfVariantsRun} Varianten; {fc.WfSelectionAttempts} Auswahlentscheidungen.",
                            "Versuche des Tick-Pfads werden (noch) nicht im Versuchsregister erfasst."
                        }),
                        Notes = new[]
                        {
                            "PBO/PSR/DSR im Tick-Pfad NICHT angebunden (separate Folgeaufgabe) — keine Overfitting-Wahrscheinlichkeit ausgewiesen.",
                            "Engine-Neuläufe mit zusätzlicher Slippage/Latenz im Tick-Pfad NICHT angebunden (separate Folgeaufgabe).",
                            "Zufallskontrolle auf den N-Tick-Bars: Einstieg am Open = erster Tick der gezogenen Bar."
                        }
                    }
                };
                return (ResearchStepStatus.Completed, null, devBasis);
            });

            // --- 12 Einzeltrades: Journal + MAE/MFE GEMESSEN aus Ticks + verknüpfte Signale ---
            await Section("trades", 0.90, 0.96, () =>
            {
                var sec = BuildTrades(fc);
                decimal pv = instrument.PointValue;
                var rows = sec.Trades.Select((r, i) =>
                {
                    var w = ws.Trades[i]; var bt0 = baseRun.RunResults.Single().Trades[i];
                    double sign = bt0.Side == PositionSide.Long ? 1 : -1, worst = 0, best = 0; int n = 0;
                    foreach (var t in devTicks)
                    {
                        if (t.Timestamp < bt0.EntryTime) continue;
                        if (t.Timestamp > bt0.ExitTime) break;
                        n++; double d = (double)(t.Price - bt0.EntryPrice) * sign;
                        worst = Math.Min(worst, d); best = Math.Max(best, d);
                    }
                    double dx = (double)(bt0.ExitPrice - bt0.EntryPrice) * sign; worst = Math.Min(worst, dx); best = Math.Max(best, dx);
                    double q = (double)pv * bt0.Quantity;
                    return r with
                    {
                        Mae = -worst * q, Mfe = best * q, ExcursionLowerBound = false, ExcursionMeasured = true,
                        ExcursionBasis = $"gemessen aus {n} Trade-Ticks zwischen Einstiegs- und Ausstiegs-Fill (+ Fill-Preise)",
                        ExitReason = "Signal (Gegen-/Flat-Signal)",
                        EntrySignalUtc = w.EntrySignal?.TimeUtc, EntrySignalDirection = w.EntrySignal?.Direction,
                        ExitSignalUtc = w.ExitSignal?.TimeUtc, ExitSignalDirection = w.ExitSignal?.Direction,
                        EntryTimeUtc = w.EntryTimeUtc, ExitTimeUtc = w.ExitTimeUtc
                    };
                }).ToList();
                report = report with
                {
                    Trades = sec with
                    {
                        Trades = rows,
                        ExcursionDefinition = "GEMESSEN aus allen Trade-Ticks zwischen Einstiegs- und Ausstiegs-Fill (inkl. Fill-Preise) — keine Kerzen-Schätzung.",
                        TickReplayNote = $"Bar-Replay zeigt die {tpb}-Tick-Bars; Signal/Fill-Zeitpunkte je Trade stammen aus dem Signal-Mitschnitt des Orderflow-Laufs."
                    }
                };
                return Done(baseTrades.Count == 0 ? ResearchStepStatus.InsufficientData : ResearchStepStatus.Completed, baseTrades.Count == 0 ? "Keine Trades." : null, devBasis);
            });

            await Section("holdout", 0.96, 0.98, () => Done(ResearchStepStatus.NotApplicable,
                hSessions > 0 ? $"Reserviert und unbenutzt: {hSessions} Handelstag(e) ({string.Join(", ", holdoutIds)}). Keine Holdout-Performance berechnet; " +
                                "die finale Schlussprüfung ist im Tick-Pfad nicht angebunden."
                              : "Kein Holdout reserviert (zu wenige Handelstage).", null));

            report = report with { Provenance = Prov() };
            report = report with { Overview = BuildOverviewStatic(report, fc) };
            record = record with { Report = report };
            record = await _store.UpdateAsync(record, ct);
            progress?.Report(1.0);
            return await Finish(record, ResearchStepStatus.Completed, null, ct);
        }
        catch (OperationCanceledException)
        {
            var steps = record.Steps.Select(s => s.Status is ResearchStepStatus.Running or ResearchStepStatus.Pending
                ? s with { Status = ResearchStepStatus.Cancelled, Reason = "Abgebrochen — fertige Abschnitte bleiben erhalten." } : s).ToList();
            record = record with { Steps = steps, Report = report };
            await Finish(record, ResearchStepStatus.Cancelled, "Der Lauf wurde abgebrochen. Fertige Teilergebnisse bleiben sichtbar.", ct: CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            var steps = record.Steps.Select(s => s.Status is ResearchStepStatus.Running or ResearchStepStatus.Pending
                ? s with { Status = ResearchStepStatus.Failed, Reason = ex.Message } : s).ToList();
            record = record with { Steps = steps, Report = report };
            return await Finish(record, ResearchStepStatus.Failed, ex.Message, ct: CancellationToken.None);
        }
    }

    /// <summary>
    /// Formt das Ergebnis des bestehenden Tick-Laufs in die Ergebnisstruktur der Berichtsbausteine um — OHNE etwas neu zu
    /// rechnen: Trades/Fills/Gebühren/Netto 1:1 aus der Engine, Bar-Indizes aus dem Orderflow-Artefakt, Mark-to-Market je
    /// Bar-Schluss aus den Engine-Trades.
    /// </summary>
    internal static OhlcBacktestResult Synthesize(OrderflowRunResult run, IReadOnlyList<Candle> bars, OrderflowRunRequest req, string source)
    {
        var rr = run.RunResults.Single();
        var ws = run.Workspace.Strategies.Single();
        var trades = rr.Trades.Select((t, i) => new OhlcBacktestTrade
        {
            Symbol = t.Symbol, Side = t.Side, Quantity = t.Quantity, EntryTime = t.EntryTime, ExitTime = t.ExitTime,
            EntryPrice = t.EntryPrice, ExitPrice = t.ExitPrice, EntryBarIndex = ws.Trades[i].EntryBarIndex, ExitBarIndex = ws.Trades[i].ExitBarIndex,
            GrossPnL = t.GrossPnL, Fees = t.Fees, NetPnL = t.NetPnL, ExitReason = OhlcExitReason.OppositeSignal,
            Note = "Exit durch Strategiesignal (Gegen-/Flat-Signal) im Tick-Pfad"
        }).ToList();
        decimal pv = req.Instrument.PointValue, start = req.Account.StartingBalance;
        var equity = new List<OhlcEquityPoint>(bars.Count);
        for (int i = 0; i < bars.Count; i++)
        {
            var b = bars[i];
            decimal realized = trades.Where(t => t.ExitTime <= b.CloseTime).Sum(t => t.NetPnL);
            var open = trades.FirstOrDefault(t => t.EntryTime <= b.CloseTime && t.ExitTime > b.CloseTime);
            decimal openPnl = open is null ? 0m : (b.Close - open.EntryPrice) * (open.Side == PositionSide.Long ? 1 : -1) * pv * open.Quantity;
            equity.Add(new OhlcEquityPoint { BarIndex = i, Time = b.CloseTime, RealizedNetPnL = realized, Equity = start + realized, OpenPnL = openPnl });
        }
        decimal feeSide = req.Fee.CommissionPerSide + req.Fee.ExchangeFeePerSide + req.Fee.ClearingFeePerSide + req.Fee.RoutingFeePerSide
                          + req.Fee.NfaFeePerSide + req.Fee.OtherFeePerSide;
        decimal slipTicks = req.SlippageTicksOverride ?? req.Fee.EstimatedSlippageTicks;
        return new OhlcBacktestResult
        {
            Status = BacktestRunStatus.Completed,
            Statistics = rr.Statistics,
            Trades = trades,
            Equity = equity,
            SignalsGenerated = rr.Diagnostics?.SignalsGenerated ?? 0,
            Data = new OhlcDataInfo
            {
                Source = source, Symbol = req.Symbol, TimeframeMinutes = 0, From = bars.Count > 0 ? bars[0].OpenTime : null,
                To = bars.Count > 0 ? bars[^1].CloseTime : null, BarCount = bars.Count, EvaluatedBars = bars.Count
            },
            StrategyName = rr.StrategyName,
            EffectiveSlippageTicks = slipTicks,
            FeePerSide = feeSide,
            InitialBalance = start,
            FinalEquity = start + rr.Trades.Sum(t => t.NetPnL)
        };
    }

    private async Task<ReportDataSection> BuildTickDataSectionAsync(FullCtx fc, LoadedTicks lt, List<MarketTick> devTicks, int tpb,
        OrderflowRunResult baseRun, OrderflowRunRequest baseReq, CancellationToken ct)
    {
        var candles = fc.Dev.Candles;
        var sessions = candles.Select((c, i) => (c, i)).GroupBy(x => TradeDateCalendar.Key(x.c.OpenTime))
            .Select(g => new SessionInfo(g.Key, g.Count(), g.First().c.OpenTime.ToUnixTimeMilliseconds(), g.Last().c.CloseTime.ToUnixTimeMilliseconds(), false)).ToList();
        int sameTs = 0, backwards = 0;
        for (int i = 1; i < devTicks.Count; i++)
        {
            if (devTicks[i].Timestamp == devTicks[i - 1].Timestamp) sameTs++;
            else if (devTicks[i].Timestamp < devTicks[i - 1].Timestamp) backwards++;
        }
        var prices = devTicks.Select(t => t.Price).Distinct().OrderBy(p => p).ToList();
        decimal step = 0m;
        for (int i = 1; i < prices.Count; i++) { var d = prices[i] - prices[i - 1]; if (d > 0 && (step == 0 || d < step)) step = d; }

        var checks = new List<CheckResult>();
        // Abschneidetest über den bestehenden Pfad: erste Hälfte der Ticks (an Bar-Grenze); Trades, die davor abgeschlossen sind, müssen identisch sein.
        int halfBars = (devTicks.Count / tpb) / 2;
        if (halfBars >= 2)
        {
            var prefix = devTicks.Take(halfBars * tpb).ToList();
            var cut = await OrderflowTickBacktest.RunAsync(baseReq with { Ticks = prefix }, ct);
            var bound = prefix[^1].Timestamp;
            var a = baseRun.RunResults.Single().Trades.Where(t => t.ExitTime < bound).ToList();
            var b = cut.RunResults.Single().Trades.Where(t => t.ExitTime < bound).ToList();
            bool same = a.Count == b.Count && a.Zip(b).All(p => p.First.EntryTime == p.Second.EntryTime && p.First.ExitTime == p.Second.ExitTime
                && p.First.EntryPrice == p.Second.EntryPrice && p.First.ExitPrice == p.Second.ExitPrice && p.First.NetPnL == p.Second.NetPnL);
            checks.Add(new CheckResult("Keine Zukunftsdaten in Signalen (Abschneidetest)", same,
                same ? $"Lauf auf den ersten {prefix.Count} von {devTicks.Count} Ticks: alle {a.Count} davor abgeschlossenen Trades identisch."
                     : $"ABWEICHUNG: {a.Count} vs. {b.Count} Trades vor {bound:yyyy-MM-dd HH:mm:ss} UTC."));
        }
        else checks.Add(new CheckResult("Keine Zukunftsdaten in Signalen (Abschneidetest)", null, $"Zu wenige Bars für den Abschneidetest (mind. 4 volle {tpb}-Tick-Bars)."));

        var wsTrades = baseRun.Workspace.Strategies.Single().Trades;
        int linked = wsTrades.Count(t => t.EntrySignal is not null);
        int bad = wsTrades.Count(t => t.EntrySignal is not null && t.EntryTickIndex is int et && et <= t.EntrySignal.TickIndex);
        checks.Add(new CheckResult("Order nicht rückwirkend gefüllt", wsTrades.Count == 0 ? null : bad == 0 && linked == wsTrades.Count,
            wsTrades.Count == 0 ? "Keine Trades zu prüfen."
            : $"{linked}/{wsTrades.Count} Einstiege mit Signal verknüpft; Fill-Tick liegt bei {wsTrades.Count - bad} von {wsTrades.Count} NACH dem letzten Tick der Signal-Bar."));
        checks.Add(new CheckResult("Delta gemessen (nicht aus OHLC geschätzt)", lt.Set.Capabilities.SupportsDeltaCvd,
            $"{devTicks.Count} Trade-Ticks, alle mit Aggressor; Delta je Bar = Käufer- minus Verkäufer-Aggressorvolumen."));
        checks.Add(new CheckResult("Zeitstempel nicht rückläufig", backwards == 0,
            $"{backwards} rückläufige Zeitstempel; {sameTs} Ticks mit gleichem Zeitstempel wie der Vorgänger bleiben in Dateireihenfolge erhalten (keine Deduplizierung)."));

        return new ReportDataSection
        {
            DataKind = "Trade-Ticks mit gemessenem Aggressor (Käufer/Verkäufer) je Trade",
            DataKindNotes = new[]
            {
                $"Quelle: {lt.Source} — {lt.Set.Quality.RowsAccepted}/{lt.Set.Quality.RowsRead} Zeilen übernommen, SHA-256 {lt.Sha256[..Math.Min(16, lt.Sha256.Length)]}…",
                $"Signal-Bars: volle {tpb}-Tick-Bars ({devTicks.Count % tpb} Rest-Ticks ohne eigene Bar). Bars haben keine feste Dauer.",
                $"Beobachtete Preisschrittweite {(step == 0 ? "n/a" : step.ToString(CultureInfo.InvariantCulture))}, Instrument-TickSize {fc.Dev.Instrument.TickSize}.",
                "Keine Bid/Ask-Quotes, kein Orderbuch: Fill-Preis = Trade-Preis ± modellierte Slippage."
            },
            SessionDefinition = "Handelstag = CME-Trade-Date (Beginn 17:00 Chicago-Zeit des Vortags).",
            Sessions = sessions,
            MedianBarsPerSession = sessions.Count == 0 ? 0 : TradingBot.Quant.Statistics.Stats.Median(sessions.Select(s => (double)s.Bars).ToList()),
            DstTransitions = candles.Count == 0 ? Array.Empty<string>() : TradeDateCalendar.DstTransitions(candles[0].OpenTime, candles[^1].CloseTime).Select(d => $"{d.Date:yyyy-MM-dd}: Sommerzeitwechsel New York").ToList(),
            TemporalChecks = checks,
            HistoryLimits = new[] { $"{sessions.Count} Handelstag(e) im Entwicklungsbereich — Robustheitsanalysen, die mehr Historie brauchen, melden „zu wenig Daten“." }
        };
    }

    private async Task<ReportSensitivitySection> BuildTickSensitivityAsync(FullCtx fc, List<MarketTick> devTicks, InstrumentProfile instrument, FeeProfile fee,
        LoadedTicks lt, CancellationToken ct)
    {
        var p = fc.Opt.SensitivityParameters.Where(x => x is "DeltaThreshold" or OrderflowTickBacktest.TicksPerBarKey).ToList();
        if (p.Count == 0)
            return new ReportSensitivitySection { Basis = "Basislauf", Applicable = false, MinTrades = fc.Opt.MinTrades, Reason = "Keine einstellbaren Parameter." };
        var sessions = fc.DevSessions;
        double Base(string name) => name == OrderflowTickBacktest.TicksPerBarKey ? OrderflowTickBacktest.TicksPerBarOf(fc.Cfg.Run.Params) : (double)OrderflowTickBacktest.ThresholdOf(fc.Cfg.Run.Params);
        Dictionary<string, string> With(Dictionary<string, string> d, string name, double v)
            => new(d) { [name] = name == OrderflowTickBacktest.TicksPerBarKey ? ((int)Math.Round(v)).ToString(CultureInfo.InvariantCulture) : v.ToString("0.######", CultureInfo.InvariantCulture) };
        var cache = new Dictionary<string, (bool Ok, string? Err, OrderflowRunResult? R)>();
        async Task<(bool Ok, string? Err, OrderflowRunResult? R)> Run(Dictionary<string, string> pars)
        {
            string key = string.Join(";", pars.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + "=" + k.Value));
            if (cache.TryGetValue(key, out var hit)) return hit;
            (bool, string?, OrderflowRunResult?) res;
            try { res = (true, null, await OrderflowTickBacktest.RunAsync(_quant.Backtest.Ticks.BuildRequest(fc.Cfg.Run, devTicks, instrument, fee, lt.Source, lt.Sha256, pars), ct)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { res = (false, ex.Message, null); }
            cache[key] = res;
            return res;
        }
        SensitivityPoint Point(double v, bool isBase, (bool Ok, string? Err, OrderflowRunResult? R) r)
        {
            if (!r.Ok || r.R is null) return new SensitivityPoint(v, isBase, false, r.Err, 0, 0, null, 0, 0, sessions.Count, Array.Empty<decimal>());
            var trades = r.R.RunResults.Single().Trades;
            var bySession = trades.GroupBy(t => TradeDateCalendar.Key(t.EntryTime)).ToDictionary(g => g.Key, g => g.Sum(t => t.NetPnL));
            var nets = sessions.Select(s => bySession.TryGetValue(s, out var x) ? x : 0m).ToList();
            decimal gw = trades.Where(t => t.NetPnL > 0).Sum(t => t.NetPnL), gl = -trades.Where(t => t.NetPnL < 0).Sum(t => t.NetPnL);
            double eq = (double)fc.Cfg.Run.InitialBalance, peak = eq, dd = 0;
            foreach (var t in trades.OrderBy(t => t.ExitTime)) { eq += (double)t.NetPnL; peak = Math.Max(peak, eq); dd = Math.Max(dd, peak - eq); }
            return new SensitivityPoint(v, isBase, true, null, trades.Sum(t => t.NetPnL), trades.Count, gl > 0 ? (double)(gw / gl) : null, dd,
                nets.Count(n => n > 0), sessions.Count, nets);
        }
        var parms = new List<SensitivityParam>();
        int runs = 0;
        foreach (var name in p)
        {
            double b = Base(name);
            var vals = SensitivityRules.Neighbors(b, name == OrderflowTickBacktest.TicksPerBarKey || b == Math.Round(b));
            var pts = new List<SensitivityPoint>();
            foreach (var v in vals) { pts.Add(Point(v, Math.Abs(v - b) < 1e-9, await Run(With(fc.Cfg.Run.Params, name, v)))); runs++; }
            parms.Add(new SensitivityParam(name, b, pts, SensitivityRules.Classify(pts.Select(x => (x.Value, x.Net, x.Trades, x.Valid)).ToList(), pts.FindIndex(x => x.IsBase), fc.Opt.MinTrades)));
        }
        var pairs = new List<SensitivityPair>();
        if (p.Count == 2)
        {
            var xs = parms[0].Points.Select(x => x.Value).ToList(); var ys = parms[1].Points.Select(x => x.Value).ToList();
            var cells = new List<SensitivityCell>();
            foreach (var x in xs)
                foreach (var y in ys)
                {
                    var r = await Run(With(With(fc.Cfg.Run.Params, p[0], x), p[1], y));
                    var trades = r.R?.RunResults.Single().Trades;
                    cells.Add(new SensitivityCell(x, y, r.Ok, trades?.Sum(t => t.NetPnL) ?? 0, trades?.Count ?? 0, r.Err));
                }
            pairs.Add(new SensitivityPair(p[0], p[1], xs, ys, cells, parms[0].BaseValue, parms[1].BaseValue));
        }
        fc.SensitivityVariants = cache.Count; fc.SensitivityRuns = cache.Count;
        return new ReportSensitivitySection
        {
            Basis = "Basislauf-Ticks — je Variante ein Lauf über den bestehenden Orderflow-Pfad",
            Applicable = true, MinTrades = fc.Opt.MinTrades, SessionKeys = sessions, Parameters = parms, Pairs = pairs, EngineRuns = cache.Count
        };
    }
}
