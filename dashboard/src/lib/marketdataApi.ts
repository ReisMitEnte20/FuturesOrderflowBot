// Client der read-only Marktdaten-API (/api/marketdata): Live-Zustand aus ATAS (über die lokale Bridge) und Herkunft von
// Aufnahmen laut Datenqualitäts-Audit. Reine Hilfsfunktionen für Herkunft und Wertzustände liegen ebenfalls hier (getestet).
import { apiUrl } from "./apiBase.ts";

// ------------------------------------------------------------------------------------------------ Typen (Backend)

export interface LiveValue { value: number | null; state: string; source: string; tMarket?: string | null; note?: string | null; }
export interface LiveTrade { seq: number; tMarket: string; tRecv: string; price: number; volume: number; side: string; }
export interface LiveTape { id: number; event: string; tMarket: string; firstPrice: number; lastPrice: number; volume: number; ticks: number; side: string; updates: number; }
export interface LiveLevel { price: number; volume: number; }
export interface LiveFootprintLevel { price: number; bid: number; ask: number; }
export interface LiveCandle {
  tStart: string; tEnd: string; open: number; high: number; low: number; close: number; volume: number; buyVolume: number; sellVolume: number;
  delta: number; ticks: number; vwap: number | null; closed: boolean; footprint: LiveFootprintLevel[]; source: string; note?: string | null;
}
export interface LiveCompareRow { field: string; atas: number | null; dashboard: number | null; difference: number | null; note: string; }
export interface LiveComparison { bar: number; tStart: string; tEnd: string; status: string; rows: LiveCompareRow[]; }
export interface LiveCounters {
  linesReceived: number; processed: number; parseErrors: number; unknownTypes: number; backendDropped: number; bridgeDropped: number;
  bridgeDropsByStream: Record<string, number>; seqGaps: number; seqMissing: number; trades: number; bboUpdates: number; domUpdates: number;
  domSnapshots: number; cumNew: number; cumUpdates: number; bars: number; heartbeats: number;
}
export interface LiveBook { state: string; source: string; bidLevels: number; askLevels: number; bids: LiveLevel[]; asks: LiveLevel[]; lastUpdateTMarket: string | null; note: string; }
export interface IndicatorAccess { name: string; category: string; detail: string; }
export interface LiveSnapshot {
  generation: number; status: string; statusNote: string; source: string | null; sourceKind: string | null; instrument: string | null; exchange: string | null;
  tickSize: number | null; chartType: string | null; timeFrame: string | null; atasVersion: string | null; bridge: string | null; bridgeSessionId: string | null;
  connectedUtc: string | null; disconnectedUtc: string | null; lastMessageUtc: string | null; lastMarketTime: string | null; session: string; mode: string;
  modeEvidence: string; bid: LiveValue; ask: LiveValue; lastPrice: LiveValue; book: LiveBook; trades: LiveTrade[]; tape: LiveTape[]; tapeVolume: LiveValue;
  candles: LiveCandle[]; atasBars: LiveCandle[]; cvd: LiveValue; cvdAnchor: string; sessionVwap: LiveValue; vwapAnchor: string; counters: LiveCounters;
  comparisons: LiveComparison[]; indicators: IndicatorAccess[]; coverageBreaks: string[];
}
export interface LiveStateResponse { listener: { port: number; error: string | null }; state: LiveSnapshot; }

export interface RecordingProvenance {
  recordingId: string; auditFound: boolean; auditFile: string | null; replayType: string | null; replayTypeEvidence: string | null; banner: string;
  streams: Record<string, string>; evidence: Record<string, string>; tradesLabel: string | null; notes: string[];
}

const BASE = apiUrl("/api/marketdata");

async function getJson<T>(url: string, signal?: AbortSignal): Promise<T> {
  const r = await fetch(url, { headers: { Accept: "application/json" }, signal });
  if (!r.ok) throw new Error(`${url} -> HTTP ${r.status}`);
  return (await r.json()) as T;
}

export const marketdataApi = {
  liveState: (signal?: AbortSignal) => getJson<LiveStateResponse>(`${BASE}/live/state?trades=60&tape=40&candles=90`, signal),
  liveCompare: (signal?: AbortSignal) => getJson<LiveComparison[]>(`${BASE}/live/compare`, signal),
  provenance: (recordingId: string, signal?: AbortSignal) => getJson<RecordingProvenance>(`${BASE}/recordings/${encodeURIComponent(recordingId)}/provenance`, signal),
};

// ------------------------------------------------------------------------------------------------ Herkunft (4 Klassen)

/** Die vier Klassen, die auf einen Blick unterscheidbar sein sollen (+ Testdaten). */
export type ProvClass = "live" | "history" | "computed" | "unavailable" | "test";

