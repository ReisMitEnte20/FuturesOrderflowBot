import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import * as echarts from "echarts";
import { cn } from "@/lib/utils";
import {
  backtestApi,
  type Candle,
  type CostProfile,
  type DataSourceDef,
  type EquityPoint,
  type InstrumentDef,
  type OhlcTrade,
  type RunRequest,
} from "@/lib/backtestApi";
import {
  defaultEvaluationOptions,
  formatDate,
  formatNumber,
  formatPercent,
  quantApi,
  type ComparisonFamilyRow,
  type HoldoutEvaluationRecord,
  type QuantEvaluationOptions,
  type QuantMonteCarloResponse,
  type ResearchCampaignResponse,
  type ResearchCampaignStartRequest,
  type ResearchComparison,
  type ResearchFamilyInput,
  type ResearchGroupHoldout,
  type ResearchHoldoutProposal,
  type ResearchRunRecord,
  type ResearchStartRequest,
  type ResearchStepState,
  type ResearchStepStatus,
} from "@/lib/quantApi";
import { HeadlineStat, MetricList, NoteBlock, Unavailable } from "@/components/research/MetricList";
import { EquityDrawdownChart } from "@/components/backtest/EquityDrawdownChart";
import { BacktestChart } from "@/components/backtest/BacktestChart";
import {
  CostHeatmap,
  DistributionChart,
  EquityDrawdownPanel,
  MonthlyReturnsTable,
  PboScatter,
  RollingChart,
  WalkForwardTimeline,
} from "@/components/research/ResearchCharts";
import { chartTradesAt, relativeTradeIndex } from "@/lib/replay";

const LAST_RUN_KEY = "research:lastRunId";
const LAST_CAMPAIGN_KEY = "research:lastCampaignId";
const toInput = (iso: string) => iso.replace("Z", "").slice(0, 16);

interface ResearchConfig {
  dataSourceId: string;
  symbol: string;
  timeframeMinutes: number;
  fromUtc: string;
  toUtc: string;
  maxRows: number;
  strategy: string;
  fastList: string;
  slowList: string;
  channelList: string;
  // Mehrstrategie-Vergleich: aktive Strategie-Familien
  familySma: boolean;
  familyDonchian: boolean;
  quantity: number;
  initialBalance: number;
  stopLossTicks: string;
  takeProfitTicks: string;
  slippageTicks: string;
  feePerSide: string;
  applyFees: boolean;
  // Kampagne / Auswahl
  campaignName: string;
  hypothesis: string;
  selectionMetric: string;
  holdoutFraction: number;
  trialBudget: number;
  // Walk-forward
  mode: string;
  trainBars: number;
  testBars: number;
  embargoBars: number;
  warmupBars: number;
  // Monte Carlo
  mcSource: string;
  mcMethod: string;
  mcIterations: number;
  seed: number;
  blockLength: string;
  capitalBarrier: string;
  // Robustheit / Overfitting
  robustnessMetric: string;
  overfittingBlocks: number;
  benchmarkId: string;
  fullyFunded: boolean;
}

const DEFAULT_CONFIG: ResearchConfig = {
  dataSourceId: "",
  symbol: "MES",
  timeframeMinutes: 5,
  fromUtc: "",
  toUtc: "",
  maxRows: 1_500_000,
  strategy: "movingaverage",
  fastList: "6, 9, 12",
  slowList: "21, 34, 55",
  channelList: "15, 20, 30",
  familySma: true,
  familyDonchian: true,
  quantity: 1,
  initialBalance: 10_000,
  stopLossTicks: "",
  takeProfitTicks: "",
  slippageTicks: "",
  feePerSide: "",
  applyFees: true,
  campaignName: "Research",
  hypothesis: "SMA-Crossover als Teststrategie (keine Edge-Behauptung).",
  selectionMetric: "sharpe",
  holdoutFraction: 0.2,
  trialBudget: 0,
  mode: "Rolling",
  trainBars: 2000,
  testBars: 500,
  embargoBars: 0,
  warmupBars: 60,
  mcSource: "trades",
  mcMethod: "Permutation",
  mcIterations: 1000,
  seed: 12345,
  blockLength: "",
  capitalBarrier: "",
  robustnessMetric: "sharpe",
  overfittingBlocks: 16,
  benchmarkId: "",
  fullyFunded: false,
};

function parseList(s: string): number[] {
  return s.split(",").map((x) => parseInt(x.trim(), 10)).filter((x) => Number.isFinite(x) && x > 0);
}
function buildCandidates(fast: string, slow: string): Record<string, string>[] {
  const out: Record<string, string>[] = [];
  for (const f of parseList(fast)) for (const s of parseList(slow)) if (f < s) out.push({ FastPeriod: String(f), SlowPeriod: String(s) });
  return out;
}
function buildDonchianCandidates(channels: string): Record<string, string>[] {
  return parseList(channels).map((c) => ({ Channel: String(c) }));
}
const numOrNull = (s: string): number | null => {
  const v = parseFloat(s.trim());
  return Number.isFinite(v) ? v : null;
};
const intOrNull = (s: string): number | null => {
  const v = parseInt(s.trim(), 10);
  return Number.isFinite(v) ? v : null;
};

const isTerminal = (s: ResearchStepStatus) => s === "Completed" || s === "Failed" || s === "Cancelled";

// ==============================================================================================

