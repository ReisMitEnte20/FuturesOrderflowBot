// Multi-Timeframe-Lauf (5m / 1m / rangeXV gleichzeitig) im Backtesting-Tab:
//   Lauf starten: Strategie → Datensatz/Zeitraum → London/New York/beide → Datenprüfung → „Backtest starten“.
//   Ergebnis: Lauftyp (Funktionstest/Demonstration), Kennzahlen, Datenprüfung, Journal mit Entscheidungsspur, Beinahe-Einstiege.
//   Trade nachspielen: öffnet die gespeicherte Aufnahme/Einstellungen genau dieses Trades (kein neuer Backtest).
import { useEffect, useMemo, useState } from "react";
import { cn } from "@/lib/utils";
import { mtfApi, tradeStats, type MtfDataCheck, type MtfRunIndexEntry, type MtfRunRequest, type MtfRunResult, type DecisionTrace } from "@/lib/mtfApi";
import { MtfTradeReplay } from "./MtfTradeReplay";

const DEFAULT: MtfRunRequest = { dataset: "synthetic", seed: 7, minutes: 120, day: "2026-11-17", sessions: "NewYork", strategy: "mtf-funktionstest",
  oneMinuteDeltaMin: 20, rangePeriod: 4, demoWithGenerated: false, initialBalance: 10000, closeOutMinutes: 5 };
