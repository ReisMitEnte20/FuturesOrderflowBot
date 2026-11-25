// Reine Logik für das gemeinsame Replay „London + New York“ (ohne React): mehrere Sessions eines Handelstags liegen
// hintereinander in EINEM Tick-Speicher mit gemeinsamer Zeitbasis (100-ns-Einheiten relativ zum Anker der ersten Session).
// Die unbeobachtete Pause wird nicht abgespielt: die Marktuhr springt vom Ende einer Session auf den ersten echten Tick
// der nächsten. Nichts wird in der Pause erzeugt; Kerzen/Trades der nächsten Session erscheinen erst nach dem Sprung.

/** Was das Replay je Session vom Server weiß (Ausschnitt aus TickReplayMeta). */
export interface SegMeta {
  id: string; label: string; session?: string; anchorUnixMs: number; windowStartUnits: number; windowEndUnits: number; count: number; bucketUnits: number;
}
/** Session im gemeinsamen Speicher: Zeiten relativ zum Basis-Anker, Index-Bereich [firstIdx, firstIdx+count). */
export interface SegLayout {
  id: string; label: string; session: string; offsetUnits: number; startUnits: number; endUnits: number; firstIdx: number; count: number;
}

export const UNITS_PER_MS = 10_000;

/** Legt die Sessions hintereinander. Fail-closed: verschiedene Kerzenraster, falsche Reihenfolge oder Überlappung → Fehler. */
export function layoutSegments(metas: SegMeta[]): { baseAnchorMs: number; bucketUnits: number; segs: SegLayout[] } {
  if (!metas.length) throw new Error("Keine Session.");
  const base = metas[0].anchorUnixMs, bucket = metas[0].bucketUnits;
  let idx = 0, prevEnd = -Infinity;
  const segs: SegLayout[] = metas.map((m) => {
    if (m.bucketUnits !== bucket) throw new Error("Sessions mit verschiedenem Kerzenraster.");
    const off = (m.anchorUnixMs - base) * UNITS_PER_MS;
    if (!Number.isInteger(off / bucket)) throw new Error("Anker nicht auf dem Kerzenraster.");
    const s = m.windowStartUnits + off, e = m.windowEndUnits + off;
    if (s < prevEnd) throw new Error(`Sessions überlappen (${m.label}) – Replay würde Marktereignisse doppelt zeigen.`);
    prevEnd = e;
    const seg = { id: m.id, label: m.label, session: m.session ?? "", offsetUnits: off, startUnits: s, endUnits: e, firstIdx: idx, count: m.count };
    idx += m.count;
    return seg;
  });
  return { baseAnchorMs: base, bucketUnits: bucket, segs };
}

/** Session, in deren Fenster die Zeit liegt; in einer Pause: -1. */
export function segmentAtTime(segs: SegLayout[], units: number): number {
  for (let i = 0; i < segs.length; i++) if (units >= segs[i].startUnits && units < segs[i].endUnits) return i;
  return -1;
}

/** Index der Session eines Tick-Index. */
export function segmentOfIndex(segs: SegLayout[], tickIdx: number): number {
  for (let i = segs.length - 1; i >= 0; i--) if (tickIdx >= segs[i].firstIdx) return i;
  return 0;
}

/** Pausenzustand der Uhr: zwischen Ende von Session k und dem ersten Tick von k+1 (dort zeigt das Replay noch nichts von k+1). */
export function pauseAt(segs: SegLayout[], firstTickUnits: (i: number) => number | null, clock: number): { from: number; to: number } | null {
  for (let k = 0; k + 1 < segs.length; k++) {
    const ft = firstTickUnits(k + 1) ?? segs[k + 1].startUnits;
    if (clock >= segs[k].endUnits && clock < ft) return { from: k, to: k + 1 };
  }
  return null;
}

/** Dauer lesbar: „2 h 47 min“, „12 min 5 s“, „3,2 s“. */
export function humanDuration(ms: number): string {
  if (ms < 0) return "−" + humanDuration(-ms);
  const totalMin = Math.floor(ms / 60_000);
  const h = Math.floor(totalMin / 60), m = totalMin % 60, s = Math.floor((ms % 60_000) / 1000);
  if (h > 0) return `${h} h ${m} min`;
  if (totalMin > 0) return `${m} min ${s} s`;
  return `${(ms / 1000).toLocaleString("de-DE", { maximumFractionDigits: 1 })} s`;
}

/** Text des Übergangs, z. B. „London beendet · 2 h 47 min übersprungen · New York beginnt“. */
export function transitionText(fromLabel: string, toLabel: string, skippedMs: number): string {
  return `${fromLabel} beendet · ${humanDuration(skippedMs)} übersprungen · ${toLabel} beginnt`;
}

/** Kerzen-Slots: je Session ihre 5-Minuten-Kerzen, dazwischen EIN Pausen-Slot (keine Kerze, keine erfundenen Werte). */
export interface SlotTemplate { kind: "bucket" | "pause"; bucket: number; seg: number; label: string; range: string; }
export function slotTemplates(segs: SegLayout[], bucketUnits: number, fmt: (units: number, seg: number) => string, pauseLabel: (k: number) => string): SlotTemplate[] {
  const out: SlotTemplate[] = [];
  segs.forEach((s, k) => {
    if (k > 0) out.push({ kind: "pause", bucket: Number.NaN, seg: k, label: "⏸", range: pauseLabel(k) });
    const b0 = Math.floor(s.startUnits / bucketUnits), b1 = Math.ceil(s.endUnits / bucketUnits) - 1;
    for (let b = b0; b <= b1; b++) out.push({ kind: "bucket", bucket: b, seg: k, label: fmt(b * bucketUnits, k), range: `${fmt(b * bucketUnits, k)}–${fmt((b + 1) * bucketUnits, k)}` });
  });
  return out;
}

/** Delta/CVD je Slot; CVD startet je Session bei 0 (Session-CVD). Pausen-/leere Slots: null. */
export function sessionDeltaCvd(slots: { seg: number; candle: { buyVolume: number; sellVolume: number } | null }[]): { delta: (number | null)[]; cvd: (number | null)[] } {
  const delta: (number | null)[] = [], cvd: (number | null)[] = [];
  let seg = -1, run = 0;
  for (const s of slots) {
    if (s.seg !== seg) { seg = s.seg; run = 0; }
    if (!s.candle) { delta.push(null); cvd.push(null); continue; }
    const d = s.candle.buyVolume - s.candle.sellVolume;
    run += d;
    delta.push(d); cvd.push(run);
  }
  return { delta, cvd };
}

/** Session-VWAP aus angewandten Ticks [from, toExcl) (startet je Session neu). */
export function vwap(store: { price: Float64Array; vol: Int32Array }, from: number, toExcl: number): number | null {
  let pv = 0, v = 0;
  for (let i = from; i < toExcl; i++) { pv += store.price[i] * store.vol[i]; v += store.vol[i]; }
  return v > 0 ? pv / v : null;
}
