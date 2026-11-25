using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using ATAS.Indicators;
using ATAS.DataFeedsCore;
// MarketDataArg.Direction / CumulativeTrade.Direction sind ATAS.Indicators.TradeDirection (es gibt auch
// ATAS.DataFeedsCore.TradeDirection) -> eindeutig auf die vom Property verwendete Variante aliasen.
using TradeDirection = ATAS.Indicators.TradeDirection;
// MarketDataArg.DataType ist ATAS.Indicators.MarketDataType (es gibt auch ATAS.DataFeedsCore.MarketDataType).
using MarketDataType = ATAS.Indicators.MarketDataType;

namespace OrderFlowTools
{
    /// <summary>
    /// ISOLIERTER, NUR-LESENDER ATAS-Replay-Recorder (Machbarkeits-Prototyp).
    ///
    /// Ein <see cref="Indicator"/> (kein ChartStrategy) -> strukturell KEINE Order-API, kann nichts handeln.
    /// Getrennt von der bestehenden MCP-Bridge und der ORB-Arbeit. Keine Feed-/Konto-Einstellungen.
    ///
    /// Zeichnet aus den ECHTEN SDK-Callbacks (nicht aus gedrosselten HTTP-Snapshots) append-only lokal auf:
    ///   ticks.ndjson      - OnNewTrade(MarketDataArg)            : einzelne Trade-/Quote-Events
    ///   cumtrades.ndjson  - OnCumulativeTrade / OnUpdateCumulativeTrade : getrennt (new/update), ohne Doppelzaehlung
    ///   bbo.ndjson        - OnBestBidAskChanged(MarketDataArg)
    ///   dom.ndjson        - MarketDepthChanged / MarketDepthsChanged : DOM-Updates (Deltas)
    ///   bars.ndjson       - geschlossene Bars: OHLCV, Delta, Min/Max-Delta, Ticks, VWAP, POC + Footprint je Preis
    ///   ticks.csv         - dieselben Trades im Schema config/import-profiles/atas-tick.example.json
    ///                       (Time,Instrument,Price,Volume,AggressorSide) -> direkt via AtasTickCsvImporter nutzbar
    ///   meta.json         - Instrument, TickSize, ATAS-Version, Startzeit, Streams
    ///   gaps.ndjson       - erkannte Luecken (Queue-Overflow) mit Seq-Bereich + Grund
    ///   summary.json      - Totale: Events/Stream, Drops/Stream, Bytes, Dauer, gemessener Durchsatz
    ///
    /// Signaturen sind per Reflection gegen die installierte ATAS.Indicators.dll (8.0.15) verifiziert.
    /// WICHTIG: Lokale Sequenznummern beweisen KEINE Vollstaendigkeit beim Datenanbieter.
    /// </summary>
    [DisplayName("OF Replay Recorder (read-only)")]
    public class OrderFlowReplayRecorder : Indicator
    {
        // ---- Konfiguration (keine Order-/Feed-Parameter) ----
        [Display(Name = "Output-Ordner (leer = F:\\ATAS_Data\\Recordings)")]
        public string OutputRoot { get; set; } = "";

        // Standard-Ausgabe auf dem Datenlaufwerk F: (Projektvorgabe: keine Aufnahmen auf C:).
        private const string DefaultOutputRoot = @"F:\ATAS_Data\Recordings";

        [Display(Name = "Footprint je Bar aufzeichnen")]
        public bool RecordFootprint { get; set; } = true;

        [Display(Name = "Queue-Kapazitaet (Events)")]
        public int QueueCapacity { get; set; } = 200_000;

        // ---- Writer-Infrastruktur (entkoppelt Callbacks von Disk-I/O) ----
        private sealed record Line(string Stream, string Json, int Bytes);

        private BlockingCollection<Line> _queue;
        private Thread _writer;
        private readonly Dictionary<string, StreamWriter> _writers = new();
        private string _dir;
        private volatile bool _running;
        private volatile bool _writerFaulted;   // Writer-Thread hatte eine Exception (A3: kein sauberer Abschluss)
        private volatile bool _joinTimedOut;     // Writer-Join lief in den Timeout (Drain nicht bestaetigt)
        private string _runId;                   // Lauf-Korrelation (aus Env ATAS_OF_RUN_ID, sonst GUID) - A4

