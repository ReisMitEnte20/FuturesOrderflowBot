// Tests fuer die Tape-Abdeckung (node --test). Das mitgelieferte Tape kann aus Browser-Schutz GEKAPPT sein
// (nur die ersten N Ticks). Dann duerfen Tape-Ansicht/Bubbles fuer nicht abgedeckte Zeitfenster NICHT als
// "keine Trades"/Null erscheinen, sondern als "nicht verfuegbar". Die vollstaendig dargestellten Indikatoren
// (VWAP/CVD/Volume-Profil) muessen trotzdem aus der VOLLSTAENDIGEN Tick-Basis (Kerzen/Footprint) verfuegbar sein.
import { test } from "node:test";
import assert from "node:assert/strict";
import { tapeCoverage, tapeCoversTime, tradeBubbles, buildSnapshot } from "../src/lib/workspace.ts";

// 6 volle Bars (je 1000 ms). Footprint je Bar. Das Tape deckt NUR die ersten ~400 ms ab (2 von 6000 Ticks).
const candles = [
  { t: 0, o: 100, h: 101, l: 99, c: 100, v: 100, delta: 10 },
  { t: 1000, o: 100, h: 103, l: 100, c: 102, v: 120, delta: 20 },
  { t: 2000, o: 102, h: 106, l: 101, c: 105, v: 140, delta: 30 },
  { t: 3000, o: 105, h: 109, l: 104, c: 108, v: 160, delta: 40 },
  { t: 4000, o: 108, h: 110, l: 106, c: 107, v: 150, delta: -10 },
  { t: 5000, o: 107, h: 108, l: 103, c: 104, v: 130, delta: -20 },
];
const footprint = [
  [{ p: 100, b: 40, a: 60 }], [{ p: 102, b: 50, a: 70 }], [{ p: 105, b: 60, a: 80 }],
  [{ p: 108, b: 70, a: 90 }], [{ p: 107, b: 80, a: 70 }], [{ p: 104, b: 70, a: 60 }],
];
// GEKAPPTES Tape: nur 2 Ticks, beide < 500 ms. Provenienz meldet 6000 Gesamt-Ticks => truncated.
const truncatedTape = [[100, 100, 50, 1], [400, 100, 20, -1]];

// Trade liegt WEIT hinter der Tape-Abdeckung (Entry Bar 3 @3000 ms, Exit Bar 4 @4000 ms).
const tradeOutside = {
  index: 0, side: "Long", qty: 1, entryBarIndex: 3, exitBarIndex: 4, entryPrice: 105, exitPrice: 108,
  exitReason: "TakeProfit", stopLossPrice: 100, takeProfitPrice: 108, ambiguous: false,
  netPnL: 6, entryTimeMs: 3000, exitTimeMs: 4000, entryTime: "00:00:03", exitTime: "00:00:04",
};

const mkData = (tape, ticks) => ({
  meta: { instrument: "MES", dataSource: "Sierra MES (UTC-Kalendertag, Test)", marketStartMs: 0, marketEndMs: 5000, ticksPerBar: 500, pointValue: 5, tickSizeNote: "0,25", feePerSide: 0.39, slippageTicks: 1, generatedUtc: "" },
  availability: { tape: "gekappt", dom: "nicht verfuegbar (unvalidiert)" },
  quality: { priceGranularity: "0,25 (nativ)", drops: 0, aggressor: "aus Sierra", footprintSource: "je 500-Tick-Bar", domNote: "unvalidiert" },
  candles, footprint, tape,
  strategies: [{ name: "DeltaFlow(OF)", usesOrderflow: true, netPnL: 6, fees: 1, finalEquity: 6, trades: [tradeOutside] }],
  provenance: { ticks },
});

test("tapeCoverage erkennt ein gekapptes Tape und sein Zeitfenster", () => {
  const d = mkData(truncatedTape, 6000);
  const c = tapeCoverage(d);
  assert.equal(c.truncated, true);
  assert.equal(c.count, 2);
  assert.equal(c.totalTicks, 6000);
  assert.equal(c.startMs, 100);
  assert.equal(c.endMs, 400);
});

