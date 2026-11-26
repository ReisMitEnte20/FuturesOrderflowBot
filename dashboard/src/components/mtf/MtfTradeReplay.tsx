// „Trade nachspielen“: lädt die ursprüngliche Ereignisfolge, die damaligen Einstellungen und genau diesen Trade (Server
// wiederholt den Lauf ab Sessionbeginn – Indikatoren haben am Fensterbeginn ihren damaligen Zustand). Standard: Pause 60 s
// vor dem Signal. Die Geschwindigkeit ändert nur die Darstellung; jede Anzeige stammt aus dem aufgezeichneten Zustand bei
// der Cursor-Position – beim Zurückspulen verschwinden spätere Marker, Ergebnisse und Informationen wieder.
import { useEffect, useMemo, useRef, useState } from "react";
import { TickReplayChart, type ReplayMarker, type ReplaySlot } from "@/components/research/TickReplayChart";
import { mtfApi, visibleBars, stateAt, knownAt, type MtfReplayWindow, type MtfRunResult, type ViewKey, type DecisionTrace } from "@/lib/mtfApi";

const btn = "h-[28px] rounded-md border border-[var(--line-2)] px-2.5 text-xs text-[var(--fg)] disabled:opacity-40";
const btnOn = "h-[28px] rounded-md bg-[var(--key)] px-2.5 text-xs font-semibold text-black disabled:opacity-40";
const SPEEDS = [1, 5, 25, 100];
const n = (v: number | null | undefined, d = 2) => (v == null ? "–" : Number(v).toLocaleString("de-DE", { maximumFractionDigits: d }));

