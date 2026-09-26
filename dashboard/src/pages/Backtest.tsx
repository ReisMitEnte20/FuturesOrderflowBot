import { useEffect, useMemo, useRef, useState } from "react";
import {
  backtestApi, type StrategyDef, type InstrumentDef, type DataSourceDef,
  type RunResponse, type RunRequest, type Candle, type ImportIssue, type OhlcTrade, type EquityPoint,
} from "@/lib/backtestApi";
import { chartTradesAt, replaySnapshot, relativeTradeIndex, clampPos } from "@/lib/replay";
import { BacktestChart, type ChartStatus } from "@/components/backtest/BacktestChart";
import { EquityDrawdownChart } from "@/components/backtest/EquityDrawdownChart";

// ---- Formatierung ----
const money = (v: number | null | undefined) => (v == null ? "–" : `${v < 0 ? "-" : ""}$${Math.abs(v).toFixed(2)}`);
const pct = (v: number | null | undefined) => (v == null ? "–" : `${(v * 100).toFixed(1)}%`);
const num = (v: number | null | undefined, d = 2) => (v == null ? "–" : v.toFixed(d));
const col = (v: number) => (v > 0 ? "text-[var(--key)]" : v < 0 ? "text-[var(--red)]" : "text-[var(--fg)]");
const isoShort = (s?: string | null) => (s ? s.slice(0, 16).replace("T", " ") : "–");
const tShort = (s: string) => s.slice(5, 16).replace("T", " ");
const msShort = (ms: number) => new Date(ms).toISOString().slice(5, 16).replace("T", " ");
const toInput = (iso: string) => iso.slice(0, 16).replace("T", " ");

// Stabile Leerwerte (verhindern unnötiges Neuzeichnen des Charts).
const NO_CANDLES: Candle[] = [];
const NO_TRADES: OhlcTrade[] = [];
const NO_EQUITY: EquityPoint[] = [];

const SPEEDS = [1, 2, 5, 10, 25];   // Kerzen je Takt
const TICK_MS = 200;                // Takt → x1 = 5 Kerzen/s
const FOLLOW_BARS = 90;             // mitlaufendes Fenster im Replay

type Mode = "overview" | "replay";
type Busy = null | "load" | "run";
type BottomTab = "equity" | "stats" | "costs";

interface Toolbar { dataSourceId: string; symbol: string; timeframeMinutes: number; fromUtc: string; toUtc: string; }
interface Settings {
  maxRows: number; strategy: string; params: Record<string, string>;
  quantity: number; initialBalance: number;
  stopLossTicks: string; takeProfitTicks: string; slippageTicks: string; feePerSide: string;
  applyFees: boolean; excludePartialEdges: boolean;
}
/** Anzeige-Metadaten der aktuell dargestellten Kerzen (aus „Daten laden" oder dem Backtest). */
interface DataView {
  source: string; symbol: string; tf: number; tz: string; from?: string | null; to?: string | null;
  barCount: number; evaluated?: number; lead: boolean; trail: boolean; partialExcluded: boolean; elapsedMs?: number;
}
interface Loaded { candles: Candle[]; issues: ImportIssue[]; view: DataView; }

