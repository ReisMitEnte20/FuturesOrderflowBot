// Tests für die Umwandlung von Rithmic-Bars in Kerzen + Orderflow (node --test).
import { test } from "node:test";
import assert from "node:assert/strict";
import { mapBars, exchangeFor } from "../src/lib/rithmicBars.ts";

const bar = (min, o) => ({
  symbol: "MESZ6", endTime: `2026-09-28T20:${String(min).padStart(2, "0")}:00Z`,
  open: 100, high: 101, low: 99, close: 100.5, volume: 10, bidVolume: 4, askVolume: 6, ...o,
});

test("Bars werden zeitlich sortiert und als Kerzen übernommen", () => {
  const { candles } = mapBars([bar(2, { close: 102 }), bar(1, { close: 101 })]);
  assert.deepEqual(candles.map((c) => c.close), [101, 102]);
  assert.equal(candles[0].volume, 10);
});

test("Orderflow: Ask-Volumen = Käufer-Aggressor, Bid-Volumen = Verkäufer-Aggressor, CVD kumuliert", () => {
  const { orderFlow } = mapBars([bar(1, { askVolume: 6, bidVolume: 4 }), bar(2, { askVolume: 1, bidVolume: 9 })]);
  assert.deepEqual(orderFlow.map((o) => [o.aggressiveBuyVolume, o.aggressiveSellVolume, o.delta, o.cvd]), [
    [6, 4, 2, 2],
    [1, 9, -8, -6],
  ]);
});

test("Börsen-Zuordnung", () => {
  assert.equal(exchangeFor("MES"), "CME");
  assert.equal(exchangeFor("CL"), "NYMEX");
  assert.equal(exchangeFor("ZN"), "CBOT");
});

import { appendTape } from "../src/lib/rithmicBars.ts";

const tick = (seq, o) => ({ seq, time: "", price: 100, size: 1, aggressor: "Buy", bid: 0, ask: 0, ...o });

test("Tape: neue Ticks vorne, keine Duplikate, begrenzt", () => {
  let tape = appendTape([], [tick(1), tick(2)], 3);
  assert.deepEqual(tape.map((t) => t.seq), [2, 1]);
  tape = appendTape(tape, [tick(2), tick(3), tick(4)], 3);
  assert.deepEqual(tape.map((t) => t.seq), [4, 3, 2]);
});