export function MtfTradeReplay({ run, trade, onClose, onTrade }: { run: MtfRunResult; trade: number; onClose: () => void; onTrade: (t: number) => void }) {
  const [preroll, setPreroll] = useState(60);
  const [w, setW] = useState<MtfReplayWindow | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [cursor, setCursor] = useState(0);
  const [view, setView] = useState<ViewKey>("rangeXV");
  const [playing, setPlaying] = useState(false);
  const [speed, setSpeed] = useState(5);
  const t = run.trades[trade];

  useEffect(() => {
    const ac = new AbortController();
    setW(null); setErr(null); setPlaying(false);
    mtfApi.replay(run.runId, trade, preroll, 30, ac.signal).then((x) => { setW(x); setCursor(x.fromEvent); }).catch((e) => !ac.signal.aborted && setErr(String(e?.message ?? e)));
    return () => ac.abort();
  }, [run.runId, trade, preroll]);

  // Abspielen: Marktzeit läuft speed-fach; der Cursor springt auf das letzte Ereignis bis zu dieser Marktzeit.
  const idxOf = useMemo(() => new Map((w?.events ?? []).map((e, i) => [e[0], i])), [w]);
  const timer = useRef<ReturnType<typeof setInterval> | null>(null);
  useEffect(() => {
    if (!playing || !w) return;
    timer.current = setInterval(() => {
      setCursor((c) => {
        const i = idxOf.get(c) ?? 0;
        const tNow = Date.parse(w.events[i][1]) + 100 * speed;
        let j = i;
        while (j + 1 < w.events.length && Date.parse(w.events[j + 1][1]) <= tNow) j++;
        if (j === i && i + 1 < w.events.length && Date.parse(w.events[i + 1][1]) - Date.parse(w.events[i][1]) > 100 * speed) return c;
        if (j >= w.events.length - 1) setPlaying(false);
        return w.events[Math.max(j, i)][0];
      });
    }, 100);
    return () => { if (timer.current) clearInterval(timer.current); };
  }, [playing, speed, w, idxOf]);

  if (err) return <div className="p-4 text-xs text-[var(--red)]" data-testid="mtf-replay-error">Trade nachspielen nicht möglich: {err} <button className={btn} onClick={onClose}>zurück</button></div>;
  if (!w) return <div className="p-4 text-xs text-[var(--fg-dim)]" data-testid="mtf-replay-loading">Lauf wird ab Sessionbeginn deterministisch wiederholt, damit VWAP, CVD, Profile und Strategie am Fensterbeginn ihren damaligen Zustand haben …</div>;

  const step = (d: number) => { const i = idxOf.get(cursor) ?? 0; const j = Math.min(w.events.length - 1, Math.max(0, i + d)); setCursor(w.events[j][0]); };
  const nextTradeTick = () => { const i = idxOf.get(cursor) ?? 0; for (let j = i + 1; j < w.events.length; j++) if (w.events[j][3] > 0) { setCursor(w.events[j][0]); return; } };
  const ev = w.events[idxOf.get(cursor) ?? 0];
  const st = stateAt(w, cursor);
  const bars = visibleBars(w, view, cursor);
  const slots: ReplaySlot[] = bars.map((b) => ({
    label: `${view === "rangeXV" ? "R" : ""}${b.index}`, range: `${view} #${b.index} · ${b.status}`, status: b.status === "offen" ? "laufend" : "abgeschlossen",
    candle: { bucket: b.index, firstIdx: 0, lastIdx: 0, open: b.open, high: b.high, low: b.low, close: b.close, volume: b.volume, buyVolume: b.buy, sellVolume: b.sell, trades: 0 },
  }));
  const slotOfEvent = (e: number) => {
    const cl = (w.closedBars[view] ?? []).find((b) => b[9] <= e && e < b[10]);
    const idx = cl ? cl[0] : bars[bars.length - 1]?.index;
    return bars.findIndex((b) => b.index === idx);
  };
  const tracesKnown = knownAt(w.traces, cursor);
  const fills = knownAt(w.transitions, cursor);
  const markers: ReplayMarker[] = [];
  const sig = w.traces.find((x) => x.eventIndex === t.signalEvent);
  if (sig && cursor >= sig.eventIndex) markers.push({ kind: "signal", slot: slotOfEvent(sig.eventIndex), price: sig.price, side: t.side, label: `Signal #${sig.eventIndex}` });
  if (t.entryFillEvent != null && cursor >= t.entryFillEvent) markers.push({ kind: "entry", slot: slotOfEvent(t.entryFillEvent), price: t.entryPrice, side: t.side, label: `Einstieg ${t.entryPrice}` });
  if (t.exitFillEvent != null && cursor >= t.exitFillEvent) markers.push({ kind: "exit", slot: slotOfEvent(t.exitFillEvent), price: t.exitPrice, side: t.side, label: `Ausstieg ${t.exitPrice}` });
  const phase = t.signalEvent != null && cursor < t.signalEvent ? "vor dem Signal" : t.entryFillEvent != null && cursor < t.entryFillEvent ? "Signal gegeben, Order offen (Fill frühestens beim nächsten Ereignis)" : t.exitFillEvent != null && cursor < t.exitFillEvent ? "Position offen – Ergebnis noch unbekannt" : "Trade abgeschlossen";
  const jumps: [string, number | null][] = [["Vorlauf", w.fromEvent], ["Signal", t.signalEvent], ["Einstieg", t.entryFillEvent], ["Ausstieg", t.exitFillEvent]];

  return (
    <div className="mtf-fade space-y-2 p-4" data-testid="mtf-replay" data-cursor={cursor} data-view={view}
      data-state={JSON.stringify({ cursor, phase, markers: markers.map((m) => m.kind), bars: bars.length, open: bars[bars.length - 1]?.status, decisions: tracesKnown.length, fills: fills.length, reproduced: w.reproduced })}>
      <section className="rounded-lg border border-[var(--cyan)] bg-[var(--panel)] p-3 space-y-2">
        <div className="flex flex-wrap items-center gap-2 text-[11px]">
          <button className={btn} onClick={onClose}>← Ergebnis</button>
          <b className="text-[13px] text-[var(--fg)]">Trade #{t.index + 1} · {t.side} · {run.dataCheck?.label}</b>
          <span className="rounded border px-1.5 font-semibold" style={{ borderColor: "var(--gold)", color: "var(--gold)" }}>{run.runKind}</span>
          <span className={w.reproduced ? "text-[var(--key)]" : "text-[var(--red)]"} data-testid="mtf-reproduced">{w.reproduced ? "✓ Wiederholung identisch mit dem gespeicherten Lauf" : `✗ Abweichung: ${w.firstDifference}`}</span>
          <span className="ml-auto flex items-center gap-1">Vorlauf <input type="number" className="h-[26px] w-16 rounded border border-[var(--line-2)] bg-[var(--bg-2)] px-1" value={preroll} min={5} max={1800} onChange={(e) => setPreroll(Math.max(5, +e.target.value))} /> s
            <button className={btn} disabled={trade <= 0} onClick={() => onTrade(trade - 1)}>‹ Trade</button><button className={btn} disabled={trade >= run.trades.length - 1} onClick={() => onTrade(trade + 1)}>Trade ›</button></span>
        </div>
        <div className="flex flex-wrap items-center gap-1.5">
          {playing ? <button className={btnOn} onClick={() => setPlaying(false)} data-testid="mtf-pause">❚❚ Pause</button> : <button className={btnOn} onClick={() => setPlaying(true)} data-testid="mtf-play">▶ Play</button>}
          <button className={btn} onClick={() => step(-1)} data-testid="mtf-back">‹ 1 Ereignis</button>
          <button className={btn} onClick={() => step(1)} data-testid="mtf-step">1 Ereignis ›</button>
          <button className={btn} onClick={nextTradeTick} title="Nächstes Ereignis mit gehandeltem Volumen (in diesem Datensatz sind alle Ereignisse Trades)">1 Trade-Tick ›</button>
          {SPEEDS.map((s) => <button key={s} className={speed === s ? btnOn : btn} onClick={() => setSpeed(s)}>{s}×</button>)}
          <span className="mx-1 h-4 w-px bg-[var(--line-2)]" />
          {jumps.map(([l, e]) => <button key={l} className={btn} disabled={e == null} onClick={() => { setPlaying(false); if (e != null) setCursor(e); }} data-testid={`mtf-jump-${l}`}>→ {l}</button>)}
          <span className="mx-1 h-4 w-px bg-[var(--line-2)]" />
          {(["rangeXV", "1m", "5m"] as ViewKey[]).map((v) => <button key={v} className={view === v ? btnOn : btn} onClick={() => setView(v)} data-testid={`mtf-view-${v}`}>{v === "rangeXV" ? "4RxV" : v}</button>)}
        </div>
        <input type="range" className="w-full" min={w.fromEvent} max={w.toEvent} value={cursor} onChange={(e) => { setPlaying(false); setCursor(+e.target.value); }} data-testid="mtf-slider" />
        <div className="flex flex-wrap gap-3 text-[11px]">
          <span className="font-mono text-[var(--fg)]">Ereignis {cursor} · {ev?.[1]?.slice(11, 23)} UTC · {ev?.[2]} · Vol {ev?.[3]} · {ev?.[4] > 0 ? "Buy" : ev?.[4] < 0 ? "Sell" : "?"}</span>
          <span className="text-[var(--gold)]" data-testid="mtf-phase">{phase}</span>
          {view === "rangeXV" && <span className="text-[var(--fg-faint)]">{run.rangeSemantics}</span>}
        </div>
      </section>

      <div className="grid gap-2 xl:grid-cols-[minmax(0,1fr)_360px]">
        <div className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-2">
          <TickReplayChart slots={slots} currentSlot={bars.length && bars[bars.length - 1].status === "offen" ? bars.length - 1 : null} lastPrice={ev?.[2] ?? null} follow markers={markers} height={440} />
        </div>
        <ThreeViews st={st} />
      </div>
      <DecisionPanel traces={tracesKnown} signalEvent={t.signalEvent} cursor={cursor} trade={t} />
    </div>
  );
}

