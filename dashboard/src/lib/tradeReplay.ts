// Trade-Nachvollzug im Tick-Replay: vom Journal-Trade zu Signal / simuliertem Entry / simuliertem Exit.
//
// Reine Logik ohne UI (direkt mit `node --test` pruefbar). Grundsaetze:
//  - Ereignisse werden NUR angezeigt, wenn sie bis zur Replay-Position eingetreten sind (kein Vorgriff).
//  - Bevorzugt exakte Tick-Indizes aus dem Lauf (gleiche Datenbasis = gleicher CSV-Hash, gleiche Tickzahl, gleicher
//    Collection-Job). Gleiche Zeitstempel werden damit ueber die gespeicherte Reihenfolge aufgeloest.
//  - Sonst nur Zeitstempel: ms-genaue Zeiten (alte Laufdateien) gelten KONSERVATIV erst als eingetreten, wenn alle
//    Ticks bis zum Ende dieser Millisekunde abgespielt sind (nie zu frueh).
//  - Kein Signalmarker ohne gespeicherten Signalzeitpunkt (nichts wird aus Bar-Indizes geschaetzt).
//  - Ergebnis: Brutto aus den Fills (Slippage steckt bereits in den Fillpreisen) minus Gebuehren = Netto.
//    Slippage wird transparent ausgewiesen, aber NIE zusaetzlich abgezogen.

import type { WorkspaceData, WsTrade, WsCandle } from "./workspace.ts";
import { countAtOrBefore, type TickStore } from "./tickReplay.ts";

export const UNITS_PER_MS = 10_000;
/** Pause vor dem Signal beim Sprung aus dem Journal (Marktzeit). */
export const DEFAULT_LEAD_UNITS = 15 * 1000 * UNITS_PER_MS;

export interface TradeCosts {
  tickSize: number | null; pointValue: number | null; feePerSide: number | null; slippageTicks: number | null;
  source: string;
}

export interface ReplayFocus {
  /** Nonce je Klick (erneuter Klick auf denselben Trade springt erneut). */
  key: number;
  runId: string;
  candidate: string;
  trade: WsTrade;
  /** Replay-Tag laut Lauf (Collection-Job / Session-Datum). */
  jobId: string | null;
  day: string | null;
  basis: { csvSha256: string; ticks: number; jobId: string | null };
  costs: TradeCosts;
  ticksPerBar: number;
  tradeDetailVersion: number;
  /** Gespeicherte Signal-Bars (500 Ticks) – das, was die Strategie beim Signal kannte. null = nicht gespeichert. */
  signalBars: { entry: WsCandle | null; exit: WsCandle | null };
}

/** Eindeutige Trade-Referenz: Lauf · Kandidat · laufende Nummer (1-basiert) [· exakte Entry-Zeit].
 *  Im Replay ohne Entry-Zeit (wuerde den Entry-Zeitpunkt vorwegnehmen). */
export function tradeRef(runId: string, candidate: string, t: WsTrade, withEntryTime = true): string {
  const base = `${runId} · ${candidate} · #${t.index + 1}`;
  return withEntryTime ? `${base} · Entry ${t.entryTimeUtc ?? new Date(t.entryTimeMs).toISOString()}` : base;
}

const num = (s: string | undefined): number | null => {
  if (s == null) return null;
  const v = Number(String(s).trim().replace(",", "."));
  return Number.isFinite(v) ? v : null;
};

/** Kosten/Tickgroesse/Punktwert des TATSAECHLICH verwendeten Laufs (Provenienz), sonst null (nicht erfinden). */
export function runCosts(d: WorkspaceData): TradeCosts {
  const c = d.provenance?.costs;
  if (c) return { tickSize: c.tickSize, pointValue: c.pointValue, feePerSide: c.feePerSideTotal, slippageTicks: c.slippageTicks, source: "Lauf-Provenienz (costs)" };
  const s = d.provenance?.settingsUsed ?? {};
  return {
    tickSize: num(s["instrument.tickSize"]), pointValue: d.meta.pointValue ?? num(s["instrument.pointValue"]),
    feePerSide: d.meta.feePerSide ?? null, slippageTicks: d.meta.slippageTicks ?? null,
    source: "Lauf-Meta/settingsUsed (keine costs-Provenienz)",
  };
}

/** Replay-Tag des Laufs. Nur Collection-Tagesläufe haben einen eindeutigen Tag. */
export function runReplayTarget(d: WorkspaceData): { jobId: string | null; day: string | null; why: string | null } {
  const s = d.provenance?.settingsUsed ?? {};
  const jobId = s["collection.jobId"] ?? null;
  const m = /^(\d{4}-\d{2}-\d{2})/.exec(s["window.session"] ?? "");
  const day = m ? m[1] : null;
  if (!jobId && !day) return { jobId: null, day: null, why: "Lauf stammt nicht aus einem Collection-Tag – kein Replay-Tag zuordenbar" };
  return { jobId, day, why: null };
}

