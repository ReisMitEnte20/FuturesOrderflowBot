using System.Globalization;
using TradingBot.DevDashboard.Services.Quant.Report;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Quant.Registry;
using TradingBot.Quant.Report;
using TradingBot.Quant.Series;
using TradingBot.Quant.Validation;
using TradingBot.Research.Orderflow;

namespace TradingBot.DevDashboard.Services.Quant;

/// <summary>
/// Walk-forward für Tick-Orderflow-Strategien (DeltaFlow). Keine zweite Simulation, keine Optimierung auf einer fertigen
/// Trade-Liste:
/// <list type="bullet">
/// <item>Fenster: der vorhandene <see cref="WalkForwardPlanner"/> im Session-Modus auf einer Achse mit EINEM Punkt je
/// CME-Handelstag der Ticks, mit DERSELBEN Holdout-Fraktion wie der Bericht (geprüft: Planer-Holdout = Bericht-Holdout).</item>
/// <item>Auswahl: der vorhandene <see cref="WalkForwardRunner"/> (nur Trainingswerte, Gleichstand = kleinste Kandidaten-Id).</item>
/// <item>Jede Bewertung = ein Lauf des bestehenden <see cref="OrderflowMultiStrategyRunner"/> (tick-getriebene Engine) auf
/// genau den Ticks der Fenster-Handelstage. Jedes Fenster startet flach mit eigenen Bars; nichts wird über eine
/// Fenstergrenze übertragen. Holdout-Ticks werden nie übergeben (harte Sperre).</item>
/// </list>
/// </summary>
public sealed partial class ResearchOrchestrator
{
    private async Task<ReportOosSection> BuildTickOosAsync(FullCtx fc, LoadedTicks lt, List<(string Key, int Start, int End)> ranges, int devCount,
        List<string> holdoutIds, InstrumentProfile instrument, FeeProfile fee, CancellationToken ct)
    {
        var cfg = fc.Cfg; var opt = fc.Opt;
        var ticks = lt.Ticks;
        int N = ranges.Count, S = devCount;
        var devIds = ranges.Take(S).Select(r => r.Key).ToList();
        var axis = ranges.Select(r => ticks[r.End - 1].Timestamp).ToList();          // ein Punkt je Handelstag (Tagesende)
        var starts = Enumerable.Range(0, N).ToList();                                  // jeder Punkt beginnt einen Handelstag
        var candidates = cfg.Candidates.Select((p, i) => new ParameterCandidate(QuantApiService.CandidateId(p, i), p)).ToList();
        var baseParams = new Dictionary<string, string>(cfg.Run.Params);
        decimal slipTicks = cfg.Run.SlippageTicks ?? fee.EstimatedSlippageTicks;
        double capital = (double)cfg.Run.InitialBalance;

        // Jeder (Parameter, Handelstagbereich) wird genau EINMAL über den bestehenden Pfad simuliert und dann wiederverwendet.
        var cache = new Dictionary<string, OrderflowRunResult>(StringComparer.Ordinal);
        static string PKey(IReadOnlyDictionary<string, string> p) => string.Join(";", p.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + "=" + k.Value));
        async Task<OrderflowRunResult> RunRange(IReadOnlyDictionary<string, string> p, int s0, int s1)
        {
            if (s1 > S) throw new InvalidOperationException($"Zugriff auf reservierte Holdout-Handelstage verweigert (Fenster bis Tag {s1}, Entwicklung endet bei {S}).");
            string key = PKey(p) + "|" + s0 + "-" + s1;
            if (cache.TryGetValue(key, out var hit)) return hit;
            int t0 = ranges[s0].Start, t1 = ranges[s1 - 1].End;
            var slice = new List<MarketTick>(t1 - t0);
            for (int i = t0; i < t1; i++) slice.Add(ticks[i]);
            var res = await OrderflowTickBacktest.RunAsync(
                _quant.Backtest.Ticks.BuildRequest(cfg.Run, slice, instrument, fee, lt.Source, lt.Sha256, new Dictionary<string, string>(p)), ct);
            cache[key] = res;
            return res;
        }
        List<SessionResult> PerSession(OrderflowRunResult? r, int s0, int s1)
        {
            var trades = r?.RunResults.Single().Trades ?? (IReadOnlyList<TradingBot.Backtesting.BacktestTrade>)Array.Empty<TradingBot.Backtesting.BacktestTrade>();
            var wv = WindowValuation.Of(r);
            return Enumerable.Range(s0, s1 - s0).Select(s =>
            {
                var k = ranges[s].Key;
                var l = trades.Where(t => TradeDateCalendar.Key(t.EntryTime) == k).ToList();
                // 0 Trades = getesteter Tag ohne Einstieg. Die offene Fensterend-Bewertung gehoert zum LETZTEN Handelstag des Fensters
                // und wird getrennt ausgewiesen (OpenValuation).
                bool last = s == s1 - 1 && wv.IsOpen;
                return new SessionResult(k, l.Sum(t => t.NetPnL) + (last ? wv.OpenValuation : 0m), l.Count, last ? wv.OpenValuation : null);
            }).ToList();
        }

