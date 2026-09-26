import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { cn } from "@/lib/utils";
import { backtestApi, type DataSourceDef, type InstrumentDef, type RunRequest } from "@/lib/backtestApi";
import {
  awaitJob,
  defaultEvaluationOptions,
  formatDate,
  formatNumber,
  formatPercent,
  quantApi,
  type CampaignInput,
  type QuantAnalyzeResponse,
  type QuantCampaign,
  type QuantEvaluationOptions,
  type QuantMonteCarloResponse,
  type QuantOverfittingResponse,
  type QuantRobustnessResponse,
  type QuantStatus,
  type QuantTrial,
  type QuantWalkForwardRequest,
  type QuantWalkForwardResponse,
} from "@/lib/quantApi";
import { HeadlineStat, MetricList, NoteBlock, Unavailable } from "@/components/research/MetricList";
import {
  CostHeatmap,
  DistributionChart,
  EquityDrawdownPanel,
  MonthlyReturnsTable,
  PboScatter,
  RollingChart,
  WalkForwardTimeline,
} from "@/components/research/ResearchCharts";

type TabId = "overview" | "benchmark" | "walkforward" | "montecarlo" | "robustness" | "overfitting" | "experiments";

const TABS: { id: TabId; label: string }[] = [
  { id: "overview", label: "Übersicht" },
  { id: "benchmark", label: "Benchmark" },
  { id: "walkforward", label: "Walk-forward" },
  { id: "montecarlo", label: "Monte Carlo" },
  { id: "robustness", label: "Robustheit" },
  { id: "overfitting", label: "Overfitting" },
  { id: "experiments", label: "Experimente" },
];

interface DataConfig {
  dataSourceId: string;
  symbol: string;
  timeframeMinutes: number;
  fromUtc: string;
  toUtc: string;
  maxRows: number;
  strategy: string;
  params: Record<string, string>;
  quantity: number;
  initialBalance: number;
}

const toInput = (iso: string) => iso.replace("Z", "").slice(0, 16);

