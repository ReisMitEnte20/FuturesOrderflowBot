// Gemeinsame typisierte Datenmodelle + abgeleitete Indikatoren fuer die Orderflow-Arbeitsflaeche.
// Datenbasis: aufgenommene ATAS-Replay-Trades (research-workspace.json), gespeicherte Simulationsergebnisse.
// Rueckblickende finale Werte sind als solche gekennzeichnet; ein Snapshot "zum Zeitpunkt t" enthaelt KEINE
// spaeteren Trades/Profile/finalen Kerzeninformationen. Fehlende Daten => "nicht verfuegbar", nie erfunden.

export interface WsCandle { t: number; o: number; h: number; l: number; c: number; v: number; delta: number; }
export interface WsFpLevel { p: number; b: number; a: number; }
export type WsTapeRow = [number, number, number, number]; // [t_ms, price, vol, dir(1=Buy/Ask,-1=Sell/Bid,0=unbekannt)]
export interface WsTrade {
  index: number; side: string; qty: number; entryBarIndex: number; exitBarIndex: number;
  entryPrice: number; exitPrice: number; exitReason: string; stopLossPrice: number; takeProfitPrice: number;
  ambiguous: boolean; netPnL: number; entryTimeMs: number; exitTimeMs: number; entryTime: string; exitTime: string;
}
export interface WsStrategy {
  name: string; usesOrderflow: boolean; netPnL: number; fees: number; finalEquity: number; trades: WsTrade[];
  // Optional (nur im echten Orderflow-Lauf): Laufende-Diagnostik.
  status?: string; openPositionSide?: string; openPositionQty?: number; openPositionAvgPrice?: number;
  openPositionUnrealizedGross?: number; signalsGenerated?: number; ordersSubmitted?: number;
  ordersRejectedByRisk?: number; ordersRejectedByBroker?: number; ordersUnfilledAtEnd?: number;
  // Vollstaendiges Endergebnis (realisiert / unrealisiert / Gebuehren / End-Equity getrennt):
  realizedNetPnL?: number; realizedFees?: number; openPositionEntryFees?: number; totalFeesPaid?: number;
  unrealizedGrossAtLastTick?: number; lastTickPrice?: number; endEquityRealized?: number; endEquityMarkToMarket?: number;
}

/** Eintrag der read-only Lauf-Liste (dashboard/public/orderflow-runs/index.json). */
export interface WsRunIndexStrategy { name: string; trades: number; status?: string; realizedNetPnL: number; endEquityMarkToMarket: number; openPositionSide: string; }
export interface WsRunIndexEntry {
  id: string; generatedUtc: string; instrument: string; dataSource: string;
  ticks: number; fullBars: number; status: string; ok: boolean; legacy?: boolean; loadable?: boolean; note?: string;
  strategies: WsRunIndexStrategy[];
}
export interface WsProvenance {
  dataFilePath: string; dataSha256: string; ticks: number; fullBars: number; remainderTicks: number;
  ticksPerBar: number; deltaThreshold: number; startingBalance: number;
  capabilities: string; executionModel: string; divergencesFromHarness: string[]; caveats: string[];
  settingsUsed?: Record<string, string>;
}
export interface WorkspaceData {
  meta: { instrument: string; dataSource: string; marketStartMs: number; marketEndMs: number; ticksPerBar: number; pointValue: number; tickSizeNote: string; feePerSide: number; slippageTicks: number; generatedUtc: string; };
  availability: Record<string, string>;
  quality: { priceGranularity: string; drops: number; aggressor: string; footprintSource: string; domNote: string; };
  candles: WsCandle[]; footprint: WsFpLevel[][]; tape: WsTapeRow[]; strategies: WsStrategy[];
  provenance?: WsProvenance;
}

export const isAvailable = (s: string | undefined) => !!s && !/nicht verfuegbar|nicht verfügbar/i.test(s);