export const PROV_CLASS: Record<ProvClass, { label: string; short: string; color: string }> = {
  live: { label: "Live von ATAS empfangen", short: "LIVE · ATAS", color: "#3cf0a0" },
  history: { label: "Aus einer historischen ATAS-Minutenkerze", short: "ATAS-Minutenhistorie", color: "#22b8f0" },
  computed: { label: "Im Replay generiert oder von uns berechnet", short: "generiert/berechnet", color: "#f0c35a" },
  unavailable: { label: "Mit diesem Feed nicht verfügbar oder ungeprüft", short: "nicht verfügbar/ungeprüft", color: "#8b9bb0" },
  test: { label: "TESTDATEN – kein Marktdatenfeed", short: "TESTDATEN", color: "#c77dff" },
};

/** Backend-Herkunft (live_atas/atas_chart/derived/unavailable/test) bzw. Audit-Herkunft → Anzeigeklasse. */
export function provClass(source: string | null | undefined): ProvClass {
  switch (source) {
    case "live_atas": case "atas_chart": return "live";
    case "derived": return "computed";
    case "test": return "test";
    case "historisch": return "history";
    default:
      if (source?.startsWith("Minutenaggregat")) return "history";
      if (source === "generiert") return "computed";
      return "unavailable";
  }
}

/** Detailtext je Backend-Herkunft (die Klasse „live“ unterscheidet Ereignis vs. Chartkerze). */
export function provDetail(source: string | null | undefined): string {
  switch (source) {
    case "live_atas": return "live von ATAS empfangen (SDK-Callback der Bridge)";
    case "atas_chart": return "direkt aus der laufenden ATAS-Chartinstanz gelesen (GetCandle)";
    case "derived": return "von uns aus den empfangenen Marktdaten berechnet";
    case "test": return "Testdaten (testfeed.py) – keine Marktdaten";
    default: return "Quelle ungeklärt / nicht verfügbar";
  }
}

// ------------------------------------------------------------------------------------------------ Wertzustände

const nf = new Intl.NumberFormat("de-DE", { maximumFractionDigits: 4 });

/** Anzeige eines Werts mit Zustand: echte Null bleibt „0“, fehlende Werte werden benannt statt als 0 gezeigt. */
export function valueText(v: LiveValue | null | undefined, digits?: number): string {
  if (!v) return "Quelle ungeklärt";
  const num = v.value == null ? null : digits != null ? v.value.toFixed(digits).replace(".", ",") : nf.format(v.value);
  switch (v.state) {
    case "wert": return num ?? "–";
    case "noch_kein_wert": return "noch kein Wert";
    case "veraltet": return num != null ? `${num} (veraltet)` : "veraltet";
    case "feed_getrennt": return num != null ? `${num} (Feed getrennt)` : "Feed getrennt";
    case "quelle_ungeklaert": return "Quelle ungeklärt";
    default: return num ?? v.state;
  }
}

export const STATUS_TEXT: Record<string, { text: string; color: string }> = {
  nie_verbunden: { text: "keine Bridge-Verbindung", color: "#8b9bb0" },
  verbunden: { text: "verbunden", color: "#3cf0a0" },
  veraltet: { text: "veraltet – keine Daten", color: "#f0c35a" },
  getrennt: { text: "getrennt", color: "#f0566a" },
};

export const BOOK_STATE_TEXT: Record<string, string> = {
  vollstaendig: "vollständig (Snapshot + Updates)",
  unvollstaendig: "UNVOLLSTÄNDIG – nur Updates, noch kein Snapshot",
  verworfen_getrennt: "verworfen – Feed getrennt",
  veraltet: "veraltet – nicht aktuell",
  kein_buch: "noch kein DOM empfangen",
};

// ------------------------------------------------------------------------------------------------ Kerzen → Chart

export interface SlotLike {
  label: string; range: string; status: "abgeschlossen" | "laufend" | null;
  candle: { bucket: number; firstIdx: number; lastIdx: number; open: number; high: number; low: number; close: number; volume: number; buyVolume: number; sellVolume: number; trades: number } | null;
}

const hm = (iso: string, tz: string) => new Intl.DateTimeFormat("de-DE", { timeZone: tz, hour: "2-digit", minute: "2-digit", hourCycle: "h23" }).format(Date.parse(iso));

/** Live-Minutenkerzen in Chart-Slots (gleicher Chart wie Aufzeichnung). Laufende Kerze = nicht abgeschlossen. */
export function candlesToSlots(candles: LiveCandle[], tz = "UTC"): SlotLike[] {
  return candles.map((c, i) => ({
    label: hm(c.tStart, tz), range: `${hm(c.tStart, tz)}–${hm(c.tEnd, tz)}`, status: c.closed ? "abgeschlossen" : "laufend",
    candle: { bucket: i, firstIdx: 0, lastIdx: 0, open: c.open, high: c.high, low: c.low, close: c.close, volume: c.volume, buyVolume: c.buyVolume, sellVolume: c.sellVolume, trades: c.ticks },
  }));
}

/** Delta/CVD je Slot; CVD beginnt beim ersten gezeigten Slot (Anker wird separat ausgewiesen). */
export function slotDeltaCvd(candles: LiveCandle[]): { delta: number[]; cvd: number[] } {
  let run = 0;
  const delta: number[] = [], cvd: number[] = [];
  for (const c of candles) { const d = c.buyVolume - c.sellVolume; delta.push(d); run += d; cvd.push(run); }
  return { delta, cvd };
}
