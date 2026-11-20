// Gespeicherter Ein-Knopf-Bericht: oben die Übersicht (jede Zahl mit Herkunft), darunter die zwölf Abschnitte.
// Kein Gesamtscore, kein „bestanden“ — Status je Abschnitt: fertig / nicht anwendbar / zu wenig Daten / abgebrochen / fehlgeschlagen.
import { useMemo, useState, type ReactNode } from "react";
import { cn } from "@/lib/utils";
import {
  STATUS_TEXT, day, dt, money, num, pct,
  type BacktestReport, type GroupBreakdown, type ReportRun, type ReportValue, type SimulationResult,
} from "@/lib/backtestingApi";
import type { QuantStressBlock, ResearchStepState } from "@/lib/quantApi";
import { Histogram, LineChart, SessionBars, divergingColor } from "./charts";
import { TradeReplay } from "./TradeReplay";

// ---------------------------------------------------------------------------------------------
// Bausteine

const STATUS_CLS: Record<string, string> = {
  Completed: "border-[var(--key)] text-[var(--key)]",
  Running: "border-[var(--cyan)] text-[var(--cyan)] animate-pulse",
  Pending: "border-[var(--line-2)] text-[var(--fg-faint)]",
  NotApplicable: "border-[var(--line-2)] text-[var(--fg-dim)]",
  InsufficientData: "border-[var(--gold)] text-[var(--gold)]",
  NotComputable: "border-[var(--gold)] text-[var(--gold)]",
  Cancelled: "border-[var(--purple)] text-[var(--purple)]",
  Failed: "border-[var(--red)] text-[var(--red)]",
  Skipped: "border-[var(--line-2)] text-[var(--fg-dim)]",
};

export function StatusChip({ status, stepKey }: { status: string; stepKey?: string }) {
  // Der finale Holdout wird im Bericht nie ausgewertet: „fertig“ heißt hier nur „vorbereitet und reserviert“.
  if (stepKey === "holdout" && status === "Completed")
    return <span className="rounded border border-[var(--purple)] px-1.5 py-0.5 text-[10px] font-medium whitespace-nowrap text-[var(--purple)]">reserviert</span>;
  return <span className={cn("rounded border px-1.5 py-0.5 text-[10px] font-medium whitespace-nowrap", STATUS_CLS[status] ?? STATUS_CLS.Pending)}>{STATUS_TEXT[status] ?? status}</span>;
}

function Basis({ children }: { children: ReactNode }) {
  return <span className="rounded bg-[var(--panel-3)] px-1.5 py-0.5 text-[10px] text-[var(--fg-dim)]">Basis: {children}</span>;
}

function Section({ id, step, title, basis, children, defaultOpen = true }: {
  id: string; step?: ResearchStepState; title: string; basis?: string | null; children?: ReactNode; defaultOpen?: boolean;
}) {
  const [open, setOpen] = useState(defaultOpen);
  return (
    <section id={`sec-${id}`} className="rounded-lg border border-[var(--line)] bg-[var(--panel)]">
      <button onClick={() => setOpen((o) => !o)} className="flex w-full flex-wrap items-center gap-2 px-3 py-2 text-left">
        <span className="text-[var(--key)] text-xs">{open ? "▾" : "▸"}</span>
        <h3 className="text-[13px] font-medium text-[var(--fg)]">{title}</h3>
        {step && <StatusChip status={step.status} stepKey={step.key} />}
        {basis && <Basis>{basis}</Basis>}
        {step?.reason && <span className="text-[10px] text-[var(--fg-faint)]">{step.reason}</span>}
      </button>
      {open && <div className="border-t border-[var(--line)] px-3 py-3 space-y-3">{children ?? <Empty step={step} />}</div>}
    </section>
  );
}

function Empty({ step }: { step?: ResearchStepState }) {
  const s = step?.status;
  return (
    <p className="text-[11px] text-[var(--fg-faint)]">
      {s === "Running" ? "Wird gerade berechnet …" : s === "Pending" ? "Wartet auf vorherige Abschnitte." : step?.reason ?? "Kein Ergebnis gespeichert."}
    </p>
  );
}

function Kv({ label, value, hint, tone }: { label: string; value: ReactNode; hint?: ReactNode; tone?: "pos" | "neg" | null }) {
  return (
    <div className="rounded-md border border-[var(--line)] bg-[var(--bg-2)] px-2 py-1.5">
      <div className="text-[10px] text-[var(--fg-faint)]">{label}</div>
      <div className={cn("mono text-[13px]", tone === "pos" ? "text-[var(--key)]" : tone === "neg" ? "text-[var(--red)]" : "text-[var(--fg)]")}>{value}</div>
      {hint && <div className="text-[10px] text-[var(--fg-faint)] leading-snug">{hint}</div>}
    </div>
  );
}

const tone = (v: number | null | undefined) => (v == null ? null : v > 0 ? "pos" : v < 0 ? "neg" : null);
const rv = (v: ReportValue, f: (x: number) => string) => (v.value == null ? <span className="text-[var(--fg-faint)] text-[11px]">nicht definiert</span> : f(v.value));
const rvHint = (v: ReportValue) => (v.value == null ? v.reason : undefined);

function Notes({ items }: { items?: (string | null | undefined)[] }) {
  const list = (items ?? []).filter(Boolean) as string[];
  if (list.length === 0) return null;
  return <ul className="list-disc pl-4 space-y-0.5 text-[11px] text-[var(--fg-dim)]">{list.map((n, i) => <li key={i}>{n}</li>)}</ul>;
}

function Tbl({ head, rows, className }: { head: ReactNode[]; rows: ReactNode[][]; className?: string }) {
  return (
    <div className={cn("overflow-x-auto", className)}>
      <table className="w-full text-[11px] mono">
        <thead><tr className="text-left text-[var(--fg-faint)]">{head.map((h, i) => <th key={i} className="pr-3 py-1 font-normal whitespace-nowrap">{h}</th>)}</tr></thead>
        <tbody>{rows.map((r, i) => <tr key={i} className="border-t border-[var(--line)] align-top">{r.map((c, j) => <td key={j} className="pr-3 py-1 whitespace-nowrap">{c}</td>)}</tr>)}</tbody>
      </table>
    </div>
  );
}

const Grid = ({ children, cols = 4 }: { children: ReactNode; cols?: number }) => (
  <div className={cn("grid gap-2", cols === 3 ? "grid-cols-2 md:grid-cols-3" : cols === 6 ? "grid-cols-2 md:grid-cols-3 xl:grid-cols-6" : "grid-cols-2 md:grid-cols-4")}>{children}</div>
);

// ---------------------------------------------------------------------------------------------

export function ReportView({ run, onOpenHoldout }: { run: ReportRun; onOpenHoldout: () => void }) {
  const rep = run.report;
  const step = (k: string) => run.steps.find((s) => s.key === k);
  if (!rep) return <p className="text-xs text-[var(--fg-faint)]">Für diesen Lauf ist (noch) kein Bericht gespeichert.</p>;
  return (
    <div className="space-y-3">
      <Overview rep={rep} run={run} />
      <Provenance rep={rep} />
      <nav className="flex flex-wrap gap-1 text-[10px]">
        {run.steps.map((s) => {
          const label = stepLabel(s);
          return (
            <a key={s.key} href={`#sec-${s.key}`} className="rounded border border-[var(--line)] px-1.5 py-0.5 text-[var(--fg-dim)] hover:text-[var(--fg)]">
              {label.replace(/^(\d\d) .*/, "$1")} {STATUS_TEXT[s.status] === "fertig" ? "✓" : "·"} {s.key === "oos" ? "Walk-forward-Testdaten" : s.key === "holdout" ? "Holdout (unbenutzt)" : label.replace(/^\d\d /, "").split(" ")[0]}
            </a>
          );
        })}
      </nav>
      <DataSection rep={rep} step={step("data")} />
      <ResultSection rep={rep} step={step("result")} />
      <EquitySection rep={rep} step={step("equity")} />
      <RiskSection rep={rep} step={step("risk")} />
      <SensitivitySection rep={rep} step={step("sensitivity")} />
      <OosSection rep={rep} step={step("oos")} />
      <SimulationSection rep={rep} step={step("simulation")} />
      <CostSection rep={rep} step={step("costs")} />
      <StabilitySection rep={rep} step={step("stability")} />
      <ConcentrationSection rep={rep} step={step("concentration")} />
      <ControlSection rep={rep} step={step("controls")} />
      <TradeSection rep={rep} step={step("trades")} />
      <HoldoutSection run={run} step={step("holdout")} onOpen={onOpenHoldout} />
    </div>
  );
}

