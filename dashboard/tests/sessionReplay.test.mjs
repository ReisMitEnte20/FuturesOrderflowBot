// Tests des gemeinsamen Replays „London + New York“ (node --test, ohne zusaetzliche Abhaengigkeiten).
// Kern: Sessions hintereinander in einer Zeitbasis (exakt, auf dem Kerzenraster), Ueberlappung fail-closed, Pause als
// eigener Zustand ohne Werte der naechsten Session, Pausen-Slot ohne Kerze, Session-CVD startet je Session neu.
import { test } from "node:test";
import assert from "node:assert/strict";
import {
  layoutSegments, segmentAtTime, segmentOfIndex, pauseAt, humanDuration, transitionText, slotTemplates, sessionDeltaCvd, vwap,
} from "../src/lib/sessionReplay.ts";
import { createStore, appendChunk, TickReplayEngine, countAtOrBefore } from "../src/lib/tickReplay.ts";

const B = 3_000_000_000; // 5 min in 100 ns
const MS = 10_000;
const ldnAnchor = Date.UTC(2026, 10, 17, 8, 0);   // 08:00 UTC
const nyAnchor = Date.UTC(2026, 10, 17, 14, 30);  // 14:30 UTC (EST)
const london = { id: "L", label: "London", session: "London", anchorUnixMs: ldnAnchor, windowStartUnits: 0, windowEndUnits: 36 * B, count: 3, bucketUnits: B };
const ny = { id: "N", label: "New York", session: "NewYork", anchorUnixMs: nyAnchor, windowStartUnits: 0, windowEndUnits: 30 * B, count: 2, bucketUnits: B };

test("Sessions liegen in einer Zeitbasis hintereinander, Offset exakt auf dem Raster", () => {
  const l = layoutSegments([london, ny]);
  assert.equal(l.baseAnchorMs, ldnAnchor);
  assert.equal(l.segs[1].offsetUnits, (nyAnchor - ldnAnchor) * MS);
  assert.equal(l.segs[1].startUnits % B, 0);
  assert.equal(l.segs[1].firstIdx, 3);
  assert.equal(segmentAtTime(l.segs, 10 * B), 0);
  assert.equal(segmentAtTime(l.segs, 40 * B), -1, "Pause gehört zu keiner Session");
  assert.equal(segmentAtTime(l.segs, l.segs[1].startUnits), 1);
  assert.equal(segmentOfIndex(l.segs, 2), 0);
  assert.equal(segmentOfIndex(l.segs, 3), 1);
});

test("Überlappende oder vertauschte Sessions werden abgelehnt (keine doppelten Marktereignisse)", () => {
  const overlap = { ...ny, anchorUnixMs: ldnAnchor + 30 * 5 * 60_000 }; // beginnt 10:30 UTC, London endet 11:00
  assert.throws(() => layoutSegments([london, overlap]), /überlappen/);
  assert.throws(() => layoutSegments([ny, london]), /überlappen/);
});

test("Pause: zwischen Sessionende und erstem echten Tick der nächsten Session", () => {
  const l = layoutSegments([london, ny]);
  const firstNy = l.segs[1].startUnits + 5 * MS; // erster NY-Tick 5 ms nach 14:30
  const ft = (i) => (i === 1 ? firstNy : 0);
  assert.equal(pauseAt(l.segs, ft, 35 * B), null, "noch in London");
  assert.deepEqual(pauseAt(l.segs, ft, 36 * B), { from: 0, to: 1 });
  assert.deepEqual(pauseAt(l.segs, ft, firstNy - 1), { from: 0, to: 1 });
  assert.equal(pauseAt(l.segs, ft, firstNy), null, "mit dem ersten NY-Tick ist die Pause vorbei");
});

test("Übergangstext mit tatsächlicher Dauer", () => {
  assert.equal(humanDuration((2 * 3600 + 47 * 60 + 12) * 1000), "2 h 47 min");
  assert.equal(humanDuration(65_000), "1 min 5 s");
  assert.equal(transitionText("London", "New York", (3 * 3600 + 30 * 60) * 1000), "London beendet · 3 h 30 min übersprungen · New York beginnt");
});

test("Slots: Kerzen je Session + genau ein Pausen-Slot; Session-CVD startet neu", () => {
  const l = layoutSegments([london, ny]);
  const t = slotTemplates(l.segs, B, (u, k) => `${k}:${u / B}`, (k) => `Pause vor ${k}`);
  assert.equal(t.filter((x) => x.kind === "pause").length, 1);
  assert.equal(t.filter((x) => x.kind === "bucket" && x.seg === 0).length, 36);
  assert.equal(t.filter((x) => x.kind === "bucket" && x.seg === 1).length, 30);
  assert.equal(t[36].kind, "pause");
  assert.equal(t[37].bucket, l.segs[1].startUnits / B);
  const dc = sessionDeltaCvd([
    { seg: 0, candle: { buyVolume: 5, sellVolume: 1 } }, { seg: 0, candle: { buyVolume: 2, sellVolume: 0 } },
    { seg: 1, candle: null }, { seg: 1, candle: { buyVolume: 0, sellVolume: 3 } },
  ]);
  assert.deepEqual(dc.delta, [4, 2, null, -3]);
  assert.deepEqual(dc.cvd, [4, 6, null, -3], "CVD in New York beginnt bei 0");
});

test("Gemeinsamer Speicher: Ticks beider Sessions chronologisch, Engine zeigt bis zum Sessionende keine NY-Kerze", () => {
  const l = layoutSegments([london, ny]);
  const st = createStore(5);
  const chunk = (t, p, v, s) => ({ n: t.length, t: Float64Array.from(t), price: Float64Array.from(p), vol: Int32Array.from(v), side: Int8Array.from(s) });
  appendChunk(st, 0, chunk([0, B + 1, 35 * B + 7], [100, 101, 102], [1, 2, 3], [1, 1, -1]));
  const off = l.segs[1].offsetUnits;
  appendChunk(st, 3, chunk([off + 5 * MS, off + B], [200, 201], [4, 1], [-1, 1]));
  const eng = new TickReplayEngine(st, B);
  eng.advanceTo(countAtOrBefore(st, l.segs[0].endUnits));
  assert.equal(eng.p, 3, "am Londoner Sessionende: alle London-Ticks, kein NY-Tick");
  assert.ok(eng.candles().every((c) => c.lastIdx < 3));
  eng.advanceTo(countAtOrBefore(st, st.t[3]));
  assert.equal(eng.p, 4, "Sprung auf den ersten echten NY-Tick");
  assert.equal(vwap(st, 3, 4), 200);
  assert.equal(vwap(st, 0, 3), (100 * 1 + 101 * 2 + 102 * 3) / 6);
});