/** Cumulative Volume Delta ab Anker-Bar (Default 0 = Aufnahmestart). Gibt je Bar den CVD-Stand. */
export function cvdSeries(candles: WsCandle[], anchorBar = 0): { cvd: number[]; anchorBar: number; anchorTime: number } {
  const cvd: number[] = [];
  let run = 0;
  for (let i = 0; i < candles.length; i++) { if (i >= anchorBar) run += candles[i].delta; cvd.push(i >= anchorBar ? run : 0); }
  return { cvd, anchorBar, anchorTime: candles[anchorBar]?.t ?? candles[0]?.t ?? 0 };
}

/** Laufender VWAP (Preis x Volumen) ab Anker-Bar, je Bar. Footprint liefert Volumen je Preis. */
export function vwapSeries(candles: WsCandle[], footprint: WsFpLevel[][], anchorBar = 0): { vwap: (number | null)[]; anchorBar: number; anchorTime: number } {
  const out: (number | null)[] = [];
  let pv = 0, vol = 0;
  for (let i = 0; i < candles.length; i++) {
    if (i >= anchorBar) for (const l of footprint[i] ?? []) { const v = l.b + l.a; pv += l.p * v; vol += v; }
    out.push(i >= anchorBar && vol > 0 ? pv / vol : null);
  }
  return { vwap: out, anchorBar, anchorTime: candles[anchorBar]?.t ?? 0 };
}

export interface VolumeProfile {
  levels: { p: number; vol: number; bid: number; ask: number }[];
  poc: number; vah: number; val: number; totalVol: number; fromBar: number; toBar: number; valueAreaPct: number;
}
/** Volume Profile ueber [fromBar..toBar] mit POC + Value Area (Default 70 %). */
export function volumeProfile(footprint: WsFpLevel[][], fromBar: number, toBar: number, valueAreaPct = 0.7): VolumeProfile {
  const agg = new Map<number, { bid: number; ask: number }>();
  for (let i = Math.max(0, fromBar); i <= Math.min(footprint.length - 1, toBar); i++)
    for (const l of footprint[i] ?? []) { const c = agg.get(l.p) ?? { bid: 0, ask: 0 }; c.bid += l.b; c.ask += l.a; agg.set(l.p, c); }
  const levels = [...agg.entries()].map(([p, v]) => ({ p, vol: v.bid + v.ask, bid: v.bid, ask: v.ask })).sort((a, b) => a.p - b.p);
  const total = levels.reduce((s, l) => s + l.vol, 0);
  if (levels.length === 0) return { levels, poc: 0, vah: 0, val: 0, totalVol: 0, fromBar, toBar, valueAreaPct };
  let pocIdx = 0; levels.forEach((l, i) => { if (l.vol > levels[pocIdx].vol) pocIdx = i; });
  // Value Area: vom POC nach aussen den jeweils volumenstaerkeren Nachbarn aufnehmen bis >= valueAreaPct.
  let lo = pocIdx, hi = pocIdx, acc = levels[pocIdx].vol;
  const target = total * valueAreaPct;
  while (acc < target && (lo > 0 || hi < levels.length - 1)) {
    const below = lo > 0 ? levels[lo - 1].vol : -1;
    const above = hi < levels.length - 1 ? levels[hi + 1].vol : -1;
    if (above >= below) { hi++; acc += levels[hi].vol; } else { lo--; acc += levels[lo].vol; }
  }
  return { levels, poc: levels[pocIdx].p, vah: levels[hi].p, val: levels[lo].p, totalVol: total, fromBar, toBar, valueAreaPct };
}

export interface BarPriceOccupancy { levels: { p: number; count: number }[]; poc: number; vah: number; val: number; fromBar: number; toBar: number; unit: string; }
/** Bar-Preis-Belegung: Anzahl der (500-Tick-)Bars, deren [low..high] einen Preis beruehrt.
 *  KEIN TPO / Zeit-am-Preis: Tick-Bars sind keine festen Zeitintervalle, daher wird Bar-Belegung je Preis
 *  gezaehlt, nicht Zeit. Einheit = 1 Bar. */