const money = (v: number | null | undefined) => v == null ? "–" : v.toLocaleString("de-DE", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
const pnl = (v: number) => (v > 0 ? "text-[var(--key)]" : v < 0 ? "text-[var(--red)]" : "");
const field = "h-[30px] rounded-md border border-[var(--line-2)] bg-[var(--bg-2)] px-2 text-xs text-[var(--fg)]";
const btn = "h-[28px] rounded-md border border-[var(--line-2)] px-2.5 text-xs text-[var(--fg)] disabled:opacity-40";
const STATUS_COLOR: Record<string, string> = { ok: "var(--key)", nur_demonstration: "var(--gold)", blockiert: "var(--red)" };

export function MtfBacktest({ runId, trade, onOpen, onTrade }: {
  runId: string | null; trade: number | null; onOpen: (id: string | null) => void; onTrade: (t: number | null) => void;
}) {
  const [form, setForm] = useState<MtfRunRequest>(DEFAULT);
  const [check, setCheck] = useState<MtfDataCheck | null>(null);
  const [checkErr, setCheckErr] = useState<string | null>(null);
  const [running, setRunning] = useState(false);
  const [err, setErr] = useState<string | null>(null);
  const [run, setRun] = useState<MtfRunResult | null>(null);
  const [runs, setRuns] = useState<MtfRunIndexEntry[]>([]);
  const set = (p: Partial<MtfRunRequest>) => setForm((f) => ({ ...f, ...p }));

  useEffect(() => { mtfApi.runs().then(setRuns).catch(() => setRuns([])); }, [runId]);
  useEffect(() => {
    const ac = new AbortController();
    setCheck(null); setCheckErr(null);
    const t = setTimeout(() => mtfApi.check(form, ac.signal).then(setCheck).catch((e) => !ac.signal.aborted && setCheckErr(String(e?.message ?? e))), 250);
    return () => { clearTimeout(t); ac.abort(); };
  }, [form.dataset, form.seed, form.minutes, form.day, form.sessions, form.strategy, form.oneMinuteDeltaMin]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    setRun(null);
    if (!runId) return;
    mtfApi.get(runId).then(setRun).catch((e) => setErr(String(e?.message ?? e)));
  }, [runId]);

  const start = async () => {
    setRunning(true); setErr(null);
    try { const r = await mtfApi.run(form); onOpen(r.runId); }
    catch (e: any) { setErr(String(e?.message ?? e)); }
    finally { setRunning(false); }
  };
  const blocked = !check?.runnable || (check?.requiresDemoFlag && !form.demoWithGenerated);

  if (run && trade != null) return <MtfTradeReplay run={run} trade={trade} onClose={() => onTrade(null)} onTrade={onTrade} />;

  return (
    <div className="space-y-3 p-4" data-testid="mtf-backtest">
      {!run && (
        <section className="mtf-fade rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3 space-y-2" data-testid="mtf-start">
          <div className="flex flex-wrap items-end gap-2 text-[11px]">
            <label className="flex flex-col gap-0.5"><span className="text-[var(--fg-faint)]">1 · Strategie</span>
              <select className={field} value={form.strategy} onChange={(e) => set({ strategy: e.target.value })} data-testid="mtf-strategy">
                <option value="mtf-funktionstest">MTF-Funktionstest (technisch, keine Handelsstrategie)</option>
              </select></label>
            <label className="flex flex-col gap-0.5"><span className="text-[var(--fg-faint)]">2 · Datensatz</span>
              <select className={field} value={form.dataset} onChange={(e) => set({ dataset: e.target.value as MtfRunRequest["dataset"], demoWithGenerated: false })} data-testid="mtf-dataset">
                <option value="synthetic">Synthetisch (Funktionstest)</option><option value="recording">ATAS-Aufnahme (Collections)</option>
              </select></label>
            {form.dataset === "synthetic" ? <>
              <label className="flex flex-col gap-0.5"><span className="text-[var(--fg-faint)]">Seed</span><input className={cn(field, "w-20")} type="number" value={form.seed} onChange={(e) => set({ seed: +e.target.value })} data-testid="mtf-seed" /></label>
              <label className="flex flex-col gap-0.5"><span className="text-[var(--fg-faint)]">Minuten</span><input className={cn(field, "w-20")} type="number" value={form.minutes} onChange={(e) => set({ minutes: +e.target.value })} /></label>
            </> : <>
              <label className="flex flex-col gap-0.5"><span className="text-[var(--fg-faint)]">Handelstag</span><input className={field} type="date" value={form.day ?? ""} onChange={(e) => set({ day: e.target.value })} data-testid="mtf-day" /></label>
              <label className="flex flex-col gap-0.5"><span className="text-[var(--fg-faint)]">3 · Session</span>
                <select className={field} value={form.sessions} onChange={(e) => set({ sessions: e.target.value as MtfRunRequest["sessions"] })} data-testid="mtf-sessions">
                  <option value="London">London</option><option value="NewYork">New York</option><option value="both">London + New York</option>
                </select></label>
            </>}
            <button className="h-[34px] rounded-md bg-grad-brand px-4 text-[13px] font-semibold text-[var(--bg)] disabled:opacity-40" onClick={start} disabled={running || !check || !!blocked} data-testid="mtf-start-btn">
              {running ? "Läuft … (Ereignisse werden verarbeitet)" : "Backtest starten"}</button>
          </div>
          <details className="text-[11px]"><summary className="cursor-pointer text-[var(--fg-dim)]">Erweiterte Parameter</summary>
            <div className="mt-1 flex flex-wrap gap-2">
              <label className="flex items-center gap-1">1m-Delta-Schwelle <input className={cn(field, "w-20")} type="number" value={form.oneMinuteDeltaMin} onChange={(e) => set({ oneMinuteDeltaMin: +e.target.value })} /></label>
              <label className="flex items-center gap-1">rangeXV-Periode <input className={cn(field, "w-16")} type="number" value={form.rangePeriod} onChange={(e) => set({ rangePeriod: +e.target.value })} /></label>
              <label className="flex items-center gap-1">Startkapital <input className={cn(field, "w-24")} type="number" value={form.initialBalance} onChange={(e) => set({ initialBalance: +e.target.value })} /></label>
            </div></details>
          {err && <p className="text-xs text-[var(--red)]">{err}</p>}
          <DataCheckPanel check={check} err={checkErr} demo={form.demoWithGenerated} onDemo={(v) => set({ demoWithGenerated: v })} />
        </section>
      )}
      {run && <MtfReport run={run} onClose={() => onOpen(null)} onTrade={onTrade} />}
      {!run && runs.length > 0 && (
        <section className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3 text-[11px]">
          <b className="text-[var(--fg)]">Gespeicherte Multi-Timeframe-Läufe</b>
          <table className="mt-1 w-full font-mono"><tbody>{runs.slice(0, 12).map((r) => (
            <tr key={r.runId}><td className="pr-2">{r.runId}</td><td className="pr-2">{r.dataset}</td><td className="pr-2">{r.runKind}</td><td className="pr-2">{r.trades} Trades</td>
              <td className={cn("pr-2", pnl(r.netPnL))}>{money(r.netPnL)}</td><td><button className={btn} onClick={() => onOpen(r.runId)}>öffnen</button></td></tr>))}</tbody></table>
        </section>
      )}
    </div>
  );
}

