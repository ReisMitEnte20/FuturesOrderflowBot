using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingBot.Application.MarketState;
using TradingBot.Application.Sessions;
using TradingBot.Backtesting;
using TradingBot.DevDashboard.Services.Live;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.MarketData;

namespace TradingBot.DevDashboard.Services.Mtf;

// =====================================================================================================================
// Multi-Timeframe-Orderflow-Lauf (5m / 1m / rangeXV gleichzeitig) über die BESTEHENDE Tick-Engine. Datenprüfung vor dem
// Start, Entscheidungsspur beim Entscheiden, deterministischer Trade-Rewind durch Wiederholung ab Sessionbeginn.
// =====================================================================================================================

public sealed record MtfRunRequest
{
    /// <summary>synthetic | recording</summary>
    public string Dataset { get; init; } = "synthetic";
    public int Seed { get; init; } = 7;
    public int Minutes { get; init; } = 90;
    /// <summary>Handelstag (yyyy-MM-dd) bei Dataset=recording.</summary>
    public string? Day { get; init; }
    /// <summary>London | NewYork | both</summary>
    public string Sessions { get; init; } = "NewYork";
    public string Strategy { get; init; } = MtfFunctionTestStrategy.Id;
    public decimal OneMinuteDeltaMin { get; init; } = 20m;
    public int RangePeriod { get; init; } = 4;
    /// <summary>Ausdrücklich gewählter Demonstrationslauf auf generierten Replay-Ereignissen (bleibt im Bericht erkennbar).</summary>
    public bool DemoWithGenerated { get; init; }
    public decimal InitialBalance { get; init; } = 10_000m;
    public int CloseOutMinutes { get; init; } = 5;
}

public sealed record MtfDataStatement(string Category, string Text);
public sealed record MtfRequirement(string Need, string Availability, string Status, string Text);
public sealed record MtfSegmentInfo(string Id, string Session, string Day, string WindowStartUtc, string WindowEndUtc, string? RecordingId, string Provenance,
    IReadOnlyDictionary<string, string> Streams, long? Drops, string? Conflict, long Events);
public sealed record MtfDataCheck(string Dataset, string Label, IReadOnlyList<MtfSegmentInfo> Segments, IReadOnlyList<MtfDataStatement> Statements,
    IReadOnlyList<MtfRequirement> Requirements, bool Runnable, bool RequiresDemoFlag, IReadOnlyList<string> Blockers, string RangeSemantics);

public sealed record MtfTradeEvent(string Kind, long EventIndex, string TimeUtc, decimal? Price, string Text);
public sealed record MtfTrade(int Index, string Session, string Side, int Qty, string EntryTimeUtc, decimal EntryPrice, string ExitTimeUtc, decimal ExitPrice,
    decimal GrossPnL, decimal Fees, decimal NetPnL, decimal BalanceAfter, int? EntryTraceId, int? ExitTraceId, long? SignalEvent, long? EntryFillEvent,
    long? ExitSignalEvent, long? ExitFillEvent, IReadOnlyList<MtfTradeEvent> Events, string Explanation, string? EntrySignalId, string? EntryOrderId, string? EntryFillId,
    string? ExitOrderId, string? ExitFillId);

