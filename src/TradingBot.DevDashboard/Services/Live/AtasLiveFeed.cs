using System.Globalization;
using System.Text.Json;

namespace TradingBot.DevDashboard.Services.Live;

// =====================================================================================================================
// ATAS-Live-Feed (nur lesend): Zustand aus den NDJSON-Nachrichten der lokalen Bridge (ATAS-Indikator „OF Live Bridge“).
// ATAS → Bridge (TCP 127.0.0.1) → dieser Zustand → /api/marketdata/live → Dashboard. Keine Orders, keine Broker-API.
//
// Herkunft je Wert (nie vermischt):
//   live_atas    – von ATAS live empfangen (Trade, Bid/Ask, DOM-Update, kumulierter Trade)
//   atas_chart   – direkt aus der laufenden ATAS-Chartinstanz gelesen (Kerze via GetCandle: OHLC, Volumen, Bid/Ask, Delta, Footprint)
//   derived      – von uns aus den empfangenen Marktdaten berechnet (Minutenkerzen, CVD, VWAP ab dokumentiertem Anker)
//   unavailable  – mit diesem Feed/dieser Version nicht verfügbar oder ungeprüft
//   test         – klar gekennzeichnete Testdaten (Bridge meldet source=test)
// Wertzustände: wert (auch echte Null), noch_kein_wert, veraltet, feed_getrennt, quelle_ungeklaert.
// =====================================================================================================================

public static class Prov
{
    public const string Live = "live_atas", Chart = "atas_chart", Derived = "derived", Unavailable = "unavailable", Test = "test";
}

public static class ValState
{
    public const string Value = "wert", NoValueYet = "noch_kein_wert", Stale = "veraltet", Disconnected = "feed_getrennt", UnknownSource = "quelle_ungeklaert";
}

/// <summary>Ein angezeigter Wert mit Zustand und Herkunft (0 mit Zustand „wert“ ist eine echte Null).</summary>
public sealed record LiveValue(decimal? Value, string State, string Source, string? TMarket = null, string? Note = null);

public sealed record LiveTrade(long Seq, string TMarket, string TRecv, decimal Price, decimal Volume, string Side);
public sealed record LiveTape(long Id, string Event, string TMarket, decimal FirstPrice, decimal LastPrice, decimal Volume, int Ticks, string Side, int Updates);
public sealed record LiveLevel(decimal Price, decimal Volume);
public sealed record LiveFootprintLevel(decimal Price, decimal Bid, decimal Ask);
public sealed record LiveCandle(string TStart, string TEnd, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume, decimal BuyVolume,
    decimal SellVolume, decimal Delta, int Ticks, decimal? Vwap, bool Closed, IReadOnlyList<LiveFootprintLevel> Footprint, string Source, string? Note = null);
public sealed record LiveCompareRow(string Field, decimal? Atas, decimal? Dashboard, decimal? Difference, string Note);
public sealed record LiveComparison(int Bar, string TStart, string TEnd, string Status, IReadOnlyList<LiveCompareRow> Rows);

public sealed record LiveCounters(long LinesReceived, long Processed, long ParseErrors, long UnknownTypes, long BackendDropped, long BridgeDropped,
    IReadOnlyDictionary<string, long> BridgeDropsByStream, long SeqGaps, long SeqMissing, long Trades, long BboUpdates, long DomUpdates, long DomSnapshots,
    long CumNew, long CumUpdates, long Bars, long Heartbeats);

public sealed record LiveBook(string State, string Source, int BidLevels, int AskLevels, IReadOnlyList<LiveLevel> Bids, IReadOnlyList<LiveLevel> Asks,
    string? LastUpdateTMarket, string Note);

