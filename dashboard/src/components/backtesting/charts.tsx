// Schlanke SVG-Diagramme für den Backtest-Bericht. Farben aus den Projekt-Tokens; Hover zeigt die exakten Werte.
// Höchstens zwei Reihen je Diagramm (Strategie = Cyan, Vergleich = Gold) — immer mit Legende.
import { useMemo, useRef, useState } from "react";
import { money } from "@/lib/backtestingApi";

const C = { cyan: "#22b8f0", gold: "#f0c35a", red: "#f0566a", key: "#3cf0a0", grid: "#14273f", axis: "#6b8399", fg: "#eefcf6", dim: "#a9c3cf", panel: "#0a1a30" };

export interface Series { name: string; color?: string; x: number[]; y: number[]; area?: boolean; dashed?: boolean }

function niceTicks(min: number, max: number, n = 4): number[] {
  if (!Number.isFinite(min) || !Number.isFinite(max)) return [];
  if (min === max) { min -= 1; max += 1; }
  const step0 = (max - min) / n;
  const mag = Math.pow(10, Math.floor(Math.log10(step0)));
  const step = [1, 2, 2.5, 5, 10].map((m) => m * mag).find((s) => s >= step0) ?? step0;
  const out: number[] = [];
  for (let v = Math.ceil(min / step) * step; v <= max + 1e-9; v += step) out.push(Math.abs(v) < 1e-9 ? 0 : v);
  return out;
}
const fmtX = (ms: number, timeAxis: boolean) => (timeAxis ? new Date(ms).toISOString().slice(5, 10) : String(Math.round(ms)));