public sealed record MtfRunResult
{
    public string RunId { get; init; } = "";
    public string CreatedUtc { get; init; } = "";
    public string Status { get; init; } = "";
    public string? Error { get; init; }
    public MtfRunRequest Request { get; init; } = new();
    public MtfDataCheck? DataCheck { get; init; }
    public bool IsDemonstration { get; init; }
    public string RunKind { get; init; } = "";
    public string Strategy { get; init; } = "";
    public string StrategyVersion { get; init; } = "";
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
    public string Symbol { get; init; } = "";
    public decimal TickSize { get; init; }
    public decimal PointValue { get; init; }
    public decimal FeePerSide { get; init; }
    public decimal SlippageTicks { get; init; }
    public string RangeSemantics { get; init; } = "";
    public string ProcessingOrder { get; init; } = "";
    public long Events { get; init; }
    public IReadOnlyList<MtfTrade> Trades { get; init; } = Array.Empty<MtfTrade>();
    public IReadOnlyList<DecisionTrace> Traces { get; init; } = Array.Empty<DecisionTrace>();
    public IReadOnlyList<PositionTransition> Transitions { get; init; } = Array.Empty<PositionTransition>();
    public decimal StartBalance { get; init; }
    public decimal EndBalance { get; init; }
    public decimal NetPnL { get; init; }
    public decimal GrossPnL { get; init; }
    public decimal Fees { get; init; }
    public decimal MaxDrawdown { get; init; }
    public string FinalPosition { get; init; } = "Flat";
    public decimal OpenValuation { get; init; }
    public string Fingerprint { get; init; } = "";
    public double ElapsedSeconds { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed record MtfReplayWindow(string RunId, int Trade, string FromUtc, string ToUtc, long FromEvent, long ToEvent, bool Reproduced, string? FirstDifference,
    IReadOnlyList<object[]> Events, IReadOnlyList<object[]> States, IReadOnlyDictionary<string, IReadOnlyList<object[]>> ClosedBars,
    IReadOnlyList<DecisionTrace> Traces, IReadOnlyList<PositionTransition> Transitions, MtfTrade TradeInfo, string StateLegend, string BarLegend);

public sealed class MtfRunService
{
    public const string Order = "Je Ereignis: (1) offene Orders der Engine werden mit diesem Ereignis gefüllt, (2) Barabschlüsse aller drei Sichten werden erkannt, (3) das Ereignis fließt in Bars und Sessionkontext, (4) die Strategie entscheidet auf diesem Zustand, (5) eine Order daraus ist frühestens beim nächsten Ereignis füllbar. Zeitbars gelten als geschlossen mit dem ersten Ereignis ab ihrem Ende. Gleiche Zeitstempel: Reihenfolge der Quelldatei.";

    private readonly BacktestApiService _bt;
    private readonly TickReplayService? _replay;
    private readonly RecordingProvenanceService? _prov;
    private readonly string _dir;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public MtfRunService(BacktestApiService bt, TickReplayService? replay, RecordingProvenanceService? prov, string runsDir)
    {
        _bt = bt; _replay = replay; _prov = prov; _dir = runsDir;
    }

    // ------------------------------------------------------------------------------------------------ Datensatz

    private sealed record Segment(SessionSegment Seg, MtfSegmentInfo Info, Func<CancellationToken, Task<List<MarketTick>>> Load);

    private List<Segment> Segments(MtfRunRequest r, int closeOut)
    {
        var res = new List<Segment>();
        if (r.Dataset == "synthetic")
        {
            var start = new DateTimeOffset(2026, 1, 15, 14, 30, 0, TimeSpan.Zero);
            var end = start.AddMinutes(Math.Clamp(r.Minutes, 10, 390));
            var seg = new SessionSegment("synthetic", "NewYork", "Synthetisch (New York)", DateOnly.FromDateTime(start.UtcDateTime), start, end, end.AddMinutes(-closeOut));
            var ticks = MtfSyntheticSession.Generate(r.Seed, start, (int)(end - start).TotalMinutes);
            res.Add(new Segment(seg, new MtfSegmentInfo("synthetic", "NewYork", "2026-01-15", Iso(start), Iso(end), null, "synthetisch (Funktionstest, keine Marktdaten)",
                new Dictionary<string, string> { ["Trades"] = "synthetisch", ["Tape"] = "fehlt", ["BBO"] = "fehlt", ["DOM"] = "fehlt" }, 0, null, ticks.Count),
                _ => Task.FromResult(ticks)));
            return res;
        }
        if (_replay is null) throw new InvalidOperationException("Keine Aufnahmen konfiguriert.");
        if (!DateOnly.TryParseExact(r.Day ?? "", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) throw new ArgumentException("Handelstag fehlt (yyyy-MM-dd).");
        var want = r.Sessions == "both" ? new[] { "London", "NewYork" } : new[] { r.Sessions };
        var jobs = _replay.ResolvedJobs().Where(j => j.Day == day && want.Contains(j.Session)).OrderBy(j => j.StartUtc).ToList();
        foreach (var s in want)
        {
            var js = jobs.Where(j => j.Session == s).ToList();
            if (js.Count == 0) { res.Add(new Segment(new SessionSegment($"{s}:{day:yyyy-MM-dd}", s, s, day, default, default, default),
                new MtfSegmentInfo("", s, day.ToString("yyyy-MM-dd"), "", "", null, "fehlt", new Dictionary<string, string>(), null, "keine Aufnahme für diese Session", 0), _ => Task.FromResult(new List<MarketTick>()))); continue; }
            var j = js.FirstOrDefault(x => x.Usable) ?? js[0];
            string? conflict = js.Count > 1 && j.Selection == "conflict" ? $"Quellenkonflikt: {j.SelectionNote}" : !j.Usable ? "Aufnahme nicht nutzbar (Drops/Summary)" : null;
            var p = _prov?.Get(j.RecordingId);
            var label = s == "NewYork" ? "New York" : s;
            res.Add(new Segment(new SessionSegment(j.Id, s, label, day, j.StartUtc, j.EndUtc, j.EndUtc.AddMinutes(-closeOut)),
                new MtfSegmentInfo(j.Id, s, day.ToString("yyyy-MM-dd"), Iso(j.StartUtc), Iso(j.EndUtc), j.RecordingId, p?.Banner ?? "Herkunft ungeklärt",
                    p?.Streams ?? new Dictionary<string, string>(), j.RecorderDrops, conflict, j.Rows),
                async ct => (await _replay.LoadMarketTicksAsync(j.Id, ct)).Ticks));
        }
        return res;
    }

    public MtfDataCheck Check(MtfRunRequest r)
    {
        var segs = Segments(r, r.CloseOutMinutes);
        var strat = BuildStrategy(r);
        var st = new List<MtfDataStatement>();
        var blockers = new List<string>();
        bool synthetic = r.Dataset == "synthetic";
        bool generated = segs.Any(s => s.Info.Streams.GetValueOrDefault("Trades", "").StartsWith("Minutenaggregat", StringComparison.Ordinal));
        foreach (var s in segs)
        {
            if (s.Info.Conflict is not null) blockers.Add($"{s.Info.Session}: {s.Info.Conflict}");
            if (s.Info.Drops is > 0) blockers.Add($"{s.Info.Session}: {s.Info.Drops} Recorder-Drops");
        }
        if (synthetic)
        {
            st.Add(new("generierte Ereignisse", "Synthetische, deterministische Ereignisfolge (Seed) – Funktionstest, keine Marktdaten."));
            st.Add(new("fehlende Daten", "Keine Quotes (Bid/Ask), kein DOM, kein Tape."));
        }
        else
        {
            st.Add(new("ATAS-Minutenwerte", "Minutenwerte (OHLC, Volumen, Bid/Ask-Volumen je Minute) stammen aus ATAS-Minutenkerzen."));
            if (generated) st.Add(new("generierte Replay-Ereignisse", "Reihenfolge, Zeitpunkt und Einzelgröße der Ereignisse INNERHALB einer Minute wurden im ATAS-Replay generiert – nicht die Börsenreihenfolge. Range-Bars, Absorption und Fills darauf sind daher nicht mit echten Börsentrades belegt."));
            st.Add(new("fehlende Daten", "Historische Quotes und historisches DOM: nicht vorhanden (im Replay generiert, eine Preisstufe je Seite)."));
        }
        st.Add(new("eigene Berechnungen", "5m/1m/rangeXV-Bars, Session-VWAP, CVD, Volumen- und TPO-Profil, Swings, Absorption: von uns aus der Ereignisfolge berechnet."));
        st.Add(new("vorläufig", RangeXvBarBuilder.SemanticsNote));
        var req = new List<MtfRequirement>();
        foreach (var need in strat.Requires)
        {
            string avail = synthetic ? "synthetisch" : need switch
            {
                "trades" or "aggressor" => generated ? "generiert (aus ATAS-Minutenkerzen)" : "beobachtet/ungeklärt",
                "dom_historisch" or "quotes_historisch" => "fehlt",
                _ => "ungeklärt"
            };
            string status = avail == "fehlt" ? "blockiert" : avail.StartsWith("generiert") || avail == "synthetisch" ? "nur_demonstration" : "ok";
            req.Add(new MtfRequirement(need, avail, status, status switch
            {
                "blockiert" => $"Regel braucht „{need}“ – die Quelle liefert es nicht. Kein Ersatzwert.",
                "nur_demonstration" => $"„{need}“ ist {avail} – Lauf nur als ausdrücklich gewählter Demonstrations-/Funktionslauf.",
                _ => $"„{need}“ verfügbar."
            }));
        }
        if (req.Any(x => x.Status == "blockiert")) blockers.Add("Datenbedarf der Strategie nicht gedeckt.");
        if (segs.All(s => s.Info.Events == 0)) blockers.Add("Keine Ereignisse im gewählten Datensatz.");
        bool demo = req.Any(x => x.Status == "nur_demonstration") && !synthetic;
        return new MtfDataCheck(r.Dataset, synthetic ? $"Synthetisch · Seed {r.Seed} · {r.Minutes} min" : $"Aufnahme {r.Day} · {r.Sessions}",
            segs.Select(s => s.Info).ToList(), st, req, blockers.Count == 0, demo, blockers, RangeXvBarBuilder.SemanticsNote);
    }

    private static IMultiTimeframeStrategy BuildStrategy(MtfRunRequest r) => r.Strategy switch
    {
        MtfFunctionTestStrategy.Id => new MtfFunctionTestStrategy(r.OneMinuteDeltaMin),
        _ => throw new ArgumentException($"Unbekannte Multi-Timeframe-Strategie: {r.Strategy}")
    };

    // ------------------------------------------------------------------------------------------------ Lauf

    private sealed record Executed(BacktestResult Engine, MultiTimeframeHost Host, List<MarketTick> Ticks, InstrumentProfile Instrument, decimal FeePerSide, decimal Slippage);

    private async Task<Executed> Execute(MtfRunRequest r, List<Segment> segs, Action<MarketSnapshot, MarketTick>? after, CancellationToken ct)
    {
        var runReq = new BacktestRunRequest { DataSourceId = "mtf", Symbol = "MNQ", InitialBalance = r.InitialBalance, ApplyFees = true, Strategy = "deltaflow", Params = new Dictionary<string, string>() };
        var (instrument, fee, _, _) = await _bt.ResolveProfilesAsync(runReq, ct);
        var orq = _bt.Ticks.BuildRequest(runReq, Array.Empty<MarketTick>(), instrument, fee, "mtf", "", strategy: "deltaflow");
        var ticks = new List<MarketTick>();
        var used = segs.Where(s => s.Info.Events > 0).ToList();
        foreach (var s in used)
            foreach (var t in await s.Load(ct))
                if (t.Timestamp >= s.Seg.StartUtc && t.Timestamp < s.Seg.EndUtc) ticks.Add(t with { Symbol = instrument.Symbol });
        var host = new MultiTimeframeHost(BuildStrategy(r), instrument.TickSize, r.RangePeriod) { AfterEvent = after };
        var gate = new SessionGateStrategy(host, used.Select(s => s.Seg).ToList());
        var res = await new BacktestEngine().RunAsync(new BacktestRequest
        {
            MarketData = new ReplayMarketDataProvider(ticks, ReplayOptions.Fast), Symbol = instrument.Symbol, Strategy = gate, Instrument = instrument, Fee = orq.Fee,
            Broker = orq.Broker, Risk = orq.Risk, Account = orq.Account, Config = new BacktestConfiguration { SlippageTicksOverride = orq.SlippageTicksOverride },
            TickGuard = c => gate.BeforeTick(c.NextTick.Timestamp, c.PositionQuantity, c.PendingOrders)
        }, ct);
        decimal feeSide = orq.Fee.CommissionPerSide + orq.Fee.ExchangeFeePerSide + orq.Fee.ClearingFeePerSide + orq.Fee.RoutingFeePerSide + orq.Fee.NfaFeePerSide + orq.Fee.OtherFeePerSide;
        return new Executed(res, host, ticks, instrument, feeSide, orq.SlippageTicksOverride ?? orq.Fee.EstimatedSlippageTicks);
    }

    public async Task<MtfRunResult> RunAsync(MtfRunRequest r, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var check = Check(r);
        string runId = $"mtf-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{(r.Dataset == "synthetic" ? $"syn{r.Seed}" : r.Day!.Replace("-", ""))}";
        var baseRes = new MtfRunResult { RunId = runId, CreatedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), Request = r, DataCheck = check, ProcessingOrder = Order, RangeSemantics = RangeXvBarBuilder.SemanticsNote };
        if (!check.Runnable) return await Save(baseRes with { Status = "Blocked", Error = string.Join(" ", check.Blockers) }, ct);
        if (check.RequiresDemoFlag && !r.DemoWithGenerated)
            return await Save(baseRes with { Status = "Blocked", Error = "Der Datensatz enthält generierte Replay-Ereignisse. Starten nur als ausdrücklich gewählter Demonstrationslauf („Demonstration mit generierten Ereignissen“)." }, ct);
        var ex = await Execute(r, Segments(r, r.CloseOutMinutes), null, ct);
        var result = Build(baseRes, r, check, ex) with { ElapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2) };
        return await Save(result, ct);
    }

    private static MtfRunResult Build(MtfRunResult b, MtfRunRequest r, MtfDataCheck check, Executed ex)
    {
        var res = ex.Engine; var host = ex.Host;
        var orders = res.Orders.ToDictionary(o => o.OrderId);
        var traceBySignal = host.Traces.Where(t => t.SignalId is not null).ToDictionary(t => t.SignalId!.Value);
        var entries = host.Transitions.Where(t => t.From == "Flat").ToList();
        var exits = host.Transitions.Where(t => t.From != "Flat" && t.To == "Flat").ToList();
        decimal bal = r.InitialBalance, peak = bal, dd = 0;
        var trades = new List<MtfTrade>();
        for (int i = 0; i < res.Trades.Count; i++)
        {
            var t = res.Trades[i];
            var link = i < res.TradeLinks.Count ? res.TradeLinks[i] : null;
            DecisionTrace? et = link is not null && orders.TryGetValue(link.EntryOrderId, out var eo) && traceBySignal.TryGetValue(eo.SignalId, out var a) ? a : null;
            DecisionTrace? xt = link is not null && orders.TryGetValue(link.ExitOrderId, out var xo) && traceBySignal.TryGetValue(xo.SignalId, out var bb) ? bb : null;
            var ef = i < entries.Count ? entries[i] : null;
            var xf = i < exits.Count ? exits[i] : null;
            bal += t.NetPnL; peak = Math.Max(peak, bal); dd = Math.Max(dd, peak - bal);
            var evs = new List<MtfTradeEvent>();
            if (et is not null) evs.Add(new("signal", et.EventIndex, Iso(et.MarketTime), et.Price, et.Summary));
            if (ef is not null) evs.Add(new("einstieg_fill", ef.EventIndex, Iso(ef.MarketTime), t.EntryPrice, $"simulierter Fill @ {N(t.EntryPrice)} (Markt {N(ef.TickPrice)}, Slippage {N(ex.Slippage)} Tick)"));
            if (xt is not null) evs.Add(new("ausstieg_signal", xt.EventIndex, Iso(xt.MarketTime), xt.Price, xt.Summary));
            else evs.Add(new("ausstieg_signal", -1, "", null, "Ausstieg ohne Strategiesignal (z. B. Session-Close-Regel)"));
            if (xf is not null) evs.Add(new("ausstieg_fill", xf.EventIndex, Iso(xf.MarketTime), t.ExitPrice, $"simulierter Fill @ {N(t.ExitPrice)} (Markt {N(xf.TickPrice)})"));
            string expl = Explain(et, ef, xt, xf, t);
            trades.Add(new MtfTrade(i, et?.Session ?? "", t.Side.ToString(), t.Quantity, Iso(t.EntryTime), t.EntryPrice, Iso(t.ExitTime), t.ExitPrice, t.GrossPnL, t.Fees, t.NetPnL, bal,
                et?.Id, xt?.Id, et?.EventIndex, ef?.EventIndex, xt?.EventIndex, xf?.EventIndex, evs, expl, et?.SignalId?.ToString(), link?.EntryOrderId.ToString(),
                link?.EntryFillId.ToString(), link?.ExitOrderId.ToString(), link?.ExitFillId.ToString()));
        }
        bool demo = check.RequiresDemoFlag;
        var strat = BuildStrategy(r);
        return b with
        {
            Status = res.Status == BacktestRunStatus.Completed ? "Completed" : res.Status == BacktestRunStatus.Stopped ? "Incomplete" : res.Status.ToString(),
            Error = res.Status == BacktestRunStatus.Completed ? null : res.Message,
            IsDemonstration = demo || r.Dataset == "synthetic",
            RunKind = r.Dataset == "synthetic" ? "Funktionstest (synthetische Ereignisse)" : demo ? "DEMONSTRATIONSLAUF auf generierten Replay-Ereignissen" : "Lauf auf beobachteten Ereignissen",
            Strategy = strat.Name, StrategyVersion = strat.Version, Parameters = strat.Parameters, Symbol = ex.Instrument.Symbol, TickSize = ex.Instrument.TickSize,
            PointValue = ex.Instrument.PointValue, FeePerSide = ex.FeePerSide, SlippageTicks = ex.Slippage, Events = host.EventCount, Trades = trades,
            Traces = host.Traces.ToList(), Transitions = host.Transitions.ToList(), StartBalance = r.InitialBalance, EndBalance = bal,
            NetPnL = trades.Sum(x => x.NetPnL), GrossPnL = trades.Sum(x => x.GrossPnL), Fees = trades.Sum(x => x.Fees), MaxDrawdown = dd,
            FinalPosition = res.FinalPositionSide == PositionSide.Flat ? "Flat" : $"{res.FinalPositionSide} {res.FinalPositionQuantity}",
            OpenValuation = res.FinalPositionUnrealizedAtLastTick,
            Fingerprint = Fingerprint(host.Traces, host.Transitions, res.Trades),
            Notes = new[] { "Kontostand, Tageslimit und Indikator-Reset sind getrennt: Konto/Tagesrisiko laufen über Sessions weiter, Bars/VWAP/CVD/Profile starten je Session neu." }
        };
    }

    private static string Explain(DecisionTrace? et, PositionTransition? ef, DecisionTrace? xt, PositionTransition? xf, BacktestTrade t)
    {
        if (et is null) return "Entscheidungsspur für diesen Einstieg nicht aufgezeichnet.";
        string Part(string role) => string.Join("; ", et.Checks.Where(c => c.Role == role).Select(c => $"{role} {c.View} {c.Result} ({c.Actual} | Soll {c.Expected})"));
        var sb = new StringBuilder();
        sb.Append(Part("Kontext")).Append("; ").Append(Part("Bestätigung")).Append("; ").Append(Part("Trigger"));
        sb.Append(FormattableString.Invariant($"; Signal bei Ereignis {et.EventIndex} ({et.MarketTime:HH:mm:ss.fff} UTC)"));
        if (ef is not null) sb.Append(FormattableString.Invariant($"; simulierter Fill bei Ereignis {ef.EventIndex} @ {t.EntryPrice}"));
        if (xt is not null) sb.Append(FormattableString.Invariant($"; Ausstieg: {xt.Checks.FirstOrDefault()?.Actual} bei Ereignis {xt.EventIndex}"));
        if (xf is not null) sb.Append(FormattableString.Invariant($"; Ausstiegs-Fill bei Ereignis {xf.EventIndex} @ {t.ExitPrice}"));
        return sb.ToString();
    }

    public static string Fingerprint(IEnumerable<DecisionTrace> traces, IEnumerable<PositionTransition> trans, IEnumerable<BacktestTrade> trades)
    {
        var sb = new StringBuilder();
        foreach (var t in traces) sb.Append(TraceKey(t)).Append('\n');
        foreach (var x in trans) sb.Append(FormattableString.Invariant($"P|{x.EventIndex}|{x.From}|{x.To}|{x.AvgPrice}\n"));
        foreach (var t in trades) sb.Append(FormattableString.Invariant($"T|{t.EntryTime:O}|{t.EntryPrice}|{t.ExitTime:O}|{t.ExitPrice}|{t.Fees}|{t.NetPnL}\n"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    public static string TraceKey(DecisionTrace t) =>
        FormattableString.Invariant($"D|{t.EventIndex}|{t.Kind}|{t.Direction}|{t.Price}|{string.Join(",", t.Checks.Select(c => $"{c.Rule}={c.Result}:{c.Actual}"))}|5m:{t.M5Closed?.Close}/{t.M5Open?.High}|1m:{t.M1Closed?.Delta}|r:{t.R4Closed?.Index}");

    // ------------------------------------------------------------------------------------------------ Trade-Rewind

    public const string StateLegend = "[ereignis, 5m:[bar,o,h,l,c,vol,buy,sell], 1m:[…], rangeXV:[…], vwap, cvd, vpoc, vvah, vval, tpoc, tvah, tval, swingHoch, swingHochBestätigtBeiEreignis, swingTief, swingTiefBestätigtBeiEreignis, absorption]";
    public const string BarLegend = "[bar, start, o, h, l, c, vol, buy, sell, ersterEreignis, geschlossenBeiEreignis]";

    public async Task<MtfReplayWindow> ReplayAsync(string runId, int tradeIndex, int prerollSeconds, int postrollSeconds, CancellationToken ct = default)
    {
        var run = Get(runId) ?? throw new KeyNotFoundException("Lauf nicht gefunden: " + runId);
        if (tradeIndex < 0 || tradeIndex >= run.Trades.Count) throw new ArgumentException("Trade nicht im Lauf.");
        var tr = run.Trades[tradeIndex];
        if (tr.SignalEvent is null) throw new InvalidOperationException("Für diesen Trade wurde damals keine Entscheidungsspur aufgezeichnet.");
        var sigTime = DateTimeOffset.Parse(tr.Events.First(e => e.Kind == "signal").TimeUtc, CultureInfo.InvariantCulture);
        var exitTime = DateTimeOffset.Parse(tr.ExitTimeUtc, CultureInfo.InvariantCulture);
        var from = sigTime.AddSeconds(-Math.Clamp(prerollSeconds, 5, 1800));
        var to = exitTime.AddSeconds(Math.Clamp(postrollSeconds, 0, 600));
        var events = new List<object[]>(); var states = new List<object[]>();
        long first = -1, last = -1;
        static object[] B(FlowBar? b) => b is null ? Array.Empty<object>() : new object[] { b.Index, b.Open, b.High, b.Low, b.Close, b.Volume, b.BuyVolume, b.SellVolume };
        // Wiederholung ab Sessionbeginn (reproduzierbare Wiederherstellung statt Kopie der Session): Indikatoren und
        // Strategie haben am Fensterbeginn exakt ihren damaligen Zustand.
        var ex = await Execute(run.Request, Segments(run.Request, run.Request.CloseOutMinutes), (s, t) =>
        {
            if (t.Timestamp < from || t.Timestamp > to) return;
            if (first < 0) first = s.EventIndex;
            last = s.EventIndex;
            events.Add(new object[] { s.EventIndex, Iso(t.Timestamp), t.Price, t.Volume, t.Aggressor == AggressorSide.Buy ? 1 : t.Aggressor == AggressorSide.Sell ? -1 : 0 });
            var c = s.Context;
            states.Add(new object?[] { s.EventIndex, B(s.M5.Current), B(s.M1.Current), B(s.R4.Current), c.SessionVwap, c.Cvd, c.VolumeProfile.Poc, c.VolumeProfile.Vah, c.VolumeProfile.Val,
                c.TpoProfile.Poc, c.TpoProfile.Vah, c.TpoProfile.Val, c.LastSwingHigh?.Price, c.LastSwingHigh?.ConfirmedAtEvent, c.LastSwingLow?.Price, c.LastSwingLow?.ConfirmedAtEvent,
                c.Absorption?.Side }!);
        }, ct);
        string fp = Fingerprint(ex.Host.Traces, ex.Host.Transitions, ex.Engine.Trades);
        string? diff = fp == run.Fingerprint ? null : FirstDifference(run, ex);
        IReadOnlyList<object[]> Closed(IReadOnlyList<FlowBar> bars, TimeSpan look) => bars
            .Where(b => b.ClosedAtEvent is { } ce && ce <= last && b.Start >= from - look)
            .Select(b => new object[] { b.Index, Iso(b.Start), b.Open, b.High, b.Low, b.Close, b.Volume, b.BuyVolume, b.SellVolume, b.FirstEvent, b.ClosedAtEvent! }).ToList();
        var closed = new Dictionary<string, IReadOnlyList<object[]>>
        {
            ["5m"] = Closed(ex.Host.Bars5, TimeSpan.FromHours(3)), ["1m"] = Closed(ex.Host.Bars1, TimeSpan.FromMinutes(45)), ["rangeXV"] = Closed(ex.Host.BarsR, TimeSpan.FromMinutes(15)),
        };
        return new MtfReplayWindow(runId, tradeIndex, Iso(from), Iso(to), first, last, diff is null, diff, events, states, closed,
            ex.Host.Traces.Where(t => t.EventIndex >= first && t.EventIndex <= last).ToList(), ex.Host.Transitions.Where(t => t.EventIndex >= first && t.EventIndex <= last).ToList(),
            tr, StateLegend, BarLegend);
    }

    private static string FirstDifference(MtfRunResult run, Executed ex)
    {
        var a = run.Traces.Select(TraceKey).ToList(); var b = ex.Host.Traces.Select(TraceKey).ToList();
        for (int i = 0; i < Math.Min(a.Count, b.Count); i++) if (a[i] != b[i]) return $"Entscheidung #{i}: gespeichert „{a[i]}“ ≠ wiederholt „{b[i]}“";
        if (a.Count != b.Count) return $"Anzahl Entscheidungen {a.Count} ≠ {b.Count}";
        var ta = run.Transitions.Select(x => $"{x.EventIndex}|{x.From}|{x.To}").ToList(); var tb = ex.Host.Transitions.Select(x => $"{x.EventIndex}|{x.From}|{x.To}").ToList();
        for (int i = 0; i < Math.Min(ta.Count, tb.Count); i++) if (ta[i] != tb[i]) return $"Positionswechsel #{i}: {ta[i]} ≠ {tb[i]}";
        return "Trades/Gebühren weichen ab";
    }

    // ------------------------------------------------------------------------------------------------ Ablage

    private async Task<MtfRunResult> Save(MtfRunResult r, CancellationToken ct)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, r.RunId + ".json");
        await File.WriteAllTextAsync(path + ".tmp", JsonSerializer.Serialize(r, Json), ct);
        File.Move(path + ".tmp", path, overwrite: true);
        return r;
    }

    public IReadOnlyList<object> List() => !Directory.Exists(_dir) ? Array.Empty<object>() : Directory.EnumerateFiles(_dir, "mtf-*.json")
        .Select(f => { try { return JsonSerializer.Deserialize<MtfRunResult>(File.ReadAllText(f), Json); } catch (JsonException) { return null; } })
        .Where(r => r is not null).OrderByDescending(r => r!.CreatedUtc, StringComparer.Ordinal)
        .Select(r => (object)new { r!.RunId, r.CreatedUtc, r.Status, r.RunKind, r.Strategy, Trades = r.Trades.Count, r.NetPnL, r.IsDemonstration, Dataset = r.DataCheck?.Label }).ToList();

    public MtfRunResult? Get(string runId)
    {
        if (runId.Any(c => !(char.IsLetterOrDigit(c) || c == '-'))) return null;
        var p = Path.Combine(_dir, runId + ".json");
        return File.Exists(p) ? JsonSerializer.Deserialize<MtfRunResult>(File.ReadAllText(p), Json) : null;
    }

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    private static string N(decimal d) => d.ToString("0.####", CultureInfo.InvariantCulture);
}
