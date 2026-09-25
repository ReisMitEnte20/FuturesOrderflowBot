import { useMemo } from "react";
import { useTradingStore } from "@/stores/tradingStore";
import { Card } from "@/components/common/Card";
import { Badge } from "@/components/common/Badge";
import { Table } from "@/components/common/Table";
import type { Position } from "@/types";
import { formatCurrency, getPnLColor } from "@/lib/utils";
import { TrendingUp, Activity, Zap } from "lucide-react";

export function TradingDesk() {
  const positions = useTradingStore((s) => s.positions);
  const orders = useTradingStore((s) => s.orders);
  const signals = useTradingStore((s) => s.signals);
  const trades = useTradingStore((s) => s.trades);

  const totals = useMemo(() => {
    const unrealized = positions.reduce((sum, p) => sum + (p.unrealizedPnL || 0), 0);
    const realized = trades.reduce((sum, t) => sum + (t.pnl || 0), 0);
    return { unrealized, realized, openPositions: positions.length };
  }, [positions, trades]);

  return (
    <div className="p-4 space-y-4">
      <div className="grid grid-cols-3 gap-4">
        <Card className="border-l-2 border-l-[var(--key)]">
          <p className="text-[10px] font-mono text-[var(--fg-faint)] uppercase tracking-wider">Unrealized PnL</p>
          <p className={`text-2xl font-mono font-bold mt-1 ${getPnLColor(totals.unrealized)}`}>
            {formatCurrency(totals.unrealized)}
          </p>
        </Card>
        <Card className="border-l-2 border-l-[var(--cyan)]">
          <p className="text-[10px] font-mono text-[var(--fg-faint)] uppercase tracking-wider">Realized PnL</p>
          <p className={`text-2xl font-mono font-bold mt-1 ${getPnLColor(totals.realized)}`}>
            {formatCurrency(totals.realized)}
          </p>
        </Card>
        <Card className="border-l-2 border-l-[var(--gold)]">
          <p className="text-[10px] font-mono text-[var(--fg-faint)] uppercase tracking-wider">Open Positions</p>
          <p className="text-2xl font-mono font-bold mt-1">{totals.openPositions}</p>
        </Card>
      </div>

      <div className="flex items-center gap-2">
        <span className="text-sm text-muted-foreground">Symbol:</span>
        {["NQ", "MNQ", "ES", "MES"].map((sym) => (
          <button
            key={sym}
            onClick={() => useTradingStore.getState().setSelectedSymbol(sym)}
            className={`px-3 py-1 rounded-md text-sm font-medium border transition-colors ${
              false
                ? "bg-primary text-primary-foreground border-primary"
                : "bg-card text-foreground border-border hover:bg-muted"
            }`}
          >
            {sym}
          </button>
        ))}
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-3 gap-4">
        <Card className="lg:col-span-2">
          <SectionHeader title="Positions" icon={<Activity className="h-4 w-4" />} />
          <PositionTable positions={positions} />
        </Card>

        <Card>
          <SectionHeader title="Signals" icon={<Zap className="h-4 w-4" />} />
          <SignalList signals={signals} />
        </Card>
      </div>

      <Card>
        <SectionHeader title="Orders" icon={<TrendingUp className="h-4 w-4" />} />
        <OrderTable orders={orders} />
      </Card>
    </div>
  );
}

function SectionHeader({ title, icon }: { title: string; icon: React.ReactNode }) {
  return (
    <div className="flex items-center justify-between mb-4">
      <div className="flex items-center gap-2">
        <span className="text-[var(--fg)]">{icon}</span>
        <h2 className="text-sm font-mono font-medium text-[var(--fg)]">{title}</h2>
      </div>
    </div>
  );
}

function PositionTable({ positions }: { positions: Position[] }) {
  if (!positions.length) return <p className="text-sm text-[var(--fg-faint)] py-8 text-center">No open positions</p>;
  return (
    <Table<Position>
      columns={[
        { key: "symbol", header: "Symbol", className: "font-mono" },
        { key: "side", header: "Side", render: (_v, _i, row) => <Badge variant={row.side === "long" ? "success" : "danger"}>{row.side.toUpperCase()}</Badge> },
        { key: "quantity", header: "Qty", className: "text-right font-mono" },
        { key: "entryPrice", header: "Entry", className: "text-right font-mono" },
        { key: "currentPrice", header: "Mark", className: "text-right font-mono" },
        { key: "unrealizedPnL", header: "PnL", className: "text-right font-mono font-medium", render: (_v, _i, row) => <span className={getPnLColor(row.unrealizedPnL)}>{formatCurrency(row.unrealizedPnL)}</span> },
        { key: "strategy", header: "Strategy", render: (_v, _i, row) => row.strategy || "—" },
      ]}
      data={positions}
      keyField="id"
    />
  );
}

function SignalList({ signals }: { signals: any[] }) {
  if (!signals.length) return <p className="text-sm text-[var(--fg-faint)] py-8 text-center">No signals</p>;
  return (
    <div className="space-y-2 max-h-[300px] overflow-y-auto">
      {signals.slice(0, 8).map((s) => (
        <div key={s.signalId} className="p-2 rounded border border-[var(--line)] bg-[var(--bg-2)]">
          <div className="flex items-center justify-between">
            <span className="font-mono text-xs">{s.symbol}</span>
            <Badge variant={s.direction === "long" ? "success" : "danger"}>{s.direction.toUpperCase()}</Badge>
          </div>
          <p className="text-[11px] text-[var(--fg-dim)] mt-1">{s.reason}</p>
          <div className="flex items-center justify-between mt-1">
            <span className="text-[10px] text-[var(--fg-faint)]">{s.strategyName}</span>
            <span className="text-[10px] font-mono text-[var(--fg-dim)]">{(s.confidence * 100).toFixed(0)}%</span>
          </div>
        </div>
      ))}
    </div>
  );
}

function OrderTable({ orders }: { orders: any[] }) {
  if (!orders.length) return <p className="text-sm text-[var(--fg-faint)] py-8 text-center">No orders</p>;
  return (
    <Table
      columns={[
        { key: "symbol", header: "Symbol", className: "font-mono" },
        { key: "side", header: "Side", className: "font-mono" },
        { key: "type", header: "Type", className: "font-mono" },
        { key: "quantity", header: "Qty", className: "text-right font-mono" },
        { key: "status", header: "Status" },
      ]}
      data={orders}
      keyField="id"
    />
  );
}