export function barPriceOccupancy(candles: WsCandle[], fromBar: number, toBar: number, unit = "1 Bar"): BarPriceOccupancy {
  const count = new Map<number, number>();
  for (let i = Math.max(0, fromBar); i <= Math.min(candles.length - 1, toBar); i++) {
    const c = candles[i];
    for (let p = Math.floor(c.l); p <= Math.ceil(c.h); p++) count.set(p, (count.get(p) ?? 0) + 1);
  }
  const levels = [...count.entries()].map(([p, c]) => ({ p, count: c })).sort((a, b) => a.p - b.p);
  if (!levels.length) return { levels, poc: 0, vah: 0, val: 0, fromBar, toBar, unit };
  let pocIdx = 0; levels.forEach((l, i) => { if (l.count > levels[pocIdx].count) pocIdx = i; });
  const total = levels.reduce((s, l) => s + l.count, 0);
  let lo = pocIdx, hi = pocIdx, acc = levels[pocIdx].count;
  while (acc < total * 0.7 && (lo > 0 || hi < levels.length - 1)) {
    const below = lo > 0 ? levels[lo - 1].count : -1, above = hi < levels.length - 1 ? levels[hi + 1].count : -1;
    if (above >= below) { hi++; acc += levels[hi].count; } else { lo--; acc += levels[lo].count; }
  }
  return { levels, poc: levels[pocIdx].p, vah: levels[hi].p, val: levels[lo].p, fromBar, toBar, unit };
}

/** Grosse Trades (Bubbles) aus dem Tape ab Schwellenvolumen, innerhalb [t0..t1] (optional). */
export function tradeBubbles(tape: WsTapeRow[], minVol: number, t0?: number, t1?: number) {
  const out: { t: number; p: number; v: number; dir: number }[] = [];
  for (const r of tape) { if (r[2] < minVol) continue; if (t0 != null && r[0] < t0) continue; if (t1 != null && r[0] > t1) continue; out.push({ t: r[0], p: r[1], v: r[2], dir: r[3] }); }
  return out;
}

/** Bar-Index, dessen Schluss <= t ist (letzte VOLLSTAENDIG bekannte Kerze zum Zeitpunkt t). */
export function barAt(candles: WsCandle[], t: number): number {
  let idx = -1;
  for (let i = 0; i < candles.length; i++) { const close = candles[i + 1]?.t ?? candles[i].t; if (close <= t) idx = i; else break; }
  return idx;
}

export interface AnalysisSnapshot {
  instrument: string; dataSource: string; strategyName: string; generatedUtc: string;
  pointInTime: string | null; marketTimeMs: number | null;
  availableData: Record<string, string>; qualityStatus: WorkspaceData["quality"];
  indicatorParams: { vwapAnchorBar: number; cvdAnchorBar: number; vpRange: [number, number]; barOccupancyUnit: string; bubbleMinVol: number; valueAreaPct: number };
  anchors: { vwapAnchorTimeMs: number; cvdAnchorTimeMs: number };
  values: { vwap: number | null; cvd: number | null; poc: number; vah: number; val: number; barOccupancyPoc: number } | null;
  selectedTrade: SnapshotTrade | null;
  retrospective: boolean;
  note: string;
}

/** Trade auf den Wissensstand zum Zeitpunkt t reduziert. Entry-Daten sind bekannt, Exit/PnL nur wenn
 *  zum Zeitpunkt t bereits eingetreten; sonst exitKnown=false und Exit/PnL-Felder null (Trade noch offen). */
export interface SnapshotTrade {
  index: number; side: string; qty: number;
  entryBarIndex: number; entryPrice: number; entryTimeMs: number; entryTime: string;
  stopLossPrice: number; takeProfitPrice: number;
  exitKnown: boolean;
  exitBarIndex: number | null; exitPrice: number | null; exitReason: string | null;
  exitTimeMs: number | null; exitTime: string | null; netPnL: number | null; ambiguous: boolean | null;
}

/** Reduziert einen Trade auf den Wissensstand zum Zeitpunkt t (pit).
 *  pit==null => voller (rueckblickender) Trade. entry > t => null (noch nicht eroeffnet).
 *  exit > t  => Exit/PnL unbekannt (redigiert). Gleichstand (Zeitstempel == t) gilt als bereits bekannt. */
