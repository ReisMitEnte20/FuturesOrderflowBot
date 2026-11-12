// Tests des historischen Tick-Replays (node --test, ohne zusaetzliche Abhaengigkeiten).
//
// Kernanforderungen: jede Kerze entsteht nur aus den bis zur Position angewandten echten Trades, Grenzen
// [Start, Ende) sind exakt, gleiche Zeitstempel bleiben in Originalreihenfolge, nichts wird dedupliziert oder
// erfunden, und 1x/100x/Seek/Ruecksprung liefern bei gleicher Position identische OHLCV-Werte.
import { test } from "node:test";
import assert from "node:assert/strict";
import {
  BUCKET_5M, ROW_BYTES, createStore, decodeChunk, appendChunk, TickReplayEngine, aggregateReference,
  countAtOrBefore, countBefore, candleStatus, advanceClock, UNITS_PER_MS,
} from "../src/lib/tickReplay.ts";

/** Baut einen Server-Abschnitt im Binaerformat [t f64][Preis f64][Vol i32][Seite i8]. */
function chunkBuffer(rows) {
  const n = rows.length;
  const buf = new ArrayBuffer(n * ROW_BYTES);
  const t = new Float64Array(buf, 0, n), p = new Float64Array(buf, 8 * n, n);
  const v = new Int32Array(buf, 16 * n, n), s = new Int8Array(buf, 20 * n, n);
  rows.forEach((r, i) => { t[i] = r[0]; p[i] = r[1]; v[i] = r[2]; s[i] = r[3]; });
  return buf;
}

function storeOf(rows) {
  const st = createStore(rows.length);
  if (rows.length) appendChunk(st, 0, decodeChunk(chunkBuffer(rows)));
  return st;
}

const strip = (cs) => cs.map(({ bucket, firstIdx, lastIdx, open, high, low, close, volume, buyVolume, sellVolume, trades }) =>
  ({ bucket, firstIdx, lastIdx, open, high, low, close, volume, buyVolume, sellVolume, trades }));

/** Deterministischer Pseudozufall (Tests reproduzierbar). */
function rng(seed) { let x = seed >>> 0; return () => ((x = (x * 1664525 + 1013904223) >>> 0) / 2 ** 32); }

/** Synthetische Session: mehrere Intervalle, gleiche Zeitstempel, identische Trades, Luecke, Trade auf Grenze. */
function syntheticRows(seed = 7, n = 4000) {
  const r = rng(seed);
  const rows = [];
  let t = 0, px = 20000;
  for (let i = 0; i < n; i++) {
    const u = r();
    if (u < 0.15) { /* gleicher Zeitstempel wie zuvor */ }
    else if (u < 0.17) t += BUCKET_5M * 2; // Luecke ueber ein ganzes Intervall
    else t += Math.floor(r() * 2_000_000);  // bis 0,2 s
    if (i === 1500) t = Math.ceil(t / BUCKET_5M) * BUCKET_5M; // Trade exakt auf einer Grenze
    px += (Math.floor(r() * 5) - 2) * 0.25;
    const prev = rows[rows.length - 1];
    // Gelegentlich Preis/Volumen/Seite des Vorgaengers wiederholen (bei gleichem t = identischer Trade).
    rows.push(prev && r() < 0.05 ? [t, prev[1], prev[2], prev[3]] : [t, px, 1 + Math.floor(r() * 4), r() < 0.5 ? 1 : -1]);
  }
  return rows;
}

test("Trade genau auf der Intervallgrenze beginnt die NEUE Kerze; 100 ns davor gehoert zur alten", () => {
  const st = storeOf([[0, 100, 1, 1], [BUCKET_5M - 1, 101, 1, 1], [BUCKET_5M, 102, 1, -1]]);
  const e = new TickReplayEngine(st);
  e.advanceTo(3);
  const cs = e.candles();
  assert.equal(cs.length, 2);
  assert.deepEqual([cs[0].bucket, cs[0].trades, cs[0].close], [0, 2, 101]);
  assert.deepEqual([cs[1].bucket, cs[1].open, cs[1].trades], [1, 102, 1]);
});

test("gleiche Zeitstempel behalten die Originalreihenfolge (Open = erster, Close = letzter)", () => {
  const st = storeOf([[5, 10, 1, 1], [5, 12, 1, 1], [5, 11, 1, -1]]);
  const e = new TickReplayEngine(st);
  e.advanceTo(3);
  const c = e.cur;
  assert.deepEqual([c.open, c.high, c.low, c.close], [10, 12, 10, 11]);
});

