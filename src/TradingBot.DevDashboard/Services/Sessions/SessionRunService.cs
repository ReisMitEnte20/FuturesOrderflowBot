using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using TradingBot.Application.Sessions;
using TradingBot.Application.Strategies.OrderFlow;
using TradingBot.Backtesting;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.DevDashboard.Services.Sessions;

/// <summary>Start eines gemeinsamen Laufs „London + New York“ über einen Datumsbereich.</summary>
public sealed record SessionRunRequest
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string Strategy { get; init; } = "deltaflow";
    public int TicksPerBar { get; init; } = OrderflowTickBacktest.DefaultTicksPerBar;
    public decimal DeltaThreshold { get; init; } = DeltaFlowStrategy.DefaultDeltaThreshold;
    public decimal InitialBalance { get; init; } = 10_000m;
    public decimal? SlippageTicks { get; init; }
    public bool ApplyFees { get; init; } = true;
    /// <summary>Tagesverlustlimit (USD, positiv). Leer = Wert aus dem Risikoprofil config/risk/research-sim.</summary>
    public decimal? MaxDailyLoss { get; init; }
    /// <summary>Close-Out-Vorlauf in Minuten vor Sessionende. Leer = Sessionplan.</summary>
    public int? CloseOutMinutes { get; init; }
}

public sealed record SessionSegmentDto(
    string Id, string Session, string Label, string Day, string WindowStartUtc, string WindowEndUtc, string CloseOutUtc,
    string Collection, string CollectionsRoot, string RecordingId, string Contract, string SliceSha256, long TicksLoaded, long TicksUsed,
    string? FirstTickUtc, decimal? FirstTickPrice, string? LastTickUtc, decimal? LastTickPrice, decimal Volume, decimal? SessionVwap, decimal SessionCvd,
    int DiscardedPartialBarTicksAtStart, int EntrySignalsPassed, int FlatSignalsPassed, int EntrySignalsSuppressedAfterCloseOut,
    string? CloseOutReachedUtc, string PositionAtCloseOut, string? SessionCloseSignalUtc, decimal? SessionCloseSignalPrice, string? SessionCloseFillUtc,
    decimal? SessionCloseFillPrice, string SessionCloseStatus, string PositionAtLastTick, IReadOnlyList<string> Incidents);

/// <summary>Übergang zwischen zwei gelaufenen Sessions: belegt, dass der Sprung weder PnL noch einen Trade erzeugt.</summary>
public sealed record SessionTransitionDto(
    string FromId, string ToId, string FromLabel, string ToLabel, string Kind, string LastTickUtc, decimal? LastTickPrice, string FirstTickUtc,
    decimal? FirstTickPrice, double SkippedSeconds, string SkippedText, double PlannedPauseSeconds, decimal BalanceBefore, decimal BalanceAfter,
    decimal PnlChangeInJump, int TradesClosedInJump, int TradesOpenedInJump, string PositionAtLastTick, IReadOnlyList<string> SkippedDays);

public sealed record SessionTradeDto(
    int Index, string Day, string Session, string SessionLabel, string SegmentId, string Collection, string RecordingId, string Side, int Qty,
    string EntryTimeUtc, decimal EntryPrice, string ExitTimeUtc, decimal ExitPrice, decimal GrossPnL, decimal Fees, decimal NetPnL,
    string ExitReason, bool CrossedPause, decimal BalanceAfter,
    string EntryCause = "", string ExitCause = "", string? EntrySignalUtc = null, string? ExitSignalUtc = null,
    string? EntrySignalId = null, string? EntryOrderId = null, string? EntryFillId = null,
    string? ExitSignalId = null, string? ExitOrderId = null, string? ExitFillId = null);

/// <summary>Abbruch an einer Sessiongrenze: letzter beobachteter Preis, offene Bewertung und realisiertes Ergebnis GETRENNT.</summary>
public sealed record SessionRunStopDto(
    string Reason, string SegmentId, string Session, string SessionLabel, string Day, string SessionEndUtc, string CloseOutUtc,
    string? SessionCloseSignalUtc, string? LastTickUtc, decimal? LastObservedPrice, string OpenPosition, string? OpenSide, int OpenQuantity,
    decimal? OpenAvgPrice, decimal UnrealizedGrossAtLastPrice, decimal RealizedGross, decimal RealizedFees, decimal RealizedNet,
    decimal BalanceRealized, int PendingOrders, string? StoppedBeforeTickUtc, IReadOnlyList<string> NotRunSegments, string Note);

/// <summary>Häufigkeit eines Ein- oder Ausstiegsgrunds je Session.</summary>
public sealed record SessionReasonCountDto(string Session, string Kind, string Cause, int Count);

/// <summary>ID-Prüfung des Journals: jede Order hat genau ein Signal, jeder Trade eigene Ein-/Ausstiegs-Order und -Fill.</summary>
public sealed record JournalIntegrityDto(int OrdersSubmitted, int OrdersLinkedToTrades, int OrdersUnlinked, int DistinctSignalIds,
    int DistinctEntryFillIds, int DistinctExitFillIds, int FillsSharedByTwoTrades, int OverlappingTrades, int TradesWithExitBeforeEntry,
    IReadOnlyList<string> Notes);

public sealed record SessionSummaryDto(string Key, string Label, int Sessions, int Trades, int Winners, int Losers, decimal GrossPnL, decimal Fees, decimal NetPnL,
    int EntrySignals, int SuppressedAfterCloseOut);

public sealed record SessionDaySummaryDto(string Day, int Trades, decimal GrossPnL, decimal Fees, decimal NetPnL, string? DailyLimitHitUtc,
    string? DailyLimitHitSession, int EntriesAfterLimit, int EntrySignalsAfterLimit, IReadOnlyList<string> Notes);

