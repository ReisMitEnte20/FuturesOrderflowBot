// Gemeinsame Ansicht „Chart & Replay“: reine Logik ohne UI (direkt mit `node --test` prüfbar).
//
//  - Gespeicherter Lauf → Trades im Chart: Marker kommen ausschließlich aus den im Bericht gespeicherten simulierten
//    Fills (Zeit, Preis, Richtung, Menge). Nichts wird neu simuliert. Offene (am Datenende nur bewertete) Positionen
//    bekommen KEINEN Ausstieg. Signalzeit ≠ Fill-Zeit: beide getrennt geführt.
//  - Kerzen stammen aus der eigenen Datenquelle des Laufs (candleRequest); passt eine gespeicherte Fill-Zeit nicht zur
//    geladenen Kerze, wird das ehrlich gemeldet (Originalquelle nicht eindeutig) und der Marker nicht gezeichnet.
//  - Tick-Replay: Footprint/Delta/CVD aus den bis zur Replay-Position angewandten Ticks; Überlagerung gespeicherter
//    Fills erst, wenn die Replay-Uhr sie erreicht (konservativ, nie zu früh).

import type { Candle } from "./backtestApi.ts";
import type { ChartTrade } from "./replay.ts";
import type { TickStore, ReplayCandle } from "./tickReplay.ts";
import { countAtOrBefore } from "./tickReplay.ts";

/** Im Bericht gespeicherter Trade (Teilmenge von TradeRow). Zeiten in ms seit Epoch (UTC). */
export interface StoredTrade {
  index: number; side: string; entryT: number; exitT: number; entryPrice: number; exitPrice: number;
  entryBar: number; exitBar: number; exitReason: string; ambiguous: boolean; closedAtEnd: boolean; session: string;
  net: number; stopLoss: number; takeProfit: number;
  entrySignalUtc?: string | null; entrySignalDirection?: string | null; exitSignalUtc?: string | null;
  entryTimeUtc?: string | null; exitTimeUtc?: string | null;
}
export interface SessionSpan { key: string; firstT: number; lastT: number; }

/** Kerzenbereich [i0, i1] (inklusiv) einer Session (Kerzenstart zwischen firstT und lastT). null = keine Kerze. */
export function sessionRange(candles: Candle[], s: SessionSpan): { i0: number; i1: number } | null {
  let lo = 0, hi = candles.length;
  while (lo < hi) { const m = (lo + hi) >>> 1; if (candles[m].t < s.firstT) lo = m + 1; else hi = m; }
  const i0 = lo;
  let j = i0;
  while (j < candles.length && candles[j].t <= s.lastT) j++;
  return j > i0 ? { i0, i1: j - 1 } : null;
}

/** Liegt die Fill-Zeit in der Kerze barIndex? Einstieg: [t, nächste t) (OHLC-Engine füllt am OPEN).
 *  Ausstieg (exit = true) bei Zeitkerzen: [t, t + Zeitrahmen] – die OHLC-Engine stempelt Ausstiege innerhalb der Kerze
 *  (Stop/Ziel, Session-Schluss, Datenende) auf den KERZENSCHLUSS, weil die Intrabar-Zeit aus OHLC unbekannt ist. */
export function fillInBar(candles: Candle[], barIndex: number, fillMs: number, tfMs: number | null, exit = false): boolean {
  const c = candles[barIndex];
  if (!c) return false;
  if (exit && tfMs) return fillMs >= c.t && fillMs <= c.t + tfMs;
  const next = candles[barIndex + 1]?.t ?? (tfMs ? c.t + tfMs : Infinity);
  return fillMs >= c.t && fillMs < next;
}

export type TradeChartStatus = "ok" | "open" | "mismatch" | "noSession" | "otherDay";
export interface DayTrade { trade: StoredTrade; status: TradeChartStatus; chart: ChartTrade | null; why: string | null; }

/**
 * Trades eines Tages als Chart-Marker (Bar-Indizes relativ zum Tagesausschnitt). Exit nur für geschlossene Trades,
 * deren Ausstieg im selben Tagesausschnitt liegt. Jeder Marker wird gegen die geladenen Kerzen geprüft.
 */
