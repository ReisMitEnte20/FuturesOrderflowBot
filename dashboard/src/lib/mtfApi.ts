// Client des Multi-Timeframe-Laufs (5m / 1m / rangeXV) – /api/backtest/mtf. Plus reine Hilfen für den Trade-Rewind
// (sichtbarer Zustand zu einer Ereignisposition: nichts, was erst danach bekannt wurde).
import { apiUrl } from "./apiBase.ts";

export interface MtfRunRequest {
  dataset: "synthetic" | "recording"; seed: number; minutes: number; day: string | null; sessions: "London" | "NewYork" | "both";
  strategy: string; oneMinuteDeltaMin: number; rangePeriod: number; demoWithGenerated: boolean; initialBalance: number; closeOutMinutes: number;
}
export interface MtfSegmentInfo { id: string; session: string; day: string; windowStartUtc: string; windowEndUtc: string; recordingId: string | null; provenance: string; streams: Record<string, string>; drops: number | null; conflict: string | null; events: number; }
export interface MtfDataCheck {
  dataset: string; label: string; segments: MtfSegmentInfo[]; statements: { category: string; text: string }[];
  requirements: { need: string; availability: string; status: string; text: string }[]; runnable: boolean; requiresDemoFlag: boolean; blockers: string[]; rangeSemantics: string;
}
export interface RuleCheck { rule: string; role: string; view: string; barStatus: string; actual: string; expected: string; result: string; }
export interface BarSnap { view: string; index: number; start: string; open: number; high: number; low: number; close: number; volume: number; buyVolume: number; sellVolume: number; trades: number; status: string; closedAtEvent: number | null; delta?: number; }
export interface DecisionTrace {
  id: number; kind: string; eventIndex: number; marketTime: string; price: number; session: string; direction: string | null; signalId: string | null;
  strategy: string; strategyVersion: string; parameters: Record<string, string>;
  m5Open: BarSnap | null; m5Closed: BarSnap | null; m1Open: BarSnap | null; m1Closed: BarSnap | null; r4Open: BarSnap | null; r4Closed: BarSnap | null;
  context: { sessionVwap: number | null; vwapBasis: string; cvd: number; cvdAnchor: string; volumeProfile: Profile; tpoProfile: Profile; tpoClock: string; sessionHigh: number | null; sessionLow: number | null;
    lastSwingHigh: Swing | null; lastSwingLow: Swing | null; structureBreak: string | null; absorption: { side: string; priceLow: number; priceHigh: number; aggressiveVolume: number; progressTicks: number; definition: string } | null };
  checks: RuleCheck[]; summary: string; contextUsed: string[]; position: string;
}
export interface Profile { poc: number | null; vah: number | null; val: number | null; total: number; levels: number; }
export interface Swing { kind: string; price: number; extremeBarIndex: number; extremeBarStart: string; confirmedAtEvent: number; confirmedAt: string; }
export interface MtfTradeEvent { kind: string; eventIndex: number; timeUtc: string; price: number | null; text: string; }
export interface MtfTrade {
  index: number; session: string; side: string; qty: number; entryTimeUtc: string; entryPrice: number; exitTimeUtc: string; exitPrice: number; grossPnL: number; fees: number; netPnL: number;
  balanceAfter: number; entryTraceId: number | null; exitTraceId: number | null; signalEvent: number | null; entryFillEvent: number | null; exitSignalEvent: number | null; exitFillEvent: number | null;
  events: MtfTradeEvent[]; explanation: string; entrySignalId: string | null; entryOrderId: string | null; entryFillId: string | null; exitOrderId: string | null; exitFillId: string | null;
}
export interface MtfRunResult {
  runId: string; createdUtc: string; status: string; error: string | null; request: MtfRunRequest; dataCheck: MtfDataCheck | null; isDemonstration: boolean; runKind: string;
  strategy: string; strategyVersion: string; parameters: Record<string, string>; symbol: string; tickSize: number; pointValue: number; feePerSide: number; slippageTicks: number;
  rangeSemantics: string; processingOrder: string; events: number; trades: MtfTrade[]; traces: DecisionTrace[]; transitions: { eventIndex: number; marketTime: string; from: string; to: string; avgPrice: number; tickPrice: number }[];
  startBalance: number; endBalance: number; netPnL: number; grossPnL: number; fees: number; maxDrawdown: number; finalPosition: string; openValuation: number; fingerprint: string; elapsedSeconds: number; notes: string[];
}
export interface MtfRunIndexEntry { runId: string; createdUtc: string; status: string; runKind: string; strategy: string; trades: number; netPnL: number; isDemonstration: boolean; dataset: string | null; }
export interface MtfReplayWindow {
  runId: string; trade: number; fromUtc: string; toUtc: string; fromEvent: number; toEvent: number; reproduced: boolean; firstDifference: string | null;
  events: [number, string, number, number, number][]; states: any[][]; closedBars: Record<string, any[][]>; traces: DecisionTrace[];
  transitions: { eventIndex: number; marketTime: string; from: string; to: string; avgPrice: number; tickPrice: number }[]; tradeInfo: MtfTrade; stateLegend: string; barLegend: string;
}