public sealed record IndicatorPolicyDto(string Name, string AtSessionStart, string Reason);

public sealed record SessionRunResult
{
    public string RunId { get; init; } = "";
    public string CreatedUtc { get; init; } = "";
    public string Status { get; init; } = "";
    public string? Error { get; init; }
    public SessionRunRequest Request { get; init; } = new();
    public string StrategyName { get; init; } = "";
    public string Engine { get; init; } = "";
    public string Symbol { get; init; } = "MNQ";
    public decimal TickSize { get; init; }
    public decimal PointValue { get; init; }
    public decimal FeePerSide { get; init; }
    public decimal SlippageTicks { get; init; }
    public decimal MaxDailyLoss { get; init; }
    public int CloseOutMinutes { get; init; }
    public bool InstrumentIsExample { get; init; }
    public bool FeeIsExample { get; init; }
    public SessionPlanDto? Plan { get; init; }
    public IReadOnlyList<string> SkippedAtLoad { get; init; } = Array.Empty<string>();
    public IReadOnlyList<SessionSegmentDto> Segments { get; init; } = Array.Empty<SessionSegmentDto>();
    public IReadOnlyList<SessionTransitionDto> Transitions { get; init; } = Array.Empty<SessionTransitionDto>();
    public IReadOnlyList<SessionTradeDto> Trades { get; init; } = Array.Empty<SessionTradeDto>();
    public IReadOnlyList<SessionSummaryDto> BySession { get; init; } = Array.Empty<SessionSummaryDto>();
    public SessionSummaryDto? Total { get; init; }
    public IReadOnlyList<SessionDaySummaryDto> ByDay { get; init; } = Array.Empty<SessionDaySummaryDto>();
    public decimal StartBalance { get; init; }
    public decimal EndBalance { get; init; }
    public decimal MaxDrawdown { get; init; }
    public long TicksProcessed { get; init; }
    public int SignalsGenerated { get; init; }
    public int OrdersSubmitted { get; init; }
    public int OrdersRejectedByRisk { get; init; }
    public int OrdersUnfilledAtEnd { get; init; }
    public string FinalPosition { get; init; } = "Flat";
    /// <summary>Gesetzt, wenn der Lauf an einer Sessiongrenze bzw. am Datenende mit offener Position/Order abgebrochen wurde.</summary>
    public SessionRunStopDto? Stop { get; init; }
    /// <summary>Kontraktprüfung zwischen den Aufnahmen („Kontraktidentität ungeprüft“, wenn die Kennung unbekannt ist).</summary>
    public string ContractCheck { get; init; } = "";
    public IReadOnlyList<SessionReasonCountDto> ReasonCounts { get; init; } = Array.Empty<SessionReasonCountDto>();
    public JournalIntegrityDto? Integrity { get; init; }
    public IReadOnlyList<IndicatorPolicyDto> IndicatorPolicy { get; init; } = Array.Empty<IndicatorPolicyDto>();
    public IReadOnlyList<string> Rules { get; init; } = Array.Empty<string>();
    public double ElapsedSeconds { get; init; }
}

public sealed record SessionRunIndexEntry(string RunId, string CreatedUtc, string Status, string From, string To, int Days, int Trades, decimal NetPnL, string StrategyName);

/// <summary>
/// Gemeinsamer Lauf „London + New York“: EINE Strategieinstanz, EIN Konto, EIN Lauf der bestehenden tick-getriebenen
/// <see cref="BacktestEngine"/> über alle vollständigen Handelstage des Bereichs. Ticks werden tageweise aus beiden Datenordnern
/// (geprüfter Tick-Replay-Cache) gestreamt; die Pause wird nicht abgewartet – der nächste Tick kommt sofort mit seinem originalen
/// Marktzeitstempel. Das <see cref="SessionGateStrategy"/> setzt Sessionzustand zurück und wendet die Close-Out-Regel an.
/// Keine Orders an Broker, keine Live-Verbindung, keine erfundenen Ticks.
/// </summary>
public sealed class SessionRunService
{
    private readonly SessionPlanService _plan;
    private readonly BacktestApiService _bt;
    private readonly string _dir;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly SemaphoreSlim _one = new(1, 1);

    public SessionRunService(SessionPlanService plan, BacktestApiService bt, string runsDir)
    {
        _plan = plan;
        _bt = bt;
        _dir = runsDir;
    }

    public static IReadOnlyList<IndicatorPolicyDto> IndicatorPolicy { get; } = new[]
    {
        new IndicatorPolicyDto("N-Tick-Orderflow-Bar (laufende Teilbar)", "zurückgesetzt", "Eine Bar spannt nie über die Pause; angefangene Bar der Vorsession wird verworfen, kein Signal aus ihr."),
        new IndicatorPolicyDto("Bar-Delta (DeltaFlow-Signal)", "je Bar neu", "Kein Übertrag – jede Bar wird aus ihren eigenen Ticks gemessen."),
        new IndicatorPolicyDto("Session-CVD (kumulatives Delta)", "zurückgesetzt", "Startet je Session bei 0 (auch im Chart)."),
        new IndicatorPolicyDto("Session-VWAP", "zurückgesetzt", "Startet je Session neu (nur Ticks dieser Session)."),
        new IndicatorPolicyDto("Kontostand, realisiertes PnL, Gebühren, Drawdown", "läuft weiter", "Ein Konto über alle Sessions und Tage."),
        new IndicatorPolicyDto("Tagesrisiko (Tagesverlust, Trades/Tag, Verlustserie)", "läuft über den Handelstag weiter", "Gilt je UTC-Kalendertag für London und New York gemeinsam; neuer Tag = neuer Zustand."),
        new IndicatorPolicyDto("Position und offene Orders", "müssen glatt sein", "Close-Out-Regel vor Sessionende. Ist vor dem ersten Tick der nächsten Session noch eine Position oder Order offen, wird der Lauf gestoppt (unvollständig) – kein Übertrag über die Pause."),
        new IndicatorPolicyDto("Zeitfenster-Indikatoren über die Pause (z. B. gleitende Mittel über Zeit, ATR)", "nicht gültig", "Werden von DeltaFlow nicht verwendet. Ein Fenster, das die unbeobachtete Pause überspannt, gilt nicht als gültig – solche Indikatoren müssten nach Sessionstart neu aufgewärmt werden."),
        new IndicatorPolicyDto("SinglePositionGate", "kein eigener Zustand", "Liest ausschließlich den tatsächlichen Engine-Positionszustand."),
    };