export function dayTrades(candles: Candle[], trades: StoredTrade[], session: SessionSpan, tfMs: number | null): DayTrade[] {
  const r = sessionRange(candles, session);
  const out: DayTrade[] = [];
  for (const t of trades) {
    if (t.session !== session.key) continue;
    if (!r) { out.push({ trade: t, status: "noSession", chart: null, why: "Für diesen Tag wurden keine Kerzen geladen." }); continue; }
    const entryOk = t.entryBar >= r.i0 && t.entryBar <= r.i1 && fillInBar(candles, t.entryBar, t.entryT, tfMs);
    if (!entryOk) { out.push({ trade: t, status: "mismatch", chart: null, why: "Einstiegs-Fill passt nicht zur geladenen Kerze (Originalquelle nicht eindeutig)." }); continue; }
    let exit: ChartTrade["exit"] = null;
    let status: TradeChartStatus = "ok";
    let why: string | null = null;
    if (t.closedAtEnd) { status = "open"; why = "Am Datenende offen – nur bewertet, kein Ausstieg."; }
    else if (t.exitBar >= r.i0 && t.exitBar <= r.i1) {
      if (!fillInBar(candles, t.exitBar, t.exitT, tfMs, true)) { out.push({ trade: t, status: "mismatch", chart: null, why: "Ausstiegs-Fill passt nicht zur geladenen Kerze (Originalquelle nicht eindeutig)." }); continue; }
      exit = { barIndex: t.exitBar - r.i0, price: t.exitPrice, reason: t.exitReason, ambiguous: t.ambiguous };
    } else { status = "otherDay"; why = "Ausstieg liegt an einem anderen Tag."; }
    out.push({ trade: t, status, why, chart: {
      index: t.index, side: t.side, entryBarIndex: t.entryBar - r.i0, entryPrice: t.entryPrice, exit,
      stopLossPrice: t.stopLoss, takeProfitPrice: t.takeProfit,
    } });
  }
  return out;
}

/** Replay im Tagesausschnitt: nur bis zur Position pos (letzte vollständig bekannte Kerze, relativ) bekannte Marker. */
export function dayTradesAt(day: DayTrade[], pos: number): ChartTrade[] {
  const out: ChartTrade[] = [];
  for (const d of day) {
    const c = d.chart;
    if (!c || c.entryBarIndex > pos) continue;
    out.push({ ...c, exit: c.exit && c.exit.barIndex <= pos ? c.exit : null });
  }
  return out;
}

/** Erreichbarkeit aller GESCHLOSSENEN Trades im Chart (Ein- und Ausstieg markierbar). */
export function reachability(candles: Candle[], trades: StoredTrade[], sessions: SessionSpan[], tfMs: number | null) {
  const closed = trades.filter((t) => !t.closedAtEnd);
  const bySession = new Map(sessions.map((s) => [s.key, s]));
  let reachable = 0;
  const problems: { index: number; why: string }[] = [];
  for (const t of closed) {
    const s = bySession.get(t.session);
    if (!s) { problems.push({ index: t.index, why: `Session ${t.session} fehlt im Bericht` }); continue; }
    const d = dayTrades(candles, [t], s, tfMs)[0];
    if (d.status === "ok" || d.status === "otherDay") reachable++;
    else problems.push({ index: t.index, why: d.why ?? d.status });
  }
  return { total: trades.length, closed: closed.length, open: trades.length - closed.length, reachable, problems };
}

// ------------------------------------------------------------------------------------------------------------
// Tick-Replay: Footprint, Delta, CVD, Überlagerung gespeicherter Fills
// ------------------------------------------------------------------------------------------------------------

export interface FootprintLevel { price: number; bid: number; ask: number; trades: number; }

