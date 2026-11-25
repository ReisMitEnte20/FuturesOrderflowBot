// Gespeicherter Lauf → alle Trades im Chart. Liest NUR den gespeicherten Bericht (Trades = simulierte Fills) und die
// Kerzen der EIGENEN Datenquelle des Laufs (candleRequest des Berichts) – kein neuer Strategielauf, keine neue Engine.
// Tagesübersicht: alle Kerzen und Trades des gewählten Tages. Replay: Kerze für Kerze, nur bis zur Position Bekanntes
// (OHLC-Daten → nur Kerzen-Replay). Journal ↔ Chart: Klick auf eine Zeile wechselt den Tag und springt zum Einstieg,
// Klick auf einen Marker markiert die Zeile. Offene Positionen bekommen keinen erfundenen Ausstieg.
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { cn } from "@/lib/utils";
import { BacktestChart } from "@/components/backtest/BacktestChart";
import { backtestApi, type Candle, type LoadedDataInfo } from "@/lib/backtestApi";
import { backtestingApi, money, type ReportRun, type ReportSummary, type TradeRow } from "@/lib/backtestingApi";
import { dayTrades, dayTradesAt, fillInBar, reachability, sessionRange, type SessionSpan, type StoredTrade } from "@/lib/chartReplay";

const SPEEDS = [1, 2, 5, 10];
const fmtUtc = (ms: number) => new Date(ms).toISOString().replace("T", " ").replace(/\.000Z$/, "Z").replace(/Z$/, " UTC");
const etFmt = new Intl.DateTimeFormat("de-DE", { timeZone: "America/New_York", hour: "2-digit", minute: "2-digit", second: "2-digit", hourCycle: "h23" });
const fillIso = (t: TradeRow, kind: "entry" | "exit") => (kind === "entry" ? t.entryTimeUtc : t.exitTimeUtc) ?? new Date(kind === "entry" ? t.entryT : t.exitT).toISOString();

/** Sessions des Berichts; ältere Berichte ohne Sessionliste: UTC-Kalendertage der Kerzen (gekennzeichnet). */
function sessionsOf(run: ReportRun | null, candles: Candle[]): { spans: SessionSpan[]; fromReport: boolean } {
  const s = run?.report?.data?.sessions ?? [];
  if (s.length) return { spans: s.map((x) => ({ key: x.key, firstT: x.firstT, lastT: x.lastT })), fromReport: true };
  const by = new Map<string, SessionSpan>();
  for (const c of candles) {
    const k = new Date(c.t).toISOString().slice(0, 10);
    const e = by.get(k);
    if (e) e.lastT = c.t; else by.set(k, { key: k, firstT: c.t, lastT: c.t });
  }
  return { spans: [...by.values()], fromReport: false };
}