export function Research() {
  const [tab, setTab] = useState<TabId>("overview");
  const [status, setStatus] = useState<QuantStatus | null>(null);
  const [sources, setSources] = useState<DataSourceDef[]>([]);
  const [instruments, setInstruments] = useState<InstrumentDef[]>([]);
  const [cfg, setCfg] = useState<DataConfig>({
    dataSourceId: "",
    symbol: "MES",
    timeframeMinutes: 5,
    fromUtc: "",
    toUtc: "",
    maxRows: 1_500_000,
    strategy: "movingaverage",
    params: { FastPeriod: "9", SlowPeriod: "21" },
    quantity: 1,
    initialBalance: 10_000,
  });
  const [evalOpts, setEvalOpts] = useState<QuantEvaluationOptions>(defaultEvaluationOptions);
  const [benchmarkId, setBenchmarkId] = useState<string>("");
  const [fullyFunded, setFullyFunded] = useState(false);
  const [showSettings, setShowSettings] = useState(false);

  const [analysis, setAnalysis] = useState<QuantAnalyzeResponse | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [progress, setProgress] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const abortRef = useRef<AbortController | null>(null);
  const jobRef = useRef<string | null>(null);

  useEffect(() => {
    const ac = new AbortController();
    (async () => {
      try {
        const [st, src, inst] = await Promise.all([
          quantApi.status(ac.signal),
          backtestApi.dataSources(),
          backtestApi.instruments(),
        ]);
        setStatus(st);
        setSources(src);
        setInstruments(inst);
        const first = src.find((s) => s.available) ?? src[0];
        if (first) {
          setCfg((c) => ({
            ...c,
            dataSourceId: first.id,
            fromUtc: first.defaultFromUtc ? toInput(first.defaultFromUtc) : c.fromUtc,
            maxRows: first.defaultMaxRows ?? c.maxRows,
            symbol: inst[0]?.symbol ?? c.symbol,
          }));
        }
      } catch (e) {
        if (!ac.signal.aborted) setError(e instanceof Error ? e.message : String(e));
      }
    })();
    return () => ac.abort();
  }, []);

  const runRequest = useCallback((): RunRequest => ({
    dataSourceId: cfg.dataSourceId,
    path: null,
    symbol: cfg.symbol,
    timeframeMinutes: cfg.timeframeMinutes,
    fromUtc: cfg.fromUtc.trim() ? `${cfg.fromUtc}:00Z` : null,
    toUtc: cfg.toUtc.trim() ? `${cfg.toUtc}:00Z` : null,
    maxRows: cfg.maxRows,
    strategy: cfg.strategy,
    params: cfg.params,
    quantity: cfg.quantity,
    initialBalance: cfg.initialBalance,
    stopLossTicks: null,
    takeProfitTicks: null,
    slippageTicks: null,
    feePerSideOverride: null,
    applyFees: true,
    excludePartialEdges: true,
  }), [cfg]);

  const runAnalysis = useCallback(async () => {
    abortRef.current?.abort();
    const ac = new AbortController();
    abortRef.current = ac;
    setBusy("Auswertung läuft…");
    setError(null);
    setProgress(0);
    try {
      const res = await quantApi.analyze(runRequest(), evalOpts, benchmarkId || null, fullyFunded, ac.signal);
      setAnalysis(res);
      if (!res.ok) setError(res.error ?? "Auswertung fehlgeschlagen.");
    } catch (e) {
      if (!ac.signal.aborted) setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(null);
    }
  }, [runRequest, evalOpts, benchmarkId, fullyFunded]);

  /** Startet einen Hintergrund-Job und wartet mit Fortschrittsanzeige auf sein Ergebnis. */
  const runJob = useCallback(async <T,>(label: string, start: () => Promise<{ jobId: string }>): Promise<T | null> => {
    abortRef.current?.abort();
    const ac = new AbortController();
    abortRef.current = ac;
    setBusy(label);
    setError(null);
    setProgress(0);
    try {
      const { jobId } = await start();
      jobRef.current = jobId;
      const result = await awaitJob<T>(jobId, (p) => setProgress(p), ac.signal);
      return result;
    } catch (e) {
      if (!ac.signal.aborted) setError(e instanceof Error ? e.message : String(e));
      return null;
    } finally {
      jobRef.current = null;
      setBusy(null);
    }
  }, []);

  const cancel = useCallback(() => {
    if (jobRef.current) void quantApi.cancelJob(jobRef.current);
    abortRef.current?.abort();
    setBusy(null);
  }, []);

  const instrument = useMemo(() => instruments.find((i) => i.symbol === cfg.symbol), [instruments, cfg.symbol]);

  return (
    <div className="flex flex-col h-full min-h-0">
      {/* Kopfzeile */}
      <div className="flex-shrink-0 border-b border-[var(--line)] bg-[var(--bg-2)]">
        <div className="flex flex-wrap items-center gap-2 px-4 py-2">
          <span className="text-sm font-medium text-[var(--fg)]">Quant-Research</span>
          <span className="text-[11px] text-[var(--fg-faint)]">
            Reproduzierbare Prüfung auf der OHLC-Engine · Simulation, keine Orders
          </span>

          <div className="ml-auto flex items-center gap-2">
            <Select
              className="w-44"
              value={cfg.dataSourceId}
              onChange={(v) => {
                const src = sources.find((s) => s.id === v);
                setCfg((c) => ({
                  ...c,
                  dataSourceId: v,
                  fromUtc: src?.defaultFromUtc ? toInput(src.defaultFromUtc) : c.fromUtc,
                  maxRows: src?.defaultMaxRows ?? c.maxRows,
                }));
              }}
              options={sources.map((s) => ({ value: s.id, label: s.label + (s.available ? "" : " (fehlt)") }))}
            />
            <Select
              className="w-[92px]"
              value={cfg.symbol}
              onChange={(v) => setCfg((c) => ({ ...c, symbol: v }))}
              options={instruments.map((i) => ({ value: i.symbol, label: i.symbol }))}
            />
            <Select
              className="w-[76px]"
              value={String(cfg.timeframeMinutes)}
              onChange={(v) => setCfg((c) => ({ ...c, timeframeMinutes: parseInt(v) }))}
              options={[1, 3, 5, 15, 30, 60].map((m) => ({ value: String(m), label: `${m} min` }))}
            />
            <Select
              className="w-[104px]"
              value={evalOpts.frequency}
              onChange={(v) => setEvalOpts((o) => ({ ...o, frequency: v as QuantEvaluationOptions["frequency"] }))}
              options={[
                { value: "Bar", label: "je Bar" },
                { value: "Daily", label: "täglich" },
                { value: "Weekly", label: "wöchentlich" },
                { value: "Monthly", label: "monatlich" },
              ]}
            />
            <button
              onClick={() => setShowSettings((s) => !s)}
              className="px-2.5 py-1.5 text-xs rounded-md border border-[var(--line-2)] text-[var(--fg-dim)] hover:text-[var(--fg)]"
            >
              Einstellungen
            </button>
            <button
              onClick={() => void runAnalysis()}
              disabled={!!busy || !cfg.dataSourceId}
              className="px-3 py-1.5 text-xs rounded-md bg-[var(--key)] text-black font-medium disabled:opacity-40"
            >
              Auswerten
            </button>
            {busy && (
              <button onClick={cancel} className="px-2.5 py-1.5 text-xs rounded-md border border-[var(--red)] text-[var(--red)]">
                Abbrechen
              </button>
            )}
          </div>
        </div>

        {showSettings && (
          <div className="grid gap-3 px-4 pb-3 sm:grid-cols-2 lg:grid-cols-4 xl:grid-cols-6 border-t border-[var(--line)] pt-3">
            <Field label="Von (UTC)">
              <Input type="datetime-local" value={cfg.fromUtc} onChange={(v) => setCfg((c) => ({ ...c, fromUtc: v }))} />
            </Field>
            <Field label="Bis (UTC)">
              <Input type="datetime-local" value={cfg.toUtc} onChange={(v) => setCfg((c) => ({ ...c, toUtc: v }))} />
            </Field>
            <Field label="Max. Sierra-Zeilen">
              <Input type="number" value={String(cfg.maxRows)} onChange={(v) => setCfg((c) => ({ ...c, maxRows: parseInt(v) || 100000 }))} />
            </Field>
            <Field label="Startkapital">
              <Input type="number" value={String(cfg.initialBalance)} onChange={(v) => setCfg((c) => ({ ...c, initialBalance: parseFloat(v) || 10000 }))} />
            </Field>
            <Field label="Schneller SMA">
              <Input value={cfg.params.FastPeriod ?? ""} onChange={(v) => setCfg((c) => ({ ...c, params: { ...c.params, FastPeriod: v } }))} />
            </Field>
            <Field label="Langsamer SMA">
              <Input value={cfg.params.SlowPeriod ?? ""} onChange={(v) => setCfg((c) => ({ ...c, params: { ...c.params, SlowPeriod: v } }))} />
            </Field>
            <Field label="Risikofreier Zins p. a.">
              <Input
                type="number"
                value={String(evalOpts.riskFreeAnnualRate)}
                onChange={(v) => setEvalOpts((o) => ({ ...o, riskFreeAnnualRate: parseFloat(v) || 0 }))}
              />
            </Field>
            <Field label="Annualisierung">
              <Select
                value={evalOpts.annualizationBasis}
                onChange={(v) => setEvalOpts((o) => ({ ...o, annualizationBasis: v as "Observed" | "Fixed" }))}
                options={[
                  { value: "Observed", label: "empirisch (aus den Daten)" },
                  { value: "Fixed", label: "fest vorgegeben" },
                ]}
              />
            </Field>
            {evalOpts.annualizationBasis === "Fixed" && (
              <Field label="Perioden pro Jahr">
                <Input
                  type="number"
                  value={String(evalOpts.fixedPeriodsPerYear ?? 252)}
                  onChange={(v) => setEvalOpts((o) => ({ ...o, fixedPeriodsPerYear: parseFloat(v) || 252 }))}
                />
              </Field>
            )}
            <Field label="Expected Shortfall α">
              <Input
                type="number"
                value={String(evalOpts.expectedShortfallAlpha)}
                onChange={(v) => setEvalOpts((o) => ({ ...o, expectedShortfallAlpha: parseFloat(v) || 0.05 }))}
              />
            </Field>
            <Field label="Rollierendes Fenster">
              <Input
                type="number"
                value={String(evalOpts.rollingWindow)}
                onChange={(v) => setEvalOpts((o) => ({ ...o, rollingWindow: parseInt(v) || 20 }))}
              />
            </Field>
            <Field label="Benchmark">
              <Select
                value={benchmarkId}
                onChange={setBenchmarkId}
                options={[{ value: "", label: "kein Vergleich" }, ...(status?.benchmarks ?? []).map((b) => ({ value: b, label: b }))]}
              />
            </Field>
            <Field label="Kapitalbasis bestätigen">
              <label className="flex items-center gap-2 text-[11px] text-[var(--fg-dim)] h-[30px]">
                <input type="checkbox" checked={fullyFunded} onChange={(e) => setFullyFunded(e.target.checked)} />
                voll finanziertes Konto
              </label>
            </Field>
          </div>
        )}

        {/* Tabs */}
        <div className="flex items-center gap-1 px-3 border-t border-[var(--line)]">
          {TABS.map((t) => (
            <button
              key={t.id}
              onClick={() => setTab(t.id)}
              className={cn(
                "px-3 py-2 text-xs border-b-2 -mb-px transition-colors",
                tab === t.id
                  ? "border-[var(--key)] text-[var(--key)]"
                  : "border-transparent text-[var(--fg-faint)] hover:text-[var(--fg-dim)]"
              )}
            >
              {t.label}
            </button>
          ))}
          {busy && (
            <span className="ml-auto text-[11px] text-[var(--fg-faint)] mono pr-2">
              {busy} {progress > 0 ? `${(progress * 100).toFixed(0)} %` : ""}
            </span>
          )}
        </div>
      </div>

      {error && (
        <div className="flex-shrink-0 mx-4 mt-3 rounded-md border border-[var(--red)]/50 bg-[var(--red)]/10 px-3 py-2 text-xs text-[var(--red)]">
          {error}
        </div>
      )}

      <div className="flex-1 min-h-0 overflow-y-auto p-4 space-y-4">
        {tab === "overview" && <OverviewTab analysis={analysis} instrument={instrument} />}
        {tab === "benchmark" && <BenchmarkTab analysis={analysis} status={status} />}
        {tab === "walkforward" && (
          <WalkForwardTab cfg={cfg} evalOpts={evalOpts} runRequest={runRequest} runJob={runJob} busy={busy} />
        )}
        {tab === "montecarlo" && (
          <MonteCarloTab evalOpts={evalOpts} runRequest={runRequest} runJob={runJob} busy={busy} status={status} />
        )}
        {tab === "robustness" && <RobustnessTab evalOpts={evalOpts} runRequest={runRequest} runJob={runJob} busy={busy} />}
        {tab === "overfitting" && (
          <OverfittingTab cfg={cfg} evalOpts={evalOpts} runRequest={runRequest} runJob={runJob} busy={busy} />
        )}
        {tab === "experiments" && <ExperimentsTab />}
      </div>
    </div>
  );
}

// ==============================================================================================
// Übersicht
// ==============================================================================================