/** Footprint einer Kerze aus den Ticks [from, toExcl): bid = Verkäufer-Aggressor, ask = Käufer-Aggressor. Absteigend nach Preis. */
export function footprint(store: Pick<TickStore, "price" | "vol" | "side">, from: number, toExcl: number): FootprintLevel[] {
  const m = new Map<number, FootprintLevel>();
  for (let i = from; i < toExcl; i++) {
    const p = store.price[i];
    let l = m.get(p);
    if (!l) { l = { price: p, bid: 0, ask: 0, trades: 0 }; m.set(p, l); }
    if (store.side[i] > 0) l.ask += store.vol[i]; else if (store.side[i] < 0) l.bid += store.vol[i];
    l.trades++;
  }
  return [...m.values()].sort((a, b) => b.price - a.price);
}

/** Delta (Buy − Sell) je Kerze und CVD ab Sessionstart. */
export function deltaCvd(candles: Pick<ReplayCandle, "buyVolume" | "sellVolume">[]): { delta: number[]; cvd: number[] } {
  const delta: number[] = [], cvd: number[] = [];
  let run = 0;
  for (const c of candles) { const d = c.buyVolume - c.sellVolume; delta.push(d); run += d; cvd.push(run); }
  return { delta, cvd };
}

/** Normalisierter gespeicherter Trade eines Laufs für die Überlagerung im Tick-Replay. */
export interface OverlayTrade {
  index: number; side: string; qty: number;
  entryIso: string | null; entryMs: number; entryPrice: number;
  /** null = offen (kein Ausstieg, nichts erfunden). */
  exitIso: string | null; exitMs: number | null; exitPrice: number | null; exitReason: string;
  signalIso: string | null;
}
export interface OverlayEvent { trade: number; kind: "signal" | "entry" | "exit"; units: number; unitsUpper: number; price: number | null; side: string; precision: "100ns" | "ms"; }

/** ISO-UTC (bis 9 Nachkommastellen, auf 100 ns abgeschnitten) → 100-ns-Einheiten relativ zum Anker. */
export function isoUnits(iso: string | null | undefined, anchorUnixMs: number): number | null {
  if (!iso) return null;
  const m = /^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,9}))?Z$/.exec(iso);
  if (!m) return null;
  const whole = Date.parse(m[1] + "Z");
  if (!Number.isFinite(whole)) return null;
  return (whole - anchorUnixMs) * 10_000 + Number((m[2] ?? "").padEnd(7, "0").slice(0, 7));
}

/** Ereignisse (Signal, Entry-Fill, Exit-Fill) in Replay-Einheiten. ms-genaue Zeiten gelten erst am Ende ihrer Millisekunde. */
export function overlayEvents(trades: OverlayTrade[], anchorUnixMs: number): OverlayEvent[] {
  const out: OverlayEvent[] = [];
  const at = (trade: OverlayTrade, kind: OverlayEvent["kind"], iso: string | null, ms: number | null, price: number | null) => {
    const exact = isoUnits(iso, anchorUnixMs);
    if (exact != null) { out.push({ trade: trade.index, kind, units: exact, unitsUpper: exact, price, side: trade.side, precision: "100ns" }); return; }
    if (ms == null) return;
    const u = (ms - anchorUnixMs) * 10_000;
    out.push({ trade: trade.index, kind, units: u, unitsUpper: u + 10_000 - 1, price, side: trade.side, precision: "ms" });
  };
  for (const t of trades) {
    if (t.signalIso) at(t, "signal", t.signalIso, null, null);
    at(t, "entry", t.entryIso, t.entryMs, t.entryPrice);
    if (t.exitPrice != null) at(t, "exit", t.exitIso, t.exitMs, t.exitPrice);
  }
  return out.sort((a, b) => a.units - b.units);
}

/** Ist das Ereignis bis zur Replay-Position p (angewandte Ticks) eingetreten? Alle Ticks bis zur Ereigniszeit müssen
 *  geladen und angewandt sein – nie zu früh. Übersicht: immer true. */
export function overlayReached(ev: OverlayEvent, store: Pick<TickStore, "t" | "loaded" | "total">, p: number): boolean {
  if (p <= 0) return false;
  const fullyLoaded = store.loaded >= store.total || (store.loaded > 0 && store.t[store.loaded - 1] > ev.unitsUpper);
  if (!fullyLoaded) return false;
  const need = countAtOrBefore(store as TickStore, ev.unitsUpper);
  return p >= need;
}