export function makeFocus(key: number, runId: string, d: WorkspaceData, candidate: string, trade: WsTrade): ReplayFocus {
  const tgt = runReplayTarget(d);
  return {
    key, runId, candidate, trade, jobId: tgt.jobId, day: tgt.day,
    basis: { csvSha256: d.provenance?.dataSha256 ?? "", ticks: d.provenance?.ticks ?? 0, jobId: tgt.jobId },
    costs: runCosts(d), ticksPerBar: d.meta.ticksPerBar, tradeDetailVersion: d.provenance?.tradeDetailVersion ?? 0,
    signalBars: {
      entry: trade.entrySignal ? d.candles[trade.entrySignal.barIndex] ?? null : null,
      exit: trade.exitSignal ? d.candles[trade.exitSignal.barIndex] ?? null : null,
    },
  };
}

/** Exakte Umrechnung ISO-UTC (bis 9 Nachkommastellen, auf 100 ns abgeschnitten) in 100-ns-Einheiten relativ zum Anker. */
export function isoToUnits(iso: string | null | undefined, anchorUnixMs: number): number | null {
  if (!iso) return null;
  const m = /^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,9}))?Z$/.exec(iso);
  if (!m) return null;
  const whole = Date.parse(m[1] + "Z");
  if (!Number.isFinite(whole)) return null;
  const frac = Number((m[2] ?? "").padEnd(7, "0").slice(0, 7));
  return (whole - anchorUnixMs) * UNITS_PER_MS + frac;
}

export interface BasisCheck { exact: boolean; why: string; }
/** Gleiche Datenbasis wie das Replay? Nur dann gelten die Tick-Indizes des Laufs auch im Replay. */
export function basisCheck(f: ReplayFocus, meta: { csvSha256: string; count: number; jobId: string }): BasisCheck {
  const shaOk = !!f.basis.csvSha256 && f.basis.csvSha256.toLowerCase() === meta.csvSha256.toLowerCase();
  const cntOk = f.basis.ticks === meta.count;
  const jobOk = !f.basis.jobId || f.basis.jobId === meta.jobId;
  if (shaOk && cntOk && jobOk) return { exact: true, why: `gleiche Datenbasis (trades.csv SHA256 ${meta.csvSha256.slice(0, 12)}…, ${meta.count} Ticks, Job ${meta.jobId})` };
  const parts = [!shaOk && "anderer CSV-Hash", !cntOk && `Tickzahl ${f.basis.ticks} ≠ ${meta.count}`, !jobOk && "anderer Collection-Job"].filter(Boolean);
  return { exact: false, why: `Replay-Datenbasis weicht vom Lauf ab (${parts.join(", ")}) – Marker nur nach Zeitstempel, ohne Tick-Reihenfolge` };
}

export type EventKind = "signal" | "entry" | "exitSignal" | "exit";
export interface TradeEvent {
  kind: EventKind;
  /** Exakter Tick-Index im Replay (nur bei gleicher Datenbasis). */
  tickIndex: number | null;
  /** Zeitpunkt (100 ns relativ zum Anker). */
  units: number | null;
  /** Obergrenze bei ms-Genauigkeit: erst eingetreten, wenn alle Ticks bis hierher abgespielt sind. */
  unitsUpper: number | null;
  precision: "tick" | "100ns" | "ms";
  price: number | null;
}

/** Ereignisse eines Trades in zeitlicher Reihenfolge. Fehlender Signalzeitpunkt -> KEIN Signalereignis. */
export function tradeEvents(t: WsTrade, anchorUnixMs: number, exact: boolean): TradeEvent[] {
  const out: TradeEvent[] = [];
  const fromSignal = (kind: EventKind, s: WsTrade["entrySignal"]) => {
    if (!s) return;
    const u = isoToUnits(s.timeUtc, anchorUnixMs);
    out.push({ kind, tickIndex: exact ? s.tickIndex : null, units: u, unitsUpper: u, precision: exact ? "tick" : "100ns", price: s.referencePrice });
  };
  const fill = (kind: EventKind, iso: string | null | undefined, ms: number, idx: number | null | undefined, price: number) => {
    const exactUnits = isoToUnits(iso, anchorUnixMs);
    if (exactUnits != null) {
      out.push({ kind, tickIndex: exact && idx != null ? idx : null, units: exactUnits, unitsUpper: exactUnits, precision: exact && idx != null ? "tick" : "100ns", price });
    } else {
      const u = (ms - anchorUnixMs) * UNITS_PER_MS;
      out.push({ kind, tickIndex: null, units: u, unitsUpper: u + UNITS_PER_MS - 1, precision: "ms", price });
    }
  };
  fromSignal("signal", t.entrySignal);
  fill("entry", t.entryTimeUtc, t.entryTimeMs, t.entryTickIndex, t.entryPrice);
  fromSignal("exitSignal", t.exitSignal);
  fill("exit", t.exitTimeUtc, t.exitTimeMs, t.exitTickIndex, t.exitPrice);
  return out;
}