function OverviewTab({ analysis, instrument }: { analysis: QuantAnalyzeResponse | null; instrument?: InstrumentDef }) {
  if (!analysis)
    return (
      <Unavailable
        title="Noch keine Auswertung"
        reason={'Datenquelle wählen und „Auswerten“ drücken. Es werden ausschließlich echte Backend-Ergebnisse angezeigt.'}
      />
    );
  if (!analysis.ok) return <Unavailable title="Auswertung nicht möglich" reason={analysis.error} />;

  const totalReturn = analysis.finalEquityTotal / analysis.initialBalance - 1;

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap divide-x divide-[var(--line)] rounded-lg border border-[var(--line)] bg-[var(--panel)]">
        <HeadlineStat label="Trades" value={String(analysis.trades)} />
        <HeadlineStat
          label="Gesamtrendite"
          value={formatPercent(totalReturn, 2)}
          tone={totalReturn >= 0 ? "good" : "bad"}
          hint="Auf Basis des Gesamtkapitals (inkl. Mark-to-Market am Ende)."
        />
        <HeadlineStat label="Endkapital realisiert" value={analysis.finalEquityRealized.toFixed(2)} />
        <HeadlineStat label="Endkapital gesamt" value={analysis.finalEquityTotal.toFixed(2)} />
        <HeadlineStat
          label="Max. Drawdown (gesamt)"
          value={formatPercent(analysis.drawdownTotal?.fraction ?? null, 2)}
          tone="bad"
        />
        <HeadlineStat
          label="Perioden/Jahr"
          value={formatNumber(analysis.periodsPerYear, 1)}
          hint={analysis.annualizationNote}
        />
        <HeadlineStat label="Frequenz" value={analysis.frequency} />
      </div>

      <div className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3">
        <div className="text-xs font-medium mb-2">Kapitalkurve und Drawdown</div>
        <EquityDrawdownPanel curve={analysis.curve} />
        <div className="text-[11px] text-[var(--fg-faint)] mt-2 space-y-0.5">
          <div>
            <span className="text-[var(--cyan)]">Realisiert</span> = nur abgeschlossene Trades.{" "}
            <span className="text-[var(--key)]">Gesamt</span> = zusätzlich Mark-to-Market offener Positionen.
          </div>
          <div>{analysis.markToMarketNote}</div>
          <div>{analysis.annualizationNote}</div>
          <div>{analysis.riskFreeNote}</div>
        </div>
      </div>

      <div className="grid gap-4 xl:grid-cols-2">
        <div className="rounded-lg border border-[var(--line)] bg-[var(--panel)] overflow-hidden">
          <MetricList
            metrics={analysis.metricsTotal}
            title="Kennzahlen — Kapitalbasis GESAMT"
            subtitle="Inklusive offener Positionen. Zum Aufklappen auf eine Kennzahl klicken."
            columns={1}
          />
        </div>
        <div className="rounded-lg border border-[var(--line)] bg-[var(--panel)] overflow-hidden">
          <MetricList
            metrics={analysis.metricsRealized}
            title="Kennzahlen — Kapitalbasis REALISIERT"
            subtitle="Nur abgeschlossene Trades. Bewusst getrennt ausgewiesen."
            columns={1}
          />
        </div>
      </div>

      <div className="rounded-lg border border-[var(--line)] bg-[var(--panel)] overflow-hidden">
        <MetricList metrics={analysis.activity} title="Handelsaktivität" columns={2} />
      </div>

      <div className="grid gap-4 xl:grid-cols-2">
        <div className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3">
          <div className="text-xs font-medium mb-2">Monatsrenditen</div>
          <MonthlyReturnsTable monthly={analysis.monthly} />
        </div>
        <div className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3">
          <div className="text-xs font-medium mb-2">Rollierende Kennzahlen</div>
          {analysis.rolling.length > 0 ? (
            <RollingChart points={analysis.rolling} window={analysis.rollingWindow} />
          ) : (
            <Unavailable title="Rollierende Kennzahlen nicht berechenbar" reason={`Weniger Perioden als das Fenster (${analysis.rollingWindow}).`} />
          )}
        </div>
      </div>

      <div className="grid gap-4 xl:grid-cols-2">
        <DataQualityPanel analysis={analysis} />
        <div className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3 space-y-2">
          <div className="text-xs font-medium">Kosten und Kontrakt</div>
          {analysis.costs ? (
            <dl className="text-[11px] grid grid-cols-2 gap-x-4 gap-y-1 mono">
              <Row k="Tick-Größe" v={`${analysis.costs.tickSize}`} />
              <Row k="Tick-Wert" v={`${analysis.costs.tickValue} ${analysis.costs.currency}`} />
              <Row k="Punktwert" v={`${analysis.costs.pointValue} ${analysis.costs.currency}`} />
              <Row k="Gebühr je Seite" v={`${analysis.costs.feePerSide.toFixed(2)} ${analysis.costs.currency}`} />
              <Row k="Round Turn" v={`${analysis.costs.feeRoundTrip.toFixed(2)} ${analysis.costs.currency}`} />
              <Row k="Slippage" v={`${analysis.costs.slippageTicks} Ticks = ${analysis.costs.slippagePerSideDollars.toFixed(2)} ${analysis.costs.currency}`} />
              <Row k="Instrument" v={instrument?.symbol ?? analysis.symbol} />
              <Row k="Quelle" v={analysis.source} />
            </dl>
          ) : (
            <div className="text-[11px] text-[var(--fg-faint)]">Keine Kostenangaben verfügbar.</div>
          )}
          {(analysis.costs?.instrumentIsExample || analysis.costs?.feeIsExample) && (
            <NoteBlock
              tone="warn"
              title="Beispielprofile"
              items={[
                "Die verwendeten Instrument-/Kostenwerte stammen aus Beispielprofilen (*.example.json), nicht aus einer echten Brokerabrechnung. Alle Netto-Kennzahlen sind damit vorläufig.",
              ]}
            />
          )}
        </div>
      </div>

      <NoteBlock title="Hinweise zur Auswertung" items={analysis.notes} tone="method" />
    </div>
  );
}

function Row({ k, v }: { k: string; v: string }) {
  return (
    <>
      <dt className="text-[var(--fg-faint)]">{k}</dt>
      <dd className="text-[var(--fg-dim)] text-right">{v}</dd>
    </>
  );
}

function DataQualityPanel({ analysis }: { analysis: QuantAnalyzeResponse }) {
  const q = analysis.dataQuality;
  if (!q) return null;
  return (
    <div className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3 space-y-2">
      <div className="text-xs font-medium">Datenqualität</div>
      <dl className="text-[11px] grid grid-cols-2 gap-x-4 gap-y-1 mono">
        <Row k="Bars" v={String(q.barCount)} />
        <Row k="Zeitzone" v={q.timezone} />
        <Row k="Von" v={formatDate(q.firstT)} />
        <Row k="Bis" v={formatDate(q.lastT)} />
        <Row k="Beobachtete Tage" v={String(q.observedDays)} />
        <Row k="Bars/Tag (Median)" v={q.medianBarsPerDay.toFixed(0)} />
      </dl>
      <div className="space-y-1">
        {q.issues.length === 0 && <div className="text-[11px] text-[var(--fg-faint)]">Keine Auffälligkeiten.</div>}
        {q.issues.map((i) => (
          <div
            key={i.code}
            className={cn(
              "text-[11px] rounded px-2 py-1 border",
              i.severity === "Error"
                ? "border-[var(--red)]/40 text-[var(--red)]"
                : i.severity === "Warning"
                ? "border-[var(--gold)]/40 text-[var(--gold)]"
                : "border-[var(--line-2)] text-[var(--fg-faint)]"
            )}
          >
            <span className="mono">{i.code}</span>
            {i.count > 0 && <span className="mono"> ×{i.count}</span>} — {i.message}
            {i.examples.length > 0 && (
              <span className="text-[var(--fg-faint)]"> (z. B. {i.examples.map(formatDate).join(", ")})</span>
            )}
          </div>
        ))}
      </div>
      <div className="text-[10px] text-[var(--fg-faint)]">
        Lücken werden gemeldet, aber nicht gefüllt. Fehlende Marktdaten werden nicht erzeugt.
      </div>
    </div>
  );
}

// ==============================================================================================
// Benchmark
// ==============================================================================================

