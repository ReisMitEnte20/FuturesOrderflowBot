// Reine Hilfsfunktionen für Übersicht vs. Bar-Replay (keine Handelslogik, keine Neuberechnung von
// Ergebnissen): entscheidet nur, welche von der Engine gelieferten Daten zu einer Replay-Position
// bereits bekannt sind. pos = Index der zuletzt VOLLSTÄNDIG bekannten Kerze.
import type { Candle, EquityPoint, OhlcTrade } from "./backtestApi";

export type TradeState = "future" | "open" | "closed";

/** Zustand eines Trades zur Replay-Position: Entry am OPEN von entryBarIndex, Exit bekannt ab exitBarIndex. */
export function tradeStateAt(t: Pick<OhlcTrade, "entryBarIndex" | "exitBarIndex">, pos: number): TradeState {
  if (t.entryBarIndex > pos) return "future";
  if (t.exitBarIndex > pos) return "open";
  return "closed";
}

/** Für den Chart aufbereiteter Trade; exit = null, solange der Exit (im Replay) noch nicht bekannt ist. */
export interface ChartTrade {
  index: number;
  side: string;
  entryBarIndex: number;
  entryPrice: number;
  exit: { barIndex: number; price: number; reason: string; ambiguous: boolean } | null;
  stopLossPrice: number;
  takeProfitPrice: number;
}

/**
 * Sichtbare Trades für den Chart. pos = null → Übersicht (alles bekannt). Im Replay nur bereits
 * eröffnete Trades, Exit-Daten nur wenn die Exit-Kerze schon vollständig bekannt ist.
 */
export function chartTradesAt(trades: OhlcTrade[], pos: number | null): ChartTrade[] {
  const out: ChartTrade[] = [];
  trades.forEach((t, index) => {
    const st: TradeState = pos == null ? "closed" : tradeStateAt(t, pos);
    if (st === "future") return;
    out.push({
      index,
      side: t.side,
      entryBarIndex: t.entryBarIndex,
      entryPrice: t.entryPrice,
      exit: st === "closed" ? { barIndex: t.exitBarIndex, price: t.exitPrice, reason: t.exitReason, ambiguous: t.ambiguous } : null,
      stopLossPrice: t.stopLossPrice,
      takeProfitPrice: t.takeProfitPrice,
    });
  });
  return out;
}

/** Equity-Punkt der Engine für Bar pos (letzter Punkt mit barIndex ≤ pos) — keine Neuberechnung. */
export function equityAt(equity: EquityPoint[], pos: number): EquityPoint | null {
  let lo = 0, hi = equity.length - 1;
  let best: EquityPoint | null = null;
  while (lo <= hi) {
    const mid = (lo + hi) >> 1;
    if (equity[mid].barIndex <= pos) { best = equity[mid]; lo = mid + 1; } else hi = mid - 1;
  }
  return best;
}

export function clampPos(pos: number, count: number): number {
  return count <= 0 ? 0 : Math.min(count - 1, Math.max(0, Math.round(pos)));
}

export interface IndexedTrade { trade: OhlcTrade; index: number; }

export interface ReplaySnapshot {
  pos: number;
  candle: Candle | null;
  /** Bis pos geschlossene Trades (in Originalreihenfolge). */
  closed: IndexedTrade[];
  /** Zur Position offener Trade (Entry bekannt, Exit noch nicht). */
  open: IndexedTrade | null;
  /** Realisierter NetPnL laut Engine-Equity-Punkt bis pos. */
  realizedNetPnL: number;
  /** Mark-to-Market der offenen Position laut Engine (nur Anzeige). */
  openPnL: number;
  wins: number;
  losses: number;
  status: "FLAT" | "LONG" | "SHORT";
}

/** Alles, was zur Replay-Position pos bekannt ist — ohne Zukunftsdaten. */
export function replaySnapshot(candles: Candle[], trades: OhlcTrade[], equity: EquityPoint[], pos: number): ReplaySnapshot {
  const p = clampPos(pos, candles.length);
  const closed: IndexedTrade[] = [];
  let open: IndexedTrade | null = null;
  trades.forEach((trade, index) => {
    const st = tradeStateAt(trade, p);
    if (st === "closed") closed.push({ trade, index });
    else if (st === "open") open = { trade, index };
  });
  const eq = equityAt(equity, p);
  const openTrade = open as IndexedTrade | null;
  return {
    pos: p,
    candle: candles.length ? candles[p] : null,
    closed,
    open: openTrade,
    realizedNetPnL: eq?.realizedNetPnL ?? 0,
    openPnL: openTrade && eq ? eq.openPnL ?? 0 : 0,
    wins: closed.filter((c) => c.trade.netPnL > 0).length,
    losses: closed.filter((c) => c.trade.netPnL < 0).length,
    status: openTrade ? (openTrade.trade.side === "Long" ? "LONG" : "SHORT") : "FLAT",
  };
}

/** Vorheriger/nächster Trade relativ zur Auswahl (ohne Auswahl: erster bzw. letzter). */
export function relativeTradeIndex(selected: number | null, delta: number, count: number): number | null {
  if (count <= 0) return null;
  const base = selected == null ? (delta > 0 ? -1 : count) : selected;
  return Math.min(count - 1, Math.max(0, base + delta));
}

/** Zoom-Fenster für einen Trade: gesamter Trade plus Kontext davor/danach. */
export function tradeWindow(entryBarIndex: number, exitBarIndex: number, count: number): { start: number; end: number } {
  const lo = Math.min(entryBarIndex, exitBarIndex);
  const hi = Math.max(entryBarIndex, exitBarIndex);
  const pad = Math.max(4, Math.round((hi - lo) * 0.4) + 4);
  return { start: Math.max(0, lo - pad), end: Math.min(count - 1, hi + pad) };
}
