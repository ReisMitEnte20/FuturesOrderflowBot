import { useId } from "react";

/**
 * Rein dekoratives Banner-Motiv (Neon-Kerzen + grüne/blaue Linie) für den Seitenhintergrund.
 * Die Werte sind fest erzeugte Zierdaten – KEINE Marktdaten und keine Kennzahl.
 */

const W = 1600;
const H = 420;
const N = 54;

interface DecoCandle {
  x: number;
  open: number;
  close: number;
  high: number;
  low: number;
  hollow: boolean;
}

// Kleiner deterministischer Generator, damit das Motiv bei jedem Rendern identisch ist.
function lcg(seed: number) {
  let s = seed;
  return () => {
    s = (s * 1664525 + 1013904223) % 4294967296;
    return s / 4294967296;
  };
}

function build() {
  const rnd = lcg(20261112);
  const step = W / N;
  const candles: DecoCandle[] = [];
  const green: string[] = [];
  const blue: string[] = [];
  let price = H * 0.78;
  let avg = price;
  for (let i = 0; i < N; i++) {
    const t = i / (N - 1);
    // Aufwärts laufender Pfad mit zwei Wellen, wie im Banner.
    const target = H * (0.8 - 0.52 * t) + Math.sin(t * 9.5) * 34 + Math.sin(t * 3.1 + 1.2) * 26;
    const open = price;
    const close = open + (target - open) * 0.55 + (rnd() - 0.5) * 34;
    const high = Math.min(open, close) - 6 - rnd() * 26;
    const low = Math.max(open, close) + 6 + rnd() * 26;
    const x = step * (i + 0.5);
    candles.push({ x, open, close, high, low, hollow: rnd() > 0.55 });
    price = close;
    avg = avg + (close - avg) * 0.22;
    green.push(`${x.toFixed(1)},${(avg - 8).toFixed(1)}`);
    blue.push(`${x.toFixed(1)},${(avg + 34 + Math.sin(t * 6) * 10).toFixed(1)}`);
  }
  return { candles, green: green.join(" "), blue: blue.join(" "), bodyW: step * 0.46 };
}

const MOTIF = build();

export function MarketBackdrop({ className = "" }: { className?: string }) {
  const id = useId().replace(/:/g, "");
  const grad = `mb-grad-${id}`;
  const glow = `mb-glow-${id}`;
  const fade = `mb-fade-${id}`;
  const mask = `mb-mask-${id}`;

  return (
    <svg
      aria-hidden="true"
      focusable="false"
      viewBox={`0 0 ${W} ${H}`}
      preserveAspectRatio="none"
      className={`pointer-events-none select-none ${className}`}
    >
      <defs>
        <linearGradient id={grad} x1="0" y1="0" x2="1" y2="0">
          <stop offset="0" stopColor="#3cf0a0" />
          <stop offset="0.55" stopColor="#2edcc4" />
          <stop offset="1" stopColor="#22b8f0" />
        </linearGradient>
        <linearGradient id={fade} x1="0" y1="0" x2="0" y2="1">
          <stop offset="0" stopColor="#fff" stopOpacity="0" />
          <stop offset="0.35" stopColor="#fff" stopOpacity="0.9" />
          <stop offset="1" stopColor="#fff" stopOpacity="1" />
        </linearGradient>
        <mask id={mask}>
          <rect x="0" y="0" width={W} height={H} fill={`url(#${fade})`} />
        </mask>
        <filter id={glow} x="-5%" y="-20%" width="110%" height="140%">
          <feGaussianBlur stdDeviation="5" result="b" />
          <feMerge>
            <feMergeNode in="b" />
            <feMergeNode in="SourceGraphic" />
          </feMerge>
        </filter>
      </defs>

      <g mask={`url(#${mask})`} className="brand-breathe">
        <g stroke={`url(#${grad})`} strokeWidth="1.4" opacity="0.5">
          {MOTIF.candles.map((c, i) => {
            const top = Math.min(c.open, c.close);
            const h = Math.max(4, Math.abs(c.close - c.open));
            return (
              <g key={i}>
                <line x1={c.x} x2={c.x} y1={c.high} y2={c.low} />
                <rect
                  x={c.x - MOTIF.bodyW / 2}
                  y={top}
                  width={MOTIF.bodyW}
                  height={h}
                  rx="1.5"
                  fill={c.hollow ? "#020d1e" : `url(#${grad})`}
                  fillOpacity={c.hollow ? 0.85 : 0.75}
                />
              </g>
            );
          })}
        </g>
        <g fill="none" strokeLinejoin="round" strokeLinecap="round" filter={`url(#${glow})`}>
          <polyline points={MOTIF.blue} stroke="#22a8f5" strokeWidth="2" opacity="0.75" />
          <polyline points={MOTIF.green} stroke="#3cf0a0" strokeWidth="2" opacity="0.8" />
        </g>
      </g>
    </svg>
  );
}
