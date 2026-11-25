// Tests des Trade-Nachvollzugs im Tick-Replay (node --test). Kleine synthetische Daten – KEINE Abnahme an echten
// Trades; die echte Abnahme laeuft im Browser gegen den MNQ-Lauf.
import { test } from "node:test";
import assert from "node:assert/strict";
import { createStore, appendChunk, decodeChunk, ROW_BYTES, TickReplayEngine, BUCKET_5M } from "../src/lib/tickReplay.ts";
import { isoToUnits, tradeEvents, eventReached, pauseTarget, tradeMath, basisCheck, runReplayTarget, runCosts, UNITS_PER_MS } from "../src/lib/tradeReplay.ts";

const ANCHOR = Date.parse("2026-10-26T13:30:00Z");

function storeOf(rows) {
  const n = rows.length, buf = new ArrayBuffer(n * ROW_BYTES);
  const t = new Float64Array(buf, 0, n), p = new Float64Array(buf, 8 * n, n), v = new Int32Array(buf, 16 * n, n), s = new Int8Array(buf, 20 * n, n);
  rows.forEach((r, i) => { t[i] = r[0]; p[i] = r[1]; v[i] = r[2] ?? 1; s[i] = r[3] ?? 1; });
  const st = createStore(n); if (n) appendChunk(st, 0, decodeChunk(buf)); return st;
}

const baseTrade = {
  index: 0, side: "Long", qty: 1, entryBarIndex: 1, exitBarIndex: 2, entryPrice: 29246.0, exitPrice: 29222.25, exitReason: "",
  stopLossPrice: 0, takeProfitPrice: 0, ambiguous: false, netPnL: -48.28,
  entryTimeMs: Date.parse("2026-10-26T13:30:02.076Z"), exitTimeMs: Date.parse("2026-10-26T13:30:04.152Z"), entryTime: "13:30:02", exitTime: "13:30:04",
};
const costs = { tickSize: 0.25, pointValue: 2, feePerSide: 0.39, slippageTicks: 1, source: "test" };

test("isoToUnits rechnet exakt in 100 ns (7 Stellen, 9 Stellen abgeschnitten, ohne Bruchteil)", () => {
  assert.equal(isoToUnits("2026-10-26T13:30:02.0761234Z", ANCHOR), 2 * 10_000_000 + 761234);
  assert.equal(isoToUnits("2026-10-26T13:30:02.076123499Z", ANCHOR), 2 * 10_000_000 + 761234);
  assert.equal(isoToUnits("2026-10-26T13:30:00Z", ANCHOR), 0);
  assert.equal(isoToUnits("kaputt", ANCHOR), null);
  assert.equal(isoToUnits(null, ANCHOR), null);
});

test("alte Laufdatei: KEIN Signalereignis (nichts geschaetzt), Entry/Exit nur ms-genau und konservativ", () => {
  const ev = tradeEvents(baseTrade, ANCHOR, false);
  assert.deepEqual(ev.map((e) => e.kind), ["entry", "exit"]);
  assert.equal(ev[0].precision, "ms");
  assert.equal(ev[0].unitsUpper - ev[0].units, UNITS_PER_MS - 1);
});

test("exakter Tick-Index: Marker erst NACH dem Fill-Tick; Zurueckspulen blendet ihn wieder aus", () => {
  const t = 2 * 10_000_000;
  // Drei Ticks mit GLEICHEM Zeitstempel: Signal-Tick (0), Fill-Tick (1), danach (2) – Reihenfolge aus dem Index.
  const st = storeOf([[t, 100], [t, 100.25], [t, 100.5], [t + 5, 101]]);
  const trade = { ...baseTrade, entryTickIndex: 1, exitTickIndex: 3, entryTimeUtc: "2026-10-26T13:30:02.0000000Z", exitTimeUtc: "2026-10-26T13:30:02.0000005Z",
    entrySignal: { barIndex: 0, tickIndex: 0, timeUtc: "2026-10-26T13:30:02.0000000Z", direction: "Long", referencePrice: 100, reason: "", positionBefore: "Flat" } };
  const ev = tradeEvents(trade, ANCHOR, true);
  const [sig, entry, exit] = [ev.find((e) => e.kind === "signal"), ev.find((e) => e.kind === "entry"), ev.find((e) => e.kind === "exit")];
  const reached = (e, p) => eventReached(e, st, p);
  assert.deepEqual([0, 1, 2, 3, 4].map((p) => [reached(sig, p), reached(entry, p), reached(exit, p)]), [
    [false, false, false], [true, false, false], [true, true, false], [true, true, false], [true, true, true],
  ]);
  // Ruecksprung: derselbe Zustand wie beim ersten Erreichen dieser Position (Funktion der Position, kein Zustand).
  assert.equal(reached(exit, 2), false);
  assert.equal(reached(entry, 1), false);
});

test("ms-Genauigkeit: Ereignis gilt erst als eingetreten, wenn ALLE Ticks dieser Millisekunde abgespielt sind", () => {
  const msU = 2076 * UNITS_PER_MS; // 13:30:02.076
  const st = storeOf([[msU - 1, 1], [msU, 2], [msU + 3000, 3], [msU + 9999, 4], [msU + UNITS_PER_MS, 5]]);
  const [entry] = tradeEvents(baseTrade, ANCHOR, false);
  assert.deepEqual([1, 2, 3, 4, 5].map((p) => eventReached(entry, st, p)), [false, false, false, true, true]);
});

