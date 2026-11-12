import { useCallback, useEffect, useMemo, useReducer, useRef, useState } from "react";
import { TickReplayChart, type ReplaySlot } from "@/components/research/TickReplayChart";
import {
  TickReplayEngine, createStore, appendChunk, decodeChunk, countAtOrBefore, candleStatus, advanceClock,
  type TickStore, type ReplayCandle,
} from "@/lib/tickReplay";
import { tickReplayApi, type TickReplayMeta, type TickReplaySession } from "@/lib/tickReplayApi";

// Historisches TICK-Replay mit sich aufbauenden 5-Minuten-Kerzen aus den ECHTEN aufgezeichneten Trades einer
// Collection-Session. Jeder Trade wird einzeln angewandt (Darstellung je Frame gebuendelt). Nur Informationen bis
// zur Replay-Position werden gezeigt. Datenquelle: /api/tickreplay (alle Trades der Session, portionsweise) –
// NICHT das auf 5.000 Ticks gekappte Dashboard-Tape und NICHT der OHLC/Quant-Pfad. Strategielaeufe (500 Ticks/Bar)
// bleiben unveraendert und werden hier nicht als Replay-Zustand dargestellt.

const CHUNK = 200_000;
const SPEEDS = [1, 10, 100];
const TZ = "America/New_York";
const UNITS_PER_SEC = 10_000_000;
const nyFmt = new Intl.DateTimeFormat("de-DE", { timeZone: TZ, hour: "2-digit", minute: "2-digit", second: "2-digit", hourCycle: "h23" });
const nyHm = new Intl.DateTimeFormat("de-DE", { timeZone: TZ, hour: "2-digit", minute: "2-digit", hourCycle: "h23" });
const utcFmt = new Intl.DateTimeFormat("de-DE", { timeZone: "UTC", hour: "2-digit", minute: "2-digit", second: "2-digit", hourCycle: "h23" });
// Formatter EINMAL anlegen (Intl.DateTimeFormat zu erzeugen ist teuer – nie pro Frame).
const tzFmt = new Intl.DateTimeFormat("en-US", { timeZone: TZ, timeZoneName: "short" });
const tzShort = (ms: number) => tzFmt.formatToParts(ms).find((p) => p.type === "timeZoneName")?.value ?? "";
const nf = new Intl.NumberFormat("de-DE");

/** Exakte Zeitdarstellung: Anker liegt auf einer ganzen Sekunde, Sekundenbruchteil aus dem 100-ns-Offset. */
function fmtTime(m: TickReplayMeta, off: number, fmt: Intl.DateTimeFormat, digits = 3): string {
  const whole = Math.floor(off / UNITS_PER_SEC);
  const frac = String(Math.floor(off - whole * UNITS_PER_SEC)).padStart(7, "0");
  const ms = m.anchorUnixMs + whole * 1000;
  return digits > 0 ? `${fmt.format(ms)}.${frac.slice(0, digits)}` : fmt.format(ms);
}
const offMs = (m: TickReplayMeta, off: number) => m.anchorUnixMs + off / 10_000;
const secOfDay = (s: string) => { const [h, mi, se] = s.split(":").map(Number); return h * 3600 + mi * 60 + (se || 0); };

interface LoadState { phase: "idle" | "prepare" | "load" | "done" | "error"; loaded: number; total: number; msg?: string; }

