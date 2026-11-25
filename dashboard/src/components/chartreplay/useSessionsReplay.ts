// Gemeinsames Tick-Replay mehrerer Sessions eines Handelstags („London + New York“): EIN Speicher, EINE Marktuhr.
// Jeder Tick wird einzeln angewandt (TickReplayEngine). Erreicht die Uhr das Ende einer Session, steht sie kurz an der
// Sessiongrenze (sichtbarer Übergang, noch keine Kerzen/Trades der nächsten Session) und springt dann OHNE die reale
// Pause abzuwarten auf den ersten echten Tick der nächsten Session. Am Ende der letzten Session: Pause (kein Loop).
// Daten: /api/tickreplay je Session portionsweise – keine CSV wird zusammenkopiert.
import { useCallback, useEffect, useReducer, useRef, useState } from "react";
import { TickReplayEngine, createStore, appendChunk, decodeChunk, countAtOrBefore, advanceClock, type TickStore } from "@/lib/tickReplay";
import { tickReplayApi, type TickReplayMeta } from "@/lib/tickReplayApi";
import { layoutSegments, pauseAt, segmentOfIndex, type SegLayout } from "@/lib/sessionReplay";

const CHUNK = 200_000;
/** Wie lange (Wandzeit) der Übergang beim Abspielen sichtbar an der Grenze steht, bevor die nächste Session beginnt. */
export const TRANSITION_HOLD_MS = 1200;

export interface MultiLoad { phase: "idle" | "prepare" | "load" | "done" | "error"; loaded: number; total: number; msg?: string; }
export interface Transition { from: number; to: number; lastTick: number; firstTick: number; atWall: number; }

