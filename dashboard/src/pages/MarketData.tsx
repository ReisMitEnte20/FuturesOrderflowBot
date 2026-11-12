import { useMemo } from "react";
import { useTradingStore } from "@/stores/tradingStore";
import { Card } from "@/components/common/Card";
import { StatTile } from "@/components/common/StatTile";
import { CandlestickChart } from "@/components/charts/CandlestickChart";
import { EmptyState } from "@/components/common/EmptyState";
import { formatNumber } from "@/lib/utils";
import { useRithmicMarketData } from "@/lib/rithmicMarketData";
import { TradeTape } from "@/components/trading/TradeTape";

const ROOTS = ["MES", "ES", "MNQ", "NQ"];

function formatPrice(value?: number | null) {
  return value === null || value === undefined ? "–" : value.toFixed(2);
}

function formatTime(iso: string) {
  return new Date(iso).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
}

export function MarketData() {
  const candles = useTradingStore((s) => s.candles);
  const orderFlow = useTradingStore((s) => s.orderFlow);
  const selectedSymbol = useTradingStore((s) => s.selectedSymbol);
  const setSelectedSymbol = useTradingStore((s) => s.setSelectedSymbol);
  const { connected, contract, quote, error, loading, exchange } = useRithmicMarketData(selectedSymbol);

  const chartCandles = useMemo(
    () => candles.slice(-240).map((c) => ({ ...c, timestamp: formatTime(c.timestamp) })),
    [candles],
  );

  const latest = orderFlow[orderFlow.length - 1];
  const totalBuy = useMemo(() => orderFlow.reduce((s, o) => s + o.aggressiveBuyVolume, 0), [orderFlow]);
  const totalSell = useMemo(() => orderFlow.reduce((s, o) => s + o.aggressiveSellVolume, 0), [orderFlow]);
  const totalDelta = totalBuy - totalSell;

  return (
    <div className="p-4 space-y-4">
      <div className="flex items-center gap-4 flex-wrap">
        <div className="flex gap-2">
          {ROOTS.map((r) => (
            <button
              key={r}
              onClick={() => setSelectedSymbol(r)}
              aria-pressed={r === selectedSymbol}
              className={`chip ${r === selectedSymbol ? "chip-active" : ""}`}
            >
              {r}
            </button>
          ))}
        </div>
        <span className="display text-lg">{contract ?? selectedSymbol}</span>
        <span className="font-mono text-xs text-[var(--fg-faint)]">{exchange} · Rithmic</span>
        {quote && (
          <span className="font-mono text-sm">
            <span className="text-[var(--red)]">{formatPrice(quote.bid)}</span>
            <span className="text-[var(--fg-faint)]"> ({quote.bidSize ?? "–"}) / </span>
            <span className="text-[var(--key)]">{formatPrice(quote.ask)}</span>
            <span className="text-[var(--fg-faint)]"> ({quote.askSize ?? "–"})</span>
            <span className="ml-3 text-2xl font-bold text-[var(--fg)]">{formatPrice(quote.lastPrice)}</span>
          </span>
        )}
        {latest && (
          <span className={`text-sm font-mono ${totalDelta >= 0 ? "text-[var(--key)]" : "text-[var(--red)]"}`}>
            Δ {formatNumber(totalDelta)}
          </span>
        )}
      </div>

      {!connected && (
        <Card className="px-4 py-2.5 flex items-center gap-2.5">
          <span className="dot dot-warn" />
          <p className="text-sm text-[var(--fg-dim)]">Rithmic nicht verbunden – oben rechts „Connect“.</p>
        </Card>
      )}
      {error && (
        <Card className="px-4 py-2.5 border-[rgba(240,86,106,0.4)]">
          <p className="text-sm font-mono text-[var(--red)]">{error}</p>
        </Card>
      )}

      <div className="grid grid-cols-4 gap-4">
        {[
          { label: "Ask-Vol (Buy-Aggr.)", value: formatNumber(totalBuy), color: "text-[var(--key)]", tone: "key" as const },
          { label: "Bid-Vol (Sell-Aggr.)", value: formatNumber(totalSell), color: "text-[var(--red)]", tone: "red" as const },
          { label: "Delta", value: formatNumber(totalDelta), color: totalDelta >= 0 ? "text-[var(--key)]" : "text-[var(--red)]", tone: totalDelta >= 0 ? ("key" as const) : ("red" as const) },
          { label: "CVD (Fenster)", value: formatNumber(latest?.cvd || 0), color: "text-[var(--cyan)]", tone: "cyan" as const },
        ].map(({ label, value, color, tone }) => (
          <StatTile key={label} label={label} value={value} accent={color} tone={tone} />
        ))}
      </div>

      <div className="grid grid-cols-1 xl:grid-cols-[1fr_340px] gap-4">
        <Card className="p-4">
          <div className="mb-4 flex items-center justify-between">
            <h2 className="display text-sm">{contract ?? selectedSymbol} · 1-Minuten-Bars</h2>
            <span className="text-[10px] font-mono text-[var(--fg-faint)]">
              {candles.length > 0 ? `${candles.length} Bars · letzte ${formatTime(candles[candles.length - 1].timestamp)}` : ""}
            </span>
          </div>
          {chartCandles.length > 0 ? (
            <CandlestickChart data={chartCandles} height={420} />
          ) : (
            <EmptyState title={loading ? "Lade Rithmic-Daten…" : "No candle data"} />
          )}
        </Card>
        <TradeTape symbol={contract} exchange={exchange} enabled={connected && !!contract} />
      </div>
    </div>
  );
}
