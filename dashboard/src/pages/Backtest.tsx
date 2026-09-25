import { useEffect, useRef, useState } from "react";
import {
  backtestApi, type StrategyDef, type InstrumentDef, type DataSourceDef,
  type RunResponse, type OhlcTrade, type RunRequest,
} from "@/lib/backtestApi";
import { BacktestChart } from "@/components/backtest/BacktestChart";
import { EquityDrawdownChart } from "@/components/backtest/EquityDrawdownChart";

const money = (v: number | null | undefined) => (v == null ? "–" : `${v < 0 ? "-" : ""}$${Math.abs(v).toFixed(2)}`);
const pct = (v: number | null | undefined) => (v == null ? "–" : `${(v * 100).toFixed(1)}%`);
const num = (v: number | null | undefined, d = 2) => (v == null ? "–" : v.toFixed(d));
const col = (v: number) => (v > 0 ? "text-[var(--key)]" : v < 0 ? "text-[var(--red)]" : "text-[var(--fg)]");

interface FormState {
  dataSourceId: string; symbol: string; timeframeMinutes: number; fromUtc: string; maxRows: number;
  strategy: string; params: Record<string, string>;
  quantity: number; initialBalance: number;
  stopLossTicks: string; takeProfitTicks: string; slippageTicks: string; feePerSide: string;
  applyFees: boolean; excludePartialEdges: boolean;
}

