import { cn } from "@/lib/utils";

type Tone = "key" | "cyan" | "gold" | "red" | "purple";

const TONE: Record<Tone, string> = {
  key: "rgba(60, 240, 160, 0.9)",
  cyan: "rgba(34, 184, 240, 0.9)",
  gold: "rgba(240, 195, 90, 0.9)",
  red: "rgba(240, 86, 106, 0.9)",
  purple: "rgba(155, 140, 255, 0.9)",
};

interface StatTileProps {
  label: string;
  value: string | number;
  change?: string;
  changeType?: "positive" | "negative" | "neutral";
  /** Textfarbe des Werts als Klasse (überschreibt changeType). */
  accent?: string;
  /** Farbe der leuchtenden Akzentlinie oben. */
  tone?: Tone;
  className?: string;
}

export function StatTile({ label, value, change, changeType = "neutral", accent, tone = "key", className }: StatTileProps) {
  const accentColor = accent || {
    positive: "text-[var(--key)]",
    negative: "text-[var(--red)]",
    neutral: "text-[var(--fg)]",
  }[changeType];

  return (
    <div className={cn("panel overflow-hidden px-4 py-3", className)}>
      <span
        aria-hidden="true"
        className="absolute inset-x-0 top-0 h-px"
        style={{ background: `linear-gradient(90deg, ${TONE[tone]}, transparent 75%)` }}
      />
      <p className="eyebrow">{label}</p>
      <p className={cn("text-[22px] leading-tight font-mono font-bold mt-1.5 tabular-nums", accentColor)}>{value}</p>
      {change && <p className={cn("text-[10px] font-mono mt-0.5", accentColor)}>{change}</p>}
    </div>
  );
}
