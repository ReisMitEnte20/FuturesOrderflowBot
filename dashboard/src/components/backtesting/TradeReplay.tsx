// Einzeltrade-Prüfung im vorhandenen Bar-Replay: Kerzen erscheinen Schritt für Schritt; Signal, Einstieg und Ausstieg
// werden erst sichtbar, wenn das Replay das jeweilige Ereignis erreicht hat (keine späteren Informationen vorab).
import { useEffect, useMemo, useState } from "react";
import { BacktestChart } from "@/components/backtest/BacktestChart";
import { backtestApi, type Candle, type OhlcTrade, type RunRequest } from "@/lib/backtestApi";
import { chartTradesAt, clampPos } from "@/lib/replay";
import { money, dt, type TradeRow } from "@/lib/backtestingApi";

const LEAD = 30;

export function toOhlcTrade(t: TradeRow, symbol: string, quantity: number): OhlcTrade {
  return {
    symbol, side: t.side, quantity, entryTime: new Date(t.entryT).toISOString(), exitTime: new Date(t.exitT).toISOString(),
    entryPrice: t.entryPrice, exitPrice: t.exitPrice, entryBarIndex: t.entryBar, exitBarIndex: t.exitBar,
    grossPnL: t.gross, fees: t.fees, netPnL: t.net, exitReason: t.exitReason as OhlcTrade["exitReason"],
    stopLossPrice: t.stopLoss, takeProfitPrice: t.takeProfit, ambiguous: t.ambiguous,
  };
}

/** MAE/MFE bis zur Replay-Position — nur aus Kerzen, die bis dahin VOLLSTÄNDIG im Trade lagen (gleiche Regel wie im Backend). */
export function excursionSoFar(candles: Candle[], t: TradeRow, pos: number, pointValue: number, qty: number): { mae: number; mfe: number; bars: number } | null {
  if (pos < t.entryBar) return null;
  const sign = t.side === "Long" ? 1 : -1;
  // Ausstieg zum Kerzenschluss (Datenende/Session-Schluss) ⇒ die Ausstiegskerze lag vollständig im Trade.
  const exitAtClose = t.exitBar < candles.length && (t.exitReason === "EndOfData" || t.exitReason === "SessionClose") && t.exitT > candles[t.exitBar].t;
  const lastFull = Math.min(pos, exitAtClose ? t.exitBar : t.exitBar - 1, candles.length - 1);
  let worst = 0, best = 0, bars = 0;
  for (let i = t.entryBar; i <= lastFull; i++) {
    const c = candles[i]; bars++;
    worst = Math.min(worst, (c.l - t.entryPrice) * sign, (c.h - t.entryPrice) * sign);
    best = Math.max(best, (c.l - t.entryPrice) * sign, (c.h - t.entryPrice) * sign);
  }
  return { mae: -worst * pointValue * qty, mfe: best * pointValue * qty, bars };
}