function BenchmarkTab({ analysis, status }: { analysis: QuantAnalyzeResponse | null; status: QuantStatus | null }) {
  if (!analysis?.ok)
    return <Unavailable title="Noch keine Auswertung" reason={'Zuerst auf „Auswerten“ drücken.'} />;

  const b = analysis.benchmark;
  if (!b)
    return (
      <div className="space-y-3">
        <Unavailable
          title="Kein Benchmark gewählt"
          reason="In den Einstellungen eine Benchmarkreihe auswählen. Ohne echte Daten wird ausdrücklich keine Vergleichskurve erzeugt."
        />
        <NoteBlock
          tone="method"
          title="Benchmarkdaten bereitstellen"
          items={[
            `Ablage: ${status?.benchmarkDirectory ?? "data/benchmarks"} (eine CSV je Reihe, Spalten date,close).`,
            "Langfristiger Vergleichsmaßstab ist der S&P 500 TOTAL RETURN (Dividenden reinvestiert) — eine reine Kursreihe verzerrt den Vergleich zugunsten der Strategie.",
            "Gefundene Reihen: " + ((status?.benchmarks ?? []).join(", ") || "keine"),
          ]}
        />
      </div>
    );

  if (!b.available) return <Unavailable title={`Vergleich mit „${b.name}" nicht möglich`} reason={b.unavailableReason} />;

  return (
    <div className="space-y-4">
      <div className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3">
        <div className="flex items-baseline justify-between">
          <div className="text-xs font-medium">Indexierter Verlauf — Strategie gegen {b.name}</div>
          <div className="text-[11px] text-[var(--fg-faint)] mono">{b.commonPeriods} gemeinsame Perioden</div>
        </div>
        <EquityDrawdownPanel
          curve={analysis.curve}
          benchmark={{ t: b.t, strategyIndex: b.strategyIndex, benchmarkIndex: b.benchmarkIndex, name: b.name }}
        />
      </div>

      <div className="rounded-lg border border-[var(--line)] bg-[var(--panel)] overflow-hidden">
        <MetricList metrics={b.metrics} title="Vergleichskennzahlen" subtitle={b.provenance} columns={2} />
      </div>

      <div className="grid gap-3 lg:grid-cols-2">
        <NoteBlock title="Annahmen" items={b.assumptions} tone="method" />
        <NoteBlock title="Warnungen" items={b.warnings} tone="warn" />
      </div>
    </div>
  );
}

// ==============================================================================================
// Walk-forward
// ==============================================================================================

interface JobRunner {
  <T>(label: string, start: () => Promise<{ jobId: string }>): Promise<T | null>;
}

function buildCandidates(fast: string, slow: string): Record<string, string>[] {
  const fasts = parseList(fast);
  const slows = parseList(slow);
  const out: Record<string, string>[] = [];
  for (const f of fasts)
    for (const s of slows)
      if (f < s) out.push({ FastPeriod: String(f), SlowPeriod: String(s) });
  return out;
}

function parseList(s: string): number[] {
  return s
    .split(",")
    .map((x) => parseInt(x.trim(), 10))
    .filter((x) => Number.isFinite(x) && x > 0);
}

function WalkForwardTab({
  cfg,
  evalOpts,
  runRequest,
  runJob,
  busy,
}: {
  cfg: DataConfig;
  evalOpts: QuantEvaluationOptions;
  runRequest: () => RunRequest;
  runJob: JobRunner;
  busy: string | null;
}) {
  const [mode, setMode] = useState<"Rolling" | "Anchored">("Rolling");
  const [trainBars, setTrainBars] = useState(2000);
  const [testBars, setTestBars] = useState(500);
  const [labelSpan, setLabelSpan] = useState(50);
  const [embargo, setEmbargo] = useState(20);
  const [warmup, setWarmup] = useState(50);
  const [holdout, setHoldout] = useState(0.2);
  const [fastList, setFastList] = useState("5,9,13");
  const [slowList, setSlowList] = useState("21,34,55");
  const [selectionMetric, setSelectionMetric] = useState("sharpe");
  const [campaignId, setCampaignId] = useState("wf-" + new Date().toISOString().slice(0, 10));
  const [hypothesis, setHypothesis] = useState("SMA-Crossover als Referenz — keine Edge-Behauptung.");
  const [result, setResult] = useState<QuantWalkForwardResponse | null>(null);

  const candidates = useMemo(() => buildCandidates(fastList, slowList), [fastList, slowList]);

  const start = async () => {
    const campaign: CampaignInput = {
      id: campaignId,
      name: campaignId,
      hypothesis,
      searchSpace: `FastPeriod ∈ {${fastList}} × SlowPeriod ∈ {${slowList}}, nur Fast < Slow → ${candidates.length} Kandidaten`,
      selectionMetric,
      trialBudget: candidates.length,
      holdoutFraction: holdout,
    };
    const req: QuantWalkForwardRequest = {
      run: runRequest(),
      options: evalOpts,
      mode,
      trainBars,
      testBars,
      stepBars: null,
      labelSpanBars: labelSpan,
      embargoBars: embargo,
      warmupBars: warmup,
      holdoutFraction: holdout,
      candidates,
      selectionMetric,
      campaign,
    };
    const res = await runJob<QuantWalkForwardResponse>("Walk-forward läuft…", () => quantApi.startWalkForward(req));
    if (res) setResult(res);
  };

  return (
    <div className="space-y-4">
      <div className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3 space-y-3">
        <div className="text-xs font-medium">Aufteilung (chronologisch — kein Zufallssplit)</div>
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4 xl:grid-cols-6">
          <Field label="Modus">
            <Select
              value={mode}
              onChange={(v) => setMode(v as "Rolling" | "Anchored")}
              options={[
                { value: "Rolling", label: "rollend" },
                { value: "Anchored", label: "verankert" },
              ]}
            />
          </Field>
          <Field label="Training (Bars)"><Input type="number" value={String(trainBars)} onChange={(v) => setTrainBars(parseInt(v) || 100)} /></Field>
          <Field label="Test (Bars)"><Input type="number" value={String(testBars)} onChange={(v) => setTestBars(parseInt(v) || 50)} /></Field>
          <Field label="Labelspanne (Purging)"><Input type="number" value={String(labelSpan)} onChange={(v) => setLabelSpan(parseInt(v) || 0)} /></Field>
          <Field label="Embargo (Bars)"><Input type="number" value={String(embargo)} onChange={(v) => setEmbargo(parseInt(v) || 0)} /></Field>
          <Field label="Warmup (Bars)"><Input type="number" value={String(warmup)} onChange={(v) => setWarmup(parseInt(v) || 0)} /></Field>
          <Field label="Holdout-Anteil"><Input type="number" value={String(holdout)} onChange={(v) => setHoldout(parseFloat(v) || 0)} /></Field>
          <Field label="Auswahlmetrik">
            <Select
              value={selectionMetric}
              onChange={setSelectionMetric}
              options={["sharpe", "sortino", "calmar", "cagr", "netprofit"].map((m) => ({ value: m, label: m }))}
            />
          </Field>
          <Field label="Schnelle SMA (Liste)"><Input value={fastList} onChange={setFastList} /></Field>
          <Field label="Langsame SMA (Liste)"><Input value={slowList} onChange={setSlowList} /></Field>
          <Field label="Kampagnen-Id"><Input value={campaignId} onChange={setCampaignId} /></Field>
          <Field label="Hypothese"><Input value={hypothesis} onChange={setHypothesis} /></Field>
        </div>
        <div className="flex items-center gap-3">
          <button
            onClick={() => void start()}
            disabled={!!busy || candidates.length === 0 || !cfg.dataSourceId}
            className="px-3 py-1.5 text-xs rounded-md bg-[var(--key)] text-black font-medium disabled:opacity-40"
          >
            Walk-forward starten
          </button>
          <span className="text-[11px] text-[var(--fg-faint)]">
            {candidates.length} Kandidaten · Budget und Suchraum werden vor der Kampagne gespeichert und danach gesperrt.
          </span>
        </div>
      </div>

      {!result && <Unavailable title="Noch kein Walk-forward-Lauf" reason="Aufteilung festlegen und starten." />}

      {result && !result.ok && <Unavailable title="Walk-forward nicht möglich" reason={result.error} />}

      {result?.ok && (
        <>
          <div className="flex flex-wrap divide-x divide-[var(--line)] rounded-lg border border-[var(--line)] bg-[var(--panel)]">
            <HeadlineStat label="Fenster" value={String(result.folds.length)} />
            <HeadlineStat label="Modus" value={result.mode} />
            <HeadlineStat label="Auswahlmetrik" value={result.selectionMetric} />
            <HeadlineStat label="Versuche erfasst" value={String(result.trialsRecorded)} hint={`Kampagne ${result.campaignId}`} />
            <HeadlineStat
              label="Holdout"
              value={result.holdoutFromT ? "reserviert" : "keiner"}
              hint={result.holdoutFromT ? `${formatDate(result.holdoutFromT)} – ${formatDate(result.holdoutToT)}` : undefined}
            />
          </div>

          <div className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3 space-y-2">
            <div className="text-xs font-medium">Train/Test-Zeitachse</div>
            <WalkForwardTimeline
              folds={result.folds}
              totalBars={result.totalBars}
              holdoutFromT={result.holdoutFromT}
              holdoutToT={result.holdoutToT}
            />
          </div>

          <div className="rounded-lg border border-[var(--line)] bg-[var(--panel)] overflow-hidden">
            <MetricList
              metrics={result.oosMetrics}
              title="Out-of-Sample (verkettete Testfenster)"
              subtitle={
                "Die einzige Reihe, die eine Aussage über unbekannte Daten erlaubt. Das Kapital ist auf 1 indexiert — " +
                "absolute Geldbeträge sind hier ohne Bedeutung, relative Kennzahlen dagegen gültig. " +
                "Fenster ohne Trades liefern eine flache Kurve und damit keine definierte Kennzahl."
              }
              columns={2}
            />
          </div>

          <NoteBlock title="Hinweise" items={result.notes} tone="method" />
        </>
      )}
    </div>
  );
}