export function Backtest() {
  const [strategies, setStrategies] = useState<StrategyDef[]>([]);
  const [instruments, setInstruments] = useState<InstrumentDef[]>([]);
  const [dataSources, setDataSources] = useState<DataSourceDef[]>([]);
  const [loadErr, setLoadErr] = useState<string | null>(null);

  const [form, setForm] = useState<FormState>({
    dataSourceId: "sierra-mesm26", symbol: "MES", timeframeMinutes: 5, fromUtc: "", maxRows: 1500000,
    strategy: "movingaverage", params: { FastPeriod: "9", SlowPeriod: "21" },
    quantity: 1, initialBalance: 10000,
    stopLossTicks: "", takeProfitTicks: "", slippageTicks: "1", feePerSide: "",
    applyFees: true, excludePartialEdges: true,
  });

  const [running, setRunning] = useState(false);
  const [resp, setResp] = useState<RunResponse | null>(null);
  const [runErr, setRunErr] = useState<string | null>(null);
  const [focus, setFocus] = useState<{ entryBarIndex: number; exitBarIndex: number } | null>(null);
  const journalRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    (async () => {
      try {
        const [s, i, d] = await Promise.all([backtestApi.strategies(), backtestApi.instruments(), backtestApi.dataSources()]);
        setStrategies(s); setInstruments(i); setDataSources(d);
        if (i.length && !i.some((x) => x.symbol === "MES")) setForm((f) => ({ ...f, symbol: i[0].symbol }));
      } catch (e: any) {
        setLoadErr(`Backend nicht erreichbar (${backtestApi.base}). Läuft das .NET-DevDashboard? ${e?.message ?? ""}`);
      }
    })();
  }, []);

  const activeStrategy = strategies.find((s) => s.id === form.strategy);
  const result = resp?.ok ? resp.result! : null;
  const stats = result?.statistics;

  async function run() {
    setRunning(true); setRunErr(null); setResp(null); setFocus(null);
    try {
      const req: RunRequest = {
        dataSourceId: form.dataSourceId,
        symbol: form.symbol,
        timeframeMinutes: form.timeframeMinutes,
        fromUtc: form.fromUtc.trim() || null,
        maxRows: form.maxRows,
        strategy: form.strategy,
        params: form.params,
        quantity: form.quantity,
        initialBalance: form.initialBalance,
        stopLossTicks: form.stopLossTicks ? parseInt(form.stopLossTicks) : null,
        takeProfitTicks: form.takeProfitTicks ? parseInt(form.takeProfitTicks) : null,
        slippageTicks: form.slippageTicks ? parseFloat(form.slippageTicks) : null,
        feePerSideOverride: form.feePerSide ? parseFloat(form.feePerSide) : null,
        applyFees: form.applyFees,
        excludePartialEdges: form.excludePartialEdges,
      };
      const r = await backtestApi.run(req);
      setResp(r);
      if (!r.ok) setRunErr(r.error ?? "Unbekannter Fehler.");
    } catch (e: any) {
      setRunErr(`Aufruf fehlgeschlagen: ${e?.message ?? e}`);
    } finally {
      setRunning(false);
    }
  }

  function exportCsv() {
    if (!result) return;
    const head = ["#", "Symbol", "Side", "Qty", "EntryTime", "EntryPrice", "ExitTime", "ExitPrice", "ExitReason", "Ambiguous", "GrossPnL", "Fees", "NetPnL"];
    const rows = result.trades.map((t, k) => [
      k + 1, t.symbol, t.side, t.quantity, t.entryTime, t.entryPrice, t.exitTime, t.exitPrice,
      t.exitReason, t.ambiguous, t.grossPnL, t.fees, t.netPnL,
    ]);
    const csv = [head, ...rows].map((r) => r.join(",")).join("\n");
    const blob = new Blob([csv], { type: "text/csv" });
    const a = document.createElement("a");
    a.href = URL.createObjectURL(blob);
    a.download = `backtest_${form.symbol}_${form.timeframeMinutes}m_${Date.now()}.csv`;
    a.click();
  }

  function focusTrade(t: OhlcTrade) {
    setFocus({ entryBarIndex: t.entryBarIndex, exitBarIndex: t.exitBarIndex });
  }

  const dataMeta = result?.data;
  const cost = resp?.ok ? resp.costProfile : null;

  return (
    <div className="p-4 space-y-4">
      <div>
        <h1 className="text-lg font-bold">OHLC Backtest</h1>
        <p className="text-xs text-[var(--fg-faint)] mono">
          Research/Simulation-only · bar-basierte Ausführung ohne Look-ahead · Ergebnisübersicht über den vollständigen Zeitraum (kein Replay).
          Kerzen ausschließlich aus Handelspreisen.
        </p>
      </div>

      {loadErr && <Panel className="text-[var(--red)] text-xs mono">{loadErr}</Panel>}

      {/* --- Konfiguration --- */}
      <Panel>
        <div className="grid grid-cols-2 md:grid-cols-4 gap-3">
          <Field label="Datenquelle">
            <Select value={form.dataSourceId} onChange={(v) => setForm({ ...form, dataSourceId: v })}
              options={dataSources.map((d) => ({ value: d.id, label: d.label + (d.available ? "" : " (n/v)") }))} />
          </Field>
          <Field label="Symbol">
            <Select value={form.symbol} onChange={(v) => setForm({ ...form, symbol: v })}
              options={instruments.map((i) => ({ value: i.symbol, label: i.symbol }))} />
          </Field>
          <Field label="Timeframe (Min)">
            <Select value={String(form.timeframeMinutes)} onChange={(v) => setForm({ ...form, timeframeMinutes: parseInt(v) })}
              options={[1, 5, 15].map((m) => ({ value: String(m), label: `${m} Min` }))} />
          </Field>
          <Field label="Von (UTC, optional)">
            <Input value={form.fromUtc} placeholder="2026-06-12 14:00" onChange={(v) => setForm({ ...form, fromUtc: v })} />
          </Field>
          <Field label="Max Zeilen (Sierra-Ticks)">
            <Input type="number" value={String(form.maxRows)} onChange={(v) => setForm({ ...form, maxRows: parseInt(v) || 100000 })} />
          </Field>

          <Field label="Strategie">
            <Select value={form.strategy} onChange={(v) => setForm({ ...form, strategy: v })}
              options={strategies.map((s) => ({ value: s.id, label: s.name }))} />
          </Field>
          {activeStrategy?.params.map((p) => (
            <Field key={p.key} label={p.label}>
              <Input value={form.params[p.key] ?? p.default}
                onChange={(v) => setForm({ ...form, params: { ...form.params, [p.key]: v } })} />
            </Field>
          ))}

          <Field label="Kontrakte"><Input type="number" value={String(form.quantity)} onChange={(v) => setForm({ ...form, quantity: parseInt(v) || 1 })} /></Field>
          <Field label="Startkapital $"><Input type="number" value={String(form.initialBalance)} onChange={(v) => setForm({ ...form, initialBalance: parseFloat(v) || 0 })} /></Field>
          <Field label="Stop-Loss (Ticks)"><Input value={form.stopLossTicks} placeholder="Instrument-Default" onChange={(v) => setForm({ ...form, stopLossTicks: v })} /></Field>
          <Field label="Take-Profit (Ticks)"><Input value={form.takeProfitTicks} placeholder="Instrument-Default" onChange={(v) => setForm({ ...form, takeProfitTicks: v })} /></Field>
          <Field label="Slippage (Ticks)"><Input value={form.slippageTicks} placeholder="Fee-Profil" onChange={(v) => setForm({ ...form, slippageTicks: v })} /></Field>
          <Field label="Gebühr/Seite $ (optional)"><Input value={form.feePerSide} placeholder="aus Fee-Profil" onChange={(v) => setForm({ ...form, feePerSide: v })} /></Field>
          <Field label="Gebühren anwenden"><Toggle checked={form.applyFees} onChange={(v) => setForm({ ...form, applyFees: v })} /></Field>
          <Field label="Randkerzen ausschließen"><Toggle checked={form.excludePartialEdges} onChange={(v) => setForm({ ...form, excludePartialEdges: v })} /></Field>
        </div>
        <div className="flex items-center gap-3 mt-3">
          <button onClick={run} disabled={running}
            className="px-4 py-1.5 rounded bg-[var(--key)] text-black text-sm font-bold disabled:opacity-50">
            {running ? "Läuft…" : "▶ Backtest starten"}
          </button>
          {activeStrategy?.isReference && <span className="text-[10px] text-[var(--gold,#e8c069)] mono">Referenz-/Teststrategie — keine Edge-Behauptung.</span>}
        </div>
        <p className="text-[10px] text-[var(--fg-faint)] mono mt-2">
          „Max Zeilen" begrenzt die aus der Sierra-Tickdatei gelesenen Rohzeilen. Dünne Dateibereiche liefern schon mit wenig Zeilen viele Bars; dichte Zeiträume brauchen deutlich mehr Zeilen für genügend Bars (SMA-Warmup). OHLC-CSV-Quellen ignorieren diesen Wert.
        </p>
      </Panel>

      {runErr && <Panel className="text-[var(--red)] text-xs mono">Fehler: {runErr}{resp && resp.dataIssues.length > 0 && ` · ${resp.dataIssues.length} Datenprobleme`}</Panel>}

      {/* --- Ergebnis --- */}
      {result && stats && dataMeta && (
        <>
          <Panel>
            <div className="text-[11px] mono text-[var(--fg-faint)] flex flex-wrap gap-x-4 gap-y-1">
              <span>Quelle: <b className="text-[var(--fg)]">{dataMeta.source}</b></span>
              <span>Symbol: <b className="text-[var(--fg)]">{dataMeta.symbol}</b></span>
              <span>TF: <b className="text-[var(--fg)]">{dataMeta.timeframeMinutes}m</b></span>
              <span>Zeitraum: <b className="text-[var(--fg)]">{dataMeta.from?.slice(0, 16)} – {dataMeta.to?.slice(0, 16)} {dataMeta.timezone}</b></span>
              <span>Bars: <b className="text-[var(--fg)]">{dataMeta.barCount}</b> (ausgewertet {dataMeta.evaluatedBars})</span>
              <span>Signale: <b className="text-[var(--fg)]">{result.signalsGenerated}</b></span>
              <span>SL/TP/Slip: <b className="text-[var(--fg)]">{result.effectiveStopLossTicks}/{result.effectiveTakeProfitTicks}/{num(result.effectiveSlippageTicks, 1)} Ticks</b></span>
              {(dataMeta.leadingPartialExcluded || dataMeta.trailingPartialExcluded) &&
                <span className="text-[var(--gold,#e8c069)]">⚠ Teilkerzen ausgeschlossen{dataMeta.leadingPartialExcluded ? " (Anfang)" : ""}{dataMeta.trailingPartialExcluded ? " (Ende)" : ""}</span>}
              {result.ambiguousTrades > 0 && <span className="text-[var(--gold,#e8c069)]">⚠ {result.ambiguousTrades} mehrdeutige Intrabar-Trades (SL&amp;TP in einer Kerze → konservativ SL)</span>}
            </div>
          </Panel>

          {cost && (
            <Panel>
              <div className="flex items-center gap-2 mb-2">
                <Head>Instrument- &amp; Kostenprofil (tatsächlich verwendete Werte)</Head>
                {(cost.instrumentIsExample || cost.feeIsExample) && (
                  <span className="text-[10px] mono px-1.5 py-0.5 rounded border border-[var(--gold,#e8c069)] text-[var(--gold,#e8c069)]">
                    ⚠ Beispielprofil — keine echten Broker-Kosten
                  </span>
                )}
              </div>
              <div className="grid grid-cols-2 md:grid-cols-4 lg:grid-cols-7 gap-3">
                <Stat label="Tickgröße" value={`${num(cost.tickSize, 4)} Pkt`} />
                <Stat label="Tickwert" value={`${money(cost.tickValue)} ${cost.currency}`} />
                <Stat label="Punktwert" value={`${money(cost.pointValue)} ${cost.currency}`} />
                <Stat label="Gebühr / Seite" value={money(cost.feePerSide)} sub={cost.feeIsExample ? "Beispiel" : "aus Fee-Profil"} />
                <Stat label="Gebühr / Roundtrip" value={money(cost.feeRoundTrip)} sub={cost.applyFees ? "angewandt" : "nicht angewandt"} />
                <Stat label="Slippage / Seite" value={`${num(cost.slippageTicks, 2)} Ticks`} sub={`${money(cost.slippagePerSideDollars)} — in Fill-Preisen`} />
                <Stat label="Kapital" value={money(result.initialBalance)} />
              </div>
              <p className="text-[10px] text-[var(--fg-faint)] mono mt-2">
                Slippage ist im Ausführungspreis von Market-Orders enthalten und wird NICHT zusätzlich vom Netto-PnL abgezogen (der Slippage-Betrag unten ist rein informativ).
              </p>
            </Panel>
          )}

          <div className="grid grid-cols-2 md:grid-cols-4 lg:grid-cols-6 gap-3">
            <Stat label="Netto-PnL" value={money(stats.netProfit)} cls={col(stats.netProfit)} />
            <Stat label="Trades" value={String(stats.totalTrades)} />
            <Stat label="Trefferquote" value={pct(stats.winRate)} sub={`${stats.winningTrades}W / ${stats.losingTrades}L`} />
            <Stat label="Profit Factor" value={stats.profitFactor == null ? "–" : num(stats.profitFactor)} sub={stats.profitFactor == null ? "kein Verlust-Nenner" : ""} />
            <Stat label="Max Drawdown" value={money(-stats.maxDrawdown)} cls="text-[var(--red)]" sub="auf realis. NetPnL" />
            <Stat label="Erwartungswert/Trade" value={money(stats.expectancy)} cls={col(stats.expectancy)} />
            <Stat label="Ø Gewinner" value={money(stats.averageWinner)} cls="text-[var(--key)]" />
            <Stat label="Ø Verlierer" value={money(stats.averageLoser)} cls="text-[var(--red)]" />
            <Stat label="Brutto-PnL" value={money(stats.grossProfit)} cls={col(stats.grossProfit)} />
            <Stat label="Gebühren" value={money(stats.totalFees)} />
            <Stat label="Slippage (info)" value={money(stats.totalSlippage)} sub="in Fill-Preisen enth." />
            <Stat label="End-Equity" value={money(result.finalEquity)} cls={col(result.finalEquity - result.initialBalance)} />
          </div>

          <Panel>
            <Head>Equity-Kurve (realisiert) &amp; Drawdown</Head>
            <EquityDrawdownChart equity={result.equity} initialBalance={result.initialBalance} />
            <p className="text-[10px] text-[var(--fg-faint)] mono mt-1">
              Basis: ausschließlich realisierte Trades (kumulierter Netto-PnL). Offene Positionen werden nicht laufend mark-to-market bewertet — unrealisierte Verluste sind hier nicht enthalten.
              Eine am Datenende offene Position wird zwangsweise zum letzten Schlusskurs geschlossen (Exit-Grund <b className="text-[var(--fg)]">EndOfData</b>) und erscheint als regulärer Trade im Journal.
            </p>
          </Panel>

          <Panel>
            <Head>Kerzenchart mit Entry-/Exit-Markern {focus && <span className="text-[10px] text-[var(--fg-faint)]">(fokussiert)</span>}</Head>
            <BacktestChart candles={resp!.candles} trades={result.trades} focus={focus} />
            <p className="text-[10px] text-[var(--fg-faint)] mono mt-1">
              Dreieck = Entry (grün Long / rot Short), Raute = Exit (grün TP / rot SL / gold sonst; weiß umrandet = mehrdeutig). Klick auf eine Trade-Zeile fokussiert den Bereich.
            </p>
          </Panel>

          <Panel>
            <div className="flex items-center justify-between mb-2">
              <Head>Trade-Journal ({result.trades.length})</Head>
              <button onClick={exportCsv} className="px-3 py-1 rounded border border-[var(--line)] text-xs mono hover:bg-[var(--bg-2)]">CSV export</button>
            </div>
            <div ref={journalRef} className="overflow-auto max-h-[360px]">
              <table className="w-full text-[11px] mono">
                <thead className="sticky top-0 bg-[var(--panel)] text-[var(--fg-faint)]">
                  <tr className="text-left">
                    <th className="p-1">#</th><th className="p-1">Side</th><th className="p-1">Entry</th><th className="p-1 text-right">Entry $</th>
                    <th className="p-1">Exit</th><th className="p-1 text-right">Exit $</th><th className="p-1">Grund</th>
                    <th className="p-1 text-right">Qty</th><th className="p-1 text-right">Net-PnL</th>
                  </tr>
                </thead>
                <tbody>
                  {result.trades.map((t, k) => (
                    <tr key={k} onClick={() => focusTrade(t)}
                      className="border-t border-[var(--line)] hover:bg-[var(--bg-2)] cursor-pointer">
                      <td className="p-1">{k + 1}</td>
                      <td className={`p-1 ${t.side === "Long" ? "text-[var(--key)]" : "text-[var(--red)]"}`}>{t.side}</td>
                      <td className="p-1">{t.entryTime.slice(5, 16).replace("T", " ")}</td>
                      <td className="p-1 text-right">{t.entryPrice.toFixed(2)}</td>
                      <td className="p-1">{t.exitTime.slice(5, 16).replace("T", " ")}</td>
                      <td className="p-1 text-right">{t.exitPrice.toFixed(2)}</td>
                      <td className="p-1">{t.exitReason}{t.ambiguous ? " ⚠" : ""}</td>
                      <td className="p-1 text-right">{t.quantity}</td>
                      <td className={`p-1 text-right ${col(t.netPnL)}`}>{money(t.netPnL)}</td>
                    </tr>
                  ))}
                  {result.trades.length === 0 && (
                    <tr><td colSpan={9} className="p-3 text-center text-[var(--fg-faint)]">Keine Trades in diesem Lauf.</td></tr>
                  )}
                </tbody>
              </table>
            </div>
          </Panel>
        </>
      )}
    </div>
  );
}