        Task<SegmentOutcome> Evaluate(IReadOnlyDictionary<string, string> p, IReadOnlyList<DataSplit> segs, SplitRole role, CancellationToken tk)
            => EvaluateAsync(p, segs, role);
        async Task<SegmentOutcome> EvaluateAsync(IReadOnlyDictionary<string, string> p, IReadOnlyList<DataSplit> segs, SplitRole role)
        {
            try
            {
                int s0 = segs[0].Start, s1 = segs[^1].End;
                var r = await RunRange(p, s0, s1);
                var trades = r.RunResults.Single().Trades;
                // Auswahl-/Bewertungszahl = Fensterwert: geschlossenes Netto + offene Bewertung zum letzten Trade-Tick des Fensters.
                var wv = WindowValuation.Of(r);
                decimal net = wv.Value;
                double? sel = role == SplitRole.Train && trades.Count < opt.MinTrainTrades ? null : (double)net;
                var sess = PerSession(r, s0, s1);
                double eq = 1.0; var levels = new List<double>(); var rets = new List<double>();
                foreach (var s in sess) { double ret = (double)s.Net / capital; rets.Add(ret); eq *= 1 + ret; levels.Add(eq); }
                return new SegmentOutcome
                {
                    SelectionValue = sel, TradeCount = trades.Count,
                    Metrics = new Dictionary<string, double?>
                    {
                        ["net"] = (double)net, ["closednet"] = (double)wv.ClosedNet, ["openvaluation"] = (double)wv.OpenValuation, ["trades"] = trades.Count
                    },
                    Returns = new ReturnSeries
                    {
                        Name = "Netto je Handelstag", Timestamps = Enumerable.Range(s0, s1 - s0).Select(i => axis[i]).ToList(),
                        Returns = rets, EquityLevels = levels, InitialCapital = 1.0, Basis = EquityBasis.Realized, Frequency = ReturnFrequency.Daily
                    }
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return SegmentOutcome.Failed(ex.Message); }
        }

        int splitTrain = Math.Max(opt.FixedSplitMinTrainSessions, (int)Math.Floor(S * opt.FixedSplitTrainShare));
        var specs = new (string Key, string Label, WalkForwardMode Mode, int Train, int Test, int Need)[]
        {
            ("split", "Fester Train/Test-Split", WalkForwardMode.Rolling, splitTrain, Math.Max(opt.FixedSplitMinTestSessions, S - splitTrain),
                opt.FixedSplitMinTrainSessions + opt.FixedSplitMinTestSessions),
            ("rolling", "Rollierender Walk-forward", WalkForwardMode.Rolling, cfg.TrainSessions, cfg.TestSessions, cfg.TrainSessions + cfg.TestSessions),
            ("anchored", "Anwachsender Walk-forward", WalkForwardMode.Anchored, cfg.TrainSessions, cfg.TestSessions, cfg.TrainSessions + cfg.TestSessions)
        };

        var variants = new List<OosVariant>();
        foreach (var sp in specs)
        {
            ct.ThrowIfCancellationRequested();
            var v0 = new OosVariant
            {
                Key = sp.Key, Label = sp.Label, SessionsNeeded = sp.Need, SessionsAvailable = S, TrainSessions = sp.Train, TestSessions = sp.Test
            };
            if (S < sp.Need) { variants.Add(v0 with { Status = "InsufficientData", Reason = $"{sp.Need} Sessions benötigt, {S} vorhanden." }); continue; }

            var plan = WalkForwardPlanner.Plan(axis, new WalkForwardOptions
            {
                Mode = sp.Mode, TrainBars = 1, TestBars = 1, SessionStarts = starts,
                TrainSessions = sp.Train, TestSessions = sp.Key == "split" ? S - sp.Train : sp.Test, HoldoutFraction = cfg.HoldoutFraction
            });
            int plannerHoldoutStart = plan.Holdout?.Start ?? N;
            if (plannerHoldoutStart != S)
            {
                variants.Add(v0 with { Status = "Failed", Reason = $"Holdout-Grenze des Planers (Tag {plannerHoldoutStart}) weicht vom Bericht (Tag {S}) ab — Lauf verweigert." });
                continue;
            }
            if (plan.IsEmpty)
            {
                variants.Add(v0 with { Status = "InsufficientData", Reason = string.Join(" ", plan.Notes) });
                continue;
            }

            var run = await WalkForwardRunner.RunAsync(plan, candidates, Evaluate, "netprofit", SelectionDirection.HigherIsBetter, null, ct);
            fc.WfVariantsRun++;
            fc.WfSelectionAttempts += run.Folds.Count;

            var folds = new List<OosFold>();
            var oosTrades = new List<ReportTrade>(); var baseTradesAll = new List<ReportTrade>();
            var oosSessions = new List<SessionResult>(); var baseSessions = new List<SessionResult>();
            var oosMarks = new List<(DateTimeOffset Time, decimal Value)>(); var baseMarks = new List<(DateTimeOffset Time, decimal Value)>();
            decimal oosOpen = 0m, baseOpen = 0m; int openEnds = 0;
            foreach (var f in run.Folds)
            {
                int tr0 = f.Fold.Train[0].Start, tr1 = f.Fold.Train[^1].End, te0 = f.Fold.Test.Start, te1 = f.Fold.Test.End;
                var sel = f.Selected;
                OrderflowRunResult? selRun = sel is null ? null : await RunRange(sel.Parameters, te0, te1);
                var baseRun = await RunRange(baseParams, te0, te1);
                var selTrades = selRun?.RunResults.Single().Trades ?? (IReadOnlyList<TradingBot.Backtesting.BacktestTrade>)Array.Empty<TradingBot.Backtesting.BacktestTrade>();
                var bTrades = baseRun.RunResults.Single().Trades;
                var testIds = Enumerable.Range(te0, te1 - te0).Select(i => ranges[i].Key).ToList();
                var selSess = PerSession(selRun, te0, te1); var baseSess = PerSession(baseRun, te0, te1);
                oosSessions.AddRange(selSess); baseSessions.AddRange(baseSess);
                oosTrades.AddRange(ToReportTradesFromRun(selRun, instrument, slipTicks));
                baseTradesAll.AddRange(ToReportTradesFromRun(baseRun, instrument, slipTicks));
                var diag = selRun?.RunResults.Single().Diagnostics;
                var sv = WindowValuation.Of(selRun); var bv = WindowValuation.Of(baseRun);
                var testEnd = ticks[ranges[te1 - 1].End - 1].Timestamp;
                if (selRun is not null && sv.IsOpen) { oosOpen += sv.OpenValuation; openEnds++; oosMarks.Add((testEnd, sv.OpenValuation)); }
                if (bv.IsOpen) { baseOpen += bv.OpenValuation; baseMarks.Add((testEnd, bv.OpenValuation)); }
                var trainTrades = candidates.ToDictionary(c => c.Id, c =>
                    cache.TryGetValue(PKey(c.Parameters) + "|" + tr0 + "-" + tr1, out var cr) ? cr.RunResults.Single().Trades.Count : 0);
                folds.Add(new OosFold
                {
                    Index = f.Fold.Index,
                    TrainFromT = ticks[ranges[tr0].Start].Timestamp.ToUnixTimeMilliseconds(), TrainToT = ticks[ranges[tr1 - 1].End - 1].Timestamp.ToUnixTimeMilliseconds(),
                    TrainSessions = tr1 - tr0,
                    TestFromT = ticks[ranges[te0].Start].Timestamp.ToUnixTimeMilliseconds(), TestToT = ticks[ranges[te1 - 1].End - 1].Timestamp.ToUnixTimeMilliseconds(),
                    TestSessions = te1 - te0,
                    SelectedCandidate = sel?.Id,
                    SelectedParameters = sel is null ? null : new Dictionary<string, string>(sel.Parameters),
                    TrainValue = f.TrainSelectionValue,
                    TestNet = selRun is null ? null : selTrades.Sum(t => t.NetPnL), TestTrades = selTrades.Count,
                    TestFees = selRun is null ? null : selTrades.Sum(t => t.Fees), TestSignals = diag?.SignalsGenerated ?? 0,
                    BaseNet = bTrades.Sum(t => t.NetPnL), BaseTrades = bTrades.Count, BaseParameters = baseParams,
                    TrainSessionIds = Enumerable.Range(tr0, tr1 - tr0).Select(i => ranges[i].Key).ToList(),
                    TestSessionIds = testIds,
                    MissingDataDays = OrderflowTickBacktest.MissingWeekdays(testIds),
                    TestSessionResults = selSess, BaseTestSessionResults = baseSess,
                    CandidateTrainValues = f.TrainValues, CandidateTrainTrades = trainTrades,
                    OpenPositionAtTestEnd = selRun is not null && sv.IsOpen ? sv.Describe() : null,
                    TestOpenUnrealized = selRun is null ? null : sv.UnrealizedGross,
                    TestOpenEntryFees = selRun is null ? null : sv.OpenEntryFees,
                    TestOpenValuation = selRun is null ? null : sv.OpenValuation,
                    TestValue = selRun is null ? null : sv.Value,
                    BaseOpenValuation = bv.OpenValuation, BaseValue = bv.Value,
                    BaseOpenPositionAtTestEnd = bv.IsOpen ? bv.Describe() : null,
                    TestTradeRows = TradeRowsFromRun(selRun),
                    Note = sel is null ? $"Kein Kandidat erfüllte die Auswahlregel im Training (mind. {opt.MinTrainTrades} Trades) — kein Testergebnis." : f.Note
                });
            }
            var tStart = oosTrades.Count > 0 ? oosTrades.Min(t => t.EntryTime) : DateTimeOffset.UtcNow;
            var v = v0 with
            {
                Status = "Completed",
                TestSessions = sp.Key == "split" ? S - sp.Train : sp.Test,
                Folds = folds,
                OosNet = oosTrades.Sum(t => t.NetPnL), OosTrades = oosTrades.Count, OosSessions = oosSessions.Count,
                BaseNetSameSessions = baseTradesAll.Sum(t => t.NetPnL), BaseTradesSameSessions = baseTradesAll.Count,
                // Drawdown auf derselben Bewertungsbasis: Exits der geschlossenen Trades + je Fensterende die offene Bewertung.
                OosMaxDrawdown = DrawdownAnalysis.Analyze(ValuedCurve(oosTrades, oosMarks, capital, tStart), "").MaxDrawdown,
                BaseMaxDrawdownSameSessions = DrawdownAnalysis.Analyze(ValuedCurve(baseTradesAll, baseMarks, capital, tStart), "").MaxDrawdown,
                OosOpenValuation = oosOpen, OosValue = oosTrades.Sum(t => t.NetPnL) + oosOpen, OosOpenWindowEnds = openEnds,
                BaseOpenValuationSameSessions = baseOpen, BaseValueSameSessions = baseTradesAll.Sum(t => t.NetPnL) + baseOpen,
                OosSessionResults = oosSessions, BaseSessionResults = baseSessions,
                Reason = folds.Any(x => x.SelectedCandidate is null) ? "In mindestens einem Fenster erfüllte kein Kandidat die Auswahlregel." : null
            };
            variants.Add(v);
            if (sp.Key == "rolling") { fc.PrimaryOos = v; fc.OosTrades = oosTrades; fc.OosSessions = oosSessions; }
        }
        if (fc.PrimaryOos is null && variants.FirstOrDefault(x => x.Status == "Completed") is { } alt) { fc.PrimaryOos = alt; fc.OosSessions = alt.OosSessionResults.ToList(); }

        string thresholds = string.Join(" / ", cfg.Candidates.Select(c => c.TryGetValue("DeltaThreshold", out var t) ? t : "?"));
        string metricNote = string.Equals(cfg.SelectionMetric, "netprofit", StringComparison.OrdinalIgnoreCase)
            ? "" : $" (konfiguriert war „{cfg.SelectionMetric}“ — im Tick-Pfad gilt ausschließlich Netto nach Kosten)";
        return new ReportOosSection
        {
            SelectionMetric = "netprofit",
            SelectionRule = $"Je Trainingsfenster laufen alle {candidates.Count} Kandidaten (Delta-Schwelle {thresholds}; Ticks je Bar fest) durch den bestehenden " +
                            $"Orderflow-Pfad. Gewählt wird der höchste Fensterwert IM TRAINING = Netto nach Kosten der geschlossenen Trades PLUS offene Bewertung " +
                            $"am letzten Trade-Tick des Trainingsfensters{metricNote}; Kandidaten mit weniger als {opt.MinTrainTrades} abgeschlossenen " +
                            "Trades im Training nehmen nicht teil. Der Gewinner wird eingefroren und unverändert auf das unmittelbar folgende Testfenster angewendet.",
            SelectionFigure = "Netto inkl. offener Bewertung am letzten Trade-Tick des Fensters (geschlossene Trades nach Kosten + unrealisiert brutto − bereits gezahlte Einstiegsgebühr).",
            WindowEndRule = WindowValuation.Rule,
            TieRule = "Lexikografisch kleinste Kandidaten-Id (deterministisch, vorab festgelegt). Kandidaten ohne gültigen Wert zählen nicht als schlechtestes Ergebnis.",
            MinTrainTrades = opt.MinTrainTrades,
            Grid = cfg.Candidates.Select(c => (IReadOnlyDictionary<string, string>)c).ToList(),
            WarmupBars = 0,
            LeakageRules = "Jedes Trainings- und Testfenster wird getrennt aus GENAU den Ticks seiner Handelstage simuliert; Bars werden im Fenster neu gebildet, " +
                           "jedes Fenster startet flach. Keine Position wird über eine Fenstergrenze übertragen — eine am Fensterende offene Position wird zum letzten " +
                           "Trade-Tick DIESES Fensters bewertet (kein Exit-Fill, kein Trade im Journal, kein Tick des Folgefensters). Warm-up: keiner nötig (DeltaFlow nutzt nur die geschlossene Bar). Ein Tag ohne Trade ist ein getesteter Tag mit 0 Trades; " +
                           "Werktage ohne Daten sind separat als fehlende Datentage ausgewiesen.",
            HoldoutNote = holdoutIds.Count > 0
                ? $"Finaler Holdout – unbenutzt: {holdoutIds.Count} Handelstag(e) ({string.Join(", ", holdoutIds)}). Weder Planer-Fenster noch Kandidatenläufe erhalten diese Ticks; keine Holdout-Performance berechnet."
                : "Kein finaler Holdout reserviert (zu wenige Handelstage).",
            Variants = variants,
            PrimaryVariant = fc.PrimaryOos?.Key,
            DevelopmentSessionIds = devIds,
            MissingDataDays = OrderflowTickBacktest.MissingWeekdays(devIds),
            HoldoutSessionIds = holdoutIds
        };
    }