const BASE = apiUrl("/api/backtest/mtf");
async function j<T>(r: Response, what: string): Promise<T> {
  if (!r.ok) { let m = `${what} -> HTTP ${r.status}`; try { const e = await r.json(); if (e?.error) m += `: ${e.error}`; } catch { /* */ } throw new Error(m); }
  return (await r.json()) as T;
}
const post = (u: string, b: unknown, s?: AbortSignal) => fetch(u, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(b), signal: s });
export const mtfApi = {
  check: async (r: MtfRunRequest, s?: AbortSignal) => j<MtfDataCheck>(await post(`${BASE}/check`, r, s), "Datenprüfung"),
  run: async (r: MtfRunRequest) => j<MtfRunResult>(await post(`${BASE}/run`, r), "Lauf"),
  runs: async () => j<MtfRunIndexEntry[]>(await fetch(`${BASE}/runs`), "Läufe"),
  get: async (id: string) => j<MtfRunResult>(await fetch(`${BASE}/runs/${encodeURIComponent(id)}`), "Lauf"),
  replay: async (id: string, trade: number, preroll: number, postroll: number, s?: AbortSignal) =>
    j<MtfReplayWindow>(await fetch(`${BASE}/runs/${encodeURIComponent(id)}/replay?trade=${trade}&preroll=${preroll}&postroll=${postroll}`, { signal: s }), "Replay"),
};

// ------------------------------------------------------------------------------------------------ Rewind-Logik (rein)

export type ViewKey = "rangeXV" | "1m" | "5m";
/** Index im Zustandsarray je Sicht. */
const VIEW_COL: Record<ViewKey, number> = { "5m": 1, "1m": 2, rangeXV: 3 };

export interface VisibleBar { index: number; open: number; high: number; low: number; close: number; volume: number; buy: number; sell: number; status: "geschlossen" | "offen"; }

/** Bars einer Sicht, wie sie bei Ereignis `cursor` sichtbar waren: geschlossene Bars nur, wenn ihr Abschluss ≤ cursor bekannt
 *  war; dazu die offene Bar mit ihren BISHERIGEN Werten aus dem Zustand bei `cursor`. Nichts aus der Zukunft. */
export function visibleBars(w: Pick<MtfReplayWindow, "closedBars" | "states">, view: ViewKey, cursor: number): VisibleBar[] {
  const closed = (w.closedBars[view] ?? []).filter((b) => b[10] <= cursor && b[9] <= cursor)
    .map((b): VisibleBar => ({ index: b[0], open: b[2], high: b[3], low: b[4], close: b[5], volume: b[6], buy: b[7], sell: b[8], status: "geschlossen" }));
  const st = stateAt(w, cursor);
  const cur = st?.[VIEW_COL[view]] as number[] | undefined;
  if (cur && cur.length) {
    const open: VisibleBar = { index: cur[0], open: cur[1], high: cur[2], low: cur[3], close: cur[4], volume: cur[5], buy: cur[6], sell: cur[7], status: "offen" };
    return [...closed.filter((b) => b.index !== open.index), open];
  }
  return closed;
}

/** Zustand (Zeile aus states) für die letzte Ereignisposition ≤ cursor. */
export function stateAt(w: Pick<MtfReplayWindow, "states">, cursor: number): any[] | null {
  let lo = 0, hi = w.states.length - 1, best = -1;
  while (lo <= hi) { const m = (lo + hi) >> 1; if (w.states[m][0] <= cursor) { best = m; lo = m + 1; } else hi = m - 1; }
  return best >= 0 ? w.states[best] : null;
}

/** Was bei `cursor` bereits bekannt war (Marker, Entscheidungen, Fills); spätere bleiben verborgen. */
export function knownAt<T extends { eventIndex: number }>(items: T[], cursor: number): T[] { return items.filter((x) => x.eventIndex >= 0 && x.eventIndex <= cursor); }

/** Kennzahlen aus Trades (geschlossene Trades; offene Endbewertung getrennt). */
export function tradeStats(trades: { netPnL: number }[]) {
  const wins = trades.filter((t) => t.netPnL > 0), losses = trades.filter((t) => t.netPnL < 0);
  const gw = wins.reduce((a, t) => a + t.netPnL, 0), gl = -losses.reduce((a, t) => a + t.netPnL, 0);
  return { trades: trades.length, winRate: trades.length ? wins.length / trades.length : null, profitFactor: gl > 0 ? gw / gl : null, net: trades.reduce((a, t) => a + t.netPnL, 0) };
}