// ==============================================================================================
// Monte Carlo
// ==============================================================================================

function MonteCarloTab({
  evalOpts,
  runRequest,
  runJob,
  busy,
  status,
}: {
  evalOpts: QuantEvaluationOptions;
  runRequest: () => RunRequest;
  runJob: JobRunner;
  busy: string | null;
  status: QuantStatus | null;
}) {
  const [source, setSource] = useState<"trades" | "returns">("trades");
  const [method, setMethod] = useState("Permutation");
  const [iterations, setIterations] = useState(2000);
  const [seed, setSeed] = useState(12345);
  const [blockLength, setBlockLength] = useState<string>("");
  const [horizon, setHorizon] = useState<string>("");
  const [barrier, setBarrier] = useState<string>("");
  const [jointBenchmark, setJointBenchmark] = useState("");
  const [result, setResult] = useState<QuantMonteCarloResponse | null>(null);

  const start = async () => {
    const req = {
      run: runRequest(),
      options: evalOpts,
      source,
      method,
      iterations,
      seed,
      blockLength: blockLength ? parseInt(blockLength) : null,
      horizon: horizon ? parseInt(horizon) : null,
      capitalBarrier: barrier ? parseFloat(barrier) : null,
      jointBenchmarkId: jointBenchmark || null,
    };
    const res = await runJob<QuantMonteCarloResponse>("Monte Carlo läuft…", () => quantApi.startMonteCarlo(req));
    if (res) setResult(res);
  };

  return (
    <div className="space-y-4">
      <div className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3 space-y-3">
        <div className="text-xs font-medium">Resampling</div>
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4 xl:grid-cols-6">
          <Field label="Grundlage">
            <Select
              value={source}
              onChange={(v) => setSource(v as "trades" | "returns")}
              options={[
                { value: "trades", label: "Trade-NetPnL (additiv)" },
                { value: "returns", label: "Periodenrenditen (multiplikativ)" },
              ]}
            />
          </Field>
          <Field label="Verfahren">
            <Select
              value={method}
              onChange={setMethod}
              options={[
                { value: "Permutation", label: "Permutation (nur Reihenfolge)" },
                { value: "MovingBlock", label: "Moving-Block-Bootstrap" },
                { value: "Stationary", label: "Stationärer Bootstrap" },
              ]}
            />
          </Field>
          <Field label="Wiederholungen"><Input type="number" value={String(iterations)} onChange={(v) => setIterations(parseInt(v) || 100)} /></Field>
          <Field label="Seed"><Input type="number" value={String(seed)} onChange={(v) => setSeed(parseInt(v) || 0)} /></Field>
          <Field label="Blocklänge (leer = n^⅓)"><Input value={blockLength} onChange={setBlockLength} /></Field>
          <Field label="Horizont (leer = wie Original)"><Input value={horizon} onChange={setHorizon} /></Field>
          <Field label="Kapitalgrenze (absolut)"><Input value={barrier} onChange={setBarrier} /></Field>
          <Field label="Gemeinsam mit Benchmark">
            <Select
              value={jointBenchmark}
              onChange={setJointBenchmark}
              options={[{ value: "", label: "nur Strategie" }, ...(status?.benchmarks ?? []).map((b) => ({ value: b, label: b }))]}
            />
          </Field>
        </div>
        <button
          onClick={() => void start()}
          disabled={!!busy}
          className="px-3 py-1.5 text-xs rounded-md bg-[var(--key)] text-black font-medium disabled:opacity-40"
        >
          Simulation starten
        </button>
      </div>

      {!result && <Unavailable title="Noch keine Simulation" reason="Verfahren wählen und starten." />}
      {result && !result.ok && <Unavailable title="Simulation nicht möglich" reason={result.error} />}

      {result?.ok && (
        <>
          <div className="flex flex-wrap divide-x divide-[var(--line)] rounded-lg border border-[var(--line)] bg-[var(--panel)]">
            <HeadlineStat label="Läufe" value={String(result.iterations)} />
            <HeadlineStat label="Beobachtungen" value={String(result.observations)} />
            <HeadlineStat label="Blocklänge" value={String(result.blockLength)} />
            <HeadlineStat label="Horizont" value={String(result.horizon)} />
            <HeadlineStat label="Seed" value={String(result.seed)} hint="Gleicher Seed ⇒ exakt gleiches Ergebnis." />
            <HeadlineStat
              label="Läufe unter Startkapital"
              value={formatPercent(result.shareOfRunsBelowStart, 1)}
              tone="bad"
              hint="Anteil der simulierten Läufe — keine Wahrscheinlichkeitsaussage."
            />
            {result.shareOfRunsBreachingBarrier !== null && (
              <HeadlineStat
                label={`Läufe unter ${result.capitalBarrier}`}
                value={formatPercent(result.shareOfRunsBreachingBarrier, 1)}
                tone="bad"
                hint="Gilt ausschließlich für diese Grenze und diesen Horizont."
              />
            )}
          </div>

          <div className="grid gap-4 xl:grid-cols-3">
            {result.finalCapital && (
              <Panel title={`Endkapital — Median ${result.finalCapital.median.toFixed(0)}`}>
                <DistributionChart dist={result.finalCapital} />
                <Quantiles d={result.finalCapital} />
              </Panel>
            )}
            {result.maxDrawdown && (
              <Panel title={`Max. Drawdown — Median ${formatPercent(result.maxDrawdown.median, 1)}`}>
                <DistributionChart dist={result.maxDrawdown} />
                <Quantiles d={result.maxDrawdown} percent />
              </Panel>
            )}
            {result.losingStreak && (
              <Panel title={`Längste Verlustserie — Median ${result.losingStreak.median.toFixed(0)}`}>
                <DistributionChart dist={result.losingStreak} />
                <Quantiles d={result.losingStreak} />
              </Panel>
            )}
          </div>

          <NoteBlock title="Annahmen und Einordnung" items={result.assumptions} tone="warn" />
          <NoteBlock title="Hinweise" items={result.notes} tone="method" />
        </>
      )}
    </div>
  );
}

function Panel({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <div className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3">
      <div className="text-xs font-medium mb-2">{title}</div>
      {children}
    </div>
  );
}

function Quantiles({ d, percent }: { d: { p5: number; p25: number; median: number; p75: number; p95: number }; percent?: boolean }) {
  const f = (v: number) => (percent ? formatPercent(v, 1) : v.toFixed(2));
  return (
    <div className="grid grid-cols-5 gap-1 text-[10px] mono text-center pt-2">
      {(["p5", "p25", "median", "p75", "p95"] as const).map((k) => (
        <div key={k}>
          <div className="text-[var(--fg-faint)]">{k.toUpperCase()}</div>
          <div className="text-[var(--fg-dim)]">{f(d[k])}</div>
        </div>
      ))}
    </div>
  );
}

// ==============================================================================================
// Robustheit
// ==============================================================================================