test("tapeCoversTime: Trade AUSSERHALB der Tape-Abdeckung ist nicht abgedeckt, innerhalb schon", () => {
  const d = mkData(truncatedTape, 6000);
  assert.equal(tapeCoversTime(d, tradeOutside.exitTimeMs), false, "Exit @4000 liegt hinter dem Tape-Ende @400");
  assert.equal(tapeCoversTime(d, tradeOutside.entryTimeMs), false, "Entry @3000 liegt hinter dem Tape-Ende @400");
  assert.equal(tapeCoversTime(d, 300), true, "300 ms liegt im Tape-Fenster");
  assert.equal(tapeCoversTime(d, 500), false, "500 ms liegt bereits hinter dem Tape-Ende");
});

test("Bubbles im Trade-Zeitfenster ausserhalb der Tape-Abdeckung sind leer (= Anlass fuer 'nicht verfuegbar', kein Nullwert)", () => {
  const d = mkData(truncatedTape, 6000);
  // Fenster = tatsaechliche Trade-Spanne [entry..exit]; hier existiert kein Tape (nur erste 400 ms sind abgedeckt).
  const b = tradeBubbles(d.tape, 1, tradeOutside.entryTimeMs, tradeOutside.exitTimeMs);
  assert.equal(b.length, 0, "kein Tape in diesem Fenster => keine Bubbles; UI muss 'nicht verfuegbar' zeigen, nicht 'keine grossen Trades'");
  // Und: alle vorhandenen Bubbles liegen auf fruehen Bars (<= Abdeckungsende), nie im Trade-Bereich (Bar 3/4).
  const starts = d.candles.map((c) => c.t);
  const barOf = (t) => { let lo = 0, hi = starts.length - 1, r = 0; while (lo <= hi) { const m = (lo + hi) >> 1; if (starts[m] <= t) { r = m; lo = m + 1; } else hi = m - 1; } return r; };
  const barsWithBubbles = tradeBubbles(d.tape, 1).map((x) => barOf(x.t));
  assert.ok(barsWithBubbles.every((bi) => bi <= 0), "Tape-Ticks (@100/@400 ms) liegen in Bar 0; keine Bubble im Trade-Bereich Bar 3/4");
});

test("Vollstaendige Indikatoren (VWAP/CVD/Volume-Profil) bleiben fuer den Trade ausserhalb des Tapes aus der vollen Basis verfuegbar", () => {
  const d = mkData(truncatedTape, 6000);
  const snap = buildSnapshot(d, 0, tradeOutside, { vwapAnchorBar: 0, cvdAnchorBar: 0, bubbleMinVol: 1, pointInTimeMs: tradeOutside.exitTimeMs });
  assert.ok(snap.values, "Werte sind trotz fehlendem Tape aus Kerzen/Footprint verfuegbar");
  assert.equal(typeof snap.values.vwap, "number", "VWAP aus Footprint (volle Basis)");
  assert.equal(typeof snap.values.cvd, "number", "CVD aus Kerzen-Delta (volle Basis)");
  assert.ok(snap.values.poc > 0, "Volume-Profil-POC aus Footprint (volle Basis)");
});

test("Vollstaendiges Tape (nicht gekappt): tapeCoversTime ist immer true", () => {
  const fullTape = [[100, 100, 5, 1], [1500, 102, 9, 1], [3500, 108, 13, -1], [4500, 107, 4, -1]];
  const d = mkData(fullTape, fullTape.length); // provenance.ticks == tape.length => nicht gekappt
  const c = tapeCoverage(d);
  assert.equal(c.truncated, false);
  assert.equal(tapeCoversTime(d, 4000), true);
  assert.equal(tapeCoversTime(d, 99999), true, "bei vollstaendigem Tape keine kuenstliche Grenze");
});
