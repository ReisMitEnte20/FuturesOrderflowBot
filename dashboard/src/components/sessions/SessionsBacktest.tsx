// Backtesting → „London + New York“: Zeitraum wählen → Plan prüfen → EIN Start → derselbe Bot (eine Strategieinstanz,
// ein Konto, ein Lauf der bestehenden Tick-Engine) läuft durch London, springt ohne Wartezeit über die Pause nach New York
// und weiter durch alle vollständigen Handelstage. Bericht getrennt nach London / New York / Gesamt, Übergänge mit Beleg
// (Kontostand vor/nach dem Sprung, keine Trades im Sprung), Journal mit Sprung in „Chart & Replay“.
// Simulation-only: keine Orders, keine Broker-Anbindung, keine Live-Controls.
import { useEffect, useMemo, useState, type ReactNode } from "react";
import { useNavigate } from "react-router";
import { cn } from "@/lib/utils";
import { money, num } from "@/lib/backtestingApi";
import { sessionsApi, type PlannedDay, type SessionPlan, type SessionRunIndexEntry, type SessionRunResult } from "@/lib/sessionsApi";

const iso = (d: Date) => d.toISOString().slice(0, 10);
const t7 = (s: string | null | undefined) => (s ? s.replace("T", " ").replace(/(\.\d{3})\d*Z$/, "$1 UTC").replace(/Z$/, " UTC") : "–");
const DAY_STATUS: Record<PlannedDay["status"], { text: string; cls: string }> = {
  complete: { text: "vollständig", cls: "border-[var(--key)] text-[var(--key)]" },
  incomplete: { text: "unvollständig – ausgelassen", cls: "border-[var(--gold)] text-[var(--gold)]" },
  "no-data": { text: "keine Daten – ausgelassen", cls: "border-[var(--line-2)] text-[var(--fg-dim)]" },
  ambiguous: { text: "nicht eindeutig – ausgelassen", cls: "border-[var(--red)] text-[var(--red)]" },
};
const SES_STATUS: Record<string, string> = { ok: "ok", missing: "fehlt", defective: "fehlerhaft", conflict: "Konflikt" };
const inp = "h-[28px] rounded-md border border-[var(--line-2)] bg-[var(--bg-2)] px-2 text-xs text-[var(--fg)]";
const btn = "h-[28px] px-2.5 rounded-md border border-[var(--line-2)] text-[var(--fg)] text-xs disabled:opacity-40 hover:border-[var(--cyan)]";
const btnOn = "h-[30px] px-3.5 rounded-md bg-grad-brand text-[var(--bg)] text-[13px] font-semibold disabled:opacity-40";
const pnlCls = (v: number) => (v > 0 ? "text-[var(--key)]" : v < 0 ? "text-[var(--red)]" : "text-[var(--fg)]");

