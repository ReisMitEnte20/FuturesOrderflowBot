// Tests der Startbildschirm-Logik (node --test): Startentscheidung aus dem Vorschauzustand, gruppierte Prüfliste,
// Holdout-Zählung, Kurzfakten. Kleine synthetische Vorschau-Objekte — keine Marktdaten.
import { test } from "node:test";
import assert from "node:assert/strict";
import { startDecision, groupChecks, checkCounts, plannedText, previewFacts, paramSummary, testedPeriod } from "../src/lib/backtestingStart.ts";

const checks = (over = {}) => [
  { key: "data", label: "01 Datenqualität & zeitliche Korrektheit", status: "possible" },
  { key: "result", label: "02 Basis-Backtest & Ergebnisrechnung", status: "possible" },
  { key: "risk", label: "04 Sharpe/Sortino/Calmar", status: over.risk ?? "possible", reason: over.risk === "insufficient" ? "20 Handelstage benötigt, 12 vorhanden" : null },
  { key: "oos-split", label: "06 Walk-forward-Testdaten: fester Train/Test-Split", status: "possible" },
  { key: "oos-rolling", label: "06 Walk-forward-Testdaten: rollierend", status: over.rolling ?? "possible", reason: over.rolling ? "25 Sessions benötigt, 12 vorhanden." : null },
  { key: "oos-anchored", label: "06 Walk-forward-Testdaten: anwachsend", status: over.rolling ?? "possible" },
  { key: "trades", label: "12 Einzeltrade-Prüfung", status: "possible" },
  { key: "holdout", label: "Finaler Holdout – unbenutzt", status: "reserved", reason: "6 Session(s) reserviert" },
];

const preview = (over = {}) => ({
  ok: true, source: "Sierra", dataKind: "OHLC aus Trade-Ticks (Last), keine Quotes", symbol: "MES", timeframeMinutes: 5,
  from: "2026-05-01T00:00:00Z", to: "2026-06-12T00:00:00Z", bars: 8232, sessions: 31, developmentSessions: 25, holdoutSessions: 6,
  holdoutFrom: "2026-06-04T00:00:00Z", holdoutFraction: 0.2, feeIsExample: true,
  costs: { feePerSide: 0.39, feeRoundTrip: 0.78, slippageTicks: 1, tickSize: 0.25, tickValue: 1.25 },
  candidates: 9, sensitivityParameters: [], estimatedEngineRuns: 350, checks: checks(over), elapsedMs: 1,
  requirements: [{ requirement: "OHLC-Zeitkerzen", met: true, detail: "Datensatz liefert Zeitkerzen." }], canStart: true, engine: "ohlc",
  signalData: "", executionData: "", ...over.preview,
});

test("startbereit: alle Analysen geplant, Fakten wie im Startbildschirm", () => {
  const p = preview();
  const d = startDecision(p);
  assert.equal(d.kind, "ready");
  assert.equal(d.title, "Startbereit");
  assert.deepEqual(previewFacts(p), [
    "MES · 5-Minuten-Kerzen · aus gehandelten Preisen, ohne Quotes",
    "31 Handelstage · 25 Entwicklung · 6 für den finalen Holdout reserviert",
    "Beispielkosten: 0,39 je Seite · 1 Tick Slippage",
  ]);
});

test("nur teilweise auswertbar: zu wenig Historie kennzeichnet NUR die betroffenen Analysen", () => {
  const d = startDecision(preview({ risk: "insufficient", rolling: "insufficient" }));
  assert.equal(d.kind, "limited");
  assert.equal(d.title, "Mit Einschränkungen startbereit");
  assert.deepEqual(d.limited.map((l) => l.label), ["04 Sharpe/Sortino/Calmar", "06 Walk-forward-Testdaten (3 Varianten)"]);
  assert.match(d.limited[1].detail, /1 von 3 Varianten auswertbar/);
  assert.equal(d.missing.length, 0);
});

test("Start gesperrt: fehlende Pflichtdaten werden direkt genannt", () => {
  const d = startDecision(preview({ preview: {
    canStart: false, checks: [],
    requirements: [{ requirement: "Trade-Ticks mit Aggressor", met: false, detail: "Gewählter Datensatz liefert nur OHLC-Kerzen." }],
  } }));
  assert.equal(d.kind, "blocked");
  assert.equal(d.title, "Start nicht möglich");
  assert.deepEqual(d.missing, [{ label: "Trade-Ticks mit Aggressor", detail: "Gewählter Datensatz liefert nur OHLC-Kerzen." }]);
  assert.equal(startDecision({ ok: false, error: "Datei fehlt" }).kind, "blocked");
  assert.equal(startDecision(null).kind, "loading");
});

test("Prüfliste: drei Walk-forward-Varianten unter einem Punkt, Holdout zählt nie als Prüfung", () => {
  const g = groupChecks(checks());
  assert.equal(g.filter((x) => x.key.startsWith("oos")).length, 1);
  assert.equal(g.find((x) => x.key === "oos").items.length, 3);
  const c = checkCounts(checks());
  assert.equal(c.total, 5);            // data, result, risk, oos (gruppiert), trades — ohne Holdout
  assert.equal(c.planned, 5);
  assert.equal(c.holdoutReserved, true);
  assert.equal(plannedText("possible"), "geplant");
  assert.equal(plannedText("reserved"), "reserviert, unbenutzt");
});

test("Kurzfassung der Parameter und getesteter Zeitraum", () => {
  const sma = [{ key: "FastPeriod", label: "Schneller SMA (Bars)", default: "9" }, { key: "SlowPeriod", label: "Langsamer SMA (Bars)", default: "21" }];
  assert.equal(paramSummary(sma, { FastPeriod: "9", SlowPeriod: "21" }), "SMA 9/21");
  assert.equal(paramSummary([{ key: "DeltaThreshold", label: "Delta-Schwelle", default: "30" }, { key: "TicksPerBar", label: "Ticks je Bar", default: "500" }], {}),
    "Delta-Schwelle 30 · Ticks je Bar 500");
  assert.equal(testedPeriod("2026-05-01T00:00:00Z", "2026-06-12T00:00:00Z"), "01.05.2026 – 11.06.2026");
  assert.equal(testedPeriod(null, null, "2026-06-01T13:30:00Z", "2026-06-16T13:33:19Z"), "01.06.2026 – 16.06.2026");
  assert.equal(testedPeriod(null, null), "ganzer Datensatz");
});
