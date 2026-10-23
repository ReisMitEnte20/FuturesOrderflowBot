// Tests fuer buildSnapshot / pointInTimeTrade (node --test, ohne zusaetzliche Abhaengigkeiten).
// Belegt, dass ein "Snapshot @t" KEINE spaeteren Daten enthaelt: keine zukuenftigen Exit-/PnL-Daten des
// gewaehlten Trades, keine finalen Werte noch offener Bars, keine spaeteren Profil-/Tape-Daten. Gleichstand
// beim Zeitstempel (== t) gilt als bereits bekannt (Ereignisreihenfolge).
import { test } from "node:test";
import assert from "node:assert/strict";
import { buildSnapshot, pointInTimeTrade } from "../src/lib/workspace.ts";

// 6 Bars, je 1000 ms auseinander. Bar i spannt [i*1000, (i+1)*1000). barAt() nimmt nur VOLLSTAENDIG
// geschlossene Bars (close <= t). Bei t=1000 ist nur Bar 0 abgeschlossen.
const baseCandles = [
  { t: 0, o: 100, h: 101, l: 99, c: 100, v: 100, delta: 10 },
  { t: 1000, o: 100, h: 103, l: 100, c: 102, v: 120, delta: 20 },
  { t: 2000, o: 102, h: 106, l: 101, c: 105, v: 140, delta: 30 },
  { t: 3000, o: 105, h: 109, l: 104, c: 108, v: 160, delta: 40 },
  { t: 4000, o: 108, h: 110, l: 106, c: 107, v: 150, delta: -10 },
  { t: 5000, o: 107, h: 108, l: 103, c: 104, v: 130, delta: -20 },
];
// Footprint je Bar (Bid/Ask je Preis). Bar 0 hat POC bei 100; spaetere Bars haben andere POCs.
const baseFootprint = [
  [{ p: 100, b: 40, a: 60 }],
  [{ p: 102, b: 50, a: 70 }],
  [{ p: 105, b: 60, a: 80 }],
  [{ p: 108, b: 70, a: 90 }],
  [{ p: 107, b: 80, a: 70 }],
  [{ p: 104, b: 70, a: 60 }],
];
const baseTape = [
  [500, 100, 5, 1], [900, 100, 7, -1],
  [1500, 102, 9, 1], [2500, 105, 11, 1], [3500, 108, 13, -1],
];

const mkTrade = (o = {}) => ({
  index: 0, side: "Long", qty: 1,
  entryBarIndex: 1, exitBarIndex: 3, entryPrice: 101, exitPrice: 108,
  exitReason: "TakeProfit", stopLossPrice: 95, takeProfitPrice: 108, ambiguous: false,
  netPnL: 42, entryTimeMs: 1000, exitTimeMs: 3500, entryTime: "01:00", exitTime: "03:00", ...o,
});

const mkData = (candles, footprint, tape, trade) => ({
  meta: {
    instrument: "MNQ", dataSource: "ATAS-Replay (Test)", marketStartMs: 0, marketEndMs: 5000,
    ticksPerBar: 500, pointValue: 2, tickSizeNote: "test", feePerSide: 0.5, slippageTicks: 1, generatedUtc: "",
  },
  availability: { dom: "nicht verfuegbar (unvalidiert)" },
  quality: { priceGranularity: "1-Punkt", drops: 0, aggressor: "aus Trades", footprintSource: "Trades", domNote: "unvalidiert" },
  candles, footprint, tape, strategies: [{ name: "TestOF", usesOrderflow: true, netPnL: 42, fees: 1, finalEquity: 42, trades: [trade] }],
});

const OPTS = { vwapAnchorBar: 0, cvdAnchorBar: 0, bubbleMinVol: 10 };

test("Snapshot @t redigiert zukuenftige Exit-/PnL-Daten des offenen Trades", () => {
  const d = mkData(baseCandles, baseFootprint, baseTape, mkTrade());
  const snap = buildSnapshot(d, 0, d.strategies[0].trades[0], { ...OPTS, pointInTimeMs: 1000 });
  const st = snap.selectedTrade;
  assert.ok(st, "Trade mit Entry == t ist sichtbar");
  assert.equal(st.entryPrice, 101, "Entry-Daten (bei t bekannt) bleiben erhalten");
  assert.equal(st.exitKnown, false, "Exit (bei t=3500) liegt in der Zukunft");
  assert.equal(st.exitPrice, null);
  assert.equal(st.netPnL, null);
  assert.equal(st.exitTimeMs, null);
  assert.equal(st.exitBarIndex, null);
});