export function SessionsBacktest({ runId, onRun }: { runId: string | null; onRun: (id: string | null) => void }) {
  const navigate = useNavigate();
  const yesterday = useMemo(() => { const d = new Date(); d.setUTCDate(d.getUTCDate() - 1); return iso(d); }, []);
  const [from, setFrom] = useState(yesterday);
  const [to, setTo] = useState(yesterday);
  const [tpb, setTpb] = useState(500);
  const [thr, setThr] = useState(30);
  const [bal, setBal] = useState(10_000);
  const [mdl, setMdl] = useState<string>("");
  const [closeOut, setCloseOut] = useState(5);
  const [fees, setFees] = useState(true);
  const [plan, setPlan] = useState<SessionPlan | null>(null);
  const [planErr, setPlanErr] = useState<string | null>(null);
  const [running, setRunning] = useState(false);
  const [runErr, setRunErr] = useState<string | null>(null);
  const [run, setRun] = useState<SessionRunResult | null>(null);
  const [runs, setRuns] = useState<SessionRunIndexEntry[]>([]);

  const loadRuns = () => sessionsApi.runs().then(setRuns).catch(() => setRuns([]));
  useEffect(() => { loadRuns(); }, []);
  useEffect(() => {
    if (!runId) { setRun(null); return; }
    if (run?.runId === runId) return;
    const ac = new AbortController();
    sessionsApi.get(runId, ac.signal).then((r) => { setRun(r); setFrom(r.request.from); setTo(r.request.to); }).catch((e) => !ac.signal.aborted && setRunErr(String(e?.message ?? e)));
    return () => ac.abort();
  }, [runId]); // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (!from || !to) return;
    const ac = new AbortController();
    setPlanErr(null);
    sessionsApi.plan(from, to, closeOut, ac.signal).then(setPlan).catch((e) => { if (!ac.signal.aborted) { setPlan(null); setPlanErr(String(e?.message ?? e)); } });
    return () => ac.abort();
  }, [from, to, closeOut]);

  const start = async () => {
    setRunning(true); setRunErr(null);
    try {
      const r = await sessionsApi.run({ from, to, ticksPerBar: tpb, deltaThreshold: thr, initialBalance: bal, applyFees: fees,
        maxDailyLoss: mdl.trim() ? Number(mdl.replace(",", ".")) : null, closeOutMinutes: closeOut });
      setRun(r); onRun(r.runId); loadRuns();
    } catch (e: any) { setRunErr(String(e?.message ?? e)); }
    finally { setRunning(false); }
  };
  const openChart = (day?: string, trade?: number) => {
    if (!run) return;
    const q = new URLSearchParams({ view: "chart", mode: "sessions", srun: run.runId });
    if (day) q.set("day", day);
    if (trade != null) q.set("trade", String(trade));
    navigate(`/backtesting?${q}`);
  };

  return (
    <div className="p-4 space-y-3" data-testid="sessions-backtest">
      <section className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3 space-y-2">
        <div className="flex flex-wrap items-baseline gap-2">
          <b className="text-[13px] text-[var(--fg)]">London + New York</b>
          <span className="text-[11px] text-[var(--fg-dim)]">Ein Start: derselbe Bot läuft durch London, überspringt die Pause ohne Wartezeit, handelt in New York weiter und dann durch den Zeitraum. Ein Konto, eine Strategieinstanz, bestehende Tick-Engine.</span>
        </div>
        <div className="flex flex-wrap items-end gap-3 text-[11px] text-[var(--fg-dim)]">
          <label className="flex flex-col gap-0.5">Von<input type="date" className={inp} value={from} onChange={(e) => setFrom(e.target.value)} data-testid="ln-from" /></label>
          <label className="flex flex-col gap-0.5">Bis<input type="date" className={inp} value={to} onChange={(e) => setTo(e.target.value)} data-testid="ln-to" /></label>
          <label className="flex flex-col gap-0.5">Strategie<select className={inp} disabled><option>DeltaFlow (Orderflow, Ticks)</option></select></label>
          <label className="flex flex-col gap-0.5">Ticks/Bar<input type="number" min={10} className={cn(inp, "w-[80px]")} value={tpb} onChange={(e) => setTpb(Number(e.target.value))} /></label>
          <label className="flex flex-col gap-0.5">Delta-Schwelle<input type="number" className={cn(inp, "w-[80px]")} value={thr} onChange={(e) => setThr(Number(e.target.value))} /></label>
          <label className="flex flex-col gap-0.5">Startkapital<input type="number" className={cn(inp, "w-[96px]")} value={bal} onChange={(e) => setBal(Number(e.target.value))} /></label>
          <label className="flex flex-col gap-0.5">Tagesverlustlimit (USD)<input placeholder="Risikoprofil" className={cn(inp, "w-[110px]")} value={mdl} onChange={(e) => setMdl(e.target.value)} data-testid="ln-mdl" /></label>
          <label className="flex flex-col gap-0.5">Close-Out vor Sessionende (min)<input type="number" min={1} max={60} className={cn(inp, "w-[70px]")} value={closeOut} onChange={(e) => setCloseOut(Number(e.target.value))} /></label>
          <label className="flex items-center gap-1"><input type="checkbox" checked={fees} onChange={(e) => setFees(e.target.checked)} /> Gebühren</label>
          <button className={btnOn} disabled={running || !plan || plan.completeDays === 0} onClick={start} data-testid="ln-start">{running ? "Läuft …" : "▶ Start"}</button>
        </div>
        {planErr && <p className="text-xs text-[var(--red)]">{planErr}</p>}
        {runErr && <p className="text-xs text-[var(--red)]" data-testid="ln-error">{runErr}</p>}
        {running && <p className="text-[11px] text-[var(--fg-dim)]">Lauf rechnet: Sessions werden tageweise aus beiden Datenordnern gelesen (beim ersten Mal je Session einmaliger Cache-Aufbau), die Engine verarbeitet jeden Tick ohne Wartezeit.</p>}
        {plan && <PlanTable plan={plan} />}
      </section>

      {run && <RunReport run={run} onOpenChart={openChart} />}

      <section className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3 text-[11px]">
        <b className="text-[12px] text-[var(--fg)]">Gespeicherte Läufe</b> <span className="text-[var(--fg-faint)]">(lokal unter artifacts/sessions-runs)</span>
        {!runs.length ? <p className="text-[var(--fg-dim)]">Noch keine.</p> : (
          <table className="mt-1 w-full font-mono">
            <thead><tr className="text-left text-[var(--fg-faint)]">{["Lauf", "Zeitraum", "Tage", "Trades", "Netto", "Status", ""].map((h) => <th key={h} className="pr-3 font-normal">{h}</th>)}</tr></thead>
            <tbody>{runs.map((r) => (
              <tr key={r.runId} className={r.runId === run?.runId ? "text-[var(--fg)]" : "text-[var(--fg-dim)]"}>
                <td className="pr-3">{r.runId}</td><td className="pr-3">{r.from} – {r.to}</td><td className="pr-3">{r.days}</td><td className="pr-3">{r.trades}</td>
                <td className={cn("pr-3", pnlCls(r.netPnL))}>{money(r.netPnL)}</td><td className="pr-3">{r.status}</td>
                <td><button className={btn} onClick={() => onRun(r.runId)}>öffnen</button></td>
              </tr>))}</tbody>
          </table>
        )}
      </section>
    </div>
  );
}

