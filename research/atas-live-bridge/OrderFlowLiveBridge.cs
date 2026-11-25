using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using ATAS.Indicators;
using TradeDirection = ATAS.Indicators.TradeDirection;
using MarketDataType = ATAS.Indicators.MarketDataType;

namespace OrderFlowTools
{
    /// <summary>
    /// NUR-LESENDE ATAS-Live-Bridge „OF Live Bridge (read-only)“.
    ///
    /// Ein <see cref="Indicator"/> (keine ChartStrategy) – es wird KEINE Order-/Konto-API benutzt (kein TradingManager,
    /// keine Portfolio-/Order-Methoden). Die Bridge liest dieselben, im Recorder (proto-2c) bereits bewährten SDK-Callbacks und
    /// sendet sie als NDJSON an das lokale Backend (TCP, ausschließlich 127.0.0.1). Aufnahme-/Archivpfad des Recorders bleibt
    /// unverändert – beide Indikatoren sind unabhängig.
    ///
    /// Nachrichten (Schema atas-live-1): hello, trade, bbo, dom, domSnapshot, cum (new/update), bar (closed/laufend), hb.
    /// Verlorene Ereignisse werden gezählt (Puffer voll oder Backend nicht verbunden) und im Heartbeat gemeldet – nie still.
    /// </summary>
    [DisplayName("OF Live Bridge (read-only)")]
    public class OrderFlowLiveBridge : Indicator
    {
        public const string Schema = "atas-live-1";
        public const string BridgeVersion = "OF Live Bridge 1";

        [Display(Name = "Backend-Port (nur 127.0.0.1)")]
        public int Port { get; set; } = 5036;

        [Display(Name = "Kerzen (OHLC/Delta/Footprint) senden")]
        public bool SendBars { get; set; } = true;

        [Display(Name = "Laufende Kerze alle n ms senden (0 = nur geschlossene)")]
        public int CurrentBarIntervalMs { get; set; } = 1000;

        private const int QueueCapacity = 200_000;
        private BlockingCollection<string> _queue;
        private Thread _sender;
        private volatile bool _running;
        private volatile bool _connected;
        private readonly string _sessionId = Guid.NewGuid().ToString("N");
        private long _seq;
        private readonly ConcurrentDictionary<string, long> _drops = new();
        private readonly ConcurrentDictionary<string, long> _sent = new();
        private readonly Dictionary<CumulativeTrade, long> _cumIds = new(ReferenceEqualityComparer.Instance);
        private long _cumIdSeq;
        private volatile bool _realtime;          // ab dem ersten Trade-Callback (historischer Rechenlauf feuert keine Trades)
        private int _lastSentClosedBar = -1;
        private long _lastCurrentBarSentTicks;

        public OrderFlowLiveBridge() : base(true)
        {
            DenyToChangePanel = true;
        }

        // ================= Lebenszyklus =================
        private void StartIfNeeded()
        {
            if (_running) return;
            lock (_drops)
            {
                if (_running) return;
                _queue = new BlockingCollection<string>(QueueCapacity);
                _running = true;
                _sender = new Thread(SenderLoop) { IsBackground = true, Name = "OFLiveBridgeSender" };
                _sender.Start();
            }
        }

        protected override void OnDispose()
        {
            _running = false;
            try { _queue?.CompleteAdding(); } catch { }
            try { _sender?.Join(2000); } catch { }
            base.OnDispose();
        }

        private void SenderLoop()
        {
            var lastHb = Stopwatch.StartNew();
            while (_running)
            {
                TcpClient client = null;
                try
                {
                    client = new TcpClient();
                    client.NoDelay = true;
                    client.Connect(IPAddress.Loopback, Port);     // ausschließlich lokal
                    using var stream = client.GetStream();
                    using var w = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
                    // Neue Verbindung: Quelle melden, Buch aus frischem Snapshot (alte Level gelten im Backend nicht weiter).
                    w.WriteLine(HelloJson());
                    w.WriteLine(DomSnapshotJson());
                    w.Flush();
                    _connected = true;
                    while (_running)
                    {
                        if (_queue.TryTake(out var line, 200))
                        {
                            w.WriteLine(line);
                            int n = 1;
                            while (n < 5000 && _queue.TryTake(out line)) { w.WriteLine(line); n++; }
                            w.Flush();
                        }
                        if (lastHb.ElapsedMilliseconds >= 1000)
                        {
                            w.WriteLine(HeartbeatJson());
                            w.Flush();
                            lastHb.Restart();
                        }
                    }
                }
                catch (Exception) { /* Backend nicht erreichbar oder Verbindung weg -> erneut versuchen */ }
                finally
                {
                    _connected = false;
                    try { client?.Close(); } catch { }
                }
                if (_running) Thread.Sleep(2000);
            }
        }