// ---- kleine UI-Helfer (Theme-konform) ----
function Panel({ children, className }: { children: React.ReactNode; className?: string }) {
  return <div className={`bg-[var(--panel)] border border-[var(--line)] rounded-lg p-3 ${className || ""}`}>{children}</div>;
}
function Head({ children }: { children: React.ReactNode }) {
  return <p className="text-xs mono text-[var(--fg-faint)] mb-2">{children}</p>;
}
function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (<label className="flex flex-col gap-1"><span className="text-[10px] mono text-[var(--fg-faint)] uppercase tracking-wider">{label}</span>{children}</label>);
}
function Input({ value, onChange, placeholder, type = "text" }: { value: string; onChange: (v: string) => void; placeholder?: string; type?: string }) {
  return <input type={type} value={value} placeholder={placeholder} onChange={(e) => onChange(e.target.value)}
    className="bg-[var(--bg-2)] border border-[var(--line)] rounded px-2 py-1 text-xs mono text-[var(--fg)] outline-none focus:border-[var(--key)]" />;
}
function Select({ value, onChange, options }: { value: string; onChange: (v: string) => void; options: { value: string; label: string }[] }) {
  return <select value={value} onChange={(e) => onChange(e.target.value)}
    className="bg-[var(--bg-2)] border border-[var(--line)] rounded px-2 py-1 text-xs mono text-[var(--fg)] outline-none focus:border-[var(--key)]">
    {options.map((o) => <option key={o.value} value={o.value}>{o.label}</option>)}
  </select>;
}
function Toggle({ checked, onChange }: { checked: boolean; onChange: (v: boolean) => void }) {
  return <button onClick={() => onChange(!checked)} className={`w-fit px-2 py-1 rounded text-xs mono border ${checked ? "border-[var(--key)] text-[var(--key)]" : "border-[var(--line)] text-[var(--fg-faint)]"}`}>{checked ? "an" : "aus"}</button>;
}
function Stat({ label, value, sub, cls }: { label: string; value: string; sub?: string; cls?: string }) {
  return (
    <div className="bg-[var(--panel)] border border-[var(--line)] rounded-lg p-3">
      <p className="text-[10px] mono text-[var(--fg-faint)] uppercase tracking-wider">{label}</p>
      <p className={`text-lg mono font-bold mt-0.5 ${cls || "text-[var(--fg)]"}`}>{value}</p>
      {sub && <p className="text-[9px] mono text-[var(--fg-faint)] mt-0.5">{sub}</p>}
    </div>
  );
}
