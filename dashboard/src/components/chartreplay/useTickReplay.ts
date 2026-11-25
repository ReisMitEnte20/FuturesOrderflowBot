// Lokale Tick-Wiedergabe EINES Handelstags (Marktuhr in 100-ns-Einheiten). Gemeinsame Uhr für Chart, Footprint,
// Volumen und Delta. Jeder Tick wird einzeln angewandt (TickReplayEngine); die Darstellung wird höchstens einmal je
// Animationsframe gebündelt – bei hoher Geschwindigkeit werden mehrere Ticks je Bild gezeichnet, aber keiner
// ausgelassen. Am Sessionende wird pausiert (kein Loop). Rücksprung stellt den damaligen Zustand exakt her.
// Daten: /api/tickreplay (nur der gewählte Tag, portionsweise) – nie die ganze Collection im Browser.
import { useCallback, useEffect, useReducer, useRef, useState } from "react";
import {
  TickReplayEngine, createStore, appendChunk, decodeChunk, countAtOrBefore, advanceClock, type TickStore,
} from "@/lib/tickReplay";
import { tickReplayApi, type TickReplayMeta } from "@/lib/tickReplayApi";

const CHUNK = 200_000;
export const UNITS_PER_SEC = 10_000_000;

export interface LoadState { phase: "idle" | "prepare" | "load" | "done" | "error"; loaded: number; total: number; msg?: string; }