/** Linien-/Flächendiagramm mit Fadenkreuz-Tooltip. x = Zeit (ms) oder Index. */
export function LineChart({ series, height = 180, timeAxis = true, zeroLine = false, yFormat = (v: number) => money(v, 0), ariaLabel }: {
  series: Series[]; height?: number; timeAxis?: boolean; zeroLine?: boolean; yFormat?: (v: number) => string; ariaLabel: string;
}) {
  const ref = useRef<SVGSVGElement | null>(null);
  const [hover, setHover] = useState<number | null>(null);
  const W = 720, H = height, L = 64, R = 12, T = 10, B = 22;
  const all = series.flatMap((s) => s.x.map((x, i) => [x, s.y[i]] as const)).filter(([, y]) => Number.isFinite(y));
  const xs = all.map((p) => p[0]), ys = all.map((p) => p[1]);
  const xMin = Math.min(...xs), xMax = Math.max(...xs);
  let yMin = Math.min(...ys, zeroLine ? 0 : Infinity), yMax = Math.max(...ys, zeroLine ? 0 : -Infinity);
  if (yMin === yMax) { yMin -= 1; yMax += 1; }
  const pad = (yMax - yMin) * 0.06; yMin -= pad; yMax += pad;
  const sx = (x: number) => L + ((x - xMin) / Math.max(1e-9, xMax - xMin)) * (W - L - R);
  const sy = (y: number) => T + (1 - (y - yMin) / (yMax - yMin)) * (H - T - B);
  const ticks = niceTicks(yMin, yMax);
  const xTicks = useMemo(() => {
    if (xs.length === 0) return [];
    const n = 5; return Array.from({ length: n }, (_, i) => xMin + ((xMax - xMin) * i) / (n - 1));
  }, [xs.length, xMin, xMax]);
  if (all.length < 2) return <div className="text-[11px] text-[var(--fg-faint)] py-6 text-center">Zu wenige Punkte für ein Diagramm.</div>;

  const primary = series[0];
  const onMove = (e: React.MouseEvent<SVGSVGElement>) => {
    const r = ref.current?.getBoundingClientRect(); if (!r) return;
    const x = ((e.clientX - r.left) / r.width) * W;
    const xv = xMin + ((x - L) / (W - L - R)) * (xMax - xMin);
    let best = 0, bd = Infinity;
    primary.x.forEach((v, i) => { const d = Math.abs(v - xv); if (d < bd) { bd = d; best = i; } });
    setHover(best);
  };
  const hx = hover != null ? primary.x[hover] : null;
  return (
    <div className="relative">
      <svg ref={ref} viewBox={`0 0 ${W} ${H}`} className="w-full" role="img" aria-label={ariaLabel} onMouseMove={onMove} onMouseLeave={() => setHover(null)}>
        {ticks.map((t) => (
          <g key={t}><line x1={L} x2={W - R} y1={sy(t)} y2={sy(t)} stroke={C.grid} strokeWidth={1} />
            <text x={L - 6} y={sy(t) + 3} textAnchor="end" fontSize={10} fill={C.axis}>{yFormat(t)}</text></g>
        ))}
        {zeroLine && <line x1={L} x2={W - R} y1={sy(0)} y2={sy(0)} stroke={C.axis} strokeWidth={1} strokeDasharray="3 3" />}
        {xTicks.map((t, i) => <text key={i} x={sx(t)} y={H - 6} textAnchor={i === 0 ? "start" : i === xTicks.length - 1 ? "end" : "middle"} fontSize={10} fill={C.axis}>{fmtX(t, timeAxis)}</text>)}
        {series.map((s, si) => {
          const col = s.color ?? (si === 0 ? C.cyan : C.gold);
          const pts = s.x.map((x, i) => `${sx(x).toFixed(1)},${sy(s.y[i]).toFixed(1)}`).join(" ");
          return (
            <g key={s.name}>
              {s.area && <polygon points={`${sx(s.x[0])},${sy(Math.max(yMin, 0))} ${pts} ${sx(s.x[s.x.length - 1])},${sy(Math.max(yMin, 0))}`} fill={col} opacity={0.14} />}
              <polyline points={pts} fill="none" stroke={col} strokeWidth={2} strokeDasharray={s.dashed ? "5 4" : undefined} strokeLinejoin="round" />
            </g>
          );
        })}
        {hx != null && <line x1={sx(hx)} x2={sx(hx)} y1={T} y2={H - B} stroke={C.dim} strokeWidth={1} />}
        {hx != null && series.map((s, si) => {
          const i = s.x.findIndex((x) => x >= hx); if (i < 0) return null;
          return <circle key={si} cx={sx(s.x[i])} cy={sy(s.y[i])} r={4} fill={s.color ?? (si === 0 ? C.cyan : C.gold)} stroke={C.panel} strokeWidth={2} />;
        })}
      </svg>
      {series.length > 1 && (
        <div className="flex gap-3 text-[10px] text-[var(--fg-dim)] mt-1">
          {series.map((s, si) => <span key={s.name} className="inline-flex items-center gap-1"><span className="inline-block h-[2px] w-4" style={{ background: s.color ?? (si === 0 ? C.cyan : C.gold) }} />{s.name}</span>)}
        </div>
      )}
      {hover != null && hx != null && (
        <div className="pointer-events-none absolute right-2 top-1 rounded border border-[var(--line-2)] bg-[var(--panel-2)] px-2 py-1 text-[10px] mono text-[var(--fg)]">
          <div className="text-[var(--fg-faint)]">{timeAxis ? new Date(hx).toISOString().slice(0, 16).replace("T", " ") : `#${Math.round(hx)}`}</div>
          {series.map((s) => { const i = s.x.findIndex((x) => x >= hx); return i < 0 ? null : <div key={s.name}>{s.name}: {yFormat(s.y[i])}</div>; })}
        </div>
      )}
    </div>
  );
}

