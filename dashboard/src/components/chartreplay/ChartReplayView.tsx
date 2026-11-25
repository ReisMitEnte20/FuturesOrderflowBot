// Backtesting → „Chart & Replay“: EINE Ansicht für gespeicherte Backtest-Trades im Chart und das freie Tick-Replay
// eines Handelstags. Bündelt vorhandene Bausteine (BacktestChart-Marker, Journal, Tick-Replay-Engine, Orderflow-
// Arbeitsfläche, bisheriger Chart-Backtest) – kein zweiter Simulator, keine neue Backtest-Engine.
// URL: ?view=chart&mode=run&run=<id>[&trade=<n>] · ?view=chart&mode=day&day=YYYY-MM-DD[&overlay=<key>]
//      ?view=chart&mode=files (Orderflow-Lauf-Dateien) · ?view=chart&mode=quick (bisheriger „Chart-Backtest“)
//      ?view=chart&mode=sessions&srun=<id>[&day=YYYY-MM-DD][&trade=<n>] (London + New York, Pause übersprungen)
import { useState } from "react";
import { useSearchParams } from "react-router";
import { cn } from "@/lib/utils";
import type { ReplayFocus } from "@/lib/tradeReplay";
import { ResearchWorkspace } from "@/components/research/ResearchWorkspace";
import { TickReplayPanel } from "@/components/research/TickReplayPanel";
import { Backtest } from "@/pages/Backtest";
import { SavedRunChart } from "./SavedRunChart";
import { DayReplay } from "./DayReplay";
import { SessionsReplay } from "./SessionsReplay";

export type ChartMode = "run" | "day" | "sessions" | "files" | "quick";
const MODES: { key: ChartMode; label: string; hint: string }[] = [
  { key: "run", label: "Trades im Chart", hint: "gespeicherter Lauf" },
  { key: "day", label: "Handelstag abspielen", hint: "Tick für Tick" },
  { key: "sessions", label: "London + New York", hint: "beide Sessions, Pause übersprungen" },
  { key: "files", label: "Orderflow-Lauf-Dateien", hint: "Footprint-Arbeitsfläche" },
  { key: "quick", label: "Schnelltest auf Kerzen", hint: "bisheriger Chart-Backtest" },
];

export function ChartReplayView({ defaultMode = "run" }: { defaultMode?: ChartMode }) {
  const [params, setParams] = useSearchParams();
  const mode = (params.get("mode") as ChartMode | null) ?? (params.get("run") ? "run" : defaultMode);
  const update = (kv: Record<string, string | null>, replace = false) => {
    const p = new URLSearchParams(params);
    p.set("view", "chart");
    for (const [k, v] of Object.entries(kv)) { if (v == null) p.delete(k); else p.set(k, v); }
    setParams(p, { replace });
  };
  const trade = params.get("trade");
  return (
    <div className="p-4 space-y-3">
      <div className="flex flex-wrap items-center gap-2">
        <div className="flex flex-wrap gap-1" role="tablist" aria-label="Chart & Replay">
          {MODES.map((m) => (
            <button key={m.key} role="tab" aria-selected={mode === m.key} data-testid={`chart-mode-${m.key}`} onClick={() => update({ mode: m.key })}
              className={cn("h-[30px] rounded-md px-3 text-[12px]", mode === m.key ? "bg-[var(--panel-3)] border border-[var(--cyan)] text-[var(--fg)] font-medium" : "border border-[var(--line-2)] text-[var(--fg-dim)] hover:text-[var(--fg)]")}>
              {m.label} <span className="text-[10px] text-[var(--fg-faint)]">· {m.hint}</span>
            </button>
          ))}
        </div>
        <span className="text-[11px] text-[var(--fg-faint)]">Nur lesend · gespeicherte simulierte Fills · keine Orders</span>
      </div>
      {mode === "run" && (
        <SavedRunChart key={params.get("run") ?? ""} runId={params.get("run")} initialTrade={trade != null ? Number(trade) : null}
          onRun={(id) => update({ run: id, trade: null })}
          onOpenDayReplay={(d, runId) => update({ mode: "day", day: d, overlay: `report:${runId}` })} />
      )}
      {mode === "day" && (
        <DayReplay day={params.get("day")} onDay={(d) => update({ day: d }, !params.get("day"))} runKey={params.get("overlay")} onRunKey={(k) => update({ overlay: k })} />
      )}
      {mode === "sessions" && (
        <SessionsReplay runId={params.get("srun")} day={params.get("day")} trade={trade != null ? Number(trade) : null} onParams={(kv) => update(kv)} />
      )}
      {mode === "files" && <FilesMode />}
      {mode === "quick" && (
        <div className="-mx-4 -mb-4 min-h-[640px] border-t border-[var(--line)]">
          <p className="px-4 py-2 text-[11px] text-[var(--fg-dim)]">
            Bisheriger „Chart-Backtest“: schneller Kerzen-Lauf ohne gespeicherten Bericht (gleiche OHLC-Engine wie im Bericht). Für gespeicherte Läufe „Trades im Chart“ nutzen.
          </p>
          <Backtest />
        </div>
      )}
    </div>
  );
}

/** Bisherige Orderflow-Arbeitsfläche (Lauf-Dateien mit Footprint) samt Tick-Replay eines Journal-Trades – unverändert. */
function FilesMode() {
  const [focus, setFocus] = useState<ReplayFocus | null>(null);
  return (
    <div className="space-y-4">
      <ResearchWorkspace onOpenInReplay={setFocus} />
      <TickReplayPanel focus={focus} onClearFocus={() => setFocus(null)} />
    </div>
  );
}
