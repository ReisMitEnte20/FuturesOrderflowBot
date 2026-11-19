// Hauptbereich „Backtesting“: Strategie wählen → einmal „Vollständigen Backtest starten“ → gespeicherter Bericht.
// Ansichten: Strategie testen · Strategien vergleichen · Laufhistorie · Orderflow & Tick-Replay · Expertenansicht (Kampagnen/Holdout).
// Simulation-only: keine Orders, keine Broker-Anbindung.
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useLocation, useNavigate, useSearchParams } from "react-router";
import { cn } from "@/lib/utils";
import { backtestApi, type DataSourceDef, type InstrumentDef } from "@/lib/backtestApi";
import {
  backtestingApi, day, dt, money, num, pct,
  type BacktestingPreview, type BacktestingStartRequest, type CompareResult, type ReportRun, type ReportSummary, type StrategyDef,
} from "@/lib/backtestingApi";
import { ReportView, StatusChip, stepLabel } from "@/components/backtesting/ReportView";
import { LineChart } from "@/components/backtesting/charts";
import { ResearchWorkspace } from "@/components/research/ResearchWorkspace";
import { TickReplayPanel } from "@/components/research/TickReplayPanel";
import type { ReplayFocus } from "@/lib/tradeReplay";
import { Research } from "./Research";

type View = "test" | "compare" | "history" | "orderflow" | "research";
const VIEWS: { key: View; label: string }[] = [
  { key: "test", label: "Strategie testen" },
  { key: "compare", label: "Strategien vergleichen" },
  { key: "history", label: "Laufhistorie" },
  { key: "orderflow", label: "Orderflow & Tick-Replay" },
  { key: "research", label: "Expertenansicht (Kampagnen & Holdout)" },
];
const LAST_REPORT_KEY = "backtesting:lastRunId";
const isTerminal = (s: string) => s === "Completed" || s === "Failed" || s === "Cancelled";

export function Backtesting({ defaultView = "test" }: { defaultView?: View }) {
  const [params, setParams] = useSearchParams();
  const location = useLocation();
  const legacyPath = location.pathname.startsWith("/research");   // alte /research-Links: bisherige Seite vollständig
  const view = (params.get("view") as View | null) ?? defaultView;
  const setView = (v: View) => { const p = new URLSearchParams(params); p.set("view", v); if (v !== "test") p.delete("run"); setParams(p); };
  return (
    <div className="flex flex-col h-full min-h-0">
      <div className="flex-shrink-0 border-b border-[var(--line)] bg-[var(--bg-2)]">
        <div className="flex flex-wrap items-center gap-2 px-4 py-2">
          <span className="text-sm font-medium text-[var(--fg)]">Backtesting</span>
          <span className="text-[11px] text-[var(--fg-faint)]">Ein Klick · alle anwendbaren Prüfungen · gespeicherter Bericht · Simulation, keine Orders</span>
          <div className="ml-auto flex flex-wrap gap-1" role="tablist">
            {VIEWS.map((v) => (
              <button key={v.key} role="tab" aria-selected={view === v.key} onClick={() => setView(v.key)}
                className={cn("h-[28px] rounded-md px-3 text-xs", view === v.key ? "bg-grad-brand text-[var(--bg)] font-semibold" : "border border-[var(--line-2)] text-[var(--fg-dim)] hover:text-[var(--fg)]")}>
                {v.label}
              </button>
            ))}
          </div>
        </div>
      </div>
      <div className="flex-1 min-h-0 overflow-y-auto">
        {view === "test" && <TestView />}
        {view === "compare" && <CompareView />}
        {view === "history" && <HistoryView />}
        {view === "orderflow" && <OrderflowView />}
        {view === "research" && <Research hideWorkspace={!legacyPath} />}
      </div>
    </div>
  );
}

// =============================================================================================
// Strategie testen
// =============================================================================================

const intOrNull = (s: string) => { const v = parseInt(s, 10); return Number.isFinite(v) && s.trim() !== "" ? v : null; };
const numOrNull = (s: string) => { const v = parseFloat(s); return Number.isFinite(v) && s.trim() !== "" ? v : null; };

interface Form {
  strategy: string; params: Record<string, string>; symbol: string; dataSourceId: string; timeframeMinutes: number; fromUtc: string; toUtc: string;
  quantity: number; initialBalance: number; stopLossTicks: string; takeProfitTicks: string; slippageTicks: string; feePerSide: string; applyFees: boolean;
  holdoutShare: number; trainSessions: number; testSessions: number; seed: number; simulationIterations: number; minTrades: number; minTrainTrades: number;
  selectionMetric: string;
}

