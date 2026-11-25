// Freies Marktreplay: Handelstag wählen → Play. Findet die ATAS-Collections über den konfigurierten Datenordner
// (/api/tickreplay, gleiche geprüfte Tagesauswahl wie der Orderflow-Chart: Drop-Status, offene Konflikte werden
// angezeigt statt still entschieden). Eine Marktuhr steuert Kerzen, Volumen, Delta/CVD und Footprint.
//  - „Tagesübersicht“: alle Kerzen des Tages (aus ALLEN Ticks) und alle Fills eines gewählten Laufs.
//  - „Replay“: nur, was bis zur Marktuhr bekannt war (laufende Kerze baut sich Tick für Tick auf, Fills erst ab Erreichen).
// Ohne gewählten Lauf nur Markt. Mit Lauf: dessen GESPEICHERTE Fills – Play rechnet keine Strategie neu.
// DOM/BBO/Smart Tape werden nicht angeboten: in der Collection nicht angebunden (nur Trades mit Aggressor).
import { useEffect, useMemo, useState } from "react";
import { cn } from "@/lib/utils";
import { TickReplayChart, type ReplayMarker, type ReplaySlot } from "@/components/research/TickReplayChart";
import { candleStatus, type ReplayCandle } from "@/lib/tickReplay";
import { tickReplayApi, sessionId, type TickReplaySession, type TickReplayMeta } from "@/lib/tickReplayApi";
import { backtestingApi, type ReportSummary } from "@/lib/backtestingApi";
import { deltaCvd, footprint, overlayEvents, overlayReached, type OverlayTrade } from "@/lib/chartReplay";
import type { WorkspaceData, WsRunIndexEntry } from "@/lib/workspace";
import { useTickReplay, UNITS_PER_SEC } from "./useTickReplay";

const TZ = "America/New_York";
const SPEEDS = [1, 10, 100, 500];
const nyFmt = new Intl.DateTimeFormat("de-DE", { timeZone: TZ, hour: "2-digit", minute: "2-digit", second: "2-digit", hourCycle: "h23" });
const nyHm = new Intl.DateTimeFormat("de-DE", { timeZone: TZ, hour: "2-digit", minute: "2-digit", hourCycle: "h23" });
const tzFmt = new Intl.DateTimeFormat("en-US", { timeZone: TZ, timeZoneName: "short" });
const tzShort = (ms: number) => tzFmt.formatToParts(ms).find((p) => p.type === "timeZoneName")?.value ?? "";
const nf = new Intl.NumberFormat("de-DE");
const offMs = (m: TickReplayMeta, off: number) => m.anchorUnixMs + off / 10_000;
function fmtTime(m: TickReplayMeta, off: number, digits = 3): string {
  const whole = Math.floor(off / UNITS_PER_SEC);
  const frac = String(Math.floor(off - whole * UNITS_PER_SEC)).padStart(7, "0");
  const s = nyFmt.format(m.anchorUnixMs + whole * 1000);
  return digits > 0 ? `${s}.${frac.slice(0, digits)}` : s;
}
const secOfDay = (s: string) => { const [h, mi, se] = s.split(":").map(Number); return h * 3600 + mi * 60 + (se || 0); };
const SRC_KEY = (jobId: string) => `chartreplay:source:${jobId}`;
const readLs = (k: string) => { try { return localStorage.getItem(k); } catch { return null; } };
const writeLs = (k: string, v: string) => { try { localStorage.setItem(k, v); } catch { /* ohne Speicher weiter */ } };

/** Auswählbarer Lauf für die Überlagerung. */
interface RunOption { key: string; label: string; kind: "report" | "file"; id: string; }