    private static List<ReportTrade> ToReportTradesFromRun(OrderflowRunResult? r, InstrumentProfile inst, decimal slipTicks)
    {
        if (r is null) return new List<ReportTrade>();
        return r.RunResults.Single().Trades.Select((t, i) => new ReportTrade
        {
            Index = i, EntryTime = t.EntryTime, ExitTime = t.ExitTime, IsLong = t.Side == PositionSide.Long, Quantity = t.Quantity,
            EntryPrice = t.EntryPrice, ExitPrice = t.ExitPrice, GrossPnL = t.GrossPnL, Fees = t.Fees, NetPnL = t.NetPnL,
            SlippageCost = slipTicks * inst.TickValue * t.Quantity * 2, MarketSides = 2, ExitReason = "Signal",
            SessionKey = TradeDateCalendar.Key(t.EntryTime)
        }).ToList();
    }

    /// <summary>Trades eines Orderflow-Laufs 1:1 (Engine-Fills + Signal-Mitschnitt), ohne Neuberechnung.</summary>
    private static IReadOnlyList<TradeRow> TradeRowsFromRun(OrderflowRunResult? r)
    {
        if (r is null) return Array.Empty<TradeRow>();
        var ws = r.Workspace.Strategies.Single();
        var bt = r.RunResults.Single().Trades;
        return bt.Select((t, i) =>
        {
            var w = ws.Trades[i];
            return new TradeRow(i, t.Side == PositionSide.Long ? "Long" : "Short", t.EntryTime.ToUnixTimeMilliseconds(), t.ExitTime.ToUnixTimeMilliseconds(),
                t.EntryPrice, t.ExitPrice, w.EntryBarIndex, w.ExitBarIndex, t.GrossPnL, t.Fees, t.NetPnL, "Signal (Gegen-/Flat-Signal)", false, false,
                TradeDateCalendar.Key(t.EntryTime), null, null, false, "", 0, 0,
                w.EntrySignal?.TimeUtc, w.EntrySignal?.Direction, w.ExitSignal?.TimeUtc, w.ExitSignal?.Direction, w.EntryTimeUtc, w.ExitTimeUtc);
        }).ToList();
    }