const DEFAULT_FORM: Form = {
  strategy: "movingaverage", params: { FastPeriod: "9", SlowPeriod: "21" }, symbol: "MES", dataSourceId: "", timeframeMinutes: 5,
  fromUtc: "2026-05-01T00:00", toUtc: "2026-06-12T00:00", quantity: 1, initialBalance: 10000, stopLossTicks: "", takeProfitTicks: "", slippageTicks: "",
  feePerSide: "", applyFees: true, holdoutShare: 0.2, trainSessions: 20, testSessions: 5, seed: 20261119, simulationIterations: 5000, minTrades: 30,
  minTrainTrades: 5, selectionMetric: "netprofit",
};

function toRequest(f: Form): BacktestingStartRequest {
  return {
    strategy: f.strategy, params: f.params, symbol: f.symbol, dataSourceId: f.dataSourceId, path: null, timeframeMinutes: f.timeframeMinutes,
    fromUtc: f.fromUtc.trim() ? `${f.fromUtc}:00Z` : null, toUtc: f.toUtc.trim() ? `${f.toUtc}:00Z` : null, maxRows: null,
    quantity: f.quantity, initialBalance: f.initialBalance, stopLossTicks: intOrNull(f.stopLossTicks), takeProfitTicks: intOrNull(f.takeProfitTicks),
    slippageTicks: numOrNull(f.slippageTicks), feePerSideOverride: numOrNull(f.feePerSide), applyFees: f.applyFees,
    holdoutShare: f.holdoutShare, trainSessions: f.trainSessions, testSessions: f.testSessions, seed: f.seed, simulationIterations: f.simulationIterations,
    minTrades: f.minTrades, minTrainTrades: f.minTrainTrades, selectionMetric: f.selectionMetric, benchmarkId: null, isDemo: false,
  };
}