function RobustnessTab({
  evalOpts,
  runRequest,
  runJob,
  busy,
}: {
  evalOpts: QuantEvaluationOptions;
  runRequest: () => RunRequest;
  runJob: JobRunner;
  busy: string | null;
}) {
  const [metric, setMetric] = useState("sharpe");
  const [result, setResult] = useState<QuantRobustnessResponse | null>(null);

  const start = async () => {
    const req = {
      run: runRequest(),
      options: evalOpts,
      feeMultipliers: [1, 1.5, 2, 3],
      slippageMultipliers: [1, 2, 3],
      executionDelays: [0, 1, 2, 3],
      parameterOffsets: [-4, -2, 2, 4],
      metric,
    };
    const res = await runJob<QuantRobustnessResponse>("Stresstests laufen…", () => quantApi.startRobustness(req));
    if (res) setResult(res);
  };

  const block = (d: string) => result?.blocks.find((b) => b.dimension === d);

  return (
    <div className="space-y-4">
      <div className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3 flex flex-wrap items-end gap-3">
        <Field label="Kennzahl">
          <Select value={metric} onChange={setMetric} options={["sharpe", "sortino", "calmar", "cagr", "netprofit"].map((m) => ({ value: m, label: m }))} />
        </Field>
        <button
          onClick={() => void start()}
          disabled={!!busy}
          className="px-3 py-1.5 text-xs rounded-md bg-[var(--key)] text-black font-medium disabled:opacity-40"
        >
          Stresstests starten
        </button>
        <span className="text-[11px] text-[var(--fg-faint)]">
          Gebühren/Slippage, Ausführungsverzögerung und Parameter-Nachbarschaft. Die Szenarien sagen nichts über ihre Eintrittswahrscheinlichkeit.
        </span>
      </div>

      {!result && <Unavailable title="Noch keine Stresstests" reason="Kennzahl wählen und starten." />}
      {result && !result.ok && <Unavailable title="Stresstests nicht möglich" reason={result.error} />}

      {result?.ok && (
        <>
          <div className="flex flex-wrap divide-x divide-[var(--line)] rounded-lg border border-[var(--line)] bg-[var(--panel)]">
            <HeadlineStat label="Ausgangswert" value={formatNumber(result.baseline, 3)} />
            {result.blocks.map((b) => (
              <HeadlineStat
                key={b.dimension}
                label={`Schlechtester (${dimensionLabel(b.dimension)})`}
                value={formatNumber(b.worst, 3)}
                tone={(b.worst ?? 0) >= 0 ? "neutral" : "bad"}
                hint={b.relativeDegradation !== null ? `Rückgang ${formatPercent(b.relativeDegradation, 0)}` : undefined}
              />
            ))}
          </div>

          {block("Costs") && (
            <Panel title="Gebühren- und Slippage-Stress">
              <CostHeatmap block={block("Costs")!} />
              <NoteBlock title="Hinweise" items={block("Costs")!.notes} tone="method" />
            </Panel>
          )}

          {block("ExecutionDelay") && (
            <Panel title="Ausführungsverzögerung">
              <ScenarioTable block={block("ExecutionDelay")!} />
            </Panel>
          )}

          {block("ParameterNeighborhood") && (
            <Panel title="Parameter-Nachbarschaft">
              <ScenarioTable block={block("ParameterNeighborhood")!} />
              <div className="text-[10px] text-[var(--fg-faint)] pt-1">
                Ein Ergebnis, das nur an einem einzigen Parameterpunkt funktioniert, ist ein Warnsignal.
              </div>
            </Panel>
          )}

          <NoteBlock title="Hinweise" items={result.notes} tone="method" />
        </>
      )}
    </div>
  );
}

function dimensionLabel(d: string): string {
  return d === "Costs" ? "Kosten" : d === "ExecutionDelay" ? "Verzögerung" : "Parameter";
}

function ScenarioTable({ block }: { block: { cells: { id: string; label: string; value: number | null; tradeCount: number; error?: string | null }[]; baseline: number | null } }) {
  return (
    <table className="w-full text-[11px] mono">
      <thead>
        <tr className="text-[var(--fg-faint)] border-b border-[var(--line)]">
          <th className="text-left font-normal px-2 py-1">Szenario</th>
          <th className="text-right font-normal px-2 py-1">Wert</th>
          <th className="text-right font-normal px-2 py-1">Δ zum Ausgangsfall</th>
          <th className="text-right font-normal px-2 py-1">Trades</th>
        </tr>
      </thead>
      <tbody>
        {block.cells.map((c) => {
          const delta = c.value !== null && block.baseline !== null ? c.value - block.baseline : null;
          return (
            <tr key={c.id} className="border-b border-[var(--line)]/60">
              <td className="px-2 py-1 text-[var(--fg-dim)]">{c.label}</td>
              <td className="px-2 py-1 text-right text-[var(--fg)]" title={c.error ?? undefined}>
                {c.value === null ? <span className="italic text-[var(--fg-faint)]">n. b.</span> : c.value.toFixed(3)}
              </td>
              <td className={cn("px-2 py-1 text-right", (delta ?? 0) < 0 ? "text-[var(--red)]" : "text-[var(--key)]")}>
                {delta === null ? "—" : delta.toFixed(3)}
              </td>
              <td className="px-2 py-1 text-right text-[var(--fg-faint)]">{c.tradeCount}</td>
            </tr>
          );
        })}
      </tbody>
    </table>
  );
}

// ==============================================================================================
// Overfitting (PBO / PSR / DSR)
// ==============================================================================================