/** Ist das Ereignis bis zur Replay-Position p (Anzahl angewandter Ticks) eingetreten? Nie zu frueh. */
export function eventReached(ev: TradeEvent, store: Pick<TickStore, "t" | "loaded" | "total">, p: number): boolean {
  if (ev.tickIndex != null) return p > ev.tickIndex;
  if (ev.unitsUpper == null || p <= 0) return false;
  const limit = ev.unitsUpper;
  // Alle Ticks bis 'limit' muessen geladen UND angewandt sein.
  const fullyLoaded = store.loaded >= store.total || (store.loaded > 0 && store.t[store.loaded - 1] > limit);
  if (!fullyLoaded) return false;
  const need = countAtOrBefore(store as TickStore, limit);
  return need > 0 && p >= need;
}

/** Sprungziel "kurz vor dem Signal" (ohne Signal: vor dem Entry). Liegt garantiert VOR dem Ereignis. */
export function pauseTarget(events: TradeEvent[], windowStartUnits: number, lead = DEFAULT_LEAD_UNITS): { units: number; anchor: EventKind } | null {
  const ev = events.find((e) => e.kind === "signal") ?? events.find((e) => e.kind === "entry");
  if (!ev || ev.units == null) return null;
  return { units: Math.max(windowStartUnits, ev.units - lead), anchor: ev.kind };
}

const cents = (x: number) => Math.round(x * 100) / 100;

export interface TradeMath {
  dir: 1 | -1; qty: number; entryFill: number; exitFill: number; pointValue: number | null;
  /** Brutto aus den Fillpreisen: (Exit − Entry) × Richtung × Menge × Punktwert. */
  grossFromFills: number | null;
  grossStored: number | null;
  fees: number | null; feesSource: string;
  netComputed: number | null; netStored: number; netMatches: boolean | null;
  /** Slippage (in Ticks, adverse = positiv) – nur wenn der Tickpreis des Fill-Ticks gespeichert ist. */
  slippage: { entryTicks: number; exitTicks: number; money: number; configTicksPerSide: number | null } | null;
  missing: string[];
}

export function tradeMath(t: WsTrade, costs: TradeCosts): TradeMath {
  const dir: 1 | -1 = t.side === "Long" ? 1 : -1;
  const missing: string[] = [];
  const pv = costs.pointValue;
  if (pv == null) missing.push("Punktwert");
  const grossFromFills = pv == null ? null : cents((t.exitPrice - t.entryPrice) * dir * t.qty * pv);
  let fees: number | null = null, feesSource: string;
  if (t.fees != null) { fees = t.fees; feesSource = "je Trade im Lauf gespeichert (Engine)"; }
  else if (costs.feePerSide != null) { fees = cents(2 * costs.feePerSide * t.qty); feesSource = `berechnet: 2 Seiten × ${costs.feePerSide} × ${t.qty} (Lauf-Kostenprofil; je Trade nicht gespeichert)`; }
  else { feesSource = "nicht gespeichert"; missing.push("Gebühren"); }
  const netComputed = grossFromFills != null && fees != null ? cents(grossFromFills - fees) : null;
  const netMatches = netComputed == null ? null : Math.abs(netComputed - t.netPnL) < 0.005;
  let slippage: TradeMath["slippage"] = null;
  if (t.entryTickPrice != null && t.exitTickPrice != null && costs.tickSize) {
    const ts = costs.tickSize;
    const entryTicks = Math.round(((t.entryPrice - t.entryTickPrice) * dir / ts) * 1e6) / 1e6;
    const exitTicks = Math.round(((t.exitTickPrice - t.exitPrice) * dir / ts) * 1e6) / 1e6;
    slippage = { entryTicks, exitTicks, money: pv == null ? NaN : cents((entryTicks + exitTicks) * ts * pv * t.qty), configTicksPerSide: costs.slippageTicks };
  } else missing.push("Tickpreis der Fill-Ticks (Slippage-Aufschlüsselung)");
  return {
    dir, qty: t.qty, entryFill: t.entryPrice, exitFill: t.exitPrice, pointValue: pv,
    grossFromFills, grossStored: t.grossPnL ?? null, fees, feesSource, netComputed, netStored: t.netPnL, netMatches, slippage, missing,
  };
}