/** Einheitliche Abschnittsnamen — auch für bereits gespeicherte Läufe mit älterer Beschriftung. Walk-forward-Testdaten
 *  und der finale Holdout sind zwei verschiedene Dinge und werden nie unter einem gemeinsamen Namen geführt. */
const STEP_LABELS: Record<string, string> = {
  oos: "06 Walk-forward-Testdaten (Out-of-Sample)",
  trades: "12 Einzeltrade-Prüfung (Journal, Replay, MAE/MFE)",
  holdout: "Finaler Holdout – unbenutzt (reserviert)",
};
export const stepLabel = (s: ResearchStepState) => STEP_LABELS[s.key] ?? s.label;

/** Übersichtsbeschriftung je Kennzahl (gespeicherte Altberichte sagten „ungesehene Daten“ ohne Walk-forward-Bezug). */
const FIGURE_LABELS: Record<string, string> = { oos: "Ergebnis auf Walk-forward-Testdaten", sessions: "Walk-forward-Testsessions" };
const figureBasis = (key: string, basis: string) =>
  key === "oos" || key === "sessions" ? basis.replace(/^OOS — /, "Walk-forward-Testdaten (OOS) — ").replace(/^OOS$/, "Walk-forward-Testdaten (OOS)") : basis;

function Overview({ rep, run }: { rep: BacktestReport; run: ReportRun }) {
  const figs = rep.overview.figures;
  const p = rep.provenance;
  return (
    <div className="rounded-lg border border-[var(--line-2)] bg-[var(--panel)] p-3">
      <div className="mb-2 flex flex-wrap items-center gap-2">
        <h2 className="text-sm font-medium text-[var(--fg)]">Übersicht</h2>
        <span className="text-[11px] text-[var(--fg-faint)]">{rep.provenance.strategyName} · {rep.provenance.symbol} · Lauf {run.runId}</span>
        <span className="text-[10px] text-[var(--fg-faint)]">Jede Zahl nennt ihre Herkunft. Kein Gesamtscore, kein „bestanden“.</span>
      </div>
      {figs.length === 0 ? <p className="text-[11px] text-[var(--fg-faint)]">Die Übersicht erscheint, sobald der Basislauf fertig ist.</p> : (
        <div className="grid gap-2 grid-cols-2 lg:grid-cols-3 xl:grid-cols-6">
          {figs.map((f) => (
            <div key={f.key} className="rounded-md border border-[var(--line)] bg-[var(--bg-2)] p-2">
              <div className="text-[10px] text-[var(--fg-faint)]">{FIGURE_LABELS[f.key] ?? f.label}</div>
              <div className={cn("mono text-lg", f.unit === "currency" ? (f.key === "dd" || f.key === "simdd" ? "text-[var(--gold)]" : tone(f.value) === "pos" ? "text-[var(--key)]" : tone(f.value) === "neg" ? "text-[var(--red)]" : "text-[var(--fg)]") : "text-[var(--fg)]")}>
                {f.value == null ? <span className="text-[12px] text-[var(--fg-faint)]">nicht verfügbar</span> : f.unit === "currency" ? money(f.value) : num(f.value, 0)}
              </div>
              <div className="mt-0.5 text-[10px] text-[var(--cyan)]">{figureBasis(f.key, f.basis)}</div>
              {(f.reason || f.detail) && <div className="text-[10px] text-[var(--fg-faint)] leading-snug">{f.reason ?? f.detail}</div>}
            </div>
          ))}
        </div>
      )}
      <div className="mt-2 grid gap-1 text-[11px] text-[var(--fg-dim)] md:grid-cols-2">
        <div className="rounded-md border border-[var(--line)] px-2 py-1"><b className="text-[var(--fg)]">Daten für das Signal:</b> {p.signalData || `OHLC-Zeitkerzen (${p.timeframeMinutes} Min) aus ${p.dataSource}`}</div>
        <div className="rounded-md border border-[var(--line)] px-2 py-1"><b className="text-[var(--fg)]">Daten für die simulierte Ausführung:</b> {p.executionData || "Bar-basierte OHLC-Engine: Fill am Open der Folgekerze ± Slippage."}</div>
      </div>
      <div className="mt-2 rounded-md border border-[var(--purple)] px-2 py-1 text-[11px] text-[var(--fg-dim)]">
        <b className="text-[var(--purple)]">Finaler Holdout – unbenutzt:</b>{" "}
        {p.holdoutFrom
          ? <>{p.holdoutSessions} Session(s), {dt(p.holdoutFrom)} – {dt(p.holdoutTo)} UTC. In keiner Kennzahl, Parameterwahl, Simulation oder im Vergleich enthalten — getrennt von den Walk-forward-Testdaten.</>
          : "kein Holdout reserviert."}
      </div>
    </div>
  );
}

function Provenance({ rep }: { rep: BacktestReport }) {
  const p = rep.provenance;
  return (
    <Section id="provenance" title="Herkunft & Reproduzierbarkeit" defaultOpen={false}>
      <Grid cols={3}>
        <Kv label="Strategieversion" value={<span className="text-[11px]">{p.strategyVersion}</span>} />
        <Kv label="Engine-Stand" value={<span className="text-[11px]">{p.engineVersion}</span>} hint={p.engineKind === "orderflow-ticks" ? "Tick-Orderflow-Pfad" : "OHLC-Kerzen-Engine"} />
        <Kv label="Daten-Hash (SHA-256, Entwicklungsbereich)" value={<span className="text-[11px] break-all">{p.dataSha256}</span>} />
        <Kv label="Datenquelle / -art" value={<span className="text-[11px]">{p.dataSource}</span>} hint={p.dataKind} />
        <Kv label="Zeitraum gesamt" value={<span className="text-[11px]">{dt(p.dataFrom)} – {dt(p.dataTo)} UTC</span>}
          hint={p.holdoutFrom ? `Entwicklung bis ${dt(p.developmentTo)} · Holdout ${dt(p.holdoutFrom)} – ${dt(p.holdoutTo)} (${p.holdoutSessions} Sessions, reserviert)` : "kein Holdout"} />
        <Kv label="Verwendete Sessions" value={p.sessionsUsed.length} hint={p.sessionsUsed.length ? `${p.sessionsUsed[0]} … ${p.sessionsUsed[p.sessionsUsed.length - 1]}` : undefined} />
        <Kv label="Parameter" value={<span className="text-[11px]">{Object.entries(p.parameters).map(([k, v]) => `${k}=${v}`).join(", ") || "—"}</span>} hint={`${p.quantity} Kontrakt(e), Startkapital ${money(p.initialBalance, 0)}`} />
        <Kv label="Kostenmodell" value={<span className="text-[11px]">{p.costModel ? `${money(p.costModel.feePerSide)} je Seite · ${num(p.costModel.slippageTicks)} Ticks Slippage` : "–"}</span>}
          hint={p.costModel?.feeIsExample ? "Beispiel-Gebührenprofil (config/fees/*.example.json) — keine echten Broker-Kosten" : undefined} />
        <Kv label="Seeds" value={<span className="text-[11px]">{Object.entries(p.seeds).map(([k, v]) => `${k}: ${v}`).join(" · ")}</span>} />
        <Kv label="Daten gelesen / wiederverwendet" value={`${p.dataFileReads}× / ${p.dataCacheHits}×`} hint="Alle Analysen nutzen dieselben geladenen Kerzen (Zwischenspeicher)." />
      </Grid>
      <Notes items={p.fillModel} />
      <Tbl head={["Analyseprofil", "Wert"]} rows={Object.entries(p.analysisProfile).map(([k, v]) => [k, v])} />
    </Section>
  );
}