function TestView() {
  const [params, setParams] = useSearchParams();
  const navigate = useNavigate();
  const [strategies, setStrategies] = useState<StrategyDef[]>([]);
  const [sources, setSources] = useState<DataSourceDef[]>([]);
  const [instruments, setInstruments] = useState<InstrumentDef[]>([]);
  const [form, setForm] = useState<Form>(DEFAULT_FORM);
  const [advanced, setAdvanced] = useState(false);
  const [preview, setPreview] = useState<BacktestingPreview | null>(null);
  const [previewing, setPreviewing] = useState(false);
  const [run, setRun] = useState<ReportRun | null>(null);
  const [progress, setProgress] = useState<number | null>(null);
  const [starting, setStarting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const startLock = useRef(false);
  const pollRef = useRef<number | null>(null);
  const set = <K extends keyof Form>(k: K, v: Form[K]) => setForm((f) => ({ ...f, [k]: v }));
  const runId = params.get("run");

  useEffect(() => {
    const ac = new AbortController();
    Promise.all([backtestingApi.strategies(ac.signal), backtestApi.dataSources(), backtestApi.instruments()])
      .then(([st, src, inst]) => {
        setStrategies(st); setSources(src); setInstruments(inst);
        const first = src.find((s) => s.available) ?? src[0];
        if (first) setForm((f) => ({ ...f, dataSourceId: f.dataSourceId || first.id, symbol: inst.find((i) => i.symbol === f.symbol)?.symbol ?? inst[0]?.symbol ?? f.symbol }));
      })
      .catch((e) => !ac.signal.aborted && setError(String(e)));
    return () => ac.abort();
  }, []);

  // Gespeicherten bzw. laufenden Auftrag aus der URL (oder dem letzten Lauf) wiederfinden — überlebt Neuladen.
  useEffect(() => {
    if (!runId) {
      const last = localStorage.getItem(LAST_REPORT_KEY);
      if (last) { const p = new URLSearchParams(params); p.set("run", last); setParams(p, { replace: true }); }
      return;
    }
    let stop = false;
    const tick = async () => {
      try {
        const res = await backtestingApi.report(runId);
        if (stop) return;
        if (!res.ok || !res.run) { setError(res.error ?? "Bericht nicht gefunden."); localStorage.removeItem(LAST_REPORT_KEY); return; }
        setRun(res.run);
        if (!isTerminal(res.run.status)) {
          const jid = res.jobId ?? res.run.jobId;
          if (jid) backtestingApi.job(jid).then((j) => !stop && setProgress(j?.progress ?? null)).catch(() => {});
          pollRef.current = window.setTimeout(tick, 900);
        } else setProgress(null);
      } catch { if (!stop) pollRef.current = window.setTimeout(tick, 2000); }
    };
    tick();
    return () => { stop = true; if (pollRef.current) clearTimeout(pollRef.current); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [runId]);

  const strategy = strategies.find((s) => s.id === form.strategy);
  const request = useMemo(() => toRequest(form), [form]);

  // Vorschau (Datenumfang, Kostenprofil, mögliche Prüfungen) — verzögert nach Änderungen.
  useEffect(() => {
    if (!form.dataSourceId) return;
    const ac = new AbortController();
    const h = window.setTimeout(() => {
      setPreviewing(true);
      backtestingApi.preview(request, ac.signal).then((p) => setPreview(p)).catch((e) => !ac.signal.aborted && setPreview({ ok: false, error: String(e) } as BacktestingPreview))
        .finally(() => !ac.signal.aborted && setPreviewing(false));
    }, 500);
    return () => { clearTimeout(h); ac.abort(); };
  }, [request, form.dataSourceId]);

  const chooseStrategy = (id: string) => {
    const s = strategies.find((x) => x.id === id);
    setForm((f) => ({ ...f, strategy: id, params: Object.fromEntries((s?.params ?? []).map((p) => [p.key, p.default])) }));
  };

  const running = run != null && !isTerminal(run.status);
  const start = useCallback(async () => {
    if (startLock.current) return;            // Doppelklick: kein zweiter Auftrag (zusätzlich serverseitig abgesichert)
    startLock.current = true; setStarting(true); setError(null); setNotice(null);
    try {
      const res = await backtestingApi.start(request);
      if (!res.ok || !res.run) { setError(res.error ?? "Start fehlgeschlagen."); return; }
      if (res.alreadyRunning) setNotice("Dieser Auftrag läuft bereits — es wurde kein zweiter gestartet.");
      localStorage.setItem(LAST_REPORT_KEY, res.run.runId);
      setRun(res.run);
      const p = new URLSearchParams(params); p.set("view", "test"); p.set("run", res.run.runId); setParams(p);
    } catch (e) { setError(e instanceof Error ? e.message : String(e)); }
    finally { setStarting(false); window.setTimeout(() => { startLock.current = false; }, 400); }
  }, [request, params, setParams]);

  const cancel = async () => { if (run) await backtestingApi.cancel(run.runId).catch(() => {}); };

  return (
    <div className="p-4 space-y-4">
      {error && <div className="rounded-lg border border-[var(--red)] bg-[var(--panel)] p-3 text-xs text-[var(--red)]">{error}</div>}
      {notice && <div className="rounded-lg border border-[var(--gold)] bg-[var(--panel)] p-3 text-xs text-[var(--gold)]">{notice}</div>}

      <div className="grid gap-4 xl:grid-cols-[minmax(0,1.1fr)_minmax(0,1fr)]">
        {/* Auswahl */}
        <div className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3 space-y-3">
          <div className="grid gap-3 sm:grid-cols-2">
            <Field label="Strategie"><Select value={form.strategy} onChange={chooseStrategy} options={strategies.map((s) => ({ value: s.id, label: s.name }))} /></Field>
            <Field label="Instrument"><Select value={form.symbol} onChange={(v) => set("symbol", v)} options={(instruments.length ? instruments.map((i) => i.symbol) : [form.symbol]).map((s) => ({ value: s, label: s }))} /></Field>
            <Field label="Datensatz"><Select value={form.dataSourceId} onChange={(v) => set("dataSourceId", v)} options={sources.map((s) => ({ value: s.id, label: s.label + (s.available ? "" : " (nicht vorhanden)") }))} /></Field>
            <Field label="Kerzen (Minuten)"><Input type="number" value={String(form.timeframeMinutes)} onChange={(v) => set("timeframeMinutes", parseInt(v, 10) || 5)} /></Field>
            <Field label="Von (UTC)"><Input type="datetime-local" value={form.fromUtc} onChange={(v) => set("fromUtc", v)} /></Field>
            <Field label="Bis (UTC, exklusiv)"><Input type="datetime-local" value={form.toUtc} onChange={(v) => set("toUtc", v)} /></Field>
          </div>
          {strategy && (
            <div className="rounded-md border border-[var(--line)] p-2">
              <p className="text-[11px] text-[var(--fg-dim)]">{strategy.description}</p>
              {strategy.params.length > 0 ? (
                <div className="mt-2 grid gap-2 sm:grid-cols-3">
                  {strategy.params.map((p) => (
                    <Field key={p.key} label={p.label}><Input value={form.params[p.key] ?? p.default} onChange={(v) => set("params", { ...form.params, [p.key]: v })} /></Field>
                  ))}
                </div>
              ) : <p className="mt-1 text-[10px] text-[var(--fg-faint)]">Keine einstellbaren Parameter — kein Parametersweep nötig.</p>}
            </div>
          )}
          <button onClick={() => setAdvanced((a) => !a)} className="text-[11px] text-[var(--key)]">{advanced ? "▾ Erweitert ausblenden" : "▸ Erweitert (Annahmen ändern — für einen normalen Lauf nicht nötig)"}</button>
          {advanced && (
            <div className="grid gap-2 sm:grid-cols-3 lg:grid-cols-4 border-t border-[var(--line)] pt-2">
              <Field label="Kontrakte"><Input type="number" value={String(form.quantity)} onChange={(v) => set("quantity", parseInt(v, 10) || 1)} /></Field>
              <Field label="Startkapital"><Input type="number" value={String(form.initialBalance)} onChange={(v) => set("initialBalance", parseFloat(v) || 0)} /></Field>
              <Field label="Stop (Ticks, leer=Profil)"><Input value={form.stopLossTicks} onChange={(v) => set("stopLossTicks", v)} /></Field>
              <Field label="Ziel (Ticks, leer=Profil)"><Input value={form.takeProfitTicks} onChange={(v) => set("takeProfitTicks", v)} /></Field>
              <Field label="Slippage (Ticks, leer=Profil)"><Input value={form.slippageTicks} onChange={(v) => set("slippageTicks", v)} /></Field>
              <Field label="Gebühr/Seite (leer=Profil)"><Input value={form.feePerSide} onChange={(v) => set("feePerSide", v)} /></Field>
              <Field label="Gebühren anwenden"><Select value={form.applyFees ? "1" : "0"} onChange={(v) => set("applyFees", v === "1")} options={[{ value: "1", label: "ja" }, { value: "0", label: "nein (brutto)" }]} /></Field>
              <Field label="Holdout-Anteil (reserviert)"><Input type="number" value={String(form.holdoutShare)} onChange={(v) => set("holdoutShare", Math.min(0.5, Math.max(0, parseFloat(v) || 0)))} /></Field>
              <Field label="Walk-forward Training (Sessions)"><Input type="number" value={String(form.trainSessions)} onChange={(v) => set("trainSessions", parseInt(v, 10) || 1)} /></Field>
              <Field label="Walk-forward Test (Sessions)"><Input type="number" value={String(form.testSessions)} onChange={(v) => set("testSessions", parseInt(v, 10) || 1)} /></Field>
              <Field label="Auswahlkriterium (Training)"><Select value={form.selectionMetric} onChange={(v) => set("selectionMetric", v)} options={["netprofit", "sharpe", "sortino", "calmar", "cagr"].map((m) => ({ value: m, label: m }))} /></Field>
              <Field label="Mindesttrades je Training"><Input type="number" value={String(form.minTrainTrades)} onChange={(v) => set("minTrainTrades", parseInt(v, 10) || 0)} /></Field>
              <Field label="Mindesttrades (Einordnung)"><Input type="number" value={String(form.minTrades)} onChange={(v) => set("minTrades", parseInt(v, 10) || 0)} /></Field>
              <Field label="Simulationen je Verfahren"><Input type="number" value={String(form.simulationIterations)} onChange={(v) => set("simulationIterations", parseInt(v, 10) || 5000)} /></Field>
              <Field label="Seed"><Input type="number" value={String(form.seed)} onChange={(v) => set("seed", parseInt(v, 10) || 0)} /></Field>
            </div>
          )}
        </div>

        {/* Vorab: Datenumfang, Kostenprofil, mögliche Prüfungen */}
        <div className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3 space-y-2">
          <div className="flex items-center gap-2">
            <h3 className="text-[13px] font-medium text-[var(--fg)]">Vor dem Start</h3>
            {previewing && <span className="text-[10px] text-[var(--cyan)] animate-pulse">Daten werden geprüft …</span>}
          </div>
          {!preview ? <p className="text-[11px] text-[var(--fg-faint)]">Wähle Datensatz und Zeitraum.</p> : !preview.ok ? (
            <p className="text-[11px] text-[var(--red)]">{preview.error}</p>
          ) : (
            <>
              <div className="grid grid-cols-2 gap-2 text-[11px]">
                <Info label="Datenumfang" value={`${num(preview.bars, 0)} Kerzen · ${preview.sessions} Handelstage`} hint={`${dt(preview.from)} – ${dt(preview.to)} UTC · ${preview.dataKind}`} />
                <Info label="Aufteilung" value={`${preview.developmentSessions} Entwicklung · ${preview.holdoutSessions} Holdout`}
                  hint={preview.holdoutSessions ? `Holdout ab ${day(preview.holdoutFrom)} reserviert, wird im normalen Lauf nicht ausgewertet` : "kein Holdout"} />
                <Info label="Kostenprofil" value={preview.costs ? `${money(preview.costs.feePerSide)} je Seite · ${num(preview.costs.slippageTicks)} Ticks Slippage` : "–"}
                  hint={preview.costs ? `${money(preview.costs.feeRoundTrip)} Round-Turn · Tick ${num(preview.costs.tickSize, 4)} = ${money(preview.costs.tickValue)}${preview.feeIsExample ? " · Beispielprofil, keine echten Broker-Kosten" : ""}` : undefined} />
                <Info label="Umfang" value={`${preview.candidates} Walk-forward-Kandidaten`} hint={`ca. ${num(preview.estimatedEngineRuns, 0)} Engine-Läufe auf denselben, einmal geladenen Kerzen`} />
              </div>
              <ul className="space-y-0.5">
                {preview.checks.map((c) => (
                  <li key={c.key} className="flex items-start gap-2 text-[11px]">
                    <span className={cn("mt-0.5 rounded px-1 text-[9px] whitespace-nowrap", c.status === "possible" ? "bg-[rgba(60,240,160,0.15)] text-[var(--key)]" : c.status === "insufficient" ? "bg-[rgba(240,195,90,0.15)] text-[var(--gold)]" : c.status === "reserved" ? "bg-[rgba(155,140,255,0.15)] text-[var(--purple)]" : "bg-[var(--panel-3)] text-[var(--fg-dim)]")}>
                      {c.status === "possible" ? "wird geprüft" : c.status === "insufficient" ? "zu wenig Daten" : c.status === "reserved" ? "reserviert" : "nicht anwendbar"}
                    </span>
                    <span className="text-[var(--fg)]">{c.label}</span>
                    {c.reason && <span className="text-[var(--fg-faint)]">— {c.reason}</span>}
                  </li>
                ))}
              </ul>
            </>
          )}
          <div className="flex items-center gap-2 pt-1">
            {running ? (
              <button onClick={cancel} className="h-9 px-4 rounded-md bg-[var(--red)] text-black text-sm font-medium">Auftrag abbrechen</button>
            ) : (
              <button onClick={start} disabled={starting || !preview?.ok}
                className="h-9 px-5 rounded-md bg-grad-brand text-[var(--bg)] text-sm font-semibold disabled:opacity-50">
                {starting ? "Wird gestartet …" : "Vollständigen Backtest starten"}
              </button>
            )}
            <span className="text-[10px] text-[var(--fg-faint)]">Ein Klick startet alle anwendbaren Analysen im Backend.</span>
          </div>
        </div>
      </div>

      {run && <RunHeader run={run} progress={progress} onOpenHistory={() => navigate("/backtesting?view=history")} />}
      {run && <ReportView run={run} onOpenHoldout={() => navigate(`/backtesting?view=research&run=${encodeURIComponent(run.runId)}`)} />}
    </div>
  );
}

function RunHeader({ run, progress, onOpenHistory }: { run: ReportRun; progress: number | null; onOpenHistory: () => void }) {
  const done = run.steps.filter((s) => !["Pending", "Running"].includes(s.status)).length;
  return (
    <div className="rounded-lg border border-[var(--line-2)] bg-[var(--panel)] p-3 space-y-2">
      <div className="flex flex-wrap items-center gap-2 text-[12px]">
        <span className="text-[var(--fg)] font-medium">Auftrag {run.runId}</span>
        <StatusChip status={run.status} />
        <span className="text-[var(--fg-faint)]">gestartet {dt(run.createdUtc)} UTC{run.completedUtc ? ` · beendet ${dt(run.completedUtc)} UTC` : ""}</span>
        <span className="text-[var(--fg-faint)]">{done}/{run.steps.length} Abschnitte</span>
        {progress != null && <span className="text-[var(--cyan)] mono">{Math.round(progress * 100)} %</span>}
        <button onClick={onOpenHistory} className="ml-auto text-[11px] text-[var(--key)]">Laufhistorie →</button>
      </div>
      {progress != null && <div className="h-1.5 rounded bg-[var(--panel-3)]"><div className="h-1.5 rounded bg-grad-brand" style={{ width: `${Math.round(progress * 100)}%` }} /></div>}
      {run.statusReason && <p className="text-[11px] text-[var(--fg-dim)]">{run.statusReason}</p>}
      <div className="flex flex-wrap gap-1">
        {run.steps.map((s) => (
          <a key={s.key} href={`#sec-${s.key}`} className="flex items-center gap-1 rounded border border-[var(--line)] px-1.5 py-0.5 text-[10px] text-[var(--fg-dim)]" title={s.reason ?? ""}>
            {s.key === "oos" ? "06 Walk-forward-Testdaten" : s.key === "holdout" ? "Holdout (unbenutzt)" : stepLabel(s).split(" ").slice(0, 2).join(" ")} <StatusChip status={s.status} stepKey={s.key} />
          </a>
        ))}
      </div>
    </div>
  );
}

// =============================================================================================
// Laufhistorie
// =============================================================================================

function useReports() {
  const [list, setList] = useState<ReportSummary[] | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const load = useCallback(() => backtestingApi.reports().then(setList).catch((e) => setErr(String(e))), []);
  useEffect(() => { load(); }, [load]);
  return { list, err, load };
}

function fig(r: ReportSummary, key: string) { return r.figures.find((f) => f.key === key)?.value ?? null; }

function HistoryView() {
  const navigate = useNavigate();
  const { list, err, load } = useReports();
  const [sel, setSel] = useState<string[]>([]);
  useEffect(() => { const h = window.setInterval(load, 4000); return () => clearInterval(h); }, [load]);
  return (
    <div className="p-4 space-y-3">
      {err && <p className="text-xs text-[var(--red)]">{err}</p>}
      <div className="flex items-center gap-2">
        <h2 className="text-sm font-medium text-[var(--fg)]">Laufhistorie</h2>
        <span className="text-[11px] text-[var(--fg-faint)]">Alle gespeicherten Aufträge (Ein-Knopf-Berichte und klassische Research-Läufe).</span>
        <button disabled={sel.length < 2} onClick={() => navigate(`/backtesting?view=compare&ids=${sel.map(encodeURIComponent).join(",")}`)}
          className="ml-auto h-8 rounded-md bg-grad-brand px-3 text-xs font-semibold text-[var(--bg)] disabled:opacity-40">{sel.length} Berichte vergleichen</button>
      </div>
      {!list ? <p className="text-xs text-[var(--fg-faint)]">Lade …</p> : (
        <div className="overflow-x-auto rounded-lg border border-[var(--line)] bg-[var(--panel)]">
          <table className="w-full text-[11px] mono">
            <thead><tr className="text-left text-[var(--fg-faint)]">{["", "Gestartet (UTC)", "Status", "Strategie", "Parameter", "Daten", "Netto (Basis)", "Walk-forward-Test-Netto", "WF-Testsess.", "Abschnitte", ""].map((h, i) => <th key={i} className="px-2 py-1 font-normal">{h}</th>)}</tr></thead>
            <tbody>
              {list.map((r) => (
                <tr key={r.runId} className="border-t border-[var(--line)]">
                  <td className="px-2">{r.profile === "full" && r.status === "Completed" && (
                    <input type="checkbox" aria-label={`Bericht ${r.runId} auswählen`} checked={sel.includes(r.runId)} onChange={(e) => setSel((s) => e.target.checked ? [...s, r.runId] : s.filter((x) => x !== r.runId))} />)}</td>
                  <td className="px-2 py-1 whitespace-nowrap">{dt(r.createdUtc)}</td>
                  <td className="px-2"><StatusChip status={r.status} /></td>
                  <td className="px-2 whitespace-nowrap">{r.strategyName}{r.profile !== "full" && <span className="ml-1 text-[var(--fg-faint)]">(klassisch)</span>}</td>
                  <td className="px-2">{Object.entries(r.parameters).map(([k, v]) => `${k}=${v}`).join(", ") || "—"}</td>
                  <td className="px-2 whitespace-nowrap">{r.symbol} {r.fromUtc ? day(r.fromUtc) : "…"} – {r.toUtc ? day(r.toUtc) : "…"}</td>
                  <td className="px-2">{money(fig(r, "net"))}</td>
                  <td className="px-2">{money(fig(r, "oos"))}</td>
                  <td className="px-2">{num(fig(r, "sessions"), 0)}</td>
                  <td className="px-2">{r.stepsDone}/{r.stepsTotal}</td>
                  <td className="px-2"><button className="text-[var(--cyan)]" onClick={() => {
                    localStorage.setItem(LAST_REPORT_KEY, r.runId);
                    navigate(r.profile === "full" ? `/backtesting?view=test&run=${encodeURIComponent(r.runId)}` : `/backtesting?view=research&run=${encodeURIComponent(r.runId)}`);
                  }}>öffnen</button></td>
                </tr>
              ))}
              {list.length === 0 && <tr><td colSpan={11} className="p-3 text-center text-[var(--fg-faint)]">Noch keine gespeicherten Aufträge.</td></tr>}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
}

// =============================================================================================
// Strategien vergleichen
// =============================================================================================

function CompareView() {
  const [params, setParams] = useSearchParams();
  const { list } = useReports();
  const ids = useMemo(() => (params.get("ids") ?? "").split(",").filter(Boolean), [params]);
  const [res, setRes] = useState<CompareResult | null>(null);
  const [busy, setBusy] = useState(false);
  const toggle = (id: string, on: boolean) => {
    const next = on ? [...ids, id] : ids.filter((x) => x !== id);
    const p = new URLSearchParams(params); p.set("ids", next.join(",")); setParams(p); setRes(null);
  };
  const compare = useCallback(async () => {
    setBusy(true);
    try { setRes(await backtestingApi.compare(ids)); } finally { setBusy(false); }
  }, [ids]);
  useEffect(() => { if (ids.length >= 2 && !res) compare(); }, []); // eslint-disable-line react-hooks/exhaustive-deps
  const full = (list ?? []).filter((r) => r.profile === "full" && r.status === "Completed");
  return (
    <div className="p-4 space-y-3">
      <div className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3 space-y-2">
        <div className="flex items-center gap-2">
          <h2 className="text-sm font-medium text-[var(--fg)]">Gespeicherte Berichte auswählen</h2>
          <span className="text-[11px] text-[var(--fg-faint)]">Der Vergleich liest nur gespeicherte Ergebnisse — es wird nichts neu simuliert.</span>
          <button disabled={ids.length < 2 || busy} onClick={compare} className="ml-auto h-8 rounded-md bg-grad-brand px-3 text-xs font-semibold text-[var(--bg)] disabled:opacity-40">
            {busy ? "Vergleiche …" : `${ids.length} Berichte vergleichen`}
          </button>
        </div>
        {full.length === 0 ? <p className="text-[11px] text-[var(--fg-faint)]">Noch keine fertigen Berichte.</p> : (
          <div className="grid gap-1 md:grid-cols-2">
            {full.map((r) => (
              <label key={r.runId} className={cn("flex items-center gap-2 rounded border px-2 py-1 text-[11px]", ids.includes(r.runId) ? "border-[var(--cyan)]" : "border-[var(--line)]")}>
                <input type="checkbox" checked={ids.includes(r.runId)} onChange={(e) => toggle(r.runId, e.target.checked)} />
                <span className="text-[var(--fg)]">{r.strategyName}</span>
                <span className="text-[var(--fg-faint)]">{Object.entries(r.parameters).map(([k, v]) => `${k}=${v}`).join(", ")}</span>
                <span className="ml-auto mono text-[var(--fg-faint)]">{dt(r.createdUtc)}</span>
              </label>
            ))}
          </div>
        )}
      </div>
      {res && !res.ok && <p className="text-xs text-[var(--red)]">{res.error}</p>}
      {res && res.ok && <CompareResultView res={res} />}
    </div>
  );
}

function CompareResultView({ res }: { res: CompareResult }) {
  const curveRows = res.rows.slice(0, 2);
  return (
    <div className="space-y-3">
      <div className={cn("rounded-lg border p-3 text-[11px] space-y-1", res.fair ? "border-[var(--key)] bg-[var(--panel)]" : "border-[var(--gold)] bg-[var(--panel)]")}>
        <div className="text-[12px] font-medium text-[var(--fg)]">{res.fair ? "Faire, gemeinsame Basis" : "Keine vollständig faire gemeinsame Basis"} — {res.basis} ({res.commonSessions.length} Sessions)</div>
        {res.problems.map((p, i) => <div key={i} className="text-[var(--gold)]">⚠ {p}</div>)}
        <div className="text-[var(--fg-dim)]">{res.conditions.join(" · ")}</div>
        <div className="text-[var(--fg-faint)]">Keine neue Simulation: Engine-Läufe vor dem Vergleich {res.engineRunsBefore}, danach {res.engineRunsAfter}.</div>
      </div>
      {res.findings.length > 0 && <ul className="list-disc pl-4 text-[12px] text-[var(--fg)]">{res.findings.map((f, i) => <li key={i}>{f}</li>)}</ul>}
      {res.commonSessions.length > 0 && <>
        <div className="grid gap-3 xl:grid-cols-3">
          <CmpTable title="Rendite (gemeinsame Sessions)" rows={res.rows} cols={[
            ["Netto", (r) => money(r.net)], ["Trades", (r) => r.trades], ["Netto je Session", (r) => money(r.netPerSession ?? null)], ["Erwartung je Trade", (r) => money(r.expectancyPerTrade ?? null)],
          ]} />
          <CmpTable title="Risiko (gemeinsame Sessions)" rows={res.rows} cols={[
            ["Max. Drawdown", (r) => money(r.maxDrawdown)], ["Schlechteste Session", (r) => money(r.worstSession)], ["Streuung je Session", (r) => money(r.sessionStdDev ?? null)], ["Netto / Max. DD", (r) => num(r.netToDrawdown ?? null)],
          ]} />
          <CmpTable title="Stabilität (gemeinsame Sessions)" rows={res.rows} cols={[
            ["Sessions positiv", (r) => pct(r.positiveSessionShare ?? null, 0)], ["Anteil beste Session", (r) => pct(r.bestSessionShare ?? null, 0)], ["Sessions", (r) => r.sessions],
          ]} />
        </div>
        <LineChart ariaLabel="Kumuliertes Netto auf den gemeinsamen OOS-Sessions" timeAxis={false} zeroLine
          series={curveRows.map((r, i) => ({ name: r.label, x: r.equity.map((_, k) => k + 1), y: r.equity, color: i === 0 ? "#22b8f0" : "#f0c35a", dashed: i === 1 }))} />
        {res.rows.length > 2 && <p className="text-[10px] text-[var(--fg-faint)]">Diagramm zeigt die ersten zwei Berichte; alle Werte stehen in den Tabellen.</p>}
      </>}
      <CmpTable title="Zur Einordnung: eigene Berichtswerte (NICHT gemeinsame Basis — unterschiedliche Zeiträume möglich)" rows={res.rows} cols={[
        ["Netto Basislauf", (r) => money(r.ownBaseNet ?? null)], ["Max. DD Basislauf", (r) => money(r.ownBaseDrawdown ?? null)], ["Simulation P95-DD", (r) => money(r.ownSimP95Drawdown ?? null)],
      ]} />
    </div>
  );
}

function CmpTable({ title, rows, cols }: { title: string; rows: CompareResult["rows"]; cols: [string, (r: CompareResult["rows"][number]) => React.ReactNode][] }) {
  return (
    <div className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-2 overflow-x-auto">
      <div className="mb-1 text-[12px] font-medium text-[var(--fg)]">{title}</div>
      <table className="w-full text-[11px] mono">
        <thead><tr className="text-left text-[var(--fg-faint)]"><th className="pr-2 font-normal">Bericht</th>{cols.map(([h]) => <th key={h} className="pr-2 font-normal">{h}</th>)}</tr></thead>
        <tbody>{rows.map((r) => <tr key={r.runId} className="border-t border-[var(--line)]"><td className="pr-2 py-1 font-sans text-[var(--fg)]">{r.label}</td>{cols.map(([h, f]) => <td key={h} className="pr-2">{f(r)}</td>)}</tr>)}</tbody>
      </table>
    </div>
  );
}

// =============================================================================================
// Orderflow & Tick-Replay (bisherige Research-Arbeitsfläche, unverändert eingebunden)
// =============================================================================================

function OrderflowView() {
  const [focus, setFocus] = useState<ReplayFocus | null>(null);
  return (
    <div className="p-4 space-y-4">
      <ResearchWorkspace onOpenInReplay={setFocus} />
      <TickReplayPanel focus={focus} onClearFocus={() => setFocus(null)} />
    </div>
  );
}

// ---- kleine Formular-Bausteine ----
function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return <label className="block"><span className="mb-0.5 block text-[10px] text-[var(--fg-faint)]">{label}</span>{children}</label>;
}
function Input({ value, onChange, type = "text" }: { value: string; onChange: (v: string) => void; type?: string }) {
  return <input type={type} value={value} onChange={(e) => onChange(e.target.value)} className="h-8 w-full rounded-md border border-[var(--line-2)] bg-[var(--bg-2)] px-2 text-xs text-[var(--fg)] mono" />;
}
function Select({ value, onChange, options }: { value: string; onChange: (v: string) => void; options: { value: string; label: string }[] }) {
  return (
    <select value={value} onChange={(e) => onChange(e.target.value)} className="h-8 w-full rounded-md border border-[var(--line-2)] bg-[var(--bg-2)] px-2 text-xs text-[var(--fg)]">
      {options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
    </select>
  );
}
function Info({ label, value, hint }: { label: string; value: string; hint?: string }) {
  return (
    <div className="rounded-md border border-[var(--line)] bg-[var(--bg-2)] px-2 py-1.5">
      <div className="text-[10px] text-[var(--fg-faint)]">{label}</div>
      <div className="text-[12px] text-[var(--fg)]">{value}</div>
      {hint && <div className="text-[10px] text-[var(--fg-faint)] leading-snug">{hint}</div>}
    </div>
  );
}
