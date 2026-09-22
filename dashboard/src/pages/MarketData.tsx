import { useMemo } from "react";
import { useTradingStore } from "@/stores/tradingStore";
import { Card } from "@/components/common/Card";
import { CandlestickChart } from "@/components/charts/CandlestickChart";
import { EmptyState } from "@/components/common/EmptyState";
import { formatCurrency, formatNumber } from "@/lib/utils";

export function MarketData() {
  const candles = useTradingStore((s) => s.candles);
  const orderFlow = useTradingStore((s) => s.orderFlow);
  const selectedSymbol = useTradingStore((s) => s.selectedSymbol);

  const chartCandles = useMemo(() => {
    return candles.slice(-100).map((c) => ({
      timestamp: c.timestamp,
      open: c.open,
      close: c.close,
      low: c.low,
      high: c.high,
    }));
  }, [candles]);

  const latest = orderFlow[orderFlow.length - 1];
  const totalBuy = useMemo(() => orderFlow.reduce((s, o) => s + o.aggressiveBuyVolume, 0), [orderFlow]);
  const totalSell = useMemo(() => orderFlow.reduce((s, o) => s + o.aggressiveSellVolume, 0), [orderFlow]);
  const totalDelta = totalBuy - totalSell;

  return (
    <div className="p-4 space-y-4">
      <div className="flex items-center gap-4">
        <span className="font-mono text-lg font-bold">{selectedSymbol}</span>
        {latest && (
          <>
            <span className="font-mono text-2xl font-bold">{formatCurrency(latest.price)}</span>
            <span className={`text-sm font-mono ${totalDelta >= 0 ? "text-[var(--key)]" : "text-[var(--red)]"}`}>
              Δ {formatNumber(totalDelta)}
            </span>
          </>
        )}
      </div>

      <div className="grid grid-cols-4 gap-4">
        {[
          { label: "Buy Vol", value: formatNumber(totalBuy), color: "text-[var(--key)]" },
          { label: "Sell Vol", value: formatNumber(totalSell), color: "text-[var(--red)]" },
          { label: "Delta", value: formatNumber(totalDelta), color: totalDelta >= 0 ? "text-[var(--key)]" : "text-[var(--red)]" },
          { label: "CVD", value: formatNumber(latest?.cvd || 0), color: "text-[var(--cyan)]" },
        ].map(({ label, value, color }) => (
          <Card key={label}>
            <p className="text-[10px] font-mono text-[var(--fg-faint)] uppercase tracking-wider">{label}</p>
            <p className={`text-lg font-mono font-bold mt-1 ${color}`}>{value}</p>
          </Card>
        ))}
      </div>

      <Card>
        <SectionHeader title={`${selectedSymbol} Candles`} />
        {chartCandles.length > 0 ? (
          <CandlestickChart data={chartCandles} height={400} />
        ) : (
          <EmptyState title="No candle data" />
        )}
      </Card>
    </div>
  );
}

function SectionHeader({ title }: { title: string }) {
  return (
    <div className="mb-4">
      <h2 className="text-sm font-mono font-medium">{title}</h2>
    </div>
  );
}