export function Research() {
  const [sources, setSources] = useState<DataSourceDef[]>([]);
  const [instruments, setInstruments] = useState<InstrumentDef[]>([]);
  const [benchmarks, setBenchmarks] = useState<string[]>([]);
  const [cfg, setCfg] = useState<ResearchConfig>(DEFAULT_CONFIG);
  const [evalOpts] = useState<QuantEvaluationOptions>({ ...defaultEvaluationOptions, frequency: "Daily" });
  const [showAdvanced, setShowAdvanced] = useState(false);

  const [run, setRun] = useState<ResearchRunRecord | null>(null);
  const [savedRuns, setSavedRuns] = useState<ResearchRunRecord[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const pollRef = useRef<number | null>(null);
  const set = <K extends keyof ResearchConfig>(k: K, v: ResearchConfig[K]) => setCfg((c) => ({ ...c, [k]: v }));

  // Startzustand laden.
  useEffect(() => {
    const ac = new AbortController();
    (async () => {
      try {
        const [src, inst, bms, runs] = await Promise.all([
          backtestApi.dataSources(),
          backtestApi.instruments(),
          quantApi.benchmarks(ac.signal).catch(() => []),
          quantApi.researchList(null, ac.signal).catch(() => []),
        ]);
        setSources(src); setInstruments(inst); setBenchmarks(bms); setSavedRuns(runs);
        const first = src.find((s) => s.available) ?? src[0];
        if (first) setCfg((c) => ({
          ...c,
          dataSourceId: first.id,
          fromUtc: first.defaultFromUtc ? toInput(first.defaultFromUtc) : c.fromUtc,
          maxRows: first.defaultMaxRows ?? c.maxRows,
          symbol: inst[0]?.symbol ?? c.symbol,
        }));
        const lastCamp = localStorage.getItem(LAST_CAMPAIGN_KEY);
        if (lastCamp) {
          const res = await quantApi.campaign(lastCamp, ac.signal).catch(() => null);
          if (res?.ok && res.groupId) {
            setCampaign(res);
            if (res.families.some((f) => !isTerminal(f.status))) { setBusy(true); startCampPolling(res.groupId); }
          }
        }
        const last = localStorage.getItem(LAST_RUN_KEY);
        if (last && !localStorage.getItem(LAST_CAMPAIGN_KEY)) {
          const res = await quantApi.research(last, ac.signal).catch(() => null);
          if (res?.ok && res.run) { setRun(res.run); if (res.run.status === "Running") startPolling(res.run.runId); }
        }
      } catch (e) {
        if (!ac.signal.aborted) setError(e instanceof Error ? e.message : String(e));
      }
    })();
    return () => { ac.abort(); stopPolling(); stopCampPolling(); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const candidates = useMemo(() => buildCandidates(cfg.fastList, cfg.slowList), [cfg.fastList, cfg.slowList]);

  const runRequest = useCallback((): RunRequest => ({
    dataSourceId: cfg.dataSourceId, path: null, symbol: cfg.symbol, timeframeMinutes: cfg.timeframeMinutes,
    fromUtc: cfg.fromUtc.trim() ? `${cfg.fromUtc}:00Z` : null,
    toUtc: cfg.toUtc.trim() ? `${cfg.toUtc}:00Z` : null,
    maxRows: cfg.maxRows, strategy: cfg.strategy, params: candidates[0] ?? { FastPeriod: "9", SlowPeriod: "21" },
    quantity: cfg.quantity, initialBalance: cfg.initialBalance,
    stopLossTicks: intOrNull(cfg.stopLossTicks), takeProfitTicks: intOrNull(cfg.takeProfitTicks),
    slippageTicks: numOrNull(cfg.slippageTicks), feePerSideOverride: numOrNull(cfg.feePerSide),
    applyFees: cfg.applyFees, excludePartialEdges: true,
  }), [cfg, candidates]);

  const buildStartRequest = useCallback((demo: boolean): ResearchStartRequest => ({
    run: runRequest(), options: evalOpts,
    campaign: {
      id: (demo ? "demo-" : "") + cfg.campaignName.trim().replace(/\s+/g, "-").toLowerCase() || "research",
      name: cfg.campaignName, hypothesis: cfg.hypothesis, searchSpace: `Fast∈{${cfg.fastList}} × Slow∈{${cfg.slowList}}, Fast<Slow`,
      selectionMetric: cfg.selectionMetric, trialBudget: cfg.trialBudget || candidates.length, holdoutFraction: cfg.holdoutFraction,
    },
    candidates, selectionMetric: cfg.selectionMetric,
    mode: cfg.mode, trainBars: cfg.trainBars, testBars: cfg.testBars, stepBars: null,
    labelSpanBars: 0, embargoBars: cfg.embargoBars, warmupBars: cfg.warmupBars, holdoutFraction: cfg.holdoutFraction,
    monteCarloSource: cfg.mcSource, monteCarloMethod: cfg.mcMethod, monteCarloIterations: cfg.mcIterations,
    seed: cfg.seed, blockLength: intOrNull(cfg.blockLength), capitalBarrier: numOrNull(cfg.capitalBarrier),
    robustnessMetric: cfg.robustnessMetric, overfittingBlocks: cfg.overfittingBlocks, estimateEffectiveTrials: true,
    benchmarkId: cfg.benchmarkId || null, strategyIsFullyFunded: cfg.fullyFunded, isDemo: demo,
  }), [cfg, candidates, runRequest, evalOpts]);

  // --- Mehrstrategie-Vergleich (eine Kampagne, mehrere Familien) ---
  const [campaign, setCampaign] = useState<ResearchCampaignResponse | null>(null);
  const [focusRunId, setFocusRunId] = useState<string | null>(null);
  const campPollRef = useRef<number | null>(null);

  const families = useMemo((): ResearchFamilyInput[] => {
    const list: ResearchFamilyInput[] = [];
    if (cfg.familySma) {
      const c = buildCandidates(cfg.fastList, cfg.slowList);
      if (c.length) list.push({ key: "movingaverage", strategyId: "movingaverage", name: "SMA-Crossover (Referenz)", candidates: c });
    }
    if (cfg.familyDonchian) {
      const c = buildDonchianCandidates(cfg.channelList);
      if (c.length) list.push({ key: "donchian", strategyId: "donchian", name: "Donchian-Ausbruch (Referenz)", candidates: c });
    }
    return list;
  }, [cfg.familySma, cfg.familyDonchian, cfg.fastList, cfg.slowList, cfg.channelList]);

  const buildCampaignRequest = useCallback((demo: boolean): ResearchCampaignStartRequest => ({
    run: runRequest(), options: evalOpts,
    campaign: {
      id: (demo ? "demo-" : "") + (cfg.campaignName.trim().replace(/\s+/g, "-").toLowerCase() || "vergleich"),
      name: cfg.campaignName, hypothesis: cfg.hypothesis,
      searchSpace: families.map((f) => `${f.name}: ${f.candidates.length} Kandidaten`).join(" · "),
      selectionMetric: cfg.selectionMetric, trialBudget: 0, holdoutFraction: cfg.holdoutFraction,
    },
    families, selectionMetric: cfg.selectionMetric,
    mode: cfg.mode, trainBars: cfg.trainBars, testBars: cfg.testBars, stepBars: null,
    labelSpanBars: 0, embargoBars: cfg.embargoBars, warmupBars: cfg.warmupBars, holdoutFraction: cfg.holdoutFraction,
    monteCarloSource: cfg.mcSource, monteCarloMethod: cfg.mcMethod, monteCarloIterations: cfg.mcIterations,
    seed: cfg.seed, blockLength: intOrNull(cfg.blockLength), capitalBarrier: numOrNull(cfg.capitalBarrier),
    robustnessMetric: cfg.robustnessMetric, overfittingBlocks: cfg.overfittingBlocks, estimateEffectiveTrials: true,
    benchmarkId: cfg.benchmarkId || null, strategyIsFullyFunded: cfg.fullyFunded, isDemo: demo,
  }), [cfg, families, runRequest, evalOpts]);

  function stopCampPolling() { if (campPollRef.current) { clearTimeout(campPollRef.current); campPollRef.current = null; } }
  const startCampPolling = useCallback((groupId: string) => {
    stopCampPolling();
    const tick = async () => {
      try {
        const res = await quantApi.campaign(groupId);
        if (res.ok) {
          setCampaign(res);
          const anyRunning = res.families.some((f) => !isTerminal(f.status));
          if (anyRunning) { campPollRef.current = window.setTimeout(tick, 900); return; }
          setBusy(false);
        }
      } catch { campPollRef.current = window.setTimeout(tick, 1500); return; }
    };
    campPollRef.current = window.setTimeout(tick, 500);
  }, []);

  const startComparison = useCallback(async (demo: boolean) => {
    if (families.length < 2) { setError("Für einen Vergleich mindestens zwei Familien mit gültigen Kandidaten auswählen."); return; }
    setBusy(true); setError(null); setRun(null); setFocusRunId(null);
    try {
      const res = await quantApi.startCampaign(buildCampaignRequest(demo));
      if (!res.ok || !res.groupId) { setError(res.error ?? "Vergleich-Start fehlgeschlagen."); setBusy(false); return; }
      setCampaign(res);
      localStorage.setItem(LAST_CAMPAIGN_KEY, res.groupId);
      startCampPolling(res.groupId);
    } catch (e) { setError(e instanceof Error ? e.message : String(e)); setBusy(false); }
  }, [buildCampaignRequest, families, startCampPolling]);

  // Klick auf eine Familienzeile: den zugehörigen Lauf als Detail laden (bestehende RunView).
  const focusFamily = useCallback(async (runId: string | null | undefined) => {
    if (!runId) return;
    setFocusRunId(runId);
    const res = await quantApi.research(runId).catch(() => null);
    if (res?.ok && res.run) setRun(res.run);
  }, []);

  function stopPolling() { if (pollRef.current) { clearTimeout(pollRef.current); pollRef.current = null; } }
  const startPolling = useCallback((runId: string) => {
    stopPolling();
    const tick = async () => {
      try {
        const res = await quantApi.research(runId);
        if (res.ok && res.run) {
          setRun(res.run);
          if (!isTerminal(res.run.status)) { pollRef.current = window.setTimeout(tick, 800); return; }
          setBusy(false);
          quantApi.researchList(null).then(setSavedRuns).catch(() => {});
        }
      } catch { pollRef.current = window.setTimeout(tick, 1500); return; }
    };
    pollRef.current = window.setTimeout(tick, 500);
  }, []);

  const start = useCallback(async (demo: boolean) => {
    if (candidates.length === 0) { setError("Kein gültiger Kandidatensatz (Fast < Slow erforderlich)."); return; }
    setBusy(true); setError(null);
    try {
      const res = await quantApi.startResearch(buildStartRequest(demo));
      if (!res.ok || !res.run) { setError(res.error ?? "Start fehlgeschlagen."); setBusy(false); return; }
      setRun(res.run);
      localStorage.setItem(LAST_RUN_KEY, res.run.runId);
      startPolling(res.run.runId);
    } catch (e) { setError(e instanceof Error ? e.message : String(e)); setBusy(false); }
  }, [buildStartRequest, candidates, startPolling]);

  const cancel = useCallback(async () => {
    if (campaign?.families.length) {
      await Promise.all(campaign.families.filter((f) => !isTerminal(f.status))
        .map((f) => quantApi.cancelResearch(f.runId).catch(() => {})));
    } else if (run) { await quantApi.cancelResearch(run.runId).catch(() => {}); }
  }, [run, campaign]);

  const openRun = useCallback(async (runId: string) => {
    setError(null);
    const res = await quantApi.research(runId).catch(() => null);
    if (res?.ok && res.run) {
      setRun(res.run); localStorage.setItem(LAST_RUN_KEY, runId);
      if (res.run.status === "Running") { setBusy(true); startPolling(runId); }
    }
  }, [startPolling]);

  const campaignRunning = campaign?.families.some((f) => f.status === "Running") ?? false;
  const running = run?.status === "Running" || campaignRunning || busy;

  return (
    <div className="flex flex-col h-full min-h-0">
      {/* Kopf / Steuerleiste */}
      <div className="flex-shrink-0 border-b border-[var(--line)] bg-[var(--bg-2)]">
        <div className="flex flex-wrap items-center gap-2 px-4 py-2">
          <span className="text-sm font-medium text-[var(--fg)]">Research</span>
          <span className="text-[11px] text-[var(--fg-faint)]">
            Einmal konfigurieren · „Research starten" · alle Prüfungen automatisch · Simulation, keine Orders
          </span>
          <div className="ml-auto flex items-center gap-2">
            {savedRuns.length > 0 && (
              <Select
                value={run?.runId ?? ""}
                onChange={(v) => v && openRun(v)}
                className="w-[260px]"
                options={[{ value: "", label: "Gespeicherte Läufe…" }, ...savedRuns.map((r) => ({
                  value: r.runId,
                  label: `${r.isDemo ? "DEMO · " : ""}${r.config.campaign.name} · ${r.status} · ${formatDate(new Date(r.createdUtc).getTime())}`,
                }))]}
              />
            )}
            {running ? (
              <button onClick={cancel} className="h-[30px] px-3 rounded-md bg-[var(--red)] text-black text-xs font-medium">Abbrechen</button>
            ) : (
              <>
                <button onClick={() => startComparison(false)} className="h-[30px] px-4 rounded-md bg-[var(--key)] text-black text-xs font-semibold" title="Mehrere Strategie-Familien vergleichen (eine Kampagne)">Vergleich starten</button>
                <button onClick={() => startComparison(true)} title="Demo-/Abnahmevergleich (verbraucht keinen echten Forschungs-Holdout)" className="h-[30px] px-3 rounded-md border border-[var(--line-2)] text-[var(--fg)] text-xs">Demo-Vergleich</button>
                <span className="mx-1 h-4 w-px bg-[var(--line-2)]" />
                <button onClick={() => start(false)} className="h-[30px] px-3 rounded-md border border-[var(--line-2)] text-[var(--fg)] text-xs" title="Einzelne Strategie (nur eine Familie)">Einzellauf</button>
              </>
            )}
          </div>
        </div>
      </div>

      <div className="flex-1 overflow-y-auto p-4 space-y-4">
        {error && <div className="rounded-lg border border-[var(--red)] bg-[var(--panel)] p-3 text-xs text-[var(--red)]">{error}</div>}

        {/* Konfiguration — bearbeitbarer Entwurf für einen NEUEN Lauf (ändert kein geladenes Ergebnis) */}
        <Panel>
          <div className="flex items-center gap-2 mb-2">
            <span className="rounded bg-[var(--panel-3)] px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-[var(--fg-dim)]">Neuer Lauf · Entwurf</span>
            <span className="text-[11px] text-[var(--fg-faint)]">Änderungen hier starten einen neuen Lauf — das unten angezeigte gespeicherte Ergebnis bleibt unverändert.</span>
          </div>
          <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4 xl:grid-cols-6">
            <Field label="Datenquelle"><Select value={cfg.dataSourceId} onChange={(v) => set("dataSourceId", v)} options={sources.map((s) => ({ value: s.id, label: s.label + (s.available ? "" : " (n/a)") }))} /></Field>
            <Field label="Symbol"><Select value={cfg.symbol} onChange={(v) => set("symbol", v)} options={(instruments.length ? instruments.map((i) => i.symbol) : [cfg.symbol]).map((s) => ({ value: s, label: s }))} /></Field>
            <Field label="Timeframe (min)"><Input type="number" value={String(cfg.timeframeMinutes)} onChange={(v) => set("timeframeMinutes", parseInt(v, 10) || 1)} /></Field>
            <Field label="Von (UTC)"><Input type="datetime-local" value={cfg.fromUtc} onChange={(v) => set("fromUtc", v)} /></Field>
            <Field label="Bis (UTC, optional)"><Input type="datetime-local" value={cfg.toUtc} onChange={(v) => set("toUtc", v)} /></Field>
            <Field label="Auswahlkriterium"><Select value={cfg.selectionMetric} onChange={(v) => set("selectionMetric", v)} options={["sharpe", "sortino", "cagr", "calmar", "netprofit"].map((m) => ({ value: m, label: m }))} /></Field>
            <Field label="Fast-Perioden"><Input value={cfg.fastList} onChange={(v) => set("fastList", v)} /></Field>
            <Field label="Slow-Perioden"><Input value={cfg.slowList} onChange={(v) => set("slowList", v)} /></Field>
            <Field label="Kapital"><Input type="number" value={String(cfg.initialBalance)} onChange={(v) => set("initialBalance", parseFloat(v) || 0)} /></Field>
            <Field label="Menge"><Input type="number" value={String(cfg.quantity)} onChange={(v) => set("quantity", parseInt(v, 10) || 1)} /></Field>
            <Field label="Holdout-Anteil"><Input type="number" value={String(cfg.holdoutFraction)} onChange={(v) => set("holdoutFraction", parseFloat(v) || 0)} /></Field>
            <Field label="Benchmark (optional)"><Select value={cfg.benchmarkId} onChange={(v) => set("benchmarkId", v)} options={[{ value: "", label: "— keiner —" }, ...benchmarks.map((b) => ({ value: b, label: b }))]} /></Field>
          </div>

          {/* Strategie-Familien für den Vergleich — jede Familie hat ihren EIGENEN Suchraum */}
          <div className="mt-3 border-t border-[var(--line)] pt-3">
            <div className="mb-2 text-[11px] font-semibold uppercase tracking-wide text-[var(--fg-dim)]">Strategie-Familien (Vergleich)</div>
            <div className="grid gap-3 lg:grid-cols-2">
              <div className={cn("rounded-md border p-2", cfg.familySma ? "border-[var(--key)]" : "border-[var(--line)]")}>
                <label className="flex items-center gap-2 text-xs font-medium text-[var(--fg)]">
                  <input type="checkbox" checked={cfg.familySma} onChange={(e) => set("familySma", e.target.checked)} />
                  SMA-Crossover <span className="text-[10px] font-normal text-[var(--fg-faint)]">Referenz · keine Edge-Behauptung</span>
                </label>
                <div className="mt-2 grid grid-cols-2 gap-2">
                  <Field label="Fast-Perioden"><Input value={cfg.fastList} onChange={(v) => set("fastList", v)} /></Field>
                  <Field label="Slow-Perioden"><Input value={cfg.slowList} onChange={(v) => set("slowList", v)} /></Field>
                </div>
                <div className="mt-1 text-[10px] text-[var(--fg-faint)]">{buildCandidates(cfg.fastList, cfg.slowList).length} Kandidaten (Fast &lt; Slow)</div>
              </div>
              <div className={cn("rounded-md border p-2", cfg.familyDonchian ? "border-[var(--key)]" : "border-[var(--line)]")}>
                <label className="flex items-center gap-2 text-xs font-medium text-[var(--fg)]">
                  <input type="checkbox" checked={cfg.familyDonchian} onChange={(e) => set("familyDonchian", e.target.checked)} />
                  Donchian-Ausbruch <span className="text-[10px] font-normal text-[var(--fg-faint)]">Referenz · deterministisch · keine Edge-Behauptung</span>
                </label>
                <div className="mt-2 grid grid-cols-2 gap-2">
                  <Field label="Kanal-Längen (Bars)"><Input value={cfg.channelList} onChange={(v) => set("channelList", v)} /></Field>
                </div>
                <div className="mt-1 text-[10px] text-[var(--fg-faint)]">{buildDonchianCandidates(cfg.channelList).length} Kandidaten</div>
              </div>
            </div>
          </div>

          <button onClick={() => setShowAdvanced((s) => !s)} className="mt-3 text-[11px] text-[var(--key)]">
            {showAdvanced ? "▾ Erweiterte Einstellungen ausblenden" : "▸ Erweiterte Einstellungen"}
          </button>
          {showAdvanced && (
            <div className="mt-3 grid gap-3 sm:grid-cols-2 lg:grid-cols-4 xl:grid-cols-6 border-t border-[var(--line)] pt-3">
              <Field label="SL (Ticks, leer=Profil)"><Input value={cfg.stopLossTicks} onChange={(v) => set("stopLossTicks", v)} /></Field>
              <Field label="TP (Ticks, leer=Profil)"><Input value={cfg.takeProfitTicks} onChange={(v) => set("takeProfitTicks", v)} /></Field>
              <Field label="Slippage (Ticks, leer=Profil)"><Input value={cfg.slippageTicks} onChange={(v) => set("slippageTicks", v)} /></Field>
              <Field label="Gebühr/Seite (leer=Profil)"><Input value={cfg.feePerSide} onChange={(v) => set("feePerSide", v)} /></Field>
              <Field label="Gebühren anwenden"><Select value={cfg.applyFees ? "1" : "0"} onChange={(v) => set("applyFees", v === "1")} options={[{ value: "1", label: "ja" }, { value: "0", label: "nein (brutto)" }]} /></Field>
              <Field label="WF-Modus"><Select value={cfg.mode} onChange={(v) => set("mode", v)} options={[{ value: "Rolling", label: "Rolling" }, { value: "Anchored", label: "Anchored" }]} /></Field>
              <Field label="Train-Bars"><Input type="number" value={String(cfg.trainBars)} onChange={(v) => set("trainBars", parseInt(v, 10) || 0)} /></Field>
              <Field label="Test-Bars"><Input type="number" value={String(cfg.testBars)} onChange={(v) => set("testBars", parseInt(v, 10) || 0)} /></Field>
              <Field label="Embargo-Bars"><Input type="number" value={String(cfg.embargoBars)} onChange={(v) => set("embargoBars", parseInt(v, 10) || 0)} /></Field>
              <Field label="Warmup-Bars"><Input type="number" value={String(cfg.warmupBars)} onChange={(v) => set("warmupBars", parseInt(v, 10) || 0)} /></Field>
              <Field label="MC-Quelle"><Select value={cfg.mcSource} onChange={(v) => set("mcSource", v)} options={[{ value: "trades", label: "Trades (NetPnL)" }, { value: "returns", label: "Periodenrenditen" }]} /></Field>
              <Field label="MC-Verfahren"><Select value={cfg.mcMethod} onChange={(v) => set("mcMethod", v)} options={[{ value: "Permutation", label: "Permutation" }, { value: "MovingBlock", label: "Moving-Block" }, { value: "Stationary", label: "Stationär" }]} /></Field>
              <Field label="MC-Iterationen"><Input type="number" value={String(cfg.mcIterations)} onChange={(v) => set("mcIterations", parseInt(v, 10) || 0)} /></Field>
              <Field label="Seed"><Input type="number" value={String(cfg.seed)} onChange={(v) => set("seed", parseInt(v, 10) || 0)} /></Field>
              <Field label="Blocklänge (leer=n^⅓)"><Input value={cfg.blockLength} onChange={(v) => set("blockLength", v)} /></Field>
              <Field label="Kapitalgrenze (leer=keine)"><Input value={cfg.capitalBarrier} onChange={(v) => set("capitalBarrier", v)} /></Field>
              <Field label="PBO-Blöcke"><Input type="number" value={String(cfg.overfittingBlocks)} onChange={(v) => set("overfittingBlocks", parseInt(v, 10) || 2)} /></Field>
              <Field label="Kampagne"><Input value={cfg.campaignName} onChange={(v) => set("campaignName", v)} /></Field>
            </div>
          )}

          {/* Kurz-Zusammenfassung des geplanten Umfangs vor dem Start */}
          <div className="mt-3 flex flex-wrap gap-x-4 gap-y-1 text-[11px] text-[var(--fg-dim)]">
            <span>Familien: <b className="text-[var(--fg)]">{families.length}</b> ({families.reduce((n, f) => n + f.candidates.length, 0)} Kandidaten)</span>
            <span>Einzellauf-Kandidaten: <b className="text-[var(--fg)]">{candidates.length}</b></span>
            <span>Auswahl: <b className="text-[var(--fg)]">{cfg.selectionMetric}</b> (vor der Suche gesperrt)</span>
            <span>Holdout: <b className="text-[var(--fg)]">{Math.round(cfg.holdoutFraction * 100)} %</b> reserviert</span>
            <span>WF: <b className="text-[var(--fg)]">{cfg.mode}</b>, Train {cfg.trainBars}/Test {cfg.testBars}</span>
            <span>MC: <b className="text-[var(--fg)]">{cfg.mcIterations}×</b> {cfg.mcMethod}, Seed {cfg.seed}</span>
            <span>Frequenz: <b className="text-[var(--fg)]">{evalOpts.frequency}</b></span>
          </div>
        </Panel>

        {!run && !campaign && (
          <Panel>
            <h2 className="text-sm font-medium text-[var(--fg)] mb-1">Noch kein Vergleich</h2>
            <p className="text-xs text-[var(--fg-dim)] leading-relaxed">
              Wähle oben die <b>Strategie-Familien</b> mit ihren Suchräumen und klicke <b>„Vergleich starten"</b>. Jede Familie
              durchläuft automatisch <b>Daten &amp; Qualität → Basis-Backtest → Walk-forward → Robustheit → Monte Carlo → Overfitting</b>
              {" "}und bereitet ihren finalen Holdout vor (dieser wird erst nach ausdrücklicher Bestätigung ausgewertet). Anschließend
              werden die Familien auf zeitlich ausgerichteten Netto-OOS-Renditen mit einem <b>gepaarten Block-Bootstrap</b> verglichen.
              <b> „Completed"</b> heißt: Berechnung abgeschlossen — nicht, dass eine Strategie „bestanden" hat. Mit <b>„Demo-Vergleich"</b>
              läuft ein gekennzeichneter Abnahmelauf ohne echten Holdout-Verbrauch.
            </p>
          </Panel>
        )}

        {campaign && (
          <ComparisonView
            campaign={campaign}
            focusRunId={focusRunId}
            running={campaignRunning}
            onFocus={focusFamily}
          />
        )}

        {run && (
          <>
            {campaign && (
              <div className="flex items-center gap-2 text-[11px] text-[var(--fg-dim)]">
                <span className="rounded bg-[var(--panel-3)] px-1.5 py-0.5 font-semibold uppercase tracking-wide">Detail der gewählten Familie</span>
                <span>{run.familyName ?? run.config.run.strategy}</span>
              </div>
            )}
            <RunView run={run} onEvaluatedHoldout={() => (campaign ? focusFamily(run.runId) : openRun(run.runId))} groupHoldout={campaign?.holdout} />
          </>
        )}
      </div>
    </div>
  );
}

// ==============================================================================================
// Ergebnisdarstellung eines Laufs
// ==============================================================================================

function RunView({ run, onEvaluatedHoldout, groupHoldout }: {
  run: ResearchRunRecord; onEvaluatedHoldout: () => void; groupHoldout?: ResearchGroupHoldout | null;
}) {
  const steps = run.steps;
  const done = steps.filter((s) => isTerminal(s.status) || s.status === "NotComputable" || s.status === "Skipped").length;
  const overall = done / Math.max(1, steps.length);
  const a = run.analysis;

  return (
    <div className="space-y-4">
      <RunProvenance run={run} />

      {/* Fortschritt + Schritt-Chips */}
      <Panel>
        <div className="flex items-center justify-between mb-2">
          <span className="text-sm font-medium text-[var(--fg)]">
            {run.isDemo && <span className="mr-2 rounded bg-[var(--gold)] px-1.5 py-0.5 text-[10px] font-semibold text-black">DEMO</span>}
            {run.config.campaign.name} · <span className="mono text-[var(--fg-dim)]">{run.runId}</span>
          </span>
          <span className={cn("text-xs", run.status === "Failed" ? "text-[var(--red)]" : run.status === "Cancelled" ? "text-[var(--gold)]" : "text-[var(--fg-dim)]")}>
            {run.status}{run.statusReason ? ` — ${run.statusReason}` : ""}
          </span>
        </div>
        <div className="h-1.5 w-full rounded bg-[var(--panel-3)] overflow-hidden mb-2">
          <div className="h-full bg-[var(--key)] transition-all" style={{ width: `${Math.round(overall * 100)}%` }} />
        </div>
        <div className="flex flex-wrap gap-2">
          {steps.map((s) => <StepChip key={s.key} step={s} />)}
        </div>
      </Panel>

      {/* Übersicht */}
      {a?.ok && (
        <Section id="overview" title="Ergebnisübersicht" basis={run.holdoutBars > 0 ? "Entwicklungsbereich (vor reserviertem Holdout)" : "Vollständige Historie (kein Holdout)"}>
          <div className="flex flex-wrap divide-x divide-[var(--line)] rounded-lg border border-[var(--line)] bg-[var(--panel)] mb-3">
            <HeadlineStat label="Trades" value={String(a.trades)} />
            <HeadlineStat label="Endkapital (real.)" value={Number(a.finalEquityRealized).toLocaleString("de-DE", { maximumFractionDigits: 0 })} />
            <HeadlineStat label="Startkapital" value={Number(a.initialBalance).toLocaleString("de-DE", { maximumFractionDigits: 0 })} />
            <HeadlineStat label="Frequenz" value={a.frequency} />
          </div>
          <div className="grid gap-4 xl:grid-cols-2">
            <MetricList title="Kennzahlen (realisiert)" metrics={a.metricsRealized} />
            <div className="space-y-3">
              {a.dataQuality && a.dataQuality.issues.length > 0 && (
                <NoteBlock title="Datenqualität" tone="warn"
                  items={a.dataQuality.issues.map((i) => `[${i.severity}] ${i.code}: ${i.message} (${i.count})`)} />
              )}
              <NoteBlock title="Hinweise" items={a.notes} />
            </div>
          </div>
          {a.curve.length > 0 && <div className="mt-3"><EquityDrawdownPanel curve={a.curve} height={280} /></div>}
          {a.monthly.length > 0 && <div className="mt-3"><MonthlyReturnsTable monthly={a.monthly} /></div>}
          {a.rolling.length > 0 && <div className="mt-3"><RollingChart points={a.rolling} window={a.rollingWindow} /></div>}
        </Section>
      )}

      {/* Backtest-Kerzenchart mit Trades */}
      {a?.ok && <BacktestBlock run={run} />}

      {/* Walk-forward */}
      <StepSection run={run} stepKey="walkforward" title="Walk-forward & Kandidatenauswahl">
        {run.walkForward?.ok && (
          <div className="space-y-3">
            <WalkForwardTimeline folds={run.walkForward.folds} totalBars={run.walkForward.totalBars} holdoutFromT={run.walkForward.holdoutFromT} holdoutToT={run.walkForward.holdoutToT} />
            {run.walkForward.oosEquity.length > 1 && (
              <OosEquityChart t={run.walkForward.oosT} equity={run.walkForward.oosEquity} />
            )}
            <MetricList title="Out-of-Sample-Kennzahlen (verkettete Testfenster)" metrics={run.walkForward.oosMetrics} />
          </div>
        )}
      </StepSection>

      {/* Robustheit */}
      <StepSection run={run} stepKey="robustness" title="Robustheit (Stress)">
        {run.robustness?.ok && (
          <div className="grid gap-4 xl:grid-cols-2">
            {run.robustness.blocks.map((b) => (
              <div key={b.dimension} className="space-y-1">
                <div className="text-[11px] text-[var(--fg-dim)]">{b.dimension} · Baseline {formatNumber(b.baseline)} · schlechtester {formatNumber(b.worst)}</div>
                {new Set(b.cells.map((c) => c.feeMultiplier)).size > 1 && new Set(b.cells.map((c) => c.slippageMultiplier)).size > 1
                  ? <CostHeatmap block={b} />
                  : <ScenarioBars block={b} />}
              </div>
            ))}
          </div>
        )}
      </StepSection>

      {/* Monte Carlo */}
      <StepSection run={run} stepKey="montecarlo" title="Monte Carlo / Bootstrap">
        {run.monteCarlo?.ok && <MonteCarloBlock mc={run.monteCarlo} />}
      </StepSection>

      {/* Overfitting */}
      <StepSection run={run} stepKey="overfitting" title="Overfitting (PBO / PSR / DSR)">
        {run.overfitting?.ok && <OverfittingBlock of={run.overfitting} />}
      </StepSection>

      {/* Benchmark */}
      {a?.benchmark && (
        <Section id="benchmark" title="Benchmark-Vergleich" basis="Gemeinsamer Zeitraum">
          {a.benchmark.available ? (
            <div className="space-y-3">
              <EquityDrawdownPanel curve={a.curve} benchmark={{ t: a.benchmark.t, strategyIndex: a.benchmark.strategyIndex, benchmarkIndex: a.benchmark.benchmarkIndex, name: a.benchmark.name }} height={260} />
              <MetricList title={`Vergleich vs. ${a.benchmark.name}`} metrics={a.benchmark.metrics} />
              <NoteBlock title="Annahmen / Währung / Total-Return" items={[...a.benchmark.assumptions, ...a.benchmark.warnings]} />
            </div>
          ) : <Unavailable title="Kein Benchmark-Vergleich" reason={a.benchmark.unavailableReason ?? "Keine Benchmark-Daten hinterlegt."} />}
        </Section>
      )}

      {/* Holdout */}
      <HoldoutBlock run={run} onEvaluated={onEvaluatedHoldout} groupHoldout={groupHoldout} />

      {/* Register */}
      {run.campaignId && <RegisterBlock campaignId={run.campaignId} />}
    </div>
  );
}

// ---------------------------------------------------------------------------------------------
// Bausteine

function fmtIso(iso?: string | null): string {
  if (!iso) return "—";
  const t = new Date(iso).getTime();
  return Number.isFinite(t) ? formatDate(t) : "—";
}

// Unveränderliche Herkunft des GELADENEN Laufs — klar getrennt vom bearbeitbaren Formular (Befund B).
function RunProvenance({ run }: { run: ResearchRunRecord }) {
  const c = run.config; const r = c.run;
  const cost = r.applyFees ? (r.feePerSideOverride != null ? `Gebühr/Seite ${r.feePerSideOverride}` : "mit Profilgebühren") : "brutto (keine Gebühren)";
  const items: [string, string][] = [
    ["Run-ID", run.runId],
    ["Kampagne", run.campaignId ?? c.campaign.id],
    ["Strategie", r.strategy],
    ["Kandidaten", String(c.candidates.length)],
    ["Symbol / TF", `${r.symbol} / ${r.timeframeMinutes}m`],
    ["Tatsächlicher Zeitraum", `${fmtIso(run.dataFrom)} … ${fmtIso(run.dataTo)}`],
    ["Entwicklung bis (excl.)", fmtIso(run.developmentToUtc)],
    ["Reservierter Holdout", run.holdoutBars > 0 ? `${fmtIso(run.holdoutFrom)} … ${fmtIso(run.holdoutTo)} (${run.holdoutBars} Bars)` : "keiner"],
    ["Bars gesamt / Dev / Holdout", `${run.totalBars} / ${run.developmentBars} / ${run.holdoutBars}`],
    ["Kapital", r.initialBalance.toLocaleString("de-DE")],
    ["Kosten", cost],
    ["SL / TP (Ticks)", `${r.stopLossTicks ?? "Profil"} / ${r.takeProfitTicks ?? "Profil"}`],
    ["Auswahlkriterium", c.selectionMetric],
    ["Seed", String(c.seed)],
    ["Daten-Fingerabdruck", run.dataSha ? run.dataSha.slice(0, 12) + "…" : "—"],
  ];
  // Läufe VOR dem Leakage-Fix tragen keine Datenbereichs-/Split-Herkunft — sie können in den Vorprüfungen
  // Holdout-Daten enthalten und sind daher für eine unabhängige finale Prüfung ungeeignet.
  const preFix = !run.dataFrom;
  return (
    <section className="rounded-lg border border-[var(--line-2)] bg-[var(--panel-2)] p-3">
      <div className="flex items-center gap-2 mb-2">
        <span className="rounded bg-[var(--cyan)] px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-black">Geladener Lauf</span>
        <span className="text-xs text-[var(--fg-dim)]">gespeicherte Konfiguration — unveränderlich</span>
      </div>
      {preFix && (
        <div className="mb-2 rounded border border-[var(--red)] bg-[var(--panel)] px-2 py-1.5 text-[11px] text-[var(--red)]">
          ⚠ Vor dem Leakage-Fix erzeugt: Die Vorprüfungen dieses Laufs könnten Daten des reservierten Holdouts enthalten.
          Er bleibt erhalten, ist aber für eine unabhängige finale Prüfung <b>ungeeignet</b>. Bitte einen neuen Lauf starten.
        </div>
      )}
      <div className="grid gap-x-6 gap-y-1 sm:grid-cols-2 lg:grid-cols-3 text-[11px]">
        {items.map(([k, v]) => (
          <div key={k} className="flex justify-between gap-3 border-b border-[var(--line)] py-0.5">
            <span className="text-[var(--fg-faint)]">{k}</span>
            <span className="mono text-[var(--fg)] text-right truncate" title={v}>{v}</span>
          </div>
        ))}
      </div>
      <p className="mt-2 text-[10px] text-[var(--fg-faint)]">
        Vorprüfungen (Basis-Backtest, Benchmark, Robustheit, Monte Carlo, Walk-forward, PBO/PSR/DSR) nutzen ausschließlich
        den Entwicklungsbereich; der reservierte Holdout bleibt bis zur bestätigten finalen Auswertung unberührt.
      </p>
    </section>
  );
}

function StepChip({ step }: { step: ResearchStepState }) {
  const color: Record<ResearchStepStatus, string> = {
    Pending: "border-[var(--line-2)] text-[var(--fg-faint)]",
    Running: "border-[var(--key)] text-[var(--key)] animate-pulse",
    Completed: "border-[var(--key)] text-[var(--key)]",
    NotComputable: "border-[var(--gold)] text-[var(--gold)]",
    Skipped: "border-[var(--line-2)] text-[var(--fg-faint)]",
    Failed: "border-[var(--red)] text-[var(--red)]",
    Cancelled: "border-[var(--gold)] text-[var(--gold)]",
  };
  const mark: Record<ResearchStepStatus, string> = {
    Pending: "○", Running: "◍", Completed: "●", NotComputable: "◐", Skipped: "–", Failed: "✕", Cancelled: "⊘",
  };
  return (
    <span title={step.reason ?? step.dataBasis ?? ""} className={cn("inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-[10px]", color[step.status])}>
      <span>{mark[step.status]}</span>{step.label}
    </span>
  );
}

function Section({ id, title, basis, children }: { id?: string; title: string; basis?: string; children: React.ReactNode }) {
  return (
    <section id={id} className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3">
      <div className="flex items-center justify-between mb-2">
        <h2 className="text-sm font-medium text-[var(--fg)]">{title}</h2>
        {basis && <span className="text-[10px] uppercase tracking-wide text-[var(--fg-faint)]">Datenbasis: {basis}</span>}
      </div>
      {children}
    </section>
  );
}

function StepSection({ run, stepKey, title, children }: { run: ResearchRunRecord; stepKey: string; title: string; children: React.ReactNode }) {
  const step = run.steps.find((s) => s.key === stepKey);
  if (!step) return null;
  return (
    <Section id={stepKey} title={title} basis={step.dataBasis ?? undefined}>
      {step.status === "Completed" ? children
        : step.status === "Running" ? <div className="text-xs text-[var(--key)]">läuft…</div>
        : step.status === "Pending" ? <div className="text-xs text-[var(--fg-faint)]">wartet…</div>
        : <Unavailable title={step.status === "Skipped" ? "Übersprungen" : step.status === "NotComputable" ? "Nicht berechenbar" : "Nicht verfügbar"} reason={step.reason ?? step.status} />}
    </Section>
  );
}

function BacktestBlock({ run }: { run: ResearchRunRecord }) {
  const [candles, setCandles] = useState<Candle[]>([]);
  const [trades, setTrades] = useState<OhlcTrade[]>([]);
  const [equity, setEquity] = useState<EquityPoint[]>([]);
  const [costs, setCosts] = useState<CostProfile | null>(null);
  const [selected, setSelected] = useState<number | null>(null);
  const [focusKey, setFocusKey] = useState(0);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    const ac = new AbortController();
    (async () => {
      setLoading(true);
      try {
        // Kerzenchart NUR auf dem Entwicklungsbereich (kein Holdout-Leakage): devRun begrenzt ToUtc auf den Holdout-Start.
        const res = await backtestApi.run(run.devRun ?? run.config.run, ac.signal);
        if (res.ok && res.result) {
          setCandles(res.candles); setTrades(res.result.trades); setEquity(res.result.equity); setCosts(res.costProfile ?? null);
        }
      } catch { /* ignore */ } finally { if (!ac.signal.aborted) setLoading(false); }
    })();
    return () => ac.abort();
  }, [run.runId, run.config.run]);

  const chartTrades = useMemo(() => chartTradesAt(trades, candles.length), [trades, candles.length]);
  const selectTrade = (i: number | null) => { setSelected(i); setFocusKey((k) => k + 1); };

  if (loading) return <Section id="backtest" title="Backtest & Trades"><div className="text-xs text-[var(--fg-faint)]">Lade Kerzen…</div></Section>;
  if (candles.length === 0) return null;

  return (
    <Section id="backtest" title="Backtest & Trades" basis={run.holdoutBars > 0 ? "Entwicklungsbereich (vor reserviertem Holdout)" : "Vollständige Historie (kein Holdout)"}>
      <div className="flex items-center gap-2 mb-2">
        <button onClick={() => selectTrade(relativeTradeIndex(selected, -1, chartTrades.length))} className="h-[26px] px-2 rounded border border-[var(--line-2)] text-xs">‹ Trade</button>
        <button onClick={() => selectTrade(relativeTradeIndex(selected, 1, chartTrades.length))} className="h-[26px] px-2 rounded border border-[var(--line-2)] text-xs">Trade ›</button>
        <button onClick={() => selectTrade(null)} className="h-[26px] px-2 rounded border border-[var(--line-2)] text-xs">Gesamt</button>
        <span className="text-[11px] text-[var(--fg-dim)]">{selected != null ? `Trade ${selected + 1}/${chartTrades.length}` : `${chartTrades.length} Trades`}{costs ? ` · Gebühr/RT ${costs.feeRoundTrip}` : ""}</span>
      </div>
      <BacktestChart candles={candles} trades={chartTrades} selected={selected} focusKey={focusKey} onSelectTrade={selectTrade} className="h-[360px]" />
      {equity.length > 0 && <div className="mt-3"><EquityDrawdownChart equity={equity} initialBalance={run.config.run.initialBalance} height={200} /></div>}
    </Section>
  );
}

function OosEquityChart({ t, equity }: { t: number[]; equity: number[] }) {
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!ref.current) return;
    const chart = echarts.init(ref.current);
    const data = equity.map((e, i) => [t[i] ?? i, e]);
    chart.setOption({
      animation: false, grid: { top: 16, right: 16, bottom: 24, left: 52 },
      tooltip: { trigger: "axis" },
      xAxis: { type: "time", axisLine: { lineStyle: { color: "#232120" } }, axisLabel: { color: "#8a857c", fontSize: 10 } },
      yAxis: { type: "value", scale: true, splitLine: { lineStyle: { color: "#232120" } }, axisLabel: { color: "#8a857c", fontSize: 10 } },
      series: [{ type: "line", data, showSymbol: false, lineStyle: { color: "#7fc4b4", width: 1.5 }, name: "OOS-Equity (verkettet, indexiert)" }],
    });
    const ro = new ResizeObserver(() => chart.resize());
    ro.observe(ref.current);
    return () => { ro.disconnect(); chart.dispose(); };
  }, [t, equity]);
  return <div><div className="text-[11px] text-[var(--fg-dim)] mb-1">Out-of-Sample-Kapitalkurve (verkettete Testfenster, Startindex 1,0)</div><div ref={ref} className="h-[220px] w-full" /></div>;
}