export function TickReplayPanel() {
  const [open, setOpen] = useState(true);
  const [sessions, setSessions] = useState<TickReplaySession[]>([]);
  const [listErr, setListErr] = useState<string | null>(null);
  const [jobId, setJobId] = useState("");
  const [meta, setMeta] = useState<TickReplayMeta | null>(null);
  const [load, setLoad] = useState<LoadState>({ phase: "idle", loaded: 0, total: 0 });
  const [playing, setPlaying] = useState(false);
  const [speed, setSpeed] = useState(10);
  const [follow, setFollow] = useState(true);
  const [gotoText, setGotoText] = useState("");
  const [, bump] = useReducer((x: number) => x + 1, 0);

  const storeRef = useRef<TickStore | null>(null);
  const engRef = useRef<TickReplayEngine | null>(null);
  const metaRef = useRef<TickReplayMeta | null>(null);
  const clockRef = useRef(0);
  const genRef = useRef(0);
  const pendingSeekRef = useRef<number | null>(null);
  const waitingRef = useRef(false);
  const playingRef = useRef(false);
  const speedRef = useRef(speed);
  const rafRef = useRef<number | null>(null);
  const lastFrameRef = useRef<number | null>(null);
  const renderQueued = useRef(false);
  speedRef.current = speed;

  /** Hoechstens eine Darstellung je Animationsframe (gebuendelt); die Engine verarbeitet trotzdem jeden Trade. */
  const requestRender = useCallback(() => {
    if (renderQueued.current) return;
    renderQueued.current = true;
    requestAnimationFrame(() => { renderQueued.current = false; bump(); });
  }, []);

  const stopLoop = useCallback(() => {
    playingRef.current = false;
    if (rafRef.current != null) cancelAnimationFrame(rafRef.current);
    rafRef.current = null;
    lastFrameRef.current = null;
  }, []);

  /** Vorgemerkter Sprung wird erst angewandt, wenn ALLE Trades bis zur Zielzeit geladen sind (kein Ueberspringen). */
  const applyPendingSeek = useCallback(() => {
    const T = pendingSeekRef.current, st = storeRef.current, eng = engRef.current;
    if (T == null || !st || !eng) return;
    if (st.loaded >= st.total || (st.loaded > 0 && st.t[st.loaded - 1] > T)) {
      pendingSeekRef.current = null;
      eng.seek(countAtOrBefore(st, T));
      clockRef.current = T;
    }
  }, []);

  // Sessions der Collection (alle fuenf Tage).
  useEffect(() => {
    const ac = new AbortController();
    tickReplayApi.sessions(ac.signal).then((l) => {
      setSessions(l.sessions);
      if (!l.available) setListErr(l.reason ?? "Keine Sessions verfuegbar.");
      if (l.sessions.length) setJobId((j) => j || l.sessions[0].jobId);
    }).catch((e) => { if (!ac.signal.aborted) setListErr(`Tick-Replay-API nicht erreichbar (${e?.message ?? e}). DevDashboard-API auf :5034 starten.`); });
    return () => ac.abort();
  }, []);

  // Session laden: vorbereiten, dann luecklos portionsweise nachladen. Veraltete Antworten (Datumswechsel) verwerfen.
  useEffect(() => {
    if (!jobId) return;
    const gen = ++genRef.current;
    const ac = new AbortController();
    stopLoop();
    setPlaying(false);
    storeRef.current = null; engRef.current = null; metaRef.current = null;
    pendingSeekRef.current = null; waitingRef.current = false;
    setMeta(null);
    setLoad({ phase: "prepare", loaded: 0, total: 0 });
    (async () => {
      try {
        const m = await tickReplayApi.meta(jobId, ac.signal);
        if (gen !== genRef.current) return;
        const st = createStore(m.count);
        const eng = new TickReplayEngine(st, m.bucketUnits);
        storeRef.current = st; engRef.current = eng; metaRef.current = m;
        clockRef.current = m.windowStartUnits;
        setMeta(m);
        setLoad({ phase: "load", loaded: 0, total: m.count });
        while (st.loaded < st.total) {
          const buf = await tickReplayApi.ticks(jobId, st.loaded, CHUNK, ac.signal);
          if (gen !== genRef.current) return;
          const chunk = decodeChunk(buf);
          if (chunk.n === 0) throw new Error(`Server lieferte keine Trades ab ${st.loaded} (erwartet ${st.total}).`);
          appendChunk(st, st.loaded, chunk); // fail-closed: Luecke/Ueberlappung/Zeit rueckwaerts -> Fehler
          applyPendingSeek();
          setLoad({ phase: st.loaded >= st.total ? "done" : "load", loaded: st.loaded, total: st.total });
          requestRender();
        }
      } catch (e: any) {
        if (gen !== genRef.current || ac.signal.aborted) return;
        stopLoop();
        setPlaying(false);
        setLoad((l) => ({ ...l, phase: "error", msg: String(e?.message ?? e) }));
      }
    })();
    return () => ac.abort();
  }, [jobId, stopLoop, applyPendingSeek, requestRender]);

  useEffect(() => () => stopLoop(), [stopLoop]);

  /** Abspielschleife: Marktuhr laeuft mit 1x/10x/100x; jeder Trade mit Zeit <= Uhr wird angewandt. */
  const frame = useCallback((now: number) => {
    rafRef.current = null;
    if (!playingRef.current) return;
    const eng = engRef.current, st = storeRef.current, m = metaRef.current;
    if (!eng || !st || !m) return;
    const prev = lastFrameRef.current ?? now;
    lastFrameRef.current = now;
    // Uhr ganzzahlig in 100-ns-Einheiten halten (exakte Vergleiche mit Trade-Zeiten und Intervallgrenzen).
    // Begrenzung je Frame (1 s Wandzeit) verhindert nur riesige Spruenge nach Hintergrund-Pausen; es werden dabei
    // nie Trades uebersprungen, die Uhr laeuft lediglich nicht weiter als 1 s x Geschwindigkeit pro Frame.
    let clock = Math.floor(advanceClock(clockRef.current, Math.min(now - prev, 1000), speedRef.current));
    const complete = st.loaded >= st.total;
    if (!complete) {
      // Nie ueber das Geladene hinaus: Uhr am letzten geladenen Trade halten, bis nachgeladen ist.
      const lastLoaded = st.loaded > 0 ? st.t[st.loaded - 1] : clockRef.current;
      waitingRef.current = clock > lastLoaded;
      if (waitingRef.current) clock = Math.max(clockRef.current, lastLoaded);
    } else waitingRef.current = false;
    eng.advanceTo(countAtOrBefore(st, clock));
    if (complete && clock >= m.windowEndUnits) {
      clock = m.windowEndUnits;
      playingRef.current = false;
      setPlaying(false);
    }
    clockRef.current = clock;
    bump();
    if (playingRef.current) rafRef.current = requestAnimationFrame(frame);
  }, []);

  const play = () => {
    const m = metaRef.current;
    if (!m || !engRef.current || clockRef.current >= m.windowEndUnits) return;
    playingRef.current = true;
    setPlaying(true);
    lastFrameRef.current = null;
    if (rafRef.current == null) rafRef.current = requestAnimationFrame(frame);
  };
  const pause = () => { stopLoop(); setPlaying(false); requestRender(); };
  const reset = () => {
    stopLoop(); setPlaying(false);
    pendingSeekRef.current = null;
    engRef.current?.reset();
    if (metaRef.current) clockRef.current = metaRef.current.windowStartUnits;
    requestRender();
  };
  const stepTrade = () => {
    stopLoop(); setPlaying(false);
    const eng = engRef.current, st = storeRef.current;
    if (!eng || !st) return;
    pendingSeekRef.current = null;
    if (eng.step()) clockRef.current = Math.max(clockRef.current, st.t[eng.p - 1]);
    else waitingRef.current = st.loaded < st.total;
    requestRender();
  };
  const seekTime = (T: number) => {
    const m = metaRef.current, st = storeRef.current, eng = engRef.current;
    if (!m || !st || !eng) return;
    const target = Math.max(m.windowStartUnits, Math.min(m.windowEndUnits, Math.round(T)));
    const complete = st.loaded >= st.total;
    const lastLoaded = st.loaded > 0 ? st.t[st.loaded - 1] : -Infinity;
    if (!complete && target >= lastLoaded) { pendingSeekRef.current = target; requestRender(); return; }
    pendingSeekRef.current = null;
    eng.seek(countAtOrBefore(st, target));
    clockRef.current = target;
    lastFrameRef.current = null;
    requestRender();
  };
  const gotoNy = () => {
    const m = metaRef.current;
    if (!m || !/^\d{1,2}:\d{2}(:\d{2})?$/.test(gotoText.trim())) return;
    // Innerhalb der Session (09:30–12:00) gibt es keinen Zeitumstellungswechsel -> Differenz der Ortszeit = Marktzeit.
    const startSec = secOfDay(nyFmt.format(offMs(m, m.windowStartUnits)));
    seekTime(m.windowStartUnits + (secOfDay(gotoText.trim()) - startSec) * UNITS_PER_SEC);
  };

  // Intervall-Beschriftungen haengen nur von der Session ab -> einmal je Session berechnen, nicht je Frame.
  const slotTemplates = useMemo(() => {
    if (!meta) return [] as { bucket: number; label: string; range: string }[];
    const b0 = Math.floor(meta.windowStartUnits / meta.bucketUnits);
    const b1 = Math.ceil(meta.windowEndUnits / meta.bucketUnits) - 1;
    const out: { bucket: number; label: string; range: string }[] = [];
    for (let b = b0; b <= b1; b++) {
      const s = offMs(meta, b * meta.bucketUnits), e = offMs(meta, (b + 1) * meta.bucketUnits);
      out.push({ bucket: b, label: nyHm.format(s), range: `${nyHm.format(s)}–${nyHm.format(e)} ${tzShort(s)}` });
    }
    return out;
  }, [meta]);

  // ----------------------------- abgeleitete Darstellung (nur Stand bis zur Replay-Position) -----------------------------
  const m = meta;
  const st = storeRef.current;
  const eng = engRef.current;
  const clock = clockRef.current;
  let slots: ReplaySlot[] = [];
  let currentSlot: number | null = null;
  let gaps = 0;
  let curStatus: ReturnType<typeof candleStatus> | null = null;
  if (m && st && eng && slotTemplates.length) {
    const byBucket = new Map<number, ReplayCandle>();
    for (const c of eng.candles()) byBucket.set(c.bucket, c);
    const exhausted = st.loaded >= st.total && eng.p >= st.total;
    const clockBucket = Math.floor(clock / m.bucketUnits);
    slots = slotTemplates.map((tpl) => {
      const c = byBucket.get(tpl.bucket) ?? null;
      if (!c && tpl.bucket < clockBucket) gaps++;
      return { label: tpl.label, range: tpl.range, candle: c, status: c ? candleStatus(c, m.bucketUnits, clock, m.windowStartUnits, m.windowEndUnits, exhausted) : null };
    });
    if (eng.cur) { currentSlot = eng.cur.bucket - slotTemplates[0].bucket; curStatus = slots[currentSlot]?.status ?? null; }
  }
  const lastIdx = eng && eng.p > 0 ? eng.p - 1 : -1;
  const lastPrice = st && lastIdx >= 0 ? st.price[lastIdx] : null;
  const cur = eng?.cur ?? null;
  const pending = pendingSeekRef.current;
  const atEnd = !!(m && clock >= m.windowEndUnits);
  const ready = !!(m && st && eng);
  const replayState = ready ? JSON.stringify({
    p: eng!.p, total: st!.total, loaded: st!.loaded, clock, closed: eng!.closed.length,
    cur: cur && { bucket: cur.bucket, open: cur.open, high: cur.high, low: cur.low, close: cur.close, volume: cur.volume, trades: cur.trades },
    // Alle sichtbaren Kerzen kompakt (Pruef-/Abnahmeschnittstelle, nur Stand bis zur Position): [b, O, H, L, C, V, Trades].
    cs: eng!.candles().map((c) => [c.bucket, c.open, c.high, c.low, c.close, c.volume, c.trades]),
  }) : "";

  const btn = "h-[28px] px-2.5 rounded-md border border-[var(--line-2)] text-[var(--fg)] text-xs disabled:opacity-40";
  const btnOn = "h-[28px] px-2.5 rounded-md bg-[var(--key)] text-black text-xs font-semibold";

  return (
    <div className="rounded-lg border border-[var(--line)] bg-[var(--panel)]" data-testid="tick-replay" data-replay-state={replayState}>
      <button onClick={() => setOpen((o) => !o)} className="flex w-full items-center gap-2 px-3 py-2 text-left">
        <span className="text-[var(--key)] text-xs">{open ? "▾" : "▸"}</span>
        <span className="rounded bg-[var(--panel-3)] px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-[var(--fg-dim)]">Tick-Replay · 5-Minuten-Kerzen</span>
        <span className="text-[11px] text-[var(--fg-faint)]">historisches Replay der echten aufgezeichneten Trades – die laufende Kerze baut sich Trade für Trade auf</span>
      </button>
      {open && (
        <div className="space-y-3 border-t border-[var(--line)] p-3">
          {listErr && <div className="text-xs text-[var(--red)]">{listErr}</div>}

          <div className="flex flex-wrap items-center gap-2">
            <label className="text-[11px] text-[var(--fg-dim)]">Session</label>
            <select value={jobId} onChange={(e) => setJobId(e.target.value)} data-testid="tick-replay-session"
              className="h-[28px] rounded-md border border-[var(--line-2)] bg-[var(--panel-3)] px-2 text-xs text-[var(--fg)]">
              {sessions.map((s) => <option key={s.jobId} value={s.jobId}>{s.label} · {nf.format(s.rows)} Trades</option>)}
            </select>
            {m && <span className="text-[11px] text-[var(--fg-faint)]">
              Fenster [{fmtTime(m, m.windowStartUnits, nyHm, 0)}, {fmtTime(m, m.windowEndUnits, nyHm, 0)}) {TZ} · {nf.format(m.count)} Trades · Vol {nf.format(m.totalVolume)} (Buy {nf.format(m.buyVolume)} / Sell {nf.format(m.sellVolume)}) · Quelle trades.csv SHA256 {m.csvSha256.slice(0, 12)}… · Cache {m.cache}
            </span>}
          </div>

          {(load.phase === "prepare" || load.phase === "load") && (
            <div className="space-y-1" data-testid="tick-replay-loading">
              <div className="text-[11px] text-[var(--fg-dim)]">
                {load.phase === "prepare" ? "Server bereitet die Session vor (beim ersten Mal einmaliger Import + Cache, bis ~1 Min) …"
                  : `Lade Trades portionsweise: ${nf.format(load.loaded)} / ${nf.format(load.total)}`}
              </div>
              <div className="h-1.5 w-full overflow-hidden rounded bg-[var(--panel-3)]">
                <div className="h-full bg-[var(--key)]" style={{ width: load.total ? `${(100 * load.loaded) / load.total}%` : "8%" }} />
              </div>
            </div>
          )}
          {load.phase === "error" && <div className="text-xs text-[var(--red)]" data-testid="tick-replay-error">Laden fehlgeschlagen (fail-closed): {load.msg}</div>}

          {ready && m && st && eng && (
            <>
              <div className="flex flex-wrap items-center gap-1.5">
                {playing
                  ? <button className={btnOn} onClick={pause} data-testid="tr-pause">❚❚ Pause</button>
                  : <button className={btnOn} onClick={play} disabled={atEnd} data-testid="tr-play">▶ Play</button>}
                <button className={btn} onClick={reset} data-testid="tr-reset">⟲ Reset</button>
                <button className={btn} onClick={stepTrade} disabled={eng.p >= st.total} data-testid="tr-step" title="Genau einen Trade anwenden">nächster Trade ›</button>
                <span className="mx-1 h-4 w-px bg-[var(--line-2)]" />
                {SPEEDS.map((s) => (
                  <button key={s} className={speed === s ? btnOn : btn} onClick={() => setSpeed(s)} data-testid={`tr-speed-${s}`}>{s}×</button>
                ))}
                <span className="mx-1 h-4 w-px bg-[var(--line-2)]" />
                {[-300, -60, 60, 300].map((d) => (
                  <button key={d} className={btn} onClick={() => seekTime(clock + d * UNITS_PER_SEC)} data-testid={`tr-jump-${d}`}>
                    {d < 0 ? "−" : "+"}{Math.abs(d) / 60} min
                  </button>
                ))}
                <input value={gotoText} onChange={(e) => setGotoText(e.target.value)} onKeyDown={(e) => e.key === "Enter" && gotoNy()}
                  placeholder="hh:mm:ss ET" data-testid="tr-goto"
                  className="h-[28px] w-[96px] rounded-md border border-[var(--line-2)] bg-[var(--panel-3)] px-2 text-xs text-[var(--fg)]" />
                <button className={btn} onClick={gotoNy}>Gehe zu</button>
                <label className="ml-2 flex items-center gap-1 text-[11px] text-[var(--fg-dim)]">
                  <input type="checkbox" checked={follow} onChange={(e) => setFollow(e.target.checked)} /> aktuellem Preis folgen
                </label>
              </div>

              <input type="range" className="w-full" min={m.windowStartUnits} max={m.windowEndUnits} step={1} value={clock}
                onChange={(e) => seekTime(Number(e.target.value))} data-testid="tr-slider" />

              <div className="grid gap-2 text-[11px] md:grid-cols-3">
                <div className="rounded-md border border-[var(--line)] bg-[var(--panel-3)] p-2" data-testid="tr-clock">
                  <div className="text-[var(--fg-faint)]">Marktzeit (Replay-Uhr)</div>
                  <div className="font-mono text-sm text-[var(--fg)]">{fmtTime(m, clock, nyFmt)} {tzShort(offMs(m, clock))} <span className="text-[var(--fg-faint)]">({TZ})</span></div>
                  <div className="font-mono text-[var(--fg-dim)]">{fmtTime(m, clock, utcFmt)} UTC · {m.day}</div>
                  <div className="mt-1 text-[var(--fg-dim)]">Position {nf.format(eng.p)} / {nf.format(st.total)} Trades{st.loaded < st.total ? ` · geladen ${nf.format(st.loaded)}` : ""}</div>
                  {playing && waitingRef.current && <div className="text-[var(--gold,#e8c069)]">wartet auf Nachladen – kein Trade wird übersprungen</div>}
                  {pending != null && <div className="text-[var(--gold,#e8c069)]">Sprung zu {fmtTime(m, pending, nyFmt, 0)} vorgemerkt – wird angewandt, sobald alle Trades bis dahin geladen sind</div>}
                  {atEnd && eng.p >= st.total && <div className="text-[var(--fg-dim)]">Sessionende erreicht (alle {nf.format(st.total)} Trades abgespielt)</div>}
                </div>
                <div className="rounded-md border border-[var(--line)] bg-[var(--panel-3)] p-2" data-testid="tr-candle">
                  <div className="text-[var(--fg-faint)]">Aktuelle Kerze {currentSlot != null && slots[currentSlot] ? slots[currentSlot].range : ""} {curStatus ? `· ${curStatus}` : ""}</div>
                  {cur ? (
                    <div className="font-mono text-[var(--fg)]">
                      <div>O {cur.open} · H {cur.high} · L {cur.low} · C <span style={{ color: cur.close >= cur.open ? "#3cf0a0" : "#f0566a" }}>{cur.close}</span></div>
                      <div className="text-[var(--fg-dim)]">Vol {nf.format(cur.volume)} (Buy {nf.format(cur.buyVolume)} / Sell {nf.format(cur.sellVolume)}) · {nf.format(cur.trades)} Trades</div>
                    </div>
                  ) : <div className="text-[var(--fg-dim)]">noch kein Trade abgespielt</div>}
                  <div className="mt-1 text-[var(--fg-dim)]">abgeschlossene Kerzen: {eng.closed.length} · Intervalle ohne Trades bisher: {gaps} (keine Kerze erfunden)</div>
                </div>
                <div className="rounded-md border border-[var(--line)] bg-[var(--panel-3)] p-2" data-testid="tr-last">
                  <div className="text-[var(--fg-faint)]">Letzter abgespielter Trade</div>
                  {lastIdx >= 0 ? (
                    <div className="font-mono text-[var(--fg)]">
                      <div>#{nf.format(lastIdx + 1)} · {st.price[lastIdx]} · {st.vol[lastIdx]} Kontr. · <span style={{ color: st.side[lastIdx] > 0 ? "#3cf0a0" : st.side[lastIdx] < 0 ? "#f0566a" : undefined }}>{st.side[lastIdx] > 0 ? "Buy" : st.side[lastIdx] < 0 ? "Sell" : "?"}</span></div>
                      <div className="text-[var(--fg-dim)]">{fmtTime(m, st.t[lastIdx], nyFmt, 7)} {tzShort(offMs(m, st.t[lastIdx]))}</div>
                    </div>
                  ) : <div className="text-[var(--fg-dim)]">–</div>}
                </div>
              </div>

              <TickReplayChart slots={slots} currentSlot={currentSlot} lastPrice={lastPrice} follow={follow} />

              <div className="text-[10px] leading-relaxed text-[var(--fg-faint)]">
                Kerzen an echten 5-Minuten-Grenzen in {TZ}, Intervalle [Start, Ende); Open = erster, Close = zuletzt abgespielter Trade, High/Low/Volumen nur aus bereits abgespielten Trades.
                Gleiche Zeitstempel in Originalreihenfolge, keine Deduplizierung, Lücken ohne erfundene Trades. {m.timePrecision}
                {" "}Bestehende Strategieläufe (500 Ticks/Bar) bleiben unverändert und werden hier nicht als Replay-Zustand eingeblendet.
              </div>
            </>
          )}
        </div>
      )}
    </div>
  );
}