/** Histogramm einer simulierten Verteilung mit Markierung von P5/Median/P95 und optionalem Referenzwert. */
export function Histogram({ bins, min, max, p5, median, p95, reference, referenceLabel, unitFormat = (v: number) => money(v, 0), ariaLabel, color = C.cyan }: {
  bins: number[]; min: number; max: number; p5?: number; median?: number; p95?: number; reference?: number | null; referenceLabel?: string;
  unitFormat?: (v: number) => string; ariaLabel: string; color?: string;
}) {
  const [hover, setHover] = useState<number | null>(null);
  const W = 360, H = 120, L = 6, R = 6, T = 14, B = 18;
  const maxC = Math.max(1, ...bins);
  const bw = (W - L - R) / Math.max(1, bins.length);
  const span = max - min || 1;
  const sx = (v: number) => L + ((v - min) / span) * (W - L - R);
  const marks: [number | undefined, string][] = [[p5, "P5"], [median, "Median"], [p95, "P95"]];
  return (
    <div className="relative">
      <svg viewBox={`0 0 ${W} ${H}`} className="w-full" role="img" aria-label={ariaLabel} onMouseLeave={() => setHover(null)}>
        {bins.map((c, i) => {
          const h = (c / maxC) * (H - T - B);
          return <rect key={i} x={L + i * bw + 1} y={H - B - h} width={Math.max(1, bw - 2)} height={Math.max(0, h)} rx={1.5} fill={color} opacity={hover === i ? 1 : 0.75}
            onMouseEnter={() => setHover(i)} />;
        })}
        {marks.map(([v, lab]) => v == null || !Number.isFinite(v) ? null : (
          <g key={lab}><line x1={sx(v)} x2={sx(v)} y1={T - 4} y2={H - B} stroke={C.fg} strokeWidth={1} strokeDasharray={lab === "Median" ? undefined : "3 3"} />
            <text x={sx(v)} y={T - 6} fontSize={9} textAnchor="middle" fill={C.dim}>{lab}</text></g>
        ))}
        {reference != null && Number.isFinite(reference) && reference >= min && reference <= max && (
          <g><line x1={sx(reference)} x2={sx(reference)} y1={T} y2={H - B} stroke={C.gold} strokeWidth={2} />
            <text x={sx(reference)} y={H - B + 12} fontSize={9} textAnchor="middle" fill={C.gold}>{referenceLabel ?? "Referenz"}</text></g>
        )}
        <text x={L} y={H - 4} fontSize={9} fill={C.axis}>{unitFormat(min)}</text>
        <text x={W - R} y={H - 4} fontSize={9} textAnchor="end" fill={C.axis}>{unitFormat(max)}</text>
      </svg>
      {hover != null && (
        <div className="pointer-events-none absolute right-1 top-0 rounded border border-[var(--line-2)] bg-[var(--panel-2)] px-2 py-0.5 text-[10px] mono text-[var(--fg)]">
          {unitFormat(min + hover * (span / bins.length))} … {unitFormat(min + (hover + 1) * (span / bins.length))}: {bins[hover]} Pfade
        </div>
      )}
    </div>
  );
}

/** Divergierende Farbskala: Verlust rot ↔ neutral grau ↔ Gewinn grün (gleiche Stärke = gleicher Betrag). */
export function divergingColor(v: number, maxAbs: number): string {
  if (!Number.isFinite(v) || maxAbs <= 0) return "#1d3654";
  const t = Math.min(1, Math.abs(v) / maxAbs);
  const [r0, g0, b0] = [0x1d, 0x36, 0x54];
  const [r1, g1, b1] = v >= 0 ? [0x3c, 0xf0, 0xa0] : [0xf0, 0x56, 0x6a];
  const mix = (a: number, b: number) => Math.round(a + (b - a) * (0.15 + 0.85 * t));
  return `rgb(${mix(r0, r1)},${mix(g0, g1)},${mix(b0, b1)})`;
}

/** Balken je Session (Gewinn/Verlust), Hover zeigt Tag und Betrag. */
export function SessionBars({ keys, values, ariaLabel, height = 90 }: { keys: string[]; values: number[]; ariaLabel: string; height?: number }) {
  const [hover, setHover] = useState<number | null>(null);
  const W = 720, H = height, T = 6, B = 6;
  const maxAbs = Math.max(1e-9, ...values.map((v) => Math.abs(v)));
  const bw = W / Math.max(1, values.length);
  const mid = T + (H - T - B) / 2;
  return (
    <div className="relative">
      <svg viewBox={`0 0 ${W} ${H}`} className="w-full" role="img" aria-label={ariaLabel} onMouseLeave={() => setHover(null)}>
        <line x1={0} x2={W} y1={mid} y2={mid} stroke={C.axis} strokeWidth={1} />
        {values.map((v, i) => {
          const h = (Math.abs(v) / maxAbs) * ((H - T - B) / 2);
          return <rect key={i} x={i * bw + 1} y={v >= 0 ? mid - h : mid} width={Math.max(1, bw - 2)} height={Math.max(0.5, h)} rx={1.5}
            fill={v >= 0 ? C.key : C.red} opacity={hover === i ? 1 : 0.8} onMouseEnter={() => setHover(i)} />;
        })}
      </svg>
      {hover != null && (
        <div className="pointer-events-none absolute right-1 top-0 rounded border border-[var(--line-2)] bg-[var(--panel-2)] px-2 py-0.5 text-[10px] mono text-[var(--fg)]">
          {keys[hover]}: {money(values[hover])}
        </div>
      )}
    </div>
  );
}
