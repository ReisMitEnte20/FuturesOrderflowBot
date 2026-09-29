// Tests der Darstellungsschicht für Quant-Kennzahlen (node --test, ohne zusätzliche Abhängigkeiten).
//
// Kernanforderung: Ein nicht berechenbarer Wert darf NIE als Zahl (und schon gar nicht als 0)
// erscheinen. Er wird ausdrücklich als „n. b." dargestellt.
import { test } from "node:test";
import assert from "node:assert/strict";
import { formatMetric, formatByUnit, formatPercent, formatNumber, formatDate } from "../src/lib/quantApi.ts";

const metric = (o) => ({
  key: "k", label: "L", value: null, unit: "ratio",
  method: "m", inputs: "i", limitation: null, sampleSize: 0, unavailableReason: null, ...o,
});

test("nicht berechenbare Kennzahlen erscheinen als 'n. b.', nie als 0", () => {
  assert.equal(formatMetric(metric({ value: null })), "n. b.");
  assert.equal(formatMetric(metric({ value: undefined })), "n. b.");
  assert.equal(formatMetric(metric({ value: Number.NaN })), "n. b.");
  assert.equal(formatMetric(metric({ value: Number.POSITIVE_INFINITY })), "n. b.");
  // Eine echte Null bleibt eine Null — sie ist ein gültiger Messwert.
  assert.equal(formatMetric(metric({ value: 0, unit: "ratio" })), "0.000");
});

test("Einheiten werden korrekt formatiert", () => {
  assert.equal(formatByUnit(0.1234, "fraction"), "12.34 %");
  assert.equal(formatByUnit(0.0512, "fraction/yr"), "5.12 %");
  assert.equal(formatByUnit(12.5, "days"), "12.5 T");
  assert.equal(formatByUnit(42, "count"), "42");
  assert.equal(formatByUnit(1.23456, "ratio"), "1.235");
});

test("formatPercent und formatNumber melden fehlende Werte statt zu raten", () => {
  assert.equal(formatPercent(null), "n. b.");
  assert.equal(formatPercent(undefined), "n. b.");
  assert.equal(formatPercent(Number.NaN), "n. b.");
  assert.equal(formatPercent(0.5, 1), "50.0 %");

  assert.equal(formatNumber(null), "n. b.");
  assert.equal(formatNumber(1 / 3, 4), "0.3333");
});

test("Zeitstempel werden als UTC ausgegeben, fehlende als Strich", () => {
  assert.equal(formatDate(null), "—");
  assert.equal(formatDate(Date.UTC(2026, 0, 2, 13, 30)), "2026-01-02 13:30 UTC");
});