export function Backtest() {
  const [strategies, setStrategies] = useState<StrategyDef[]>([]);
  const [instruments, setInstruments] = useState<InstrumentDef[]>([]);
  const [dataSources, setDataSources] = useState<DataSourceDef[]>([]);
  const [loadErr, setLoadErr] = useState<string | null>(null);

  const [tb, setTb] = useState<Toolbar>({ dataSourceId: "", symbol: "MES", timeframeMinutes: 5, fromUtc: "", toUtc: "" });
  const [st, setSt] = useState<Settings>({
    maxRows: 1_500_000, strategy: "movingaverage", params: { FastPeriod: "9", SlowPeriod: "21" },
    quantity: 1, initialBalance: 10000, stopLossTicks: "", takeProfitTicks: "", slippageTicks: "1", feePerSide: "",
    applyFees: true, excludePartialEdges: true,
  });
  const [settingsOpen, setSettingsOpen] = useState(false);

  const [data, setData] = useState<Loaded | null>(null);
  const [result, setResult] = useState<RunResponse | null>(null);
  const [busy, setBusy] = useState<Busy>(null);
  const [busySince, setBusySince] = useState(0);
  const [now, setNow] = useState(Date.now());
  const [err, setErr] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const [mode, setMode] = useState<Mode>("overview");
  const [pos, setPos] = useState(0);
  const [playing, setPlaying] = useState(false);
  const [speed, setSpeed] = useState(1);

  const [selected, setSelected] = useState<number | null>(null);
  const [focusKey, setFocusKey] = useState(0);
  const [bottomTab, setBottomTab] = useState<BottomTab>("equity");

  const abortRef = useRef<AbortController | null>(null);
  const chartPanelRef = useRef<HTMLDivElement | null>(null);
  const journalRef = useRef<HTMLDivElement | null>(null);
  const rowRefs = useRef<Record<number, HTMLTableRowElement | null>>({});

  const candles = data?.candles ?? NO_CANDLES;
  const res = result?.ok ? result.result ?? null : null;
  const trades = res?.trades ?? NO_TRADES;
  const equity = res?.equity ?? NO_EQUITY;
  const stats = res?.statistics;
  const cost = result?.ok ? result.costProfile : null;
  const isReplay = mode === "replay";
  const n = candles.length;

  // Refs für Tastatur-/Timer-Handler (immer aktueller Stand).
  const posRef = useRef(pos); posRef.current = pos;
  const nRef = useRef(n); nRef.current = n;

  function buildRequest(t: Toolbar = tb, s: Settings = st): RunRequest {
    return {
      dataSourceId: t.dataSourceId, symbol: t.symbol, timeframeMinutes: t.timeframeMinutes,
      fromUtc: t.fromUtc.trim() || null, toUtc: t.toUtc.trim() || null, maxRows: s.maxRows,
      strategy: s.strategy, params: s.params, quantity: s.quantity, initialBalance: s.initialBalance,
      stopLossTicks: s.stopLossTicks ? parseInt(s.stopLossTicks) : null,
      takeProfitTicks: s.takeProfitTicks ? parseInt(s.takeProfitTicks) : null,
      slippageTicks: s.slippageTicks ? parseFloat(s.slippageTicks) : null,
      feePerSideOverride: s.feePerSide ? parseFloat(s.feePerSide) : null,
      applyFees: s.applyFees, excludePartialEdges: s.excludePartialEdges,
    };
  }

  function startBusy(kind: Busy): AbortController {
    abortRef.current?.abort();
    const ac = new AbortController();
    abortRef.current = ac;
    setBusy(kind); setBusySince(Date.now()); setErr(null); setNotice(null);
    setPlaying(false); setSelected(null);
    return ac;
  }
  function endBusy(ac: AbortController) {
    if (abortRef.current === ac) { abortRef.current = null; setBusy(null); }
  }
  function cancelBusy() {
    abortRef.current?.abort();
    abortRef.current = null;
    setBusy(null);
    setNotice("Vorgang abgebrochen.");
  }

  // „Daten laden": nur echte OHLC-Kerzen, kein Strategielauf. Ein altes Ergebnis wird verworfen.
  async function loadData(t: Toolbar = tb, s: Settings = st) {
    const ac = startBusy("load");
    setData(null); setResult(null); rowRefs.current = {};
    try {
      const r = await backtestApi.candles(buildRequest(t, s), ac.signal);
      if (ac.signal.aborted) return;
      if (!r.ok || !r.data) { setErr(r.error ?? "Laden fehlgeschlagen."); return; }
      setData({
        candles: r.candles, issues: r.dataIssues,
        view: {
          source: r.data.source, symbol: r.data.symbol, tf: r.data.timeframeMinutes, tz: r.data.timezone,
          from: r.data.from, to: r.data.to, barCount: r.data.barCount,
          lead: r.data.leadingPartial, trail: r.data.trailingPartial, partialExcluded: false, elapsedMs: r.elapsedMs,
        },
      });
      setPos(0);
    } catch (e: any) {
      if (e?.name !== "AbortError") setErr(`Laden fehlgeschlagen: ${e?.message ?? e}`);
    } finally {
      endBusy(ac);
    }
  }

  // „Backtest starten": Engine im Backend; die Kerzen des Laufs ersetzen die Anzeige (Bar-Indizes passen).
  async function runBacktest() {
    const ac = startBusy("run");
    try {
      const r = await backtestApi.run(buildRequest(), ac.signal);
      if (ac.signal.aborted) return;
      if (!r.ok || !r.result) { setErr(r.error ?? "Backtest fehlgeschlagen."); return; }
      const d = r.result.data;
      rowRefs.current = {};
      setData({
        candles: r.candles, issues: r.dataIssues,
        view: {
          source: d.source, symbol: d.symbol, tf: d.timeframeMinutes, tz: d.timezone, from: d.from, to: d.to,
          barCount: d.barCount, evaluated: d.evaluatedBars,
          lead: d.leadingPartialExcluded, trail: d.trailingPartialExcluded, partialExcluded: true,
        },
      });
      setResult(r);
      setMode("overview");
      setPos(0);
    } catch (e: any) {
      if (e?.name !== "AbortError") setErr(`Backtest fehlgeschlagen: ${e?.message ?? e}`);
    } finally {
      endBusy(ac);
    }
  }

  // Beim ersten Öffnen: Listen laden und eine verfügbare lokale Quelle mit begrenztem Ausschnitt laden.
  async function init() {
    setLoadErr(null);
    try {
      const [s, i, d] = await Promise.all([backtestApi.strategies(), backtestApi.instruments(), backtestApi.dataSources()]);
      setStrategies(s); setInstruments(i); setDataSources(d);
      const src = d.find((x) => x.available);
      if (!src) return;
      const sym = i.some((x) => x.symbol === "MES") ? "MES" : i[0]?.symbol ?? "MES";
      const t2: Toolbar = { ...tb, dataSourceId: src.id, symbol: sym, fromUtc: src.defaultFromUtc ? toInput(src.defaultFromUtc) : "", toUtc: "" };
      const s2: Settings = { ...st, maxRows: src.defaultMaxRows ?? st.maxRows };
      setTb(t2); setSt(s2);
      void loadData(t2, s2);
    } catch (e: any) {
      setLoadErr(`Backend nicht erreichbar (${backtestApi.base}). Läuft das .NET-DevDashboard? ${e?.message ?? ""}`);
    }
  }
  useEffect(() => { void init(); return () => abortRef.current?.abort(); }, []);

  // Laufzeit-Anzeige während Laden/Backtest.
  useEffect(() => {
    if (!busy) return;
    const id = setInterval(() => setNow(Date.now()), 200);
    return () => clearInterval(id);
  }, [busy]);

  // ---- Bar-Replay: je Takt kommen `speed` vollständige Kerzen hinzu (keine Intrabar-Bewegung). ----
  useEffect(() => {
    if (!playing || !isReplay || n === 0) return;
    const id = setInterval(() => setPos((p) => Math.min(nRef.current - 1, p + speed)), TICK_MS);
    return () => clearInterval(id);
  }, [playing, isReplay, speed, n]);
  useEffect(() => { if (playing && pos >= n - 1) setPlaying(false); }, [playing, pos, n]);

  function togglePlay() {
    if (n === 0) return;
    if (!playing && posRef.current >= nRef.current - 1) setPos(0);
    setPlaying((p) => !p);
  }
  function step(d: number) { setPlaying(false); setPos((p) => clampPos(p + d, nRef.current)); }
  function resetReplay() { setPlaying(false); setPos(0); }

  // Tastatur im Replay: Leertaste = Play/Pause, ←/→ = eine Kerze.
  const keyRef = useRef({ togglePlay, step });
  keyRef.current = { togglePlay, step };
  useEffect(() => {
    if (!isReplay) return;
    const onKey = (e: KeyboardEvent) => {
      const tag = (e.target as HTMLElement | null)?.tagName;
      if (tag === "INPUT" || tag === "SELECT" || tag === "TEXTAREA" || tag === "BUTTON") return;
      if (e.key === " ") { e.preventDefault(); keyRef.current.togglePlay(); }
      else if (e.key === "ArrowRight") { e.preventDefault(); keyRef.current.step(1); }
      else if (e.key === "ArrowLeft") { e.preventDefault(); keyRef.current.step(-1); }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [isReplay]);

  function switchMode(m: Mode) {
    if (m === mode) return;
    setPlaying(false); setNotice(null);
    if (m === "replay") { setSelected(null); setPos((p) => clampPos(p, n)); }
    setMode(m);
  }

  // Trade-Auswahl: springt in der Übersicht zu Entry/Exit. Aus dem Replay ausdrücklich in die Übersicht wechseln.
  function selectTrade(index: number) {
    if (index < 0 || index >= trades.length) return;
    if (isReplay) {
      setPlaying(false);
      setMode("overview");
      setNotice(`Aus dem Replay zur Ergebnisübersicht gewechselt (Trade #${index + 1}) — die Übersicht zeigt den vollständigen Zeitraum inkl. späterer Ergebnisse.`);
    }
    setSelected(index);
    setFocusKey((k) => k + 1);
    requestAnimationFrame(() => {
      const r = chartPanelRef.current?.getBoundingClientRect();
      if (r && (r.top < 0 || r.bottom > window.innerHeight)) chartPanelRef.current?.scrollIntoView({ behavior: "smooth", block: "start" });
      const row = rowRefs.current[index];
      const box = journalRef.current;
      if (row && box && (row.offsetTop < box.scrollTop || row.offsetTop + row.offsetHeight > box.scrollTop + box.clientHeight))
        box.scrollTop = row.offsetTop - box.clientHeight / 2;
    });
  }
  function selectRelative(delta: number) {
    const next = relativeTradeIndex(selected, delta, trades.length);
    if (next != null) selectTrade(next);
  }
  function showAll() { setSelected(null); setFocusKey((k) => k + 1); }

  // ---- Abgeleitete Ansicht (Replay: nur bis pos bekannt) ----
  const replayPos = isReplay ? pos : null;
  const chartCandles = useMemo(() => (replayPos == null ? candles : candles.slice(0, replayPos + 1)), [candles, replayPos]);
  const chartTrades = useMemo(() => chartTradesAt(trades, replayPos), [trades, replayPos]);
  const snap = useMemo(() => replaySnapshot(candles, trades, equity, pos), [candles, trades, equity, pos]);
  const openLevels = useMemo(
    () => (isReplay && snap.open
      ? { fromBar: snap.open.trade.entryBarIndex, toBar: snap.pos, sl: snap.open.trade.stopLossPrice, tp: snap.open.trade.takeProfitPrice }
      : null),
    [isReplay, snap],
  );
  const journal = useMemo(
    () => (isReplay
      ? [...snap.closed.map((c) => ({ ...c, open: false })), ...(snap.open ? [{ ...snap.open, open: true }] : [])]
      : trades.map((trade, index) => ({ trade, index, open: false }))),
    [isReplay, snap, trades],
  );
  const replayEquity = useMemo(() => (isReplay ? equity.filter((p) => p.barIndex <= pos) : equity), [isReplay, equity, pos]);
  const selectedTrade = !isReplay && selected != null ? trades[selected] ?? null : null;
  const noSource = dataSources.length > 0 && !dataSources.some((d) => d.available);
  const view = data?.view;

  // Im Replay neueste Journalzeile sichtbar halten.
  useEffect(() => {
    if (isReplay && journalRef.current) journalRef.current.scrollTop = journalRef.current.scrollHeight;
  }, [isReplay, journal.length]);

  const secs = busy ? ((now - busySince) / 1000).toFixed(1) : "0";
  let chartStatus: ChartStatus | null = null;
  if (loadErr) chartStatus = { kind: "error", text: loadErr, actionLabel: "Erneut verbinden", onAction: () => void init() };
  else if (busy) chartStatus = {
    kind: "loading",
    text: busy === "load" ? `Lade echte OHLC-Kerzen … ${secs} s (Sierra: Aggregation aus Handelspreisen)` : `Backtest läuft … ${secs} s`,
    actionLabel: "Abbrechen", onAction: cancelBusy,
  };
  else if (err && n === 0) chartStatus = { kind: "error", text: err, actionLabel: "Erneut versuchen", onAction: () => void loadData() };
  else if (n === 0) chartStatus = noSource
    ? { kind: "empty", text: "Keine lokale Datenquelle verfügbar (erwartet: lokale Sierra-Datei oder OHLC-CSV unter samples/ohlc/*.csv)." }
    : { kind: "empty", text: "Noch keine Kerzen geladen. Datenquelle und Zeitraum wählen, dann „Daten laden“.", actionLabel: dataSources.length ? "Daten laden" : undefined, onAction: () => void loadData() };

  function exportCsv() {
    if (!res) return;
    const head = ["#", "Symbol", "Side", "Qty", "EntryTime", "EntryPrice", "ExitTime", "ExitPrice", "ExitReason", "Ambiguous", "StopLoss", "TakeProfit", "GrossPnL", "Fees", "NetPnL"];
    const rows = res.trades.map((t, k) => [k + 1, t.symbol, t.side, t.quantity, t.entryTime, t.entryPrice, t.exitTime, t.exitPrice,
      t.exitReason, t.ambiguous, t.stopLossPrice, t.takeProfitPrice, t.grossPnL, t.fees, t.netPnL]);
    const csv = [head, ...rows].map((r) => r.join(",")).join("\n");
    const a = document.createElement("a");
    a.href = URL.createObjectURL(new Blob([csv], { type: "text/csv" }));
    a.download = `backtest_${tb.symbol}_${tb.timeframeMinutes}m_${Date.now()}.csv`;
    a.click();
  }

  const activeStrategy = strategies.find((s) => s.id === st.strategy);
  const cur = snap.candle;

  return (
    <>
    {/* Erster Bildschirm: Werkzeugleiste, Status, Kennzahlen und der Chart füllen genau die Höhe des Inhaltsbereichs. */}
    <div className="h-full min-h-[620px] flex flex-col gap-2 p-3">
      {/* ---------- Werkzeugleiste ---------- */}
      <Panel className="py-2">
        <div className="flex flex-wrap items-end gap-2">
          <Field label="Datenquelle">
            <Select className="w-44" value={tb.dataSourceId} onChange={(v) => {
              const src = dataSources.find((d) => d.id === v);
              setTb({ ...tb, dataSourceId: v, fromUtc: src?.defaultFromUtc ? toInput(src.defaultFromUtc) : tb.fromUtc });
              if (src?.defaultMaxRows) setSt({ ...st, maxRows: src.defaultMaxRows });
            }}
              options={dataSources.length ? dataSources.map((d) => ({ value: d.id, label: d.label + (d.available ? "" : " (nicht verfügbar)") })) : [{ value: "", label: "—" }]} />
          </Field>
          <Field label="Symbol">
            <Select className="w-[72px]" value={tb.symbol} onChange={(v) => setTb({ ...tb, symbol: v })}
              options={instruments.length ? instruments.map((i) => ({ value: i.symbol, label: i.symbol })) : [{ value: tb.symbol, label: tb.symbol }]} />
          </Field>
          <Field label="Timeframe">
            <Select className="w-[76px]" value={String(tb.timeframeMinutes)} onChange={(v) => setTb({ ...tb, timeframeMinutes: parseInt(v) })}
              options={[1, 5, 15, 30, 60].map((m) => ({ value: String(m), label: `${m} Min` }))} />
          </Field>
          <Field label="Von (UTC)">
            <Input className="w-[132px]" value={tb.fromUtc} placeholder="2026-06-12 13:30" onChange={(v) => setTb({ ...tb, fromUtc: v })} />
          </Field>
          <Field label="Bis (UTC)">
            <Input className="w-[132px]" value={tb.toUtc} placeholder="offen" onChange={(v) => setTb({ ...tb, toUtc: v })} />
          </Field>
          <div className="flex items-center gap-1.5">
            <button onClick={() => void loadData()} disabled={!!busy || !tb.dataSourceId}
              className="h-7 px-3 rounded border border-[var(--key)] text-[var(--key)] text-xs font-bold hover:bg-[var(--bg-2)] disabled:opacity-40">
              {busy === "load" ? "Lädt…" : "Daten laden"}
            </button>
            <button onClick={() => void runBacktest()} disabled={!!busy || !tb.dataSourceId}
              className="h-7 px-3 rounded bg-[var(--key)] text-black text-xs font-bold disabled:opacity-40">
              {busy === "run" ? "Läuft…" : "▶ Backtest starten"}
            </button>
            <button onClick={() => setSettingsOpen((o) => !o)} aria-expanded={settingsOpen} title="Strategie, Parameter, Kosten und Datenumfang"
              className={`h-7 px-2.5 rounded border text-xs mono ${settingsOpen ? "border-[var(--key)] text-[var(--key)]" : "border-[var(--line-2)] text-[var(--fg-dim)]"} hover:bg-[var(--bg-2)]`}>
              ⚙ Einstellungen {settingsOpen ? "▴" : "▾"}
            </button>
          </div>
        </div>

        {settingsOpen && (
          <div className="mt-2 pt-2 border-t border-[var(--line)]">
            <div className="grid grid-cols-2 md:grid-cols-4 xl:grid-cols-8 gap-2">
              <Field label="Strategie">
                <Select value={st.strategy} onChange={(v) => setSt({ ...st, strategy: v })} options={strategies.map((s) => ({ value: s.id, label: s.name }))} />
              </Field>
              {activeStrategy?.params.map((p) => (
                <Field key={p.key} label={p.label}>
                  <Input value={st.params[p.key] ?? p.default} onChange={(v) => setSt({ ...st, params: { ...st.params, [p.key]: v } })} />
                </Field>
              ))}
              <Field label="Kontrakte"><Input type="number" value={String(st.quantity)} onChange={(v) => setSt({ ...st, quantity: parseInt(v) || 1 })} /></Field>
              <Field label="Startkapital $"><Input type="number" value={String(st.initialBalance)} onChange={(v) => setSt({ ...st, initialBalance: parseFloat(v) || 0 })} /></Field>
              <Field label="Stop-Loss (Ticks)"><Input value={st.stopLossTicks} placeholder="Instrument-Default" onChange={(v) => setSt({ ...st, stopLossTicks: v })} /></Field>
              <Field label="Take-Profit (Ticks)"><Input value={st.takeProfitTicks} placeholder="Instrument-Default" onChange={(v) => setSt({ ...st, takeProfitTicks: v })} /></Field>
              <Field label="Slippage (Ticks)"><Input value={st.slippageTicks} placeholder="Fee-Profil" onChange={(v) => setSt({ ...st, slippageTicks: v })} /></Field>
              <Field label="Gebühr/Seite $"><Input value={st.feePerSide} placeholder="aus Fee-Profil" onChange={(v) => setSt({ ...st, feePerSide: v })} /></Field>
              <Field label="Max. Sierra-Zeilen"><Input type="number" value={String(st.maxRows)} onChange={(v) => setSt({ ...st, maxRows: parseInt(v) || 100000 })} /></Field>
              <Field label="Gebühren anwenden"><Toggle checked={st.applyFees} onChange={(v) => setSt({ ...st, applyFees: v })} /></Field>
              <Field label="Randkerzen ausschließen"><Toggle checked={st.excludePartialEdges} onChange={(v) => setSt({ ...st, excludePartialEdges: v })} /></Field>
            </div>
            <p className="text-[10px] text-[var(--fg-faint)] mono mt-2">
              {activeStrategy?.isReference && <span className="text-[var(--gold)]">Referenz-/Teststrategie — keine Edge-Behauptung. </span>}
              „Max. Sierra-Zeilen" begrenzt die gelesenen Rohzeilen (dichte Zeiträume brauchen mehr Zeilen für genügend Bars); OHLC-CSV ignoriert den Wert.
              Kosten: Instrument-/Fee-Profile aus config/ — Details nach dem Lauf unter „Kosten &amp; Datenbasis".
            </p>
          </div>
        )}
      </Panel>

      {/* ---------- Status-/Datenzeile ---------- */}
      <div className="flex flex-wrap items-center gap-x-4 gap-y-1 px-1 text-[11px] mono text-[var(--fg-faint)] min-h-[18px]">
        <span className="text-[var(--fg)] font-bold not-italic">OHLC-Backtest <span className="font-normal text-[var(--fg-faint)]">· Simulation-only · UTC</span></span>
        {view ? (
          <>
            <span>Quelle <b className="text-[var(--fg)]">{view.source}</b></span>
            <span>{view.symbol} · {view.tf}m</span>
            <span>Zeitraum <b className="text-[var(--fg)]">{isoShort(view.from)} – {isoShort(view.to)} {view.tz}</b></span>
            <span>Bars <b className="text-[var(--fg)]">{view.barCount}</b>{view.evaluated != null && <> (ausgewertet {view.evaluated})</>}</span>
            {view.elapsedMs != null && <span>geladen in {(view.elapsedMs / 1000).toFixed(1)} s</span>}
            {(view.lead || view.trail) && (
              <span className="text-[var(--gold)]">
                ⚠ Teilkerze{view.lead ? " am Anfang" : ""}{view.lead && view.trail ? " und" : ""}{view.trail ? " am Ende" : ""}
                {view.partialExcluded ? " — aus der Auswertung ausgeschlossen" : " — im Backtest standardmäßig ausgeschlossen"}
              </span>
            )}
            {res && res.ambiguousTrades > 0 && <span className="text-[var(--gold)]">⚠ {res.ambiguousTrades} mehrdeutige Intrabar-Trades (konservativ SL)</span>}
            {!res && <span className="text-[var(--fg-dim)]">Kerzen ohne Strategielauf</span>}
            {data && data.issues.length > 0 && <span className="text-[var(--gold)]">⚠ {data.issues.length} verworfene Datenzeilen</span>}
          </>
        ) : (
          <span>{busy ? "Lädt…" : "Keine Daten geladen."}</span>
        )}
        {err && n > 0 && <span className="text-[var(--red)]">Fehler: {err}</span>}
        {notice && <span className="text-[var(--cyan)]">ℹ {notice}</span>}
      </div>

      {/* ---------- Kompakte Kennzahlen ---------- */}
      <div className="grid grid-cols-4 lg:grid-cols-8 gap-1.5">
        {isReplay ? (
          <>
            <Kpi label="Replay-Zeit (UTC)" value={cur ? msShort(cur.t) : "–"} />
            <Kpi label="Close" value={cur ? num(cur.c) : "–"} sub={cur ? `O ${num(cur.o)} H ${num(cur.h)} L ${num(cur.l)}` : undefined} />
            <Kpi label="Bar" value={n ? `${snap.pos + 1} / ${n}` : "–"} />
            <Kpi label="Status" value={res ? snap.status : "–"} cls={snap.status === "LONG" ? "text-[var(--key)]" : snap.status === "SHORT" ? "text-[var(--red)]" : undefined} sub={res ? undefined : "kein Backtest"} />
            <Kpi label="Realisiert (bis hier)" value={res ? money(snap.realizedNetPnL) : "–"} cls={res ? col(snap.realizedNetPnL) : undefined} />
            <Kpi label="Offen (MtM, brutto)" value={res && snap.open ? money(snap.openPnL) : "–"} cls={snap.open ? col(snap.openPnL) : undefined} />
            <Kpi label="Trades geschlossen" value={res ? String(snap.closed.length) : "–"} />
            <Kpi label="W / L (bis hier)" value={res ? `${snap.wins} / ${snap.losses}` : "–"} />
          </>
        ) : stats && res ? (
          <>
            <Kpi label="Netto-PnL" value={money(stats.netProfit)} cls={col(stats.netProfit)} />
            <Kpi label="Trades" value={String(stats.totalTrades)} />
            <Kpi label="Trefferquote" value={pct(stats.winRate)} sub={`${stats.winningTrades}W / ${stats.losingTrades}L`} />
            <Kpi label="Profit Factor" value={stats.profitFactor == null ? "–" : num(stats.profitFactor)} />
            <Kpi label="Max Drawdown" value={money(-stats.maxDrawdown)} cls="text-[var(--red)]" sub="realisiert" />
            <Kpi label="Erwartung/Trade" value={money(stats.expectancy)} cls={col(stats.expectancy)} />
            <Kpi label="Gebühren" value={money(stats.totalFees)} />
            <Kpi label="End-Equity" value={money(res.finalEquity)} cls={col(res.finalEquity - res.initialBalance)} />
          </>
        ) : (
          <>
            <Kpi label="Bars" value={n ? String(n) : "–"} />
            <Kpi label="Erste Kerze" value={n ? msShort(candles[0].t) : "–"} />
            <Kpi label="Letzte Kerze" value={n ? msShort(candles[n - 1].t) : "–"} />
            <Kpi label="Letzter Close" value={n ? num(candles[n - 1].c) : "–"} />
            <Kpi label="Hoch (geladen)" value={n ? num(candles.reduce((m, c) => (c.h > m ? c.h : m), -Infinity)) : "–"} />
            <Kpi label="Tief (geladen)" value={n ? num(candles.reduce((m, c) => (c.l < m ? c.l : m), Infinity)) : "–"} />
            <Kpi label="Backtest" value="–" sub="noch nicht gestartet" />
            <Kpi label="Modus" value="Übersicht" />
          </>
        )}
      </div>

      {/* ---------- Chart + Journal ---------- */}
      <div className="flex-1 min-h-[340px] grid gap-2 grid-cols-1 grid-rows-[minmax(0,1fr)_260px] xl:grid-cols-[minmax(0,1fr)_380px] xl:grid-rows-[minmax(0,1fr)]">
        <div ref={chartPanelRef} className="bg-[var(--panel)] border border-[var(--line)] rounded-lg p-2 flex flex-col min-w-0 min-h-0">
          <div className="flex flex-wrap items-center gap-1.5 mb-1.5">
            <div className="flex rounded border border-[var(--line-2)] overflow-hidden mr-1">
              <TabBtn active={!isReplay} onClick={() => switchMode("overview")}>Übersicht</TabBtn>
              <TabBtn active={isReplay} onClick={() => switchMode("replay")} disabled={n === 0}>Replay</TabBtn>
            </div>
            {isReplay ? (
              <>
                <NavBtn onClick={togglePlay} disabled={n === 0} primary>{playing ? "⏸ Pause" : "▶ Play"}</NavBtn>
                <NavBtn onClick={resetReplay} disabled={n === 0} title="Zurücksetzen auf die erste Kerze">⏮ Reset</NavBtn>
                <NavBtn onClick={() => step(-1)} disabled={pos <= 0} title="Eine Kerze zurück (←)">◀ 1 Bar</NavBtn>
                <NavBtn onClick={() => step(1)} disabled={pos >= n - 1} title="Eine Kerze vor (→)">1 Bar ▶</NavBtn>
                <div className="flex">
                  {SPEEDS.map((s) => (
                    <button key={s} onClick={() => setSpeed(s)} title={`${(s * 1000) / TICK_MS} Kerzen/s`}
                      className={`h-6 px-1.5 text-[11px] mono border border-[var(--line-2)] -ml-px first:ml-0 first:rounded-l last:rounded-r ${speed === s ? "bg-[var(--bg-2)] text-[var(--key)]" : "text-[var(--fg-dim)] hover:bg-[var(--bg-2)]"}`}>
                      x{s}
                    </button>
                  ))}
                </div>
                <input type="range" min={0} max={Math.max(0, n - 1)} value={pos} aria-label="Replay-Position"
                  onChange={(e) => { setPlaying(false); setPos(clampPos(parseInt(e.target.value), n)); }}
                  className="flex-1 min-w-[120px] accent-[var(--key)]" />
                <span className="text-[11px] mono text-[var(--fg-faint)] whitespace-nowrap">Bar {n ? snap.pos + 1 : 0} / {n}</span>
              </>
            ) : (
              <>
                <NavBtn onClick={() => selectRelative(-1)} disabled={trades.length === 0}>◀ Vorheriger</NavBtn>
                <NavBtn onClick={() => selectRelative(1)} disabled={trades.length === 0}>Nächster ▶</NavBtn>
                <NavBtn onClick={showAll} disabled={n === 0}>Gesamten Zeitraum</NavBtn>
                <span className="flex-1 min-w-0 truncate text-[10px] mono text-[var(--fg-faint)] ml-1">
                  {res ? "Klick auf Journalzeile oder Marker springt zum Trade" : "Übersicht: gesamter geladener Zeitraum"}
                </span>
              </>
            )}
          </div>

          {selectedTrade && (
            <div className="text-[11px] mono text-[var(--fg-faint)] flex flex-wrap gap-x-3 gap-y-0.5 mb-1.5">
              <span className="text-[var(--key)]">Trade #{selected! + 1}</span>
              <span className={selectedTrade.side === "Long" ? "text-[var(--key)]" : "text-[var(--red)]"}>{selectedTrade.side}</span>
              <span>Entry <b className="text-[var(--fg)]">{selectedTrade.entryPrice.toFixed(2)}</b> @ {tShort(selectedTrade.entryTime)} (Bar {selectedTrade.entryBarIndex})</span>
              <span>Exit <b className="text-[var(--fg)]">{selectedTrade.exitPrice.toFixed(2)}</b> @ {tShort(selectedTrade.exitTime)} (Bar {selectedTrade.exitBarIndex})</span>
              <span>{selectedTrade.exitReason}{selectedTrade.ambiguous ? " ⚠ mehrdeutig" : ""}</span>
              <span>SL <b className="text-[var(--red)]">{selectedTrade.stopLossPrice > 0 ? selectedTrade.stopLossPrice.toFixed(2) : "–"}</b> / TP <b className="text-[var(--key)]">{selectedTrade.takeProfitPrice > 0 ? selectedTrade.takeProfitPrice.toFixed(2) : "–"}</b></span>
              <span className={col(selectedTrade.netPnL)}>Net {money(selectedTrade.netPnL)}</span>
            </div>
          )}

          <BacktestChart
            className="flex-1 min-h-0"
            candles={chartCandles}
            trades={chartTrades}
            selected={isReplay ? null : selected}
            focusKey={focusKey}
            openLevels={openLevels}
            follow={isReplay ? FOLLOW_BARS : null}
            onSelectTrade={selectTrade}
            status={chartStatus}
          />
          {(() => {
            const legend = isReplay
              ? "Replay: nur Kerzen, Marker und Ergebnisse bis zur aktuellen Kerze · pro Schritt eine vollständige Kerze · SL/TP gestrichelt = Level des offenen Trades · Leertaste Play/Pause, ←/→ eine Kerze."
              : "▲ Entry (grün Long / rot Short) · ◆ Exit (grün TP / rot SL / gold sonst; weiß umrandet = mehrdeutig) · ausgewählter Trade: betont, Band über die Dauer, SL/TP gestrichelt · Mausrad/Ziehen = Zoom/Verschieben.";
            return <p title={legend} className="text-[10px] text-[var(--fg-faint)] mono mt-1 truncate">{legend}</p>;
          })()}
        </div>

        <div className="bg-[var(--panel)] border border-[var(--line)] rounded-lg p-2 flex flex-col min-w-0 min-h-0">
          <div className="flex items-center justify-between mb-1.5">
            <p className="text-xs mono text-[var(--fg-faint)]">
              Trade-Journal{" "}
              {isReplay
                ? res ? <>· bis Bar {snap.pos + 1} ({snap.closed.length} geschl.{snap.open ? ", 1 offen" : ""})</> : "· kein Backtest"
                : res ? `(${trades.length})` : ""}
            </p>
            <button onClick={exportCsv} disabled={!res || isReplay} title={isReplay ? "CSV-Export in der Übersicht (vollständige Ergebnisse)" : undefined}
              className="px-2 py-0.5 rounded border border-[var(--line)] text-[10px] mono hover:bg-[var(--bg-2)] disabled:opacity-40">CSV</button>
          </div>
          <div ref={journalRef} className="flex-1 min-h-0 overflow-auto">
            {!res ? (
              <p className="p-3 text-center text-[11px] mono text-[var(--fg-faint)]">
                Noch kein Backtest. Kerzen werden ohne Strategielauf angezeigt — „▶ Backtest starten" erzeugt Trades.
              </p>
            ) : (
              <table className="w-full text-[11px] mono">
                <thead className="sticky top-0 bg-[var(--panel)] text-[var(--fg-faint)] z-10">
                  <tr className="text-left">
                    <th className="p-1">#</th><th className="p-1">Side</th><th className="p-1">Entry</th><th className="p-1">Exit</th>
                    <th className="p-1">Grund</th><th className="p-1 text-right">Net</th>
                  </tr>
                </thead>
                <tbody>
                  {journal.map(({ trade: t, index: k, open }) => (
                    <tr key={k} ref={(el) => { rowRefs.current[k] = el; }} onClick={() => selectTrade(k)}
                      className={`border-t border-[var(--line)] cursor-pointer align-top ${!isReplay && selected === k ? "bg-[var(--bg-2)] outline outline-1 outline-[var(--key)]" : open ? "bg-[var(--bg-2)]" : "hover:bg-[var(--bg-2)]"}`}>
                      <td className="p-1">{!isReplay && selected === k ? "▶" : ""}{k + 1}</td>
                      <td className={`p-1 ${t.side === "Long" ? "text-[var(--key)]" : "text-[var(--red)]"}`}>{t.side}</td>
                      <td className="p-1 leading-tight">{tShort(t.entryTime)}<br /><span className="text-[var(--fg-dim)]">{t.entryPrice.toFixed(2)}</span></td>
                      {open ? (
                        <>
                          <td className="p-1 text-[var(--gold)]">offen</td>
                          <td className="p-1 text-[var(--fg-faint)]">SL {t.stopLossPrice.toFixed(2)}<br />TP {t.takeProfitPrice.toFixed(2)}</td>
                          <td className="p-1 text-right text-[var(--fg-faint)]">–</td>
                        </>
                      ) : (
                        <>
                          <td className="p-1 leading-tight">{tShort(t.exitTime)}<br /><span className="text-[var(--fg-dim)]">{t.exitPrice.toFixed(2)}</span></td>
                          <td className="p-1">{t.exitReason}{t.ambiguous ? " ⚠" : ""}</td>
                          <td className={`p-1 text-right ${col(t.netPnL)}`}>{money(t.netPnL)}</td>
                        </>
                      )}
                    </tr>
                  ))}
                  {journal.length === 0 && (
                    <tr><td colSpan={6} className="p-3 text-center text-[var(--fg-faint)]">{isReplay ? "Bis zu dieser Kerze noch kein Trade." : "Keine Trades in diesem Lauf."}</td></tr>
                  )}
                </tbody>
              </table>
            )}
          </div>
        </div>
      </div>

    </div>

    {/* ---------- Auswertung (ausführlich, unterhalb des ersten Bildschirms) ---------- */}
    <div className="px-3 pb-3">
      <Panel>
        <div className="flex items-center gap-1 mb-2">
          <TabBtn active={bottomTab === "equity"} onClick={() => setBottomTab("equity")}>Equity &amp; Drawdown</TabBtn>
          <TabBtn active={bottomTab === "stats"} onClick={() => setBottomTab("stats")}>Kennzahlen (Detail)</TabBtn>
          <TabBtn active={bottomTab === "costs"} onClick={() => setBottomTab("costs")}>Kosten &amp; Datenbasis</TabBtn>
          {isReplay && res && <span className="ml-2 text-[10px] mono text-[var(--gold)]">Replay: Auswertung nur bis Bar {snap.pos + 1}</span>}
        </div>

        {!res ? (
          <p className="text-[11px] mono text-[var(--fg-faint)] py-3">Auswertung erscheint nach „▶ Backtest starten".</p>
        ) : bottomTab === "equity" ? (
          <>
            <EquityDrawdownChart equity={replayEquity} initialBalance={res.initialBalance} />
            <p className="text-[10px] text-[var(--fg-faint)] mono mt-1">
              Basis: ausschließlich realisierte Trades (kumulierter Netto-PnL). Offene Positionen sind hier nicht mark-to-market enthalten.
              Eine am Datenende offene Position wird zwangsweise zum letzten Schlusskurs geschlossen (Exit-Grund <b className="text-[var(--fg)]">EndOfData</b>).
            </p>
          </>
        ) : bottomTab === "stats" ? (
          isReplay ? (
            <p className="text-[11px] mono text-[var(--fg-faint)] py-3">Detail-Kennzahlen beschreiben den vollständigen Lauf und werden im Replay nicht gezeigt (keine Zukunftsdaten). Zur Übersicht wechseln.</p>
          ) : stats && (
            <div className="grid grid-cols-2 md:grid-cols-4 lg:grid-cols-6 gap-1.5">
              <Kpi label="Brutto-PnL" value={money(stats.grossProfit)} cls={col(stats.grossProfit)} />
              <Kpi label="Netto-PnL" value={money(stats.netProfit)} cls={col(stats.netProfit)} />
              <Kpi label="Gebühren" value={money(stats.totalFees)} />
              <Kpi label="Slippage (info)" value={money(stats.totalSlippage)} sub="in Fill-Preisen enthalten" />
              <Kpi label="Ø Gewinner" value={money(stats.averageWinner)} cls="text-[var(--key)]" />
              <Kpi label="Ø Verlierer" value={money(stats.averageLoser)} cls="text-[var(--red)]" />
              <Kpi label="Größter Gewinn" value={money(stats.largestWin)} cls="text-[var(--key)]" />
              <Kpi label="Größter Verlust" value={money(stats.largestLoss)} cls="text-[var(--red)]" />
              <Kpi label="Serie Gewinne/Verluste" value={`${stats.maxWinningStreak} / ${stats.maxLosingStreak}`} />
              <Kpi label="Trades/Tag" value={num(stats.tradesPerDay)} />
              <Kpi label="Signale" value={String(res.signalsGenerated)} />
              <Kpi label="Mehrdeutig (SL&TP)" value={String(res.ambiguousTrades)} sub="konservativ als SL" />
            </div>
          )
        ) : (
          <div className="space-y-2">
            {cost && (
              <div className="grid grid-cols-2 md:grid-cols-4 lg:grid-cols-7 gap-1.5">
                <Kpi label="Tickgröße" value={`${num(cost.tickSize, 4)} Pkt`} />
                <Kpi label="Tickwert" value={`${money(cost.tickValue)} ${cost.currency}`} />
                <Kpi label="Punktwert" value={`${money(cost.pointValue)} ${cost.currency}`} />
                <Kpi label="Gebühr / Seite" value={money(cost.feePerSide)} sub={cost.feeIsExample ? "Beispielprofil" : "aus Fee-Profil"} />
                <Kpi label="Gebühr / Roundtrip" value={money(cost.feeRoundTrip)} sub={cost.applyFees ? "angewandt" : "nicht angewandt"} />
                <Kpi label="Slippage / Seite" value={`${num(cost.slippageTicks, 2)} Ticks`} sub={`${money(cost.slippagePerSideDollars)} in Fill-Preisen`} />
                <Kpi label="Startkapital" value={money(res.initialBalance)} />
              </div>
            )}
            {cost && (cost.instrumentIsExample || cost.feeIsExample) && (
              <p className="text-[10px] mono text-[var(--gold)]">⚠ Beispielprofil — keine echten Broker-Kosten (config/*.example.json).</p>
            )}
            <p className="text-[10px] mono text-[var(--fg-faint)]">
              Strategie {res.strategyName} · SL/TP/Slippage effektiv {res.effectiveStopLossTicks}/{res.effectiveTakeProfitTicks}/{num(res.effectiveSlippageTicks, 1)} Ticks ·
              Datenbasis {res.data.source}, {isoShort(res.data.from)} – {isoShort(res.data.to)} UTC, {res.data.barCount} Bars ({res.data.evaluatedBars} ausgewertet).
              Slippage ist im Ausführungspreis von Market-Orders enthalten und wird nicht zusätzlich abgezogen.
            </p>
          </div>
        )}
      </Panel>
    </div>
    </>
  );
}

// ---- kleine UI-Helfer (Theme-konform) ----
function Panel({ children, className }: { children: React.ReactNode; className?: string }) {
  return <div className={`bg-[var(--panel)] border border-[var(--line)] rounded-lg p-3 ${className || ""}`}>{children}</div>;
}
function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (<label className="flex flex-col gap-0.5"><span className="text-[9px] mono text-[var(--fg-faint)] uppercase tracking-wider">{label}</span>{children}</label>);
}
function Input({ value, onChange, placeholder, type = "text", className }: { value: string; onChange: (v: string) => void; placeholder?: string; type?: string; className?: string }) {
  return <input type={type} value={value} placeholder={placeholder} onChange={(e) => onChange(e.target.value)}
    className={`h-7 bg-[var(--bg-2)] border border-[var(--line)] rounded px-2 text-xs mono text-[var(--fg)] outline-none focus:border-[var(--key)] ${className || ""}`} />;
}
function Select({ value, onChange, options, className }: { value: string; onChange: (v: string) => void; options: { value: string; label: string }[]; className?: string }) {
  return <select value={value} onChange={(e) => onChange(e.target.value)}
    className={`h-7 bg-[var(--bg-2)] border border-[var(--line)] rounded px-1.5 text-xs mono text-[var(--fg)] outline-none focus:border-[var(--key)] ${className || ""}`}>
    {options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
  </select>;
}
function Toggle({ checked, onChange }: { checked: boolean; onChange: (v: boolean) => void }) {
  return <button onClick={() => onChange(!checked)} className={`h-7 w-fit px-2 rounded text-xs mono border ${checked ? "border-[var(--key)] text-[var(--key)]" : "border-[var(--line)] text-[var(--fg-faint)]"}`}>{checked ? "an" : "aus"}</button>;
}
function NavBtn({ children, onClick, disabled, title, primary }: { children: React.ReactNode; onClick: () => void; disabled?: boolean; title?: string; primary?: boolean }) {
  return (
    <button onClick={onClick} disabled={disabled} title={title}
      className={`h-6 px-2 rounded text-[11px] mono border disabled:opacity-40 disabled:cursor-not-allowed ${primary ? "bg-[var(--key)] text-black border-[var(--key)] font-bold" : "border-[var(--line-2)] text-[var(--fg)] hover:bg-[var(--bg-2)]"}`}>
      {children}
    </button>
  );
}
function TabBtn({ children, active, onClick, disabled }: { children: React.ReactNode; active: boolean; onClick: () => void; disabled?: boolean }) {
  return (
    <button onClick={onClick} disabled={disabled}
      className={`h-6 px-2.5 text-[11px] mono disabled:opacity-40 ${active ? "bg-[var(--bg-2)] text-[var(--key)]" : "text-[var(--fg-faint)] hover:text-[var(--fg)]"}`}>
      {children}
    </button>
  );
}
function Kpi({ label, value, sub, cls }: { label: string; value: string; sub?: string; cls?: string }) {
  return (
    <div className="bg-[var(--panel)] border border-[var(--line)] rounded-md px-2 py-1 min-w-0">
      <p className="text-[9px] mono text-[var(--fg-faint)] uppercase tracking-wider truncate">{label}</p>
      <p className={`text-[13px] mono font-bold leading-tight truncate ${cls || "text-[var(--fg)]"}`}>{value}</p>
      {sub && <p className="text-[9px] mono text-[var(--fg-faint)] truncate">{sub}</p>}
    </div>
  );
}