// ---- 01 ----
function DataSection({ rep, step }: { rep: BacktestReport; step?: ResearchStepState }) {
  const d = rep.data;
  return (
    <Section id="data" step={step} title="01 Datenqualität & zeitliche Korrektheit" basis={step?.dataBasis}>
      {d && <>
        <Grid>
          <Kv label="Datenart" value={<span className="text-[11px]">{d.dataKind}</span>} />
          <Kv label="Handelstage" value={d.sessions.length} hint={`Median ${num(d.medianBarsPerSession, 0)} Kerzen je Tag`} />
          <Kv label="Lücken innerhalb von Sessions" value={d.intraSessionGaps} hint={d.intraSessionGaps ? `größte ${num(d.largestGapMinutes, 0)} Min.` : "keine"} />
          <Kv label="Sommerzeitwechsel im Zeitraum" value={d.dstTransitions.length} />
        </Grid>
        <div>
          <div className="mb-1 text-[11px] font-medium text-[var(--fg-dim)]">Zeitliche Korrektheit</div>
          <Tbl head={["Prüfung", "Ergebnis", "Nachweis"]} rows={d.temporalChecks.map((c) => [
            c.name,
            c.passed == null ? <span className="text-[var(--fg-dim)]">Hinweis</span> : c.passed ? <span className="text-[var(--key)]">✓ bestanden</span> : <span className="text-[var(--red)]">✗ nicht bestanden</span>,
            <span className="whitespace-normal font-sans">{c.detail}</span>,
          ])} />
        </div>
        <Notes items={[d.sessionDefinition, ...d.dataKindNotes, ...d.dstTransitions, ...d.historyLimits]} />
        {d.quality && d.quality.issues.length > 0 && (
          <Tbl head={["Schwere", "Code", "Befund", "Anzahl"]} rows={d.quality.issues.map((i) => [i.severity, i.code, <span className="whitespace-normal font-sans">{i.message}</span>, i.count])} />
        )}
        <details className="text-[11px]"><summary className="cursor-pointer text-[var(--fg-dim)]">Sessions im Entwicklungsbereich ({d.sessions.length})</summary>
          <Tbl head={["Handelstag", "Kerzen", "von", "bis", ""]} rows={d.sessions.map((s) => [s.key, s.bars, dt(s.firstT), dt(s.lastT), s.short ? <span className="text-[var(--gold)]">verkürzt</span> : ""])} />
        </details>
      </>}
    </Section>
  );
}

// ---- 02 ----
function ResultSection({ rep, step }: { rep: BacktestReport; step?: ResearchStepState }) {
  const r = rep.result;
  const s = r?.stats;
  return (
    <Section id="result" step={step} title="02 Basis-Backtest & Ergebnisrechnung" basis={r?.basis}>
      {s && r && <>
        <Grid cols={6}>
          <Kv label="Netto (nach Gebühren)" value={money(s.netPnL)} tone={tone(s.netPnL)} />
          <Kv label="Brutto (Fill-Preise)" value={money(s.grossPnL)} hint="Slippage bereits enthalten" />
          <Kv label="Gebühren" value={money(s.fees)} hint="genau einmal je Seite" />
          <Kv label="Modellierte Slippage" value={money(s.modeledSlippage)} hint="steckt im Fill-Preis, nicht doppelt abgezogen" />
          <Kv label="Vor allen Kosten" value={money(s.preCostPnL)} />
          <Kv label="Trades" value={s.trades} hint={`${s.longs} Long · ${s.shorts} Short`} />
          <Kv label="Trefferquote" value={rv(s.winRate, (v) => pct(v))} hint={rvHint(s.winRate) ?? `${s.wins} / ${s.losses} / ${s.breakeven} (G/V/0)`} />
          <Kv label="Ø Gewinn" value={rv(s.averageWin, (v) => money(v))} hint={rvHint(s.averageWin)} />
          <Kv label="Ø Verlust" value={rv(s.averageLoss, (v) => money(v))} hint={rvHint(s.averageLoss)} />
          <Kv label="Payoff-Ratio" value={rv(s.payoffRatio, (v) => num(v))} hint={rvHint(s.payoffRatio)} />
          <Kv label="Erwartungswert je Trade" value={rv(s.expectancy, (v) => money(v))} hint={rvHint(s.expectancy)} tone={tone(s.expectancy.value)} />
          <Kv label="Profit Factor" value={rv(s.profitFactor, (v) => num(v))} hint={rvHint(s.profitFactor)} />
          <Kv label="Exposure" value={rv(s.exposure, (v) => pct(v))} hint="Anteil Kerzen mit offener Position" />
          <Kv label="Haltedauer Ø / Median" value={`${rv(s.averageHoldingMinutes, (v) => num(v, 0))} / ${s.medianHoldingMinutes.value != null ? num(s.medianHoldingMinutes.value, 0) : "–"} Min.`} />
          <Kv label="Größter Gewinn / Verlust" value={<span className="text-[12px]">{rv(s.largestWin, (v) => money(v))} / {rv(s.largestLoss, (v) => money(v))}</span>} />
          <Kv label="Realisiert" value={money(s.realizedNetPnL)} hint="ohne die am Datenende nur bewertete Position" />
          <Kv label="Unrealisiert am Datenende" value={money(s.unrealizedAtEnd)} hint={s.openAtEnd ? `${s.openAtEnd} offene Position(en), zum letzten Schluss bewertet` : "keine offene Position"} />
          <Kv label="Signale / verworfene Entries" value={`${r.signalsGenerated} / ${r.missedEntrySkips + r.invalidStopSkips}`} hint={`${r.evaluatedBars} ausgewertete Kerzen`} />
        </Grid>
        <Notes items={r.notes} />
      </>}
    </Section>
  );
}

// ---- 03 ----
function EquitySection({ rep, step }: { rep: BacktestReport; step?: ResearchStepState }) {
  const e = rep.equity;
  return (
    <Section id="equity" step={step} title="03 Equity, Drawdown & Verlustphasen" basis={e?.basis}>
      {e && <>
        <Grid cols={6}>
          <Kv label="Max. DD (abgeschlossene Trades)" value={money(e.closedTrades.maxDrawdown)} hint={pct(e.closedTrades.maxDrawdownFraction) + " vom Hoch"} />
          <Kv label="Max. DD (Mark-to-Market)" value={money(e.markToMarket.maxDrawdown)} hint={pct(e.markToMarket.maxDrawdownFraction) + " · zwischenzeitlich"} />
          <Kv label="Status max. DD" value={e.closedTrades.maxDrawdownOpen ? <span className="text-[var(--gold)]">offen</span> : "aufgeholt"}
            hint={e.closedTrades.peakTime ? `Hoch ${dt(e.closedTrades.peakTime)} → Tief ${dt(e.closedTrades.troughTime)}` : undefined} />
          <Kv label="Zeit bis Erholung" value={e.closedTrades.recoveryDays == null ? "–" : `${num(e.closedTrades.recoveryDays, 1)} Tage`} hint={e.closedTrades.maxDrawdownOpen ? "bis Datenende (nicht erholt)" : undefined} />
          <Kv label="Längste Zeit unter Hoch" value={`${num(e.closedTrades.longestUnderwaterDays, 1)} Tage`} hint={e.closedTrades.longestUnderwaterOpen ? "bis Datenende offen" : undefined} />
          <Kv label="Längste Verlustserie" value={`${e.losingStreak?.longestCount ?? 0} Trades`} hint={e.losingStreak ? `${money(e.losingStreak.longestSum)} · ${e.losingStreak.longestSessionCount} Verlusttage in Folge` : undefined} />
        </Grid>
        <LineChart ariaLabel="Kapitalkurve" series={[
          { name: "Mark-to-Market", x: e.markToMarket.t, y: e.markToMarket.equity, color: "#22b8f0" },
          { name: "Abgeschlossene Trades", x: e.closedTrades.t, y: e.closedTrades.equity, color: "#f0c35a", dashed: true },
        ]} />
        <div className="text-[11px] text-[var(--fg-dim)]">Drawdown (Mark-to-Market) unter dem laufenden Hoch</div>
        <LineChart ariaLabel="Drawdown-Verlauf" height={120} series={[{ name: "Drawdown", x: e.markToMarket.t, y: e.markToMarket.drawdown, color: "#f0566a", area: true }]} />
        <Notes items={e.notes} />
      </>}
    </Section>
  );
}

// ---- 04 ----
function RiskSection({ rep, step }: { rep: BacktestReport; step?: ResearchStepState }) {
  const r = rep.risk;
  return (
    <Section id="risk" step={step} title="04 Rendite im Verhältnis zum Risiko" basis={r?.basis}>
      {r && <>
        <Grid>
          {r.metrics.map((m) => (
            <Kv key={m.key} label={m.label} value={m.value == null ? <span className="text-[11px] text-[var(--fg-faint)]">nicht definiert</span> : m.unit.startsWith("fraction") ? pct(m.value) : num(m.value, 3)}
              hint={m.value == null ? m.unavailableReason : `n = ${m.sampleSize} · ${m.method}`} />
          ))}
        </Grid>
        <Notes items={[`Frequenz: ${r.frequency}${r.periodsPerYear ? ` · annualisiert mit ${num(r.periodsPerYear, 0)} Perioden/Jahr` : ""}`, r.annualizationNote, r.capitalBase, ...r.notes]} />
      </>}
    </Section>
  );
}