export function useTickReplay(id: string | null) {
  const [meta, setMeta] = useState<TickReplayMeta | null>(null);
  const [load, setLoad] = useState<LoadState>({ phase: "idle", loaded: 0, total: 0 });
  const [playing, setPlaying] = useState(false);
  const [speed, setSpeed] = useState(10);
  const [, bump] = useReducer((x: number) => x + 1, 0);

  const storeRef = useRef<TickStore | null>(null);
  const engRef = useRef<TickReplayEngine | null>(null);
  const overviewRef = useRef<TickReplayEngine | null>(null);
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

  /** Vorgemerkter Sprung erst, wenn ALLE Ticks bis zur Zielzeit geladen sind (kein Überspringen). */
  const applyPendingSeek = useCallback(() => {
    const T = pendingSeekRef.current, st = storeRef.current, eng = engRef.current;
    if (T == null || !st || !eng) return;
    if (st.loaded >= st.total || (st.loaded > 0 && st.t[st.loaded - 1] > T)) {
      pendingSeekRef.current = null;
      eng.seek(countAtOrBefore(st, T));
      clockRef.current = T;
    }
  }, []);

  useEffect(() => {
    const gen = ++genRef.current;
    stopLoop();
    setPlaying(false);
    storeRef.current = null; engRef.current = null; overviewRef.current = null; metaRef.current = null;
    pendingSeekRef.current = null; waitingRef.current = false;
    setMeta(null);
    if (!id) { setLoad({ phase: "idle", loaded: 0, total: 0 }); return; }
    const ac = new AbortController();
    setLoad({ phase: "prepare", loaded: 0, total: 0 });
    (async () => {
      try {
        const m = await tickReplayApi.meta(id, ac.signal);
        if (gen !== genRef.current) return;
        const st = createStore(m.count);
        const eng = new TickReplayEngine(st, m.bucketUnits);
        storeRef.current = st; engRef.current = eng; metaRef.current = m;
        clockRef.current = m.windowStartUnits;
        setMeta(m);
        setLoad({ phase: "load", loaded: 0, total: m.count });
        while (st.loaded < st.total) {
          const buf = await tickReplayApi.ticks(id, st.loaded, CHUNK, ac.signal);
          if (gen !== genRef.current) return;
          const chunk = decodeChunk(buf);
          if (chunk.n === 0) throw new Error(`Server lieferte keine Ticks ab ${st.loaded} (erwartet ${st.total}).`);
          appendChunk(st, st.loaded, chunk); // fail-closed: Lücke/Überlappung/Zeit rückwärts -> Fehler
          applyPendingSeek();
          setLoad({ phase: st.loaded >= st.total ? "done" : "load", loaded: st.loaded, total: st.total });
          requestRender();
        }
        // Tagesübersicht: unabhängige Engine über ALLE Ticks (gleicher Codepfad wie das Replay).
        const ov = new TickReplayEngine(st, m.bucketUnits);
        ov.advanceTo(st.total);
        overviewRef.current = ov;
        requestRender();
      } catch (e: any) {
        if (gen !== genRef.current || ac.signal.aborted) return;
        stopLoop();
        setPlaying(false);
        setLoad((l) => ({ ...l, phase: "error", msg: String(e?.message ?? e) }));
      }
    })();
    return () => ac.abort();
  }, [id, stopLoop, applyPendingSeek, requestRender]);

  useEffect(() => () => stopLoop(), [stopLoop]);

  const frame = useCallback((now: number) => {
    rafRef.current = null;
    if (!playingRef.current) return;
    const eng = engRef.current, st = storeRef.current, m = metaRef.current;
    if (!eng || !st || !m) return;
    const prev = lastFrameRef.current ?? now;
    lastFrameRef.current = now;
    let clock = Math.floor(advanceClock(clockRef.current, Math.min(now - prev, 1000), speedRef.current));
    const complete = st.loaded >= st.total;
    if (!complete) {
      const lastLoaded = st.loaded > 0 ? st.t[st.loaded - 1] : clockRef.current;
      waitingRef.current = clock > lastLoaded;
      if (waitingRef.current) clock = Math.max(clockRef.current, lastLoaded);
    } else waitingRef.current = false;
    eng.advanceTo(countAtOrBefore(st, clock)); // jeder Tick einzeln – auch wenn viele in dieses Bild fallen
    if (complete && clock >= m.windowEndUnits) {
      clock = m.windowEndUnits;
      playingRef.current = false;
      setPlaying(false); // Sessionende: pausieren, nicht loopen
    }
    clockRef.current = clock;
    bump();
    if (playingRef.current) rafRef.current = requestAnimationFrame(frame);
  }, []);

  const play = useCallback(() => {
    const m = metaRef.current;
    if (!m || !engRef.current || clockRef.current >= m.windowEndUnits) return;
    playingRef.current = true;
    setPlaying(true);
    lastFrameRef.current = null;
    if (rafRef.current == null) rafRef.current = requestAnimationFrame(frame);
  }, [frame]);
  const pause = useCallback(() => { stopLoop(); setPlaying(false); requestRender(); }, [stopLoop, requestRender]);
  const reset = useCallback(() => {
    stopLoop(); setPlaying(false);
    pendingSeekRef.current = null;
    engRef.current?.reset();
    if (metaRef.current) clockRef.current = metaRef.current.windowStartUnits;
    requestRender();
  }, [stopLoop, requestRender]);
  /** Genau EINEN Tick weiter (Uhr auf dessen Zeit). */
  const stepTick = useCallback(() => {
    stopLoop(); setPlaying(false);
    const eng = engRef.current, st = storeRef.current;
    if (!eng || !st) return;
    pendingSeekRef.current = null;
    if (eng.step()) clockRef.current = Math.max(clockRef.current, st.t[eng.p - 1]);
    else waitingRef.current = st.loaded < st.total;
    requestRender();
  }, [stopLoop, requestRender]);
  const seekTime = useCallback((T: number) => {
    const m = metaRef.current, st = storeRef.current, eng = engRef.current;
    if (!m || !st || !eng) return;
    const target = Math.max(m.windowStartUnits, Math.min(m.windowEndUnits, Math.round(T)));
    const lastLoaded = st.loaded > 0 ? st.t[st.loaded - 1] : -Infinity;
    if (st.loaded < st.total && target >= lastLoaded) { pendingSeekRef.current = target; requestRender(); return; }
    pendingSeekRef.current = null;
    eng.seek(countAtOrBefore(st, target));
    clockRef.current = target;
    lastFrameRef.current = null;
    requestRender();
  }, [requestRender]);

  return {
    meta, load, playing, speed, setSpeed, play, pause, reset, stepTick, seekTime,
    store: storeRef.current, eng: engRef.current, overview: overviewRef.current,
    clock: clockRef.current, waiting: waitingRef.current, pending: pendingSeekRef.current,
  };
}
