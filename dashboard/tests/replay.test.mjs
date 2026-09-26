// Tests für die Übersicht/Replay-Trennung (node --test, ohne zusätzliche Abhängigkeiten).
// Geprüft wird, dass zu einer Replay-Position keine Zukunftsdaten (spätere Kerzen, Exits,
// Trades oder Ergebnisse) sichtbar werden und die Trade-Navigation korrekt zuordnet.
import { test } from "node:test";
import assert from "node:assert/strict";
import {
  tradeStateAt, chartTradesAt, equityAt, replaySnapshot, relativeTradeIndex, tradeWindow, clampPos,
} from "../src/lib/replay.ts";

const candles = Array.from({ length: 10 }, (_, i) => ({ t: 1_000 * i, o: 100 + i, h: 101 + i, l: 99 + i, c: 100.5 + i, v: 1 }));
const trade = (o) => ({
  symbol: "MES", side: "Long", quantity: 1, entryTime: "", exitTime: "", entryPrice: 100, exitPrice: 105,
  grossPnL: 5, fees: 1, netPnL: 4, exitReason: "TakeProfit", stopLossPrice: 95, takeProfitPrice: 105, ambiguous: false, ...o,
});
// Trade 0: Bar 2 -> 4 (Gewinn). Trade 1: Bar 5 -> 5 (selbe Kerze, Verlust). Trade 2: Bar 6 -> 8 (Short).
const trades = [
  trade({ entryBarIndex: 2, exitBarIndex: 4, netPnL: 4 }),
  trade({ entryBarIndex: 5, exitBarIndex: 5, netPnL: -3, exitReason: "StopLoss", entryPrice: 106, exitPrice: 104 }),
  trade({ entryBarIndex: 6, exitBarIndex: 8, netPnL: 2, side: "Short" }),
];
// Equity-Punkte wie von der Engine (realisiert je Bar, openPnL = Mark-to-Market).
const equity = candles.map((_, i) => ({
  barIndex: i, time: "", equity: 0,
  realizedNetPnL: (i >= 4 ? 4 : 0) + (i >= 5 ? -3 : 0) + (i >= 8 ? 2 : 0),
  openPnL: i >= 2 && i < 4 ? 1.5 : i >= 6 && i < 8 ? -0.5 : 0,
}));

test("Trade-Zustand je Replay-Position: future / open / closed", () => {
  assert.equal(tradeStateAt(trades[0], 1), "future");
  assert.equal(tradeStateAt(trades[0], 2), "open", "Entry am OPEN von Bar 2 ist mit der vollständigen Kerze bekannt");
  assert.equal(tradeStateAt(trades[0], 3), "open");
  assert.equal(tradeStateAt(trades[0], 4), "closed");
  assert.equal(tradeStateAt(trades[1], 5), "closed", "Entry und Exit in derselben Kerze");
});

test("Replay-Chart zeigt keine zukünftigen Trades und keine noch unbekannten Exits", () => {
  const at3 = chartTradesAt(trades, 3);
  assert.equal(at3.length, 1, "nur Trade 0 ist bis Bar 3 eröffnet");
  assert.equal(at3[0].exit, null, "Exit von Trade 0 (Bar 4) ist bei Bar 3 noch unbekannt");
  assert.equal(at3[0].index, 0);

  const at5 = chartTradesAt(trades, 5);
  assert.deepEqual(at5.map((t) => t.index), [0, 1]);
  assert.equal(at5[1].exit?.barIndex, 5, "Same-Bar-Trade ist bei Bar 5 vollständig bekannt");

  const overview = chartTradesAt(trades, null);
  assert.equal(overview.length, 3);
  assert.ok(overview.every((t) => t.exit !== null), "Übersicht zeigt alle abgeschlossenen Ergebnisse");
});

test("Replay-Kennzahlen stammen aus Engine-Equity bis zur Position, ohne Zukunftsdaten", () => {
  const s3 = replaySnapshot(candles, trades, equity, 3);
  assert.equal(s3.closed.length, 0);
  assert.equal(s3.open?.index, 0);
  assert.equal(s3.status, "LONG");
  assert.equal(s3.realizedNetPnL, 0);
  assert.equal(s3.openPnL, 1.5);
  assert.equal(s3.candle.t, 3_000, "aktuelle Kerze = pos, keine spätere");

  const s7 = replaySnapshot(candles, trades, equity, 7);
  assert.deepEqual(s7.closed.map((c) => c.index), [0, 1]);
  assert.equal(s7.realizedNetPnL, 1, "4 - 3; Trade 2 (Exit Bar 8) noch nicht enthalten");
  assert.equal(s7.wins, 1);
  assert.equal(s7.losses, 1);
  assert.equal(s7.status, "SHORT");

  const sEnd = replaySnapshot(candles, trades, equity, 99);
  assert.equal(sEnd.pos, 9, "Position wird auf den geladenen Bereich begrenzt");
  assert.equal(sEnd.realizedNetPnL, 3);
  assert.equal(sEnd.status, "FLAT");
  assert.equal(sEnd.openPnL, 0);
});

test("equityAt nimmt den letzten Punkt ≤ pos (Randkerzen ohne Punkt → null)", () => {
  const sparse = [{ barIndex: 2, realizedNetPnL: 1 }, { barIndex: 3, realizedNetPnL: 2 }];
  assert.equal(equityAt(sparse, 1), null);
  assert.equal(equityAt(sparse, 2).realizedNetPnL, 1);
  assert.equal(equityAt(sparse, 50).realizedNetPnL, 2);
});

test("Trade-Navigation: vorheriger/nächster, Grenzen, ohne Auswahl", () => {
  assert.equal(relativeTradeIndex(null, 1, 3), 0);
  assert.equal(relativeTradeIndex(null, -1, 3), 2);
  assert.equal(relativeTradeIndex(0, -1, 3), 0);
  assert.equal(relativeTradeIndex(2, 1, 3), 2);
  assert.equal(relativeTradeIndex(1, 1, 3), 2);
  assert.equal(relativeTradeIndex(null, 1, 0), null);
});

test("Zoom-Fenster enthält Entry und Exit samt Kontext und bleibt im Datenbereich", () => {
  const w = tradeWindow(50, 60, 100);
  assert.ok(w.start < 50 && w.end > 60);
  const same = tradeWindow(5, 5, 100);
  assert.ok(same.start <= 1 && same.end >= 9, "auch bei Entry=Exit genug Kontext");
  const edge = tradeWindow(0, 98, 100);
  assert.equal(edge.start, 0);
  assert.equal(edge.end, 99);
  assert.equal(clampPos(-3, 10), 0);
  assert.equal(clampPos(12, 10), 9);
});
