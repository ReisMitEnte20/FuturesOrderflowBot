// Chart & Replay → „London + New York“: gemeinsames Tick-Replay der Sessions eines Handelstags (optional mit Folgetag)
// mit den GESPEICHERTEN Fills eines London+New-York-Laufs. Eine Marktuhr; am Sessionende kurzer sichtbarer Übergang
// („London beendet · 3 h 30 min übersprungen · New York beginnt“), dann sofort der erste echte New-York-Tick. Während der
// Pause werden keine New-York-Kerzen, -Werte oder -Trades gezeigt. Play rechnet keine Strategie neu. Keine Orders.
import { useEffect, useMemo, useState } from "react";
import { cn } from "@/lib/utils";
import { money, num } from "@/lib/backtestingApi";
import { TickReplayChart, type ReplayMarker, type ReplaySlot } from "@/components/research/TickReplayChart";
import { candleStatus, type ReplayCandle } from "@/lib/tickReplay";
import { tickReplayApi, sessionId, type TickReplaySession, type TickReplayMeta } from "@/lib/tickReplayApi";
import { footprint, overlayEvents, overlayReached, isoUnits, type OverlayTrade } from "@/lib/chartReplay";
import { sessionDeltaCvd, slotTemplates, transitionText, vwap, segmentOfIndex, humanDuration } from "@/lib/sessionReplay";
import { sessionsApi, type SessionRunIndexEntry, type SessionRunResult } from "@/lib/sessionsApi";
import { useSessionsReplay } from "./useSessionsReplay";
import { FootprintLadder, tzFormats, tzShortOf } from "./DayReplay";
import { UNITS_PER_SEC } from "./useTickReplay";
import { RecordingProvenanceBanner } from "@/components/marktdaten/RecordingProvenanceBanner";

const SPEEDS = [1, 10, 100, 500];
const nf = new Intl.NumberFormat("de-DE");
const short = (m: TickReplayMeta | undefined) => (m?.session === "NewYork" ? "New York" : m?.session || m?.label || "?");
const btn = "h-[28px] px-2.5 rounded-md border border-[var(--line-2)] text-[var(--fg)] text-xs disabled:opacity-40";
const btnOn = "h-[28px] px-2.5 rounded-md bg-[var(--key)] text-black text-xs font-semibold disabled:opacity-40";
const sel = "h-[28px] max-w-[520px] rounded-md border border-[var(--line-2)] bg-[var(--bg-2)] px-2 text-xs text-[var(--fg)]";