        /// <summary>Nicht blockierend einreihen. Ohne Backend-Verbindung oder bei vollem Puffer: zählen, nicht still verwerfen.</summary>
        private void Enqueue(string stream, string json)
        {
            if (!_running) return;
            if (!_connected) { _drops.AddOrUpdate("offline_" + stream, 1, (_, c) => c + 1); return; }
            bool added;
            try { added = _queue.TryAdd(json); } catch (InvalidOperationException) { return; }
            if (added) _sent.AddOrUpdate(stream, 1, (_, c) => c + 1);
            else _drops.AddOrUpdate(stream, 1, (_, c) => c + 1);
        }

        private long NextSeq() => Interlocked.Increment(ref _seq);

        // ================= Markt-Callbacks (SDK, wie im Recorder verifiziert) =================
        protected override void OnNewTrade(MarketDataArg trade)
        {
            StartIfNeeded();
            if (trade.DataType != MarketDataType.Trade) return;
            _realtime = true;
            Enqueue("trade", J(("type", "trade"), ("seq", NextSeq()), ("t", Iso(trade.Time)), ("r", Iso(DateTime.UtcNow)),
                ("p", trade.Price), ("v", trade.Volume), ("d", Aggressor(trade.Direction))));
        }

        protected override void OnCumulativeTrade(CumulativeTrade trade) => Cum(trade, false);
        protected override void OnUpdateCumulativeTrade(CumulativeTrade trade) => Cum(trade, true);

        private void Cum(CumulativeTrade t, bool isUpdate)
        {
            StartIfNeeded();
            long id;
            lock (_cumIds)
            {
                if (!_cumIds.TryGetValue(t, out id))
                {
                    id = ++_cumIdSeq;
                    _cumIds[t] = id;
                    if (_cumIds.Count > 100_000) _cumIds.Clear();
                }
            }
            // Backend ersetzt je id den Stand (volume/tickCount sind kumuliert) – nie aufsummieren.
            Enqueue("cum", J(("type", "cum"), ("seq", NextSeq()), ("id", id), ("ev", isUpdate ? "update" : "new"), ("t", Iso(t.Time)),
                ("p0", t.FirstPrice), ("p1", t.Lastprice), ("v", t.Volume), ("n", t.Ticks?.Count ?? 0), ("d", Aggressor(t.Direction))));
        }

        protected override void OnBestBidAskChanged(MarketDataArg depth)
        {
            StartIfNeeded();
            Enqueue("bbo", J(("type", "bbo"), ("seq", NextSeq()), ("t", Iso(depth.Time)), ("side", depth.IsBid ? "Bid" : depth.IsAsk ? "Ask" : "?"),
                ("p", depth.Price), ("v", depth.Volume)));
        }

        protected override void MarketDepthChanged(MarketDataArg depth) => Dom(depth);
        protected override void MarketDepthsChanged(IEnumerable<MarketDataArg> depths) { foreach (var d in depths) Dom(d); }

        private void Dom(MarketDataArg d)
        {
            StartIfNeeded();
            Enqueue("dom", J(("type", "dom"), ("seq", NextSeq()), ("t", Iso(d.Time)), ("side", d.IsBid ? "Bid" : d.IsAsk ? "Ask" : "?"),
                ("p", d.Price), ("v", d.Volume)));
        }

        // ================= Kerzen der Chartinstanz (GetCandle) =================
        protected override void OnCalculate(int bar, decimal value)
        {
            StartIfNeeded();
            if (!SendBars || !_realtime) { _lastSentClosedBar = Math.Max(_lastSentClosedBar, CurrentBar - 2); return; }
            // Geschlossen sind alle Kerzen vor der laufenden (CurrentBar - 1).
            for (int i = _lastSentClosedBar + 1; i <= CurrentBar - 2; i++) SendBar(i, true);
            if (CurrentBar - 2 > _lastSentClosedBar) _lastSentClosedBar = CurrentBar - 2;
            if (CurrentBarIntervalMs > 0)
            {
                long now = DateTime.UtcNow.Ticks;
                if (now - _lastCurrentBarSentTicks >= CurrentBarIntervalMs * TimeSpan.TicksPerMillisecond)
                {
                    _lastCurrentBarSentTicks = now;
                    SendBar(CurrentBar - 1, false);
                }
            }
        }