function OverfittingTab({
  cfg,
  evalOpts,
  runRequest,
  runJob,
  busy,
}: {
  cfg: DataConfig;
  evalOpts: QuantEvaluationOptions;
  runRequest: () => RunRequest;
  runJob: JobRunner;
  busy: string | null;
}) {
  const [blocks, setBlocks] = useState(8);
  const [trainBars, setTrainBars] = useState(2000);
  const [testBars, setTestBars] = useState(500);
  const [fastList, setFastList] = useState("5,9,13");
  const [slowList, setSlowList] = useState("21,34,55");
  const [estimateEffective, setEstimateEffective] = useState(true);
  const [campaignId, setCampaignId] = useState("pbo-" + new Date().toISOString().slice(0, 10));
  const [result, setResult] = useState<QuantOverfittingResponse | null>(null);

  const candidates = useMemo(() => buildCandidates(fastList, slowList), [fastList, slowList]);

  const start = async () => {
    const wf: QuantWalkForwardRequest = {
      run: runRequest(),
      options: evalOpts,
      mode: "Rolling",
      trainBars,
      testBars,
      stepBars: null,
      labelSpanBars: 50,
      embargoBars: 20,
      warmupBars: 50,
      holdoutFraction: 0.2,
      candidates,
      selectionMetric: "sharpe",
      campaign: {
        id: campaignId,
        name: campaignId,
        hypothesis: "Mehrfachtest-Korrektur für die geprüften SMA-Varianten.",
        searchSpace: `FastPeriod ∈ {${fastList}} × SlowPeriod ∈ {${slowList}} → ${candidates.length} Kandidaten`,
        selectionMetric: "sharpe",
        trialBudget: candidates.length,
        holdoutFraction: 0.2,
      },
    };
    const res = await runJob<QuantOverfittingResponse>("PBO/DSR wird berechnet…", () =>
      quantApi.startOverfitting({ walkForward: wf, blocks, estimateEffectiveTrials: estimateEffective })
    );
    if (res) setResult(res);
  };

  return (
    <div className="space-y-4">
      <div className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3 space-y-3">
        <div className="text-xs font-medium">CSCV / PBO und PSR / DSR</div>
        <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4 xl:grid-cols-6">
          <Field label="Blöcke S (gerade)"><Input type="number" value={String(blocks)} onChange={(v) => setBlocks(parseInt(v) || 8)} /></Field>
          <Field label="Training (Bars)"><Input type="number" value={String(trainBars)} onChange={(v) => setTrainBars(parseInt(v) || 100)} /></Field>
          <Field label="Test (Bars)"><Input type="number" value={String(testBars)} onChange={(v) => setTestBars(parseInt(v) || 50)} /></Field>
          <Field label="Schnelle SMA"><Input value={fastList} onChange={setFastList} /></Field>
          <Field label="Langsame SMA"><Input value={slowList} onChange={setSlowList} /></Field>
          <Field label="Kampagnen-Id"><Input value={campaignId} onChange={setCampaignId} /></Field>
          <Field label="Effektive Versuchszahl">
            <label className="flex items-center gap-2 text-[11px] text-[var(--fg-dim)] h-[30px]">
              <input type="checkbox" checked={estimateEffective} onChange={(e) => setEstimateEffective(e.target.checked)} />
              aus Korrelationen schätzen
            </label>
          </Field>
        </div>
        <div className="flex items-center gap-3">
          <button
            onClick={() => void start()}
            disabled={!!busy || candidates.length < 2 || !cfg.dataSourceId}
            className="px-3 py-1.5 text-xs rounded-md bg-[var(--key)] text-black font-medium disabled:opacity-40"
          >
            Berechnen
          </button>
          <span className="text-[11px] text-[var(--fg-faint)]">
            {candidates.length} Kandidaten · Ein niedriger PBO ist keine Garantie, sondern nur die Abwesenheit eines bestimmten Warnsignals.
          </span>
        </div>
      </div>

      {!result && <Unavailable title="Noch keine Overfitting-Analyse" reason="Kandidaten festlegen und berechnen." />}
      {result && !result.ok && <Unavailable title="Analyse nicht möglich" reason={result.error} />}

      {result?.ok && (
        <>
          <div className="flex flex-wrap divide-x divide-[var(--line)] rounded-lg border border-[var(--line)] bg-[var(--panel)]">
            <HeadlineStat
              label="PBO"
              value={formatPercent(result.pbo, 1)}
              tone={result.pbo === null ? "neutral" : result.pbo > 0.5 ? "bad" : "good"}
              hint={result.pboUnavailableReason ?? `${result.combinations} Kombinationen, ${result.candidates} Kandidaten`}
            />
            <HeadlineStat label="Kandidaten" value={String(result.candidates)} />
            <HeadlineStat label="Blöcke S" value={String(result.blocks)} />
            <HeadlineStat label="Kombinationen" value={String(result.combinations)} />
            <HeadlineStat
              label="OOS-Verluste"
              value={formatPercent(result.shareNegativeOutOfSample, 1)}
              hint="Anteil der Kombinationen, in denen der gewählte Kandidat out-of-sample verliert."
            />
            <HeadlineStat label="PSR" value={formatPercent(result.psr, 1)} hint={result.psrUnavailableReason ?? undefined} />
            <HeadlineStat
              label="DSR"
              value={formatPercent(result.dsr, 1)}
              hint={result.dsrUnavailableReason ?? `Effektive Versuche: ${formatNumber(result.effectiveTrials, 2)}`}
            />
          </div>

          {result.pbo !== null ? (
            <Panel title="In-Sample gegen Out-of-Sample (CSCV)">
              <PboScatter pairs={result.pairs} logits={result.logits} />
            </Panel>
          ) : (
            <Unavailable title="PBO nicht berechenbar" reason={result.pboUnavailableReason} />
          )}

          <div className="grid gap-4 xl:grid-cols-2">
            <Panel title="PSR — Probabilistic Sharpe Ratio">
              <dl className="text-[11px] grid grid-cols-2 gap-x-4 gap-y-1 mono">
                <Row k="Sharpe je Periode" v={formatNumber(result.observedSharpePerPeriod, 4)} />
                <Row k="Schiefe γ₃" v={formatNumber(result.skewness, 4)} />
                <Row k="Wölbung γ₄" v={formatNumber(result.kurtosis, 4)} />
                <Row k="Mindest-Historienlänge" v={formatNumber(result.minimumTrackRecordLength, 1)} />
              </dl>
              {result.psrUnavailableReason && (
                <div className="text-[11px] text-[var(--gold)] pt-2">{result.psrUnavailableReason}</div>
              )}
              <NoteBlock title="Definition" items={result.psrDefinitions} tone="method" />
            </Panel>

            <Panel title="DSR — Deflated Sharpe Ratio">
              <dl className="text-[11px] grid grid-cols-2 gap-x-4 gap-y-1 mono">
                <Row k="Tatsächliche Versuche" v={String(result.actualTrials)} />
                <Row k="Effektive Versuche" v={formatNumber(result.effectiveTrials, 2)} />
                <Row k="Erwarteter Max-Sharpe (H₀)" v={formatNumber(result.expectedMaxSharpeUnderNull, 4)} />
              </dl>
              <div className="text-[11px] text-[var(--fg-faint)] pt-2">{result.effectiveTrialsRationale}</div>
              {result.dsrUnavailableReason && (
                <div className="text-[11px] text-[var(--gold)] pt-2">{result.dsrUnavailableReason}</div>
              )}
              <NoteBlock title="Warnungen" items={result.dsrWarnings} tone="warn" />
              <NoteBlock title="Definition" items={result.dsrDefinitions} tone="method" />
            </Panel>
          </div>

          <NoteBlock title="Festgelegte Definitionen (CSCV)" items={result.pboDefinitions} tone="method" />
          <NoteBlock title="Hinweise" items={[...result.pboNotes, ...result.notes]} tone="method" />
          <NoteBlock
            tone="warn"
            title="Einordnung"
            items={[
              "SPA / Reality Check nach White bzw. Hansen ist bewusst NICHT enthalten — das ist ein eigener Forschungsbaustein und wird nicht durch PBO ersetzt.",
              "Es gibt keine Gesamtpunktzahl: PBO, PSR und DSR beantworten verschiedene Fragen und werden getrennt ausgewiesen.",
            ]}
          />
        </>
      )}
    </div>
  );
}

// ==============================================================================================
// Experimente (Versuchsregister)
// ==============================================================================================