function BarLine({ label, b }: { label: string; b: number[] | undefined }) {
  if (!b || !b.length) return <div><span className="text-[var(--fg-faint)]">{label}</span> –</div>;
  return <div className="font-mono"><span className="text-[var(--fg-faint)]">{label} #{b[0]} </span>O {b[1]} H {b[2]} L {b[3]} C {b[4]} · Vol {b[5]} · Δ {b[6] - b[7]}</div>;
}

function ThreeViews({ st }: { st: any[] | null }) {
  if (!st) return <div className="rounded-lg border border-[var(--line)] p-2 text-[11px]">kein Zustand</div>;
  return (
    <div className="space-y-1 rounded-lg border border-[var(--line)] bg-[var(--panel)] p-2 text-[11px]" data-testid="mtf-three-views">
      <b className="text-[var(--fg)]">Was der Bot bei Ereignis {st[0]} sah (offene Bars = bisherige Werte)</b>
      <BarLine label="5m offen" b={st[1]} /><BarLine label="1m offen" b={st[2]} /><BarLine label="4RxV offen" b={st[3]} />
      <div className="mt-1 grid grid-cols-2 gap-x-2 font-mono text-[var(--fg-dim)]">
        <span>VWAP {n(st[4])}</span><span>CVD {n(st[5], 0)}</span>
        <span>Vol-POC {n(st[6])}</span><span>VAH/VAL {n(st[7])} / {n(st[8])}</span>
        <span>TPO-POC {n(st[9])}</span><span>TPO VAH/VAL {n(st[10])} / {n(st[11])}</span>
        <span>Swing-Hoch {n(st[12])}{st[13] != null ? ` (bestätigt #${st[13]})` : ""}</span><span>Swing-Tief {n(st[14])}{st[15] != null ? ` (bestätigt #${st[15]})` : ""}</span>
      </div>
      <div className="text-[var(--fg-dim)]">Absorption: {st[16] ?? "keine Beobachtung"}</div>
      <div className="text-[10px] text-[var(--fg-faint)]">VWAP/CVD/Profile: von uns ab Sessionbeginn berechnet (keine ausgelesene ATAS-Linie). Sichtbar ≠ benutzt: welche Werte eine Regel auswertete, steht in der Entscheidung.</div>
    </div>
  );
}