    public async Task<SessionRunResult> RunAsync(SessionRunRequest req, CancellationToken ct = default)
    {
        if (!await _one.WaitAsync(0, ct)) throw new InvalidOperationException("Es läuft bereits ein London+New-York-Lauf.");
        try { return await RunCoreAsync(req, ct); }
        finally { _one.Release(); }
    }

    private async Task<SessionRunResult> RunCoreAsync(SessionRunRequest req, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var from = DateOnly.ParseExact(req.From, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var to = DateOnly.ParseExact(req.To, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (req.TicksPerBar <= 0) throw new ArgumentException("TicksPerBar muss > 0 sein.");
        if (req.MaxDailyLoss is <= 0) throw new ArgumentException("Tagesverlustlimit muss positiv sein.");
        var plan = _plan.Plan(from, to, req.CloseOutMinutes);
        var (schedFile, _) = _plan.LoadSchedule();
        int closeOut = req.CloseOutMinutes ?? schedFile.CloseOutMinutesBeforeEnd;
        string runId = $"lnny-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{from:yyyyMMdd}-{to:yyyyMMdd}";

        // Profile + Kandidat exakt wie der bestehende Tick-Orderflow-Pfad (config/, gegateter DeltaFlow-Kandidat).
        var runReq = new BacktestRunRequest
        {
            DataSourceId = "atas-sessions:london-newyork", Symbol = "MNQ", InitialBalance = req.InitialBalance, SlippageTicks = req.SlippageTicks,
            ApplyFees = req.ApplyFees, Strategy = req.Strategy,
            Params = new Dictionary<string, string>
            {
                [OrderflowTickBacktest.TicksPerBarKey] = req.TicksPerBar.ToString(CultureInfo.InvariantCulture),
                [DeltaFlowStrategy.ThresholdParameterKey] = req.DeltaThreshold.ToString(CultureInfo.InvariantCulture)
            }
        };
        var (instrument, fee, instEx, feeEx) = await _bt.ResolveProfilesAsync(runReq, ct);
        var orq = _bt.Ticks.BuildRequest(runReq, Array.Empty<MarketTick>(), instrument, fee, "London + New York", "", strategy: req.Strategy);
        var risk = req.MaxDailyLoss is decimal mdl ? orq.Risk with { MaxDailyLoss = mdl } : orq.Risk;
        decimal feePerSide = orq.Fee.CommissionPerSide + orq.Fee.ExchangeFeePerSide + orq.Fee.ClearingFeePerSide + orq.Fee.RoutingFeePerSide
                             + orq.Fee.NfaFeePerSide + orq.Fee.OtherFeePerSide;

        var segments = BuildSegments(plan, closeOut);
        var baseResult = new SessionRunResult
        {
            RunId = runId, CreatedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), Request = req, Plan = plan,
            Symbol = instrument.Symbol, TickSize = instrument.TickSize, PointValue = instrument.PointValue, FeePerSide = feePerSide,
            SlippageTicks = orq.SlippageTicksOverride ?? orq.Fee.EstimatedSlippageTicks, MaxDailyLoss = risk.MaxDailyLoss, CloseOutMinutes = closeOut,
            InstrumentIsExample = instEx, FeeIsExample = feeEx, StartBalance = req.InitialBalance, EndBalance = req.InitialBalance,
            IndicatorPolicy = IndicatorPolicy, Rules = Rules(closeOut),
            Engine = "Bestehende tick-getriebene BacktestEngine (Strategy → RiskManager → OrderManager → PositionManager, simulierter Fill-Adapter)"
        };
        if (segments.Count == 0)
            return await Save(baseResult with { Status = "NoData", Error = "Kein vollständiger Handelstag (London + New York) im Zeitraum – nichts gelaufen.", ElapsedSeconds = sw.Elapsed.TotalSeconds }, ct);

        var cand = orq.Candidates.Single();
        IStrategy inner = cand.Build(cand.BaseConfig);
        if (inner is IOrderFlowStrategy) inner = new OrderFlowBarAggregatorStrategy(inner, ticksPerBar: req.TicksPerBar);
        var gate = new SessionGateStrategy(inner, segments.Select(s => s.Segment).ToList());
        gate.Initialize(new StrategyExecutionContext { Symbol = instrument.Symbol, Instrument = instrument, Config = cand.BaseConfig });

        var feed = new SessionTickFeed(segments, _plan.Replay);
        var engineResult = await new BacktestEngine().RunAsync(new BacktestRequest
        {
            MarketData = feed, Symbol = instrument.Symbol, Strategy = gate, Instrument = instrument, Fee = orq.Fee, Broker = orq.Broker,
            Risk = risk, Account = orq.Account,
            Config = new BacktestConfiguration { SlippageTicksOverride = orq.SlippageTicksOverride },
            // Sessiongrenze: offene Position/Order vor dem ersten Tick der nächsten Session → Lauf stoppt VOR diesem Tick.
            TickGuard = c => gate.BeforeTick(c.NextTick.Timestamp, c.PositionQuantity, c.PendingOrders)
        }, ct);

        var result = Evaluate(baseResult with { StrategyName = cand.Name }, engineResult, gate, feed, risk.MaxDailyLoss, req.InitialBalance,
            segments.Select(x => x.Segment).ToList(), req.DeltaThreshold);
        return await Save(result with { ElapsedSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2) }, ct);
    }

