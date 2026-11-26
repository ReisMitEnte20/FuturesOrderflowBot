// Hauptbereich „Backtesting“: Strategie wählen → einmal „Vollständigen Backtest starten“ → gespeicherter Bericht.
// Hauptansichten: Strategie testen · Strategien vergleichen · Chart & Replay (gespeicherte Trades im Chart, Handelstag
// abspielen, Orderflow-Lauf-Dateien, Schnelltest = bisheriger Chart-Backtest). Sekundär: Alle Läufe (?view=history),
// Expertenansicht (?view=research). Alte Links bleiben gültig: ?view=orderflow → Chart & Replay (Orderflow-Lauf-Dateien),
// /backtest → Chart & Replay (Schnelltest), /research → Expertenansicht.
// Simulation-only: keine Orders, keine Broker-Anbindung.
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useLocation, useNavigate, useSearchParams } from "react-router";
import { cn } from "@/lib/utils";
import { backtestApi, type DataSourceDef, type InstrumentDef } from "@/lib/backtestApi";
import {
  backtestingApi, day, dt, money, num, pct,
  type BacktestingPreview, type BacktestingStartRequest, type CompareResult, type ReportRun, type ReportSummary, type StrategyDef,
} from "@/lib/backtestingApi";
import { checkCounts, groupChecks, isHoldout, paramSummary, plannedText, previewFacts, startDecision, testedPeriod, type CheckGroup } from "@/lib/backtestingStart";
import { ReportView, StatusChip, stepLabel } from "@/components/backtesting/ReportView";
import { LineChart } from "@/components/backtesting/charts";
import { ChartReplayView } from "@/components/chartreplay/ChartReplayView";
import { SessionsBacktest } from "@/components/sessions/SessionsBacktest";
import { Research } from "./Research";
import { MtfBacktest } from "@/components/mtf/MtfBacktest";

type View = "test" | "sessions" | "compare" | "chart" | "history" | "orderflow" | "research" | "mtf";
// Drei Aufgaben: Lauf starten · Ergebnis ansehen · Trade nachspielen. Die bisherigen Ansichten (und ihre Direktlinks) bleiben –
// sie sind diesen Aufgaben nur zugeordnet.
type Task = "start" | "result" | "replay";
const TASKS: { key: Task; label: string; hint: string }[] = [
  { key: "start", label: "1 · Lauf starten", hint: "Strategie → Datensatz/Zeitraum → Session → Datenprüfung → Start" },
  { key: "result", label: "2 · Ergebnis ansehen", hint: "Datenqualität, Kennzahlen, Robustheit, Journal" },
  { key: "replay", label: "3 · Trade nachspielen", hint: "Gespeicherte Trades im Chart, Ereignis für Ereignis" },
];
const SUBVIEWS: Record<Task, { key: View; label: string }[]> = {
  start: [{ key: "test", label: "Strategie-Bericht (Kerzen / Tick)" }, { key: "sessions", label: "London + New York" }, { key: "mtf", label: "Multi-Timeframe 5m · 1m · 4RxV" }],
  result: [{ key: "history", label: "Alle Läufe" }, { key: "compare", label: "Strategien vergleichen" }],
  replay: [{ key: "chart", label: "Chart & Replay" }, { key: "mtf", label: "Multi-Timeframe-Trade" }],
};
const PRIMARY: { key: View; label: string }[] = [];
const SECONDARY: { key: View; label: string }[] = [
  { key: "research", label: "Expertenansicht (Kampagnen & Holdout)" },
];
const CHART_KEYS = ["mode", "day", "overlay", "trade"];
const LAST_REPORT_KEY = "backtesting:lastRunId";
const isTerminal = (s: string) => s === "Completed" || s === "Failed" || s === "Cancelled";