public sealed record LiveSnapshot(
    int Generation, string Status, string StatusNote, string? Source, string? SourceKind, string? Instrument, string? Exchange, decimal? TickSize,
    string? ChartType, string? TimeFrame, string? AtasVersion, string? Bridge, string? BridgeSessionId, string? ConnectedUtc, string? DisconnectedUtc,
    string? LastMessageUtc, string? LastMarketTime, string Session, string Mode, string ModeEvidence, LiveValue Bid, LiveValue Ask, LiveValue LastPrice,
    LiveBook Book, IReadOnlyList<LiveTrade> Trades, IReadOnlyList<LiveTape> Tape, LiveValue TapeVolume, IReadOnlyList<LiveCandle> Candles,
    IReadOnlyList<LiveCandle> AtasBars, LiveValue Cvd, string CvdAnchor, LiveValue SessionVwap, string VwapAnchor, LiveCounters Counters,
    IReadOnlyList<LiveComparison> Comparisons, IReadOnlyList<IndicatorAccess> Indicators, IReadOnlyList<string> CoverageBreaks);

public sealed record IndicatorAccess(string Name, string Category, string Detail);

/// <summary>Zustand des Live-Feeds. Thread-sicher (ein Lock); Ingest einzeln je Zeile, Snapshot jederzeit.</summary>
public sealed class AtasLiveFeed
{
    public const string Schema = "atas-live-1";
    private readonly object _lock = new();
    private readonly Func<DateTimeOffset> _now;
    private readonly TimeSpan _staleAfter;
    private readonly Func<DateTimeOffset, string> _sessionOf;
    private readonly int _tradeCap;

    // Verbindung / Quelle
    private int _generation;
    private bool _everConnected, _connected;
    private DateTimeOffset? _connectedAt, _disconnectedAt, _lastMessageAt;
    private string? _disconnectReason;
    private Hello? _hello;
    private long _lastSeq = -1;

    // Markt
    private readonly LinkedList<(DateTimeOffset T, LiveTrade X)> _trades = new();
    private (decimal P, decimal V, DateTimeOffset T)? _bid, _ask;
    private decimal? _lastPrice; private DateTimeOffset? _lastMarket;
    private readonly SortedDictionary<decimal, decimal> _bookBid = new(), _bookAsk = new();
    private string _bookState = "kein_buch";
    private DateTimeOffset? _bookUpdate;
    private readonly Dictionary<long, LiveTape> _cum = new();
    private readonly LinkedList<long> _cumOrder = new();
    private readonly SortedDictionary<DateTimeOffset, MinuteAgg> _minutes = new();
    private readonly SortedDictionary<int, LiveCandle> _bars = new();
    private decimal _cvd; private decimal _pv, _vol; private DateTimeOffset? _anchor;
    private readonly List<(DateTimeOffset From, DateTimeOffset? To)> _breaks = new();
    private (DateTimeOffset T, DateTimeOffset R)? _lastTradeTimes;

    // Zähler
    private long _lines, _processed, _parseErr, _unknown, _backendDrop, _bridgeDrop, _gaps, _missing, _nTrades, _nBbo, _nDom, _nSnap, _nCumNew, _nCumUpd, _nBars, _nHb;
    private Dictionary<string, long> _bridgeDropsByStream = new();