// ---- 05 ----
function SensitivitySection({ rep, step }: { rep: BacktestReport; step?: ResearchStepState }) {
  const s = rep.sensitivity;
  return (
    <Section id="sensitivity" step={step} title="05 Parametersensitivität & Wechselwirkungen" basis={s?.basis}>
      {s && !s.applicable && <p className="text-[11px] text-[var(--fg-dim)]">{s.reason}</p>}
      {s && s.applicable && <>
        <p className="text-[11px] text-[var(--fg-dim)]">{s.definition} Mindesttrades: {s.minTrades}. Engine-Läufe: {s.engineRuns}.</p>
        <div className="grid gap-3 lg:grid-cols-2">
          {s.parameters.map((p) => (
            <div key={p.name} className="rounded-md border border-[var(--line)] p-2">
              <div className="flex items-center gap-2 text-[12px]">
                <b className="text-[var(--fg)]">{p.name}</b><span className="text-[var(--fg-faint)]">Basis {num(p.baseValue)}</span>
                <span className={cn("ml-auto rounded px-1.5 py-0.5 text-[10px]", p.classification === "stabil positiv" ? "bg-[rgba(60,240,160,0.15)] text-[var(--key)]" : p.classification === "einzelne Spitze" || p.classification === "zu wenige Trades" ? "bg-[rgba(240,195,90,0.15)] text-[var(--gold)]" : p.classification === "stabiler Verlust" ? "bg-[rgba(240,86,106,0.15)] text-[var(--red)]" : "bg-[var(--panel-3)] text-[var(--fg-dim)]")}>{p.classification}</span>
              </div>
              <Tbl head={["Wert", "Netto", "Trades", "PF", "Max. DD", "Tage +", "Netto je Tag"]} rows={p.points.map((pt) => [
                <span className={pt.isBase ? "text-[var(--cyan)]" : ""}>{num(pt.value)}{pt.isBase ? " (Basis)" : ""}</span>,
                pt.valid ? <span className={pt.net >= 0 ? "text-[var(--key)]" : "text-[var(--red)]"}>{money(pt.net, 0)}</span> : <span className="text-[var(--fg-faint)]" title={pt.error ?? ""}>ungültig</span>,
                pt.valid ? <span className={pt.trades < s.minTrades ? "text-[var(--gold)]" : ""}>{pt.trades}</span> : "–",
                pt.valid ? num(pt.profitFactor ?? null) : "–",
                pt.valid ? money(pt.maxDrawdown, 0) : "–",
                pt.valid ? `${pt.positiveSessions}/${pt.sessions}` : "–",
                pt.valid ? <MiniBars values={pt.sessionNets} /> : "",
              ])} />
            </div>
          ))}
        </div>
        {s.pairs.map((pair) => <Heatmap key={pair.x + pair.y} pair={pair} minTrades={s.minTrades} />)}
      </>}
    </Section>
  );
}

function MiniBars({ values }: { values: number[] }) {
  const maxAbs = Math.max(1e-9, ...values.map(Math.abs));
  return (
    <svg viewBox={`0 0 ${values.length * 3} 16`} className="h-4" style={{ width: Math.min(160, values.length * 3) }} aria-label="Netto je Handelstag">
      {values.map((v, i) => { const h = (Math.abs(v) / maxAbs) * 7.5; return <rect key={i} x={i * 3} y={v >= 0 ? 8 - h : 8} width={2} height={Math.max(0.4, h)} fill={v >= 0 ? "#3cf0a0" : "#f0566a"} />; })}
    </svg>
  );
}