function DataCheckPanel({ check, err, demo, onDemo }: { check: MtfDataCheck | null; err: string | null; demo: boolean; onDemo: (v: boolean) => void }) {
  if (err) return <p className="text-xs text-[var(--red)]" data-testid="mtf-check-error">Datenprüfung: {err}</p>;
  if (!check) return <p className="text-[11px] text-[var(--fg-dim)]">Datenprüfung läuft …</p>;
  return (
    <div className="space-y-1.5 rounded-md border border-[var(--line)] bg-[var(--panel-3)] p-2 text-[11px]" data-testid="mtf-datacheck" data-runnable={check.runnable} data-demo={check.requiresDemoFlag}>
      <div className="flex flex-wrap items-center gap-2"><b className="text-[var(--fg)]">4 · Datenprüfung: {check.label}</b>
        <span style={{ color: check.runnable ? "var(--key)" : "var(--red)" }}>{check.runnable ? (check.requiresDemoFlag ? "nur als Demonstration startbar" : "startbar") : "nicht startbar"}</span></div>
      {check.blockers.map((b) => <div key={b} className="text-[var(--red)]">⚠ {b}</div>)}
      <ul className="space-y-0.5">{check.statements.map((s) => <li key={s.category + s.text}><span className="mr-1 rounded border border-[var(--line-2)] px-1 text-[10px] text-[var(--fg-faint)]">{s.category}</span>{s.text}</li>)}</ul>
      <div className="flex flex-wrap gap-x-4 gap-y-0.5">{check.requirements.map((r) => <span key={r.need} style={{ color: STATUS_COLOR[r.status] }}>{r.text}</span>)}</div>
      {check.segments.map((s) => <div key={s.id + s.session} className="text-[var(--fg-dim)]">{s.session} {s.day}: {s.recordingId ?? "synthetisch"} · {s.provenance} · {s.events.toLocaleString("de-DE")} Ereignisse{s.drops ? ` · ⚠ ${s.drops} Drops` : ""}{s.conflict ? ` · ⚠ ${s.conflict}` : ""}</div>)}
      {check.requiresDemoFlag && <label className="flex items-center gap-1.5 text-[var(--gold)]"><input type="checkbox" checked={demo} onChange={(e) => onDemo(e.target.checked)} data-testid="mtf-demo" />
        Ich starte ausdrücklich einen Demonstrationslauf auf generierten Replay-Ereignissen (bleibt im Bericht als solcher erkennbar).</label>}
    </div>
  );
}

