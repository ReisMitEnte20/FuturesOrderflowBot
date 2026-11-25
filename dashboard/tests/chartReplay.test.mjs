// Tests der gemeinsamen Ansicht „Chart & Replay“ (node --test, ohne zusaetzliche Abhaengigkeiten).
//
// Kernanforderungen: Marker nur aus gespeicherten Fills, offene Positionen ohne erfundenen Ausstieg, Fill-Zeit muss
// zur geladenen Kerze passen (sonst ehrlich "nicht eindeutig"), Replay zeigt nur bis zur Position Bekanntes,
// Footprint/Delta aus angewandten Ticks, ueberlagerte Fills erst nach Erreichen der Replay-Uhr.
import { test } from "node:test";
import assert from "node:assert/strict";
import {
  sessionRange, fillInBar, dayTrades, dayTradesAt, reachability, footprint, deltaCvd, isoUnits, overlayEvents, overlayReached,
} from "../src/lib/chartReplay.ts";
import { createStore, TickReplayEngine } from "../src/lib/tickReplay.ts";

const M5 = 5 * 60_000;
const T0 = Date.UTC(2026, 2, 23, 13, 30);
// Zwei Sessions mit je 4 Kerzen (5 Minuten), dazwischen eine Luecke.
const candles = [0, 1, 2, 3].map((i) => ({ t: T0 + i * M5, o: 1, h: 2, l: 0.5, c: 1.5, v: 10 }))
  .concat([0, 1, 2, 3].map((i) => ({ t: T0 + 86_400_000 + i * M5, o: 1, h: 2, l: 0.5, c: 1.5, v: 10 })));
const sessions = [
  { key: "2026-03-23", firstT: T0, lastT: T0 + 3 * M5 },
  { key: "2026-03-24", firstT: T0 + 86_400_000, lastT: T0 + 86_400_000 + 3 * M5 },
];
const trade = (o) => ({
  index: 0, side: "Long", entryT: T0 + M5, exitT: T0 + 3 * M5, entryPrice: 100.25, exitPrice: 101, entryBar: 1, exitBar: 3,
  exitReason: "TakeProfit", ambiguous: false, closedAtEnd: false, session: "2026-03-23", net: 3.47, stopLoss: 99, takeProfit: 101, ...o,
});

test("sessionRange findet die Kerzen einer Session", () => {
  assert.deepEqual(sessionRange(candles, sessions[0]), { i0: 0, i1: 3 });
  assert.deepEqual(sessionRange(candles, sessions[1]), { i0: 4, i1: 7 });
  assert.equal(sessionRange(candles, { key: "x", firstT: 0, lastT: 1 }), null);
});

test("fillInBar: Kerze [t, naechste t), letzte Kerze ueber den Zeitrahmen", () => {
  assert.ok(fillInBar(candles, 1, T0 + M5, M5));
  assert.ok(fillInBar(candles, 1, T0 + M5 + 299_999, M5));
  assert.ok(!fillInBar(candles, 1, T0 + 2 * M5, M5));
  assert.ok(fillInBar(candles, 7, T0 + 86_400_000 + 3 * M5 + 1000, M5));
  assert.ok(!fillInBar(candles, 7, T0 + 86_400_000 + 4 * M5, M5));
  // Ausstieg der OHLC-Engine innerhalb der Kerze: Zeitstempel = Kerzenschluss (gehoert zur Ausstiegskerze, nicht zur naechsten).
  assert.ok(fillInBar(candles, 1, T0 + 2 * M5, M5, true));
  assert.ok(!fillInBar(candles, 1, T0 + 2 * M5 + 1, M5, true));
});

test("SL/TP-Ausstieg mit Kerzenschluss-Zeitstempel wird der Ausstiegskerze zugeordnet", () => {
  const [d] = dayTrades(candles, [trade({ exitT: T0 + 4 * M5, exitBar: 3 })], sessions[0], M5);
  assert.equal(d.status, "ok");
  assert.equal(d.chart.exit.barIndex, 3);
});

test("Marker relativ zum Tag aus den gespeicherten Fills (Preis, Richtung, Bar)", () => {
  const [d] = dayTrades(candles, [trade({ index: 4, session: "2026-03-24", entryT: T0 + 86_400_000 + M5, exitT: T0 + 86_400_000 + 2 * M5, entryBar: 5, exitBar: 6 })], sessions[1], M5);
  assert.equal(d.status, "ok");
  assert.deepEqual(d.chart, { index: 4, side: "Long", entryBarIndex: 1, entryPrice: 100.25, exit: { barIndex: 2, price: 101, reason: "TakeProfit", ambiguous: false }, stopLossPrice: 99, takeProfitPrice: 101 });
});

test("offene (am Datenende bewertete) Position bekommt keinen Ausstieg", () => {
  const [d] = dayTrades(candles, [trade({ closedAtEnd: true })], sessions[0], M5);
  assert.equal(d.status, "open");
  assert.equal(d.chart.exit, null);
});

