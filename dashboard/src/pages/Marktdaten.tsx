// Marktdaten → Live | Aufzeichnung. Hauptweg des Dashboards: was der Bot aus ATAS sieht. Keine Strategie, keine Orders,
// kein Training auf dieser Seite. „Aufzeichnung“ öffnet einen vorhandenen London- oder New-York-Tag im selben Chart wie
// unter Backtesting (DayReplay), aber ohne Lauf-Überlagerung. Ein Quellenwechsel baut die Ansicht neu auf (keine alten
// Kerzen, Tape-Zeilen oder DOM-Level).
import { useSearchParams } from "react-router";
import { cn } from "@/lib/utils";
import { LiveMarket } from "@/components/marktdaten/LiveMarket";
import { DayReplay } from "@/components/chartreplay/DayReplay";
import { ProvLegend } from "@/components/marktdaten/Provenance";

type Tab = "live" | "aufzeichnung";

export function Marktdaten() {
  const [params, setParams] = useSearchParams();
  const tab: Tab = params.get("tab") === "aufzeichnung" ? "aufzeichnung" : "live";
  const day = params.get("day");
  const set = (patch: Record<string, string | null>) => {
    const n = new URLSearchParams(params);
    for (const [k, v] of Object.entries(patch)) { if (v == null) n.delete(k); else n.set(k, v); }
    setParams(n, { replace: false });
  };
  return (
    <div className="space-y-3 p-4" data-testid="marktdaten" data-tab={tab}>
      <div className="flex flex-wrap items-center gap-3">
        <div className="flex rounded-md border border-[var(--line-2)] p-0.5" role="tablist" aria-label="Marktdaten">
          {(["live", "aufzeichnung"] as Tab[]).map((t) => (
            <button key={t} role="tab" aria-selected={tab === t} data-testid={`md-tab-${t}`} onClick={() => set({ tab: t === "live" ? null : t })}
              className={cn("h-[28px] rounded px-4 text-[13px]", tab === t ? "bg-grad-brand font-semibold text-[var(--bg)]" : "text-[var(--fg-dim)] hover:text-[var(--fg)]")}>
              {t === "live" ? "Live" : "Aufzeichnung"}
            </button>
          ))}
        </div>
        <span className="text-[11px] text-[var(--fg-faint)]">
          {tab === "live" ? "ATAS → lokale Bridge → Backend → Dashboard · nur lesend" : "Vorhandener London- oder New-York-Tag aus den Collections · ohne Strategie"}
        </span>
      </div>
      {/* key: Quellenwechsel Live ↔ Aufzeichnung baut die Ansicht vollständig neu auf – nichts Altes bleibt stehen. */}
      {tab === "live" ? <LiveMarket key="live" /> : (
        <div key="rec" className="space-y-2" data-testid="md-recording">
          <ProvLegend />
          <DayReplay day={day} onDay={(d) => set({ day: d })} runKey={null} onRunKey={() => {}} marketOnly />
        </div>
      )}
    </div>
  );
}