    private static IReadOnlyList<string> Rules(int closeOut) => new[]
    {
        "Ein Lauf der bestehenden Tick-Engine mit EINER Strategieinstanz und EINEM Konto über alle Sessions und Tage.",
        "Die Pause wird nicht abgewartet: nach dem letzten London-Tick folgt sofort der erste New-York-Tick mit seinem originalen Marktzeitstempel; die Engine-Uhr springt auf diese Marktzeit (zeitbasierte Abläufe sehen die tatsächlich vergangene Marktzeit).",
        $"Session-Close-Regel (vorab festgelegt): ab Sessionende − {closeOut} min keine neuen Einstiege; eine offene Position erhält ein Flat-Signal, das die Engine mit dem vorhandenen Fill-/Kostenmodell am nächsten Tick füllt. Kein rückwirkender Ausstieg auf dem letzten Tick.",
        "Kommt nach der Glattstellung kein ausführbarer Tick mehr vor dem Sessionende, stoppt der Lauf als „unvollständig – Position vor Sessionende nicht glattstellbar“: kein rückwirkender Fill auf dem letzten Tick, keine Glattstellung am ersten Tick nach der Pause, keine offene Order in der nächsten Session. Letzter beobachteter Preis, offene Bewertung und realisiertes Ergebnis werden getrennt gezeigt.",
        "In der Pause entstehen keine Kurse, Ticks oder Fills.",
        "Tagesverlustlimit: der RiskManager (fail-closed) prüft je UTC-Kalendertag – hat London das Limit erreicht, sind in New York an diesem Tag keine neuen Einstiege möglich."
    };

    // ---------------------------------------------------------------------------------------------------------------

    internal sealed record SegmentSource(SessionSegment Segment, PlannedSessionDto Planned);