export function TradeReplay({ request, trades, focus, pointValue, onClose }: {
  request: RunRequest; trades: TradeRow[]; focus: number; pointValue: number; onClose: () => void;
}) {
  const [candles, setCandles] = useState<Candle[]>([]);
  const [err, setErr] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const t = trades[focus];
  const [pos, setPos] = useState(Math.max(0, (t?.entryBar ?? 0) - LEAD));

  useEffect(() => {
    const ac = new AbortController();
    setLoading(true);
    backtestApi.candles({ ...request, maxRows: request.maxRows || 100_000_000 }, ac.signal)
      .then((r) => { if (r.ok) setCandles(r.candles); else setErr(r.error ?? "Kerzen konnten nicht geladen werden."); })
      .catch((e) => { if (!ac.signal.aborted) setErr(String(e)); })
      .finally(() => setLoading(false));
    return () => ac.abort();
  }, [request]);

  useEffect(() => { if (t) setPos(Math.max(0, t.entryBar - LEAD)); }, [focus]); // eslint-disable-line react-hooks/exhaustive-deps

  const ohlc = useMemo(() => trades.map((x) => toOhlcTrade(x, request.symbol, request.quantity)), [trades, request.symbol, request.quantity]);
  const p = clampPos(pos, candles.length);
  const visible = useMemo(() => candles.slice(0, p + 1), [candles, p]);
  const chartTrades = useMemo(() => chartTradesAt(ohlc, p).filter((x) => x.index === focus), [ohlc, p, focus]);
  if (!t) return null;

  const signalReached = p >= t.entryBar - 1;
  const entryReached = p >= t.entryBar;
  const exitReached = p >= t.exitBar;
  const exc = excursionSoFar(candles, t, p, pointValue, request.quantity);
  const step = (d: number) => setPos((x) => clampPos(x + d, candles.length));

  return (
    <div className="rounded-lg border border-[var(--line-2)] bg-[var(--bg-2)] p-3 space-y-2">
      <div className="flex flex-wrap items-center gap-2 text-[11px]">
        <span className="rounded bg-[var(--panel-3)] px-1.5 py-0.5 font-semibold uppercase tracking-wide text-[var(--fg-dim)]">Bar-Replay · Trade #{focus + 1}</span>
        <span className="text-[var(--fg-faint)]">Kerze {p + 1} / {candles.length} · {visible.length ? dt(visible[visible.length - 1].t) : "–"} UTC</span>
        <div className="ml-auto flex gap-1">
          <button className="h-7 px-2 rounded border border-[var(--line-2)] text-[var(--fg)]" onClick={() => setPos(Math.max(0, t.entryBar - LEAD))}>⏮ Vor Signal</button>
          <button className="h-7 px-2 rounded border border-[var(--line-2)] text-[var(--fg)]" onClick={() => step(-1)}>◀ 1</button>
          <button className="h-7 px-2 rounded border border-[var(--line-2)] text-[var(--fg)]" onClick={() => step(1)}>1 ▶</button>
          <button className="h-7 px-2 rounded border border-[var(--line-2)] text-[var(--fg)]" onClick={() => step(5)}>5 ▶▶</button>
          <button className="h-7 px-2 rounded border border-[var(--line-2)] text-[var(--fg)]" onClick={onClose}>Schließen</button>
        </div>
      </div>
      <div className="grid grid-cols-2 md:grid-cols-5 gap-2 text-[11px] mono">
        <Ev label="Signal" on={signalReached} text={signalReached ? `Schluss Kerze ${t.entryBar} (${dt(candles[t.entryBar - 1]?.t)})` : "noch nicht erreicht"} />
        <Ev label="Einstieg" on={entryReached} text={entryReached ? `${t.side} @ ${t.entryPrice.toFixed(2)} (Open Kerze ${t.entryBar + 1})` : "noch nicht erreicht"} />
        <Ev label="Ausstieg" on={exitReached} text={exitReached ? `${t.exitReason} @ ${t.exitPrice.toFixed(2)} · Netto ${money(t.net)}` : "noch nicht erreicht"} />
        <Ev label="MAE bisher (Schätzung)" on={!!exc} text={exc ? `${money(exc.mae)} (${exc.bars} volle Kerzen)` : "–"} />
        <Ev label="MFE bisher (Schätzung)" on={!!exc} text={exc ? `${money(exc.mfe)}` : "–"} />
      </div>
      {exitReached && (
        <div className="text-[11px] text-[var(--fg-dim)]">
          Schätzung aus Kerzen, ganzer Trade (Backend): MAE {money(t.mae)} · MFE {money(t.mfe)}{t.excursionLowerBound ? " — Untergrenze" : ""} · {t.excursionBasis}
        </div>
      )}
      <BacktestChart
        candles={visible}
        trades={chartTrades}
        selected={null}
        focusKey={0}
        follow={90}
        openLevels={entryReached && !exitReached ? { fromBar: t.entryBar, toBar: p, sl: t.stopLoss, tp: t.takeProfit } : null}
        status={loading ? { kind: "loading", text: "Kerzen werden geladen (aus dem Zwischenspeicher des Auftrags) …" } : err ? { kind: "error", text: err } : null}
        className="h-[360px]"
      />
      <p className="text-[10px] text-[var(--fg-faint)]">
        Je Schritt eine vollständige Kerze; Marker und Werte erscheinen erst, wenn das Ereignis erreicht ist. MAE/MFE nutzen nur vollständig gehaltene
        Kerzen — nie das Hoch/Tief einer nur teilweise gehaltenen Kerze.
      </p>
    </div>
  );
}

function Ev({ label, on, text }: { label: string; on: boolean; text: string }) {
  return (
    <div className={`rounded border px-2 py-1 ${on ? "border-[var(--line-2)] text-[var(--fg)]" : "border-[var(--line)] text-[var(--fg-faint)]"}`}>
      <div className="text-[9px] uppercase tracking-wide text-[var(--fg-faint)]">{label}</div>{text}
    </div>
  );
}