function DecisionPanel({ traces, signalEvent, cursor, trade }: { traces: DecisionTrace[]; signalEvent: number | null; cursor: number; trade: MtfRunResult["trades"][number] }) {
  const last = traces[traces.length - 1];
  return (
    <section className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3 text-[11px]" data-testid="mtf-decisions">
      <b className="text-[12px] text-[var(--fg)]">Entscheidungen bis Ereignis {cursor} (gespeichert beim Entscheiden)</b>
      {signalEvent != null && cursor < signalEvent && <div className="text-[var(--fg-dim)]">Signal dieses Trades noch nicht erreicht – seine Gründe, Fills und das Ergebnis sind hier noch nicht bekannt.</div>}
      {!last ? <div className="text-[var(--fg-dim)]">Im Fenster noch keine Auswertung.</div> : (
        <div className="mt-1 space-y-1">
          <div className="text-[var(--fg)]">{last.kind} bei Ereignis {last.eventIndex} ({last.marketTime.slice(11, 23)} UTC): {last.summary}</div>
          <table className="w-full"><thead className="text-left text-[var(--fg-faint)]"><tr>{["Rolle", "Regel", "Sicht/Status", "Ist", "Soll", "Ergebnis"].map((h) => <th key={h} className="pr-2 font-normal">{h}</th>)}</tr></thead>
            <tbody>{last.checks.map((c) => <tr key={c.rule}><td className="pr-2">{c.role}</td><td className="pr-2">{c.rule}</td><td className="pr-2">{c.view} · {c.barStatus}</td><td className="pr-2 font-mono">{c.actual}</td><td className="pr-2">{c.expected}</td>
              <td className={c.result === "erfüllt" ? "text-[var(--key)]" : "text-[var(--red)]"}>{c.result}</td></tr>)}</tbody></table>
          <div className="text-[var(--fg-faint)]">Von der Regel benutzt: {last.contextUsed.join(" · ")}</div>
        </div>
      )}
      {trade.exitFillEvent != null && cursor >= trade.exitFillEvent && <div className="mt-1 text-[var(--fg-dim)]" data-testid="mtf-result-known">Ergebnis jetzt bekannt: Netto {n(trade.netPnL)} USD (Gebühren {n(trade.fees)}).</div>}
    </section>
  );
}