function ExperimentsTab() {
  const [campaigns, setCampaigns] = useState<QuantCampaign[]>([]);
  const [trials, setTrials] = useState<QuantTrial[]>([]);
  const [selectedCampaign, setSelectedCampaign] = useState<string>("");
  const [detail, setDetail] = useState<QuantTrial | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const [c, t] = await Promise.all([quantApi.campaigns(), quantApi.trials(selectedCampaign || null)]);
      setCampaigns(c);
      setTrials(t);
      setLoadError(null);
    } catch (e) {
      setLoadError(e instanceof Error ? e.message : String(e));
    }
  }, [selectedCampaign]);

  useEffect(() => {
    void load();
  }, [load]);

  const exportCsv = () => {
    const keys = Array.from(new Set(trials.flatMap((t) => Object.keys(t.metrics))));
    const head = [
      "id", "campaignId", "createdUtc", "strategyId", "origin", "status", "statusReason",
      "periodRole", "codeVersion", "dataSha256", "dataBars", "parameters", ...keys,
    ];
    const rows = trials.map((t) => [
      t.id, t.campaignId, t.createdUtc, t.strategyId, t.origin, t.status, t.statusReason ?? "",
      t.periodRole, t.codeVersion, t.dataSha256, String(t.dataBars),
      Object.entries(t.parameters).map(([k, v]) => `${k}=${v}`).join(" "),
      ...keys.map((k) => (t.metrics[k] === null || t.metrics[k] === undefined ? "" : String(t.metrics[k]))),
    ]);
    const csv = [head, ...rows].map((r) => r.map((c) => `"${String(c).replace(/"/g, '""')}"`).join(",")).join("\n");
    const url = URL.createObjectURL(new Blob([csv], { type: "text/csv;charset=utf-8" }));
    const a = document.createElement("a");
    a.href = url;
    a.download = `versuchsregister_${Date.now()}.csv`;
    a.click();
    URL.revokeObjectURL(url);
  };

  return (
    <div className="space-y-4">
      {loadError && <Unavailable title="Register nicht lesbar" reason={loadError} />}

      <div className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3 space-y-2">
        <div className="flex items-center gap-3">
          <div className="text-xs font-medium">Kampagnen</div>
          <button onClick={() => void load()} className="text-[11px] text-[var(--fg-faint)] hover:text-[var(--fg)]">
            aktualisieren
          </button>
          <button onClick={exportCsv} disabled={trials.length === 0} className="ml-auto text-[11px] text-[var(--key)] disabled:opacity-40">
            Register als CSV exportieren
          </button>
        </div>
        {campaigns.length === 0 ? (
          <div className="text-[11px] text-[var(--fg-faint)]">
            Noch keine Kampagne. Eine Kampagne entsteht beim ersten Walk-forward-Lauf — Budget, Suchraum und
            Auswahlkriterium werden dabei vorab gespeichert und gesperrt.
          </div>
        ) : (
          <table className="w-full text-[11px] mono">
            <thead>
              <tr className="text-[var(--fg-faint)] border-b border-[var(--line)]">
                <th className="text-left font-normal px-2 py-1">Id</th>
                <th className="text-left font-normal px-2 py-1">Kriterium</th>
                <th className="text-right font-normal px-2 py-1">Versuche</th>
                <th className="text-left font-normal px-2 py-1">Holdout</th>
                <th className="text-left font-normal px-2 py-1">Suchraum</th>
              </tr>
            </thead>
            <tbody>
              {campaigns.map((c) => (
                <tr
                  key={c.id}
                  onClick={() => setSelectedCampaign(selectedCampaign === c.id ? "" : c.id)}
                  className={cn(
                    "border-b border-[var(--line)]/60 cursor-pointer hover:bg-[var(--panel-2)]",
                    selectedCampaign === c.id && "bg-[var(--panel-2)]"
                  )}
                >
                  <td className="px-2 py-1 text-[var(--fg)]">{c.id}</td>
                  <td className="px-2 py-1 text-[var(--fg-dim)]">{c.selectionMetric}</td>
                  <td className="px-2 py-1 text-right text-[var(--fg-dim)]">{c.trialsUsed}/{c.trialBudget}</td>
                  <td className="px-2 py-1 text-[var(--fg-dim)]">
                    {c.holdoutFrom ? (c.holdoutConsumed ? "verbraucht" : "reserviert") : "—"}
                  </td>
                  <td className="px-2 py-1 text-[var(--fg-faint)] truncate max-w-[340px]" title={c.searchSpace}>
                    {c.searchSpace}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      <div className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3 space-y-2">
        <div className="text-xs font-medium">
          Versuche {selectedCampaign && <span className="text-[var(--fg-faint)]">— Kampagne {selectedCampaign}</span>}
        </div>
        {trials.length === 0 ? (
          <div className="text-[11px] text-[var(--fg-faint)]">Keine Versuche erfasst.</div>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-[11px] mono">
              <thead>
                <tr className="text-[var(--fg-faint)] border-b border-[var(--line)]">
                  <th className="text-left font-normal px-2 py-1">Id</th>
                  <th className="text-left font-normal px-2 py-1">Herkunft</th>
                  <th className="text-left font-normal px-2 py-1">Status</th>
                  <th className="text-left font-normal px-2 py-1">Rolle</th>
                  <th className="text-left font-normal px-2 py-1">Parameter</th>
                  <th className="text-right font-normal px-2 py-1">Test-Sharpe</th>
                  <th className="text-right font-normal px-2 py-1">gewählt in</th>
                </tr>
              </thead>
              <tbody>
                {trials.map((t) => (
                  <tr
                    key={t.id}
                    onClick={() => setDetail(detail?.id === t.id ? null : t)}
                    className={cn(
                      "border-b border-[var(--line)]/60 cursor-pointer hover:bg-[var(--panel-2)]",
                      detail?.id === t.id && "bg-[var(--panel-2)]"
                    )}
                  >
                    <td className="px-2 py-1 text-[var(--fg)]">{t.id}</td>
                    <td className="px-2 py-1 text-[var(--fg-dim)]">{t.origin}</td>
                    <td
                      className={cn(
                        "px-2 py-1",
                        t.status === "Completed" ? "text-[var(--key)]" : t.status === "Failed" ? "text-[var(--red)]" : "text-[var(--gold)]"
                      )}
                      title={t.statusReason ?? undefined}
                    >
                      {t.status}
                    </td>
                    <td className="px-2 py-1 text-[var(--fg-dim)]">{t.periodRole}</td>
                    <td className="px-2 py-1 text-[var(--fg-faint)]">
                      {Object.entries(t.parameters).map(([k, v]) => `${k}=${v}`).join(" ")}
                    </td>
                    <td className="px-2 py-1 text-right text-[var(--fg-dim)]">
                      {formatNumber(t.metrics["test.sharpe_per_period"], 4)}
                    </td>
                    <td className="px-2 py-1 text-right text-[var(--fg-faint)]">
                      {formatNumber(t.metrics["folds.selected"], 0)}/{formatNumber(t.metrics["folds.total"], 0)}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
        <div className="text-[10px] text-[var(--fg-faint)]">
          Auch negative, fehlgeschlagene und verworfene Versuche bleiben erfasst — sie sind für die
          Mehrfachtest-Korrektur (PBO/DSR) unverzichtbar.
        </div>
      </div>

      {detail && (
        <div className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3 space-y-2">
          <div className="flex items-center justify-between">
            <div className="text-xs font-medium">Versuch {detail.id}</div>
            <button onClick={() => setDetail(null)} className="text-[11px] text-[var(--fg-faint)] hover:text-[var(--fg)]">
              schließen
            </button>
          </div>
          <dl className="text-[11px] grid grid-cols-2 lg:grid-cols-4 gap-x-4 gap-y-1 mono">
            <Row k="Strategie" v={`${detail.strategyId} @ ${detail.strategyVersion}`} />
            <Row k="Herkunft" v={`${detail.origin}${detail.originReference ? ` (${detail.originReference})` : ""}`} />
            <Row k="Codestand" v={detail.codeVersion} />
            <Row k="Seed" v={String(detail.seed)} />
            <Row k="Daten" v={`${detail.dataSymbol} · ${detail.dataBars} Bars`} />
            <Row k="Datenfingerabdruck" v={detail.dataSha256.slice(0, 16) + "…"} />
            <Row k="Quelle" v={detail.dataSource} />
            <Row k="Zeitraum" v={`${detail.periodFrom?.slice(0, 16) ?? "—"} → ${detail.periodTo?.slice(0, 16) ?? "—"}`} />
            <Row k="Rolle" v={detail.periodRole} />
            <Row k="Status" v={`${detail.status}${detail.statusReason ? ` — ${detail.statusReason}` : ""}`} />
            <Row k="Gebühr je Seite" v={`${detail.costs.feePerSide} ${detail.costs.currency}`} />
            <Row k="Slippage" v={`${detail.costs.slippageTicks} Ticks`} />
            <Row k="Kostenprofil" v={detail.costs.isExampleProfile ? "Beispielwerte" : "echte Werte"} />
            <Row k="Erstellt" v={detail.createdUtc.slice(0, 19).replace("T", " ")} />
          </dl>
          <div className="text-[11px]">
            <div className="text-[var(--fg-faint)] mb-1">Kennzahlen</div>
            <div className="grid grid-cols-2 lg:grid-cols-4 gap-x-4 gap-y-0.5 mono">
              {Object.entries(detail.metrics).map(([k, v]) => (
                <div key={k} className="flex justify-between gap-2">
                  <span className="text-[var(--fg-faint)] truncate">{k}</span>
                  <span className={v === null ? "italic text-[var(--fg-faint)]" : "text-[var(--fg-dim)]"}>
                    {v === null ? "n. b." : formatNumber(v, 4)}
                  </span>
                </div>
              ))}
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

// ==============================================================================================
// Kleine Formularbausteine
// ==============================================================================================

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <label className="block">
      <span className="block text-[10px] uppercase tracking-wide text-[var(--fg-faint)] mb-1">{label}</span>
      {children}
    </label>
  );
}

function Input({
  value,
  onChange,
  type = "text",
  className,
}: {
  value: string;
  onChange: (v: string) => void;
  type?: string;
  className?: string;
}) {
  return (
    <input
      type={type}
      value={value}
      onChange={(e) => onChange(e.target.value)}
      className={cn(
        "w-full h-[30px] px-2 rounded-md bg-[var(--panel-2)] border border-[var(--line-2)]",
        "text-xs text-[var(--fg)] mono focus:outline-none focus:border-[var(--key)]",
        className
      )}
    />
  );
}

function Select({
  value,
  onChange,
  options,
  className,
}: {
  value: string;
  onChange: (v: string) => void;
  options: { value: string; label: string }[];
  className?: string;
}) {
  return (
    <select
      value={value}
      onChange={(e) => onChange(e.target.value)}
      className={cn(
        "h-[30px] px-2 rounded-md bg-[var(--panel-2)] border border-[var(--line-2)]",
        "text-xs text-[var(--fg)] focus:outline-none focus:border-[var(--key)]",
        className ?? "w-full"
      )}
    >
      {options.map((o) => (
        <option key={o.value} value={o.value}>
          {o.label}
        </option>
      ))}
    </select>
  );
}