test("identische Trades werden NICHT dedupliziert", () => {
  const st = storeOf([[7, 20000.25, 2, 1], [7, 20000.25, 2, 1]]);
  const e = new TickReplayEngine(st);
  e.advanceTo(2);
  assert.equal(e.cur.trades, 2);
  assert.equal(e.cur.volume, 4);
  assert.equal(e.cur.buyVolume, 4);
});

test("keine Zukunftsdaten: Hoch/Tief/Volumen nur aus Trades vor der Position", () => {
  const st = storeOf([[0, 10, 1, 1], [1, 20, 5, 1], [2, 5, 3, -1]]);
  const e = new TickReplayEngine(st);
  e.advanceTo(1);
  assert.deepEqual([e.cur.high, e.cur.low, e.cur.close, e.cur.volume], [10, 10, 10, 1]);
  e.advanceTo(2);
  assert.deepEqual([e.cur.high, e.cur.low, e.cur.close, e.cur.volume], [20, 10, 20, 6]);
  e.seek(1);
  assert.deepEqual([e.cur.high, e.cur.low, e.cur.close, e.cur.volume], [10, 10, 10, 1]);
});

test("Zeitluecke: leere Intervalle erzeugen KEINE erfundene Kerze", () => {
  const st = storeOf([[0, 10, 1, 1], [3 * BUCKET_5M + 5, 11, 1, 1]]);
  const e = new TickReplayEngine(st);
  e.advanceTo(2);
  assert.deepEqual(e.candles().map((c) => c.bucket), [0, 3]);
});

test("Engine = unabhaengige Referenz-Aggregation an JEDER Position", () => {
  const rows = syntheticRows(11, 2500);
  const st = storeOf(rows);
  const e = new TickReplayEngine(st);
  for (let p = 0; p <= rows.length; p++) {
    e.advanceTo(p);
    assert.deepEqual(strip(e.candles()), strip(aggregateReference(st, p)), `Position ${p}`);
  }
});

test("1x/10x/100x und Seek liefern bei gleicher Position identische OHLCV", () => {
  const rows = syntheticRows(23, 5000);
  const st = storeOf(rows);
  const checkpoints = [1, 37, 499, 1500, 1501, 2999, 4321, 5000];
  const snap = (e) => strip(e.candles());
  const results = [];
  for (const stepSize of [1, 10, 100, 7919]) {
    // Derselbe Engine laeuft in Paketen der Groesse stepSize (wie gebuendelte Frames bei 1x/10x/100x) und wird
    // an jedem Checkpoint selbst abgelesen – kein Hilfs-Engine.
    const e = new TickReplayEngine(st);
    const got = [];
    for (const cp of checkpoints) {
      while (e.p < cp) e.advanceTo(Math.min(cp, e.p + stepSize));
      got.push(snap(e));
    }
    results.push(got);
  }
  assert.deepEqual(results[0], results[3]);
  const seekEngine = new TickReplayEngine(st);
  const seekGot = checkpoints.map((p) => { seekEngine.seek(p); return snap(seekEngine); });
  assert.deepEqual(results[0], results[1]);
  assert.deepEqual(results[0], results[2]);
  assert.deepEqual(results[0], seekGot);
});

test("Ruecksprung stellt exakt denselben Zustand her wie erneutes Abspielen", () => {
  const rows = syntheticRows(5, 3000);
  const st = storeOf(rows);
  const e = new TickReplayEngine(st);
  e.advanceTo(rows.length);
  const r = rng(99);
  for (let k = 0; k < 300; k++) {
    const p = Math.floor(r() * (rows.length + 1));
    e.seek(p);
    const fresh = new TickReplayEngine(st); fresh.advanceTo(p);
    assert.equal(e.p, p);
    assert.deepEqual(strip(e.candles()), strip(fresh.candles()), `Seek auf ${p}`);
    // Nach dem Ruecksprung wieder vorwaerts -> weiterhin identisch.
    const q = Math.min(rows.length, p + Math.floor(r() * 400));
    e.advanceTo(q); fresh.advanceTo(q);
    assert.deepEqual(strip(e.candles()), strip(fresh.candles()), `Vorwaerts auf ${q}`);
  }
});