        private void SendBar(int i, bool closed)
        {
            if (i < 0) return;
            IndicatorCandle c;
            try { c = GetCandle(i); } catch { return; }
            if (c == null) return;
            var sb = new StringBuilder(256);
            sb.Append("{\"type\":\"bar\",\"seq\":").Append(NextSeq()).Append(",\"bar\":").Append(i).Append(",\"closed\":").Append(closed ? "true" : "false")
              .Append(",\"t\":\"").Append(Iso(c.Time)).Append("\",\"tLast\":\"").Append(Iso(c.LastTime)).Append('"');
            Num(sb, "o", c.Open); Num(sb, "h", c.High); Num(sb, "l", c.Low); Num(sb, "c", c.Close); Num(sb, "v", c.Volume);
            Num(sb, "bid", c.Bid); Num(sb, "ask", c.Ask); Num(sb, "delta", c.Delta); Num(sb, "ticks", c.Ticks); Num(sb, "vwap", c.VWAP);
            Num(sb, "maxDelta", c.MaxDelta); Num(sb, "minDelta", c.MinDelta);
            sb.Append(",\"fp\":[");
            bool first = true;
            IEnumerable<PriceVolumeInfo> levels;
            try { levels = c.GetAllPriceLevels(); } catch { levels = null; }
            if (levels != null)
                foreach (var p in levels)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"p\":").Append(N(p.Price)).Append(",\"b\":").Append(N(p.Bid)).Append(",\"a\":").Append(N(p.Ask)).Append('}');
                }
            sb.Append("]}");
            Enqueue("bar", sb.ToString());
        }

        // ================= Nachrichten =================
        private string HelloJson()
        {
            string ver = "";
            try { ver = FileVersionInfo.GetVersionInfo(typeof(Indicator).Assembly.Location).FileVersion ?? ""; } catch { }
            string chartType = "", tf = "";
            try { chartType = ChartInfo?.ChartType ?? ""; tf = ChartInfo?.TimeFrame ?? ""; } catch { }
            decimal tick = 0m; string inst = "UNKNOWN", exch = "";
            try { if (InstrumentInfo != null) { tick = InstrumentInfo.TickSize; inst = InstrumentInfo.Instrument ?? inst; exch = InstrumentInfo.Exchange ?? ""; } } catch { }
            return J(("type", "hello"), ("schema", Schema), ("source", "atas"), ("instrument", inst), ("exchange", exch), ("tickSize", tick),
                ("chartType", chartType), ("timeFrame", tf), ("atasVersion", ver), ("bridge", BridgeVersion), ("sessionId", _sessionId),
                ("startedUtc", Iso(DateTime.UtcNow)));
        }

        private string DomSnapshotJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"type\":\"domSnapshot\",\"seq\":").Append(NextSeq()).Append(",\"t\":\"").Append(Iso(DateTime.UtcNow)).Append("\",\"levels\":[");
            bool first = true;
            try
            {
                var snap = MarketDepthInfo?.GetMarketDepthSnapshot();
                if (snap != null)
                    foreach (var d in snap)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        sb.Append("{\"side\":\"").Append(d.IsBid ? "Bid" : d.IsAsk ? "Ask" : "?").Append("\",\"p\":").Append(N(d.Price)).Append(",\"v\":").Append(N(d.Volume)).Append('}');
                    }
            }
            catch { }
            sb.Append("]}");
            return sb.ToString();
        }

        private string HeartbeatJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"type\":\"hb\",\"seq\":").Append(NextSeq()).Append(",\"r\":\"").Append(Iso(DateTime.UtcNow)).Append("\",\"queue\":").Append(_queue?.Count ?? 0)
              .Append(",\"drops\":{");
            bool first = true;
            foreach (var kv in _drops)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(kv.Key).Append("\":").Append(kv.Value);
            }
            sb.Append("}}");
            return sb.ToString();
        }

        // ================= Helfer =================
        private static string Aggressor(TradeDirection d) => d == TradeDirection.Buy ? "Buy" : d == TradeDirection.Sell ? "Sell" : "Unknown";

        private static string Iso(DateTime t) =>
            (t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t.ToUniversalTime())
            .ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

        private static string N(decimal d) => d.ToString(CultureInfo.InvariantCulture);
        private static void Num(StringBuilder sb, string k, decimal v) => sb.Append(",\"").Append(k).Append("\":").Append(N(v));

        private static string J(params (string k, object v)[] kv)
        {
            var sb = new StringBuilder(160);
            sb.Append('{');
            for (int i = 0; i < kv.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(kv[i].k).Append("\":");
                switch (kv[i].v)
                {
                    case null: sb.Append("null"); break;
                    case bool b: sb.Append(b ? "true" : "false"); break;
                    case decimal d: sb.Append(N(d)); break;
                    case int n: sb.Append(n.ToString(CultureInfo.InvariantCulture)); break;
                    case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
                    default: sb.Append('"').Append(Escape(kv[i].v.ToString())).Append('"'); break;
                }
            }
            sb.Append('}');
            return sb.ToString();
        }

        private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