function Heatmap({ pair, minTrades }: { pair: NonNullable<BacktestReport["sensitivity"]>["pairs"][number]; minTrades: number }) {
  const maxAbs = Math.max(1e-9, ...pair.cells.filter((c) => c.valid).map((c) => Math.abs(c.net)));
  return (
    <div>
      <div className="mb-1 text-[11px] text-[var(--fg-dim)]">2D: <b>{pair.x}</b> (Zeilen) × <b>{pair.y}</b> (Spalten) — Netto, Zahl = Trades; Rahmen = Basis; ausgegraut = weniger als {minTrades} Trades</div>
      <div className="overflow-x-auto">
        <table className="text-[10px] mono">
          <thead><tr><th className="pr-2 text-[var(--fg-faint)] font-normal">{pair.x} \ {pair.y}</th>{pair.yValues.map((y) => <th key={y} className="px-1 text-[var(--fg-faint)] font-normal">{num(y)}</th>)}</tr></thead>
          <tbody>
            {pair.xValues.map((x) => (
              <tr key={x}><td className="pr-2 text-[var(--fg-faint)]">{num(x)}</td>
                {pair.yValues.map((y) => {
                  const c = pair.cells.find((k) => k.x === x && k.y === y);
                  const base = x === pair.baseX && y === pair.baseY;
                  return (
                    <td key={y} className="p-0.5">
                      <div title={c?.valid ? `${pair.x}=${x}, ${pair.y}=${y}: Netto ${money(c.net)}, ${c.trades} Trades` : c?.error ?? "ungültig"}
                        className={cn("w-[74px] rounded px-1 py-1 text-center", base && "outline outline-2 outline-[var(--cyan)]", c && c.valid && c.trades < minTrades && "opacity-45")}
                        style={{ background: c?.valid ? divergingColor(c.net, maxAbs) : "#0e2139", color: "#020d1e" }}>
                        {c?.valid ? <><div className="font-semibold">{money(c.net, 0)}</div><div>{c.trades} T</div></> : <span className="text-[var(--fg-faint)]">ungültig</span>}
                      </div>
                    </td>
                  );
                })}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}

// ---- 06 ----
function utcTime(iso?: string | null) {
  return iso ? iso.replace(/^\d{4}-\d{2}-\d{2}T/, "").replace(/(\.\d{3})\d*Z$/, "$1 UTC") : "–";
}

/** Kosten-/Verzögerungs-Stress (Tick-Pfad) — Teil desselben Berichts, kein eigener Start. */
function TickStressBlock({ s }: { s: NonNullable<NonNullable<BacktestReport["oos"]>["tickStress"]> }) {
  const ex = s.fillExample;
  return (
    <div className="space-y-2 rounded-md border border-[var(--line)] p-2">
      <div className="flex flex-wrap items-center gap-2 text-[12px]">
        <b className="text-[var(--fg)]">Kosten- und Verzögerungs-Stress (angenommene Szenarien, Engine-Neuläufe)</b>
        <StatusChip status={s.status} />
        <span className="text-[10px] text-[var(--fg-faint)]">{s.basis}</span>
      </div>
      {s.status !== "Completed" && s.reason && <div className="text-[11px] text-[var(--gold)]">{s.reason}</div>}
      {s.rows.length > 0 && (
        <Tbl head={["Szenario", "geschlossene Trades", "offene Endposition", "Gebühren", "Netto inkl. offener Bewertung", "Differenz zur Basis"]}
          rows={s.rows.map((r) => [
            <span className="whitespace-normal font-sans">
              <b className="text-[var(--fg)]">{r.label}</b>
              <span className="block text-[10px] text-[var(--fg-faint)]">{r.assumption}</span>
              {r.unfilledOrdersAtEnd > 0 && <span className="block text-[10px] text-[var(--gold)]">{r.unfilledOrdersAtEnd} Order(s) am Fensterende ohne zulässigen Fill-Tick — nicht gefüllt, nichts erfunden</span>}
            </span>,
            r.closedTrades, r.openEndPositions, money(r.fees),
            <span className={r.value >= 0 ? "text-[var(--key)]" : "text-[var(--red)]"} title={`geschlossen ${money(r.closedNet)} + offen ${money(r.openValuation)}`}>{money(r.value)}</span>,
            r.key === "base" ? (s.baseReproducesWalkForward ? "— (= Walk-forward)" : <span className="text-[var(--red)]">weicht ab</span>) : money(r.diffToBase),
          ])} />
      )}
      {ex && (
        <div className="text-[11px] text-[var(--fg-dim)]">
          <b className="text-[var(--fg)]">Ein Trade, verschoben durch die Verzögerung</b> ({ex.window}, {ex.side}): Einstiegssignal {utcTime(ex.entrySignalUtc)} ·
          Basis-Fill Tick {ex.baseEntryTick ?? "–"} {utcTime(ex.baseEntryUtc)} @ {num(ex.baseEntryPrice)} → verzögert Tick {ex.delayedEntryTick ?? "–"} {utcTime(ex.delayedEntryUtc)} @ {num(ex.delayedEntryPrice)} ·
          Ausstiegssignal {utcTime(ex.exitSignalUtc)} · Basis-Exit Tick {ex.baseExitTick ?? "–"} @ {num(ex.baseExitPrice)} → verzögert Tick {ex.delayedExitTick ?? "–"} @ {num(ex.delayedExitPrice)} ·
          Netto {money(ex.baseNet)} → {money(ex.delayedNet)} (Tick-Index im Testfenster)
        </div>
      )}
      <ul className="list-disc pl-4 text-[10px] text-[var(--fg-faint)]">{s.notes.map((n, i) => <li key={i}>{n}</li>)}</ul>
    </div>
  );
}

function OosSection({ rep, step }: { rep: BacktestReport; step?: ResearchStepState }) {
  const o = rep.oos;
  const [sel, setSel] = useState<string | null>(null);
  const variant = o?.variants.find((v) => v.key === (sel ?? o.primaryVariant)) ?? o?.variants[0];
  const curves = useMemo(() => {
    if (!variant || variant.status !== "Completed") return null;
    let a = 0, b = 0;
    const x: number[] = [], ya: number[] = [], yb: number[] = [];
    variant.oosSessionResults.forEach((s, i) => { a += s.net; b += variant.baseSessionResults[i]?.net ?? 0; x.push(i + 1); ya.push(a); yb.push(b); });
    return { x, ya, yb };
  }, [variant]);
  return (
    <Section id="oos" step={step} title="06 Walk-forward-Testdaten (Out-of-Sample) — Auswahl nur im Training" basis={step?.dataBasis?.replace(/^OOS:/, "Walk-forward-Testdaten:") ?? "Walk-forward-Testdaten"}>
      {o && <>
        <div className="rounded-md border border-[var(--line-2)] bg-[var(--bg-2)] p-2 text-[11px] text-[var(--fg-dim)] space-y-1">
          <div><b className="text-[var(--fg)]">Auswahlregel (vorab gespeichert):</b> {o.selectionRule}</div>
          {o.selectionFigure && <div><b className="text-[var(--fg)]">Auswahlzahl:</b> {o.selectionFigure}</div>}
          {o.windowEndRule && <div><b className="text-[var(--fg)]">Fensterende:</b> {o.windowEndRule}</div>}
          <div><b className="text-[var(--fg)]">Gleichstand:</b> {o.tieRule}</div>
          <div><b className="text-[var(--fg)]">Raster:</b> {o.grid.length} Kandidaten — {o.grid.slice(0, 9).map((g) => Object.entries(g).map(([k, v]) => `${k}=${v}`).join(",") || "keine Parameter").join(" · ")}{o.grid.length > 9 ? " …" : ""}</div>
          <div><b className="text-[var(--fg)]">Grenzen/Warm-up:</b> {o.leakageRules}</div>
          <div className="text-[var(--gold)]">{o.holdoutNote}</div>
        </div>
        <div className="flex flex-wrap gap-2">
          {o.variants.map((v) => (
            <button key={v.key} onClick={() => setSel(v.key)}
              className={cn("rounded-md border px-2 py-1 text-left text-[11px]", variant?.key === v.key ? "border-[var(--cyan)]" : "border-[var(--line)]")}>
              <div className="flex items-center gap-2"><b className="text-[var(--fg)]">{v.label}</b><StatusChip status={v.status} />{v.key === o.primaryVariant && <span className="text-[10px] text-[var(--cyan)]">Primär</span>}</div>
              <div className="text-[10px] text-[var(--fg-faint)]">
                {v.status === "Completed" ? `OOS ${money(v.oosValue ?? v.oosNet, 0)}${v.oosValue != null ? " inkl. offen" : ""} · ${v.oosTrades} Trades · ${v.oosSessions} Sessions` : v.reason}
              </div>
            </button>
          ))}
        </div>
        {variant && variant.status === "Completed" && <>
          <Grid cols={6}>
            {variant.oosValue != null ? <>
              <Kv label="OOS-Netto inkl. offener Bewertung (gewählte Parameter)" value={money(variant.oosValue)} tone={tone(variant.oosValue)}
                hint={`geschlossen ${money(variant.oosNet)} + offen ${money(variant.oosOpenValuation ?? 0)} (${variant.oosOpenWindowEnds ?? 0} Fenster offen)`} />
              <Kv label="Basisstrategie inkl. offener Bewertung, dieselben Tage" value={money(variant.baseValueSameSessions ?? variant.baseNetSameSessions)}
                tone={tone(variant.baseValueSameSessions ?? variant.baseNetSameSessions)}
                hint={`geschlossen ${money(variant.baseNetSameSessions)} (${variant.baseTradesSameSessions} Trades) + offen ${money(variant.baseOpenValuationSameSessions ?? 0)}`} />
            </> : <>
              <Kv label="OOS-Netto (gewählte Parameter)" value={money(variant.oosNet)} tone={tone(variant.oosNet)} hint="nur verkettete Testfenster" />
              <Kv label="Basisstrategie, dieselben Tage" value={money(variant.baseNetSameSessions)} tone={tone(variant.baseNetSameSessions)} hint={`${variant.baseTradesSameSessions} Trades`} />
            </>}
            <Kv label="OOS-Trades / Sessions" value={`${variant.oosTrades} / ${variant.oosSessions}`} />
            <Kv label="OOS max. DD" value={money(variant.oosMaxDrawdown)} hint={`Basis gleiche Tage: ${money(variant.baseMaxDrawdownSameSessions)}`} />
            <Kv label="Training / Test" value={`${variant.trainSessions} / ${variant.testSessions} Sessions`} />
            <Kv label="Versuchsregister" value={`${variant.trialsRecorded} Trials`} hint={variant.campaignId ?? undefined} />
          </Grid>
          {curves && <LineChart ariaLabel="Kumuliertes OOS-Netto je Testsession" timeAxis={false} zeroLine series={[
            { name: "OOS (im Training gewählt)", x: curves.x, y: curves.ya, color: "#22b8f0" },
            { name: "Basisstrategie (unverändert), dieselben Tage", x: curves.x, y: curves.yb, color: "#f0c35a", dashed: true },
          ]} />}
          {o.tickStress && variant.key === o.tickStress.variant && <TickStressBlock s={o.tickStress} />}
          {o.tickStress && variant.key !== o.tickStress.variant && o.tickStress.variant && (
            <div className="text-[10px] text-[var(--fg-faint)]">Kosten-/Verzögerungs-Stress: siehe Primärvariante ({o.tickStress.variant}).</div>
          )}
          {o.tickStress && !o.tickStress.variant && o.tickStress.reason && <div className="text-[10px] text-[var(--fg-faint)]">Kosten-/Verzögerungs-Stress: {o.tickStress.reason}</div>}
          {variant.folds.some((f) => f.testValue != null || f.baseValue != null) ? (
            <Tbl head={["Fenster", "Training", "Sess.", "Test", "Sess.", "Gewählt (nur Training)", "Trainingswert (inkl. offen)", "Test geschlossen", "Test offen bewertet", "Test inkl. offen", "Trades", "Basis inkl. offen", "Hinweis"]}
              rows={variant.folds.map((f) => [
                f.index + 1, `${day(f.trainFromT)} – ${day(f.trainToT)}`, f.trainSessions, `${day(f.testFromT)} – ${day(f.testToT)}`, f.testSessions,
                f.selectedParameters ? Object.entries(f.selectedParameters).map(([k, v]) => `${k}=${v}`).join(", ") || "feste Strategie" : <span className="text-[var(--gold)]">keiner</span>,
                num(f.trainValue ?? null), money(f.testNet ?? null), money(f.testOpenValuation ?? null),
                <span className={(f.testValue ?? 0) >= 0 ? "text-[var(--key)]" : "text-[var(--red)]"}>{money(f.testValue ?? null)}</span>, f.testTrades,
                <span title={`geschlossen ${money(f.baseNet)} + offen ${money(f.baseOpenValuation ?? 0)}`}>{money(f.baseValue ?? f.baseNet)}</span>,
                <span className="whitespace-normal font-sans text-[var(--fg-faint)]">{f.note ?? ""}</span>,
              ])} />
          ) : (
            <Tbl head={["Fenster", "Training", "Sess.", "Test", "Sess.", "Gewählt (nur Training)", "Trainingswert", "Test-Netto", "Trades", "Basis-Netto", "Hinweis"]}
              rows={variant.folds.map((f) => [
                f.index + 1, `${day(f.trainFromT)} – ${day(f.trainToT)}`, f.trainSessions, `${day(f.testFromT)} – ${day(f.testToT)}`, f.testSessions,
                f.selectedParameters ? Object.entries(f.selectedParameters).map(([k, v]) => `${k}=${v}`).join(", ") || "feste Strategie" : <span className="text-[var(--gold)]">keiner</span>,
                num(f.trainValue ?? null), <span className={(f.testNet ?? 0) >= 0 ? "text-[var(--key)]" : "text-[var(--red)]"}>{money(f.testNet ?? null)}</span>, f.testTrades,
                money(f.baseNet), <span className="whitespace-normal font-sans text-[var(--fg-faint)]">{f.note ?? ""}</span>,
              ])} />
          )}
          {variant.folds.some((f) => (f.testSessionIds?.length ?? 0) > 0) && (
            <div className="space-y-2">
              <div className="text-[11px] text-[var(--fg-dim)]">
                Je Fenster gespeichert: tatsächliche Handelstage, Trainingswert je Kandidat, eingefrorener Gewinner und der Standardparameter auf genau denselben Testtagen.
                {o.missingDataDays && o.missingDataDays.length > 0 && <> Fehlende Datentage (Werktage ohne Ticks): <b className="text-[var(--gold)]">{o.missingDataDays.join(", ")}</b>.</>}
                {o.holdoutSessionIds && o.holdoutSessionIds.length > 0 && <> Reservierter Holdout (unbenutzt): {o.holdoutSessionIds.join(", ")}.</>}
              </div>
              {variant.folds.map((f) => (
                <div key={f.index} className="rounded-md border border-[var(--line)] p-2 text-[11px]">
                  <div className="flex flex-wrap gap-x-4 gap-y-0.5 text-[var(--fg-dim)]">
                    <span><b className="text-[var(--fg)]">Fenster {f.index + 1}</b></span>
                    <span>Training: {f.trainSessionIds?.join(", ")}</span>
                    <span>Test: {f.testSessionIds?.join(", ")}</span>
                    {f.missingDataDays && f.missingDataDays.length > 0 && <span className="text-[var(--gold)]">fehlender Datentag: {f.missingDataDays.join(", ")}</span>}
                  </div>
                  <div className="mt-1 text-[var(--fg-dim)]">
                    Trainingswert je Kandidat ({o.selectionFigure ? "Netto inkl. offener Bewertung am Fensterende" : "Netto nach Kosten"}): {Object.entries(f.candidateTrainValues ?? {}).map(([k, v]) =>
                      `${k.replace(/_TicksPerBar\d+$/, "")}: ${v == null ? "ausgeschlossen" : money(v)} (${f.candidateTrainTrades?.[k] ?? 0} Tr.)`).join(" · ")}
                  </div>
                  <Tbl className="mt-1" head={["Testtag", "Trades (Gewinner)", "Netto (Gewinner)", "Trades (Standard)", "Netto (Standard)"]}
                    rows={(f.testSessionResults ?? []).map((s, i) => [
                      s.key, s.trades === 0 ? <span className="text-[var(--fg-faint)]">0 — getestet, kein Einstieg</span> : s.trades,
                      <>{money(s.net)}{s.openValuation != null && <span className="text-[var(--fg-faint)]"> (davon offen {money(s.openValuation)})</span>}</>,
                      f.baseTestSessionResults?.[i]?.trades ?? "–",
                      <>{money(f.baseTestSessionResults?.[i]?.net ?? null)}{f.baseTestSessionResults?.[i]?.openValuation != null &&
                        <span className="text-[var(--fg-faint)]"> (davon offen {money(f.baseTestSessionResults[i].openValuation ?? null)})</span>}</>,
                    ])} />
                  <div className="mt-1 text-[var(--fg-faint)]">
                    Gewinner {f.selectedParameters ? Object.entries(f.selectedParameters).map(([k, v]) => `${k}=${v}`).join(", ") : "–"} · Standard{" "}
                    {f.baseParameters ? Object.entries(f.baseParameters).map(([k, v]) => `${k}=${v}`).join(", ") : "–"} · Signale {f.testSignals ?? "–"} · Gebühren {money(f.testFees ?? null)}
                    {f.testOpenEntryFees != null && f.testOpenEntryFees !== 0 && <> (+ Einstiegsgebühr offen {money(f.testOpenEntryFees)})</>}
                    {f.openPositionAtTestEnd && <> · Gewinner: {f.openPositionAtTestEnd}</>}
                    {f.baseOpenPositionAtTestEnd && <> · Standard: {f.baseOpenPositionAtTestEnd}</>}
                  </div>
                </div>
              ))}
            </div>
          )}
        </>}
        {variant && variant.status !== "Completed" && <p className="text-[11px] text-[var(--gold)]">{variant.label}: {variant.reason}</p>}
      </>}
    </Section>
  );
}

// ---- 07 ----
function SimCard({ m }: { m: SimulationResult }) {
  return (
    <div className="rounded-md border border-[var(--line)] p-2 space-y-1">
      <div className="text-[12px] text-[var(--fg)] font-medium">{m.title}</div>
      <div className="text-[11px] text-[var(--fg-dim)]">{m.question}</div>
      <div className="text-[10px] text-[var(--fg-faint)]">{m.method} · {m.observations} × „{m.observationUnit}“ · {m.iterations} Wiederholungen · Seed {m.seed}</div>
      {m.unavailableReason ? <p className="text-[11px] text-[var(--gold)]">{m.unavailableReason}</p> : <>
        <div className="grid grid-cols-2 gap-2">
          {m.finalNet && <div><div className="text-[10px] text-[var(--fg-faint)]">End-Netto: P5 {money(m.finalNet.p5, 0)} · Median {money(m.finalNet.median, 0)} · P95 {money(m.finalNet.p95, 0)}</div>
            <Histogram ariaLabel={`${m.title}: End-Netto`} bins={m.finalNet.histogram} min={m.finalNet.histogramMin} max={m.finalNet.histogramMax} p5={m.finalNet.p5} median={m.finalNet.median} p95={m.finalNet.p95} /></div>}
          {m.maxDrawdown && <div><div className="text-[10px] text-[var(--fg-faint)]">Max. Drawdown: Median {money(m.maxDrawdown.median, 0)} · <b className="text-[var(--gold)]">P95 {money(m.maxDrawdown.p95, 0)}</b></div>
            <Histogram ariaLabel={`${m.title}: maximaler Drawdown`} color="#f0c35a" bins={m.maxDrawdown.histogram} min={m.maxDrawdown.histogramMin} max={m.maxDrawdown.histogramMax} p5={m.maxDrawdown.p5} median={m.maxDrawdown.median} p95={m.maxDrawdown.p95} /></div>}
        </div>
        <div className="flex flex-wrap gap-3 text-[10px] mono text-[var(--fg-dim)]">
          {m.losingStreak && <span>Verlustserie: Median {num(m.losingStreak.median, 0)} · P95 {num(m.losingStreak.p95, 0)}</span>}
          {m.shareLossPaths != null && <span>Pfade mit Verlust: {pct(m.shareLossPaths)}</span>}
          {m.finalNetInvariant != null && <span className={m.finalNetInvariant ? "text-[var(--key)]" : "text-[var(--red)]"}>End-Netto in allen Pfaden identisch: {m.finalNetInvariant ? "ja ✓" : "NEIN"}</span>}
        </div>
      </>}
    </div>
  );
}

function SimulationSection({ rep, step }: { rep: BacktestReport; step?: ResearchStepState }) {
  const s = rep.simulation;
  return (
    <Section id="simulation" step={step} title="07 Monte Carlo & Bootstrap" basis={s ? "Simulation — " + s.basis : null}>
      {s && <>
        <p className="text-[11px] text-[var(--fg-dim)]">{s.basisReason} {s.iterations} Wiederholungen je Verfahren, fester Seed {s.seed}.</p>
        <div className="grid gap-2 xl:grid-cols-2">{s.methods.map((m) => <SimCard key={m.key} m={m} />)}</div>
        <div className="text-[12px] text-[var(--fg)] font-medium pt-1">Ausführungsstress (klar benannte, hypothetische Szenarien)</div>
        <Tbl head={["Szenario", "Art", "Netto", "Max. DD", "Trades"]} rows={s.executionStress.map((r) => [r.label, r.kind, money(r.net), money(r.maxDrawdown), r.trades])} />
        {s.missedFills && <SimCard m={s.missedFills} />}
        {s.delayedFills && <StressTable block={s.delayedFills} title="Verzögerte Fills (Engine-Neulauf, Ausführung n Kerzen später) — Netto" />}
        <Notes items={s.notes} />
      </>}
    </Section>
  );
}

function StressTable({ block, title }: { block: QuantStressBlock; title: string }) {
  return (
    <div>
      <div className="mb-1 text-[11px] text-[var(--fg-dim)]">{title}</div>
      <Tbl head={["Szenario", "Gebühr×", "Slippage×", "Verzögerung", "Netto", "Trades"]} rows={block.cells.map((c) => [
        c.label, num(c.feeMultiplier), num(c.slippageMultiplier), c.delayBars, c.value == null ? <span className="text-[var(--fg-faint)]">{c.error ?? "–"}</span> : money(c.value), c.tradeCount,
      ])} />
    </div>
  );
}

// ---- 08 ----
function CostSection({ rep, step }: { rep: BacktestReport; step?: ResearchStepState }) {
  const c = rep.costs;
  return (
    <Section id="costs" step={step} title="08 Kosten-, Slippage- & Ausführungsstress" basis={c?.basis}>
      {c && <>
        <Grid>
          <Kv label="Ø Ergebnis je Trade vor Kosten" value={money(c.breakeven.preCostExpectancy ?? null)} />
          <Kv label="Ø modellierte Kosten je Trade" value={money(c.breakeven.costPerTrade ?? null)} hint="Gebühren + enthaltene Slippage" />
          <Kv label="Gewinnschwelle: Gesamtkosten je Round-Turn" value={rv(c.breakeven.breakevenCostPerTrade, (v) => money(v))} hint={rvHint(c.breakeven.breakevenCostPerTrade) ?? "ab hier fällt der historische Erwartungswert auf null"} />
          <Kv label="… in Ticks je Seite" value={rv(c.breakeven.breakevenTicksPerSide, (v) => num(v, 2))} hint={c.breakeven.headroom.value != null ? `Spielraum über heutige Kosten: ${money(c.breakeven.headroom.value)} je Trade` : rvHint(c.breakeven.headroom)} />
        </Grid>
        <Tbl head={["Vorab festgelegtes Szenario", "Netto", "Erwartung je Trade", "Gesamtkosten", ""]} rows={c.breakeven.scenarios.map((s) => [
          s.label, <span className={s.net >= 0 ? "text-[var(--key)]" : "text-[var(--red)]"}>{money(s.net)}</span>, money(s.expectancy ?? null), money(s.totalCosts), s.hypothetical ? <span className="text-[var(--fg-faint)]">hypothetisch</span> : "Basis",
        ])} />
        {c.costGrid && <StressTable block={c.costGrid} title="Engine-Neulauf mit skalierten Gebühren/Slippage (vorhandene Robustheitsprüfung) — Netto" />}
        <div className="rounded-md border border-[var(--line)] p-2 text-[11px] text-[var(--fg-dim)]">
          <b className="text-[var(--fg)]">OHLC-Mehrdeutigkeit:</b> {c.ambiguousTrades === 0 ? "keine Kerze mit gleichzeitig erreichbarem Stop und Ziel." :
            <>{c.ambiguousTrades} Trades mit Stop UND Ziel in derselben Kerze. Angenommen (Stop zuerst): {money(c.ambiguousAssumedNet)}; wäre stattdessen das Ziel zuerst gefüllt worden: {money(c.ambiguousAlternativeNet)} (Wirkung {money(c.ambiguousAlternativeNet - c.ambiguousAssumedNet)}).</>}
        </div>
        <Notes items={c.dataLimits} />
      </>}
    </Section>
  );
}

// ---- 09 ----
function BreakdownTable({ b, min }: { b: GroupBreakdown; min: number }) {
  return (
    <div className="rounded-md border border-[var(--line)] p-2">
      <div className="text-[12px] text-[var(--fg)] font-medium">{b.dimension}</div>
      <div className="text-[10px] text-[var(--fg-faint)] mb-1">{b.definition}</div>
      <Tbl head={["Gruppe", "Trades", "Netto", "Erwartung", "Treffer", "Tage", "Zeitraum", ""]} rows={b.rows.map((r) => [
        r.key, r.trades, <span className={r.net >= 0 ? "text-[var(--key)]" : "text-[var(--red)]"}>{money(r.net, 0)}</span>, money(r.expectancy ?? null), pct(r.winRate ?? null, 0), r.sessions,
        `${day(r.from)} – ${day(r.to)}`, r.thin ? <span className="text-[var(--gold)]" title={`weniger als ${min} Trades`}>dünn</span> : "",
      ])} />
    </div>
  );
}

function StabilitySection({ rep, step }: { rep: BacktestReport; step?: ResearchStepState }) {
  const s = rep.stability;
  const days = s?.breakdowns.find((b) => b.dimension === "Tag");
  return (
    <Section id="stability" step={step} title="09 Stabilität über Zeit & Marktbedingungen" basis={s?.basis}>
      {s && <>
        {days && <><div className="text-[11px] text-[var(--fg-dim)]">Netto je Handelstag</div><SessionBars ariaLabel="Netto je Handelstag" keys={days.rows.map((r) => r.key)} values={days.rows.map((r) => r.net)} /></>}
        {s.rolling.length > 1 && <>
          <div className="text-[11px] text-[var(--fg-dim)]">Rollierender Erwartungswert je Trade (Fenster {s.rollingWindow} Trades)</div>
          <LineChart ariaLabel="Rollierender Erwartungswert" height={120} zeroLine series={[{ name: "Erwartung", x: s.rolling.map((r) => r.t), y: s.rolling.map((r) => r.expectancy), color: "#22b8f0" }]} />
          <div className="text-[11px] text-[var(--fg-dim)]">Rollierender Drawdown (innerhalb der letzten {s.rollingWindow} Trades)</div>
          <LineChart ariaLabel="Rollierender Drawdown" height={110} series={[{ name: "Drawdown", x: s.rolling.map((r) => r.t), y: s.rolling.map((r) => -r.drawdown), color: "#f0566a", area: true }]} />
        </>}
        <div className="grid gap-2 xl:grid-cols-2">
          {s.breakdowns.filter((b) => b.dimension !== "Tag").map((b) => <BreakdownTable key={b.dimension} b={b} min={s.minTradesPerGroup} />)}
          {s.regimes.map((b) => <BreakdownTable key={b.dimension} b={b} min={s.minTradesPerGroup} />)}
        </div>
        {days && <details className="text-[11px]"><summary className="cursor-pointer text-[var(--fg-dim)]">Je Handelstag ({days.rows.length})</summary><BreakdownTable b={days} min={s.minTradesPerGroup} /></details>}
        <Notes items={s.notes} />
      </>}
    </Section>
  );
}

// ---- 10 ----
function ConcentrationSection({ rep, step }: { rep: BacktestReport; step?: ResearchStepState }) {
  const c = rep.concentration?.result;
  return (
    <Section id="concentration" step={step} title="10 Konzentration & Ausreißer" basis={rep.concentration?.basis}>
      {c && <>
        <Grid cols={6}>
          <Kv label="Unverändertes Original" value={money(c.totalNet)} tone={tone(c.totalNet)} />
          <Kv label="Anteil beste 5 Trades" value={rv(c.top5TradesShare, (v) => pct(v))} hint={rvHint(c.top5TradesShare) ?? money(c.top5TradesNet)} />
          <Kv label="Anteil bester Tag" value={rv(c.bestDayShare, (v) => pct(v))} hint={`${c.bestDay ?? "–"}: ${money(c.bestDayNet)}`} />
          <Kv label="Anteil beste Woche" value={rv(c.bestWeekShare, (v) => pct(v))} hint={`${c.bestWeek ?? "–"}: ${money(c.bestWeekNet)}`} />
          <Kv label="Ohne besten Tag" value={money(c.netWithoutBestDay)} tone={tone(c.netWithoutBestDay)} />
          <Kv label="Ohne beste 5 Trades" value={money(c.netWithoutTop5Trades)} tone={tone(c.netWithoutTop5Trades)} hint={`ohne beste Woche: ${money(c.netWithoutBestWeek)}`} />
        </Grid>
        {c.leaveOneOutAvailable ? <>
          <div className="text-[11px] text-[var(--fg-dim)]">Je ein Handelstag ausgelassen: Ergebnis zwischen {money(c.leaveOneOutMin ?? null)} und {money(c.leaveOneOutMax ?? null)};
            {" "}<b className={c.signFlippingDays > 0 ? "text-[var(--gold)]" : "text-[var(--fg)]"}>{c.signFlippingDays}</b> Tag(e), deren Weglassen das Ergebnis auf ≤ 0 bzw. ins Gegenteil kippt.</div>
          <SessionBars ariaLabel="Netto ohne den jeweiligen Handelstag" keys={c.leaveOneOut.map((r) => `ohne ${r.sessionKey}`)} values={c.leaveOneOut.map((r) => r.netWithout)} />
        </> : <p className="text-[11px] text-[var(--gold)]">Weglassen je Handelstag: {c.leaveOneOutReason}</p>}
      </>}
    </Section>
  );
}

// ---- 11 ----
function ControlSection({ rep, step }: { rep: BacktestReport; step?: ResearchStepState }) {
  const c = rep.controls;
  const of = c?.overfitting;
  return (
    <Section id="controls" step={step} title="11 Benchmarks, Kontrollläufe & Overfitting" basis={c?.basis}>
      {c && <>
        <Tbl head={["Vergleich", "Netto", "Trades", "Max. DD", "Basis", "Hinweis"]} rows={c.rows.map((r) => [
          r.label, r.net == null ? "–" : <span className={r.net >= 0 ? "text-[var(--key)]" : "text-[var(--red)]"}>{money(r.net)}</span>, r.trades ?? "–", money(r.maxDrawdown ?? null), r.basis,
          <span className="whitespace-normal font-sans text-[var(--fg-faint)]">{r.note ?? ""}</span>,
        ])} />
        {c.random && (c.random.available && c.random.net ? (
          <div className="rounded-md border border-[var(--line)] p-2 space-y-1">
            <div className="text-[12px] text-[var(--fg)] font-medium">Zufallskontrolle</div>
            <div className="text-[11px] text-[var(--fg-dim)]">{c.random.definition}</div>
            <div className="text-[10px] text-[var(--fg-faint)]">{c.random.iterations} Läufe · Seed {c.random.seed} · je {c.random.tradesPerRun} Trades · Anteil Zufallsläufe ≥ Strategie: <b className="text-[var(--fg)]">{pct(c.random.shareRandomAtLeastStrategy ?? null)}</b> (Einordnung, kein Signifikanztest)</div>
            <Histogram ariaLabel="Netto der Zufallskontrolle" bins={c.random.net.histogram} min={Math.min(c.random.net.histogramMin, c.random.strategyNet)} max={Math.max(c.random.net.histogramMax, c.random.strategyNet)}
              p5={c.random.net.p5} median={c.random.net.median} p95={c.random.net.p95} reference={c.random.strategyNet} referenceLabel="Strategie" />
          </div>
        ) : <p className="text-[11px] text-[var(--gold)]">Zufallskontrolle: {c.random.unavailableReason}</p>)}
        {c.trials && <div className="rounded-md border border-[var(--line)] p-2 text-[11px] text-[var(--fg-dim)]">
          <b className="text-[var(--fg)]">Versuchshistorie:</b> {c.trials.variantsTested} ausprobierte Varianten · {c.trials.selectionAttempts} Auswahlentscheidungen · {c.trials.registryTrials} Trials im Register
          <Notes items={c.trials.breakdown} />
        </div>}
        {of && <Grid cols={3}>
          <Kv label="Probability of Backtest Overfitting (CSCV)" value={of.pbo == null ? <span className="text-[11px] text-[var(--fg-faint)]">nicht ausgewiesen</span> : pct(of.pbo)} hint={of.pbo == null ? of.pboUnavailableReason : `${of.candidates} Kandidaten · ${of.blocks} Blöcke · ${of.observations} Perioden`} />
          <Kv label="Probabilistic Sharpe (OOS)" value={of.psr == null ? <span className="text-[11px] text-[var(--fg-faint)]">nicht ausgewiesen</span> : pct(of.psr)} hint={of.psrUnavailableReason ?? undefined} />
          <Kv label="Deflated Sharpe Ratio" value={of.dsr == null ? <span className="text-[11px] text-[var(--fg-faint)]">nicht ausgewiesen</span> : pct(of.dsr)} hint={of.dsrUnavailableReason ?? `Versuche: ${of.actualTrials}, effektiv ${num(of.effectiveTrials, 1)}`} />
        </Grid>}
        {c.benchmark && <p className="text-[11px] text-[var(--fg-dim)]">Benchmark {c.benchmark.name}: {c.benchmark.available ? `${c.benchmark.commonPeriods} gemeinsame Perioden` : c.benchmark.unavailableReason}</p>}
        <Notes items={c.notes} />
      </>}
    </Section>
  );
}

// ---- 12 ----
function TradeSection({ rep, step }: { rep: BacktestReport; step?: ResearchStepState }) {
  const t = rep.trades;
  const [focus, setFocus] = useState<number | null>(null);
  const [filter, setFilter] = useState<"all" | "win" | "loss" | "amb">("all");
  const measured = (t?.trades ?? []).some((r) => r.excursionMeasured);
  const rows = (t?.trades ?? []).filter((r) => filter === "all" || (filter === "win" ? r.net > 0 : filter === "loss" ? r.net < 0 : r.ambiguous));
  return (
    <Section id="trades" step={step} title={`12 Einzeltrade-Prüfung — Journal, Replay, MAE/MFE-${(t?.trades ?? []).some((r) => r.excursionMeasured) ? "Messung aus Ticks" : "Schätzung"}`} basis={t?.basis}>
      {t && <>
        {measured
          ? <p className="text-[11px] text-[var(--key)]">MAE/MFE GEMESSEN aus allen Trade-Ticks zwischen Ein- und Ausstieg.</p>
          : <p className="text-[11px] text-[var(--gold)]">MAE/MFE sind eine SCHÄTZUNG aus {rep.provenance.timeframeMinutes}-Minuten-Kerzen, nicht tickgenau.</p>}
        <p className="text-[11px] text-[var(--fg-dim)]">{t.excursionDefinition.replace(/^SCHÄTZUNG aus \d+-Minuten-Kerzen \(nicht tickgenau\)\. /, "")}</p>
        <p className="text-[10px] text-[var(--fg-faint)]">{t.tickReplayNote}</p>
        {focus != null && t.candleRequest && (
          <TradeReplay request={t.candleRequest} trades={t.trades} focus={focus} pointValue={rep.provenance.costModel?.pointValue ?? 1} onClose={() => setFocus(null)} />
        )}
        <div className="flex gap-1 text-[11px]">
          {(["all", "win", "loss", "amb"] as const).map((f) => (
            <button key={f} onClick={() => setFilter(f)} className={cn("rounded border px-2 py-0.5", filter === f ? "border-[var(--cyan)] text-[var(--fg)]" : "border-[var(--line)] text-[var(--fg-dim)]")}>
              {f === "all" ? `Alle (${t.trades.length})` : f === "win" ? "Gewinner" : f === "loss" ? "Verlierer" : "Mehrdeutig"}
            </button>
          ))}
        </div>
        <div className="max-h-[420px] overflow-y-auto">
          <Tbl head={["#", "Tag", "Seite", "Einstieg (UTC)", "Ausstieg", "Grund", "Netto", measured ? "MAE (gemessen)" : "MAE (Schätzung)", measured ? "MFE (gemessen)" : "MFE (Schätzung)", ""]} rows={rows.slice(0, 1500).map((r) => [
            r.index + 1, r.session, r.side,
            <span title={r.entrySignalUtc ? `Signal ${r.entrySignalDirection} ${r.entrySignalUtc} · Fill ${r.entryTimeUtc}` : ""}>{dt(r.entryT)}</span>,
            <span title={r.exitSignalUtc ? `Signal ${r.exitSignalDirection} ${r.exitSignalUtc} · Fill ${r.exitTimeUtc}` : ""}>{dt(r.exitT)}</span>, r.exitReason + (r.ambiguous ? " ⚠" : "") + (r.closedAtEnd ? " (bewertet)" : ""),
            <span className={r.net >= 0 ? "text-[var(--key)]" : "text-[var(--red)]"}>{money(r.net)}</span>,
            <span title={r.excursionBasis}>{money(r.mae ?? null)}{r.excursionLowerBound ? "≥" : ""}</span>, <span title={r.excursionBasis}>{money(r.mfe ?? null)}{r.excursionLowerBound ? "≥" : ""}</span>,
            <button className="rounded border border-[var(--line-2)] px-1.5 text-[var(--cyan)]" onClick={() => setFocus(r.index)}>Im Replay ansehen</button>,
          ])} />
        </div>
        <p className="text-[10px] text-[var(--fg-faint)]">„≥“ = Untergrenze (Ausstiegskerze nur teilweise gehalten). ⚠ = Stop und Ziel in derselben Kerze, konservativ als Stop gewertet.</p>
      </>}
    </Section>
  );
}

function HoldoutSection({ run, step, onOpen }: { run: ReportRun; step?: ResearchStepState; onOpen: () => void }) {
  const p = run.holdoutProposal;
  return (
    <Section id="holdout" step={step} title="Finaler Holdout – unbenutzt (reserviert für die bewusste Schlussprüfung)" basis="nicht Teil dieses Berichts" defaultOpen={false}>
      <p className="text-[11px] text-[var(--fg-dim)]">
        {run.holdoutFrom ? <>Zeitraum {dt(run.holdoutFrom)} – {dt(run.holdoutTo)} UTC ({run.holdoutBars} Kerzen) wurde in keinem Abschnitt dieses Berichts verwendet.</> : "Kein Holdout reserviert."}
        {" "}Die Auswertung verbraucht ihn unwiderruflich und ist nur für eine eingefrorene Strategieversion gedacht.
      </p>
      {p && <p className="text-[11px] text-[var(--fg-dim)]">Vorbereiteter Kandidat: {p.candidateReference ?? "–"} — {p.reason || p.unavailableReason}</p>}
      {p?.available && <button onClick={onOpen} className="h-7 rounded-md border border-[var(--gold)] px-3 text-[11px] text-[var(--gold)]">Zur Schlussprüfung (Expertenansicht, mit Bestätigung)</button>}
    </Section>
  );
}