test("Einzelschritt wendet genau einen Trade an", () => {
  const st = storeOf([[0, 10, 1, 1], [1, 11, 2, -1]]);
  const e = new TickReplayEngine(st);
  assert.equal(e.step(), true); assert.equal(e.p, 1); assert.equal(e.cur.volume, 1);
  assert.equal(e.step(), true); assert.equal(e.p, 2); assert.equal(e.cur.volume, 3);
  assert.equal(e.step(), false); assert.equal(e.p, 2);
});

test("Nachladen in Abschnitten: Engine wartet am Ladeende und ueberspringt nichts", () => {
  const rows = syntheticRows(3, 1200);
  const full = storeOf(rows);
  const st = createStore(rows.length);
  appendChunk(st, 0, decodeChunk(chunkBuffer(rows.slice(0, 500))));
  const e = new TickReplayEngine(st);
  e.advanceTo(1000);
  assert.equal(e.p, 500, "nicht ueber das Geladene hinaus");
  appendChunk(st, 500, decodeChunk(chunkBuffer(rows.slice(500))));
  e.advanceTo(1200);
  const ref = new TickReplayEngine(full); ref.advanceTo(1200);
  assert.deepEqual(strip(e.candles()), strip(ref.candles()));
});

test("appendChunk ist fail-closed (Luecke, Ueberlappung, Zeit rueckwaerts, ungueltige Werte)", () => {
  const st = createStore(4);
  appendChunk(st, 0, decodeChunk(chunkBuffer([[10, 1, 1, 1], [20, 1, 1, 1]])));
  assert.throws(() => appendChunk(st, 3, decodeChunk(chunkBuffer([[30, 1, 1, 1]]))), /keine Luecken/);
  assert.throws(() => appendChunk(st, 1, decodeChunk(chunkBuffer([[30, 1, 1, 1]]))), /keine Luecken/);
  assert.throws(() => appendChunk(st, 2, decodeChunk(chunkBuffer([[19, 1, 1, 1]]))), /chronologisch/);
  assert.throws(() => appendChunk(st, 2, decodeChunk(chunkBuffer([[30, 0, 1, 1]]))), /Preis/);
  assert.throws(() => appendChunk(st, 2, decodeChunk(chunkBuffer([[30, 1, 1, 5]]))), /Seite/);
  assert.throws(() => appendChunk(st, 2, decodeChunk(chunkBuffer([[30, 1, 1, 1], [31, 1, 1, 1], [32, 1, 1, 1]]))), /ueberschreitet/);
  assert.throws(() => decodeChunk(new ArrayBuffer(ROW_BYTES + 1)), /Vielfaches/);
});

test("Zeitnavigation: countAtOrBefore / countBefore mit gleichen Zeitstempeln", () => {
  const st = storeOf([[0, 1, 1, 1], [5, 1, 1, 1], [5, 1, 1, 1], [9, 1, 1, 1]]);
  assert.equal(countAtOrBefore(st, 5), 3);
  assert.equal(countBefore(st, 5), 1);
  assert.equal(countAtOrBefore(st, -1), 0);
  assert.equal(countAtOrBefore(st, 100), 4);
});

test("Kerzenstatus: laufend, abgeschlossen, Randkerze", () => {
  const c = { bucket: 1, firstIdx: 0, lastIdx: 0, open: 1, high: 1, low: 1, close: 1, volume: 1, buyVolume: 1, sellVolume: 0, trades: 1 };
  const win0 = 0, win1 = 10 * BUCKET_5M;
  assert.equal(candleStatus(c, BUCKET_5M, BUCKET_5M + 5, win0, win1, false), "laufend");
  assert.equal(candleStatus(c, BUCKET_5M, 2 * BUCKET_5M, win0, win1, false), "abgeschlossen");
  assert.equal(candleStatus(c, BUCKET_5M, BUCKET_5M + 5, win0, win1, true), "abgeschlossen");
  assert.equal(candleStatus(c, BUCKET_5M, BUCKET_5M + 5, BUCKET_5M + 1, win1, false), "rand-unvollstaendig");
});

test("Abspieluhr skaliert Marktzeit mit der Geschwindigkeit", () => {
  assert.equal(advanceClock(0, 1000, 1), 1000 * UNITS_PER_MS);
  assert.equal(advanceClock(0, 1000, 100), 100_000 * UNITS_PER_MS);
  assert.equal(advanceClock(5, -10, 10), 5);
});