export function SavedRunChart({ runId, onRun, initialTrade, onOpenDayReplay }: {
  runId: string | null; onRun: (id: string | null) => void; initialTrade?: number | null;
  onOpenDayReplay?: (day: string, runId: string) => void;
}) {
  const [reports, setReports] = useState<ReportSummary[] | null>(null);
  const [run, setRun] = useState<ReportRun | null>(null);
  const [runErr, setRunErr] = useState<string | null>(null);
  const [candles, setCandles] = useState<Candle[]>([]);
  const [dataInfo, setDataInfo] = useState<LoadedDataInfo | null>(null);
  const [candleErr, setCandleErr] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [dayKey, setDayKey] = useState<string | null>(null);
  const [view, setView] = useState<"overview" | "replay">("overview");
  const [selected, setSelected] = useState<number | null>(initialTrade ?? null);
  const [focusKey, setFocusKey] = useState(0);
  const [pos, setPos] = useState(0);
  const [playing, setPlaying] = useState(false);
  const [speed, setSpeed] = useState(2);
  const [onlyDay, setOnlyDay] = useState(false);
  const rowRefs = useRef<Record<number, HTMLTableRowElement | null>>({});

  useEffect(() => {
    const ac = new AbortController();
    backtestingApi.reports(ac.signal).then(setReports).catch(() => setReports([]));
    return () => ac.abort();
  }, []);

  // Bericht laden (nur lesen).
  useEffect(() => {
    setRun(null); setRunErr(null); setCandles([]); setDataInfo(null); setCandleErr(null); setDayKey(null); setPlaying(false);
    if (!runId) return;
    const ac = new AbortController();
    backtestingApi.report(runId, ac.signal).then((r) => {
      if (!r.ok || !r.run) { setRunErr(r.error ?? "Bericht nicht gefunden."); return; }
      setRun(r.run);
    }).catch((e) => !ac.signal.aborted && setRunErr(String(e)));
    return () => ac.abort();
  }, [runId]);

  const rep = run?.report ?? null;
  const req = rep?.trades?.candleRequest ?? null;
  const rows: TradeRow[] = useMemo(() => rep?.trades?.trades ?? [], [rep]);
  const prov = rep?.provenance ?? null;
  const qty = prov?.quantity ?? req?.quantity ?? 1;
  const summary = reports?.find((r) => r.runId === runId) ?? null;
  // Zeitrahmen nur für echte Zeitkerzen; Tick-Bars (Orderflow-Lauf) haben keinen festen Zeitrahmen.
  const tfMs = prov && prov.timeframeMinutes > 0 ? prov.timeframeMinutes * 60_000 : null;

  // Kerzen der EIGENEN Quelle des Laufs (gleiche Anfrage wie das Bar-Replay im Bericht).
  useEffect(() => {
    if (!req) return;
    const ac = new AbortController();
    setLoading(true);
    backtestApi.candles({ ...req, maxRows: req.maxRows || 100_000_000 }, ac.signal)
      .then((r) => { if (r.ok) { setCandles(r.candles); setDataInfo(r.data ?? null); } else setCandleErr(r.error ?? "Kerzen der Laufquelle konnten nicht geladen werden."); })
      .catch((e) => { if (!ac.signal.aborted) setCandleErr(String(e)); })
      .finally(() => !ac.signal.aborted && setLoading(false));
    return () => ac.abort();
  }, [req]);

  const { spans, fromReport } = useMemo(() => sessionsOf(run, candles), [run, candles]);
  const stored: StoredTrade[] = rows;
  const reach = useMemo(() => (candles.length ? reachability(candles, stored, spans, tfMs) : null), [candles, stored, spans, tfMs]);
  const tradeDays = useMemo(() => {
    const m = new Map<string, number>();
    for (const t of rows) m.set(t.session, (m.get(t.session) ?? 0) + 1);
    return m;
  }, [rows]);

  // Start-Tag: Tag des gewählten Trades, sonst erster Tag mit Trades.
  useEffect(() => {
    if (!spans.length || dayKey) return;
    const sel = selected != null ? rows.find((t) => t.index === selected) : null;
    setDayKey(sel?.session ?? spans.find((s) => tradeDays.has(s.key))?.key ?? spans[0].key);
    if (sel) setFocusKey((k) => k + 1);
  }, [spans, dayKey, rows, selected, tradeDays]);

  const span = spans.find((s) => s.key === dayKey) ?? null;
  const range = span ? sessionRange(candles, span) : null;
  const dayCandles = useMemo(() => (range ? candles.slice(range.i0, range.i1 + 1) : []), [candles, range?.i0, range?.i1]); // eslint-disable-line react-hooks/exhaustive-deps
  const day = useMemo(() => (span ? dayTrades(candles, stored, span, tfMs) : []), [candles, stored, span, tfMs]);
  const dayChart = useMemo(() => day.flatMap((d) => (d.chart ? [d.chart] : [])), [day]);
  const n = dayCandles.length;
  const p = Math.max(0, Math.min(pos, n - 1));
  const replayCandles = useMemo(() => dayCandles.slice(0, p + 1), [dayCandles, p]);
  const replayTrades = useMemo(() => dayTradesAt(day, p), [day, p]);

  // Bar-Replay abspielen (ganze Kerzen; OHLC enthält keine Ticks). Am Tagesende anhalten.
  useEffect(() => {
    if (!playing || view !== "replay") return;
    const h = window.setInterval(() => setPos((x) => {
      if (x >= n - 1) { setPlaying(false); return x; }
      return x + 1;
    }), 1000 / speed);
    return () => clearInterval(h);
  }, [playing, view, speed, n]);

  const selectTrade = useCallback((idx: number, fromChart = false) => {
    const t = rows.find((x) => x.index === idx);
    if (!t) return;
    setSelected(idx);
    if (!fromChart) {
      if (t.session !== dayKey) setDayKey(t.session);
      setFocusKey((k) => k + 1);
      const s = spans.find((x) => x.key === t.session);
      const r = s ? sessionRange(candles, s) : null;
      if (r) setPos(Math.max(0, t.entryBar - r.i0)); // Replay: genau die Einstiegskerze (Ausstieg noch unbekannt)
      setPlaying(false);
    } else {
      window.setTimeout(() => rowRefs.current[idx]?.scrollIntoView({ block: "nearest", behavior: "smooth" }), 30);
    }
  }, [rows, dayKey, spans, candles]);

  const sel = selected != null ? rows.find((t) => t.index === selected) ?? null : null;
  const selDay = sel ? day.find((d) => d.trade.index === sel.index) ?? null : null;
  const dayIdx = spans.findIndex((s) => s.key === dayKey);
  const tradeDayKeys = spans.filter((s) => tradeDays.has(s.key)).map((s) => s.key);
  const atasDay = req?.dataSourceId?.startsWith("atas-collection:") ? req.dataSourceId.slice("atas-collection:".length) : null;
  const journal = onlyDay ? rows.filter((t) => t.session === dayKey) : rows;
  const runs = (reports ?? []).filter((r) => r.status === "Completed");

  // Abnahme-/Prüfschnittstelle: Werte des ausgewählten Trades aus Journal und aus dem gezeichneten Marker.
  const check = sel && selDay?.chart && range ? (() => {
    const c = selDay.chart!;
    const entryCandle = dayCandles[c.entryBarIndex];
    const exitCandle = c.exit ? dayCandles[c.exit.barIndex] : null;
    return {
      journal: { day: sel.session, side: sel.side, qty, entryFill: fillIso(sel, "entry"), entryPrice: sel.entryPrice, exitFill: sel.closedAtEnd ? null : fillIso(sel, "exit"), exitPrice: sel.closedAtEnd ? null : sel.exitPrice, signal: sel.entrySignalUtc ?? null },
      chart: { day: dayKey, side: c.side, entryCandleUtc: entryCandle ? new Date(entryCandle.t).toISOString() : null, entryPrice: c.entryPrice, exitCandleUtc: exitCandle ? new Date(exitCandle.t).toISOString() : null, exitPrice: c.exit?.price ?? null },
      match: {
        day: sel.session === dayKey, side: sel.side === c.side, entryPrice: sel.entryPrice === c.entryPrice,
        entryTimeInMarkerCandle: fillInBar(dayCandles, c.entryBarIndex, sel.entryT, tfMs),
        exitTimeInMarkerCandle: sel.closedAtEnd ? null : c.exit != null ? fillInBar(dayCandles, c.exit.barIndex, sel.exitT, tfMs, true) : null,
        exitPrice: sel.closedAtEnd ? c.exit == null : c.exit != null ? sel.exitPrice === c.exit.price : null,
      },
    };
  })() : null;

  return (
    <div className="space-y-3" data-testid="saved-run-chart" data-reach={reach ? JSON.stringify({ total: reach.total, closed: reach.closed, open: reach.open, reachable: reach.reachable }) : ""}
      data-selected-check={check ? JSON.stringify(check) : ""}>
      {/* ---- Laufwahl + Herkunft ---- */}
      <section className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3 space-y-2">
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-[11px] text-[var(--fg-faint)]">Gespeicherter Lauf</span>
          <select value={runId ?? ""} onChange={(e) => { setSelected(null); onRun(e.target.value || null); }} data-testid="run-select"
            className="h-8 min-w-[320px] max-w-full rounded-md border border-[var(--line-2)] bg-[var(--bg-2)] px-2 text-xs text-[var(--fg)]">
            <option value="">– Lauf wählen –</option>
            {runs.map((r) => (
              <option key={r.runId} value={r.runId}>
                {r.strategyName} · {r.symbol}{r.instrumentNote ? " ⚠" : ""} · {r.dataFrom ? r.dataFrom.slice(0, 10) : "?"}–{r.dataTo ? r.dataTo.slice(0, 10) : "?"} · {r.runId}
              </option>
            ))}
          </select>
          {!runId && <span className="text-[11px] text-[var(--fg-dim)]">Im Bericht führt „Trades im Chart“ direkt hierher.</span>}
        </div>
        {runErr && <p className="text-xs text-[var(--red)]">{runErr}</p>}
        {run && (
          <div className="grid gap-x-4 gap-y-0.5 text-[11px] md:grid-cols-2" data-testid="run-provenance">
            <div><span className="text-[var(--fg-faint)]">Lauf-ID</span> <span className="mono text-[var(--fg)]">{run.runId}</span></div>
            <div><span className="text-[var(--fg-faint)]">Strategie</span> <b className="text-[var(--fg)]">{prov?.strategyName ?? summary?.strategyName ?? run.config?.run?.strategy ?? "–"}</b>
              <span className="text-[var(--fg-dim)]"> {Object.entries(prov?.parameters ?? run.config?.run?.params ?? {}).map(([k, v]) => `${k}=${v}`).join(", ")}</span></div>
            <div><span className="text-[var(--fg-faint)]">Instrument</span> <b className="text-[var(--fg)]">{prov?.symbol ?? run.config?.run?.symbol ?? "–"}</b> · {qty} Kontrakt(e)
              <span className="text-[var(--fg-dim)]"> · Kontrakt/Serie: nicht im Bericht gespeichert</span></div>
            <div><span className="text-[var(--fg-faint)]">Datenquelle</span> <span className="text-[var(--fg)]">{prov?.dataSource ?? "–"}</span> <span className="mono text-[var(--fg-dim)]">({req?.dataSourceId ?? run.config?.run?.dataSourceId ?? "–"})</span></div>
            <div><span className="text-[var(--fg-faint)]">Zeitraum</span> <span className="text-[var(--fg)]">{run.dataFrom ? fmtUtc(Date.parse(run.dataFrom)) : "–"} – {run.dataTo ? fmtUtc(Date.parse(run.dataTo)) : "–"}</span></div>
            <div><span className="text-[var(--fg-faint)]">Datenart</span> <span className="text-[var(--fg-dim)]">{prov?.dataKind ?? "–"}</span></div>
          </div>
        )}
        {summary?.instrumentNote && <p className="text-[11px] font-medium text-[var(--red)]" data-testid="instrument-note">⚠ {summary.instrumentNote} – der Chart zeigt die Kerzen DIESER Laufquelle, nichts wird untergelegt oder umgedeutet.</p>}
        {run && !req && (
          <p className="rounded border border-[var(--gold)] px-2 py-1 text-[11px] text-[var(--gold)]" data-testid="no-source">
            Dieser Bericht enthält {rows.length ? `${rows.length} Trades, aber` : "keine gespeicherten Trades und"} keine eindeutige Kerzenquelle (älteres Format).
            Die Originalquelle steht nicht eindeutig fest – es wird nichts nachträglich zugeordnet oder neu gerechnet.
          </p>
        )}
        {run && req && rows.length === 0 && <p className="text-[11px] text-[var(--gold)]">Dieser Lauf hat keine Trades gespeichert.</p>}
        {candleErr && <p className="text-[11px] text-[var(--red)]">Kerzen der Laufquelle nicht ladbar: {candleErr} – keine Ersatzdaten.</p>}
        {reach && (
          <p className="text-[11px] text-[var(--fg-dim)]" data-testid="reach-line">
            <b className="text-[var(--fg)]">{reach.total}</b> Trades gespeichert · <b className="text-[var(--fg)]">{reach.closed}</b> geschlossen
            {reach.open > 0 && <> · {reach.open} am Datenende offen (nur bewertet, kein Ausstieg)</>}
            {" "}· im Chart erreichbar: <b className={reach.reachable === reach.closed ? "text-[var(--key)]" : "text-[var(--gold)]"}>{reach.reachable} von {reach.closed}</b> geschlossenen
            {dataInfo && <> · Kerzen: {dataInfo.barCount} aus „{dataInfo.source}“ ({dataInfo.symbol}{dataInfo.timeframeMinutes > 0 ? `, ${dataInfo.timeframeMinutes} Min` : ""})</>}
            {!fromReport && <span className="text-[var(--gold)]"> · Tage nach UTC-Kalendertag (Bericht ohne Sessionliste)</span>}
          </p>
        )}
        {reach && reach.problems.length > 0 && (
          <details className="text-[11px] text-[var(--gold)]"><summary className="cursor-pointer">{reach.problems.length} Trade(s) nicht eindeutig zuordenbar – Originalquelle nicht eindeutig</summary>
            <ul className="mt-1 space-y-0.5">{reach.problems.slice(0, 50).map((x) => <li key={x.index}>#{x.index + 1}: {x.why}</li>)}</ul>
          </details>
        )}
      </section>

      {run && req && (
        <section className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3 space-y-2">
          <div className="flex flex-wrap items-center gap-2">
            <div className="flex rounded-md border border-[var(--line-2)] p-0.5" role="tablist" aria-label="Ansicht">
              {(["overview", "replay"] as const).map((v) => (
                <button key={v} role="tab" aria-selected={view === v} data-testid={`run-view-${v}`} onClick={() => { setView(v); setPlaying(false); }}
                  className={cn("h-7 rounded px-3 text-[12px]", view === v ? "bg-grad-brand font-semibold text-[var(--bg)]" : "text-[var(--fg-dim)] hover:text-[var(--fg)]")}>
                  {v === "overview" ? "Tagesübersicht" : "Replay (Kerze für Kerze)"}
                </button>
              ))}
            </div>
            <span className="mx-1 h-4 w-px bg-[var(--line-2)]" />
            <button className="h-7 rounded border border-[var(--line-2)] px-2 text-xs text-[var(--fg)] disabled:opacity-40" disabled={dayIdx <= 0} onClick={() => { setDayKey(spans[dayIdx - 1].key); setPos(0); }}>◀ Tag</button>
            <select value={dayKey ?? ""} onChange={(e) => { setDayKey(e.target.value); setPos(0); setPlaying(false); }} data-testid="run-day"
              className="h-7 rounded-md border border-[var(--line-2)] bg-[var(--bg-2)] px-2 text-xs text-[var(--fg)]">
              {spans.map((s) => <option key={s.key} value={s.key}>{s.key}{tradeDays.has(s.key) ? ` · ${tradeDays.get(s.key)} Trades` : ""}</option>)}
            </select>
            <button className="h-7 rounded border border-[var(--line-2)] px-2 text-xs text-[var(--fg)] disabled:opacity-40" disabled={dayIdx < 0 || dayIdx >= spans.length - 1} onClick={() => { setDayKey(spans[dayIdx + 1].key); setPos(0); }}>Tag ▶</button>
            <span className="text-[10px] text-[var(--fg-faint)]">{tradeDayKeys.length} Tage mit Trades</span>
            {atasDay && onOpenDayReplay && (
              <button onClick={() => onOpenDayReplay(atasDay, run.runId)} className="ml-auto h-7 rounded-md border border-[var(--cyan)] px-2 text-[11px] text-[var(--cyan)]">Diesen Tag Tick für Tick abspielen →</button>
            )}
          </div>

          {view === "replay" && (
            <div className="flex flex-wrap items-center gap-1.5 text-xs">
              {playing
                ? <button className="h-7 rounded-md bg-[var(--key)] px-2.5 font-semibold text-black" onClick={() => setPlaying(false)}>❚❚ Pause</button>
                : <button className="h-7 rounded-md bg-[var(--key)] px-2.5 font-semibold text-black disabled:opacity-40" disabled={p >= n - 1} onClick={() => setPlaying(true)}>▶ Play</button>}
              <button className="h-7 rounded border border-[var(--line-2)] px-2 text-[var(--fg)]" onClick={() => { setPlaying(false); setPos(0); }}>⏮ Tagesbeginn</button>
              <button className="h-7 rounded border border-[var(--line-2)] px-2 text-[var(--fg)]" onClick={() => { setPlaying(false); setPos((x) => Math.max(0, x - 1)); }}>◀ 1 Kerze</button>
              <button className="h-7 rounded border border-[var(--line-2)] px-2 text-[var(--fg)]" onClick={() => { setPlaying(false); setPos((x) => Math.min(n - 1, x + 1)); }}>1 Kerze ▶</button>
              {SPEEDS.map((s) => <button key={s} onClick={() => setSpeed(s)} className={cn("h-7 rounded px-2", speed === s ? "bg-[var(--key)] font-semibold text-black" : "border border-[var(--line-2)] text-[var(--fg)]")}>{s} K/s</button>)}
              <input type="range" min={0} max={Math.max(0, n - 1)} value={p} onChange={(e) => { setPlaying(false); setPos(Number(e.target.value)); }} className="min-w-[160px] flex-1" />
              <span className="mono text-[11px] text-[var(--fg-dim)]">Kerze {n ? p + 1 : 0}/{n} · {dayCandles[p] ? fmtUtc(dayCandles[p].t) : "–"}</span>
            </div>
          )}

          <BacktestChart
            candles={view === "overview" ? dayCandles : replayCandles}
            trades={view === "overview" ? dayChart : replayTrades}
            selected={view === "overview" ? selected : null}
            focusKey={focusKey}
            follow={view === "replay" ? 90 : null}
            onSelectTrade={(i) => selectTrade(i, true)}
            status={loading ? { kind: "loading", text: "Kerzen der Laufquelle werden geladen …" } : candleErr ? { kind: "error", text: candleErr } : !n && candles.length ? { kind: "empty", text: "Für diesen Tag keine Kerzen in der Laufquelle." } : null}
            className="h-[420px]"
          />
          <p className="text-[10px] text-[var(--fg-faint)]">
            {view === "overview"
              ? "Tagesübersicht: alle Kerzen und gespeicherten Trades dieses Tages. ▲/▼ Einstiegs-Fill, ◆ Ausstiegs-Fill (Preis = simulierter Fill inkl. Slippage). Zeiten UTC."
              : "Replay: nur vollständige Kerzen bis zur Position; Einstieg ab seiner Kerze, Ausstieg erst ab der Ausstiegskerze. OHLC-Daten enthalten keine Ticks – daher nur Kerzen-Replay."}
          </p>

          {sel && <TradeDetail t={sel} qty={qty} d={selDay} check={check} />}
        </section>
      )}

      {run && rows.length > 0 && (
        <section className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3 space-y-2">
          <div className="flex flex-wrap items-center gap-2">
            <h3 className="text-[13px] font-medium text-[var(--fg)]">Trade-Journal</h3>
            <span className="text-[11px] text-[var(--fg-faint)]">{rows.length} Trades des Laufs · Klick wechselt den Tag und springt zum Einstieg</span>
            <label className="ml-auto flex items-center gap-1 text-[11px] text-[var(--fg-dim)]">
              <input type="checkbox" checked={onlyDay} onChange={(e) => setOnlyDay(e.target.checked)} /> nur gewählter Tag
            </label>
          </div>
          <div className="max-h-[360px] overflow-y-auto">
            <table className="w-full text-[11px] mono" data-testid="run-journal">
              <thead className="sticky top-0 bg-[var(--panel)]">
                <tr className="text-left text-[var(--fg-faint)]">{["#", "Tag", "Richtung", "Menge", "Einstieg-Fill (UTC)", "Preis", "Ausstieg-Fill (UTC)", "Preis", "Grund", "Netto", "Chart"].map((h, i) => <th key={i} className="px-1.5 py-1 font-normal">{h}</th>)}</tr>
              </thead>
              <tbody>
                {journal.slice(0, 3000).map((t) => {
                  const on = t.index === selected;
                  return (
                    <tr key={t.index} ref={(el) => { rowRefs.current[t.index] = el; }} onClick={() => selectTrade(t.index)} data-trade={t.index}
                      className={cn("cursor-pointer border-t border-[var(--line)] hover:bg-[var(--bg-2)]", on && "bg-[rgba(34,184,240,0.14)] ring-1 ring-inset ring-[var(--cyan)]")}>
                      <td className="px-1.5 py-0.5">{t.index + 1}</td>
                      <td className="px-1.5">{t.session}</td>
                      <td className={cn("px-1.5", t.side === "Long" ? "text-[var(--key)]" : "text-[var(--red)]")}>{t.side}</td>
                      <td className="px-1.5">{qty}</td>
                      <td className="px-1.5 whitespace-nowrap">{fillIso(t, "entry").replace("T", " ")}</td>
                      <td className="px-1.5">{t.entryPrice}</td>
                      <td className="px-1.5 whitespace-nowrap">{t.closedAtEnd ? <span className="text-[var(--gold)]">offen (bewertet)</span> : fillIso(t, "exit").replace("T", " ")}</td>
                      <td className="px-1.5">{t.closedAtEnd ? "–" : t.exitPrice}</td>
                      <td className="px-1.5">{t.exitReason}{t.ambiguous ? " ⚠" : ""}</td>
                      <td className={cn("px-1.5", t.net >= 0 ? "text-[var(--key)]" : "text-[var(--red)]")}>{money(t.net)}</td>
                      <td className="px-1.5 text-[var(--fg-faint)]">{reach?.problems.some((x) => x.index === t.index) ? <span className="text-[var(--gold)]">nicht eindeutig</span> : t.closedAtEnd ? "nur Einstieg" : "✓"}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        </section>
      )}
    </div>
  );
}

function TradeDetail({ t, qty, d, check }: { t: TradeRow; qty: number; d: ReturnType<typeof dayTrades>[number] | null; check: any }) {
  const ok = (v: boolean | null | undefined) => (v == null ? <span className="text-[var(--fg-faint)]">–</span> : v ? <span className="text-[var(--key)]">✓ gleich</span> : <span className="text-[var(--red)]">✕ abweichend</span>);
  const et = (iso: string) => `${etFmt.format(Date.parse(iso))} ET`;
  return (
    <div className="rounded-md border border-[var(--line-2)] bg-[var(--bg-2)] p-2 text-[11px]" data-testid="trade-detail">
      <div className="mb-1 flex flex-wrap items-baseline gap-2">
        <b className="text-[var(--fg)]">Trade #{t.index + 1}</b>
        <span className="text-[var(--fg-dim)]">Journal (gespeicherter Lauf) gegen Marker im Chart</span>
        {d?.why && <span className="text-[var(--gold)]">{d.why}</span>}
      </div>
      <table className="w-full mono">
        <thead><tr className="text-left text-[var(--fg-faint)]"><th className="font-normal pr-2"></th><th className="font-normal pr-2">Journal</th><th className="font-normal pr-2">Chart-Marker</th><th className="font-normal">Abgleich</th></tr></thead>
        <tbody>
          <tr><td className="pr-2 text-[var(--fg-faint)]">Datum</td><td className="pr-2">{t.session}</td><td className="pr-2">{check?.chart.day ?? "–"}</td><td>{ok(check?.match.day)}</td></tr>
          <tr><td className="pr-2 text-[var(--fg-faint)]">Richtung</td><td className="pr-2">{t.side} · {qty} Kontr.</td><td className="pr-2">{check?.chart.side ?? "–"}</td><td>{ok(check?.match.side)}</td></tr>
          <tr><td className="pr-2 text-[var(--fg-faint)]">Einstieg-Fill</td><td className="pr-2">{check?.journal.entryFill ?? fillIso(t, "entry")} <span className="text-[var(--fg-dim)]">({et(fillIso(t, "entry"))})</span></td>
            <td className="pr-2">Kerze ab {check?.chart.entryCandleUtc ?? "–"}</td><td>{ok(check?.match.entryTimeInMarkerCandle)}</td></tr>
          <tr><td className="pr-2 text-[var(--fg-faint)]">Einstieg-Preis</td><td className="pr-2">{t.entryPrice}</td><td className="pr-2">{check?.chart.entryPrice ?? "–"}</td><td>{ok(check?.match.entryPrice)}</td></tr>
          <tr><td className="pr-2 text-[var(--fg-faint)]">Ausstieg-Fill</td><td className="pr-2">{t.closedAtEnd ? "offen – nur bewertet" : `${fillIso(t, "exit")} (${t.exitReason})`}</td>
            <td className="pr-2">{check?.chart.exitCandleUtc ? `Kerze ab ${check.chart.exitCandleUtc}` : "kein Ausstiegsmarker"}</td><td>{ok(check?.match.exitTimeInMarkerCandle)}</td></tr>
          <tr><td className="pr-2 text-[var(--fg-faint)]">Ausstieg-Preis</td><td className="pr-2">{t.closedAtEnd ? "–" : t.exitPrice}</td>
            <td className="pr-2">{check?.chart.exitPrice ?? "–"}</td><td>{ok(check?.match.exitPrice)}</td></tr>
          <tr><td className="pr-2 text-[var(--fg-faint)]">Zeitstempel</td><td className="pr-2 text-[var(--fg-dim)]" colSpan={3}>OHLC-Engine: Einstieg am Kerzen-OPEN; Ausstieg zum Open (Gegensignal/Gap) oder – bei Stop/Ziel, Session-Schluss, Datenende innerhalb der Kerze – mit dem Kerzenschluss gestempelt (Intrabar-Zeit aus OHLC unbekannt).</td></tr>
          <tr><td className="pr-2 text-[var(--fg-faint)]">Signal</td><td className="pr-2" colSpan={3}>{t.entrySignalUtc ? `${t.entrySignalUtc} (${t.entrySignalDirection ?? ""}) – getrennt von der Fill-Zeit` : "Signalzeit im Bericht nicht gespeichert (keine Schätzung)."}</td></tr>
        </tbody>
      </table>
    </div>
  );
}