    /// <summary>
    /// Kapitalkurve auf Bewertungsbasis: Startpunkt, ein Punkt je Exit eines geschlossenen Trades und je Fensterende mit offener
    /// Position die offene Bewertung (Fenster sind unabhängig und starten flach — die Bewertung geht nicht ins Folgefenster über).
    /// </summary>
    private static IReadOnlyList<EquitySample> ValuedCurve(IReadOnlyList<ReportTrade> trades, IReadOnlyList<(DateTimeOffset Time, decimal Value)> marks,
        double capital, DateTimeOffset start)
    {
        var events = trades.Select(t => (Time: t.ExitTime, Order: 0, Value: t.NetPnL)).Concat(marks.Select(m => (m.Time, Order: 1, m.Value)))
            .OrderBy(e => e.Time).ThenBy(e => e.Order).ToList();
        var list = new List<EquitySample>(events.Count + 1) { new(start, capital) };
        double eq = capital;
        foreach (var e in events) { eq += (double)e.Value; list.Add(new EquitySample(e.Time, eq)); }
        return list;
    }
}

/// <summary>
/// Einheitliche Fensterend-Bewertung eines Orderflow-Laufs (eine Regel für Training, Test, Standardvergleich und Zusammenfassung):
/// Fensterwert = Netto der geschlossenen Trades + (unrealisiert brutto zum letzten Trade-Tick IM Fenster − bereits gezahlte
/// Einstiegsgebühr der offenen Position). Das ist eine BEWERTUNG, kein ausgeführter Exit: kein Fill, kein Journal-Eintrag,
/// keine Exit-Gebühr, kein Tick aus dem Folgefenster. Quelle sind die Werte des bestehenden Runners (WsStrategy).
/// </summary>
internal readonly record struct WindowValuation(decimal ClosedNet, decimal UnrealizedGross, decimal OpenEntryFees, decimal LastTickPrice,
    string Side, int Quantity, decimal AvgPrice)
{
    public const string Rule =
        "Offene Position am Fensterende: wird zum LETZTEN Trade-Tick innerhalb des Fensters bewertet (unrealisiert brutto, Slippage steckt im Einstiegs-Fill) " +
        "abzüglich der bereits gezahlten Einstiegsgebühr. Keine Exit-Gebühr, kein fiktiver Fill im Journal, kein Tick aus dem Folgefenster; das nächste Fenster " +
        "startet flach. Geschlossene Trades und offene Bewertung werden getrennt ausgewiesen; die Auswahl im Training nutzt die Summe (Netto inkl. offener Bewertung).";

    public bool IsOpen => Quantity > 0;
    public decimal OpenValuation => IsOpen ? UnrealizedGross - OpenEntryFees : 0m;
    public decimal Value => ClosedNet + OpenValuation;

    public static WindowValuation Of(OrderflowRunResult? r)
    {
        if (r is null) return default;
        var ws = r.Workspace.Strategies.Single();
        return new WindowValuation(ws.RealizedNetPnL, ws.UnrealizedGrossAtLastTick, ws.OpenPositionEntryFees, ws.LastTickPrice,
            ws.OpenPositionSide, ws.OpenPositionQty, ws.OpenPositionAvgPrice);
    }

    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"{Side} {Quantity} @ {AvgPrice} — offen am Fensterende, bewertet zum letzten Trade-Tick {LastTickPrice}: unrealisiert {UnrealizedGross:0.00} − Einstiegsgebühr {OpenEntryFees:0.00} = {OpenValuation:0.00} (kein Trade, kein Exit-Fill)");
}