function PlanTable({ plan }: { plan: SessionPlan }) {
  return (
    <div className="space-y-1" data-testid="ln-plan">
      <div className="text-[11px] text-[var(--fg-dim)]">
        Plan „{plan.schedule}“ ({plan.schedulePath}) · {plan.completeDays} vollständige Handelstage, {plan.excludedDays} ausgelassen · Close-Out {plan.closeOutMinutes} min vor Sessionende
      </div>
      <div className="max-h-[260px] overflow-y-auto">
        <table className="w-full text-[11px]">
          <thead><tr className="text-left text-[var(--fg-faint)]">{["Tag", "Status", "London", "New York", "geplante Pause", "Grund"].map((h) => <th key={h} className="pr-3 font-normal">{h}</th>)}</tr></thead>
          <tbody>{plan.days.map((d) => (
            <tr key={d.day} className="align-top" data-testid={`ln-plan-${d.day}`} data-status={d.status}>
              <td className="pr-3 font-mono text-[var(--fg)]">{d.day}</td>
              <td className="pr-3"><span className={cn("rounded border px-1.5 py-0.5 text-[10px] whitespace-nowrap", DAY_STATUS[d.status].cls)}>{DAY_STATUS[d.status].text}</span></td>
              {["London", "NewYork"].map((k) => {
                const s = d.sessions.find((x) => x.key === k);
                return <td key={k} className="pr-3 font-mono">
                  {!s ? "–" : <>
                    <span className={s.status === "ok" ? "text-[var(--key)]" : s.status === "missing" ? "text-[var(--gold)]" : "text-[var(--red)]"}>{SES_STATUS[s.status]}</span>{" "}
                    <span className="text-[var(--fg-dim)]">{(s.windowStartUtc ?? s.plannedStartUtc).slice(11, 16)}–{(s.windowEndUtc ?? s.plannedEndUtc).slice(11, 16)} UTC</span>
                    {s.recordingId && <div className="text-[10px] text-[var(--fg-faint)]">{s.recordingId} · Drops {s.recorderDrops ?? "?"}</div>}
                  </>}
                </td>;
              })}
              <td className="pr-3 font-mono text-[var(--fg-dim)]">{d.plannedPauseMinutes != null ? `${Math.floor(d.plannedPauseMinutes / 60)} h ${d.plannedPauseMinutes % 60} min` : "–"}</td>
              <td className="text-[var(--fg-dim)]">{d.reasons.join(" ")}{d.overlapNote && d.status === "complete" ? ` ${d.overlapNote}` : ""}</td>
            </tr>
          ))}</tbody>
        </table>
      </div>
    </div>
  );
}