    private static List<SegmentSource> BuildSegments(SessionPlanDto plan, int closeOut)
    {
        var list = new List<SegmentSource>();
        foreach (var d in plan.Days.Where(x => x.Status == "complete"))
            foreach (var s in d.Sessions.OrderBy(x => x.EffectiveStartUtc, StringComparer.Ordinal))
            {
                var start = DateTimeOffset.Parse(s.EffectiveStartUtc!, CultureInfo.InvariantCulture);
                var end = DateTimeOffset.Parse(s.WindowEndUtc!, CultureInfo.InvariantCulture);
                list.Add(new SegmentSource(new SessionSegment(s.Id!, s.Key, s.Label, DateOnly.ParseExact(d.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    start, end, end - TimeSpan.FromMinutes(closeOut)), s));
            }
        return list;
    }

    /// <summary>Streamt die Ticks tageweise (alle Sessions eines Tags werden VOR dem ersten Tick geladen und geprüft).
    /// Ein Tag, dessen Sessions beim Laden die Prüfung nicht bestehen (z. B. Kontrakt-Wechsel), wird komplett ausgelassen.</summary>
    internal sealed class SessionTickFeed : IMarketDataProvider
    {
        private readonly IReadOnlyList<SegmentSource> _segments;
        private readonly TickReplayService _replay;
        public Dictionary<string, (TickReplaySessionMeta Meta, long Loaded, long Used)> Loaded { get; } = new();
        public List<string> Skipped { get; } = new();

        public SessionTickFeed(IReadOnlyList<SegmentSource> segments, TickReplayService replay)
        {
            _segments = segments;
            _replay = replay;
        }

        public bool IsConnected { get; private set; }
        public Task ConnectAsync(CancellationToken cancellationToken = default) { IsConnected = true; return Task.CompletedTask; }
        public Task DisconnectAsync(CancellationToken cancellationToken = default) { IsConnected = false; return Task.CompletedTask; }

        public async IAsyncEnumerable<MarketTick> SubscribeTicksAsync(string symbol, [EnumeratorCancellation] CancellationToken ct = default)
        {
            DateTimeOffset last = DateTimeOffset.MinValue;
            foreach (var day in _segments.GroupBy(s => s.Segment.Day))
            {
                var loaded = new List<(SegmentSource Src, TickReplaySessionMeta Meta, List<MarketTick> Ticks)>();
                string? skip = null;
                foreach (var src in day)
                {
                    var (meta, ticks) = await _replay.LoadMarketTicksAsync(src.Segment.Id, ct);
                    if (meta.RecordingId != src.Planned.RecordingId) skip = $"{src.Segment.Label}: Aufnahme beim Laden {meta.RecordingId} ≠ Plan {src.Planned.RecordingId}.";
                    loaded.Add((src, meta, ticks));
                }
                var contracts = loaded.Select(l => l.Meta.Contract).Distinct(StringComparer.Ordinal).ToList();
                if (contracts.Count > 1) skip ??= $"Kontrakt/Serie wechselt zwischen den Sessions ({string.Join(" / ", contracts)}) – kein stiller Roll.";
                if (skip is not null)
                {
                    Skipped.Add($"{day.Key:yyyy-MM-dd}: ausgelassen – {skip}");
                    continue;
                }
                foreach (var (src, meta, ticks) in loaded)
                {
                    long used = 0;
                    foreach (var t in ticks)
                    {
                        if (t.Timestamp < src.Segment.StartUtc || t.Timestamp >= src.Segment.EndUtc) continue; // Überlappung: nicht doppelt
                        if (t.Timestamp < last) throw new InvalidDataException($"Nicht chronologische Ticks an der Grenze zu {src.Segment.Id}.");
                        last = t.Timestamp;
                        used++;
                        yield return t;
                    }
                    Loaded[src.Segment.Id] = (meta, ticks.Count, used);
                }
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------

    internal static SessionRunResult Evaluate(SessionRunResult r, BacktestResult res, SessionGateStrategy gate, SessionTickFeed feed,
        decimal maxDailyLoss, decimal start, IReadOnlyList<SessionSegment> plannedSegments, decimal deltaThreshold)
    {
        var obs = gate.Observations.Where(o => o.Ticks > 0).ToList();
        // Engine-Trades in Abschlussreihenfolge, mit den Order-/Fill-IDs (gleicher Index) und dem auslösenden Signal je Order.
        var linked = res.Trades.Select((t, i) => (T: t, L: i < res.TradeLinks.Count ? res.TradeLinks[i] : null))
            .OrderBy(x => x.T.ExitTime).ThenBy(x => x.T.EntryTime).ToList();
        var trades = linked.Select(x => x.T).ToList();
        var orderById = res.Orders.GroupBy(o => o.OrderId).ToDictionary(g => g.Key, g => g.First());
        string thr = deltaThreshold.ToString("0.##", CultureInfo.InvariantCulture);
        string EntryCause(BacktestOrderRecord? o) => o is null ? "?" : o.SignalDirection switch
        {
            SignalDirection.Long => o.SignalStrategy.StartsWith("DeltaFlow", StringComparison.Ordinal) ? $"Long-Signal: Bar-Delta > +{thr}" : "Long-Signal",
            SignalDirection.Short => o.SignalStrategy.StartsWith("DeltaFlow", StringComparison.Ordinal) ? $"Short-Signal: Bar-Delta < −{thr}" : "Short-Signal",
            _ => o.SignalDirection.ToString()
        };
        string ExitCause(BacktestOrderRecord? o) => o is null ? "?"
            : o.SignalStrategy == SessionGateStrategy.SessionCloseStrategyName ? "Session-Close-Regel (Close-Out-Grenze)"
            : o.SignalDirection == SignalDirection.Flat
                ? (o.SignalStrategy.StartsWith("DeltaFlow", StringComparison.Ordinal) ? $"Flat-Signal: |Bar-Delta| ≤ {thr}" : "Flat-Signal")
                : $"Gegensignal {o.SignalDirection}: schließt nur (kein Reverse)";
        SessionSegmentObservation? SegOf(DateTimeOffset t) => obs.FirstOrDefault(o => t >= o.Segment.StartUtc && t < o.Segment.EndUtc);

        // --- Trades mit Session, Ausstiegsgrund, Kontostand ---
        decimal bal = start, peak = start, maxDd = 0m;
        var tradeDtos = new List<SessionTradeDto>();
        var closeFill = new Dictionary<string, BacktestTrade>();
        int idx = 0;
        foreach (var (t, link) in linked)
        {
            BacktestOrderRecord? eo = link is not null && orderById.TryGetValue(link.EntryOrderId, out var a1) ? a1 : null;
            BacktestOrderRecord? xo = link is not null && orderById.TryGetValue(link.ExitOrderId, out var a2) ? a2 : null;
            var es = SegOf(t.EntryTime); var xs = SegOf(t.ExitTime);
            bool crossed = es is null || xs is null || es.Segment.Id != xs.Segment.Id;
            string reason = "Signal";
            if (es is not null && es.SessionCloseSignalUtc is { } sc && t.EntryTime <= sc && t.ExitTime > sc && !crossed)
            {
                reason = "Session-Close";
                closeFill.TryAdd(es.Segment.Id, t);
            }
            else if (crossed) reason = "über die Pause getragen (Vorfall)";
            bal += t.NetPnL;
            peak = Math.Max(peak, bal);
            maxDd = Math.Max(maxDd, peak - bal);
            var seg = es ?? xs;
            tradeDtos.Add(new SessionTradeDto(idx++, seg?.Segment.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "", seg?.Segment.SessionKey ?? "?",
                seg?.Segment.Label ?? "?", seg?.Segment.Id ?? "", feed.Loaded.TryGetValue(seg?.Segment.Id ?? "", out var lm) ? lm.Meta.Collection : "",
                lm.Meta?.RecordingId ?? "", t.Side.ToString(), t.Quantity, Iso(t.EntryTime), t.EntryPrice, Iso(t.ExitTime), t.ExitPrice,
                t.GrossPnL, t.Fees, t.NetPnL, reason, crossed, bal,
                EntryCause(eo), ExitCause(xo), eo is null ? null : Iso(eo.SignalTime), xo is null ? null : Iso(xo.SignalTime),
                eo?.SignalId.ToString(), link?.EntryOrderId.ToString(), link?.EntryFillId.ToString(),
                xo?.SignalId.ToString(), link?.ExitOrderId.ToString(), link?.ExitFillId.ToString()));
        }

        // --- Segmente ---
        var segDtos = new List<SessionSegmentDto>();
        foreach (var o in obs)
        {
            feed.Loaded.TryGetValue(o.Segment.Id, out var l);
            string closeStatus;
            closeFill.TryGetValue(o.Segment.Id, out var cf);
            if (o.SessionCloseSignalUtc is null)
                closeStatus = o.CloseOutReachedUtc is null
                    ? (o.PositionAtLastTick == "Flat" ? "keine Close-Out-Grenze erreicht (kein Tick danach); flach" : "Close-Out-Grenze ohne Tick – kein zulässiger Fill vor der Grenze möglich")
                    : (o.PositionAtCloseOut == "Flat" ? "an der Grenze bereits flach – keine Glattstellung nötig" : "Position an der Grenze offen, aber kein Flat nötig");
            else if (cf is not null) closeStatus = FormattableString.Invariant($"glattgestellt {Iso(cf.ExitTime)} @ {cf.ExitPrice} (vor Sessionende)");
            else closeStatus = "Flat-Signal ausgegeben, aber KEIN Fill vor Sessionende – ausgewiesen";
            segDtos.Add(new SessionSegmentDto(o.Segment.Id, o.Segment.SessionKey, o.Segment.Label, o.Segment.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Iso(o.Segment.StartUtc), Iso(o.Segment.EndUtc), Iso(o.Segment.CloseOutUtc), l.Meta?.Collection ?? "", RootLabel(r.Plan, o.Segment.Id),
                l.Meta?.RecordingId ?? "", l.Meta?.Contract ?? "", l.Meta?.SliceSha256 ?? "", l.Loaded, l.Used,
                IsoN(o.FirstTickUtc), o.FirstTickPrice, IsoN(o.LastTickUtc), o.LastTickPrice, o.Volume, o.SessionVwap, o.SessionCvd,
                o.DiscardedPartialBarTicksAtStart, o.EntrySignalsPassed, o.FlatSignalsPassed, o.EntrySignalsSuppressedAfterCloseOut,
                IsoN(o.CloseOutReachedUtc), o.PositionAtCloseOut, IsoN(o.SessionCloseSignalUtc), o.SessionCloseSignalPrice,
                cf is null ? null : Iso(cf.ExitTime), cf?.ExitPrice, closeStatus, o.PositionAtLastTick, o.Incidents.ToList()));
        }

        // --- Übergänge: Kontostand vor/nach dem Sprung aus den Engine-Trades ---
        decimal BalanceAt(DateTimeOffset t) => start + trades.Where(x => x.ExitTime <= t).Sum(x => x.NetPnL);
        var transitions = new List<SessionTransitionDto>();
        var planned = r.Plan?.Days ?? Array.Empty<PlannedDayDto>();
        for (int i = 1; i < obs.Count; i++)
        {
            var a = obs[i - 1]; var b = obs[i];
            var lt = a.LastTickUtc!.Value; var ft = b.FirstTickUtc!.Value;
            decimal before = BalanceAt(lt), after = BalanceAt(ft);
            var skipped = planned.Where(d => string.CompareOrdinal(d.Day, a.Segment.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) > 0
                                             && string.CompareOrdinal(d.Day, b.Segment.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) < 0)
                .Select(d => $"{d.Day}: {(d.Status == "complete" ? "beim Laden ausgelassen" : d.Status)} – {string.Join(" ", d.Reasons)}").ToList();
            skipped.AddRange(feed.Skipped.Where(s => string.CompareOrdinal(s[..10], a.Segment.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) > 0
                                                     && string.CompareOrdinal(s[..10], b.Segment.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) < 0));
            transitions.Add(new SessionTransitionDto(a.Segment.Id, b.Segment.Id, a.Segment.Label, b.Segment.Label,
                a.Segment.Day == b.Segment.Day ? "Session → Session (gleicher Handelstag)" : "Tageswechsel",
                Iso(lt), a.LastTickPrice, Iso(ft), b.FirstTickPrice, (ft - lt).TotalSeconds, Human(ft - lt),
                (b.Segment.StartUtc - a.Segment.EndUtc).TotalSeconds, before, after, after - before,
                trades.Count(x => x.ExitTime > lt && x.ExitTime <= ft), trades.Count(x => x.EntryTime > lt && x.EntryTime <= ft),
                a.PositionAtLastTick, skipped.Distinct().ToList()));
        }

        // --- Zusammenfassungen ---
        SessionSummaryDto Sum(string key, string label, IEnumerable<SessionTradeDto> ts, IEnumerable<SessionSegmentObservation> os)
        {
            var l = ts.ToList(); var o = os.ToList();
            return new SessionSummaryDto(key, label, o.Count, l.Count, l.Count(x => x.NetPnL > 0), l.Count(x => x.NetPnL < 0),
                l.Sum(x => x.GrossPnL), l.Sum(x => x.Fees), l.Sum(x => x.NetPnL), o.Sum(x => x.EntrySignalsPassed), o.Sum(x => x.EntrySignalsSuppressedAfterCloseOut));
        }
        var keys = obs.Select(o => (o.Segment.SessionKey, o.Segment.Label)).Distinct().ToList();
        var bySession = keys.Select(k => Sum(k.SessionKey, k.Label, tradeDtos.Where(t => t.Session == k.SessionKey), obs.Where(o => o.Segment.SessionKey == k.SessionKey))).ToList();
        // Sessions ohne Lauf (z. B. alle Tage ausgelassen) trotzdem mit 0 zeigen
        var total = Sum("Total", "Gesamt", tradeDtos, obs);

        var byDay = new List<SessionDaySummaryDto>();
        foreach (var g in obs.GroupBy(o => o.Segment.Day))
        {
            string dayKey = g.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var dts = tradeDtos.Where(t => t.Day == dayKey).ToList();
            // Tageslimit: kumuliertes Netto der an diesem UTC-Tag geschlossenen Trades (wie der Backtest-Tageszustand).
            decimal cum = 0; DateTimeOffset? hit = null;
            foreach (var t in trades.Where(t => DateOnly.FromDateTime(t.ExitTime.UtcDateTime) == g.Key).OrderBy(t => t.ExitTime))
            {
                cum += t.NetPnL;
                if (hit is null && cum <= -maxDailyLoss) hit = t.ExitTime;
            }
            var notes = new List<string>();
            int entriesAfter = 0, signalsAfter = 0;
            string? hitSession = null;
            if (hit is { } h)
            {
                hitSession = SegOf(h)?.Segment.Label;
                entriesAfter = trades.Count(t => t.EntryTime > h && DateOnly.FromDateTime(t.EntryTime.UtcDateTime) == g.Key);
                signalsAfter = g.Where(o => o.Segment.StartUtc > h).Sum(o => o.EntrySignalsPassed);
                notes.Add(FormattableString.Invariant($"Tagesverlustlimit {maxDailyLoss:0.##} erreicht um {Iso(h)} ({hitSession}). Danach {entriesAfter} neue Einstiege; {signalsAfter} Einstiegssignale späterer Sessions dieses Tages gingen an den RiskManager (abgelehnt)."));
            }
            byDay.Add(new SessionDaySummaryDto(dayKey, dts.Count, dts.Sum(x => x.GrossPnL), dts.Sum(x => x.Fees), dts.Sum(x => x.NetPnL),
                IsoN(hit), hitSession, entriesAfter, signalsAfter, notes));
        }

        // --- Abbruch an der Sessiongrenze bzw. offene Position/Order am Datenende ---
        SessionRunStopDto? stop = null;
        bool openAtEnd = res.FinalPositionSide != PositionSide.Flat && res.FinalPositionQuantity != 0;
        if (res.Status == BacktestRunStatus.Stopped || (res.Status == BacktestRunStatus.Completed && (openAtEnd || res.OrdersUnfilledAtEnd > 0)))
        {
            var bs = gate.BoundaryStop;
            var lastObs = obs.LastOrDefault();
            var seg = bs?.Segment ?? lastObs?.Segment;
            string reason = bs?.Reason ?? (openAtEnd ? SessionGateStrategy.NotFlattenableReason : SessionGateStrategy.PendingOrderReason);
            var notRun = plannedSegments.Where(p => seg is null || p.StartUtc >= seg.EndUtc).Select(p => p.Id).ToList();
            decimal realizedNet = trades.Sum(t => t.NetPnL);
            stop = new SessionRunStopDto(reason, seg?.Id ?? "", seg?.SessionKey ?? "", seg?.Label ?? "",
                seg?.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "", seg is null ? "" : Iso(seg.EndUtc), seg is null ? "" : Iso(seg.CloseOutUtc),
                IsoN(bs?.SessionCloseSignalUtc ?? lastObs?.SessionCloseSignalUtc), IsoN(res.LastTickUtc), res.LastTickPrice,
                openAtEnd ? FormattableString.Invariant($"{res.FinalPositionSide} {res.FinalPositionQuantity} @ {res.FinalPositionAvgPrice}") : "Flat",
                openAtEnd ? res.FinalPositionSide.ToString() : null, res.FinalPositionQuantity, openAtEnd ? res.FinalPositionAvgPrice : null,
                res.FinalPositionUnrealizedAtLastTick, trades.Sum(t => t.GrossPnL), trades.Sum(t => t.Fees), realizedNet, start + realizedNet,
                res.OrdersUnfilledAtEnd, IsoN(res.StoppedBeforeTickUtc), notRun,
                "Offene Bewertung = Position zum letzten beobachteten Preis (brutto, ohne Ausstiegsgebühren/Slippage, NICHT gebucht). " +
                "Realisiert = nur geschlossene Trades. Kein Fill wurde erfunden; die nächste Session wurde nicht gestartet.");
        }

        // --- Kontraktidentität: zwei unbekannte Kennungen sind kein Nachweis ---
        var contracts = feed.Loaded.Values.Select(v => v.Meta.Contract ?? "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        static bool Unknown(string c) => string.IsNullOrWhiteSpace(c) || c.Equals("unknown", StringComparison.OrdinalIgnoreCase);
        string contractCheck = contracts.Count == 0 ? "keine Session geladen"
            : contracts.All(Unknown) ? "Kontraktidentität ungeprüft – Kennung in den Aufnahmen „unknown“; zwei unbekannte Kennungen sind kein Nachweis."
            : contracts.Any(Unknown) ? "Kontraktidentität ungeprüft – mindestens eine Aufnahme ohne Kennung."
            : contracts.Count == 1 ? $"geprüft: einheitlich {contracts[0]}" : $"uneinheitlich: {string.Join(" / ", contracts)}";

        // --- Ein-/Ausstiegsgründe je Session ---
        var reasonCounts = tradeDtos.SelectMany(t => new[] { (t.Session, Kind: "Einstieg", Cause: t.EntryCause), (t.Session, Kind: "Ausstieg", Cause: t.ExitCause) })
            .GroupBy(x => x).Select(g => new SessionReasonCountDto(g.Key.Session, g.Key.Kind, g.Key.Cause, g.Count()))
            .OrderBy(x => x.Session, StringComparer.Ordinal).ThenBy(x => x.Kind, StringComparer.Ordinal).ThenByDescending(x => x.Count).ToList();

        // --- ID-Prüfung: getrennte, geschlossene Positionen? ---
        var links = linked.Where(x => x.L is not null).Select(x => x.L!).ToList();
        var linkedOrders = links.SelectMany(l => new[] { l.EntryOrderId, l.ExitOrderId }).ToHashSet();
        var entryFills = links.Select(l => l.EntryFillId).ToList();
        var exitFills = links.Select(l => l.ExitFillId).ToList();
        int shared = entryFills.Intersect(exitFills).Count() + (entryFills.Count - entryFills.Distinct().Count()) + (exitFills.Count - exitFills.Distinct().Count());
        var byEntry = trades.OrderBy(t => t.EntryTime).ToList();
        int overlapping = byEntry.Zip(byEntry.Skip(1)).Count(p => p.Second.EntryTime < p.First.ExitTime);
        var integrityNotes = new List<string>();
        if (links.Count != trades.Count) integrityNotes.Add($"{trades.Count - links.Count} Trades ohne ID-Verknüpfung.");
        var unlinked = res.Orders.Where(o => !linkedOrders.Contains(o.OrderId)).ToList();
        if (unlinked.Count > 0)
            integrityNotes.Add(FormattableString.Invariant($"Nicht verknüpfte Orders: {string.Join(", ", unlinked.GroupBy(o => o.Lifecycle).Select(g => $"{g.Key} {g.Count()}"))} (z. B. offen am Ende)."));
        var integrity = new JournalIntegrityDto(res.Orders.Count, res.Orders.Count(o => linkedOrders.Contains(o.OrderId)), unlinked.Count,
            res.Orders.Select(o => o.SignalId).Distinct().Count(), entryFills.Distinct().Count(), exitFills.Distinct().Count(), shared, overlapping,
            trades.Count(t => t.ExitTime < t.EntryTime), integrityNotes);

        string status = stop is not null ? "Incomplete" : res.Status == BacktestRunStatus.Completed ? "Completed" : res.Status.ToString();
        return r with
        {
            Status = status, Error = stop is not null ? stop.Reason : res.Status == BacktestRunStatus.Completed ? null : res.Message,
            Stop = stop, ContractCheck = contractCheck, ReasonCounts = reasonCounts, Integrity = integrity,
            SkippedAtLoad = feed.Skipped.ToList(), Segments = segDtos, Transitions = transitions, Trades = tradeDtos, BySession = bySession, Total = total,
            ByDay = byDay, EndBalance = start + trades.Sum(t => t.NetPnL), MaxDrawdown = maxDd, TicksProcessed = res.TicksProcessed,
            SignalsGenerated = res.SignalsGenerated, OrdersSubmitted = res.OrdersSubmitted, OrdersRejectedByRisk = res.OrdersRejectedByRisk,
            OrdersUnfilledAtEnd = res.OrdersUnfilledAtEnd,
            FinalPosition = res.FinalPositionSide == PositionSide.Flat ? "Flat" : FormattableString.Invariant($"{res.FinalPositionSide} {res.FinalPositionQuantity} @ {res.FinalPositionAvgPrice}")
        };
    }

    private static string RootLabel(SessionPlanDto? plan, string id) =>
        plan?.Days.SelectMany(d => d.Sessions).FirstOrDefault(s => s.Id == id)?.CollectionsRoot is { } root ? Path.GetFileName(Path.GetDirectoryName(root.TrimEnd('\\', '/')) ?? root) ?? "" : "";

    internal static string Human(TimeSpan t)
    {
        if (t < TimeSpan.Zero) return "−" + Human(-t);
        int h = (int)t.TotalHours, m = t.Minutes;
        return h > 0 ? $"{h} h {m} min" : t.TotalMinutes >= 1 ? $"{m} min {t.Seconds} s" : $"{t.TotalSeconds:0.###} s";
    }

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    private static string? IsoN(DateTimeOffset? t) => t is { } v ? Iso(v) : null;

    // ---- Ablage (artifacts/, gitignored – Läufe bleiben lokal) -------------------------------------------------------

    private async Task<SessionRunResult> Save(SessionRunResult r, CancellationToken ct)
    {
        Directory.CreateDirectory(_dir);
        string path = Path.Combine(_dir, r.RunId + ".json");
        string tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(r, Json), ct);
        File.Move(tmp, path, overwrite: true);
        return r;
    }

    public IReadOnlyList<SessionRunIndexEntry> List()
    {
        if (!Directory.Exists(_dir)) return Array.Empty<SessionRunIndexEntry>();
        var list = new List<SessionRunIndexEntry>();
        foreach (var f in Directory.EnumerateFiles(_dir, "lnny-*.json"))
        {
            try
            {
                var r = JsonSerializer.Deserialize<SessionRunResult>(File.ReadAllText(f), Json);
                if (r is null) continue;
                list.Add(new SessionRunIndexEntry(r.RunId, r.CreatedUtc, r.Status, r.Request.From, r.Request.To,
                    r.Segments.Select(s => s.Day).Distinct().Count(), r.Trades.Count, r.Total?.NetPnL ?? 0m, r.StrategyName));
            }
            catch (JsonException) { /* unlesbare Datei überspringen */ }
        }
        return list.OrderByDescending(x => x.CreatedUtc, StringComparer.Ordinal).ToList();
    }

    public SessionRunResult? Get(string runId)
    {
        if (runId.Any(c => !(char.IsLetterOrDigit(c) || c == '-'))) return null;
        string path = Path.Combine(_dir, runId + ".json");
        return File.Exists(path) ? JsonSerializer.Deserialize<SessionRunResult>(File.ReadAllText(path), Json) : null;
    }
}