function ScenarioBars({ block }: { block: { cells: { label: string; value: number | null; error?: string | null }[] } }) {
  return (
    <div className="rounded-lg border border-[var(--line)] bg-[var(--panel-2)] p-2 text-[11px]">
      <table className="w-full mono">
        <tbody>
          {block.cells.map((c, k) => (
            <tr key={k} className="border-b border-[var(--line)] last:border-0">
              <td className="py-0.5 text-[var(--fg-dim)]">{c.label}</td>
              <td className="py-0.5 text-right text-[var(--fg)]">{c.error ? <span className="text-[var(--red)]">{c.error}</span> : formatNumber(c.value)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function OverfittingBlock({ of }: { of: import("@/lib/quantApi").QuantOverfittingResponse }) {
  return (
    <div className="space-y-3">
      <div className="flex flex-wrap divide-x divide-[var(--line)] rounded-lg border border-[var(--line)]">
        <HeadlineStat label="PBO" value={formatPercent(of.pbo)} />
        <HeadlineStat label="PSR" value={formatPercent(of.psr)} />
        <HeadlineStat label="DSR" value={formatPercent(of.dsr)} />
        <HeadlineStat label="Versuche (tats./eff.)" value={`${of.actualTrials} / ${formatNumber(of.effectiveTrials, 1)}`} />
      </div>
      {of.pbo !== null && of.pairs.length > 0
        ? <PboScatter pairs={of.pairs} logits={of.logits} />
        : <Unavailable title="PBO nicht berechenbar" reason={of.pboUnavailableReason ?? "Zu wenige Kandidaten/Perioden."} />}
      <div className="grid gap-2 sm:grid-cols-2 text-[11px] text-[var(--fg-dim)]">
        {of.psrUnavailableReason && <div className="text-[var(--gold)]">PSR: {of.psrUnavailableReason}</div>}
        {of.dsrUnavailableReason && <div className="text-[var(--gold)]">DSR: {of.dsrUnavailableReason}</div>}
        {of.effectiveTrialsRationale && <div>Effektive Versuche: {of.effectiveTrialsRationale}</div>}
      </div>
      <NoteBlock title="Grenzen & Hinweise" items={of.notes} />
    </div>
  );
}

function RegisterBlock({ campaignId }: { campaignId: string }) {
  const [trials, setTrials] = useState<import("@/lib/quantApi").QuantTrial[]>([]);
  useEffect(() => { quantApi.trials(campaignId).then(setTrials).catch(() => {}); }, [campaignId]);
  const [open, setOpen] = useState(false);
  return (
    <Section id="register" title={`Versuchsregister (${trials.length})`}>
      <button onClick={() => setOpen((o) => !o)} className="text-[11px] text-[var(--key)] mb-2">{open ? "▾ Details ausblenden" : "▸ Kandidaten/Trials anzeigen"}</button>
      {open && (
        <div className="overflow-x-auto text-[11px] mono">
          <table className="w-full">
            <thead><tr className="text-[var(--fg-faint)] text-left"><th className="py-1 pr-3">Parameter</th><th className="py-1 pr-3">Rolle</th><th className="py-1 pr-3">Status</th><th className="py-1 pr-3">test.sharpe/Periode</th></tr></thead>
            <tbody>
              {trials.map((t) => (
                <tr key={t.id} className="border-t border-[var(--line)]">
                  <td className="py-1 pr-3 text-[var(--fg)]">{Object.entries(t.parameters).map(([k, v]) => `${k}=${v}`).join(", ")}</td>
                  <td className="py-1 pr-3 text-[var(--fg-dim)]">{t.periodRole}</td>
                  <td className="py-1 pr-3 text-[var(--fg-dim)]">{t.status}</td>
                  <td className="py-1 pr-3 text-[var(--fg-dim)]">{formatNumber(t.metrics["test.sharpe_per_period"] ?? null)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Section>
  );
}

// ==============================================================================================
// Mehrstrategie-Vergleich
// ==============================================================================================

const FAMILY_COLORS = ["#7fc4b4", "#d9a441", "#6f9bd8", "#c67fb0", "#9ac47f", "#d87f7f"];
const fmtMoney = (v: number | null | undefined): string =>
  v === null || v === undefined || !Number.isFinite(v)
    ? "n. b."
    : v.toLocaleString("de-DE", { maximumFractionDigits: 0 });

function ComparisonView({ campaign, focusRunId, running, onFocus }: {
  campaign: ResearchCampaignResponse;
  focusRunId: string | null;
  running: boolean;
  onFocus: (runId: string | null | undefined) => void;
}) {
  const cmp = campaign.comparison ?? null;
  const fams = campaign.families;
  const campName = fams[0]?.config.campaign.name ?? "Vergleich";
  // Zeilen: bevorzugt aus dem berechneten Vergleich; sonst Platzhalter aus den Läufen (mit Fortschritt).
  const rows: (ComparisonFamilyRow & { record?: ResearchRunRecord })[] =
    cmp?.families?.map((r) => ({ ...r, record: fams.find((f) => f.runId === r.runId) })) ??
    fams.map((f) => ({
      familyKey: f.familyKey ?? f.runId, familyName: f.familyName ?? f.config.run.strategy,
      strategyId: f.config.run.strategy, runId: f.runId, status: f.status,
      candidates: f.config.candidates.length, oosObservations: f.walkForward?.oosT.length ?? 0,
      record: f,
    }));

  return (
    <div className="space-y-4">
      {/* Kopf + Fortschritt je Familie */}
      <Section title="Vergleichs-Kampagne" basis={cmp?.available ? `Gemeinsamer gepaarter Bootstrap (${cmp.method})` : undefined}>
        <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-[11px] text-[var(--fg-dim)]">
          <span>Kampagne: <b className="text-[var(--fg)]">{campName}</b></span>
          <span>Gruppen-ID: <span className="mono text-[var(--fg-faint)]">{campaign.groupId}</span></span>
          <span>Familien: <b className="text-[var(--fg)]">{fams.length}</b></span>
          {running && <span className="text-[var(--key)]">läuft…</span>}
        </div>
        <div className="mt-2 space-y-1.5">
          {fams.map((f) => (
            <div key={f.runId} className="flex flex-wrap items-center gap-1.5">
              <span className="w-[190px] shrink-0 text-xs text-[var(--fg)]">{f.familyName ?? f.config.run.strategy}</span>
              {f.steps.map((s) => <StepChip key={s.key} step={s} />)}
            </div>
          ))}
        </div>
        <p className="mt-2 text-[10px] leading-relaxed text-[var(--fg-faint)]">
          „Completed" bedeutet: Berechnung abgeschlossen — nicht, dass eine Strategie „bestanden" hat oder künftig
          profitabel ist. Der finale Holdout fließt NICHT in Ranking oder Vergleich ein und wird nicht automatisch ausgewertet.
        </p>
      </Section>

      {/* Vergleichstabelle je Familie */}
      <Section title="Vergleich je Strategie-Familie" basis={cmp?.available ? `${cmp.commonObservations} gemeinsame OOS-${cmp.frequency}-Perioden · ${cmp.method} · ${cmp.iterations}× · Block ${cmp.blockLength} · Seed ${cmp.seed}` : undefined}>
        {cmp?.available && (
          <div className={cn("mb-2 rounded-md border p-2 text-[11px]",
            cmp.sufficient ? "border-[var(--line)] text-[var(--fg-dim)]" : "border-[var(--gold)] bg-[color-mix(in_srgb,var(--gold)_10%,transparent)] text-[var(--gold)]")}>
            <div className="flex flex-wrap gap-x-4 gap-y-1">
              <span>Echte gemeinsame Perioden: <b className="text-[var(--fg)]">{cmp.commonObservations}</b></span>
              <span>Frequenz: <b className="text-[var(--fg)]">{cmp.frequency}</b></span>
              <span>Zeitraum: <b className="text-[var(--fg)]">{cmp.fromT ? formatDate(cmp.fromT) : "—"} … {cmp.toT ? formatDate(cmp.toT) : "—"}</b></span>
              <span>Bootstrap: <b className="text-[var(--fg)]">{cmp.iterations}×</b>, Block <b className="text-[var(--fg)]">{cmp.blockLength}</b></span>
              {cmp.excludedIntervalMismatch > 0 && <span>Intervall-Ausschlüsse: <b className="text-[var(--fg)]">{cmp.excludedIntervalMismatch}</b></span>}
            </div>
            {!cmp.sufficient && (
              <div className="mt-1 font-medium">
                ⚠ Datengrundlage zu klein (unter der Konvention von {cmp.minObservations} gemeinsamen Perioden): KEIN Sieger,
                keine Erfolgswahrscheinlichkeit, kein positiver Freigabestatus. Die Zahlen zeigen nur die Technik. Mehr
                Bootstrap-Replikationen erhöhen NICHT die Zahl historischer Beobachtungen.
              </div>
            )}
          </div>
        )}
        <div className="overflow-x-auto">
          <table className="w-full text-[11px] mono">
            <thead>
              <tr className="text-[var(--fg-faint)] text-left">
                <th className="py-1 pr-2 font-medium">Familie</th>
                <th className="py-1 px-2 font-medium">Status</th>
                <th className="py-1 px-2 font-medium text-right">Kand.</th>
                <th className="py-1 px-2 font-medium">Gewählt (WF)</th>
                <th className="py-1 px-2 font-medium text-right">OOS-Rendite</th>
                <th className="py-1 px-2 font-medium text-right">OOS-Sharpe</th>
                <th className="py-1 px-2 font-medium text-right">OOS-MaxDD</th>
                <th className="py-1 px-2 font-medium text-right">MC-Median</th>
                <th className="py-1 px-2 font-medium text-right">MC P5–P95</th>
                <th className="py-1 px-2 font-medium text-right">&lt; Start</th>
                <th className="py-1 px-2 font-medium text-right">PBO</th>
                <th className="py-1 px-2 font-medium text-right">DSR</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((r, i) => (
                <tr
                  key={r.familyKey}
                  onClick={() => onFocus(r.runId)}
                  className={cn("border-t border-[var(--line)] cursor-pointer hover:bg-[var(--panel-2)]",
                    focusRunId && r.runId === focusRunId && "bg-[var(--panel-2)]")}
                >
                  <td className="py-1 pr-2 text-[var(--fg)]">
                    <span className="inline-block h-2 w-2 rounded-full mr-1.5 align-middle" style={{ background: FAMILY_COLORS[i % FAMILY_COLORS.length] }} />
                    {r.familyName}
                  </td>
                  <td className="py-1 px-2 text-[var(--fg-dim)]">{r.status}</td>
                  <td className="py-1 px-2 text-right text-[var(--fg-dim)]">{r.candidates}</td>
                  <td className="py-1 px-2 text-[var(--fg-dim)]">{r.selectedCandidate ?? "—"}</td>
                  <td className="py-1 px-2 text-right text-[var(--fg)]">{formatPercent(r.oosReturn)}</td>
                  <td className="py-1 px-2 text-right text-[var(--fg)]">{formatNumber(r.oosSharpe, 2)}</td>
                  <td className="py-1 px-2 text-right text-[var(--fg)]">{formatPercent(r.oosMaxDrawdown)}</td>
                  <td className="py-1 px-2 text-right text-[var(--fg)]">{fmtMoney(r.mcMedianFinal)}</td>
                  <td className="py-1 px-2 text-right text-[var(--fg-dim)]">{r.mcP5Final != null ? `${fmtMoney(r.mcP5Final)}–${fmtMoney(r.mcP95Final)}` : "—"}</td>
                  <td className="py-1 px-2 text-right text-[var(--fg-dim)]">{formatPercent(r.mcShareBelowStart)}</td>
                  <td className="py-1 px-2 text-right text-[var(--fg-dim)]">{formatPercent(r.pbo)}</td>
                  <td className="py-1 px-2 text-right text-[var(--fg-dim)]">{formatNumber(r.dsr, 2)}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <p className="mt-2 text-[10px] text-[var(--fg-faint)]">
          Klick auf eine Zeile öffnet unten das Detail der Familie (Walk-forward, eigener Monte-Carlo-Fächer, Robustheit,
          Kerzenchart, Trade-Journal, Versuchsregister). PBO bezieht sich auf den Auswahlprozess der jeweiligen Familie —
          kein Einzelstrategie-Gütesiegel. Geldbeträge in Kontowährung, Renditen in %, MaxDD als Bruchteil.
        </p>
        {!cmp?.available && (
          <div className="mt-2 text-[11px] text-[var(--gold)]">{cmp?.unavailableReason ?? "Vergleich wird berechnet, sobald mindestens zwei Familien eine OOS-Reihe haben…"}</div>
        )}
      </Section>

      {cmp?.available && (
        <>
          <Section title="Gemeinsame Out-of-Sample-Kapitalkurven" basis={`Startkapital ${fmtMoney(cmp.initialCapital)} · gemeinsame Zeitachse (${cmp.commonObservations} Punkte)`}>
            <SharedEquityChart cmp={cmp} />
          </Section>

          <Section title="Gepaarte Differenzen (identische Bootstrap-Pfade)">
            <div className="overflow-x-auto">
              <table className="w-full text-[11px] mono">
                <thead>
                  <tr className="text-[var(--fg-faint)] text-left">
                    <th className="py-1 pr-2 font-medium">Paar (Links − Rechts)</th>
                    <th className="py-1 px-2 font-medium text-right">Δ Median</th>
                    <th className="py-1 px-2 font-medium text-right">Δ P5</th>
                    <th className="py-1 px-2 font-medium text-right">Δ P95</th>
                    <th className="py-1 px-2 font-medium text-right">Links &gt; Rechts{cmp.sufficient ? "" : " (n. b.)"}</th>
                    <th className="py-1 px-2 font-medium text-right">unentschieden</th>
                  </tr>
                </thead>
                <tbody>
                  {cmp.differences.map((d, i) => (
                    <tr key={i} className="border-t border-[var(--line)]">
                      <td className="py-1 pr-2 text-[var(--fg)]">{d.left} − {d.right}</td>
                      <td className={cn("py-1 px-2 text-right", !cmp.sufficient ? "text-[var(--fg-dim)]" : d.deltaMedianFinal >= 0 ? "text-[var(--key)]" : "text-[var(--red)]")}>{fmtMoney(d.deltaMedianFinal)}</td>
                      <td className="py-1 px-2 text-right text-[var(--fg-dim)]">{fmtMoney(d.deltaP5Final)}</td>
                      <td className="py-1 px-2 text-right text-[var(--fg-dim)]">{fmtMoney(d.deltaP95Final)}</td>
                      <td className="py-1 px-2 text-right text-[var(--fg)]">{formatPercent(d.shareLeftBeatsRight)}</td>
                      <td className="py-1 px-2 text-right text-[var(--fg-faint)]">{formatPercent(d.shareTie)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <p className="mt-2 text-[10px] text-[var(--fg-faint)]">
              Differenz = Endkapital Links − Rechts, je Replikation auf DEMSELBEN gezogenen Block-/Zeitindexpfad über
              INTERVALLGLEICHE Renditen (gleicher Start und gleiches Ende). „Links &gt; Rechts" ist eine Auszählung unter dem
              Resampling, keine Wahrscheinlichkeit künftigen Erfolgs. Identische Eingangsreihen ergeben Differenz 0; die
              Kandidatenreihenfolge ändert das Ergebnis nicht.
              {!cmp.sufficient && <> <b className="text-[var(--gold)]">Bei dieser kleinen Stichprobe ist „Links &gt; Rechts" nicht belastbar — kein Sieger.</b></>}
            </p>
          </Section>

          <Section title="Methode & Grenzen">
            <ul className="list-disc pl-4 space-y-1 text-[11px] text-[var(--fg-dim)]">
              {cmp.assumptions.map((a, i) => <li key={`a${i}`}>{a}</li>)}
              {cmp.notes.map((n, i) => <li key={`n${i}`} className="text-[var(--fg-faint)]">{n}</li>)}
            </ul>
          </Section>
        </>
      )}
    </div>
  );
}

function SharedEquityChart({ cmp }: { cmp: ResearchComparison }) {
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!ref.current) return;
    const chart = echarts.init(ref.current);
    const series = cmp.families
      .filter((f) => f.sharedOosEquity && f.sharedOosEquity.length > 0)
      .map((f, i) => ({
        type: "line" as const, name: f.familyName, showSymbol: false,
        lineStyle: { color: FAMILY_COLORS[i % FAMILY_COLORS.length], width: 1.5 },
        data: (f.sharedOosEquity ?? []).map((e, k) => [k, e]),
      }));
    chart.setOption({
      animation: false, grid: { top: 28, right: 16, bottom: 28, left: 64 },
      legend: { top: 0, textStyle: { color: "#8a857c", fontSize: 10 } },
      tooltip: { trigger: "axis", valueFormatter: (v: number) => fmtMoney(v) },
      xAxis: { type: "value", name: "OOS-Beobachtung", nameTextStyle: { color: "#8a857c", fontSize: 9 }, axisLine: { lineStyle: { color: "#232120" } }, axisLabel: { color: "#8a857c", fontSize: 10 } },
      yAxis: { type: "value", scale: true, name: "Kapital", nameTextStyle: { color: "#8a857c", fontSize: 9 }, splitLine: { lineStyle: { color: "#232120" } }, axisLabel: { color: "#8a857c", fontSize: 10 } },
      series,
    });
    const ro = new ResizeObserver(() => chart.resize());
    ro.observe(ref.current);
    return () => { ro.disconnect(); chart.dispose(); };
  }, [cmp]);
  return (
    <div>
      <div className="text-[11px] text-[var(--fg-dim)] mb-1">
        Verkettete Netto-OOS-Kapitalkurven auf der gemeinsamen Zeitachse, normiert auf das gemeinsame Startkapital.
      </div>
      <div ref={ref} className="h-[260px] w-full" />
    </div>
  );
}

// Kleine Formularbausteine
function Panel({ children }: { children: React.ReactNode }) {
  return <div className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3">{children}</div>;
}
function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return <label className="block"><span className="block text-[10px] uppercase tracking-wide text-[var(--fg-faint)] mb-1">{label}</span>{children}</label>;
}
function Input({ value, onChange, type = "text", className }: { value: string; onChange: (v: string) => void; type?: string; className?: string }) {
  return <input type={type} value={value} onChange={(e) => onChange(e.target.value)} className={cn("w-full h-[30px] px-2 rounded-md bg-[var(--panel-2)] border border-[var(--line-2)] text-xs text-[var(--fg)] mono focus:outline-none focus:border-[var(--key)]", className)} />;
}
function Select({ value, onChange, options, className }: { value: string; onChange: (v: string) => void; options: { value: string; label: string }[]; className?: string }) {
  return (
    <select value={value} onChange={(e) => onChange(e.target.value)} className={cn("h-[30px] px-2 rounded-md bg-[var(--panel-2)] border border-[var(--line-2)] text-xs text-[var(--fg)] focus:outline-none focus:border-[var(--key)]", className ?? "w-full")}>
      {options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
    </select>
  );
}

// ==============================================================================================
// Monte-Carlo-Fächerchart mit Animation
// ==============================================================================================

function MonteCarloBlock({ mc }: { mc: QuantMonteCarloResponse }) {
  return (
    <div className="space-y-3">
      <MonteCarloFan mc={mc} />
      <div className="grid gap-4 xl:grid-cols-3">
        {mc.finalCapital && <div><div className="text-[11px] text-[var(--fg-dim)] mb-1">Endkapital</div><DistributionChart dist={mc.finalCapital} /></div>}
        {mc.maxDrawdown && <div><div className="text-[11px] text-[var(--fg-dim)] mb-1">Maximaler Drawdown</div><DistributionChart dist={mc.maxDrawdown} /></div>}
        {mc.losingStreak && <div><div className="text-[11px] text-[var(--fg-dim)] mb-1">Längste Verlustserie</div><DistributionChart dist={mc.losingStreak} /></div>}
      </div>
      <div className="flex flex-wrap gap-x-4 gap-y-1 text-[11px] text-[var(--fg-dim)]">
        <span>Verfahren: <b className="text-[var(--fg)]">{mc.method}</b></span>
        <span>Seed: <b className="text-[var(--fg)]">{mc.seed}</b></span>
        <span>Simulationen: <b className="text-[var(--fg)]">{mc.totalPaths || mc.iterations}</b></span>
        {mc.blockLength > 0 && <span>Blocklänge: <b className="text-[var(--fg)]">{mc.blockLength}</b></span>}
        <span>Basis: <b className="text-[var(--fg)]">{mc.sourceLabel}</b></span>
        <span>Anteil unter Start: <b className="text-[var(--fg)]">{formatPercent(mc.shareOfRunsBelowStart)}</b></span>
        {mc.capitalBarrier != null && <span>Grenze verletzt: <b className="text-[var(--fg)]">{formatPercent(mc.shareOfRunsBreachingBarrier)}</b></span>}
      </div>
      <NoteBlock title="Annahmen (Szenarien, keine Wahrscheinlichkeiten)" items={[...mc.assumptions, ...mc.notes]} />
    </div>
  );
}

function MonteCarloFan({ mc }: { mc: QuantMonteCarloResponse }) {
  const ref = useRef<HTMLDivElement>(null);
  const chartRef = useRef<echarts.ECharts | null>(null);
  const [reveal, setReveal] = useState(1); // 0..1
  const [playing, setPlaying] = useState(false);
  const rafRef = useRef<number | null>(null);

  const horizon = mc.bandMedian.length > 0 ? mc.bandMedian.length : (mc.paths[0]?.length ?? 0);

  // Animationsschleife (reine Darstellung; ändert keine Ergebnisse).
  useEffect(() => {
    if (!playing) return;
    let last = performance.now();
    const step = (now: number) => {
      const dt = (now - last) / 1000; last = now;
      setReveal((r) => {
        const nr = r + dt / 2.2; // ~2,2 s Aufbau
        if (nr >= 1) { setPlaying(false); return 1; }
        return nr;
      });
      rafRef.current = requestAnimationFrame(step);
    };
    rafRef.current = requestAnimationFrame(step);
    return () => { if (rafRef.current) cancelAnimationFrame(rafRef.current); };
  }, [playing]);

  useEffect(() => {
    if (!ref.current) return;
    if (!chartRef.current) chartRef.current = echarts.init(ref.current);
    const chart = chartRef.current;
    const k = Math.max(1, Math.floor(horizon * reveal));
    const sliceX = (arr: number[]) => arr.slice(0, k).map((v, i) => [i, v]);

    const pathSeries = mc.paths.slice(0, 60).map((p) => ({
      type: "line" as const, data: sliceX(p), showSymbol: false, silent: true,
      lineStyle: { color: "#7fc4b4", width: 0.6, opacity: 0.12 }, z: 1, animation: false,
    }));
    const band = [
      { type: "line" as const, data: sliceX(mc.bandP95), showSymbol: false, lineStyle: { opacity: 0 }, z: 2, animation: false, name: "P95" },
      { type: "line" as const, data: sliceX(mc.bandP5), showSymbol: false, lineStyle: { opacity: 0 }, z: 2, animation: false, name: "P5",
        areaStyle: { color: "rgba(162,230,93,0.10)" } },
      { type: "line" as const, data: sliceX(mc.bandMedian), showSymbol: false, z: 3, animation: false,
        lineStyle: { color: "#a2e65d", width: 2 }, name: "Median" },
    ];
    const marks = {
      type: "line" as const, data: [], markLine: {
        silent: true, symbol: "none",
        data: [
          { yAxis: mc.initialCapital, lineStyle: { color: "#8a857c", type: "dashed" as const }, label: { formatter: "Start", color: "#8a857c", fontSize: 10 } },
          ...(mc.capitalBarrier != null ? [{ yAxis: mc.capitalBarrier, lineStyle: { color: "#c1503f", type: "dashed" as const }, label: { formatter: "Grenze", color: "#c1503f", fontSize: 10 } }] : []),
        ],
      }, z: 4, animation: false,
    };
    chart.setOption({
      animation: false, grid: { top: 16, right: 16, bottom: 24, left: 56 },
      tooltip: { trigger: "axis" },
      xAxis: { type: "value", min: 0, max: Math.max(1, horizon - 1), axisLine: { lineStyle: { color: "#232120" } }, axisLabel: { color: "#8a857c", fontSize: 10 }, name: "Schritt", nameTextStyle: { color: "#8a857c", fontSize: 10 } },
      yAxis: { type: "value", scale: true, splitLine: { lineStyle: { color: "#232120" } }, axisLabel: { color: "#8a857c", fontSize: 10 } },
      series: [...pathSeries, ...band, marks],
    }, true);
    const ro = new ResizeObserver(() => chart.resize());
    ro.observe(ref.current);
    return () => ro.disconnect();
  }, [mc, reveal, horizon]);

  useEffect(() => () => { chartRef.current?.dispose(); chartRef.current = null; }, []);

  return (
    <div>
      <div className="flex items-center gap-2 mb-1">
        <button onClick={() => { if (reveal >= 1) setReveal(0); setPlaying((p) => !p); }} className="h-[26px] px-3 rounded border border-[var(--line-2)] text-xs text-[var(--fg)]">
          {playing ? "❚❚ Pause" : "▶ Play"}
        </button>
        <button onClick={() => { setPlaying(false); setReveal(1); }} className="h-[26px] px-3 rounded border border-[var(--line-2)] text-xs text-[var(--fg)]">Vollständig anzeigen</button>
        <span className="text-[11px] text-[var(--fg-dim)]">
          {mc.displayedPaths > 0 ? `${Math.min(60, mc.displayedPaths)} von ${mc.totalPaths} Pfaden dargestellt · Band (P5/Median/P95) aus allen ${mc.totalPaths} Läufen` : "Keine Pfaddaten"}
        </span>
      </div>
      <div ref={ref} className="h-[320px] w-full" />
      <p className="mt-1 text-[10px] text-[var(--fg-faint)]">Das Band ist der simulierte Ergebnisbereich UNTER den Modellannahmen — kein garantierter zukünftiger Kursbereich.</p>
    </div>
  );
}

// ==============================================================================================
// Holdout: Vorbereitung + bewusste Freigabe
// ==============================================================================================

function HoldoutBlock({ run, onEvaluated, groupHoldout }: { run: ResearchRunRecord; onEvaluated: () => void; groupHoldout?: ResearchGroupHoldout | null }) {
  const p: ResearchHoldoutProposal | null = run.holdoutProposal;
  const [confirm, setConfirm] = useState(false);
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [existing, setExisting] = useState<HoldoutEvaluationRecord | null>(p?.existing?.evaluation ?? null);

  // Gruppen-Kontext: gehört dieser Lauf zu einer Vergleichs-Gruppe, läuft die finale Auswertung über die
  // GRUPPENSPERRE (ein Holdout je Gruppe). Ist die Gruppe bereits an eine ANDERE Familie gebunden, ist dieser
  // Kandidat gesperrt.
  const grouped = !!(run.campaignGroupId && run.familyKey);
  const lockedByOther = grouped && groupHoldout?.reserved === true && groupHoldout.selectedFamilyKey !== run.familyKey;

  // Live-Holdout-Status abrufen (der gespeicherte Vorschlag stammt vom Laufende und kennt eine spätere
  // bestätigte Auswertung nicht). Vorhandenes Ergebnis wird gelesen, nicht neu gerechnet.
  useEffect(() => {
    if (!run.campaignId) return;
    const ac = new AbortController();
    quantApi.holdout(run.campaignId, ac.signal).then((h) => { if (h.evaluation) setExisting(h.evaluation); }).catch(() => {});
    return () => ac.abort();
  }, [run.campaignId]);

  const evaluate = useCallback(async () => {
    if (!run.campaignId || !p?.available) return;
    setBusy(true); setErr(null);
    try {
      const req = {
        run: { ...run.config.run, params: p.parameters }, options: run.config.options,
        candidateReference: p.candidateReference, candidateTrialId: p.candidateTrialId,
        warmupBars: p.warmupBars, confirm: true,
      };
      // Grouped: über die Gruppensperre (ein Holdout je Gruppe); sonst direkt am Kampagnen-Register.
      const ok = grouped
        ? (await quantApi.evaluateGroupHoldout(run.campaignGroupId!, run.familyKey!, req))
        : (await quantApi.evaluateHoldout(run.campaignId, req));
      if (!ok.ok) { setErr(ok.error ?? "Ablehnung."); setBusy(false); return; }
      // Auf Abschluss warten, dann das fertige Ergebnis lesen (kein Neuberechnen).
      for (let i = 0; i < 60; i++) {
        const h = await quantApi.holdout(run.campaignId);
        if (h.evaluation && ["Completed", "Failed", "Cancelled"].includes(h.evaluation.status)) { setExisting(h.evaluation); break; }
        await new Promise((r) => setTimeout(r, 800));
      }
      onEvaluated();
    } catch (e) { setErr(e instanceof Error ? e.message : String(e)); } finally { setBusy(false); }
  }, [run, p, onEvaluated, grouped]);

  return (
    <Section id="holdout" title="Finaler Holdout" basis={existing ? "Finaler Holdout (ausgewertet)" : "Finaler Holdout (reserviert)"}>
      {existing ? (
        <HoldoutResult ev={existing} />
      ) : !p ? (
        <div className="text-xs text-[var(--fg-faint)]">wird vorbereitet…</div>
      ) : !p.available ? (
        <Unavailable title="Kein Holdout" reason={p.unavailableReason ?? "Kein Holdout-Kandidat."} />
      ) : lockedByOther ? (
        <Unavailable title="Für die Gruppe gesperrt"
          reason={`Der EINE finale Holdout dieser Vergleichs-Gruppe ist bereits für Familie „${groupHoldout?.selectedFamilyKey}" ${groupHoldout?.consumed ? "ausgewertet" : "reserviert"}. Ein Holdout je Gruppe — keine zweite finale Auswertung, kein Kandidatenwechsel.`} />
      ) : (
        <div className="space-y-2 text-xs">
          <div className="rounded-lg border border-[var(--line)] bg-[var(--panel-2)] p-3 space-y-1">
            <div className="text-[var(--fg)]">Vorgeschlagener Kandidat: <span className="mono">{Object.entries(p.parameters).map(([k, v]) => `${k}=${v}`).join(", ")}</span></div>
            <div className="text-[var(--fg-dim)]">{p.reason}</div>
            <div className="text-[var(--fg-dim)]">Reservierter Zeitraum: {p.holdoutFrom ? formatDate(new Date(p.holdoutFrom).getTime()) : "—"} … {p.holdoutTo ? formatDate(new Date(p.holdoutTo).getTime()) : "—"}</div>
            {grouped && <div className="text-[var(--fg-dim)]">Gruppensperre: die finale Auswertung bindet den EINEN Holdout der GESAMTEN Gruppe an diese Familie — danach ist keine andere Familie mehr auswertbar.</div>}
            <div className="text-[var(--gold)]">Die finale Auswertung verbraucht diesen Zeitraum unwiderruflich (einmalig, eingefroren).</div>
          </div>
          {err && <div className="text-[var(--red)]">{err}</div>}
          <label className="flex items-center gap-2"><input type="checkbox" checked={confirm} onChange={(e) => setConfirm(e.target.checked)} /> Ich bestätige die einmalige Auswertung des finalen Holdouts{grouped ? " für die gesamte Gruppe" : ""}.</label>
          <button disabled={!confirm || busy} onClick={evaluate} className={cn("h-[30px] px-4 rounded-md text-xs font-semibold", confirm && !busy ? "bg-[var(--gold)] text-black" : "bg-[var(--panel-3)] text-[var(--fg-faint)]")}>
            {busy ? "Wertet aus…" : "Finalen Holdout auswerten – verbraucht diesen Zeitraum"}
          </button>
        </div>
      )}
    </Section>
  );
}

function HoldoutResult({ ev }: { ev: HoldoutEvaluationRecord }) {
  const equity: EquityPoint[] = ev.equity.map((p) => ({ barIndex: p.barIndex, time: new Date(p.timeMs).toISOString(), realizedNetPnL: p.equity, equity: p.equity, openPnL: p.totalEquity - p.equity }));
  return (
    <div className="space-y-3 text-xs">
      <div className="flex flex-wrap divide-x divide-[var(--line)] rounded-lg border border-[var(--line)]">
        <HeadlineStat label="Status" value={ev.status} />
        <HeadlineStat label="Netto-PnL" value={formatNumber(ev.netProfit)} />
        <HeadlineStat label="End-Equity" value={formatNumber(ev.finalEquity)} />
        <HeadlineStat label="Max. Drawdown (abs.)" value={formatNumber(ev.maxDrawdown)} />
        <HeadlineStat label="Trades" value={String(ev.tradeCount)} />
      </div>
      {equity.length > 0 && <EquityDrawdownChart equity={equity} initialBalance={ev.config.initialCapital} height={220} />}
      <NoteBlock title="Hinweise" items={ev.notes} />
      {ev.trades.length > 0 && (
        <details><summary className="cursor-pointer text-[var(--key)] text-[11px]">Trade-Journal ({ev.trades.length})</summary>
          <div className="overflow-x-auto mt-2 text-[11px] mono">
            <table className="w-full"><thead><tr className="text-[var(--fg-faint)] text-left"><th className="pr-3 py-1">#</th><th className="pr-3">Seite</th><th className="pr-3">NetPnL</th><th className="pr-3">Exit</th></tr></thead>
              <tbody>{ev.trades.map((t) => <tr key={t.index} className="border-t border-[var(--line)]"><td className="pr-3 py-1">{t.index + 1}</td><td className="pr-3">{t.side}</td><td className="pr-3">{formatNumber(t.netPnL)}</td><td className="pr-3 text-[var(--fg-dim)]">{t.exitReason}</td></tr>)}</tbody>
            </table>
          </div>
        </details>
      )}
    </div>
  );
}