export function MtfReport({ run, onClose, onTrade }: { run: MtfRunResult; onClose: () => void; onTrade: (t: number) => void }) {
  const st = useMemo(() => tradeStats(run.trades), [run]);
  const rejected = run.traces.filter((t) => t.kind === "kandidat_abgelehnt");
  const [sel, setSel] = useState<number | null>(null);
  return (
    <div className="mtf-fade space-y-3" data-testid="mtf-report" data-run={run.runId}>
      <section className="rounded-lg border bg-[var(--panel)] p-3 space-y-2" style={{ borderColor: run.isDemonstration ? "var(--gold)" : "var(--line)" }}>
        <div className="flex flex-wrap items-center gap-2">
          <b className="text-[13px] text-[var(--fg)]">{run.strategy} · {run.dataCheck?.label}</b>
          <span className="rounded border px-1.5 text-[11px] font-semibold" style={{ borderColor: "var(--gold)", color: "var(--gold)" }} data-testid="mtf-runkind">{run.runKind}</span>
          <span className="text-[11px] text-[var(--fg-faint)]">Lauf {run.runId} · gestartet {run.createdUtc.slice(0, 19).replace("T", " ")} UTC · zeigt die gespeicherten Einstellungen dieses Laufs</span>
          <button className={cn(btn, "ml-auto")} onClick={onClose}>Neuer Lauf</button>
        </div>
        <div className="grid grid-cols-2 gap-2 text-[11px] sm:grid-cols-6" data-testid="mtf-kpis">
          {[["Netto nach Kosten", money(run.netPnL), pnl(run.netPnL)], ["Trades", String(st.trades), ""], ["Trefferquote", st.winRate == null ? "–" : `${(100 * st.winRate).toFixed(1)} %`, ""],
            ["Profit Factor", st.profitFactor == null ? "nicht definiert" : st.profitFactor.toFixed(2), ""], ["Max. Drawdown (geschl.)", money(run.maxDrawdown), ""], ["Ereignisse", run.events.toLocaleString("de-DE"), ""]].map(([k, v, c]) => (
            <div key={k} className="rounded-md border border-[var(--line)] bg-[var(--panel-3)] p-2"><div className="text-[var(--fg-faint)]">{k}</div><div className={cn("font-mono text-[14px] text-[var(--fg)]", c)}>{v}</div></div>))}
        </div>
        <div className="text-[10px] text-[var(--fg-faint)]">Brutto {money(run.grossPnL)} · Gebühren {money(run.fees)} ({money(run.feePerSide)} je Seite, Slippage {run.slippageTicks} Tick) · Position am Ende {run.finalPosition}{run.openValuation ? ` (offene Bewertung ${money(run.openValuation)}, nicht gebucht)` : ""} · Fingerabdruck {run.fingerprint}</div>
        <details className="text-[11px]"><summary className="cursor-pointer text-[var(--fg-dim)]">Datenqualität, Verarbeitungsreihenfolge, Parameter</summary>
          <div className="mt-1 space-y-1 text-[var(--fg-dim)]">
            {run.dataCheck?.statements.map((s) => <div key={s.text}><b>{s.category}:</b> {s.text}</div>)}
            <div><b>Reihenfolge:</b> {run.processingOrder}</div>
            <div><b>Parameter ({run.strategyVersion}):</b> {Object.entries(run.parameters).map(([k, v]) => `${k} = ${v}`).join(" · ")}</div>
            {run.notes.map((n) => <div key={n}>{n}</div>)}
            <div className="text-[var(--gold)]">Robustheitsprüfungen (Walk-forward, Monte Carlo, Kostenstress …) sind für Multi-Timeframe-Läufe noch nicht angebunden.</div>
          </div></details>
      </section>

      <section className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3 text-[11px]" data-testid="mtf-journal">
        <b className="text-[12px] text-[var(--fg)]">Journal ({run.trades.length} Trades) – Erklärung aus der gespeicherten Entscheidungsspur</b>
        <div className="mt-1 max-h-[420px] overflow-y-auto">
          <table className="w-full font-mono"><thead className="sticky top-0 bg-[var(--panel)] text-left text-[var(--fg-faint)]"><tr>{["#", "Seite", "Einstieg", "Ausstieg", "Netto", "Signal → Fill (Ereignis)", ""].map((h) => <th key={h} className="pr-2 font-normal">{h}</th>)}</tr></thead>
            <tbody>{run.trades.map((t) => (
              <tr key={t.index} onClick={() => setSel(t.index === sel ? null : t.index)} className={cn("mtf-row cursor-pointer align-top", sel === t.index && "mtf-row-selected")} data-testid={`mtf-trade-${t.index}`}>
                <td className="pr-2">{t.index + 1}</td><td className="pr-2">{t.side}</td>
                <td className="pr-2">{t.entryTimeUtc.slice(11, 23)} @ {t.entryPrice}</td><td className="pr-2">{t.exitTimeUtc.slice(11, 23)} @ {t.exitPrice}</td>
                <td className={cn("pr-2", pnl(t.netPnL))}>{money(t.netPnL)}</td>
                <td className="pr-2">{t.signalEvent ?? "–"} → {t.entryFillEvent ?? "–"} · {t.exitSignalEvent ?? "–"} → {t.exitFillEvent ?? "–"}</td>
                <td><button className={cn(btn, "border-[var(--cyan)]")} disabled={t.signalEvent == null} title={t.signalEvent == null ? "damals nicht aufgezeichnet" : ""}
                  onClick={(e) => { e.stopPropagation(); onTrade(t.index); }} data-testid={`mtf-replay-${t.index}`}>Trade nachspielen</button></td>
              </tr>))}</tbody></table>
        </div>
        {sel != null && run.trades[sel] && <div className="mt-2 rounded-md border border-[var(--cyan)] p-2 text-[var(--fg-dim)]" data-testid="mtf-explanation">{run.trades[sel].explanation}</div>}
      </section>

      <section className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3 text-[11px]" data-testid="mtf-rejected">
        <b className="text-[12px] text-[var(--fg)]">Beinahe-Einstiege ({rejected.length}) – Trigger erfüllt, abgelehnt mit dem tatsächlichen Grund</b>
        <div className="mt-1 max-h-[220px] overflow-y-auto space-y-0.5">{rejected.slice(0, 50).map((r) => <RejectedLine key={r.id} r={r} />)}</div>
      </section>
    </div>
  );
}

function RejectedLine({ r }: { r: DecisionTrace }) {
  return <div className="font-mono text-[var(--fg-dim)]">#{r.eventIndex} {r.marketTime.slice(11, 23)} · {r.checks.filter((c) => c.result !== "erfüllt").map((c) => `${c.rule}: ${c.result} (${c.actual} | Soll ${c.expected})`).join("; ")}</div>;
}