test("nicht vollstaendig geladene Daten: Ereignis gilt NIE als eingetreten (kein Vorgriff)", () => {
  const msU = 2076 * UNITS_PER_MS;
  const st = storeOf([[msU, 1], [msU + 1, 2]]);
  st.total = 10; // weitere Ticks derselben Millisekunde koennten noch folgen
  const [entry] = tradeEvents(baseTrade, ANCHOR, false);
  assert.equal(eventReached(entry, st, 2), false);
});

test("Pause liegt vor dem Signal; ohne Signal vor dem Entry", () => {
  const trade = { ...baseTrade, entrySignal: { barIndex: 0, tickIndex: 9, timeUtc: "2026-10-26T13:30:20.0000000Z", direction: "Long", referencePrice: 1, reason: "", positionBefore: "Flat" } };
  const p = pauseTarget(tradeEvents(trade, ANCHOR, true), 0);
  assert.equal(p.anchor, "signal");
  assert.equal(p.units, 5 * 10_000_000);
  const q = pauseTarget(tradeEvents(baseTrade, ANCHOR, false), 0);
  assert.equal(q.anchor, "entry");
  assert.equal(q.units, 0); // an den Fensterstart begrenzt
});

test("Ergebnis Long: Brutto aus Fills − Gebuehren = Netto; Slippage steckt im Fill und wird NICHT doppelt abgezogen", () => {
  const m = tradeMath({ ...baseTrade, grossPnL: -47.5, fees: 0.78, entryTickPrice: 29245.75, exitTickPrice: 29222.5 }, costs);
  assert.equal(m.grossFromFills, -47.5);
  assert.equal(m.fees, 0.78);
  assert.equal(m.netComputed, -48.28);
  assert.equal(m.netMatches, true);
  assert.deepEqual(m.slippage, { entryTicks: 1, exitTicks: 1, money: 1, configTicksPerSide: 1 });
  assert.equal(m.netComputed, m.grossFromFills - m.fees); // keine zusaetzliche Slippage-Abbuchung
});

test("Ergebnis Short + alte Datei ohne Tickpreise/Gebuehren je Trade: Gebuehren aus Lauf-Profil, Slippage-Aufschluesselung fehlt", () => {
  const t = { ...baseTrade, side: "Short", entryPrice: 29206.5, exitPrice: 29232.5, netPnL: -52.78 };
  const m = tradeMath(t, costs);
  assert.equal(m.grossFromFills, -52);
  assert.equal(m.fees, 0.78);
  assert.match(m.feesSource, /berechnet/);
  assert.equal(m.netMatches, true);
  assert.equal(m.slippage, null);
  assert.ok(m.missing.some((x) => /Tickpreis/.test(x)));
});

test("fehlender Punktwert/Gebuehren werden als fehlend gekennzeichnet, nicht ersetzt", () => {
  const m = tradeMath(baseTrade, { tickSize: null, pointValue: null, feePerSide: null, slippageTicks: null, source: "x" });
  assert.equal(m.grossFromFills, null);
  assert.equal(m.netComputed, null);
  assert.equal(m.netMatches, null);
  assert.deepEqual(m.missing.slice(0, 2), ["Punktwert", "Gebühren"]);
});

test("Datenbasis: Tick-Indizes nur bei gleichem CSV-Hash, gleicher Tickzahl und gleichem Job", () => {
  const f = { basis: { csvSha256: "ABC", ticks: 10, jobId: "J" } };
  assert.equal(basisCheck(f, { csvSha256: "abc", count: 10, jobId: "J" }).exact, true);
  assert.equal(basisCheck(f, { csvSha256: "abd", count: 10, jobId: "J" }).exact, false);
  assert.equal(basisCheck(f, { csvSha256: "abc", count: 11, jobId: "J" }).exact, false);
  assert.equal(basisCheck(f, { csvSha256: "abc", count: 10, jobId: "K" }).exact, false);
});

test("Replay-Tag und Kosten kommen aus dem Lauf (Provenienz)", () => {
  const d = { meta: { pointValue: 2, feePerSide: 0.39, slippageTicks: 1, ticksPerBar: 500 },
    provenance: { dataSha256: "x", ticks: 1, settingsUsed: { "collection.jobId": "NY_1", "window.session": "2026-10-26 09:30–12:00 America/New_York" },
      costs: { tickSize: 0.25, pointValue: 2, feePerSideTotal: 0.39, slippageTicks: 1 } } };
  assert.deepEqual(runReplayTarget(d), { jobId: "NY_1", day: "2026-10-26", why: null });
  assert.equal(runCosts(d).tickSize, 0.25);
  assert.equal(runReplayTarget({ meta: {}, provenance: { settingsUsed: {} } }).jobId, null);
});

test("5-Minuten-Kerze nimmt kein spaeteres Hoch/Tief vorweg; Kerzenwechsel genau an der Grenze", () => {
  const st = storeOf([[BUCKET_5M - 2, 100], [BUCKET_5M - 1, 105], [BUCKET_5M, 99], [BUCKET_5M + 1, 110]]);
  const eng = new TickReplayEngine(st, BUCKET_5M);
  eng.advanceTo(1);
  assert.equal(eng.cur.high, 100);           // 105 kommt erst spaeter
  eng.advanceTo(3);
  assert.equal(eng.closed.length, 1);        // neue Kerze ab der 5-Minuten-Grenze
  assert.equal(eng.closed[0].high, 105);
  assert.equal(eng.cur.open, 99);
  assert.equal(eng.cur.high, 99);            // 110 noch nicht abgespielt
  eng.seek(1);                               // Zurueckspulen
  assert.equal(eng.closed.length, 0);
  assert.equal(eng.cur.high, 100);
});
