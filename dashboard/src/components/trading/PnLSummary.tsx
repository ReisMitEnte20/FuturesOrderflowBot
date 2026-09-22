import { useMemo } from "react";
import { useTradingStore } from "@/stores/tradingStore";
import { formatCurrency, getPnLColor } from "@/lib/utils";
import { TrendingUp, DollarSign, BarChart3 } from "lucide-react";

export function PnLSummary() {
  const positions = useTradingStore((s) => s.positions);
  const trades = useTradingStore((s) => s.trades);

  const stats = useMemo(() => {
    const unrealized = positions.reduce((sum, p) => sum + (p.unrealizedPnL || 0), 0);
    const realized = trades.reduce((sum, t) => sum + (t.pnl || 0), 0);
    return { unrealized, realized, total: unrealized + realized };
  }, [positions, trades]);

  return (
    <div className="grid grid-cols-3 gap-4">
      <StatCard
        label="Unrealized"
        value={formatCurrency(stats.unrealized)}
        icon={<TrendingUp className="h-4 w-4" />}
        color={getPnLColor(stats.unrealized)}
      />
      <StatCard
        label="Realized"
        value={formatCurrency(stats.realized)}
        icon={<DollarSign className="h-4 w-4" />}
        color={getPnLColor(stats.realized)}
      />
      <StatCard
        label="Total"
        value={formatCurrency(stats.total)}
        icon={<BarChart3 className="h-4 w-4" />}
        color={getPnLColor(stats.total)}
      />
    </div>
  );
}

function StatCard({
  label,
  value,
  icon,
  color,
}: {
  label: string;
  value: string;
  icon: React.ReactNode;
  color: string;
}) {
  return (
    <div className="bg-[var(--panel)] border border-[var(--line)] rounded-lg p-4">
      <div className="flex items-center justify-between mb-2">
        <span className="text-[10px] font-mono text-[var(--fg-faint)] uppercase tracking-wider">{label}</span>
        <span className="text-[var(--fg-faint)]">{icon}</span>
      </div>
      <p className={`text-2xl font-mono font-bold ${color}`}>{value}</p>
    </div>
  );
}