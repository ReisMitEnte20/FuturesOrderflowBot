import { cn } from "@/lib/utils";

interface StatTileProps {
  label: string;
  value: string | number;
  change?: string;
  changeType?: "positive" | "negative" | "neutral";
  accent?: string;
  className?: string;
}

export function StatTile({ label, value, change, changeType = "neutral", accent, className }: StatTileProps) {
  const accentColor = accent || {
    positive: "text-[var(--key)]",
    negative: "text-[var(--red)]",
    neutral: "text-[var(--fg)]",
  }[changeType];

  return (
    <div className={cn("bg-[var(--panel)] border border-[var(--line)] rounded-lg p-3", className)}>
      <p className="text-[10px] font-mono text-[var(--fg-faint)] uppercase tracking-wider">{label}</p>
      <p className={cn("text-lg font-mono font-bold mt-0.5", accentColor)}>{value}</p>
      {change && (
        <p className={cn("text-[10px] font-mono mt-0.5", accentColor)}>{change}</p>
      )}
    </div>
  );
}