export function useSessionsReplay(ids: string[] | null) {
  const key = ids?.join("|") ?? "";
  const [metas, setMetas] = useState<TickReplayMeta[]>([]);
  const [layout, setLayout] = useState<{ baseAnchorMs: number; bucketUnits: number; segs: SegLayout[] } | null>(null);
  const [load, setLoad] = useState<MultiLoad>({ phase: "idle", loaded: 0, total: 0 });
  const [playing, setPlaying] = useState(false);
  const [speed, setSpeed] = useState(10);
  const [, bump] = useReducer((x: number) => x + 1, 0);

  const storeRef = useRef<TickStore | null>(null);
  const engRef = useRef<TickReplayEngine | null>(null);
  const overviewRef = useRef<TickReplayEngine | null>(null);
  const layoutRef = useRef<typeof layout>(null);
  const clockRef = useRef(0);
  const genRef = useRef(0);
  const playingRef = useRef(false);
  const speedRef = useRef(speed);
  const rafRef = useRef<number | null>(null);
  const lastFrameRef = useRef<number | null>(null);
  const holdRef = useRef<Transition | null>(null);
  const lastTransitionRef = useRef<Transition | null>(null);
  const waitingRef = useRef(false);
  speedRef.current = speed;

  const stopLoop = useCallback(() => {
    playingRef.current = false;
    if (rafRef.current != null) cancelAnimationFrame(rafRef.current);
    rafRef.current = null;
    lastFrameRef.current = null;
  }, []);

  const firstTick = (k: number): number | null => {
    const st = storeRef.current, l = layoutRef.current;
    if (!st || !l) return null;
    const s = l.segs[k];
    return s.count > 0 && st.loaded > s.firstIdx ? st.t[s.firstIdx] : null;
  };
  const lastTick = (k: number): number | null => {
    const st = storeRef.current, l = layoutRef.current;
    if (!st || !l) return null;
    const s = l.segs[k];
    const li = s.firstIdx + s.count - 1;
    return s.count > 0 && st.loaded > li ? st.t[li] : null;
  };

  useEffect(() => {
    const gen = ++genRef.current;
    stopLoop(); setPlaying(false);
    storeRef.current = null; engRef.current = null; overviewRef.current = null; layoutRef.current = null;
    holdRef.current = null; lastTransitionRef.current = null; waitingRef.current = false;
    setMetas([]); setLayout(null);
    if (!ids || ids.length === 0) { setLoad({ phase: "idle", loaded: 0, total: 0 }); return; }
    const ac = new AbortController();
    setLoad({ phase: "prepare", loaded: 0, total: 0 });
    (async () => {
      try {
        const ms: TickReplayMeta[] = [];
        for (const id of ids) ms.push(await tickReplayApi.meta(id, ac.signal)); // je Session einmaliger Cache-Aufbau
        if (gen !== genRef.current) return;
        const l = layoutSegments(ms.map((m) => ({ id: m.id || m.jobId, label: m.label, session: m.session, anchorUnixMs: m.anchorUnixMs,
          windowStartUnits: m.windowStartUnits, windowEndUnits: m.windowEndUnits, count: m.count, bucketUnits: m.bucketUnits })));
        const total = ms.reduce((a, m) => a + m.count, 0);
        const st = createStore(total);
        const eng = new TickReplayEngine(st, l.bucketUnits);
        storeRef.current = st; engRef.current = eng; layoutRef.current = l;
        clockRef.current = l.segs[0].startUnits;
        setMetas(ms); setLayout(l);
        setLoad({ phase: "load", loaded: 0, total });
        for (let k = 0; k < ms.length; k++) {
          const seg = l.segs[k];
          let got = 0;
          while (got < seg.count) {
            const buf = await tickReplayApi.ticks(seg.id, got, CHUNK, ac.signal);
            if (gen !== genRef.current) return;
            const chunk = decodeChunk(buf);
            if (chunk.n === 0) throw new Error(`Server lieferte keine Ticks für ${seg.label} ab ${got}.`);
            for (let i = 0; i < chunk.n; i++) chunk.t[i] += seg.offsetUnits; // gemeinsame Zeitbasis (exakt, Vielfaches des Rasters)
            appendChunk(st, st.loaded, chunk); // fail-closed: Lücke/Überlappung/Zeit rückwärts → Fehler
            got += chunk.n;
            setLoad({ phase: st.loaded >= st.total ? "done" : "load", loaded: st.loaded, total });
            bump();
          }
        }
        const ov = new TickReplayEngine(st, l.bucketUnits);
        ov.advanceTo(st.total);
        overviewRef.current = ov;
        bump();
      } catch (e: any) {
        if (gen !== genRef.current || ac.signal.aborted) return;
        stopLoop(); setPlaying(false);
        setLoad((x) => ({ ...x, phase: "error", msg: String(e?.message ?? e) }));
      }
    })();
    return () => ac.abort();
  }, [key, stopLoop]); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => () => stopLoop(), [stopLoop]);

  /** Uhr setzen + alle Ticks bis dahin anwenden. */
  const applyClock = (clock: number) => {
    const st = storeRef.current, eng = engRef.current;
    if (!st || !eng) return;
    eng.seek(countAtOrBefore(st, clock));
    clockRef.current = clock;
  };

  const frame = useCallback((now: number) => {
    rafRef.current = null;
    if (!playingRef.current) return;
    const eng = engRef.current, st = storeRef.current, l = layoutRef.current;
    if (!eng || !st || !l) return;
    const prev = lastFrameRef.current ?? now;
    lastFrameRef.current = now;
    const hold = holdRef.current;
    if (hold) {
      // Übergang sichtbar an der Grenze; danach Sprung auf den ersten echten Tick der nächsten Session (keine reale Pause).
      if (now - hold.atWall >= TRANSITION_HOLD_MS) {
        const ft = firstTick(hold.to);
        if (ft == null) { waitingRef.current = true; }
        else { waitingRef.current = false; holdRef.current = null; eng.advanceTo(countAtOrBefore(st, ft)); clockRef.current = ft; }
      }
      bump();
      rafRef.current = requestAnimationFrame(frame);
      return;
    }
    let clock = Math.floor(advanceClock(clockRef.current, Math.min(now - prev, 1000), speedRef.current));
    const k = segmentOfIndex(l.segs, Math.max(0, eng.p - 1));
    const seg = l.segs[k];
    if (clock >= seg.endUnits) {
      clock = seg.endUnits;
      if (k + 1 < l.segs.length) {
        eng.advanceTo(Math.min(st.loaded, seg.firstIdx + seg.count)); // alle Ticks dieser Session, keiner der nächsten
        const tr: Transition = { from: k, to: k + 1, lastTick: lastTick(k) ?? seg.endUnits, firstTick: firstTick(k + 1) ?? l.segs[k + 1].startUnits, atWall: now };
        holdRef.current = tr; lastTransitionRef.current = tr;
        clockRef.current = clock;
        bump();
        rafRef.current = requestAnimationFrame(frame);
        return;
      }
    }
    const complete = st.loaded >= st.total;
    if (!complete) {
      const lastLoaded = st.loaded > 0 ? st.t[st.loaded - 1] : clockRef.current;
      waitingRef.current = clock > lastLoaded;
      if (waitingRef.current) clock = Math.max(clockRef.current, lastLoaded);
    } else waitingRef.current = false;
    eng.advanceTo(countAtOrBefore(st, clock));
    const last = l.segs[l.segs.length - 1];
    if (complete && clock >= last.endUnits) { clock = last.endUnits; playingRef.current = false; setPlaying(false); }
    clockRef.current = clock;
    bump();
    if (playingRef.current) rafRef.current = requestAnimationFrame(frame);
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  const atEnd = () => { const l = layoutRef.current; return !!l && clockRef.current >= l.segs[l.segs.length - 1].endUnits; };
  const play = useCallback(() => {
    if (!engRef.current || atEnd()) return;
    playingRef.current = true; setPlaying(true); lastFrameRef.current = null;
    if (holdRef.current) holdRef.current = { ...holdRef.current, atWall: performance.now() };
    if (rafRef.current == null) rafRef.current = requestAnimationFrame(frame);
  }, [frame]);
  const pause = useCallback(() => { stopLoop(); setPlaying(false); bump(); }, [stopLoop]);
  const reset = useCallback(() => {
    stopLoop(); setPlaying(false);
    holdRef.current = null; lastTransitionRef.current = null;
    engRef.current?.reset();
    if (layoutRef.current) clockRef.current = layoutRef.current.segs[0].startUnits;
    bump();
  }, [stopLoop]);
  /** Genau EINEN Tick weiter; überquert er eine Sessiongrenze, wird der Übergang angezeigt (ohne Warten). */
  const stepTick = useCallback(() => {
    stopLoop(); setPlaying(false);
    const eng = engRef.current, st = storeRef.current, l = layoutRef.current;
    if (!eng || !st || !l) return;
    holdRef.current = null;
    const before = segmentOfIndex(l.segs, Math.max(0, eng.p - 1));
    if (eng.step()) {
      const after = segmentOfIndex(l.segs, eng.p - 1);
      if (eng.p > 1 && after !== before) lastTransitionRef.current = { from: before, to: after, lastTick: lastTick(before) ?? 0, firstTick: st.t[eng.p - 1], atWall: performance.now() };
      clockRef.current = Math.max(clockRef.current, st.t[eng.p - 1]);
    }
    bump();
  }, [stopLoop]); // eslint-disable-line react-hooks/exhaustive-deps
  /** Sprung auf eine Marktzeit (gemeinsame Einheiten). In einer Pause steht die Uhr am Ende der vorigen Session. */
  const seekTime = useCallback((T: number) => {
    const l = layoutRef.current, st = storeRef.current;
    if (!l || !st) return;
    holdRef.current = null;
    let target = Math.max(l.segs[0].startUnits, Math.min(l.segs[l.segs.length - 1].endUnits, Math.round(T)));
    const p = pauseAt(l.segs, firstTick, target);
    if (p) target = l.segs[p.from].endUnits;
    if (st.loaded < st.total && (st.loaded === 0 || target > st.t[st.loaded - 1])) { waitingRef.current = true; bump(); return; }
    applyClock(target);
    lastFrameRef.current = null;
    bump();
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  const l = layoutRef.current;
  const clock = clockRef.current;
  const pauseNow = l ? pauseAt(l.segs, firstTick, clock) : null;
  return {
    metas, layout, load, playing, speed, setSpeed, play, pause, reset, stepTick, seekTime,
    store: storeRef.current, eng: engRef.current, overview: overviewRef.current, clock, waiting: waitingRef.current,
    holding: holdRef.current, lastTransition: lastTransitionRef.current, inPause: pauseNow, firstTick, lastTick,
    atEnd: !!l && clock >= l.segs[l.segs.length - 1].endUnits,
  };
}
