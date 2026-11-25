// Client für „London + New York“ (gemeinsamer Lauf: eine Strategie, ein Konto) – /api/backtest/sessions.
// Nur lesend bzw. Simulation: keine Orders, keine Broker-Anbindung. Basisadresse zentral aus ./apiBase.
import { apiUrl } from "./apiBase.ts";

const BASE = String(((import.meta as any).env ?? {}).VITE_SESSIONS_API || apiUrl("/api/backtest/sessions"));

export interface PlannedSession {
  key: string; label: string; plannedStartUtc: string; plannedEndUtc: string; closeOutUtc: string;
  status: "ok" | "missing" | "defective" | "conflict"; reasons: string[];
  id?: string | null; jobId?: string | null; collection?: string | null; collectionsRoot?: string | null; recordingId?: string | null;
  rows?: number | null; recorderDrops?: number | null; summaryClean?: boolean | null; manifestTickDrops?: number | null;
  selection?: string | null; selectionNote?: string | null; windowStartUtc?: string | null; windowEndUtc?: string | null; effectiveStartUtc?: string | null;
}
export interface PlannedDay {
  day: string; status: "complete" | "incomplete" | "no-data" | "ambiguous"; reasons: string[]; sessions: PlannedSession[];
  plannedPauseMinutes: number | null; overlapNote: string | null;
}
export interface SessionPlan {
  from: string; to: string; schedule: string; schedulePath: string; closeOutMinutes: number; roots: string[];
  days: PlannedDay[]; completeDays: number; excludedDays: number; notes: string[];
}
export interface SessionRunRequest {
  from: string; to: string; strategy?: string; ticksPerBar: number; deltaThreshold: number; initialBalance: number;
  slippageTicks?: number | null; applyFees: boolean; maxDailyLoss?: number | null; closeOutMinutes?: number | null;
}
export interface SessionSegment {
  id: string; session: string; label: string; day: string; windowStartUtc: string; windowEndUtc: string; closeOutUtc: string;
  collection: string; collectionsRoot: string; recordingId: string; contract: string; sliceSha256: string; ticksLoaded: number; ticksUsed: number;
  firstTickUtc: string | null; firstTickPrice: number | null; lastTickUtc: string | null; lastTickPrice: number | null; volume: number;
  sessionVwap: number | null; sessionCvd: number; discardedPartialBarTicksAtStart: number; entrySignalsPassed: number; flatSignalsPassed: number;
  entrySignalsSuppressedAfterCloseOut: number; closeOutReachedUtc: string | null; positionAtCloseOut: string; sessionCloseSignalUtc: string | null;
  sessionCloseSignalPrice: number | null; sessionCloseFillUtc: string | null; sessionCloseFillPrice: number | null; sessionCloseStatus: string;
  positionAtLastTick: string; incidents: string[];
}
export interface SessionTransition {
  fromId: string; toId: string; fromLabel: string; toLabel: string; kind: string; lastTickUtc: string; lastTickPrice: number | null;
  firstTickUtc: string; firstTickPrice: number | null; skippedSeconds: number; skippedText: string; plannedPauseSeconds: number;
  balanceBefore: number; balanceAfter: number; pnlChangeInJump: number; tradesClosedInJump: number; tradesOpenedInJump: number;
  positionAtLastTick: string; skippedDays: string[];
}
export interface SessionTrade {
  index: number; day: string; session: string; sessionLabel: string; segmentId: string; collection: string; recordingId: string;
  side: string; qty: number; entryTimeUtc: string; entryPrice: number; exitTimeUtc: string; exitPrice: number;
  grossPnL: number; fees: number; netPnL: number; exitReason: string; crossedPause: boolean; balanceAfter: number;
}
export interface SessionSummary { key: string; label: string; sessions: number; trades: number; winners: number; losers: number; grossPnL: number; fees: number; netPnL: number; entrySignals: number; suppressedAfterCloseOut: number; }
export interface SessionDaySummary { day: string; trades: number; grossPnL: number; fees: number; netPnL: number; dailyLimitHitUtc: string | null; dailyLimitHitSession: string | null; entriesAfterLimit: number; entrySignalsAfterLimit: number; notes: string[]; }
export interface IndicatorPolicy { name: string; atSessionStart: string; reason: string; }
export interface SessionRunResult {
  runId: string; createdUtc: string; status: string; error: string | null; request: SessionRunRequest; strategyName: string; engine: string;
  symbol: string; tickSize: number; pointValue: number; feePerSide: number; slippageTicks: number; maxDailyLoss: number; closeOutMinutes: number;
  instrumentIsExample: boolean; feeIsExample: boolean; plan: SessionPlan | null; skippedAtLoad: string[]; segments: SessionSegment[];
  transitions: SessionTransition[]; trades: SessionTrade[]; bySession: SessionSummary[]; total: SessionSummary | null; byDay: SessionDaySummary[];
  startBalance: number; endBalance: number; maxDrawdown: number; ticksProcessed: number; signalsGenerated: number; ordersSubmitted: number;
  ordersRejectedByRisk: number; ordersUnfilledAtEnd: number; finalPosition: string; indicatorPolicy: IndicatorPolicy[]; rules: string[]; elapsedSeconds: number;
}
export interface SessionRunIndexEntry { runId: string; createdUtc: string; status: string; from: string; to: string; days: number; trades: number; netPnL: number; strategyName: string; }

async function read<T>(r: Response, what: string): Promise<T> {
  if (r.ok) return r.json();
  let msg = `${what} -> HTTP ${r.status}`;
  try { const j = await r.json(); if (j?.error) msg = `${what}: ${j.error}`; } catch { /* kein JSON */ }
  throw new Error(msg);
}

export const sessionsApi = {
  async plan(from: string, to: string, closeOutMinutes?: number | null, signal?: AbortSignal): Promise<SessionPlan> {
    const q = new URLSearchParams({ from, to });
    if (closeOutMinutes != null) q.set("closeOutMinutes", String(closeOutMinutes));
    return read(await fetch(`${BASE}/plan?${q}`, { signal }), "Sessionplan");
  },
  async run(req: SessionRunRequest, signal?: AbortSignal): Promise<SessionRunResult> {
    return read(await fetch(`${BASE}/run`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(req), signal }), "Lauf");
  },
  async runs(signal?: AbortSignal): Promise<SessionRunIndexEntry[]> {
    return read(await fetch(`${BASE}/runs`, { signal }), "Gespeicherte Läufe");
  },
  async get(runId: string, signal?: AbortSignal): Promise<SessionRunResult> {
    return read(await fetch(`${BASE}/runs/${encodeURIComponent(runId)}`, { signal }), "Lauf laden");
  },
};