export function SessionsReplay({ runId, day, trade, onParams }: {
  runId: string | null; day: string | null; trade: number | null; onParams: (kv: Record<string, string | null>) => void;
}) {
  const [runs, setRuns] = useState<SessionRunIndexEntry[]>([]);
  const [run, setRun] = useState<SessionRunResult | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [market, setMarket] = useState<TickReplaySession[]>([]);
  const [withNext, setWithNext] = useState(false);
  const [view, setView] = useState<"replay" | "overview">("replay");
  const [follow, setFollow] = useState(true);
  const [fpSlot, setFpSlot] = useState<number | null>(null);
  const [focusDone, setFocusDone] = useState<string | null>(null);

  useEffect(() => { sessionsApi.runs().then(setRuns).catch(() => setRuns([])); tickReplayApi.sessions().then((l) => setMarket(l.sessions)).catch(() => setMarket([])); }, []);
  useEffect(() => {
    setRun(null); setErr(null);
    if (!runId) return;
    const ac = new AbortController();
    sessionsApi.get(runId, ac.signal).then(setRun).catch((e) => !ac.signal.aborted && setErr(String(e?.message ?? e)));
    return () => ac.abort();
  }, [runId]);

  // Tage: mit Lauf die gelaufenen Tage; ohne Lauf Tage, an denen London UND New York eindeutig vorliegen.
  const marketDays = useMemo(() => {
    const by = new Map<string, TickReplaySession[]>();
    for (const s of market) by.set(s.day, [...(by.get(s.day) ?? []), s]);
    return [...by.entries()].filter(([, l]) => l.some((s) => s.session === "London") && l.some((s) => (s.session ?? "NewYork") === "NewYork")
      && !l.some((s) => s.selection === "conflict")).map(([d]) => d).sort();
  }, [market]);
  const days = run ? [...new Set(run.segments.map((s) => s.day))] : marketDays;
  const focusTrade = run && trade != null ? run.trades.find((t) => t.index === trade) ?? null : null;
  const curDay = day && days.includes(day) ? day : focusTrade?.day ?? days[0] ?? null;
  const dayIdx = curDay ? days.indexOf(curDay) : -1;
  const shownDays = curDay ? (withNext && dayIdx + 1 < days.length ? [curDay, days[dayIdx + 1]] : [curDay]) : [];
  const ids = run
    ? run.segments.filter((s) => shownDays.includes(s.day)).sort((a, b) => a.windowStartUtc.localeCompare(b.windowStartUtc)).map((s) => s.id)
    : market.filter((s) => shownDays.includes(s.day)).sort((a, b) => a.windowStartUtc.localeCompare(b.windowStartUtc)).map((s) => sessionId(s));
  const r = useSessionsReplay(ids.length ? ids : null);
  const { layout: L, store: st, eng, overview: ov, clock, metas } = r;
  const isOverview = view === "overview";
  const source = isOverview ? ov : eng;
  const multiDay = shownDays.length > 1;
  const segName = (k: number) => `${short(metas[k])}${multiDay ? ` ${metas[k]?.day ?? ""}` : ""}`;
  useEffect(() => { setFpSlot(null); }, [ids.join("|"), view]); // eslint-disable-line react-hooks/exhaustive-deps

  // ---------------- Slots: Kerzen je Session, ein Pausen-Slot dazwischen ----------------
  const templates = useMemo(() => {
    if (!L || !metas.length) return [];
    return slotTemplates(L.segs, L.bucketUnits,
      (u, k) => tzFormats(metas[k]?.timezone || "UTC").hm.format(L.baseAnchorMs + u / 10_000),
      (k) => `Pause ${segName(k - 1)} → ${segName(k)} (übersprungen)`);
  }, [L, metas, multiDay]); // eslint-disable-line react-hooks/exhaustive-deps
  const bucketToSlot = useMemo(() => { const m = new Map<number, number>(); templates.forEach((t, i) => t.kind === "bucket" && m.set(t.bucket, i)); return m; }, [templates]);
  const byBucket = new Map<number, ReplayCandle>();
  if (source) for (const c of source.candles()) byBucket.set(c.bucket, c);
  const exhausted = isOverview || (!!st && !!eng && st.loaded >= st.total && eng.p >= st.total);
  const slots: ReplaySlot[] = templates.map((t) => {
    if (t.kind === "pause") return { label: t.label, range: t.range, candle: null, status: null };
    const c = byBucket.get(t.bucket) ?? null;
    const sg = L!.segs[t.seg];
    return { label: t.label, range: t.range, candle: c, status: c ? candleStatus(c, L!.bucketUnits, isOverview ? sg.endUnits : clock, sg.startUnits, sg.endUnits, exhausted) : null };
  });
  const currentSlot = !isOverview && eng?.cur ? bucketToSlot.get(eng.cur.bucket) ?? null : null;
  const delta = sessionDeltaCvd(templates.map((t, i) => ({ seg: t.seg, candle: slots[i]?.candle ?? null })));
  const fpIndex = fpSlot ?? currentSlot;
  const fpCandle = fpIndex != null ? slots[fpIndex]?.candle ?? null : null;
  const fp = fpCandle && st ? footprint(st, fpCandle.firstIdx, fpCandle.lastIdx + 1) : [];

  // ---------------- Gespeicherte Fills des Laufs (nur angezeigte Sessions) ----------------
  const shownTrades = useMemo(() => (run ? run.trades.filter((t) => ids.includes(t.segmentId)) : []), [run, ids.join("|")]); // eslint-disable-line react-hooks/exhaustive-deps
  const overlay: OverlayTrade[] = shownTrades.map((t) => ({
    index: t.index, side: t.side, qty: t.qty, entryIso: t.entryTimeUtc, entryMs: Date.parse(t.entryTimeUtc), entryPrice: t.entryPrice,
    exitIso: t.exitTimeUtc, exitMs: Date.parse(t.exitTimeUtc), exitPrice: t.exitPrice, exitReason: t.exitReason, signalIso: null,
  }));
  const events = L ? overlayEvents(overlay, L.baseAnchorMs) : [];
  const reached = events.map((e) => isOverview || (!!st && !!eng && overlayReached(e, st, eng.p)));
  const markers: ReplayMarker[] = [];
  if (L) events.forEach((e, i) => {
    if (!reached[i] || e.price == null) return;
    const slot = bucketToSlot.get(Math.floor(e.units / L.bucketUnits));
    if (slot == null) return;
    markers.push({ kind: e.kind, slot, price: e.price, side: e.side, label: `${e.kind === "entry" ? "Entry" : "Exit"} #${e.trade + 1} ${e.price}` });
  });
  const tradeReached = (idx: number) => events.some((e, i) => e.trade === idx && e.kind === "entry" && reached[i]);
  const exitReached = (idx: number) => events.some((e, i) => e.trade === idx && e.kind === "exit" && reached[i]);
  // Konto zur Replay-Uhr: Start + Netto aller Trades, deren Ausstieg bereits erreicht ist (ein Konto über alle Sessions).
  const balanceNow = run ? run.startBalance + run.trades.filter((t) => !ids.includes(t.segmentId) ? t.exitTimeUtc < (run.segments.find((s) => s.id === ids[0])?.windowStartUtc ?? "") : exitReached(t.index)).reduce((a, t) => a + t.netPnL, 0) : null;

  // Fokus aus dem Journal (?trade=n): einmal nach dem Laden kurz vor den Einstieg springen.
  useEffect(() => {
    if (!focusTrade || !L || !st || st.loaded < st.total || focusDone === `${run?.runId}:${trade}`) return;
    const u = isoUnits(focusTrade.entryTimeUtc, L.baseAnchorMs);
    if (u == null) return;
    r.seekTime(u); // genau auf den Einstiegs-Fill: Marker sofort sichtbar, Ausstieg erst nach Weiterspielen
    setFocusDone(`${run?.runId}:${trade}`);
  }, [focusTrade, L, st?.loaded, st?.total]); // eslint-disable-line react-hooks/exhaustive-deps

  // ---------------- Uhr, aktuelle Session, Übergang ----------------
  const k = L && eng ? segmentOfIndex(L.segs, Math.max(0, eng.p - 1)) : 0;
  const curMeta = metas[k];
  const clockMs = L ? L.baseAnchorMs + clock / 10_000 : 0;
  const tz = curMeta?.timezone || "UTC";
  const fmtClock = L ? `${tzFormats(tz).full.format(clockMs)}.${String(Math.floor(clock % UNITS_PER_SEC)).padStart(7, "0").slice(0, 3)} ${tzShortOf(tz, clockMs)}` : "–";
  const trans = r.holding ?? (r.inPause ? { from: r.inPause.from, to: r.inPause.to, lastTick: r.lastTick(r.inPause.from) ?? 0, firstTick: r.firstTick(r.inPause.to) ?? 0, atWall: 0 } : null);
  const transMsg = trans ? transitionText(segName(trans.from), segName(trans.to), (trans.firstTick - trans.lastTick) / 10_000) : null;
  const lastMsg = !trans && r.lastTransition ? transitionText(segName(r.lastTransition.from), segName(r.lastTransition.to), (r.lastTransition.firstTick - r.lastTransition.lastTick) / 10_000) : null;
  const ready = !!(L && st && eng);
  const lastIdx = eng && eng.p > 0 ? eng.p - 1 : -1;
  const lastPrice = st && lastIdx >= 0 && !isOverview && !trans ? st.price[lastIdx] : null;
  const segTicks = (s: number) => (L && eng ? Math.max(0, Math.min(L.segs[s].count, (isOverview ? st!.total : eng.p) - L.segs[s].firstIdx)) : 0);
  const stateAttr = ready ? JSON.stringify({
    p: eng!.p, total: st!.total, loaded: st!.loaded, clock, seg: k, atEnd: r.atEnd, playing: r.playing, hold: !!r.holding, pause: !!r.inPause,
    segs: L!.segs.map((s, i) => ({ id: s.id, applied: segTicks(i), candles: [...byBucket.values()].filter((c) => c.firstIdx >= s.firstIdx && c.firstIdx < s.firstIdx + s.count).length })),
    markers: markers.length, transition: transMsg,
    // Marker des fokussierten Journal-Trades (Prüfbarkeit: unsichtbar vor dem Fill, beim Fill zum Journalpreis).
    focus: focusTrade ? { trade: focusTrade.index, markers: markers.filter((m) => m.label.includes(`#${focusTrade.index + 1} `)).map((m) => ({ kind: m.kind, price: m.price })) } : null,
    lastTickIso: st && lastIdx >= 0 && L ? new Date(L.baseAnchorMs + st.t[lastIdx] / 10_000).toISOString() : null,
  }) : "";

  return (
    <div className="space-y-3" data-testid="sessions-replay" data-replay-state={stateAttr}>
      <section className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3 space-y-2">
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-[11px] text-[var(--fg-faint)]">Lauf</span>
          <select value={runId ?? ""} className={sel} onChange={(e) => onParams({ srun: e.target.value || null, trade: null, day: null })} data-testid="ln-run">
            <option value="">– nur Markt (kein Lauf) –</option>
            {runs.map((x) => <option key={x.runId} value={x.runId}>{x.runId} · {x.from}–{x.to} · {x.trades} Trades · Netto {money(x.netPnL)}</option>)}
          </select>
          <span className="text-[11px] text-[var(--fg-faint)]">Handelstag</span>
          <button className={btn} disabled={dayIdx <= 0} onClick={() => onParams({ day: days[dayIdx - 1], trade: null })}>◀</button>
          <select value={curDay ?? ""} className={sel} onChange={(e) => onParams({ day: e.target.value, trade: null })} data-testid="ln-day">
            {days.map((d) => <option key={d} value={d}>{d}</option>)}
          </select>
          <button className={btn} disabled={dayIdx < 0 || dayIdx >= days.length - 1} onClick={() => onParams({ day: days[dayIdx + 1], trade: null })}>▶</button>
          <label className="flex items-center gap-1 text-[11px] text-[var(--fg-dim)]"><input type="checkbox" checked={withNext} onChange={(e) => setWithNext(e.target.checked)} disabled={dayIdx < 0 || dayIdx >= days.length - 1} data-testid="ln-next" /> Folgetag anschließen</label>
        </div>
        {err && <p className="text-xs text-[var(--red)]">{err}</p>}
        {!run && !days.length && <p className="text-xs text-[var(--fg-dim)]">Kein Tag mit eindeutiger London- und New-York-Aufnahme gefunden.</p>}
        {metas.length > 0 && <p className="text-[10px] text-[var(--fg-faint)]">
          {metas.map((m) => `${short(m)} ${m.day}: ${m.collection} · Aufnahme ${m.recordingId || "?"} · Kontrakt ${m.contract || "?"} · ${nf.format(m.count)} Ticks · Fenster ${m.windowStartUtc.slice(11, 16)}–${m.windowEndUtc.slice(11, 16)} UTC`).join("  |  ")}
        </p>}
        {(r.load.phase === "prepare" || r.load.phase === "load") && (
          <div className="space-y-1">
            <div className="text-[11px] text-[var(--fg-dim)]">{r.load.phase === "prepare" ? "Server bereitet die Sessions vor (beim ersten Mal je Session einmaliger Cache-Aufbau) …" : `Lade Ticks: ${nf.format(r.load.loaded)} / ${nf.format(r.load.total)}`}</div>
            <div className="h-1.5 w-full overflow-hidden rounded bg-[var(--panel-3)]"><div className="h-full bg-[var(--key)]" style={{ width: r.load.total ? `${(100 * r.load.loaded) / r.load.total}%` : "8%" }} /></div>
          </div>
        )}
        {r.load.phase === "error" && <p className="text-xs text-[var(--red)]">Laden fehlgeschlagen (fail-closed): {r.load.msg}</p>}
      </section>

      {ready && L && st && eng && (
        <section className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3 space-y-2">
          <div className="flex flex-wrap items-center gap-1.5">
            <div className="flex rounded-md border border-[var(--line-2)] p-0.5" role="tablist">
              {(["overview", "replay"] as const).map((v) => (
                <button key={v} role="tab" aria-selected={view === v} onClick={() => { if (v === "overview") r.pause(); setView(v); }} data-testid={`ln-view-${v}`}
                  className={cn("h-[26px] rounded px-3 text-[12px]", view === v ? "bg-grad-brand font-semibold text-[var(--bg)]" : "text-[var(--fg-dim)] hover:text-[var(--fg)]")}>
                  {v === "overview" ? "Übersicht" : "Replay"}
                </button>))}
            </div>
            {!isOverview && <>
              <span className="mx-1 h-4 w-px bg-[var(--line-2)]" />
              {r.playing ? <button className={btnOn} onClick={r.pause} data-testid="ln-pause">❚❚ Pause</button>
                : <button className={btnOn} onClick={r.play} disabled={r.atEnd} data-testid="ln-play">▶ Play</button>}
              <button className={btn} onClick={r.stepTick} disabled={eng.p >= st.total} data-testid="ln-step">1 Tick weiter ›</button>
              <button className={btn} onClick={r.reset} data-testid="ln-reset">⏮ Anfang</button>
              <span className="mx-1 h-4 w-px bg-[var(--line-2)]" />
              {SPEEDS.map((s) => <button key={s} className={r.speed === s ? btnOn : btn} onClick={() => r.setSpeed(s)} data-testid={`ln-speed-${s}`}>{s}×</button>)}
              <span className="mx-1 h-4 w-px bg-[var(--line-2)]" />
              {[-300, -60, 60, 300].map((d) => <button key={d} className={btn} onClick={() => r.seekTime(clock + d * UNITS_PER_SEC)}>{d < 0 ? "−" : "+"}{Math.abs(d) / 60} min</button>)}
              {L.segs.map((s, i) => <button key={s.id} className={btn} onClick={() => r.seekTime(s.startUnits)} data-testid={`ln-goto-seg-${i}`}>→ {segName(i)}</button>)}
              <label className="ml-1 flex items-center gap-1 text-[11px] text-[var(--fg-dim)]"><input type="checkbox" checked={follow} onChange={(e) => setFollow(e.target.checked)} /> folgen</label>
            </>}
          </div>
          {!isOverview && <input type="range" className="w-full" min={L.segs[0].startUnits} max={L.segs[L.segs.length - 1].endUnits} step={1} value={clock} onChange={(e) => r.seekTime(Number(e.target.value))} data-testid="ln-slider" />}

          {transMsg && !isOverview && (
            <div className="rounded-md border border-[var(--gold)] bg-[rgba(240,195,90,0.08)] px-3 py-2 text-[13px] font-semibold text-[var(--gold)]" data-testid="ln-transition" data-phase={r.holding ? "hold" : "pause"}>
              ⏭ {transMsg}
              <div className="text-[11px] font-normal text-[var(--fg-dim)]">Keine Kurse, Ticks oder Fills in der Pause – die nächste Session erscheint erst mit ihrem ersten echten Tick. Es wird keine reale Zeit abgewartet.</div>
            </div>
          )}

          <div className="grid gap-2 text-[11px] md:grid-cols-4">
            <div className="rounded-md border border-[var(--line)] bg-[var(--panel-3)] p-2" data-testid="ln-clock">
              <div className="text-[var(--fg-faint)]">{isOverview ? "Übersicht (alle Ticks)" : "Marktuhr (Replay)"}</div>
              <div className="font-mono text-sm text-[var(--fg)]">{isOverview ? "–" : fmtClock}</div>
              <div className="text-[var(--fg-dim)]">{isOverview ? "" : trans ? "Pause (übersprungen)" : segName(k)} · {nf.format(isOverview ? st.total : eng.p)} / {nf.format(st.total)} Ticks</div>
              {lastMsg && !isOverview && <div className="text-[var(--fg-faint)]" data-testid="ln-last-transition">Zuletzt: {lastMsg}</div>}
              {r.playing && r.waiting && <div className="text-[var(--gold)]">wartet auf Nachladen – kein Tick wird übersprungen</div>}
              {!isOverview && r.atEnd && <div className="text-[var(--fg-dim)]" data-testid="ln-end">Ende der letzten Session – pausiert (kein Loop)</div>}
            </div>
            {L.segs.map((s, i) => {
              const n = segTicks(i);
              const started = n > 0;
              const cvd = (() => { let c = 0; for (let j = 0; j < templates.length; j++) if (templates[j].seg === i && slots[j].candle) c += slots[j].candle!.buyVolume - slots[j].candle!.sellVolume; return c; })();
              return (
                <div key={s.id} className="rounded-md border border-[var(--line)] bg-[var(--panel-3)] p-2" data-testid={`ln-seg-${i}`}>
                  <div className="text-[var(--fg-faint)]">{segName(i)} · {metas[i]?.windowStartUtc.slice(11, 16)}–{metas[i]?.windowEndUtc.slice(11, 16)} UTC</div>
                  <RecordingProvenanceBanner recordingId={metas[i]?.recordingId} compact />
                  {started ? <div className="font-mono text-[var(--fg)]">
                    <div>{nf.format(n)} / {nf.format(s.count)} Replay-Ereignisse</div>
                    <div className="text-[var(--fg-dim)]">Session-VWAP {num(vwap(st, s.firstIdx, s.firstIdx + n))} · Session-CVD {nf.format(cvd)}</div>
                  </div> : <div className="text-[var(--fg-dim)]">noch nicht begonnen – keine Werte</div>}
                </div>
              );
            })}
          </div>
          {balanceNow != null && <div className="text-[11px] text-[var(--fg-dim)]" data-testid="ln-balance">Konto zur Replay-Uhr (gespeicherte Fills, ein Konto über alle Sessions): <b className="text-[var(--fg)]">{money(balanceNow)}</b></div>}

          <div className="grid gap-2 xl:grid-cols-[minmax(0,1fr)_280px]">
            <TickReplayChart slots={slots} currentSlot={currentSlot} lastPrice={lastPrice} follow={follow && !isOverview} markers={markers} delta={delta} height={520}
              onSlotClick={(i) => setFpSlot(templates[i]?.kind === "bucket" ? i : null)} selectedSlot={fpIndex} />
            <FootprintLadder levels={fp} title={fpIndex != null && slots[fpIndex] ? slots[fpIndex].range : ""} partial={!isOverview && fpIndex === currentSlot && !r.atEnd} />
          </div>
          <p className="text-[10px] leading-relaxed text-[var(--fg-faint)]">
            5-Minuten-Kerzen je Session in ihrer Börsenzeit; der Slot „⏸“ steht für die übersprungene Pause (keine Kerze, keine Werte). Delta je Kerze, CVD und VWAP starten je Session neu.
            Marker = gespeicherte Fills des Laufs, sichtbar erst ab Erreichen der Marktuhr. DOM/BBO werden nicht angeboten (nur Trades mit Aggressor).
          </p>
        </section>
      )}

      {run && ready && (
        <section className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3 space-y-1.5 text-[11px]" data-testid="ln-replay-journal">
          <b className="text-[12px] text-[var(--fg)]">Journal dieser Sessions ({shownTrades.length} Trades)</b>
          <span className="ml-2 text-[var(--fg-faint)]">Lauf {run.runId} · Klick springt zum Einstieg (in der richtigen Session und Aufnahme)</span>
          <div className="max-h-[260px] overflow-y-auto">
            <table className="w-full font-mono">
              <thead className="sticky top-0 bg-[var(--panel)]"><tr className="text-left text-[var(--fg-faint)]">{["#", "Session", "Aufnahme", "Seite", "Einstieg", "Ausstieg", "Netto", "Grund", "Stand"].map((h) => <th key={h} className="pr-2 font-normal">{h}</th>)}</tr></thead>
              <tbody>{shownTrades.map((t) => {
                const seen = isOverview || tradeReached(t.index);
                return (
                  <tr key={t.index} onClick={() => { const u = L ? isoUnits(t.entryTimeUtc, L.baseAnchorMs) : null; if (u != null) { setView("replay"); r.seekTime(u); onParams({ trade: String(t.index) }); } }}
                    className={cn("cursor-pointer hover:bg-[var(--panel-3)]", t.index === trade && "bg-[rgba(34,184,240,0.10)]", seen ? "text-[var(--fg)]" : "text-[var(--fg-faint)]")} data-testid={`ln-rj-${t.index}`}>
                    <td className="pr-2">{t.index + 1}</td><td className="pr-2">{t.sessionLabel} {multiDay ? t.day : ""}</td><td className="pr-2 text-[10px]">{t.recordingId}</td><td className="pr-2">{t.side}</td>
                    <td className="pr-2">{seen ? `${t.entryTimeUtc.slice(11, 23)} @ ${num(t.entryPrice)}` : "—"}</td>
                    <td className="pr-2">{seen && (isOverview || exitReached(t.index)) ? `${t.exitTimeUtc.slice(11, 23)} @ ${num(t.exitPrice)}` : "—"}</td>
                    <td className="pr-2">{seen && (isOverview || exitReached(t.index)) ? money(t.netPnL) : "—"}</td>
                    <td className="pr-2 text-[10px]">{seen ? t.exitReason : ""}</td><td>{seen ? "erreicht" : "noch nicht erreicht"}</td>
                  </tr>);
              })}</tbody>
            </table>
          </div>
          {run.transitions.filter((t) => ids.includes(t.fromId) && ids.includes(t.toId)).map((t) => (
            <div key={t.fromId + t.toId} className="text-[var(--fg-dim)]">
              Übergang {t.fromLabel} → {t.toLabel}: letzter Tick {t.lastTickUtc.slice(11, 23)} · erster Tick {t.firstTickUtc.slice(11, 23)} · {t.skippedText} übersprungen ({humanDuration(t.skippedSeconds * 1000)}) ·
              Konto {money(t.balanceBefore)} → {money(t.balanceAfter)} · PnL im Sprung {money(t.pnlChangeInJump)} · Trades im Sprung {t.tradesClosedInJump + t.tradesOpenedInJump}
            </div>))}
        </section>
      )}
    </div>
  );
}