        private long _seq;                      // monoton, lokal
        private readonly ConcurrentDictionary<string, long> _count = new();   // Events je Stream
        private readonly ConcurrentDictionary<string, long> _drops = new();   // verworfen je Stream (Overflow)
        private readonly ConcurrentDictionary<string, long> _bytes = new();
        private long _queueHighWater;
        private DateTime _startWall;

        // Cumulative-Trade-Identitaet -> lokale Id, damit Updates nicht doppelt zaehlen.
        private readonly Dictionary<CumulativeTrade, long> _cumIds =
            new(ReferenceEqualityComparer.Instance);
        private long _cumIdSeq;

        private int _lastClosedBar = -1;
        private bool _metaWritten;
        private volatile bool _playStarted;   // true ab dem ersten echten Trade (= Replay laeuft, nicht Historie)

        public OrderFlowReplayRecorder() : base(true)
        {
            // Dummy-Series, damit der Indicator ohne Zeichenlast laeuft. Keine Darstellung noetig.
            DenyToChangePanel = true;
        }

        // ================= Lebenszyklus =================
        private void StartIfNeeded()
        {
            if (_running) return;
            lock (_writers)
            {
                if (_running) return;
                _startWall = DateTime.UtcNow;
                // A4: Lauf-Korrelation. Der Steuerungslauf kann ATAS_OF_RUN_ID setzen; sonst eindeutige GUID.
                try { _runId = Environment.GetEnvironmentVariable("ATAS_OF_RUN_ID"); } catch { }
                if (string.IsNullOrWhiteSpace(_runId)) _runId = Guid.NewGuid().ToString("N");
                var root = string.IsNullOrWhiteSpace(OutputRoot) ? DefaultOutputRoot : OutputRoot;
                // Kein stiller Fallback nach C:: fehlt das Ziellaufwerk, klar abbrechen statt auf C: zu schreiben.
                var driveRoot = Path.GetPathRoot(root);
                if (string.IsNullOrEmpty(driveRoot) || !Directory.Exists(driveRoot))
                    throw new DirectoryNotFoundException(
                        $"OF Replay Recorder: Ziellaufwerk '{driveRoot}' fuer OutputRoot '{root}' nicht verfuegbar - KEIN Fallback nach C:.");
                var sym = SafeInstrument();
                _dir = Path.Combine(root, $"{sym}_{_startWall:yyyyMMdd_HHmmss}Z");
                Directory.CreateDirectory(_dir);

                _queue = new BlockingCollection<Line>(Math.Max(1000, QueueCapacity));
                _running = true;
                _writer = new Thread(WriterLoop) { IsBackground = true, Name = "OFRecorderWriter" };
                _writer.Start();
            }
        }

        private void WriterLoop()
        {
            try
            {
                var sw = Stopwatch.StartNew();
                long sinceFlush = 0;
                // Timeout-Take: auch im Leerlauf (pausiertes Replay) regelmaessig flushen, damit
                // meta.json & Co. sofort auf Platte stehen und bei Absturz nichts im Puffer verloren geht.
                while (!_queue.IsCompleted)
                {
                    if (_queue.TryTake(out var ln, 200))
                    {
                        var w = GetWriter(ln.Stream);
                        w.WriteLine(ln.Json);
                        _bytes.AddOrUpdate(ln.Stream, ln.Bytes, (_, b) => b + ln.Bytes);
                        sinceFlush++;
                    }
                    if (sinceFlush > 0 && (sinceFlush >= 200 || sw.ElapsedMilliseconds >= 500))
                    {
                        foreach (var wr in _writers.Values) { try { wr.Flush(); } catch { } }
                        sinceFlush = 0; sw.Restart();
                    }
                }
            }
            catch (Exception) { _writerFaulted = true; /* Writer darf Callbacks nie stoeren, aber Fehler wird vermerkt (A3) */ }
            finally
            {
                foreach (var w in _writers.Values) { try { w.Flush(); w.Dispose(); } catch { _writerFaulted = true; } }
            }
        }

