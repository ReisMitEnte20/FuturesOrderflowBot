import { useEffect, useMemo, useState } from "react";
import { BacktestChart } from "@/components/backtest/BacktestChart";
import { FootprintChart, type FpBar } from "@/components/research/FootprintChart";
import type { ChartTrade } from "@/lib/replay";
import type { Candle } from "@/lib/backtestApi";

// Trade-Vorschau auf AUFGENOMMENEN Daten (ATAS-Replay -> Ticks -> OrderFlowBars).
// Technischer Test: natives Footprint/DOM NICHT verwendet; Footprint rueckblickend aus Ticks; Granularitaet ungeklaert.
// Keine Neuberechnung der Strategie, keine Veraenderung der Trades (reine Anzeige der gespeicherten Ergebnisse).
// Ansicht umschaltbar: Kerzen (BacktestChart) oder Bid/Ask-Footprint pro Kerze (FootprintChart).

interface PvTrade {
  index: number; side: string; entryBarIndex: number; exitBarIndex: number;
  entryPrice: number; exitPrice: number; exitReason: string; ambiguous: boolean;
  netPnL: number; entryTime: string; exitTime: string;
}
interface PvStrat { name: string; usesOrderflow: boolean; netPnL: number; fees: number; finalEquity: number; trades: PvTrade[]; }
interface FpLevel { p: number; b: number; a: number; }
interface Preview {
  symbol: string; priceGranularityNote: string; footprintNote: string; caveats: string[];
  config: { ticksPerBar: number; pointValue: number; feePerSide: number; slippageTicks: number };
  candles: (Candle & { delta: number })[]; footprint: FpLevel[][]; strategies: PvStrat[];
}

const KEY = "#3cf0a0", RED = "#f0566a";
const eur = (n: number) => (n < 0 ? "−" : "") + "$" + Math.abs(n).toFixed(2);
const sideCol = (s: string) => (s === "Long" ? KEY : RED);