export function Backtesting({ defaultView = "test" }: { defaultView?: View }) {
  const [params, setParams] = useSearchParams();
  const location = useLocation();
  const legacyPath = location.pathname.startsWith("/research");   // alte /research-Links: bisherige Seite vollständig
  const raw = (params.get("view") as View | null) ?? defaultView;
  // Alter Direktlink ?view=orderflow: gleiche Arbeitsfläche, jetzt unter „Chart & Replay“.
  const view: View = raw === "orderflow" ? "chart" : raw;
  const setView = (v: View) => {
    const p = new URLSearchParams(params); p.set("view", v);
    if (v !== "test" && v !== "chart") p.delete("run");
    if (v !== "chart") CHART_KEYS.forEach((k) => p.delete(k));
    if (v !== "chart" && v !== "sessions") p.delete("srun");
    if (v !== "mtf") { p.delete("mrun"); p.delete("mtrade"); }
    if (v === "chart" && p.get("run")) p.set("mode", "run");
    setParams(p);
  };
  const opened = (view === "test" && params.get("run")) || (view === "sessions" && params.get("srun")) || (view === "mtf" && params.get("mrun") && params.get("mtrade") == null);
  const task: Task = view === "chart" || (view === "mtf" && params.get("mtrade") != null) ? "replay"
    : view === "history" || view === "compare" || opened ? "result" : "start";
  const goTask = (t: Task) => {
    if (t === "start") { const p = new URLSearchParams(); p.set("view", view === "sessions" || view === "mtf" ? view : "test"); setParams(p); return; }
    if (t === "result") { if (!opened) setView("history"); return; }
    setView("chart");
  };
  const setMtf = (patch: Record<string, string | null>) => {
    const p = new URLSearchParams(params); p.set("view", "mtf");
    for (const [k, v] of Object.entries(patch)) { if (v == null) p.delete(k); else p.set(k, v); }
    setParams(p);
  };
  return (
    <div className="flex flex-col h-full min-h-0">
      {/* Eine Überschrift steht bereits in der Kopfzeile der App — hier nur die Ansichten. */}
      <div className="flex-shrink-0 border-b border-[var(--line)] bg-[var(--bg-2)]">
        <div className="flex flex-wrap items-center gap-x-3 gap-y-1.5 px-4 py-2">
          <div className="flex flex-wrap gap-1" role="tablist" aria-label="Backtesting-Aufgaben" data-testid="bt-tasks" data-task={task}>
            {TASKS.map((t) => (
              <button key={t.key} role="tab" aria-selected={task === t.key} onClick={() => goTask(t.key)} title={t.hint} data-testid={`bt-task-${t.key}`}
                className={cn("h-[30px] rounded-md px-3.5 text-[13px] transition-colors", task === t.key ? "bg-grad-brand text-[var(--bg)] font-semibold" : "border border-[var(--line-2)] text-[var(--fg-dim)] hover:text-[var(--fg)]")}>
                {t.label}
              </button>
            ))}
          </div>
          <div className="flex flex-wrap gap-1" role="tablist" aria-label="Backtesting-Ansichten">
            {SUBVIEWS[task].map((v) => (
              <button key={v.key + task} role="tab" aria-selected={view === v.key} onClick={() => setView(v.key)} data-testid={`bt-view-${v.key}`}
                className={cn("h-[26px] rounded-md px-2.5 text-[12px]", view === v.key ? "border border-[var(--cyan)] text-[var(--fg)]" : "text-[var(--fg-dim)] hover:text-[var(--fg)]")}>
                {v.label}
              </button>
            ))}
            {PRIMARY.length > 0 && null}
          </div>
          <span className="hidden text-[11px] text-[var(--fg-faint)] md:inline">Simulation, keine Orders</span>
          <nav aria-label="Weitere Ansichten" className="ml-auto flex flex-wrap items-center gap-x-3 gap-y-1 text-[11px]">
            <span className="text-[var(--fg-faint)]">Weitere Ansichten:</span>
            {SECONDARY.map((v) => (
              <button key={v.key} onClick={() => setView(v.key)} aria-current={view === v.key ? "page" : undefined}
                className={cn("underline-offset-2 hover:underline", view === v.key ? "text-[var(--fg)] font-medium underline" : "text-[var(--key)]")}>
                {v.label} →
              </button>
            ))}
          </nav>
        </div>
      </div>
      <div className="flex-1 min-h-0 overflow-y-auto">
        {view === "test" && <TestView />}
        {view === "sessions" && (
          <SessionsBacktest runId={params.get("srun")} onRun={(id) => { const p = new URLSearchParams(params); p.set("view", "sessions"); if (id) p.set("srun", id); else p.delete("srun"); setParams(p); }} />
        )}
        {view === "compare" && <CompareView />}
        {view === "history" && <HistoryView />}
        {view === "chart" && <ChartReplayView defaultMode={raw === "orderflow" ? "files" : "run"} />}
        {view === "research" && <Research hideWorkspace={!legacyPath} />}
        {view === "mtf" && (
          <MtfBacktest runId={params.get("mrun")} trade={params.get("mtrade") != null ? Number(params.get("mtrade")) : null}
            onOpen={(id) => setMtf({ mrun: id, mtrade: null })} onTrade={(t) => setMtf({ mtrade: t == null ? null : String(t) })} />
        )}
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
  const [showParams, setShowParams] = useState(false);
  const [preview, setPreview] = useState<BacktestingPreview | null>(null);
  const [previewing, setPreviewing] = useState(false);
  const [run, setRun] = useState<ReportRun | null>(null);
  const [starting, setStarting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const startLock = useRef(false);
  const pollRef = useRef<number | null>(null);
  const reports = useReports();
  const set = <K extends keyof Form>(k: K, v: Form[K]) => setForm((f) => ({ ...f, [k]: v }));
  const runId = params.get("run");

  useEffect(() => {
    const ac = new AbortController();
    Promise.all([backtestingApi.strategies(ac.signal), backtestApi.dataSources(), backtestApi.instruments()])
      .then(([st, src, inst]) => {
        setStrategies(st); setSources(src); setInstruments(inst);
        const first = src.find((s) => s.available) ?? src[0];
        if (first) setForm((f) => {
          const id = f.dataSourceId || first.id;
          const known = src.find((s) => s.id === id)?.instrument;
          return { ...f, dataSourceId: id, symbol: known ?? inst.find((i) => i.symbol === f.symbol)?.symbol ?? inst[0]?.symbol ?? f.symbol };
        });
      })
      .catch((e) => !ac.signal.aborted && setError(String(e)));
    return () => ac.abort();
  }, []);

  // Wiederfinden nach Neuladen: ein noch LAUFENDER letzter Auftrag wird wieder angehängt. Ein abgeschlossener Bericht wird
  // nicht automatisch unter der aktuellen Auswahl geöffnet — er steht in „Letzte Läufe“ und öffnet mit einem Klick.
  useEffect(() => {
    if (runId) return;
    setRun(null);
    const last = localStorage.getItem(LAST_REPORT_KEY);
    if (!last) return;
    let stop = false;
    backtestingApi.report(last).then((res) => {
      if (stop) return;
      if (!res.ok || !res.run) { localStorage.removeItem(LAST_REPORT_KEY); return; }
      if (!isTerminal(res.run.status)) { const p = new URLSearchParams(params); p.set("run", last); setParams(p, { replace: true }); }
    }).catch(() => {});
    return () => { stop = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [runId]);

  // Geöffneten bzw. laufenden Auftrag aus der URL laden und bis zum Ende nachladen.
  useEffect(() => {
    if (!runId) return;
    let stop = false;
    const tick = async () => {
      try {
        const res = await backtestingApi.report(runId);
        if (stop) return;
        if (!res.ok || !res.run) { setError(res.error ?? "Bericht nicht gefunden."); localStorage.removeItem(LAST_REPORT_KEY); return; }
        setRun(res.run);
        if (!isTerminal(res.run.status)) pollRef.current = window.setTimeout(tick, 900);
        else reports.load();
      } catch { if (!stop) pollRef.current = window.setTimeout(tick, 2000); }
    };
    tick();
    return () => { stop = true; if (pollRef.current) clearTimeout(pollRef.current); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [runId]);

  const strategy = strategies.find((s) => s.id === form.strategy);
  const request = useMemo(() => toRequest(form), [form]);
  const requestKey = useMemo(() => JSON.stringify(request), [request]);
  const [previewKey, setPreviewKey] = useState<string | null>(null);

  // Vorschau (Datenumfang, Kostenprofil, mögliche Prüfungen) — verzögert nach Änderungen. Eine verspätete Antwort zu einer
  // älteren Auswahl wird verworfen; Entscheidung und Start nutzen nur eine Vorschau, die GENAU zur aktuellen Auswahl gehört.
  useEffect(() => {
    if (!form.dataSourceId) return;
    const ac = new AbortController();
    const key = requestKey;
    const h = window.setTimeout(() => {
      setPreviewing(true);
      backtestingApi.preview(request, ac.signal)
        .then((p) => { if (!ac.signal.aborted) { setPreview(p); setPreviewKey(key); } })
        .catch((e) => { if (!ac.signal.aborted) { setPreview({ ok: false, error: String(e) } as BacktestingPreview); setPreviewKey(key); } })
        .finally(() => !ac.signal.aborted && setPreviewing(false));
    }, 500);
    return () => { clearTimeout(h); ac.abort(); };
  }, [request, requestKey, form.dataSourceId]);
  const freshPreview = previewKey === requestKey ? preview : null;

  const chooseStrategy = (id: string) => {
    const s = strategies.find((x) => x.id === id);
    setForm((f) => ({ ...f, strategy: id, params: Object.fromEntries((s?.params ?? []).map((p) => [p.key, p.default])) }));
  };

  const decision = startDecision(freshPreview);
  const anyRunning = (run != null && !isTerminal(run.status)) || (reports.list ?? []).some((r) => !isTerminal(r.status));
  const start = useCallback(async () => {
    if (startLock.current) return;            // Doppelklick: kein zweiter Auftrag (zusätzlich serverseitig abgesichert)
    startLock.current = true; setStarting(true); setError(null); setNotice(null);
    try {
      const res = await backtestingApi.start(request);
      if (!res.ok || !res.run) { setError(res.error ?? "Start fehlgeschlagen."); return; }
      if (res.alreadyRunning) setNotice("Dieser Auftrag läuft bereits — es wurde kein zweiter gestartet.");
      localStorage.setItem(LAST_REPORT_KEY, res.run.runId);
      setRun(res.run);
      reports.load();
      const p = new URLSearchParams(params); p.set("view", "test"); p.set("run", res.run.runId); setParams(p);
    } catch (e) { setError(e instanceof Error ? e.message : String(e)); }
    finally { setStarting(false); window.setTimeout(() => { startLock.current = false; }, 400); }
  }, [request, params, setParams, reports]);

  const openReport = (r: ReportSummary) => {
    localStorage.setItem(LAST_REPORT_KEY, r.runId);
    if (r.profile !== "full") { navigate(`/backtesting?view=research&run=${encodeURIComponent(r.runId)}`); return; }
    const p = new URLSearchParams(params); p.set("view", "test"); p.set("run", r.runId); setParams(p);
    window.setTimeout(() => document.getElementById("opened-report")?.scrollIntoView({ behavior: "smooth", block: "start" }), 50);
  };
  const closeReport = () => { const p = new URLSearchParams(params); p.delete("run"); setParams(p); setRun(null); };
  const cancelRun = async (id: string) => { await backtestingApi.cancel(id).catch(() => {}); reports.load(); };

  const sourceOptions = sources.map((s) => ({ value: s.id, label: s.label + (s.available ? "" : " (nicht vorhanden)") }));
  const source = sources.find((s) => s.id === form.dataSourceId);
  const knownInstrument = source?.instrument ?? null;
  // Quellenwechsel: Datensatz und (bekanntes) Instrument in EINEM Schritt setzen — Vorschau und Start sehen nie eine Mischung.
  const chooseSource = (v: string) => {
    const src = sources.find((s) => s.id === v);
    setForm((f) => ({
      ...f, dataSourceId: v, ...(src?.instrument ? { symbol: src.instrument } : {}),
      ...(v.startsWith("ticks-csv:") || v.startsWith("atas-collection:") || v.startsWith("synthetic:") ? { fromUtc: "", toUtc: "" } : {}),
    }));
  };
  const dataInstr = freshPreview?.dataInstrument;
  const adoptInstrument = dataInstr?.symbol && !dataInstr.ambiguous && dataInstr.symbol !== form.symbol && !knownInstrument ? dataInstr.symbol : null;
  const isTickStrategy = strategy?.engine === "orderflow-ticks";

  return (
    <div className="p-4 space-y-4">
      {error && <div className="rounded-lg border border-[var(--red)] bg-[var(--panel)] p-3 text-xs text-[var(--red)]">{error}</div>}
      {notice && <div className="rounded-lg border border-[var(--gold)] bg-[var(--panel)] p-3 text-xs text-[var(--gold)]">{notice}</div>}

      {run && (
        <div className="report-fade flex flex-wrap items-center gap-2 rounded-lg border border-[var(--cyan)] bg-[var(--panel)] px-3 py-2 text-[12px]" data-testid="opened-run-banner">
          <b className="text-[var(--fg)]">Gespeicherter Bericht · Lauf {run.runId}</b>
          <span className="text-[var(--fg-dim)]">zeigt die Einstellungen und Daten dieses Laufs – nicht deine aktuelle Auswahl.</span>
          <button onClick={closeReport} className="ml-auto h-[28px] rounded-md border border-[var(--line-2)] px-2.5 text-xs text-[var(--fg)]" data-testid="new-run">Neuen Lauf konfigurieren</button>
        </div>
      )}
      {!run && (
        <div className="flex flex-wrap items-center gap-2 rounded-lg border border-[var(--line-2)] bg-[var(--panel)] px-3 py-2" data-testid="start-bar">
          <button onClick={start} disabled={starting || anyRunning || (decision.kind !== "ready" && decision.kind !== "limited")} data-testid="start-full"
            className="h-9 rounded-md bg-grad-brand px-5 text-sm font-semibold text-[var(--bg)] disabled:cursor-not-allowed disabled:opacity-50">
            {starting ? "Wird gestartet …" : "Vollständigen Backtest starten"}
          </button>
          <span className={cn("text-[11px]", decision.kind === "blocked" ? "text-[var(--red)]" : decision.kind === "limited" ? "text-[var(--gold)]" : "text-[var(--fg-dim)]")}>
            {anyRunning ? "Ein Auftrag läuft gerade — Fortschritt und Abbruch unter „Letzte Läufe“."
              : decision.kind === "blocked" ? "Start gesperrt — rechts steht, welche Pflichtdaten fehlen."
              : decision.kind === "limited" ? "Startbar mit Einschränkungen — rechts stehen die nicht anwendbaren Prüfungen."
              : "Ein Klick startet alle anwendbaren Analysen und speichert den Bericht. Der finale Holdout bleibt reserviert."}
          </span>
        </div>
      )}
      {!run && <div className="grid items-start gap-4 lg:grid-cols-[minmax(0,1.05fr)_minmax(0,1fr)]">
        {/* ---- Neue Konfiguration ---- */}
        <section aria-label="Neue Konfiguration" className="self-start rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3 space-y-2.5">
          <h2 className="text-[13px] font-medium text-[var(--fg)]">Neuer Backtest</h2>
          <Field label="Strategie"><Select value={form.strategy} onChange={chooseStrategy} options={strategies.map((s) => ({ value: s.id, label: s.name }))} /></Field>
          <Field label="Datensatz"><Select value={form.dataSourceId} onChange={chooseSource} options={sourceOptions} /></Field>
          <div className="grid grid-cols-2 gap-2">
            <Field label="Von (UTC)"><Input type="datetime-local" value={form.fromUtc} onChange={(v) => set("fromUtc", v)} /></Field>
            <Field label="Bis (UTC, exklusiv)"><Input type="datetime-local" value={form.toUtc} onChange={(v) => set("toUtc", v)} /></Field>
          </div>
          {!form.fromUtc && !form.toUtc && <p className="-mt-1 text-[10px] text-[var(--fg-faint)]">Kein Zeitraum gesetzt — der ganze Datensatz wird verwendet.</p>}
          {!knownInstrument && source && (
            <p className="-mt-1 text-[10px] text-[var(--fg-faint)]">
              Dieser Datensatz legt kein Instrument fest — das gewählte Instrument wird verwendet und vor dem Start gegen die Daten geprüft, soweit sie ein Symbol enthalten.
            </p>
          )}
          {adoptInstrument && (
            <p className="-mt-1 text-[11px] text-[var(--gold)]">
              Die Daten enthalten {adoptInstrument}, gewählt ist {form.symbol}.{" "}
              <button onClick={() => set("symbol", adoptInstrument)} className="underline text-[var(--key)]">{adoptInstrument} übernehmen</button>
            </p>
          )}
          <div className="flex flex-wrap items-end gap-2">
            {knownInstrument ? (
              <div className="w-[150px]">
                <span className="mb-0.5 block text-[10px] text-[var(--fg-faint)]">Instrument</span>
                <div data-testid="instrument-from-data" title={`Festgelegt durch den Datensatz: ${source?.instrumentBasis ?? ""}`}
                  className="flex h-8 items-center gap-1.5 rounded-md border border-[var(--line)] bg-[var(--bg-2)] px-2 text-xs">
                  <b className="text-[var(--fg)]">{knownInstrument}</b><span className="text-[var(--fg-faint)]">· aus Datensatz</span>
                </div>
              </div>
            ) : (
              <div className="w-[96px]"><Field label="Instrument (wählen)"><Select value={form.symbol} onChange={(v) => set("symbol", v)} options={(instruments.length ? instruments.map((i) => i.symbol) : [form.symbol]).map((s) => ({ value: s, label: s }))} /></Field></div>
            )}
            <div className="w-[96px]"><Field label={isTickStrategy ? "Kerzen (Min., ungenutzt)" : "Kerzen (Minuten)"}><Input type="number" value={String(form.timeframeMinutes)} onChange={(v) => set("timeframeMinutes", parseInt(v, 10) || 5)} /></Field></div>
            {strategy && (
              <div className="min-w-0 flex-1">
                <span className="mb-0.5 block text-[10px] text-[var(--fg-faint)]">Parameter</span>
                <div className="flex h-8 items-center gap-2 rounded-md border border-[var(--line)] px-2 text-[12px]">
                  <span className="min-w-0 flex-1 truncate text-[var(--fg)]" title={paramSummary(strategy.params, form.params)}>{paramSummary(strategy.params, form.params)}</span>
                  {strategy.params.length > 0 && (
                    <button onClick={() => setShowParams((s) => !s)} aria-expanded={showParams} className="whitespace-nowrap text-[11px] text-[var(--key)]">{showParams ? "Fertig" : "Parameter ändern"}</button>
                  )}
                </div>
              </div>
            )}
          </div>
          {strategy && showParams && (
            <div className="rounded-md border border-[var(--line)] p-2">
              <p className="text-[11px] text-[var(--fg-dim)]">{strategy.description}</p>
              <div className="mt-2 grid gap-2 sm:grid-cols-3">
                {strategy.params.map((p) => (
                  <Field key={p.key} label={p.label}><Input value={form.params[p.key] ?? p.default} onChange={(v) => set("params", { ...form.params, [p.key]: v })} /></Field>
                ))}
              </div>
            </div>
          )}
          <button onClick={() => setAdvanced((a) => !a)} aria-expanded={advanced} className="text-[11px] text-[var(--key)]">{advanced ? "▾ Erweitert ausblenden" : "▸ Erweitert (Annahmen ändern — für einen normalen Lauf nicht nötig)"}</button>
          {advanced && (
            <div className="grid gap-2 sm:grid-cols-3 border-t border-[var(--line)] pt-2">
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
        </section>

        {/* ---- Entscheidung ---- */}
        <StartDecisionCard preview={freshPreview} previewing={previewing || (preview != null && freshPreview == null)} decision={decision} strategy={strategy} form={form} />

        {/* ---- Gespeicherte Läufe (getrennt von der neuen Konfiguration) ---- */}
        <RecentRuns list={reports.list} err={reports.err} openId={runId} onOpen={openReport} onCancel={cancelRun}
          onAll={() => navigate("/backtesting?view=history")} onRefresh={reports.load} />
      </div>}

      {run && (
        <section id="opened-report" aria-label="Geöffneter Bericht" className="space-y-3 scroll-mt-4">
          <RunHeader run={run} request={request} summary={reports.list?.find((r) => r.runId === run.runId)} onClose={closeReport}
            onOpenHistory={() => navigate("/backtesting?view=history")} onOpenChart={() => navigate(`/backtesting?view=chart&mode=run&run=${encodeURIComponent(run.runId)}`)} />
          <ReportView run={run} onOpenHoldout={() => navigate(`/backtesting?view=research&run=${encodeURIComponent(run.runId)}`)}
            onOpenChart={(trade) => navigate(`/backtesting?view=chart&mode=run&run=${encodeURIComponent(run.runId)}${trade != null ? `&trade=${trade}` : ""}`)} />
        </section>
      )}
    </div>
  );
}

const DECISION_CLS: Record<string, string> = {
  ready: "border-[var(--key)] text-[var(--key)]",
  limited: "border-[var(--gold)] text-[var(--gold)]",
  blocked: "border-[var(--red)] text-[var(--red)]",
  loading: "border-[var(--line-2)] text-[var(--fg-dim)]",
};
const DECISION_ICON: Record<string, string> = { ready: "✓", limited: "!", blocked: "✕", loading: "…" };
const CHECK_CLS: Record<string, string> = {
  possible: "bg-[rgba(60,240,160,0.15)] text-[var(--key)]",
  partial: "bg-[rgba(240,195,90,0.15)] text-[var(--gold)]",
  insufficient: "bg-[rgba(240,195,90,0.15)] text-[var(--gold)]",
  reserved: "bg-[rgba(155,140,255,0.15)] text-[var(--purple)]",
  notApplicable: "bg-[var(--panel-3)] text-[var(--fg-dim)]",
};

function StartDecisionCard({ preview, previewing, decision, strategy, form }: {
  preview: BacktestingPreview | null; previewing: boolean; decision: ReturnType<typeof startDecision>; strategy?: StrategyDef; form: Form;
}) {
  const [showChecks, setShowChecks] = useState(false);
  const groups = preview?.ok ? groupChecks(preview.checks ?? []) : [];
  const counts = preview?.ok ? checkCounts(preview.checks ?? []) : null;
  const facts = preview?.ok && preview.canStart ? previewFacts(preview, form.params.TicksPerBar, form.applyFees) : [];
  return (
    <section aria-label="Startentscheidung" className="self-start rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3 space-y-2.5 lg:row-span-2">
      <div className="flex items-center gap-2">
        <h2 className="text-[13px] font-medium text-[var(--fg)]">Vor dem Start</h2>
        {previewing && <span className="text-[10px] text-[var(--cyan)] animate-pulse">wird aktualisiert …</span>}
      </div>
      <div role="status" aria-live="polite" className={cn("flex items-center gap-2.5 rounded-md border px-3 py-2", DECISION_CLS[decision.kind])}>
        <span aria-hidden className="flex h-6 w-6 items-center justify-center rounded-full border border-current text-[13px] font-bold">{DECISION_ICON[decision.kind]}</span>
        <span className="text-[15px] font-semibold">{decision.title}</span>
      </div>
      {decision.missing.length > 0 && (
        <div className="space-y-1">
          <div className="text-[11px] font-medium text-[var(--fg)]">Es fehlt:</div>
          <ul className="space-y-1">
            {decision.missing.map((m) => (
              <li key={m.label} className="rounded-md border border-[var(--red)] px-2 py-1 text-[11px]">
                <b className="text-[var(--red)]">{m.label}</b> <span className="text-[var(--fg-dim)]">— {m.detail}</span>
              </li>
            ))}
          </ul>
          <p className="text-[10px] text-[var(--fg-faint)]">Es wird nichts geschätzt oder ersetzt. Wähle einen passenden Datensatz.</p>
        </div>
      )}
      {facts.length > 0 && (
        <ul className="space-y-1 text-[12px] text-[var(--fg)]">
          {facts.map((f) => <li key={f} className="flex gap-2"><span aria-hidden className="text-[var(--fg-faint)]">•</span><span>{f}</span></li>)}
        </ul>
      )}
      {preview?.ok && preview.canStart && (
        <p className="text-[10px] text-[var(--fg-faint)]">
          Daten {day(preview.from)} – {day(preview.to)} UTC · {num(preview.bars, 0)} {preview.engine === "orderflow-ticks" ? "Tick-Bars" : "Kerzen"}
          {preview.holdoutSessions > 0 && <> · Holdout ab {day(preview.holdoutFrom)} wird im normalen Lauf nicht ausgewertet</>}
          {preview.feeIsExample && <> · Beispielprofil, keine echten Broker-Kosten</>}
        </p>
      )}
      {decision.limited.length > 0 && (
        <div className="space-y-1">
          <div className="text-[11px] font-medium text-[var(--fg)]">Nur eingeschränkt auswertbar (zu wenig Historie):</div>
          <ul className="space-y-0.5">
            {decision.limited.map((l) => (
              <li key={l.label} className="text-[11px]"><span className="text-[var(--gold)]">{l.label}</span> <span className="text-[var(--fg-faint)]">— {l.detail}</span></li>
            ))}
          </ul>
        </div>
      )}
      {preview?.ok && (
        <div className="border-t border-[var(--line)] pt-2">
          <button onClick={() => setShowChecks((s) => !s)} aria-expanded={showChecks} className="text-[11px] text-[var(--key)]">
            {preview.canStart ? (showChecks ? "▾ Geplante Prüfungen ausblenden" : "▸ Geplante Prüfungen anzeigen")
              : (showChecks ? "▾ Datenanforderungen ausblenden" : "▸ Datenanforderungen anzeigen")}
          </button>
          {counts && counts.total > 0 && (
            <span className="ml-2 text-[10px] text-[var(--fg-faint)]">
              {counts.planned} von {counts.total} geplant{counts.limited > 0 ? ` · ${counts.limited} eingeschränkt` : ""}{counts.notApplicable > 0 ? ` · ${counts.notApplicable} nicht anwendbar` : ""}
              {counts.holdoutReserved ? " · finaler Holdout reserviert (keine Prüfung dieses Laufs)" : ""}
            </span>
          )}
          {showChecks && (
            <div className="mt-2 space-y-2">
              {strategy?.dataRequirements && strategy.dataRequirements.length > 0 && (
                <div className="rounded-md border border-[var(--line)] p-2 text-[11px]">
                  <div className="mb-0.5 font-medium text-[var(--fg)]">Datenanforderungen „{strategy.name}“
                    <span className="ml-2 rounded bg-[var(--panel-3)] px-1 text-[9px] text-[var(--fg-dim)]">{strategy.engine === "orderflow-ticks" ? "Tick-Orderflow-Pfad" : "OHLC-Kerzen-Engine"}</span>
                  </div>
                  <ul className="space-y-0.5" aria-label="Prüfung der Datenanforderungen">
                    {(preview.requirements ?? []).map((r) => (
                      <li key={r.requirement} className="flex items-start gap-2">
                        <span className={cn("mt-0.5 rounded px-1 text-[9px] whitespace-nowrap", r.met ? CHECK_CLS.possible : "bg-[rgba(240,86,106,0.18)] text-[var(--red)]")}>{r.met ? "erfüllt" : "fehlt"}</span>
                        <span className="text-[var(--fg)]">{r.requirement}</span><span className="text-[var(--fg-faint)]">— {r.detail}</span>
                      </li>
                    ))}
                  </ul>
                </div>
              )}
              {preview.canStart && preview.signalData && (
                <div className="text-[11px] text-[var(--fg-dim)]"><b className="text-[var(--fg)]">Signal:</b> {preview.signalData} · <b className="text-[var(--fg)]">Ausführung:</b> {preview.executionData}</div>
              )}
              {preview.canStart && (
                <div className="text-[11px] text-[var(--fg-dim)]">
                  <b className="text-[var(--fg)]">Umfang:</b> {preview.engine === "orderflow-ticks" ? `${preview.candidates} Delta-Schwellen im Walk-forward-Raster` : `${preview.candidates} Walk-forward-Kandidaten · ca. ${num(preview.estimatedEngineRuns, 0)} Engine-Läufe auf denselben, einmal geladenen Kerzen`}
                  {preview.costs && <> · {money(preview.costs.feeRoundTrip)} Round-Turn · Tick {num(preview.costs.tickSize, 4)} = {money(preview.costs.tickValue)}</>}
                </div>
              )}
              <ul className="space-y-0.5" aria-label="Geplante Prüfungen">
                {groups.map((g) => <CheckLine key={g.key} g={g} />)}
              </ul>
            </div>
          )}
        </div>
      )}
    </section>
  );
}

function CheckLine({ g }: { g: CheckGroup }) {
  return (
    <li className="text-[11px]">
      <div className="flex items-start gap-2">
        <span className={cn("mt-0.5 rounded px-1 text-[9px] whitespace-nowrap", CHECK_CLS[g.status] ?? CHECK_CLS.notApplicable)}>{plannedText(g.status)}</span>
        <span className="text-[var(--fg)]">{isHoldout(g) ? "Finaler Holdout – unbenutzt (keine Prüfung dieses Laufs)" : g.label}</span>
        {g.reason && <span className="text-[var(--fg-faint)]">— {g.reason}</span>}
      </div>
      {g.items && (
        <ul className="ml-[68px] mt-0.5 space-y-0.5">
          {g.items.map((i) => (
            <li key={i.key} className="flex items-start gap-2 text-[10px]">
              <span className={cn("rounded px-1 text-[9px] whitespace-nowrap", CHECK_CLS[i.status])}>{plannedText(i.status)}</span>
              <span className="text-[var(--fg-dim)]">{i.label.replace(/^06 Walk-forward-Testdaten:\s*/, "")}</span>
              {i.reason && <span className="text-[var(--fg-faint)]">— {i.reason}</span>}
            </li>
          ))}
        </ul>
      )}
    </li>
  );
}

function RecentRuns({ list, err, openId, onOpen, onCancel, onAll, onRefresh }: {
  list: ReportSummary[] | null; err: string | null; openId: string | null; onOpen: (r: ReportSummary) => void; onCancel: (id: string) => void;
  onAll: () => void; onRefresh: () => void;
}) {
  const [progress, setProgress] = useState<Record<string, number | null>>({});
  const running = (list ?? []).filter((r) => !isTerminal(r.status));
  const runningKey = running.map((r) => r.runId).join(",");
  // Liste nachladen: schnell, solange ein Auftrag läuft; sonst ruhig.
  useEffect(() => { const h = window.setInterval(onRefresh, running.length > 0 ? 2000 : 15000); return () => clearInterval(h); }, [onRefresh, running.length]);
  // Fortschritt NUR für tatsächlich laufende Aufträge.
  useEffect(() => {
    if (running.length === 0) return;
    let stop = false;
    const poll = () => Promise.all(running.filter((r) => r.jobId).map((r) => backtestingApi.job(r.jobId!).then((j) => [r.runId, j?.progress ?? null] as const).catch(() => [r.runId, null] as const)))
      .then((xs) => { if (!stop) setProgress(Object.fromEntries(xs)); });
    poll();
    const h = window.setInterval(poll, 1200);
    return () => { stop = true; clearInterval(h); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [runningKey]);
  const rows = (list ?? []).slice(0, 5);
  return (
    <section aria-label="Letzte Läufe" className="self-start rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3 space-y-2">
      <div className="flex flex-wrap items-baseline gap-2">
        <h2 className="text-[13px] font-medium text-[var(--fg)]">Letzte Läufe</h2>
        <span className="text-[10px] text-[var(--fg-faint)]">gespeicherte Berichte — unabhängig von der Auswahl oben</span>
        <button onClick={onAll} className="ml-auto text-[11px] text-[var(--key)]">Alle Läufe →</button>
      </div>
      {err && <p className="text-[11px] text-[var(--red)]">{err}</p>}
      {!list ? <p className="text-[11px] text-[var(--fg-faint)]">Lade …</p> : rows.length === 0 ? (
        <p className="text-[11px] text-[var(--fg-faint)]">Noch keine gespeicherten Läufe.</p>
      ) : (
        <ul className="divide-y divide-[var(--line)]">
          {rows.map((r) => (
            <RunRow key={r.runId} r={r} open={openId === r.runId} progress={progress[r.runId]} onOpen={onOpen} onCancel={onCancel} />
          ))}
        </ul>
      )}
    </section>
  );
}

function RunRow({ r, open, progress, onOpen, onCancel }: {
  r: ReportSummary; open: boolean; progress: number | null | undefined; onOpen: (r: ReportSummary) => void; onCancel: (id: string) => void;
}) {
  const [details, setDetails] = useState(false);
  const live = !isTerminal(r.status);
  return (
    <li className={cn("py-1.5", open && "-mx-1 rounded bg-[var(--bg-2)] px-1")}>
      <div className="flex items-center gap-2 text-[12px]">
        <span className="min-w-0 flex-1 truncate" title={`${r.strategyName} · ${r.symbol}`}>
          <span className="font-medium text-[var(--fg)]">{r.strategyName}</span>
          {r.profile !== "full" && <span className="ml-1 text-[10px] text-[var(--fg-faint)]">(klassisch)</span>}
        </span>
        <span className={cn(r.instrumentNote ? "text-[var(--red)] font-medium" : "text-[var(--fg-dim)]")} title={r.instrumentNote ?? undefined}>
          {r.symbol}{r.instrumentNote && " ⚠"}
        </span>
        <span className="whitespace-nowrap font-semibold text-[var(--fg)] mono text-[11px]" title="getesteter Zeitraum">{testedPeriod(r.fromUtc, r.toUtc, r.dataFrom, r.dataTo)}</span>
        <StatusChip status={r.status} />
        {open ? <span className="w-[92px] text-right text-[10px] text-[var(--fg-faint)]">geöffnet ↓</span>
          : <button onClick={() => onOpen(r)} className="w-[92px] whitespace-nowrap rounded border border-[var(--line-2)] py-0.5 text-[11px] text-[var(--cyan)] hover:border-[var(--cyan)]">Bericht öffnen</button>}
        <button onClick={() => setDetails((d) => !d)} aria-expanded={details} aria-label={`Details zu ${r.strategyName}`}
          title="Details" className="w-5 text-[11px] text-[var(--fg-faint)] hover:text-[var(--fg)]">{details ? "▾" : "▸"}</button>
      </div>
      {live && (
        <div className="mt-1 flex items-center gap-2">
          <div className="h-1.5 flex-1 rounded bg-[var(--panel-3)]" role="progressbar" aria-label="Fortschritt" aria-valuemin={0} aria-valuemax={100} aria-valuenow={progress != null ? Math.round(progress * 100) : undefined}>
            <div className="h-1.5 rounded bg-grad-brand transition-all" style={{ width: `${Math.round((progress ?? 0) * 100)}%` }} />
          </div>
          <span className="mono text-[10px] text-[var(--cyan)]">{progress != null ? `${Math.round(progress * 100)} %` : `${r.stepsDone}/${r.stepsTotal}`}</span>
          <button onClick={() => onCancel(r.runId)} className="rounded bg-[var(--red)] px-2 py-0.5 text-[11px] font-medium text-black">Abbrechen</button>
        </div>
      )}
      {details && (
        <div className="mt-1 space-y-0.5 pl-1 text-[10px] text-[var(--fg-faint)]">
          <div>Auftrag <span className="mono">{r.runId}</span> · gestartet {dt(r.createdUtc)} UTC{r.completedUtc ? ` · beendet ${dt(r.completedUtc)} UTC` : ""}</div>
          <div>Parameter: {Object.entries(r.parameters).map(([k, v]) => `${k}=${v}`).join(", ") || "—"} · Abschnitte {r.stepsDone}/{r.stepsTotal}</div>
          {(fig(r, "net") != null || fig(r, "oos") != null) && <div>Netto (Basis) {money(fig(r, "net"))} · Walk-forward-Test {money(fig(r, "oos"))}</div>}
          {r.statusReason && <div>{r.statusReason}</div>}
        </div>
      )}
      {r.instrumentNote && <div className="mt-0.5 text-[10px] text-[var(--red)]">{r.instrumentNote}</div>}
    </li>
  );
}

const sameInstant = (a?: string | null, b?: string | null) => (!a && !b) || (!!a && !!b && Date.parse(a) === Date.parse(b));
const sameParams = (a: Record<string, string> = {}, b: Record<string, string> = {}) =>
  Object.keys({ ...a, ...b }).every((k) => (a[k] ?? "") === (b[k] ?? ""));

function RunHeader({ run, request, summary, onClose, onOpenHistory, onOpenChart }: {
  run: ReportRun; request: BacktestingStartRequest; summary?: ReportSummary; onClose: () => void; onOpenHistory: () => void; onOpenChart: () => void;
}) {
  const cfg = run.config?.run;
  const prov = run.report?.provenance;
  const name = prov?.strategyName ?? summary?.strategyName ?? cfg?.strategy ?? "–";
  const symbol = prov?.symbol ?? cfg?.symbol ?? summary?.symbol ?? "–";
  const matches = !!cfg && cfg.strategy === request.strategy && cfg.symbol === request.symbol && cfg.dataSourceId === request.dataSourceId
    && cfg.timeframeMinutes === request.timeframeMinutes && sameInstant(cfg.fromUtc, request.fromUtc) && sameInstant(cfg.toUtc, request.toUtc)
    && sameParams(cfg.params, request.params);
  const live = !isTerminal(run.status);
  return (
    <div className="rounded-lg border border-[var(--line-2)] bg-[var(--panel)] p-3 space-y-1.5">
      <div className="flex flex-wrap items-center gap-2 text-[12px]">
        <span className="text-[10px] uppercase tracking-wide text-[var(--fg-faint)]">{live ? "Laufender Auftrag" : "Gespeicherter Bericht"}</span>
        <span className="font-medium text-[var(--fg)]">{name} · {symbol} · <b>{testedPeriod(cfg?.fromUtc, cfg?.toUtc, run.dataFrom ?? prov?.dataFrom, run.dataTo ?? prov?.dataTo)}</b></span>
        <StatusChip status={run.status} />
        <span className="text-[var(--fg-faint)]">gestartet {dt(run.createdUtc)} UTC{run.completedUtc ? ` · beendet ${dt(run.completedUtc)} UTC` : ""}</span>
        <span className="ml-auto flex items-center gap-3">
          {!live && (run.report?.trades?.trades?.length ?? 0) > 0 && (
            <button onClick={onOpenChart} data-testid="open-trades-chart" className="h-7 rounded-md bg-grad-brand px-3 text-[12px] font-semibold text-[var(--bg)]">
              Trades im Chart ({run.report!.trades!.trades.length})
            </button>
          )}
          <button onClick={onOpenHistory} className="text-[11px] text-[var(--key)]">Alle Läufe →</button>
          <button onClick={onClose} className="rounded border border-[var(--line-2)] px-2 py-0.5 text-[11px] text-[var(--fg-dim)] hover:text-[var(--fg)]">Bericht schließen</button>
        </span>
      </div>
      <p className={cn("text-[11px]", matches ? "text-[var(--fg-dim)]" : "text-[var(--gold)]")}>
        {matches ? "Entspricht der oben ausgewählten Konfiguration."
          : "Andere Konfiguration als oben ausgewählt — dieser Bericht ist NICHT das Ergebnis der aktuellen Auswahl."}
        {live && " Fortschritt und Abbruch unter „Letzte Läufe“."}
      </p>
      {run.statusReason && <p className="text-[11px] text-[var(--fg-dim)]">{run.statusReason}</p>}
      {summary?.instrumentNote ? <p className="text-[11px] text-[var(--red)]">{summary.instrumentNote}</p>
        : prov?.instrumentCheck ? <p className="text-[11px] text-[var(--fg-dim)]">Instrument: {prov.instrumentCheck}{prov.costModel ? ` · Punktwert ${money(prov.costModel.pointValue)} ${prov.costModel.currency} · Tickwert ${money(prov.costModel.tickValue)}` : ""}</p>
        : prov ? <p className="text-[11px] text-[var(--fg-faint)]">Instrument {prov.symbol}: Prüfung gegen den Datensatz ist in diesem älteren Bericht nicht hinterlegt — welches Profil zu den Daten passte, wird nicht nachträglich behauptet.</p> : null}
      <details className="text-[10px] text-[var(--fg-faint)]">
        <summary className="cursor-pointer select-none">Technische Details</summary>
        <div className="mt-1 space-y-1">
          <div>Auftrag <span className="mono">{run.runId}</span> · {run.steps.filter((s) => !["Pending", "Running"].includes(s.status)).length}/{run.steps.length} Abschnitte
            {cfg && <> · Parameter {Object.entries(cfg.params ?? {}).map(([k, v]) => `${k}=${v}`).join(", ") || "—"} · Datensatz {cfg.dataSourceId}</>}</div>
          <div className="flex flex-wrap gap-1">
            {run.steps.map((s) => (
              <a key={s.key} href={`#sec-${s.key}`} className="flex items-center gap-1 rounded border border-[var(--line)] px-1.5 py-0.5 text-[10px] text-[var(--fg-dim)]" title={s.reason ?? ""}>
                {s.key === "oos" ? "06 Walk-forward-Testdaten" : s.key === "holdout" ? "Holdout (unbenutzt)" : stepLabel(s).split(" ").slice(0, 2).join(" ")} <StatusChip status={s.status} stepKey={s.key} />
              </a>
            ))}
          </div>
        </div>
      </details>
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