        private StreamWriter GetWriter(string stream)
        {
            if (_writers.TryGetValue(stream, out var w)) return w;
            var path = Path.Combine(_dir, stream);
            var sw = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
                                      new UTF8Encoding(false)) { AutoFlush = false };
            // CSV-Header einmalig.
            if (stream == "ticks.csv" && new FileInfo(path).Length == 0)
                sw.WriteLine("Time,Instrument,Price,Volume,AggressorSide");
            _writers[stream] = sw;
            return sw;
        }

        /// <summary>Nicht-blockierend einreihen; bei vollem Puffer: Drop zaehlen + Luecke markieren (kein stilles Verwerfen).</summary>
        private void Enqueue(string stream, string json)
        {
            if (!_running) return;
            var q = _queue;
            if (q == null || q.IsAddingCompleted) return;   // A3: nach CompleteAdding nichts mehr annehmen (kein Throw/Race)
            var line = new Line(stream, json, Encoding.UTF8.GetByteCount(json) + 1);
            bool added;
            try { added = q.TryAdd(line); }
            catch (InvalidOperationException) { return; }    // Dispose lief gerade an -> nicht mehr annehmen
            if (added)
            {
                _count.AddOrUpdate(stream, 1, (_, c) => c + 1);
                var n = q.Count;
                if (n > Interlocked.Read(ref _queueHighWater)) Interlocked.Exchange(ref _queueHighWater, n);
            }
            else
            {
                var d = _drops.AddOrUpdate(stream, 1, (_, c) => c + 1);
                // Luecke dokumentieren (best effort, selbst ueber die Queue, sonst verworfen).
                var gap = J(("type", "queue_overflow"), ("stream", stream), ("afterSeq", _seq), ("dropCountStream", d),
                            ("tRecv", Iso(DateTime.UtcNow)));
                try { q.TryAdd(new Line("gaps.ndjson", gap, 0)); } catch { }
            }
        }

        private long NextSeq() => Interlocked.Increment(ref _seq);

        // ================= Markt-Callbacks (SDK, verifiziert) =================
        protected override void OnNewTrade(MarketDataArg trade)
        {
            StartIfNeeded();
            if (trade.DataType == MarketDataType.Trade) _playStarted = true;   // ab jetzt Bars aufzeichnen
            var seq = NextSeq();
            Enqueue("ticks.ndjson", J(
                ("seq", seq),
                ("tMarket", Iso(trade.Time)),
                ("tRecv", Iso(DateTime.UtcNow)),
                ("dataType", trade.DataType.ToString()),
                ("price", trade.Price),
                ("volume", trade.Volume),
                ("direction", trade.Direction.ToString()),
                ("isBid", trade.IsBid),
                ("isAsk", trade.IsAsk),
                ("exchOrderId", trade.ExchangeOrderId),
                ("aggrExchOrderId", trade.AggressorExchangeOrderId),
                ("oi", trade.OpenInterest)));

            // Nur echte Trades (nicht Bid/Ask-Quote-Updates) in die wiederverwendbare CSV.
            if (trade.DataType == MarketDataType.Trade)
                Enqueue("ticks.csv", string.Join(",",
                    Iso(trade.Time), SafeInstrument(), Num(trade.Price), Num(trade.Volume), Aggressor(trade.Direction)));
        }

        protected override void OnCumulativeTrade(CumulativeTrade trade) => Cum(trade, isUpdate: false);
        protected override void OnUpdateCumulativeTrade(CumulativeTrade trade) => Cum(trade, isUpdate: true);

