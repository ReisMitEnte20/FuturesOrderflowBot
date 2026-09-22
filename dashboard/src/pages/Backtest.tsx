import { useMemo } from "react";
import { useTradingStore } from "@/stores/tradingStore";
import { Card } from "@/components/common/Card";
import { EmptyState } from "@/components/common/EmptyState";
import { formatCurrency, formatPercent } from "@/lib/utils";

export function Backtest() {
  const trades = useTradingStore((s) => s.trades);

  const totals = useMemo(() => {
    const pnl = trades.reduce((sum, t) => sum + (t.pnl || 0), 0);
    const wins = trades.filter((t) => (t.pnl || 0) > 0).length;
    return {
      totalPnL: pnl,
      winRate: trades.length > 0 ? (wins / trades.length) * 100 : 0,
      totalTrades: trades.length,
    };
  }, [trades]);

  return (
    <div className="p-4 space-y-4">
      <div className="grid grid-cols-4 gap-4">
        <Card>
          <p className="text-[10px] font-mono text-[var(--fg-faint)] uppercase">Total Return</p>
          <p className={`text-2xl font-mono font-bold mt-1 ${getColor(totals.totalPnL)}`}>
            {formatPercent(totals.totalPnL)}
          </p>
        </Card>
        <Card>
          <p className="text-[10px] font-mono text-[var(--fg-faint)] uppercase">Win Rate</p>
          <p className="text-2xl font-mono font-bold mt-1">{formatPercent(totals.winRate)}</p>
        </Card>
        <Card>
          <p className="text-[10px] font-mono text-[var(--fg-faint)] uppercase">Total Trades</p>
          <p className="text-2xl font-mono font-bold mt-1">{totals.totalTrades}</p>
        </Card>
        <Card>
          <p className="text-[10px] font-mono text-[var(--fg-faint)] uppercase">PnL</p>
          <p className={`text-2xl font-mono font-bold mt-1 ${getColor(totals.totalPnL)}`}>
            {formatCurrency(totals.totalPnL)}
          </p>
        </Card>
      </div>

      <Card>
        <p className="text-xs font-mono text-[var(--fg-faint)] mb-4">Trade History</p>
        {trades.length > 0 ? (
          <div className="space-y-2 max-h-[400px] overflow-y-auto">
            {trades.map((t) => (
              <div key={t.id} className="flex items-center justify-between p-3 rounded border border-[var(--line)] bg-[var(--bg-2)]">
                <div className="flex items-center gap-3">
                  <span className="font-mono text-sm">{t.symbol}</span>
                  <span className={`text-xs ${(t.pnl || 0) >= 0 ? "text-[var(--key)]" : "text-[var(--red)]"}`}>
                    {(t.side || "buy").toUpperCase()} {t.quantity}
                  </span>
                  <span className="text-[10px] text-[var(--fg-faint)]">{t.strategy}</span>
                </div>
                <span className={`font-mono text-sm ${getColor(t.pnl || 0)}`}>
                  {formatCurrency(t.pnl || 0)}
                </span>
              </div>
            ))}
          </div>
        ) : (
          <EmptyState title="No backtest data" description="Run a backtest to see results" />
        )}
      </Card>
    </div>
  );
}

function getColor(pnl: number) {
  return pnl >= 0 ? "text-[var(--key)]" : "text-[var(--red)]";
}