test("Spaetere Daten veraendern den Snapshot @t nicht (Bars/Footprint/Tape/Exit vergiftet)", () => {
  const base = mkData(baseCandles, baseFootprint, baseTape, mkTrade());
  // Vergiftete Kopie: alles ab Bar 1 (= Zukunft bei t=1000) auf Extremwerte, Exit des Trades manipuliert.
  const poison = structuredClone(base);
  for (let i = 1; i < poison.candles.length; i++) {
    poison.candles[i] = { t: i * 1000, o: 99999, h: 99999, l: 99999, c: 99999, v: 1e9, delta: 1e9 };
    poison.footprint[i] = [{ p: 99999, b: 1e9, a: 1e9 }];
  }
  poison.tape.push([1200, 99999, 1e9, 1], [4800, 99999, 1e9, -1]);
  const pt = poison.strategies[0].trades[0];
  Object.assign(pt, { exitPrice: 999, netPnL: -999, exitTimeMs: 9999, exitBarIndex: 5, exitReason: "StopLoss" });

  const sBase = buildSnapshot(base, 0, base.strategies[0].trades[0], { ...OPTS, pointInTimeMs: 1000 });
  const sPois = buildSnapshot(poison, 0, pt, { ...OPTS, pointInTimeMs: 1000 });

  // Alle aus dem Zeitpunkt t abgeleiteten Felder muessen identisch sein (generatedUtc ausgenommen).
  assert.deepEqual(sPois.values, sBase.values, "POC/VWAP/CVD nur aus Bar 0 -> unveraendert");
  assert.deepEqual(sPois.selectedTrade, sBase.selectedTrade, "redigierter Trade identisch, kein zukuenftiger Exit/PnL");
  assert.deepEqual(sPois.indicatorParams.vpRange, [0, 0], "vpRange nur bis letzte vollstaendige Bar");
  assert.deepEqual(sPois.indicatorParams, sBase.indicatorParams);
  assert.deepEqual(sPois.anchors, sBase.anchors);
  // Und die vergifteten Zukunftswerte sind nirgends durchgesickert:
  assert.equal(sPois.selectedTrade.netPnL, null);
  assert.ok(sPois.values.poc !== 99999 && sPois.values.vwap !== 99999, "kein Zukunftspreis im Profil/VWAP");
  assert.equal(sPois.values.poc, 100);
});

test("Noch offene (unfertige) Bar fliesst nicht in Snapshot-Werte ein", () => {
  const d = mkData(baseCandles, baseFootprint, baseTape, mkTrade());
  const atClose = buildSnapshot(d, 0, d.strategies[0].trades[0], { ...OPTS, pointInTimeMs: 1000 }); // Bar 0 fertig, Bar 1 offen
  const midBar1 = buildSnapshot(d, 0, d.strategies[0].trades[0], { ...OPTS, pointInTimeMs: 1500 }); // mitten in Bar 1
  assert.deepEqual(midBar1.values, atClose.values, "Bar 1 ist bei t=1500 noch offen und darf die Werte nicht aendern");
});

test("Gleicher Zeitstempel: Exit genau bei t gilt als bekannt", () => {
  const d = mkData(baseCandles, baseFootprint, baseTape, mkTrade({ exitTimeMs: 3000, exitBarIndex: 3 }));
  const snap = buildSnapshot(d, 0, d.strategies[0].trades[0], { ...OPTS, pointInTimeMs: 3000 });
  assert.equal(snap.selectedTrade.exitKnown, true, "Exit-Zeitstempel == t -> Exit ist eingetreten/bekannt");
  assert.equal(snap.selectedTrade.exitPrice, 108);
});

test("Rueckblickender Snapshot (pit=null) enthaelt finale Exit-/PnL-Werte", () => {
  const d = mkData(baseCandles, baseFootprint, baseTape, mkTrade());
  const snap = buildSnapshot(d, 0, d.strategies[0].trades[0], { ...OPTS, pointInTimeMs: null });
  assert.equal(snap.retrospective, true);
  assert.equal(snap.selectedTrade.exitKnown, true);
  assert.equal(snap.selectedTrade.exitPrice, 108);
  assert.equal(snap.selectedTrade.netPnL, 42);
});

test("pointInTimeTrade: Trade mit Entry in der Zukunft ist unsichtbar", () => {
  const future = mkTrade({ entryTimeMs: 4000, exitTimeMs: 4500 });
  assert.equal(pointInTimeTrade(future, 1000), null);
  // Entry exakt bei t -> sichtbar.
  assert.ok(pointInTimeTrade(mkTrade({ entryTimeMs: 1000 }), 1000));
});