        private void Cum(CumulativeTrade t, bool isUpdate)
        {
            StartIfNeeded();
            _playStarted = true;   // Trades laufen (Cumulative-Pfad) -> Replay aktiv
            long localId;
            lock (_cumIds)
            {
                if (!_cumIds.TryGetValue(t, out localId))
                {
                    localId = ++_cumIdSeq;
                    _cumIds[t] = localId;
                    if (_cumIds.Count > 100_000) _cumIds.Clear(); // Speicher kappen (Prototyp)
                }
            }
            int tickCount = t.Ticks?.Count ?? 0;
            Enqueue("cumtrades.ndjson", J(
                ("seq", NextSeq()),
                ("cumId", localId),
                ("event", isUpdate ? "update" : "new"),
                ("tMarket", Iso(t.Time)),
                ("tRecv", Iso(DateTime.UtcNow)),
                ("firstPrice", t.FirstPrice),
                ("lastPrice", t.Lastprice),
                ("volume", t.Volume),
                ("tickCount", tickCount),
                ("direction", t.Direction.ToString())));
            // Hinweis fuer Downstream: pro cumId gilt der LETZTE Record (volume/tickCount sind kumuliert) -> nicht aufsummieren.
        }

        protected override void OnBestBidAskChanged(MarketDataArg depth)
        {
            StartIfNeeded();
            Enqueue("bbo.ndjson", J(
                ("seq", NextSeq()),
                ("tMarket", Iso(depth.Time)),
                ("tRecv", Iso(DateTime.UtcNow)),
                ("price", depth.Price),
                ("volume", depth.Volume),
                ("side", depth.IsBid ? "Bid" : depth.IsAsk ? "Ask" : "?")));
        }

        protected override void MarketDepthChanged(MarketDataArg depth) => Dom(depth);
        protected override void MarketDepthsChanged(IEnumerable<MarketDataArg> depths)
        {
            foreach (var d in depths) Dom(d);
        }

        private void Dom(MarketDataArg d)
        {
            StartIfNeeded();
            Enqueue("dom.ndjson", J(
                ("seq", NextSeq()),
                ("tMarket", Iso(d.Time)),
                ("tRecv", Iso(DateTime.UtcNow)),
                ("side", d.IsBid ? "Bid" : d.IsAsk ? "Ask" : "Trade"),
                ("price", d.Price),
                ("volume", d.Volume),
                ("dataType", d.DataType.ToString())));
        }

        // ================= Geschlossene Bars + Footprint =================
        protected override void OnCalculate(int bar, decimal value)
        {
            StartIfNeeded();
            if (!_metaWritten) WriteMeta();

            // Historie NICHT nachladen: bis zum ersten echten Trade (Play) nur den aktuellen Stand nachfuehren,
            // nichts schreiben. ATAS' historischer Rechenlauf feuert KEINE OnNewTrade-Events -> _playStarted bleibt
            // false, bis das Replay wirklich laeuft. Danach nur noch ab jetzt schliessende Bars aufzeichnen.
            if (!_playStarted) { _lastClosedBar = CurrentBar; return; }

            // KORREKTUR-KANDIDAT: Candle erst lesen, wenn sie mind. einen weiteren Bar lang geschlossen ist.
            // (Hypothese: Replay finalisiert die gerade geschlossene Bar verzoegert -> sonst degeneriert sie zu 1 Tick.)
            int readable = CurrentBar - 1;   // ein Bar Puffer
            for (int i = _lastClosedBar + 1; i < readable; i++)
                FlushBar(i);
            if (readable - 1 > _lastClosedBar) _lastClosedBar = readable - 1;
        }