function Box({ title, children, testid }: { title: string; children: ReactNode; testid?: string }) {
  return (
    <section className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3 space-y-1.5 text-[11px]" data-testid={testid}>
      <b className="text-[12px] text-[var(--fg)]">{title}</b>
      {children}
    </section>
  );
}

function RunReport({ run, onOpenChart }: { run: SessionRunResult; onOpenChart: (day?: string, trade?: number) => void }) {
  const [sessFilter, setSessFilter] = useState<string>("all");
  const rows = [...run.bySession, ...(run.total ? [run.total] : [])];
  const keys = ["London", "NewYork"];
  for (const k of keys) if (!rows.some((r) => r.key === k)) rows.splice(rows.length - 1, 0, { key: k, label: k === "NewYork" ? "New York" : k, sessions: 0, trades: 0, winners: 0, losers: 0, grossPnL: 0, fees: 0, netPnL: 0, entrySignals: 0, suppressedAfterCloseOut: 0 });
  const trades = run.trades.filter((t) => sessFilter === "all" || t.session === sessFilter);
  return (
    <div className="space-y-3" data-testid="ln-report" data-run={run.runId}>
      <Box title={`Ergebnis · ${run.strategyName || "–"} · ${run.request.from} – ${run.request.to}`} testid="ln-summary">
        <div className="text-[var(--fg-dim)]">
          Lauf {run.runId} · Status <b className={run.status === "Completed" ? "text-[var(--key)]" : "text-[var(--red)]"}>{run.status}</b>{run.error ? ` – ${run.error}` : ""} ·
          {" "}{num(run.ticksProcessed, 0)} Ticks in einem Engine-Lauf · {num(run.elapsedSeconds, 1)} s · Gebühr/Seite {money(run.feePerSide)}{run.feeIsExample ? " (Beispielprofil)" : ""} · Slippage {num(run.slippageTicks)} Ticks ·
          Tageslimit {run.maxDailyLoss >= 1e7 ? "aus (Risikoprofil research-sim)" : money(run.maxDailyLoss)} · Close-Out {run.closeOutMinutes} min
        </div>
        <table className="w-full font-mono">
          <thead><tr className="text-left text-[var(--fg-faint)]">{["", "Sessions", "Trades", "Gewinner/Verlierer", "Brutto", "Gebühren", "Netto", "Einstiegssignale", "nach Close-Out unterdrückt"].map((h) => <th key={h} className="pr-3 font-normal">{h}</th>)}</tr></thead>
          <tbody>{rows.map((r) => (
            <tr key={r.key} className={r.key === "Total" ? "border-t border-[var(--line)] font-semibold" : ""} data-testid={`ln-sum-${r.key}`}>
              <td className="pr-3 text-[var(--fg)]">{r.label}</td><td className="pr-3">{r.sessions}</td><td className="pr-3">{r.trades}</td>
              <td className="pr-3">{r.winners}/{r.losers}</td><td className={cn("pr-3", pnlCls(r.grossPnL))}>{money(r.grossPnL)}</td><td className="pr-3">{money(r.fees)}</td>
              <td className={cn("pr-3", pnlCls(r.netPnL))}>{money(r.netPnL)}</td><td className="pr-3">{r.entrySignals}</td><td>{r.suppressedAfterCloseOut}</td>
            </tr>))}</tbody>
        </table>
        <div className="text-[var(--fg-dim)]">
          Konto {money(run.startBalance)} → <b className={pnlCls(run.endBalance - run.startBalance)}>{money(run.endBalance)}</b> · max. Drawdown (geschlossene Trades) {money(run.maxDrawdown)} ·
          Position am Ende {run.finalPosition} · vom RiskManager abgelehnt {run.ordersRejectedByRisk} · Null Trades in einer Session sind ein gültiges Ergebnis.
        </div>
        <div className="flex flex-wrap gap-2 pt-1">
          <button className={btnOn} onClick={() => onOpenChart()} data-testid="ln-open-chart">Trades im Chart</button>
        </div>
        {run.skippedAtLoad.length > 0 && <ul className="list-disc pl-4 text-[var(--gold)]">{run.skippedAtLoad.map((s) => <li key={s}>{s}</li>)}</ul>}
        {run.contractCheck && <div className={run.contractCheck.startsWith("geprüft") ? "text-[var(--fg-dim)]" : "text-[var(--gold)]"} data-testid="ln-contract">{run.contractCheck}</div>}
      </Box>

      {run.stop && (
        <Box title={`Lauf abgebrochen: ${run.stop.reason}`} testid="ln-stop">
          <div className="text-[var(--red)]">
            {run.stop.day} {run.stop.sessionLabel}: Sessionende {t7(run.stop.sessionEndUtc).slice(11, 16)} UTC, Close-Out {t7(run.stop.closeOutUtc).slice(11, 16)} UTC
            {run.stop.sessionCloseSignalUtc ? ` · Flat-Signal ${t7(run.stop.sessionCloseSignalUtc).slice(11)} ohne ausführbaren Folge-Tick` : " · kein Tick nach der Close-Out-Grenze"}
          </div>
          <table className="font-mono">
            <tbody>
              <tr><td className="pr-4 text-[var(--fg-faint)]">Letzter beobachteter Preis</td><td>{num(run.stop.lastObservedPrice)} <span className="text-[var(--fg-dim)]">({t7(run.stop.lastTickUtc)})</span></td></tr>
              <tr><td className="pr-4 text-[var(--fg-faint)]">Offene Position</td><td>{run.stop.openPosition}{run.stop.pendingOrders ? ` · ${run.stop.pendingOrders} offene Order(s) – nicht in die nächste Session übernommen` : ""}</td></tr>
              <tr><td className="pr-4 text-[var(--fg-faint)]">Offene Bewertung (nicht gebucht)</td><td className={pnlCls(run.stop.unrealizedGrossAtLastPrice)}>{money(run.stop.unrealizedGrossAtLastPrice)}</td></tr>
              <tr><td className="pr-4 text-[var(--fg-faint)]">Realisiert (geschlossene Trades)</td><td><span className={pnlCls(run.stop.realizedNet)}>{money(run.stop.realizedNet)}</span> netto · Brutto {money(run.stop.realizedGross)} · Gebühren {money(run.stop.realizedFees)} · Konto {money(run.stop.balanceRealized)}</td></tr>
              <tr><td className="pr-4 text-[var(--fg-faint)]">Nicht gelaufen</td><td>{run.stop.notRunSegments.join(", ") || "–"}</td></tr>
            </tbody>
          </table>
          <p className="text-[var(--fg-faint)]">{run.stop.note}</p>
        </Box>
      )}

      <Box title="Übergänge (Sprung über die Pause)" testid="ln-transitions">
        <table className="w-full font-mono">
          <thead><tr className="text-left text-[var(--fg-faint)]">{["Übergang", "letzter Tick", "erster Tick", "übersprungen", "Kontostand vor → nach", "PnL im Sprung", "Trades im Sprung", "Position an der Grenze"].map((h) => <th key={h} className="pr-3 font-normal">{h}</th>)}</tr></thead>
          <tbody>{run.transitions.map((t) => (
            <tr key={t.fromId + t.toId} className="align-top">
              <td className="pr-3 text-[var(--fg)]">{t.fromLabel} → {t.toLabel}<div className="text-[10px] text-[var(--fg-faint)]">{t.kind}</div>
                {t.skippedDays.length > 0 && <div className="text-[10px] text-[var(--gold)]">{t.skippedDays.join(" · ")}</div>}</td>
              <td className="pr-3">{t7(t.lastTickUtc)}<div className="text-[var(--fg-dim)]">@ {num(t.lastTickPrice)}</div></td>
              <td className="pr-3">{t7(t.firstTickUtc)}<div className="text-[var(--fg-dim)]">@ {num(t.firstTickPrice)}</div></td>
              <td className="pr-3">{t.skippedText}</td>
              <td className="pr-3">{money(t.balanceBefore)} → {money(t.balanceAfter)}</td>
              <td className={cn("pr-3", t.pnlChangeInJump === 0 ? "text-[var(--key)]" : "text-[var(--red)]")}>{money(t.pnlChangeInJump)}</td>
              <td className={cn("pr-3", t.tradesClosedInJump + t.tradesOpenedInJump === 0 ? "text-[var(--key)]" : "text-[var(--red)]")}>{t.tradesClosedInJump + t.tradesOpenedInJump}</td>
              <td>{t.positionAtLastTick}</td>
            </tr>))}</tbody>
        </table>
        {!run.transitions.length && <p className="text-[var(--fg-dim)]">Nur eine Session gelaufen – kein Übergang.</p>}
      </Box>

      <Box title="Sessions (Daten, Session-Close, Sessionwerte)" testid="ln-segments">
        <div className="overflow-x-auto">
          <table className="w-full font-mono">
            <thead><tr className="text-left text-[var(--fg-faint)]">{["Session", "Fenster (UTC)", "Quelle / Aufnahme / Kontrakt", "Ticks", "erster / letzter Tick", "Session-VWAP", "Session-CVD", "verworfene Teilbar", "Session-Close", "Vorfälle"].map((h) => <th key={h} className="pr-3 font-normal">{h}</th>)}</tr></thead>
            <tbody>{run.segments.map((s) => (
              <tr key={s.id} className="align-top">
                <td className="pr-3 text-[var(--fg)]">{s.day} {s.label}</td>
                <td className="pr-3">{s.windowStartUtc.slice(11, 16)}–{s.windowEndUtc.slice(11, 16)}<div className="text-[var(--fg-dim)]">Close-Out {s.closeOutUtc.slice(11, 16)}</div></td>
                <td className="pr-3 text-[10px]">{s.collectionsRoot}<div>{s.recordingId} · {s.contract || "–"}</div><div className="text-[var(--fg-faint)]">SHA {s.sliceSha256.slice(0, 12)}…</div></td>
                <td className="pr-3">{num(s.ticksUsed, 0)}</td>
                <td className="pr-3">{t7(s.firstTickUtc).slice(11)}<div>{t7(s.lastTickUtc).slice(11)}</div></td>
                <td className="pr-3">{num(s.sessionVwap)}</td><td className="pr-3">{num(s.sessionCvd, 0)}</td><td className="pr-3">{s.discardedPartialBarTicksAtStart}</td>
                <td className="pr-3 text-[10px]">{s.sessionCloseStatus}</td>
                <td className={cn("text-[10px]", s.incidents.length ? "text-[var(--red)]" : "text-[var(--fg-faint)]")}>{s.incidents.length ? s.incidents.join(" ") : "keine"}</td>
              </tr>))}</tbody>
          </table>
        </div>
      </Box>

      <Box title="Handelstage (Tages-PnL, Tagesverlustlimit)">
        <table className="w-full font-mono">
          <thead><tr className="text-left text-[var(--fg-faint)]">{["Tag", "Trades", "Brutto", "Gebühren", "Netto", "Tageslimit", ""].map((h) => <th key={h} className="pr-3 font-normal">{h}</th>)}</tr></thead>
          <tbody>{run.byDay.map((d) => (
            <tr key={d.day} className="align-top">
              <td className="pr-3 text-[var(--fg)]">{d.day}</td><td className="pr-3">{d.trades}</td><td className={cn("pr-3", pnlCls(d.grossPnL))}>{money(d.grossPnL)}</td>
              <td className="pr-3">{money(d.fees)}</td><td className={cn("pr-3", pnlCls(d.netPnL))}>{money(d.netPnL)}</td>
              <td className="pr-3 text-[10px]">{d.dailyLimitHitUtc ? d.notes.join(" ") : "nicht erreicht"}</td>
              <td><button className={btn} onClick={() => onOpenChart(d.day)}>im Chart</button></td>
            </tr>))}</tbody>
        </table>
      </Box>

      {(run.reasonCounts?.length || run.integrity) && (
        <Box title="Ein-/Ausstiegsgründe und ID-Prüfung" testid="ln-reasons">
          <table className="font-mono">
            <thead><tr className="text-left text-[var(--fg-faint)]">{["Session", "Art", "Grund", "Anzahl"].map((h) => <th key={h} className="pr-3 font-normal">{h}</th>)}</tr></thead>
            <tbody>{(run.reasonCounts ?? []).map((r) => <tr key={r.session + r.kind + r.cause}><td className="pr-3">{r.session}</td><td className="pr-3">{r.kind}</td><td className="pr-3">{r.cause}</td><td>{r.count}</td></tr>)}</tbody>
          </table>
          {run.integrity && (
            <div className="text-[var(--fg-dim)]" data-testid="ln-integrity">
              Orders {run.integrity.ordersSubmitted} · davon mit Trade verknüpft {run.integrity.ordersLinkedToTrades} · unverknüpft {run.integrity.ordersUnlinked} ·
              verschiedene Signal-IDs {run.integrity.distinctSignalIds} · Einstiegs-Fills {run.integrity.distinctEntryFillIds} · Ausstiegs-Fills {run.integrity.distinctExitFillIds} ·
              von zwei Trades geteilte Fills {run.integrity.fillsSharedByTwoTrades} · überlappende Trades {run.integrity.overlappingTrades}
              {run.integrity.notes.length > 0 && <div className="text-[var(--gold)]">{run.integrity.notes.join(" ")}</div>}
            </div>
          )}
        </Box>
      )}

      <Box title={`Journal (${run.trades.length} Trades)`} testid="ln-journal">
        <div className="flex gap-1">
          {[["all", "Alle"], ["London", "London"], ["NewYork", "New York"]].map(([k, l]) => (
            <button key={k} className={cn(btn, sessFilter === k && "border-[var(--cyan)]")} onClick={() => setSessFilter(k)}>{l}</button>))}
        </div>
        <div className="max-h-[360px] overflow-y-auto">
          <table className="w-full font-mono">
            <thead className="sticky top-0 bg-[var(--panel)]"><tr className="text-left text-[var(--fg-faint)]">{["#", "Tag", "Session", "Seite", "Einstieg (UTC)", "Ausstieg (UTC)", "Brutto", "Gebühren", "Netto", "Grund", "Konto", "IDs", ""].map((h) => <th key={h} className="pr-2 font-normal">{h}</th>)}</tr></thead>
            <tbody>{trades.slice(0, 2000).map((t) => (
              <tr key={t.index} className={t.crossedPause ? "text-[var(--red)]" : ""}>
                <td className="pr-2">{t.index + 1}</td><td className="pr-2">{t.day}</td><td className="pr-2">{t.sessionLabel}</td><td className="pr-2">{t.side}</td>
                <td className="pr-2">{t.entryTimeUtc.slice(11, 23)} @ {num(t.entryPrice)}</td><td className="pr-2">{t.exitTimeUtc.slice(11, 23)} @ {num(t.exitPrice)}</td>
                <td className={cn("pr-2", pnlCls(t.grossPnL))}>{money(t.grossPnL)}</td><td className="pr-2">{money(t.fees)}</td>
                <td className={cn("pr-2", pnlCls(t.netPnL))}>{money(t.netPnL)}</td><td className="pr-2 text-[10px]" title={t.entryCause ? `Einstieg: ${t.entryCause}` : undefined}>{t.exitCause || t.exitReason}</td><td className="pr-2">{money(t.balanceAfter)}</td>
                <td className="pr-2 text-[10px] text-[var(--fg-faint)]" title={t.entryOrderId ? `Signal ${t.entrySignalId} → Order ${t.entryOrderId} → Fill ${t.entryFillId}\nSignal ${t.exitSignalId} → Order ${t.exitOrderId} → Fill ${t.exitFillId}` : undefined}>{t.entryFillId ? `${t.entryFillId.slice(0, 8)}/${t.exitFillId?.slice(0, 8)}` : "–"}</td>
                <td><button className={btn} onClick={() => onOpenChart(t.day, t.index)} data-testid={`ln-trade-chart-${t.index}`}>Im Chart</button></td>
              </tr>))}</tbody>
          </table>
          {trades.length > 2000 && <p className="text-[var(--fg-faint)]">Erste 2000 von {trades.length} angezeigt.</p>}
        </div>
      </Box>

      <Box title="Regeln und Indikatoren beim Sessionwechsel">
        <ul className="list-disc pl-4 text-[var(--fg-dim)]">{run.rules.map((r) => <li key={r}>{r}</li>)}</ul>
        <table className="w-full">
          <thead><tr className="text-left text-[var(--fg-faint)]">{["Größe", "beim Sessionstart", "Begründung"].map((h) => <th key={h} className="pr-3 font-normal">{h}</th>)}</tr></thead>
          <tbody>{run.indicatorPolicy.map((p) => <tr key={p.name}><td className="pr-3 text-[var(--fg)]">{p.name}</td><td className="pr-3 font-mono">{p.atSessionStart}</td><td className="text-[var(--fg-dim)]">{p.reason}</td></tr>)}</tbody>
        </table>
        <p className="text-[var(--fg-faint)]">{run.engine}. Simulation – keine Orders, keine Broker-Verbindung.</p>
      </Box>
    </div>
  );
}
