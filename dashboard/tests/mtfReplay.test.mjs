// Trade-Rewind: sichtbar ist nur, was bei der Cursor-Position bekannt war (geschlossene Bars erst ab ihrem Abschluss-Ereignis,
// offene Bar mit bisherigen Werten, Marker/Entscheidungen erst ab ihrem Ereignis). Zurückspulen verbirgt Späteres wieder.
import { test } from "node:test";
import assert from "node:assert/strict";
import { visibleBars, stateAt, knownAt, tradeStats } from "../src/lib/mtfApi.ts";

// Bar-Zeilen: [bar, start, o, h, l, c, vol, buy, sell, ersterEreignis, geschlossenBeiEreignis]
const w = {
  closedBars: { "5m": [[0, "t0", 100, 105, 99, 104, 10, 6, 4, 1, 50], [1, "t1", 104, 108, 103, 107, 12, 8, 4, 50, 90]] },
  states: [
    [49, [0, 100, 105, 99, 104, 9, 5, 4], [], []],
    [50, [1, 104, 104, 104, 104, 1, 1, 0], [], []],
    [89, [1, 104, 108, 103, 106, 11, 7, 4], [], []],
    [90, [2, 107, 107, 107, 107, 1, 0, 1], [], []],
  ],
};

test("Vor dem Abschluss: Bar 0 ist offen mit bisherigen Werten, Endwerte unsichtbar", () => {
  const b = visibleBars(w, "5m", 49);
  assert.equal(b.length, 1);
  assert.deepEqual([b[0].status, b[0].close, b[0].volume], ["offen", 104, 9]);
});

test("Mit dem Abschluss-Ereignis: Bar 0 geschlossen (Endwerte), Bar 1 offen", () => {
  const b = visibleBars(w, "5m", 50);
  assert.deepEqual(b.map((x) => [x.index, x.status, x.volume]), [[0, "geschlossen", 10], [1, "offen", 1]]);
});

test("Zurückspulen verbirgt spätere Abschlüsse wieder", () => {
  assert.equal(visibleBars(w, "5m", 95).filter((x) => x.status === "geschlossen").length, 2);
  assert.equal(visibleBars(w, "5m", 60).filter((x) => x.status === "geschlossen").length, 1);
  assert.equal(stateAt(w, 70)[0], 50, "letzter Zustand ≤ Cursor");
  assert.equal(stateAt(w, 10), null);
});

test("Marker und Entscheidungen erst ab ihrem Ereignis; Kennzahlen ohne Ersatzwerte", () => {
  const items = [{ eventIndex: 40 }, { eventIndex: 80 }, { eventIndex: -1 }];
  assert.equal(knownAt(items, 50).length, 1);
  assert.equal(knownAt(items, 80).length, 2);
  assert.deepEqual(tradeStats([{ netPnL: 10 }, { netPnL: -5 }]), { trades: 2, winRate: 0.5, profitFactor: 2, net: 5 });
  assert.equal(tradeStats([{ netPnL: 3 }]).profitFactor, null, "ohne Verlust: nicht definiert statt Ersatzwert");
});