export function TradePreview() {
  const [data, setData] = useState<Preview | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [stratIdx, setStratIdx] = useState(1); // DeltaFlow(OF) als Default (nutzt Orderflow)
  const [selected, setSelected] = useState<number | null>(null);
  const [focusKey, setFocusKey] = useState(0);
  const [view, setView] = useState<"candles" | "footprint">("candles");

  useEffect(() => {
    let ok = true;
    fetch("/trade-preview.json")
      .then((r) => (r.ok ? r.json() : Promise.reject(`HTTP ${r.status}`)))
      .then((d: Preview) => { if (ok) { const of = d.strategies.findIndex((s) => s.usesOrderflow); setData(d); setStratIdx(of >= 0 ? of : 0); setSelected(0); setFocusKey((k) => k + 1); } })
      .catch((e) => ok && setErr(String(e)));
    return () => { ok = false; };
  }, []);

  const strat = data?.strategies[stratIdx];
  const candles: Candle[] = useMemo(() => (data ? data.candles.map((c) => ({ t: c.t, o: c.o, h: c.h, l: c.l, c: c.c, v: c.v })) : []), [data]);
  const fpBars: FpBar[] = useMemo(() => (data ? data.candles.map((c, i) => ({ candle: c, levels: data.footprint[i] ?? [] })) : []), [data]);
  const chartTrades: ChartTrade[] = useMemo(
    () => (strat ? strat.trades.map((t) => ({ index: t.index, side: t.side, entryBarIndex: t.entryBarIndex, entryPrice: t.entryPrice, exit: { barIndex: t.exitBarIndex, price: t.exitPrice, reason: t.exitReason, ambiguous: t.ambiguous }, stopLossPrice: 0, takeProfitPrice: 0 })) : []),
    [strat],
  );
  const pick = (i: number | null) => { setSelected(i); setFocusKey((k) => k + 1); };
  const selTrade = strat && selected != null ? strat.trades.find((t) => t.index === selected) ?? null : null;

  if (err) return <Shell><p className="text-xs text-[var(--red)] mono">trade-preview.json nicht geladen: {err}. (Harness erzeugt dashboard/public/trade-preview.json)</p></Shell>;
  if (!data || !strat) return <Shell><p className="text-xs text-[var(--fg-faint)]">Lade Trade-Vorschau…</p></Shell>;

  return (
    <Shell>
      <div className="flex flex-wrap items-center gap-2 mb-2">
        <span className="text-sm font-medium text-[var(--fg)]">Trade-Vorschau · {data.symbol} (aufgenommene Daten)</span>
        <span className="text-[10px] px-1.5 py-0.5 rounded bg-[rgba(240,195,90,.14)] text-[var(--gold,#f0c35a)] border border-[rgba(240,195,90,.4)]">TECHNISCHER TEST · keine Profitabilitätsaussage</span>
        <div className="ml-auto flex items-center gap-1">
          {data.strategies.map((s, i) => (
            <button key={s.name} onClick={() => { setStratIdx(i); setSelected(0); setFocusKey((k) => k + 1); }}
              className={`h-[26px] px-2.5 rounded-md text-[11px] border ${i === stratIdx ? "bg-[var(--key)] text-black border-transparent font-semibold" : "border-[var(--line-2)] text-[var(--fg)]"}`}>
              {s.name}{s.usesOrderflow ? " ·OF" : ""}
            </button>
          ))}
        </div>
      </div>

      <div className="text-[10.5px] text-[var(--fg-faint)] mb-2 leading-snug">
        {data.footprintNote} · {data.priceGranularityNote} · Netto {eur(strat.netPnL)} · Gebühren ${strat.fees.toFixed(2)} · End-Equity {eur(strat.finalEquity)}
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-[260px_1fr] gap-3">
        {/* Trade-Liste */}
        <div className="border border-[var(--line)] rounded-lg overflow-hidden">
          <div className="px-2 py-1.5 text-[10px] uppercase tracking-wide text-[var(--fg-faint)] border-b border-[var(--line)] bg-[var(--bg-2)]">Trades ({strat.trades.length}) · klick fokussiert</div>
          <div className="max-h-[380px] overflow-y-auto">
            {strat.trades.map((t) => (
              <button key={t.index} onClick={() => pick(t.index)}
                className={`w-full flex items-center gap-2 px-2 py-1.5 text-[11px] border-b border-[var(--line)] mono text-left ${selected === t.index ? "bg-[rgba(34,184,240,0.12)]" : "hover:bg-[var(--bg-2)]"}`}>
                <span className="text-[var(--fg-faint)] w-5">{t.index + 1}</span>
                <span style={{ color: sideCol(t.side) }} className="w-10 font-semibold">{t.side === "Long" ? "LONG" : "SHORT"}</span>
                <span className="text-[var(--fg)] flex-1">{t.entryTime}→{t.exitTime}{t.exitReason === "EndOfData" ? " ·EoD" : ""}</span>
                <span style={{ color: t.netPnL >= 0 ? KEY : RED }} className="w-16 text-right">{eur(t.netPnL)}</span>
              </button>
            ))}
          </div>
        </div>

        {/* Chart: Umschaltung Kerzen / Footprint */}
        <div className="min-w-0">
          <div className="flex items-center gap-2 mb-1">
            {selTrade ? (
              <span className="text-[11px] mono text-[var(--fg)]">
                #{selTrade.index + 1} <b style={{ color: sideCol(selTrade.side) }}>{selTrade.side}</b> · Bar {selTrade.entryBarIndex}→{selTrade.exitBarIndex} · Entry {selTrade.entryPrice.toFixed(2)} · Exit {selTrade.exitPrice.toFixed(2)} · <span style={{ color: selTrade.netPnL >= 0 ? KEY : RED }}>{eur(selTrade.netPnL)}</span>
              </span>
            ) : <span className="text-[11px] text-[var(--fg-faint)]">Trade links wählen</span>}
            <div className="ml-auto inline-flex rounded-md border border-[var(--line-2)] overflow-hidden">
              <button onClick={() => setView("candles")} className={`h-[24px] px-3 text-[10.5px] ${view === "candles" ? "bg-[var(--key)] text-black font-semibold" : "text-[var(--fg)]"}`}>Kerzen</button>
              <button onClick={() => setView("footprint")} className={`h-[24px] px-3 text-[10.5px] border-l border-[var(--line-2)] ${view === "footprint" ? "bg-[var(--key)] text-black font-semibold" : "text-[var(--fg)]"}`}>Footprint</button>
            </div>
          </div>

          <div className="relative h-[380px] border border-[var(--line)] rounded-lg overflow-hidden">
            {view === "candles"
              ? <BacktestChart candles={candles} trades={chartTrades} selected={selected} focusKey={focusKey} onSelectTrade={pick} className="h-full w-full" />
              : <FootprintChart bars={fpBars} trades={chartTrades} selected={selected} focusKey={focusKey} onSelectTrade={pick} className="h-full w-full" />}
          </div>

          <div className="text-[10px] text-[var(--fg-faint)] mt-1">
            {view === "footprint"
              ? <>Footprint <b>pro Kerze</b>: je Preislevel <span style={{ color: RED }}>Bid links</span> / <span style={{ color: KEY }}>Ask rechts</span> (Intensität = Volumen); Δ und Volumen je Bar an der X‑Achse. <b>Keine Summierung</b> über das Trade‑Fenster. Reinzoomen → Zahlen; rauszoomen → nur Intensitäts‑Zellen (nichts überlappt). Tooltip zeigt alle Preislevel‑Werte.</>
              : <>Kerzen = OrderFlowBars (Σ Volumen/Delta pro Bar) mit Entry/Exit‑Markern. Umschalten auf „Footprint" zeigt Bid/Ask je Preis <b>pro Kerze</b>.</>}
            {" "}Werte <b>rückblickend</b> (finale Ticks); Preisgranularität 1‑Punkt (MNQ real 0,25 ungeklärt) — keine Viertelpunkte erfunden.
          </div>
        </div>
      </div>
    </Shell>
  );
}

function Shell({ children }: { children: React.ReactNode }) {
  return <div className="rounded-xl border border-[var(--line)] bg-[var(--panel)] p-3">{children}</div>;
}