export function DayReplay({ day, onDay, runKey, onRunKey }: {
  day: string | null; onDay: (d: string) => void; runKey: string | null; onRunKey: (k: string | null) => void;
}) {
  const [sessions, setSessions] = useState<TickReplaySession[]>([]);
  const [listErr, setListErr] = useState<string | null>(null);
  const [srcPick, setSrcPick] = useState<Record<string, string>>({});
  const [view, setView] = useState<"replay" | "overview">("replay");
  const [follow, setFollow] = useState(true);
  const [gotoText, setGotoText] = useState("");
  const [fpSlot, setFpSlot] = useState<number | null>(null);

  useEffect(() => {
    const ac = new AbortController();
    tickReplayApi.sessions(ac.signal).then((l) => {
      setSessions(l.sessions);
      if (!l.available) setListErr(l.reason ?? "Keine Handelstage verfügbar.");
      if (!day && l.sessions.length) onDay(l.sessions[l.sessions.length - 1].day);
    }).catch((e) => !ac.signal.aborted && setListErr(`Tick-Replay-API nicht erreichbar (${e?.message ?? e}). Backend auf :5034 starten.`));
    return () => ac.abort();
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  const days = useMemo(() => [...new Set(sessions.map((s) => s.day))], [sessions]);
  const forDay = sessions.filter((s) => s.day === day);
  const conflict = forDay.length > 1 || forDay.some((s) => s.selection === "conflict");
  const chosen = conflict ? forDay.find((s) => sessionId(s) === (srcPick[forDay[0].jobId] ?? readLs(SRC_KEY(forDay[0].jobId)))) ?? null : forDay[0] ?? null;
  const id = chosen ? sessionId(chosen) : null;
  const r = useTickReplay(id);
  const { meta: m, store: st, eng, overview: ov, clock, load } = r;
  const dayIdx = day ? days.indexOf(day) : -1;
  useEffect(() => setFpSlot(null), [id, view]);

  // ---------------- Kerzen-Slots (Tagesübersicht: alle Ticks; Replay: bis zur Marktuhr) ----------------
  const slotTemplates = useMemo(() => {
    if (!m) return [] as { bucket: number; label: string; range: string }[];
    const b0 = Math.floor(m.windowStartUnits / m.bucketUnits), b1 = Math.ceil(m.windowEndUnits / m.bucketUnits) - 1;
    const out: { bucket: number; label: string; range: string }[] = [];
    for (let b = b0; b <= b1; b++) {
      const s = offMs(m, b * m.bucketUnits), e = offMs(m, (b + 1) * m.bucketUnits);
      out.push({ bucket: b, label: nyHm.format(s), range: `${nyHm.format(s)}–${nyHm.format(e)} ${tzShort(s)}` });
    }
    return out;
  }, [m]);
  const isOverview = view === "overview";
  const source = isOverview ? ov : eng;
  let slots: ReplaySlot[] = [];
  let currentSlot: number | null = null;
  const candlesBySlot: (ReplayCandle | null)[] = [];
  if (m && st && source && slotTemplates.length) {
    const byBucket = new Map<number, ReplayCandle>();
    for (const c of source.candles()) byBucket.set(c.bucket, c);
    const exhausted = isOverview || (st.loaded >= st.total && source.p >= st.total);
    const clk = isOverview ? m.windowEndUnits : clock;
    slots = slotTemplates.map((tpl) => {
      const c = byBucket.get(tpl.bucket) ?? null;
      candlesBySlot.push(c);
      return { label: tpl.label, range: tpl.range, candle: c, status: c ? candleStatus(c, m.bucketUnits, clk, m.windowStartUnits, m.windowEndUnits, exhausted) : null };
    });
    if (!isOverview && eng?.cur) currentSlot = eng.cur.bucket - slotTemplates[0].bucket;
  }
  const dc = deltaCvd(candlesBySlot.filter((c): c is ReplayCandle => !!c));
  const delta = { delta: [] as (number | null)[], cvd: [] as (number | null)[] };
  { let k = 0; for (const c of candlesBySlot) { if (c) { delta.delta.push(dc.delta[k]); delta.cvd.push(dc.cvd[k]); k++; } else { delta.delta.push(null); delta.cvd.push(null); } } }

  // Footprint: Replay = laufende Kerze (bis Marktuhr) bzw. angeklickte; Übersicht = angeklickte oder letzte Kerze.
  const fpIndex = fpSlot ?? (isOverview ? (() => { for (let i = candlesBySlot.length - 1; i >= 0; i--) if (candlesBySlot[i]) return i; return null; })() : currentSlot);
  const fpCandle = fpIndex != null ? candlesBySlot[fpIndex] ?? null : null;
  const fp = fpCandle && st ? footprint(st, fpCandle.firstIdx, fpCandle.lastIdx + 1) : [];

  // ---------------- Lauf-Überlagerung (nur gespeicherte Fills) ----------------
  const overlay = useRunOverlay(runKey, day, m);
  const events = useMemo(() => (overlay.trades && m ? overlayEvents(overlay.trades, m.anchorUnixMs) : []), [overlay.trades, m]);
  const reachedEv = st && eng ? events.map((e) => isOverview || overlayReached(e, st, eng.p)) : events.map(() => false);
  const markers: ReplayMarker[] = [];
  if (m && slotTemplates.length) events.forEach((e, i) => {
    if (!reachedEv[i] || e.price == null) return;
    const slot = Math.floor(e.units / m.bucketUnits) - slotTemplates[0].bucket;
    if (slot < 0 || slot >= slotTemplates.length) return;
    markers.push({ kind: e.kind, slot, price: e.price, side: e.side, label: `${e.kind === "entry" ? "Entry" : e.kind === "exit" ? "Exit" : "Signal"} #${e.trade + 1} ${e.price}` });
  });

  const atEnd = !!(m && clock >= m.windowEndUnits);
  const ready = !!(m && st && eng);
  const gotoNy = () => {
    if (!m || !/^\d{1,2}:\d{2}(:\d{2})?$/.test(gotoText.trim())) return;
    const startSec = secOfDay(nyFmt.format(offMs(m, m.windowStartUnits)));
    r.seekTime(m.windowStartUnits + (secOfDay(gotoText.trim()) - startSec) * UNITS_PER_SEC);
  };
  const lastIdx = eng && eng.p > 0 ? eng.p - 1 : -1;
  const lastPrice = st && lastIdx >= 0 && !isOverview ? st.price[lastIdx] : null;
  const cs = (e: { candles(): ReplayCandle[] } | null) => e ? e.candles().map((c) => [c.bucket, c.open, c.high, c.low, c.close, c.volume, c.buyVolume, c.sellVolume, c.trades]) : [];
  const stateAttr = ready ? JSON.stringify({ p: eng!.p, total: st!.total, loaded: st!.loaded, clock, atEnd, playing: r.playing, view, cs: cs(eng) }) : "";
  const overviewAttr = ov ? JSON.stringify({ cs: cs(ov) }) : "";
  const btn = "h-[28px] px-2.5 rounded-md border border-[var(--line-2)] text-[var(--fg)] text-xs disabled:opacity-40";
  const btnOn = "h-[28px] px-2.5 rounded-md bg-[var(--key)] text-black text-xs font-semibold disabled:opacity-40";

  return (
    <div className="space-y-3" data-testid="day-replay" data-replay-state={stateAttr} data-overview-state={overviewAttr}>
      <section className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3 space-y-2">
        <div className="flex flex-wrap items-center gap-2">
          <span className="text-[11px] text-[var(--fg-faint)]">Handelstag</span>
          <button className={btn} disabled={dayIdx <= 0} onClick={() => onDay(days[dayIdx - 1])}>◀</button>
          <input type="date" value={day ?? ""} min={days[0]} max={days[days.length - 1]} onChange={(e) => e.target.value && onDay(e.target.value)} data-testid="day-date"
            className="h-[28px] rounded-md border border-[var(--line-2)] bg-[var(--bg-2)] px-2 text-xs text-[var(--fg)]" />
          <button className={btn} disabled={dayIdx < 0 || dayIdx >= days.length - 1} onClick={() => onDay(days[dayIdx + 1])}>▶</button>
          <select value={day ?? ""} onChange={(e) => onDay(e.target.value)} className="h-[28px] max-w-[320px] rounded-md border border-[var(--line-2)] bg-[var(--bg-2)] px-2 text-xs text-[var(--fg)]">
            {days.map((d) => { const s = sessions.find((x) => x.day === d)!; const c = sessions.filter((x) => x.day === d).length > 1;
              return <option key={d} value={d}>{s.label} · {nf.format(s.rows)} Trades{c ? " · ⚠ Konflikt" : ""}{s.recorderDrops ? " · ⚠ Drops" : ""}</option>; })}
          </select>
          <span className="text-[10px] text-[var(--fg-faint)]">{days.length} Handelstage aus dem Collector-Datenordner</span>
        </div>
        {listErr && <p className="text-xs text-[var(--red)]">{listErr}</p>}
        {day && !forDay.length && sessions.length > 0 && <p className="text-xs text-[var(--gold)]">Für {day} gibt es keinen übernommenen Handelstag (New York 09:30–12:00) in den Collections.</p>}
        {conflict && (
          <div className="rounded border border-[var(--gold)] p-2 text-[11px] space-y-1" data-testid="source-conflict">
            <div className="text-[var(--gold)]">⚠ {forDay[0].selectionNote ?? "Mehrere Fassungen dieses Tages."}</div>
            {forDay.map((s) => {
              const c = s.candidates?.find((x) => x.id === sessionId(s));
              return (
                <label key={sessionId(s)} className="flex items-center gap-2">
                  <input type="radio" name="src" checked={chosen === s} onChange={() => { setSrcPick((p) => ({ ...p, [s.jobId]: sessionId(s) })); writeLs(SRC_KEY(s.jobId), sessionId(s)); }} />
                  <span className="text-[var(--fg)]">{s.collection}</span>
                  <span className="text-[var(--fg-dim)]">Aufnahme {c?.recordingId ?? "?"} · {nf.format(s.rows)} Trades · Recorder-Drops {c?.recorderDrops ?? "unbekannt"}</span>
                </label>
              );
            })}
            {!chosen && <div className="text-[var(--fg-dim)]">Bitte eine Fassung wählen – es wird nichts still ausgewählt.</div>}
          </div>
        )}
        {chosen && !conflict && chosen.selection === "resolved" && <p className="text-[11px] text-[var(--fg-dim)]" data-testid="source-resolved">Quelle: {chosen.selectionNote}</p>}
        {chosen && (chosen.recorderDrops ?? 0) > 0 && <p className="text-[11px] text-[var(--red)]">⚠ Diese Aufnahme meldet {nf.format(chosen.recorderDrops!)} Recorder-Drops – Ticks können fehlen.</p>}
        {chosen && chosen.recorderDrops == null && <p className="text-[11px] text-[var(--gold)]">Drop-Status dieser Aufnahme nicht feststellbar.</p>}
        {m && <p className="text-[10px] text-[var(--fg-faint)]">
          Quelle {m.collection} · Tagesabschnitt {m.sliceBytes || "(Volldatei)"} {m.sliceSha256 ? `SHA256 ${m.sliceSha256.slice(0, 12)}…` : ""} · {nf.format(m.count)} Trades · Vol {nf.format(m.totalVolume)} (Buy {nf.format(m.buyVolume)} / Sell {nf.format(m.sellVolume)}) · Cache {m.cache}{m.prepareSeconds ? ` (${m.prepareSeconds} s)` : ""}
        </p>}
        {(load.phase === "prepare" || load.phase === "load") && (
          <div className="space-y-1" data-testid="day-loading">
            <div className="text-[11px] text-[var(--fg-dim)]">{load.phase === "prepare" ? "Server bereitet den Tag vor (beim ersten Mal: nur dieser Tagesabschnitt wird geprüft und zwischengespeichert) …" : `Lade Ticks: ${nf.format(load.loaded)} / ${nf.format(load.total)}`}</div>
            <div className="h-1.5 w-full overflow-hidden rounded bg-[var(--panel-3)]"><div className="h-full bg-[var(--key)]" style={{ width: load.total ? `${(100 * load.loaded) / load.total}%` : "8%" }} /></div>
          </div>
        )}
        {load.phase === "error" && <p className="text-xs text-[var(--red)]" data-testid="day-error">Laden fehlgeschlagen (fail-closed): {load.msg}</p>}
      </section>

      {ready && m && st && eng && (
        <section className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3 space-y-2">
          <div className="flex flex-wrap items-center gap-1.5">
            <div className="flex rounded-md border border-[var(--line-2)] p-0.5" role="tablist" aria-label="Ansicht">
              {(["overview", "replay"] as const).map((v) => (
                <button key={v} role="tab" aria-selected={view === v} data-testid={`day-view-${v}`} onClick={() => { if (v === "overview") r.pause(); setView(v); }}
                  className={cn("h-[26px] rounded px-3 text-[12px]", view === v ? "bg-grad-brand font-semibold text-[var(--bg)]" : "text-[var(--fg-dim)] hover:text-[var(--fg)]")}>
                  {v === "overview" ? "Tagesübersicht" : "Replay"}
                </button>
              ))}
            </div>
            {!isOverview && <>
              <span className="mx-1 h-4 w-px bg-[var(--line-2)]" />
              {r.playing ? <button className={btnOn} onClick={r.pause} data-testid="dr-pause">❚❚ Pause</button>
                : <button className={btnOn} onClick={r.play} disabled={atEnd} data-testid="dr-play">▶ Play</button>}
              <button className={btn} onClick={r.stepTick} disabled={eng.p >= st.total} data-testid="dr-step">1 Tick weiter ›</button>
              <button className={btn} onClick={r.reset} data-testid="dr-reset">⏮ Anfang</button>
              <span className="mx-1 h-4 w-px bg-[var(--line-2)]" />
              {SPEEDS.map((s) => <button key={s} className={r.speed === s ? btnOn : btn} onClick={() => r.setSpeed(s)} data-testid={`dr-speed-${s}`}>{s}×</button>)}
              <span className="mx-1 h-4 w-px bg-[var(--line-2)]" />
              {[-300, -60, 60, 300].map((d) => <button key={d} className={btn} onClick={() => r.seekTime(clock + d * UNITS_PER_SEC)} data-testid={`dr-jump-${d}`}>{d < 0 ? "−" : "+"}{Math.abs(d) / 60} min</button>)}
              <input value={gotoText} onChange={(e) => setGotoText(e.target.value)} onKeyDown={(e) => e.key === "Enter" && gotoNy()} placeholder="hh:mm:ss ET" data-testid="dr-goto"
                className="h-[28px] w-[96px] rounded-md border border-[var(--line-2)] bg-[var(--panel-3)] px-2 text-xs text-[var(--fg)]" />
              <button className={btn} onClick={gotoNy} data-testid="dr-goto-btn">Gehe zu</button>
              <label className="ml-1 flex items-center gap-1 text-[11px] text-[var(--fg-dim)]"><input type="checkbox" checked={follow} onChange={(e) => setFollow(e.target.checked)} /> folgen</label>
            </>}
          </div>
          {!isOverview && <input type="range" className="w-full" min={m.windowStartUnits} max={m.windowEndUnits} step={1} value={clock} onChange={(e) => r.seekTime(Number(e.target.value))} data-testid="dr-slider" />}

          <div className="grid gap-2 text-[11px] md:grid-cols-3">
            <div className="rounded-md border border-[var(--line)] bg-[var(--panel-3)] p-2" data-testid="dr-clock">
              <div className="text-[var(--fg-faint)]">{isOverview ? "Tagesübersicht (alle Ticks des Tages)" : "Marktuhr (Replay)"}</div>
              <div className="font-mono text-sm text-[var(--fg)]">{isOverview ? `${fmtTime(m, m.windowStartUnits, 0)}–${fmtTime(m, m.windowEndUnits, 0)}` : fmtTime(m, clock)} {tzShort(offMs(m, clock))}</div>
              <div className="text-[var(--fg-dim)]">{m.day} · Position {nf.format(isOverview ? st.total : eng.p)} / {nf.format(st.total)} Ticks{st.loaded < st.total ? ` · geladen ${nf.format(st.loaded)}` : ""}</div>
              {r.playing && r.waiting && <div className="text-[var(--gold)]">wartet auf Nachladen – kein Tick wird übersprungen</div>}
              {r.pending != null && <div className="text-[var(--gold)]">Sprung vorgemerkt – wird angewandt, sobald alle Ticks bis dahin geladen sind</div>}
              {!isOverview && atEnd && <div className="text-[var(--fg-dim)]" data-testid="dr-end">Sessionende erreicht – pausiert (kein Loop)</div>}
              {isOverview && !ov && <div className="text-[var(--gold)]">Übersicht erst nach vollständigem Laden.</div>}
            </div>
            <div className="rounded-md border border-[var(--line)] bg-[var(--panel-3)] p-2" data-testid="dr-candle">
              {(() => {
                const c = fpCandle; const s = fpIndex != null ? slots[fpIndex] : null;
                return <>
                  <div className="text-[var(--fg-faint)]">Kerze {s?.range ?? ""} {s?.status ? `· ${s.status}` : ""}</div>
                  {c ? <div className="font-mono text-[var(--fg)]">
                    <div>O {c.open} · H {c.high} · L {c.low} · C <span style={{ color: c.close >= c.open ? "#3cf0a0" : "#f0566a" }}>{c.close}</span></div>
                    <div className="text-[var(--fg-dim)]">Vol {nf.format(c.volume)} · Buy {nf.format(c.buyVolume)} / Sell {nf.format(c.sellVolume)} · Δ {nf.format(c.buyVolume - c.sellVolume)} · {nf.format(c.trades)} Ticks</div>
                  </div> : <div className="text-[var(--fg-dim)]">noch kein Tick</div>}
                </>;
              })()}
            </div>
            <div className="rounded-md border border-[var(--line)] bg-[var(--panel-3)] p-2">
              <div className="text-[var(--fg-faint)]">Letzter Tick</div>
              {!isOverview && lastIdx >= 0 ? <div className="font-mono text-[var(--fg)]">#{nf.format(lastIdx + 1)} · {st.price[lastIdx]} · {st.vol[lastIdx]} Kontr. · <span style={{ color: st.side[lastIdx] > 0 ? "#3cf0a0" : "#f0566a" }}>{st.side[lastIdx] > 0 ? "Buy" : st.side[lastIdx] < 0 ? "Sell" : "?"}</span>
                <div className="text-[var(--fg-dim)]">{fmtTime(m, st.t[lastIdx], 7)}</div></div> : <div className="text-[var(--fg-dim)]">–</div>}
            </div>
          </div>

          <div className="grid gap-2 xl:grid-cols-[minmax(0,1fr)_280px]">
            <TickReplayChart slots={slots} currentSlot={currentSlot} lastPrice={lastPrice} follow={follow && !isOverview} markers={markers} delta={delta} height={520}
              onSlotClick={(i) => setFpSlot(i)} selectedSlot={fpIndex} />
            <FootprintLadder levels={fp} title={fpIndex != null && slots[fpIndex] ? slots[fpIndex].range : ""} partial={!isOverview && fpIndex === currentSlot && !atEnd} />
          </div>
          <p className="text-[10px] leading-relaxed text-[var(--fg-faint)]">
            Kerzen an echten 5-Minuten-Grenzen ({TZ}), Intervalle [Start, Ende). Volumen, Delta (Buy − Sell) und CVD (ab Sessionstart) aus denselben Ticks.
            Footprint: Bid = Verkäufer-Aggressor, Ask = Käufer-Aggressor; Klick auf eine Kerze zeigt ihren Footprint. DOM, BBO und Smart Tape werden nicht angeboten –
            diese Streams sind in den Collections nicht angebunden (nur Trades mit Aggressor). {m.timePrecision}
          </p>
        </section>
      )}

      {ready && m && <OverlayPanel overlay={overlay} runKey={runKey} onRunKey={onRunKey} events={events} reached={reachedEv} meta={m} overview={isOverview} />}
    </div>
  );
}

function FootprintLadder({ levels, title, partial }: { levels: ReturnType<typeof footprint>; title: string; partial: boolean }) {
  const max = levels.reduce((a, l) => Math.max(a, l.bid, l.ask), 1);
  const bid = levels.reduce((a, l) => a + l.bid, 0), ask = levels.reduce((a, l) => a + l.ask, 0);
  return (
    <div className="rounded-md border border-[var(--line)] bg-[var(--panel-3)] p-2 text-[11px]" data-testid="dr-footprint">
      <div className="mb-1 flex items-baseline gap-2"><b className="text-[var(--fg)]">Footprint</b><span className="text-[var(--fg-faint)]">{title}{partial ? " · laufend" : ""}</span></div>
      {!levels.length ? <div className="text-[var(--fg-dim)]">keine Ticks</div> : (
        <div className="max-h-[470px] overflow-y-auto">
          <table className="w-full font-mono">
            <thead><tr className="text-[var(--fg-faint)]"><th className="text-right font-normal">Bid</th><th className="font-normal">Preis</th><th className="text-left font-normal">Ask</th><th className="text-right font-normal">Δ</th></tr></thead>
            <tbody>
              {levels.map((l) => (
                <tr key={l.price}>
                  <td className="relative text-right"><span className="absolute inset-y-0 right-0 bg-[rgba(240,86,106,0.30)]" style={{ width: `${(100 * l.bid) / max}%` }} /><span className="relative">{l.bid || ""}</span></td>
                  <td className="px-1 text-center text-[var(--fg)]">{l.price}</td>
                  <td className="relative text-left"><span className="absolute inset-y-0 left-0 bg-[rgba(60,240,160,0.28)]" style={{ width: `${(100 * l.ask) / max}%` }} /><span className="relative">{l.ask || ""}</span></td>
                  <td className={cn("text-right", l.ask - l.bid >= 0 ? "text-[var(--key)]" : "text-[var(--red)]")}>{l.ask - l.bid}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
      {levels.length > 0 && <div className="mt-1 text-[var(--fg-dim)]">Σ Bid {nf.format(bid)} · Σ Ask {nf.format(ask)} · Δ {nf.format(ask - bid)}</div>}
    </div>
  );
}

// ---------------------------------------------------------------------------------------------
// Lauf-Überlagerung: gespeicherte Fills eines Berichts oder einer Orderflow-Lauf-Datei dieses Tages.
// ---------------------------------------------------------------------------------------------

interface Overlay { options: RunOption[]; trades: OverlayTrade[] | null; note: string | null; error: string | null; loading: boolean; label: string | null; }

function useRunOverlay(runKey: string | null, day: string | null, m: TickReplayMeta | null): Overlay {
  const [reports, setReports] = useState<ReportSummary[]>([]);
  const [files, setFiles] = useState<WsRunIndexEntry[]>([]);
  const [state, setState] = useState<{ trades: OverlayTrade[] | null; note: string | null; error: string | null; loading: boolean; label: string | null }>({ trades: null, note: null, error: null, loading: false, label: null });
  useEffect(() => {
    backtestingApi.reports().then(setReports).catch(() => setReports([]));
    fetch("/orderflow-runs/index.json").then((r) => (r.ok ? r.json() : [])).then((l) => setFiles(Array.isArray(l) ? l : [])).catch(() => setFiles([]));
  }, []);
  const options: RunOption[] = useMemo(() => {
    if (!day) return [];
    const d0 = Date.parse(day + "T00:00:00Z"), d1 = d0 + 86_400_000;
    const rep = reports.filter((r) => r.status === "Completed" && r.dataFrom && r.dataTo && Date.parse(r.dataFrom) < d1 && Date.parse(r.dataTo) >= d0)
      .map((r) => ({ key: `report:${r.runId}`, kind: "report" as const, id: r.runId, label: `Bericht ${r.strategyName} · ${r.symbol} · ${r.runId}` }));
    const fl = files.filter((f) => (f.loadable !== false) && String((f as any).dataSource ?? "").includes(day))
      .map((f) => ({ key: `file:${f.id}`, kind: "file" as const, id: f.id, label: `Orderflow-Lauf-Datei ${f.id}` }));
    return [...rep, ...fl];
  }, [reports, files, day]);

  useEffect(() => {
    setState({ trades: null, note: null, error: null, loading: false, label: null });
    if (!runKey || !day || !m) return;
    const [kind, ...rest] = runKey.split(":");
    const rid = rest.join(":");
    const ac = new AbortController();
    setState((s) => ({ ...s, loading: true }));
    (async () => {
      try {
        if (kind === "report") {
          const res = await backtestingApi.report(rid, ac.signal);
          const rep = res.run?.report;
          if (!res.ok || !rep?.trades) throw new Error(res.error ?? "Bericht ohne gespeicherte Trades.");
          const sym = rep.provenance.symbol;
          if (sym !== "MNQ") { setState({ trades: [], note: null, error: `Lauf-Instrument ${sym} ≠ MNQ (Collection) – keine Überlagerung, nichts wird umgedeutet.`, loading: false, label: rid }); return; }
          const qty = rep.provenance.quantity;
          const trades = rep.trades.trades.filter((t) => t.session === day).map((t): OverlayTrade => ({
            index: t.index, side: t.side, qty, entryIso: t.entryTimeUtc ?? null, entryMs: t.entryT, entryPrice: t.entryPrice,
            exitIso: t.closedAtEnd ? null : t.exitTimeUtc ?? null, exitMs: t.closedAtEnd ? null : t.exitT, exitPrice: t.closedAtEnd ? null : t.exitPrice,
            exitReason: t.closedAtEnd ? "offen (bewertet)" : t.exitReason, signalIso: t.entrySignalUtc ?? null,
          }));
          const basis = rep.provenance.dataSource === `ATAS-Collection MNQ New York ${day}` ? "gleicher Collection-Tag" : `Lauf rechnete auf „${rep.provenance.dataSource}“`;
          setState({ trades, note: `${trades.length} gespeicherte Trades dieses Tages · ${basis}; Replay-Quelle ${m.collection}. Marker nach gespeicherten Fill-Zeitstempeln.`, error: null, loading: false, label: rid });
        } else {
          const r = await fetch(`/orderflow-runs/${rid}.json`, { signal: ac.signal });
          if (!r.ok) throw new Error(`HTTP ${r.status}`);
          const d = (await r.json()) as WorkspaceData;
          if (d.meta.instrument !== "MNQ") throw new Error(`Instrument ${d.meta.instrument} ≠ MNQ`);
          const s = d.strategies.find((x) => x.usesOrderflow) ?? d.strategies[0];
          const trades = (s?.trades ?? []).map((t): OverlayTrade => ({
            index: t.index, side: t.side, qty: t.qty, entryIso: t.entryTimeUtc ?? null, entryMs: t.entryTimeMs, entryPrice: t.entryPrice,
            exitIso: t.exitTimeUtc ?? null, exitMs: t.exitTimeMs, exitPrice: t.exitPrice, exitReason: t.exitReason, signalIso: t.entrySignal?.timeUtc ?? null,
          }));
          const open = s?.openPositionSide && s.openPositionSide !== "Flat" ? ` · am Ende offen: ${s.openPositionSide} ${s.openPositionQty ?? ""} @ ${s.openPositionAvgPrice ?? "?"} (kein Ausstieg)` : "";
          setState({ trades, note: `Strategie „${s?.name}“ · ${trades.length} Trades${open} · Lauf auf „${d.meta.dataSource}“ (Datenbasis kann eine andere Aufnahme sein als ${m.collection}) – Marker nach gespeicherten Fill-Zeitstempeln.`, error: null, loading: false, label: rid });
        }
      } catch (e: any) {
        if (!ac.signal.aborted) setState({ trades: null, note: null, error: String(e?.message ?? e), loading: false, label: rid });
      }
    })();
    return () => ac.abort();
  }, [runKey, day, m?.anchorUnixMs, m?.collection]); // eslint-disable-line react-hooks/exhaustive-deps

  return { options, ...state };
}

function OverlayPanel({ overlay, runKey, onRunKey, events, reached, meta, overview }: {
  overlay: Overlay; runKey: string | null; onRunKey: (k: string | null) => void; events: ReturnType<typeof overlayEvents>; reached: boolean[];
  meta: TickReplayMeta; overview: boolean;
}) {
  const t = (u: number) => fmtTime(meta, u, 3);
  return (
    <section className="rounded-lg border border-[var(--line)] bg-[var(--panel)] p-3 space-y-2 text-[11px]" data-testid="dr-overlay">
      <div className="flex flex-wrap items-center gap-2">
        <b className="text-[12px] text-[var(--fg)]">Strategielauf (optional)</b>
        <select value={runKey ?? ""} onChange={(e) => onRunKey(e.target.value || null)} className="h-[28px] max-w-[520px] rounded-md border border-[var(--line-2)] bg-[var(--bg-2)] px-2 text-xs text-[var(--fg)]" data-testid="dr-run">
          <option value="">– nur Markt (kein Lauf) –</option>
          {overlay.options.map((o) => <option key={o.key} value={o.key}>{o.label}</option>)}
        </select>
        <span className="text-[var(--fg-faint)]">{overlay.options.length ? `${overlay.options.length} gespeicherte Läufe decken diesen Tag ab` : "Kein gespeicherter Lauf deckt diesen Tag ab."} Play rechnet keine Strategie neu.</span>
      </div>
      {overlay.loading && <p className="text-[var(--fg-dim)]">Lade gespeicherte Fills …</p>}
      {overlay.error && <p className="text-[var(--red)]">{overlay.error}</p>}
      {overlay.note && <p className="text-[var(--fg-dim)]">{overlay.note}</p>}
      {overlay.trades && overlay.trades.length > 0 && (
        <div className="max-h-[220px] overflow-y-auto">
          <table className="w-full font-mono">
            <thead><tr className="text-left text-[var(--fg-faint)]">{["#", "Ereignis", "Zeit (ET)", "Preis", "Stand"].map((h) => <th key={h} className="pr-2 font-normal">{h}</th>)}</tr></thead>
            <tbody>{events.map((e, i) => (
              <tr key={i} className={reached[i] ? "text-[var(--fg)]" : "text-[var(--fg-faint)]"}>
                <td className="pr-2">{e.trade + 1}</td>
                <td className="pr-2">{e.kind === "entry" ? `Einstieg ${e.side}` : e.kind === "exit" ? "Ausstieg" : "Signal"}</td>
                <td className="pr-2">{reached[i] || overview ? `${t(e.units)}${e.precision === "ms" ? " (ms)" : ""}` : "—"}</td>
                <td className="pr-2">{reached[i] ? (e.price ?? "–") : "—"}</td>
                <td>{reached[i] ? "erreicht" : "noch nicht erreicht"}</td>
              </tr>
            ))}</tbody>
          </table>
        </div>
      )}
    </section>
  );
}