export function pointInTimeTrade(t: WsTrade | null, pit: number | null): SnapshotTrade | null {
  if (!t) return null;
  if (pit != null && t.entryTimeMs > pit) return null; // Entry liegt in der Zukunft => Trade noch nicht sichtbar
  const exitKnown = pit == null || t.exitTimeMs <= pit;
  return {
    index: t.index, side: t.side, qty: t.qty,
    entryBarIndex: t.entryBarIndex, entryPrice: t.entryPrice, entryTimeMs: t.entryTimeMs, entryTime: t.entryTime,
    stopLossPrice: t.stopLossPrice, takeProfitPrice: t.takeProfitPrice,
    exitKnown,
    exitBarIndex: exitKnown ? t.exitBarIndex : null,
    exitPrice: exitKnown ? t.exitPrice : null,
    exitReason: exitKnown ? t.exitReason : null,
    exitTimeMs: exitKnown ? t.exitTimeMs : null,
    exitTime: exitKnown ? t.exitTime : null,
    netPnL: exitKnown ? t.netPnL : null,
    ambiguous: exitKnown ? t.ambiguous : null,
  };
}

/** Baut einen maschinenlesbaren Analyse-Snapshot. pointInTimeMs gesetzt => keine spaeteren Trades/Exits/Profile/finalen Kerzen.
 *  Alle Werte werden nur aus Bars/Trades bis zum Zeitpunkt t abgeleitet (letzte VOLLSTAENDIGE Kerze via barAt). */
export function buildSnapshot(d: WorkspaceData, stratIdx: number, trade: WsTrade | null, opts: { vwapAnchorBar: number; cvdAnchorBar: number; bubbleMinVol: number; pointInTimeMs?: number | null }): AnalysisSnapshot {
  const pit = opts.pointInTimeMs ?? null;
  const lastBar = pit != null ? barAt(d.candles, pit) : d.candles.length - 1;
  const vp = lastBar >= 0 ? volumeProfile(d.footprint, 0, lastBar) : null;
  const barOcc = lastBar >= 0 ? barPriceOccupancy(d.candles, 0, lastBar) : null;
  const vwap = lastBar >= 0 ? vwapSeries(d.candles, d.footprint, opts.vwapAnchorBar).vwap[lastBar] ?? null : null;
  const cvd = lastBar >= 0 ? cvdSeries(d.candles, opts.cvdAnchorBar).cvd[lastBar] ?? null : null;
  const safeTrade = pointInTimeTrade(trade, pit);
  return {
    instrument: d.meta.instrument, dataSource: d.meta.dataSource, strategyName: d.strategies[stratIdx]?.name ?? "", generatedUtc: new Date().toISOString(),
    pointInTime: pit != null ? new Date(pit).toISOString() : null,
    marketTimeMs: pit ?? (trade ? trade.exitTimeMs : d.meta.marketEndMs),
    availableData: d.availability, qualityStatus: d.quality,
    indicatorParams: { vwapAnchorBar: opts.vwapAnchorBar, cvdAnchorBar: opts.cvdAnchorBar, vpRange: [0, Math.max(0, lastBar)], barOccupancyUnit: `1 Bar = ${d.meta.ticksPerBar} Ticks`, bubbleMinVol: opts.bubbleMinVol, valueAreaPct: 0.7 },
    anchors: { vwapAnchorTimeMs: d.candles[opts.vwapAnchorBar]?.t ?? 0, cvdAnchorTimeMs: d.candles[opts.cvdAnchorBar]?.t ?? 0 },
    values: lastBar >= 0 && vp ? { vwap, cvd, poc: vp.poc, vah: vp.vah, val: vp.val, barOccupancyPoc: barOcc?.poc ?? 0 } : null,
    selectedTrade: safeTrade,
    retrospective: pit == null,
    note: pit != null ? "Snapshot zum Zeitpunkt t: nur Daten bis zur letzten vollstaendigen Kerze; keine spaeteren Trades/Exits/Profile." : "Rueckblickende finale Werte (gesamter Aufnahmeabschnitt).",
  };
}
