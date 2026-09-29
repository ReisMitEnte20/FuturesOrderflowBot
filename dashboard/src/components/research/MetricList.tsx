import { useState } from "react";
import { cn } from "@/lib/utils";
import { formatMetric, type QuantMetric } from "@/lib/quantApi";

/**
 * Kennzahlenliste. Jede Kennzahl lässt sich aufklappen und zeigt dann Methode, Eingaben,
 * Datenumfang und Grenzen. Nicht berechenbare Kennzahlen erscheinen als „n. b." mit Begründung —
 * es wird nie ein Ersatzwert angezeigt.
 */
export function MetricList({
  metrics,
  title,
  subtitle,
  columns = 2,
}: {
  metrics: QuantMetric[];
  title?: string;
  subtitle?: string;
  columns?: 1 | 2 | 3;
}) {
  if (metrics.length === 0) {
    return (
      <div className="text-xs text-[var(--fg-faint)] px-3 py-4">
        Keine Kennzahlen vorhanden.
      </div>
    );
  }
  return (
    <div>
      {title && (
        <div className="px-3 pt-3 pb-1">
          <div className="text-xs font-medium text-[var(--fg)]">{title}</div>
          {subtitle && <div className="text-[11px] text-[var(--fg-faint)] mt-0.5">{subtitle}</div>}
        </div>
      )}
      <div
        className={cn(
          "grid gap-px bg-[var(--line)]",
          columns === 1 && "grid-cols-1",
          columns === 2 && "grid-cols-1 xl:grid-cols-2",
          columns === 3 && "grid-cols-1 lg:grid-cols-2 2xl:grid-cols-3"
        )}
      >
        {metrics.map((m) => (
          <MetricRow key={m.key} metric={m} />
        ))}
      </div>
    </div>
  );
}

function MetricRow({ metric }: { metric: QuantMetric }) {
  const [open, setOpen] = useState(false);
  const unavailable = metric.value === null || metric.value === undefined;

  return (
    <div className="bg-[var(--panel)]">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        className="w-full flex items-baseline justify-between gap-3 px-3 py-2 text-left hover:bg-[var(--panel-2)] transition-colors"
        aria-expanded={open}
      >
        <span className="text-xs text-[var(--fg-dim)] truncate">{metric.label}</span>
        <span
          className={cn(
            "mono text-sm tabular-nums whitespace-nowrap",
            unavailable ? "text-[var(--fg-faint)] italic" : "text-[var(--fg)]"
          )}
          title={unavailable ? metric.unavailableReason ?? "nicht berechenbar" : undefined}
        >
          {formatMetric(metric)}
        </span>
      </button>

      {open && (
        <dl className="px-3 pb-3 space-y-1.5 text-[11px] leading-relaxed border-t border-[var(--line)] pt-2">
          <Detail label="Methode" value={metric.method} />
          <Detail label="Eingaben" value={metric.inputs} />
          <Detail label="Datenumfang" value={`${metric.sampleSize} Beobachtung(en)`} />
          {metric.limitation && <Detail label="Grenzen" value={metric.limitation} warn />}
          {unavailable && (
            <Detail label="Nicht berechenbar" value={metric.unavailableReason ?? "kein Grund angegeben"} warn />
          )}
        </dl>
      )}
    </div>
  );
}

function Detail({ label, value, warn }: { label: string; value: string; warn?: boolean }) {
  return (
    <div className="flex gap-2">
      <dt className="text-[var(--fg-faint)] w-[104px] flex-shrink-0">{label}</dt>
      <dd className={cn("flex-1", warn ? "text-[var(--gold)]" : "text-[var(--fg-dim)]")}>{value}</dd>
    </div>
  );
}

/** Hinweisblock für Annahmen, Warnungen und Methodendefinitionen. */
export function NoteBlock({
  title,
  items,
  tone = "info",
}: {
  title: string;
  items: string[] | undefined;
  tone?: "info" | "warn" | "method";
}) {
  if (!items || items.length === 0) return null;
  const toneClass =
    tone === "warn"
      ? "border-[var(--gold)]/40 text-[var(--gold)]"
      : tone === "method"
      ? "border-[var(--line-2)] text-[var(--fg-faint)]"
      : "border-[var(--line-2)] text-[var(--fg-dim)]";
  return (
    <div className={cn("rounded-md border px-3 py-2 text-[11px] leading-relaxed", toneClass)}>
      <div className="font-medium mb-1">{title}</div>
      <ul className="space-y-0.5 list-disc pl-4">
        {items.map((t, i) => (
          <li key={i}>{t}</li>
        ))}
      </ul>
    </div>
  );
}

/** Kompakte Kennzahl für eine Kopfzeile. */
export function HeadlineStat({
  label,
  value,
  hint,
  tone,
}: {
  label: string;
  value: string;
  hint?: string;
  tone?: "good" | "bad" | "neutral";
}) {
  return (
    <div className="px-3 py-2 min-w-[120px]" title={hint}>
      <div className="text-[10px] uppercase tracking-wide text-[var(--fg-faint)]">{label}</div>
      <div
        className={cn(
          "mono text-base tabular-nums",
          tone === "good" && "text-[var(--key)]",
          tone === "bad" && "text-[var(--red)]",
          (!tone || tone === "neutral") && "text-[var(--fg)]"
        )}
      >
        {value}
      </div>
    </div>
  );
}

/** Deutlicher Hinweis, wenn eine Auswertung mangels Daten nicht möglich ist. */
export function Unavailable({ title, reason }: { title: string; reason?: string | null }) {
  return (
    <div className="rounded-md border border-dashed border-[var(--line-2)] px-4 py-6 text-center">
      <div className="text-sm text-[var(--fg-dim)]">{title}</div>
      {reason && <div className="text-[11px] text-[var(--fg-faint)] mt-1 max-w-xl mx-auto">{reason}</div>}
    </div>
  );
}