    public AtasLiveFeed(Func<DateTimeOffset>? now = null, TimeSpan? staleAfter = null, Func<DateTimeOffset, string>? sessionOf = null, int tradeCapacity = 400_000)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _staleAfter = staleAfter ?? TimeSpan.FromSeconds(3);
        _sessionOf = sessionOf ?? (_ => "unbekannt");
        _tradeCap = tradeCapacity;
    }

    private sealed record Hello(string Source, string? Instrument, string? Exchange, decimal? TickSize, string? ChartType, string? TimeFrame,
        string? AtasVersion, string? Bridge, string? SessionId);

    private sealed class MinuteAgg
    {
        public DateTimeOffset Start; public decimal O, H, L, C, V, Buy, Sell, PV; public int N;
        public readonly SortedDictionary<decimal, (decimal Bid, decimal Ask)> Fp = new();
    }

    // ------------------------------------------------------------------------------------------------- Verbindung

    public void OnConnected(string remote)
    {
        lock (_lock)
        {
            _everConnected = _connected = true;
            _connectedAt = _now();
            _disconnectReason = null;
            // Buch aus einer früheren Verbindung gilt nie weiter: neu aus Snapshot aufbauen, bis dahin unvollständig.
            ClearBook("unvollstaendig");
            if (_breaks.Count > 0 && _breaks[^1].To is null) _breaks[^1] = (_breaks[^1].From, _connectedAt);
        }
    }

    public void OnDisconnected(string reason)
    {
        lock (_lock)
        {
            if (!_connected) return;
            _connected = false;
            _disconnectedAt = _now();
            _disconnectReason = reason;
            ClearBook("verworfen_getrennt");
            _breaks.Add((_disconnectedAt.Value, null));
        }
    }

    public void NoteBackendDrop(long n) { lock (_lock) _backendDrop += n; }

    private void ClearBook(string state)
    {
        _bookBid.Clear(); _bookAsk.Clear(); _bookState = state; _bookUpdate = null;
        _bid = _ask = null;
    }

    private void ResetMarket()
    {
        _generation++;
        _trades.Clear(); _cum.Clear(); _cumOrder.Clear(); _minutes.Clear(); _bars.Clear(); _breaks.Clear();
        _lastPrice = null; _lastMarket = null; _cvd = 0; _pv = 0; _vol = 0; _anchor = null; _lastSeq = -1; _lastTradeTimes = null;
        ClearBook("unvollstaendig");
        _lines = _processed = _parseErr = _unknown = _backendDrop = _bridgeDrop = _gaps = _missing = 0;
        _nTrades = _nBbo = _nDom = _nSnap = _nCumNew = _nCumUpd = _nBars = _nHb = 0;
        _bridgeDropsByStream = new();
        _lines = 1; // die hello-Zeile selbst gehört schon zur neuen Quelle
    }

    // ------------------------------------------------------------------------------------------------- Eingang

    public void Ingest(string line)
    {
        lock (_lock)
        {
            _lines++;
            _lastMessageAt = _now();
            JsonElement e;
            try { using var doc = JsonDocument.Parse(line); e = doc.RootElement.Clone(); }
            catch (JsonException) { _parseErr++; return; }
            string type = Str(e, "type") ?? "";
            if (type != "hello" && _hello is null) { _unknown++; return; } // ohne hello keine Zuordnung zu Quelle/Instrument
            if (type != "hello" && e.TryGetProperty("seq", out var sq) && sq.TryGetInt64(out var seq))
            {
                if (_lastSeq >= 0 && seq > _lastSeq + 1) { _gaps++; _missing += seq - _lastSeq - 1; }
                if (seq > _lastSeq) _lastSeq = seq;
            }
            switch (type)
            {
                case "hello": OnHello(e); break;
                case "trade": OnTrade(e); break;
                case "bbo": OnBbo(e); break;
                case "dom": OnDom(e); break;
                case "domSnapshot": OnDomSnapshot(e); break;
                case "cum": OnCum(e); break;
                case "bar": OnBar(e); break;
                case "hb": OnHeartbeat(e); break;
                default: _unknown++; return;
            }
            _processed++;
        }
    }

    private void OnHello(JsonElement e)
    {
        var h = new Hello(Str(e, "source") ?? "unbekannt", Str(e, "instrument"), Str(e, "exchange"), Dec(e, "tickSize"), Str(e, "chartType"),
            Str(e, "timeFrame"), Str(e, "atasVersion"), Str(e, "bridge"), Str(e, "sessionId"));
        // Quellen- oder Instrumentwechsel: nichts Altes stehen lassen (Kerzen, Tape, Buch, Zähler).
        bool sameSource = _hello is not null && _hello.SessionId == h.SessionId && _hello.Instrument == h.Instrument && _hello.Source == h.Source;
        if (!sameSource) ResetMarket();
        else _lastSeq = -1; // neue Verbindung derselben Instanz: Sequenz beginnt beim Bridge-Zähler neu geprüft
        _hello = h;
    }

    private void OnTrade(JsonElement e)
    {
        var t = Time(e, "t"); var r = Time(e, "r");
        decimal p = Dec(e, "p") ?? 0, v = Dec(e, "v") ?? 0;
        string side = Str(e, "d") ?? "Unknown";
        _nTrades++;
        var lt = new LiveTrade(Long(e, "seq"), Iso(t), Iso(r), p, v, side);
        _trades.AddLast((t, lt));
        while (_trades.Count > _tradeCap) _trades.RemoveFirst();
        _lastPrice = p; _lastMarket = t; _lastTradeTimes = (t, r);
        _anchor ??= t;
        decimal signed = side == "Buy" ? v : side == "Sell" ? -v : 0;
        _cvd += signed; _pv += p * v; _vol += v;
        var m = new DateTimeOffset(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, TimeSpan.Zero);
        if (!_minutes.TryGetValue(m, out var a)) { a = new MinuteAgg { Start = m, O = p, H = p, L = p }; _minutes[m] = a; while (_minutes.Count > 240) _minutes.Remove(_minutes.Keys.First()); }
        a.H = Math.Max(a.H, p); a.L = Math.Min(a.L, p); a.C = p; a.V += v; a.N++; a.PV += p * v;
        if (side == "Buy") a.Buy += v; else if (side == "Sell") a.Sell += v;
        a.Fp.TryGetValue(p, out var f);
        a.Fp[p] = side == "Buy" ? (f.Bid, f.Ask + v) : side == "Sell" ? (f.Bid + v, f.Ask) : f;
    }

    private void OnBbo(JsonElement e)
    {
        _nBbo++;
        var t = Time(e, "t"); decimal p = Dec(e, "p") ?? 0, v = Dec(e, "v") ?? 0;
        if (Str(e, "side") == "Bid") _bid = (p, v, t); else if (Str(e, "side") == "Ask") _ask = (p, v, t);
    }

    private void OnDom(JsonElement e)
    {
        _nDom++;
        var book = Str(e, "side") == "Bid" ? _bookBid : Str(e, "side") == "Ask" ? _bookAsk : null;
        if (book is null) return;
        decimal p = Dec(e, "p") ?? 0, v = Dec(e, "v") ?? 0;
        if (v == 0) book.Remove(p); else book[p] = v;
        _bookUpdate = Time(e, "t");
        if (_bookState is "kein_buch" or "verworfen_getrennt") _bookState = "unvollstaendig";
    }

    private void OnDomSnapshot(JsonElement e)
    {
        _nSnap++;
        _bookBid.Clear(); _bookAsk.Clear();
        if (e.TryGetProperty("levels", out var lv) && lv.ValueKind == JsonValueKind.Array)
            foreach (var l in lv.EnumerateArray())
            {
                decimal p = Dec(l, "p") ?? 0, v = Dec(l, "v") ?? 0;
                if (v == 0) continue;
                if (Str(l, "side") == "Bid") _bookBid[p] = v; else if (Str(l, "side") == "Ask") _bookAsk[p] = v;
            }
        _bookUpdate = Time(e, "t");
        _bookState = "vollstaendig";
    }

    private void OnCum(JsonElement e)
    {
        long id = Long(e, "id");
        string ev = Str(e, "ev") ?? "new";
        bool known = _cum.TryGetValue(id, out var prev);
        // Updates eines bereits kumulierten Trades ERSETZEN dessen Stand – das Volumen wird nie doppelt gezählt.
        var rec = new LiveTape(id, ev, Iso(Time(e, "t")), Dec(e, "p0") ?? 0, Dec(e, "p1") ?? 0, Dec(e, "v") ?? 0, (int)Long(e, "n"),
            Str(e, "d") ?? "Unknown", known ? prev!.Updates + (ev == "update" ? 1 : 0) : 0);
        _cum[id] = rec;
        if (!known) { _cumOrder.AddLast(id); while (_cumOrder.Count > 5000) { _cum.Remove(_cumOrder.First!.Value); _cumOrder.RemoveFirst(); } }
        if (ev == "update") _nCumUpd++; else _nCumNew++;
    }

    private void OnBar(JsonElement e)
    {
        _nBars++;
        int bar = (int)Long(e, "bar");
        var fp = new List<LiveFootprintLevel>();
        if (e.TryGetProperty("fp", out var f) && f.ValueKind == JsonValueKind.Array)
            foreach (var l in f.EnumerateArray()) fp.Add(new LiveFootprintLevel(Dec(l, "p") ?? 0, Dec(l, "b") ?? 0, Dec(l, "a") ?? 0));
        decimal bid = Dec(e, "bid") ?? 0, ask = Dec(e, "ask") ?? 0;
        _bars[bar] = new LiveCandle(Iso(Time(e, "t")), Str(e, "tLast") is { } tl ? Iso(ParseTime(tl)) : "", Dec(e, "o") ?? 0, Dec(e, "h") ?? 0, Dec(e, "l") ?? 0,
            Dec(e, "c") ?? 0, Dec(e, "v") ?? 0, ask, bid, Dec(e, "delta") ?? 0, (int)(Dec(e, "ticks") ?? 0), Dec(e, "vwap"),
            e.TryGetProperty("closed", out var c) && c.ValueKind == JsonValueKind.True, fp.OrderByDescending(x => x.Price).ToList(), SrcOr(Prov.Chart),
            "ATAS-Kerze der Chartinstanz (GetCandle); Bid = Verkäufer-, Ask = Käufer-Aggressorvolumen laut ATAS");
        while (_bars.Count > 240) _bars.Remove(_bars.Keys.First());
    }

    private void OnHeartbeat(JsonElement e)
    {
        _nHb++;
        if (e.TryGetProperty("drops", out var d) && d.ValueKind == JsonValueKind.Object)
        {
            var m = new Dictionary<string, long>();
            foreach (var p in d.EnumerateObject()) m[p.Name] = p.Value.TryGetInt64(out var n) ? n : 0;
            _bridgeDropsByStream = m;
            _bridgeDrop = m.Values.Sum();
        }
    }

    private string SrcOr(string p) => _hello?.Source == "test" ? Prov.Test : p;

    // ------------------------------------------------------------------------------------------------- Sicht

    public IReadOnlyList<LiveComparison> Compare() { lock (_lock) return CompareBars(); }

    public LiveSnapshot Snapshot(int trades = 200, int tape = 100, int candles = 120)
    {
        lock (_lock)
        {
            var now = _now();
            string status, note;
            if (!_everConnected) { status = "nie_verbunden"; note = "Noch keine Bridge-Verbindung. ATAS-Indikator „OF Live Bridge (read-only)“ an einen MNQ-Chart hängen."; }
            else if (!_connected) { status = "getrennt"; note = $"Verbindung getrennt ({_disconnectReason}). Buch verworfen; angezeigte Werte sind nicht aktuell."; }
            else if (_lastMessageAt is null || now - _lastMessageAt > _staleAfter) { status = "veraltet"; note = $"Verbunden, aber seit {(now - (_lastMessageAt ?? _connectedAt!.Value)).TotalSeconds:0.0} s keine Nachricht (auch kein Heartbeat)."; }
            else { status = "verbunden"; note = "Daten treffen ein."; }
            string valState = status switch { "verbunden" => ValState.Value, "veraltet" => ValState.Stale, _ => _everConnected ? ValState.Disconnected : ValState.NoValueYet };
            string src(string p) => _hello is null ? Prov.Unavailable : SrcOr(p);

            LiveValue Quote((decimal P, decimal V, DateTimeOffset T)? q) => q is { } x
                ? new LiveValue(x.P, valState, src(Prov.Live), Iso(x.T), $"Volumen {x.V.ToString(CultureInfo.InvariantCulture)}")
                : new LiveValue(null, _connected ? ValState.NoValueYet : (_everConnected ? ValState.Disconnected : ValState.NoValueYet), src(Prov.Live));

            string mode = "unbekannt", modeEv = "noch kein Trade";
            if (_lastTradeTimes is { } lt)
            {
                var lag = (lt.R - lt.T).TotalSeconds;
                mode = Math.Abs(lag) <= 15 ? "live" : "replay_oder_historie";
                modeEv = FormattableString.Invariant($"Empfang − Marktzeit des letzten Trades = {lag:0.###} s (abgeleitet aus Zeitstempeln, kein ATAS-Moduskennzeichen)");
            }

            var bookState = _bookState;
            var bids = _bookBid.Reverse().Take(20).Select(k => new LiveLevel(k.Key, k.Value)).ToList();
            var asks = _bookAsk.Take(20).Select(k => new LiveLevel(k.Key, k.Value)).ToList();
            string bookNote = bookState switch
            {
                "vollstaendig" => "Snapshot (MarketDepthInfo) + Updates seit Verbindungsaufbau.",
                "unvollstaendig" => "Nur Updates seit Verbindungsaufbau, noch kein Snapshot – nicht als vollständiges Buch lesen.",
                "verworfen_getrennt" => "Feed getrennt – Buch verworfen, wird nach Wiederverbindung aus einem neuen Snapshot aufgebaut.",
                _ => "Noch kein DOM empfangen."
            };
            if (status == "veraltet" && bookState == "vollstaendig") { bookState = "veraltet"; bookNote = "Feed veraltet – Buch nicht als aktuell lesen."; }

            var tapeList = _cumOrder.Reverse().Take(tape).Select(id => _cum[id]).ToList();
            decimal tapeVol = _cum.Values.Sum(x => x.Volume);

            var cand = _minutes.Values.TakeLast(candles).Select(a => new LiveCandle(Iso(a.Start), Iso(a.Start.AddMinutes(1)), a.O, a.H, a.L, a.C, a.V, a.Buy, a.Sell,
                a.Buy - a.Sell, a.N, a.V > 0 ? Math.Round(a.PV / a.V, 4) : null, a.Start.AddMinutes(1) <= (_lastMarket ?? DateTimeOffset.MinValue),
                a.Fp.OrderByDescending(k => k.Key).Select(k => new LiveFootprintLevel(k.Key, k.Value.Bid, k.Value.Ask)).ToList(), src(Prov.Derived),
                "von uns aus empfangenen Trades berechnet (1-Minuten-Raster UTC)")).ToList();

            var counters = new LiveCounters(_lines, _processed, _parseErr, _unknown, _backendDrop, _bridgeDrop, _bridgeDropsByStream, _gaps, _missing, _nTrades,
                _nBbo, _nDom, _nSnap, _nCumNew, _nCumUpd, _nBars, _nHb);

            return new LiveSnapshot(_generation, status, note, _hello?.Source, _hello is null ? null : SrcOr(Prov.Live), _hello?.Instrument, _hello?.Exchange,
                _hello?.TickSize, _hello?.ChartType, _hello?.TimeFrame, _hello?.AtasVersion, _hello?.Bridge, _hello?.SessionId, IsoN(_connectedAt),
                IsoN(_disconnectedAt), IsoN(_lastMessageAt), IsoN(_lastMarket), _lastMarket is { } lm ? _sessionOf(lm) : "unbekannt", mode, modeEv,
                Quote(_bid), Quote(_ask),
                _lastPrice is { } lp ? new LiveValue(lp, valState, src(Prov.Live), IsoN(_lastMarket)) : new LiveValue(null, ValState.NoValueYet, src(Prov.Live)),
                new LiveBook(bookState, src(Prov.Live), _bookBid.Count, _bookAsk.Count, bids, asks, IsoN(_bookUpdate), bookNote),
                _trades.Reverse().Take(trades).Select(x => x.X).ToList(), tapeList,
                _cum.Count > 0 ? new LiveValue(tapeVol, valState, src(Prov.Derived), null, "Summe der LETZTEN Stände je kumuliertem Trade (Updates ersetzen, nicht addieren)")
                               : new LiveValue(null, ValState.NoValueYet, src(Prov.Live)),
                cand, _bars.Values.TakeLast(candles).ToList(),
                _anchor is null ? new LiveValue(null, ValState.NoValueYet, src(Prov.Derived)) : new LiveValue(_cvd, valState, src(Prov.Derived)),
                _anchor is { } an ? $"ab erstem empfangenem Trade dieser Quelle ({Iso(an)}) – nicht der ATAS-Sessionanker" : "noch kein Anker",
                _vol > 0 ? new LiveValue(Math.Round(_pv / _vol, 4), valState, src(Prov.Derived)) : new LiveValue(null, ValState.NoValueYet, src(Prov.Derived)),
                _anchor is { } an2 ? $"VWAP von uns ab {Iso(an2)} (erster empfangener Trade) – NICHT die VWAP-Linie deines Charts" : "noch kein Anker",
                counters, Array.Empty<LiveComparison>(), Indicators(), _breaks.Select(b => $"{Iso(b.From)} – {(b.To is { } to ? Iso(to) : "offen")}").ToList());
        }
    }

    /// <summary>Herkunft der Indikatorwerte in dieser Version (aus der öffentlichen ATAS-Indikator-API geprüft, nicht vermischt).</summary>
    public static IReadOnlyList<IndicatorAccess> Indicators() => new[]
    {
        new IndicatorAccess("Kerzen-OHLC, Volumen, Bid/Ask-Volumen, Delta, Footprint je Preis", "direkt aus der laufenden Chartinstanz",
            "Bridge-Indikator liest GetCandle(bar) des Charts, an dem er hängt (IndicatorCandle, GetAllPriceLevels)."),
        new IndicatorAccess("Kerzen-VWAP (je Kerze)", "direkt aus der laufenden Chartinstanz", "IndicatorCandle.VWAP – Anker = Kerzenbeginn, nicht Session."),
        new IndicatorAccess("Deine VWAP-Linie (ATAS-Indikator VWAP mit deinem Anker)", "in dieser Version nicht zuverlässig zugänglich",
            "Die öffentliche Indikator-API (IChart) listet keine anderen Indikatorinstanzen. Eine zweite ATAS-VWAP-Instanz mit übernommenen Einstellungen ist möglich, aber noch nicht gebaut/verglichen."),
        new IndicatorAccess("Session-VWAP / CVD im Dashboard", "von uns aus Marktdaten abgeleitet", "Anker = erster empfangener Trade der Quelle (offen ausgewiesen)."),
        new IndicatorAccess("Delta/CVD-Indikator von ATAS (CumulativeDelta)", "in dieser Version nicht zuverlässig zugänglich", "Klasse vorhanden, Werte deiner Instanz nicht lesbar."),
        new IndicatorAccess("TPO / Market Profile (TPO-POC)", "in dieser Version nicht zuverlässig zugänglich", "Keine öffentliche TPO-Klasse; ein Volumen-POC wird NICHT als TPO-POC ausgegeben."),
        new IndicatorAccess("Smart Tape (Modul)", "in dieser Version nicht zuverlässig zugänglich", "Modul-Filter nicht lesbar; Grunddaten = kumulierte Trades (OnCumulativeTrade) live."),
    };

    private IReadOnlyList<LiveComparison> CompareBars()
    {
        var res = new List<LiveComparison>();
        var keys = _bars.Keys.ToList();
        for (int i = 0; i < keys.Count; i++)
        {
            var b = _bars[keys[i]];
            if (!b.Closed) continue;
            var start = ParseTime(b.TStart);
            DateTimeOffset end = i + 1 < keys.Count ? ParseTime(_bars[keys[i + 1]].TStart) : (b.TEnd.Length > 0 ? ParseTime(b.TEnd).AddTicks(1) : start);
            if (end <= start) continue;
            string status = "verglichen";
            var first = _trades.First?.Value;
            if (first is null || first.Value.T > start) status = "nicht_vergleichbar: Trades vor Kerzenbeginn nicht empfangen";
            if (_breaks.Any(br => br.From < end && (br.To ?? DateTimeOffset.MaxValue) > start)) status = "nicht_vergleichbar: Verbindungsunterbrechung in der Kerze";
            if (_missing > 0 || _bridgeDrop > 0 || _backendDrop > 0) status = status == "verglichen" ? "verglichen (Achtung: Feed meldet verlorene Ereignisse)" : status;
            var ts = new List<LiveTrade>();
            for (var n = _trades.Last; n is not null && n.Value.T >= start; n = n.Previous)
                if (n.Value.T < end) ts.Add(n.Value.X);
            ts.Reverse();
            var rows = new List<LiveCompareRow>();
            if (status.StartsWith("verglichen") && ts.Count > 0)
            {
                decimal o = ts[0].Price, c = ts[^1].Price, h = ts.Max(t => t.Price), l = ts.Min(t => t.Price), v = ts.Sum(t => t.Volume);
                decimal buy = ts.Where(t => t.Side == "Buy").Sum(t => t.Volume), sell = ts.Where(t => t.Side == "Sell").Sum(t => t.Volume);
                decimal vw = v > 0 ? Math.Round(ts.Sum(t => t.Price * t.Volume) / v, 4) : 0;
                void Row(string f, decimal a, decimal d, string n = "") => rows.Add(new LiveCompareRow(f, a, d, d - a, n));
                Row("Open", b.Open, o); Row("High", b.High, h); Row("Low", b.Low, l); Row("Close", b.Close, c); Row("Volumen", b.Volume, v);
                Row("Buy (Ask-Volumen)", b.BuyVolume, buy); Row("Sell (Bid-Volumen)", b.SellVolume, sell); Row("Delta", b.Delta, buy - sell);
                if (b.Vwap is { } bv) Row("VWAP (Anker Kerzenbeginn)", Math.Round(bv, 4), vw, "gleicher Anker: Kerzenbeginn");
                var poc = b.Footprint.OrderByDescending(x => x.Bid + x.Ask).FirstOrDefault();
                if (poc is not null)
                {
                    Row($"Footprint {poc.Price.ToString(CultureInfo.InvariantCulture)} Bid", poc.Bid, ts.Where(t => t.Price == poc.Price && t.Side == "Sell").Sum(t => t.Volume), "Preisstufe mit größtem Volumen laut ATAS");
                    Row($"Footprint {poc.Price.ToString(CultureInfo.InvariantCulture)} Ask", poc.Ask, ts.Where(t => t.Price == poc.Price && t.Side == "Buy").Sum(t => t.Volume));
                }
            }
            else if (status.StartsWith("verglichen")) status = "nicht_vergleichbar: keine Trades im Intervall empfangen";
            res.Add(new LiveComparison(keys[i], b.TStart, Iso(end), status, rows));
        }
        return res.TakeLast(30).ToList();
    }

    // ------------------------------------------------------------------------------------------------- Helfer

    private static string? Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static decimal? Dec(JsonElement e, string k) => e.TryGetProperty(k, out var v)
        ? v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null
        : null;
    private static long Long(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
    private static DateTimeOffset Time(JsonElement e, string k) => Str(e, k) is { } s ? ParseTime(s) : DateTimeOffset.MinValue;
    public static DateTimeOffset ParseTime(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    private static string? IsoN(DateTimeOffset? t) => t is { } v ? Iso(v) : null;
}