test("Fill-Zeit passt nicht zur Kerze -> nicht eindeutig, kein Marker", () => {
  const [d] = dayTrades(candles, [trade({ entryT: T0 + 2 * M5 + 1 })], sessions[0], M5);
  assert.equal(d.status, "mismatch");
  assert.equal(d.chart, null);
});

test("Replay: Entry erst ab Entry-Kerze, Exit erst ab Exit-Kerze", () => {
  const day = dayTrades(candles, [trade({})], sessions[0], M5);
  assert.deepEqual(dayTradesAt(day, 0), []);
  assert.equal(dayTradesAt(day, 1)[0].exit, null);
  assert.equal(dayTradesAt(day, 2)[0].exit, null);
  assert.equal(dayTradesAt(day, 3)[0].exit.price, 101);
});

test("Erreichbarkeit zaehlt nur geschlossene Trades", () => {
  const r = reachability(candles, [trade({}), trade({ index: 1, closedAtEnd: true }), trade({ index: 2, session: "fehlt" })], sessions, M5);
  assert.deepEqual({ total: r.total, closed: r.closed, open: r.open, reachable: r.reachable }, { total: 3, closed: 2, open: 1, reachable: 1 });
  assert.equal(r.problems[0].index, 2);
});

function store(rows) {
  const s = createStore(rows.length);
  rows.forEach((r, i) => { s.t[i] = r[0]; s.price[i] = r[1]; s.vol[i] = r[2]; s.side[i] = r[3]; });
  s.loaded = rows.length;
  return s;
}

test("Footprint und Delta aus angewandten Ticks; Replay am Kerzenschluss == Uebersicht", () => {
  const B = 3_000_000_000;
  const s = store([[0, 10, 2, 1], [5, 10, 1, -1], [7, 10.25, 3, 1], [B - 1, 9.75, 4, -1], [B, 10, 1, 1], [B + 10, 10.5, 2, -1]]);
  const fp = footprint(s, 0, 4);
  assert.deepEqual(fp.map((l) => [l.price, l.bid, l.ask]), [[10.25, 0, 3], [10, 1, 2], [9.75, 4, 0]]);
  const overview = new TickReplayEngine(s, B); overview.advanceTo(s.loaded);
  const replay = new TickReplayEngine(s, B); replay.seek(4);   // genau am Schluss der ersten Kerze
  const [o1] = overview.candles(), r1 = replay.candles()[0];
  for (const k of ["open", "high", "low", "close", "volume", "buyVolume", "sellVolume", "trades"]) assert.equal(r1[k], o1[k], k);
  assert.deepEqual(deltaCvd(overview.candles()), { delta: [0, -1], cvd: [0, -1] });
  replay.seek(2); replay.advanceTo(4);                          // zurueck und wieder vor: identisch
  assert.deepEqual(replay.candles()[0], o1);
});

test("isoUnits rechnet exakt auf 100 ns", () => {
  const a = Date.UTC(2026, 10, 18, 14, 30);
  assert.equal(isoUnits("2026-11-18T14:30:00.0000001Z", a), 1);
  assert.equal(isoUnits("2026-11-18T14:30:01.123456789Z", a), 11_234_567);
  assert.equal(isoUnits("2026-11-18T14:30:01Z", a), 10_000_000);
  assert.equal(isoUnits("kaputt", a), null);
});

test("Ueberlagerte Fills erscheinen erst nach Erreichen der Replay-Uhr; offene Trades ohne Exit", () => {
  const a = Date.UTC(2026, 10, 18, 14, 30);
  const evs = overlayEvents([
    { index: 0, side: "Long", qty: 1, entryIso: "2026-11-18T14:30:00.0000005Z", entryMs: a, entryPrice: 10, exitIso: null, exitMs: a + 1, exitPrice: 11, exitReason: "x", signalIso: "2026-11-18T14:30:00.0000002Z" },
    { index: 1, side: "Short", qty: 1, entryIso: null, entryMs: a + 2, entryPrice: 12, exitIso: null, exitMs: null, exitPrice: null, exitReason: "", signalIso: null },
  ], a);
  assert.deepEqual(evs.map((e) => [e.trade, e.kind, e.precision]), [[0, "signal", "100ns"], [0, "entry", "100ns"], [0, "exit", "ms"], [1, "entry", "ms"]]);
  const s = store([[1, 10, 1, 1], [5, 10, 1, 1], [6, 10, 1, -1], [10_500, 11, 1, 1], [30_000, 12, 1, -1]]);
  const entry = evs[1], exitMs = evs[2];
  assert.equal(overlayReached(entry, s, 1), false);  // Tick bei 1 angewandt, Entry bei 5 noch nicht
  assert.equal(overlayReached(entry, s, 2), true);
  assert.equal(overlayReached(exitMs, s, 3), false); // ms-genau: erst wenn alle Ticks bis Ende der Millisekunde (19.999) angewandt sind
  assert.equal(overlayReached(exitMs, s, 4), true);
  const partial = { ...s, loaded: 2 };
  assert.equal(overlayReached(exitMs, partial, 2), false); // nicht alles bis zur Ereigniszeit geladen -> nie zu frueh
});
