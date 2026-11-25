// Herkunftskennzeichnung: jede Zahl/Ansicht trägt genau eine der vier Klassen (plus TESTDATEN), nie gemischt.
import { PROV_CLASS, provClass, provDetail, type ProvClass } from "@/lib/marketdataApi";

export function ProvChip({ source, cls, title, testid }: { source?: string | null; cls?: ProvClass; title?: string; testid?: string }) {
  const c = cls ?? provClass(source);
  const p = PROV_CLASS[c];
  return (
    <span data-testid={testid} data-prov={c} title={title ?? `${p.label}${source ? ` · ${provDetail(source)}` : ""}`}
      className="inline-flex items-center gap-1 rounded-full border px-1.5 py-[1px] text-[10px] font-medium leading-4"
      style={{ borderColor: p.color, color: p.color }}>
      <span className="inline-block h-1.5 w-1.5 rounded-full" style={{ background: p.color }} />
      {p.short}
    </span>
  );
}

export function ProvLegend() {
  return (
    <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-[10px] text-[var(--fg-dim)]" data-testid="prov-legend">
      <span className="text-[var(--fg-faint)]">Herkunft:</span>
      {(["live", "history", "computed", "unavailable", "test"] as ProvClass[]).map((c) => (
        <span key={c} className="inline-flex items-center gap-1"><ProvChip cls={c} />{PROV_CLASS[c].label}</span>
      ))}
    </div>
  );
}
