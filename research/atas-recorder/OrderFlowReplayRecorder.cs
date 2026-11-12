using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using ATAS.Indicators;
using ATAS.DataFeedsCore;
using OrderFlowTools;
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
    /// Der Lebenszyklus-/Writer-Kern liegt in <see cref="RecorderCore"/> (ATAS-frei, offline regressionsgetestet,
    /// Finding 3). Dieser Indicator liefert ausschließlich die SDK-Callbacks und reicht die Events an den Core.
    ///
    /// Zeichnet aus den ECHTEN SDK-Callbacks (nicht aus gedrosselten HTTP-Snapshots) append-only lokal auf:
    ///   ticks.ndjson, cumtrades.ndjson, bbo.ndjson, dom.ndjson, bars.ndjson, ticks.csv, meta.json,
    ///   gaps.ndjson, summary.json.
    ///
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

        // ---- Writer-/Lifecycle-Kern (ATAS-frei, Finding 3) ----
        // CoreGate koordiniert Dispose und erstmalige Core-Erzeugung terminal (Recheck Finding 3):
        // nach OnDispose entsteht kein neuer Running-Kern mehr, auch nicht durch einen späten ersten Callback.
        private readonly CoreGate _gate = new();
        private readonly string _claimId = Guid.NewGuid().ToString("N");   // A4: Token-Konsum-Identität dieser Instanz

        // Cumulative-Trade-Identitaet -> lokale Id, damit Updates nicht doppelt zaehlen.
        private readonly Dictionary<CumulativeTrade, long> _cumIds =
            new(ReferenceEqualityComparer.Instance);
        private long _cumIdSeq;

        private int _lastClosedBar = -1;
        private bool _metaWritten;
        private volatile bool _playStarted;   // true ab dem ersten echten Trade (= Replay laeuft, nicht Historie)

        public OrderFlowReplayRecorder() : base(true)
        {
            DenyToChangePanel = true;
        }

        // ================= Lebenszyklus =================
        // Core über das terminale Gate holen (null, wenn bereits disposed -> kein neuer Kern, Finding 3 Recheck).
        private RecorderCore? Core()
            => _gate.GetOrCreateAndStart(() => new RecorderCore(OutputRoot, DefaultOutputRoot, QueueCapacity, SafeInstrument, ReadRunToken));

        // A4 (Recheck): Token EXKLUSIV konsumieren (atomares Rename), nicht nur lesen. Der Steuerungslauf stellt
        // VOR dem Laden ein eindeutiges Token nach <OutputRoot>\.of_run_token bereit; nur EIN Recorder beansprucht es
        // und schreibt es als runId in meta/summary. Ein fremder Recorder erhält null -> GUID-runId -> kein Token-Match.
        private string? ReadRunToken()
        {
            var root = string.IsNullOrWhiteSpace(OutputRoot) ? DefaultOutputRoot : OutputRoot;
            return TokenHandshake.Consume(root, _claimId);   // -> sonst Core-Fallback Env/GUID
        }

        // ================= Markt-Callbacks (SDK, verifiziert) =================
        protected override void OnNewTrade(MarketDataArg trade)
        {
            var core = Core();
            if (core == null) return;   // nach Dispose kein neuer Kern (terminal)
            if (trade.DataType == MarketDataType.Trade) _playStarted = true;
            core.Enqueue("ticks.ndjson", RecorderCore.J(
                ("seq", core.NextSeq()),
                ("tMarket", RecorderCore.Iso(trade.Time)),
                ("tRecv", RecorderCore.Iso(DateTime.UtcNow)),
                ("dataType", trade.DataType.ToString()),
                ("price", trade.Price),
                ("volume", trade.Volume),
                ("direction", trade.Direction.ToString()),
                ("isBid", trade.IsBid),
                ("isAsk", trade.IsAsk),
                ("exchOrderId", trade.ExchangeOrderId),
                ("aggrExchOrderId", trade.AggressorExchangeOrderId),
                ("oi", trade.OpenInterest)));

            if (trade.DataType == MarketDataType.Trade)
                core.Enqueue("ticks.csv", string.Join(",",
                    RecorderCore.Iso(trade.Time), SafeInstrument(), RecorderCore.Num(trade.Price),
                    RecorderCore.Num(trade.Volume), Aggressor(trade.Direction)));
        }

        protected override void OnCumulativeTrade(CumulativeTrade trade) => Cum(trade, isUpdate: false);
        protected override void OnUpdateCumulativeTrade(CumulativeTrade trade) => Cum(trade, isUpdate: true);

        private void Cum(CumulativeTrade t, bool isUpdate)
        {
            var core = Core();
            if (core == null) return;
            _playStarted = true;
            long localId;
            lock (_cumIds)
            {
                if (!_cumIds.TryGetValue(t, out localId))
                {
                    localId = ++_cumIdSeq;
                    _cumIds[t] = localId;
                    if (_cumIds.Count > 100_000) _cumIds.Clear();
                }
            }
            int tickCount = t.Ticks?.Count ?? 0;
            core.Enqueue("cumtrades.ndjson", RecorderCore.J(
                ("seq", core.NextSeq()),
                ("cumId", localId),
                ("event", isUpdate ? "update" : "new"),
                ("tMarket", RecorderCore.Iso(t.Time)),
                ("tRecv", RecorderCore.Iso(DateTime.UtcNow)),
                ("firstPrice", t.FirstPrice),
                ("lastPrice", t.Lastprice),
                ("volume", t.Volume),
                ("tickCount", tickCount),
                ("direction", t.Direction.ToString())));
        }

        protected override void OnBestBidAskChanged(MarketDataArg depth)
        {
            var core = Core();
            if (core == null) return;
            core.Enqueue("bbo.ndjson", RecorderCore.J(
                ("seq", core.NextSeq()),
                ("tMarket", RecorderCore.Iso(depth.Time)),
                ("tRecv", RecorderCore.Iso(DateTime.UtcNow)),
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
            var core = Core();
            if (core == null) return;
            core.Enqueue("dom.ndjson", RecorderCore.J(
                ("seq", core.NextSeq()),
                ("tMarket", RecorderCore.Iso(d.Time)),
                ("tRecv", RecorderCore.Iso(DateTime.UtcNow)),
                ("side", d.IsBid ? "Bid" : d.IsAsk ? "Ask" : "Trade"),
                ("price", d.Price),
                ("volume", d.Volume),
                ("dataType", d.DataType.ToString())));
        }

        // ================= Geschlossene Bars + Footprint =================
        protected override void OnCalculate(int bar, decimal value)
        {
            var core = Core();
            if (core == null) return;
            if (!_metaWritten) WriteMeta(core);

            if (!_playStarted) { _lastClosedBar = CurrentBar; return; }

            int readable = CurrentBar - 1;   // ein Bar Puffer
            for (int i = _lastClosedBar + 1; i < readable; i++)
                FlushBar(core, i);
            if (readable - 1 > _lastClosedBar) _lastClosedBar = readable - 1;
        }

        private void FlushBar(RecorderCore core, int i)
        {
            IndicatorCandle c;
            try { c = GetCandle(i); } catch { return; }
            if (c == null) return;

            var sb = new StringBuilder();
            sb.Append('{');
            RecorderCore.Kv(sb, "seq", core.NextSeq()); sb.Append(',');
            RecorderCore.Kv(sb, "bar", i); sb.Append(',');
            RecorderCore.Kv(sb, "cbAtFlush", CurrentBar); sb.Append(',');
            RecorderCore.Kv(sb, "tMarket", RecorderCore.Iso(c.Time)); sb.Append(',');
            RecorderCore.Kv(sb, "tRecv", RecorderCore.Iso(DateTime.UtcNow)); sb.Append(',');
            RecorderCore.Kv(sb, "open", c.Open); sb.Append(',');
            RecorderCore.Kv(sb, "high", c.High); sb.Append(',');
            RecorderCore.Kv(sb, "low", c.Low); sb.Append(',');
            RecorderCore.Kv(sb, "close", c.Close); sb.Append(',');
            RecorderCore.Kv(sb, "volume", c.Volume); sb.Append(',');
            RecorderCore.Kv(sb, "delta", c.Delta); sb.Append(',');
            RecorderCore.Kv(sb, "minDelta", c.MinDelta); sb.Append(',');
            RecorderCore.Kv(sb, "maxDelta", c.MaxDelta); sb.Append(',');
            RecorderCore.Kv(sb, "trades", c.Ticks); sb.Append(',');
            RecorderCore.Kv(sb, "vwap", c.VWAP); sb.Append(',');
            try { RecorderCore.Kv(sb, "pocPrice", c.MaxVolumePriceInfo != null ? c.MaxVolumePriceInfo.Price : 0m); sb.Append(','); } catch { }

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
                        RecorderCore.Kv(sb, "price", p.Price); sb.Append(',');
                        RecorderCore.Kv(sb, "bid", p.Bid); sb.Append(',');
                        RecorderCore.Kv(sb, "ask", p.Ask); sb.Append(',');
                        RecorderCore.Kv(sb, "vol", p.Volume); sb.Append(',');
                        RecorderCore.Kv(sb, "ticks", p.Ticks); sb.Append(',');
                        RecorderCore.Kv(sb, "time", p.Time);
                        sb.Append('}');
                    }
                sb.Append(']');
            }
            else
            {
                sb.Length -= 1;   // trailing comma vermeiden
            }
            sb.Append('}');
            core.Enqueue("bars.ndjson", sb.ToString());
        }

        private void WriteMeta(RecorderCore core)
        {
            _metaWritten = true;
            decimal tick = 0m;
            try { tick = InstrumentInfo != null ? InstrumentInfo.TickSize : 0m; } catch { }
            var meta = RecorderCore.J(
                ("recorder", "OrderFlowReplayRecorder/proto-4-recheck"),
                ("runId", core.RunId),
                ("instanceId", core.InstanceId),
                ("instrument", SafeInstrument()),
                ("tickSize", tick),
                ("startWallUtc", RecorderCore.Iso(core.StartWallUtc)),
                ("note", "tMarket=ATAS-Replay-Marktzeit; tRecv=lokale Empfangs-Wanduhr (UTC). Lokale Seq != Anbieter-Vollstaendigkeit."),
                ("streams", "ticks.ndjson,cumtrades.ndjson,bbo.ndjson,dom.ndjson,bars.ndjson,ticks.csv"));
            core.Enqueue("meta.json", meta);
        }

        // ================= Stop / Summary (A3 im Core) =================
        protected override void OnDispose()
        {
            try { _gate.Dispose(); } catch { }   // terminal: finalisiert den Kern (falls erzeugt) und sperrt Neustart
            base.OnDispose();
        }

        // ================= Helfer =================
        private string SafeInstrument()
        {
            try { return InstrumentInfo != null && !string.IsNullOrEmpty(InstrumentInfo.Instrument) ? InstrumentInfo.Instrument : "UNKNOWN"; }
            catch { return "UNKNOWN"; }
        }

        private static string Aggressor(TradeDirection d) =>
            d == TradeDirection.Buy ? "Buy" : d == TradeDirection.Sell ? "Sell" : "Unknown";
    }
}