        private void FlushBar(int i)
        {
            IndicatorCandle c;
            try { c = GetCandle(i); } catch { return; }
            if (c == null) return;

            var sb = new StringBuilder();
            sb.Append('{');
            Kv(sb, "seq", NextSeq()); sb.Append(',');
            Kv(sb, "bar", i); sb.Append(',');
            Kv(sb, "cbAtFlush", CurrentBar); sb.Append(',');   // Diagnose: CurrentBar zum Lesezeitpunkt
            Kv(sb, "tMarket", Iso(c.Time)); sb.Append(',');
            Kv(sb, "tRecv", Iso(DateTime.UtcNow)); sb.Append(',');
            Kv(sb, "open", c.Open); sb.Append(',');
            Kv(sb, "high", c.High); sb.Append(',');
            Kv(sb, "low", c.Low); sb.Append(',');
            Kv(sb, "close", c.Close); sb.Append(',');
            Kv(sb, "volume", c.Volume); sb.Append(',');
            Kv(sb, "delta", c.Delta); sb.Append(',');
            Kv(sb, "minDelta", c.MinDelta); sb.Append(',');
            Kv(sb, "maxDelta", c.MaxDelta); sb.Append(',');
            Kv(sb, "trades", c.Ticks); sb.Append(',');
            Kv(sb, "vwap", c.VWAP); sb.Append(',');
            try { Kv(sb, "pocPrice", c.MaxVolumePriceInfo != null ? c.MaxVolumePriceInfo.Price : 0m); sb.Append(','); } catch { }

            if (RecordFootprint)
            {
                sb.Append("\"footprint\":[");
                bool first = true;
                IEnumerable<PriceVolumeInfo> levels;
                try { levels = c.GetAllPriceLevels(); } catch { levels = null; }
                if (levels != null)
                    foreach (var p in levels)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append('{');
                        Kv(sb, "price", p.Price); sb.Append(',');
                        Kv(sb, "bid", p.Bid); sb.Append(',');
                        Kv(sb, "ask", p.Ask); sb.Append(',');
                        Kv(sb, "vol", p.Volume); sb.Append(',');
                        Kv(sb, "ticks", p.Ticks); sb.Append(',');
                        Kv(sb, "time", p.Time);
                        sb.Append('}');
                    }
                sb.Append(']');
            }
            else
            {
                // trailing comma vermeiden
                sb.Length -= 1;
            }
            sb.Append('}');
            Enqueue("bars.ndjson", sb.ToString());
        }

        private void WriteMeta()
        {
            _metaWritten = true;
            decimal tick = 0m;
            try { tick = InstrumentInfo != null ? InstrumentInfo.TickSize : 0m; } catch { }
            var meta = J(
                ("recorder", "OrderFlowReplayRecorder/proto-2-bufferfix"),
                ("runId", _runId ?? ""),
                ("instrument", SafeInstrument()),
                ("tickSize", tick),
                ("startWallUtc", Iso(_startWall)),
                ("note", "tMarket=ATAS-Replay-Marktzeit; tRecv=lokale Empfangs-Wanduhr (UTC). Lokale Seq != Anbieter-Vollstaendigkeit."),
                ("streams", "ticks.ndjson,cumtrades.ndjson,bbo.ndjson,dom.ndjson,bars.ndjson,ticks.csv"));
            Enqueue("meta.json", meta);
        }

        // ================= Stop / Summary =================
        // A3: Kontrollierter Abschluss in korrekter Reihenfolge.
        //   1) keine neuen Events mehr annehmen (_running=false)
        //   2) Queue schliessen -> Writer draint die bereits akzeptierten Events und flusht/schliesst die Dateien
        //   3) auf den Writer warten (Join) UND das Ergebnis pruefen (Timeout => kein sauberer Abschluss)
        //   4) ERST DANACH die Summary schreiben, mit 'clean'-Status (vorhandene Summary != erfolgreicher Abschluss)
        private const int WriterJoinMs = 30000;
        protected override void OnDispose()
        {
            if (!_running) { base.OnDispose(); return; }
            _running = false;                                  // 1) Annahme stoppen
            bool joined = true;
            try
            {
                _queue?.CompleteAdding();                       // 2) keine Adds mehr; Writer draint Restqueue
                if (_writer != null) joined = _writer.Join(WriterJoinMs);  // 3) auf Drain/Flush warten + pruefen
            }
            catch (Exception) { _writerFaulted = true; }
            _joinTimedOut = !joined;
            bool clean = joined && !_writerFaulted;
            try { WriteSummary(clean); } catch { }             // 4) Summary mit Status erst nach bestaetigtem Drain
            base.OnDispose();
        }

        private void WriteSummary(bool clean)
        {
            var dur = (DateTime.UtcNow - _startWall).TotalSeconds;
            long totalEvents = _count.Values.Sum();
            long totalBytes = _bytes.Values.Sum();
            long totalDrops = _drops.Values.Sum();
            var sb = new StringBuilder();
            sb.Append('{');
            Kv(sb, "schemaVersion", "ofrec-summary-2"); sb.Append(',');   // A3: Schema-/Statusversion
            Kv(sb, "runId", _runId ?? ""); sb.Append(',');                // A4: Lauf-Korrelation
            Kv(sb, "clean", clean); sb.Append(',');                        // true nur bei bestaetigtem Drain ohne Writer-Fault
            Kv(sb, "writerFaulted", _writerFaulted); sb.Append(',');
            Kv(sb, "joinTimedOut", _joinTimedOut); sb.Append(',');
            Kv(sb, "totalDrops", totalDrops); sb.Append(',');
            Kv(sb, "durationSec", (decimal)dur); sb.Append(',');
            Kv(sb, "totalEvents", totalEvents); sb.Append(',');
            Kv(sb, "totalBytes", totalBytes); sb.Append(',');
            Kv(sb, "eventsPerSec", (decimal)(dur > 0 ? totalEvents / dur : 0)); sb.Append(',');
            Kv(sb, "bytesPerSec", (decimal)(dur > 0 ? totalBytes / dur : 0)); sb.Append(',');
            Kv(sb, "queueHighWater", Interlocked.Read(ref _queueHighWater)); sb.Append(',');
            sb.Append("\"perStream\":{");
            bool first = true;
            foreach (var s in _count.Keys.Concat(_drops.Keys).Distinct())
            {
                if (!first) sb.Append(',');
                first = false;
                _count.TryGetValue(s, out var ce); _drops.TryGetValue(s, out var de); _bytes.TryGetValue(s, out var be);
                sb.Append('"').Append(s).Append("\":{");
                Kv(sb, "events", ce); sb.Append(',');
                Kv(sb, "drops", de); sb.Append(',');
                Kv(sb, "bytes", be);
                sb.Append('}');
            }
            sb.Append("}}");
            // Direkt schreiben (Writer laeuft evtl. noch); notfalls ueber Queue.
            try
            {
                File.WriteAllText(Path.Combine(_dir, "summary.json"), sb.ToString(), new UTF8Encoding(false));
            }
            catch { Enqueue("summary.json", sb.ToString()); }
        }

        // ================= Helfer =================
        private string SafeInstrument()
        {
            try { return InstrumentInfo != null && !string.IsNullOrEmpty(InstrumentInfo.Instrument) ? InstrumentInfo.Instrument : "UNKNOWN"; }
            catch { return "UNKNOWN"; }
        }

        private static string Aggressor(TradeDirection d) =>
            d == TradeDirection.Buy ? "Buy" : d == TradeDirection.Sell ? "Sell" : "Unknown";

        private static string Iso(DateTime t) =>
            (t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t.ToUniversalTime())
            .ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

        private static string Num(decimal d) => d.ToString(CultureInfo.InvariantCulture);

        private static void Kv(StringBuilder sb, string k, decimal v) { sb.Append('"').Append(k).Append("\":").Append(Num(v)); }
        private static void Kv(StringBuilder sb, string k, long v) { sb.Append('"').Append(k).Append("\":").Append(v.ToString(CultureInfo.InvariantCulture)); }
        private static void Kv(StringBuilder sb, string k, int v) { sb.Append('"').Append(k).Append("\":").Append(v.ToString(CultureInfo.InvariantCulture)); }
        private static void Kv(StringBuilder sb, string k, string v) { sb.Append('"').Append(k).Append("\":\"").Append(v).Append('"'); }
        private static void Kv(StringBuilder sb, string k, bool v) { sb.Append('"').Append(k).Append("\":").Append(v ? "true" : "false"); }

        private static string J(params (string k, object v)[] kv)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            for (int i = 0; i < kv.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(kv[i].k).Append("\":");
                var v = kv[i].v;
                switch (v)
                {
                    case null: sb.Append("null"); break;
                    case bool b: sb.Append(b ? "true" : "false"); break;
                    case decimal d: sb.Append(Num(d)); break;
                    case int n: sb.Append(n.ToString(CultureInfo.InvariantCulture)); break;
                    case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
                    default: sb.Append('"').Append(v.ToString()).Append('"'); break;
                }
            }
            sb.Append('}');
            return sb.ToString();
        }
    }
}
