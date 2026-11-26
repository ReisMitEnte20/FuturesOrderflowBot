// Marktdaten: Herkunftsklassen und Wertzustände (echte Null ≠ noch kein Wert ≠ Feed getrennt ≠ Quelle ungeklärt).
import { test } from "node:test";
import assert from "node:assert/strict";
import { valueText, provClass, candlesToSlots, slotDeltaCvd, PROV_CLASS } from "../src/lib/marketdataApi.ts";

test("Wertzustände: echte Null bleibt 0, fehlende Werte werden benannt statt 0", () => {
  assert.equal(valueText({ value: 0, state: "wert", source: "derived" }), "0");
  assert.equal(valueText({ value: null, state: "noch_kein_wert", source: "live_atas" }), "noch kein Wert");
  assert.equal(valueText({ value: 21000.25, state: "feed_getrennt", source: "live_atas" }), "21.000,25 (Feed getrennt)");
  assert.equal(valueText({ value: 5, state: "veraltet", source: "live_atas" }), "5 (veraltet)");
  assert.equal(valueText({ value: null, state: "quelle_ungeklaert", source: "unavailable" }), "Quelle ungeklärt");
  assert.equal(valueText(null), "Quelle ungeklärt");
});

test("Herkunft: vier Klassen + Testdaten, nie vermischt", () => {
  assert.equal(provClass("live_atas"), "live");
  assert.equal(provClass("atas_chart"), "live");
  assert.equal(provClass("derived"), "computed");
  assert.equal(provClass("test"), "test");
  assert.equal(provClass("Minutenaggregat (Ablauf innerhalb der Minute generiert)"), "history");
  assert.equal(provClass("generiert"), "computed");
  assert.equal(provClass("ungeklärt"), "unavailable");
  assert.equal(provClass(undefined), "unavailable");
  assert.equal(new Set(Object.values(PROV_CLASS).map((p) => p.label)).size, 5);
});

test("Live-Kerzen werden zu Chart-Slots; laufende Kerze bleibt als laufend markiert; CVD je Slot", () => {
  const c = (t, o, cl, b, s, closed) => ({ tStart: t, tEnd: t, open: o, high: Math.max(o, cl), low: Math.min(o, cl), close: cl, volume: b + s, buyVolume: b, sellVolume: s, delta: b - s, ticks: 3, vwap: null, closed, footprint: [], source: "derived" });
  const candles = [c("2026-11-25T14:30:00Z", 1, 2, 5, 2, true), c("2026-11-25T14:31:00Z", 2, 1, 1, 4, false)];
  const slots = candlesToSlots(candles, "America/New_York");
  assert.equal(slots[0].label, "09:30");
  assert.deepEqual(slots.map((x) => x.status), ["abgeschlossen", "laufend"]);
  assert.deepEqual(slotDeltaCvd(candles), { delta: [3, -3], cvd: [3, 0] });
});
