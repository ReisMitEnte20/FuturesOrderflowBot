// Client der read-only Tick-Replay-API des lokalen .NET-Backends (DevDashboard-API-Host, /api/tickreplay).
// Liefert ECHTE Trades einer Collection-Session portionsweise als Binaer-Spaltenblock – die grosse Wochen-CSV
// gelangt nie als Ganzes in den Browser. Basisadresse zentral aus ./apiBase. Abbruch ueber AbortSignal.

import { apiUrl } from "./apiBase.ts";

const env = (import.meta as any).env ?? {};
const BASE: string = String(env.VITE_TICKREPLAY_API || apiUrl("/api/tickreplay"));

export interface TickReplaySession {
  jobId: string; day: string; label: string; timezone: string;
  windowStartUtc: string; windowEndUtc: string; rows: number; totalVolume: number;
}
export interface TickReplaySessionList {
  available: boolean; reason: string | null; collectionDir: string; manifestPath: string; sessions: TickReplaySession[];
}
/** Zeiten der Trades: 100-ns-Einheiten relativ zu anchorUnixMs (auf einer 5-Minuten-Grenze). */
export interface TickReplayMeta {
  jobId: string; day: string; label: string; timezone: string;
  windowStartUtc: string; windowEndUtc: string; anchorUtc: string; anchorUnixMs: number;
  windowStartUnits: number; windowEndUnits: number; unitsPerMs: number; bucketUnits: number;
  count: number; totalVolume: number; buyVolume: number; sellVolume: number;
  rowBytes: number; csvPath: string; csvSha256: string; manifestPath: string; cache: string; prepareSeconds: number;
  timePrecision: string;
}

async function fail(r: Response, what: string): Promise<never> {
  let msg = `${what} -> HTTP ${r.status}`;
  try { const j = await r.json(); if (j?.error) msg += `: ${j.error}`; } catch { /* kein JSON */ }
  throw new Error(msg);
}

export const tickReplayApi = {
  async sessions(signal?: AbortSignal): Promise<TickReplaySessionList> {
    const r = await fetch(`${BASE}/sessions`, { headers: { Accept: "application/json" }, signal });
    if (!r.ok) return fail(r, "Sessions");
    return r.json();
  },
  /** Bereitet die Session vor (beim ersten Mal einmaliger Cache-Aufbau auf dem Server, ggf. bis ~1 Min). */
  async meta(jobId: string, signal?: AbortSignal): Promise<TickReplayMeta> {
    const r = await fetch(`${BASE}/sessions/${encodeURIComponent(jobId)}/meta`, { headers: { Accept: "application/json" }, signal });
    if (!r.ok) return fail(r, "Session vorbereiten");
    return r.json();
  },
  /** Trades [offset, offset+count) als Spaltenblock (siehe decodeChunk in tickReplay.ts). */
  async ticks(jobId: string, offset: number, count: number, signal?: AbortSignal): Promise<ArrayBuffer> {
    const r = await fetch(`${BASE}/sessions/${encodeURIComponent(jobId)}/ticks?offset=${offset}&count=${count}`, { signal });
    if (!r.ok) return fail(r, `Ticks ab ${offset}`);
    return r.arrayBuffer();
  },
};
