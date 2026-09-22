import { Card, CardHeader } from "@/components/common/Card";
import { Badge } from "@/components/common/Badge";
import { Table } from "@/components/common/Table";
import type { Position } from "@/types";
import { formatCurrency, getPnLColor } from "@/lib/utils";
import { Activity } from "lucide-react";

interface PositionTableProps {
  positions: Position[];
}

export function PositionTable({ positions }: PositionTableProps) {
  return (
    <Card>
      <CardHeader className="flex items-center justify-between">
        <div className="flex items-center gap-2">
          <Activity className="h-4 w-4" />
          <h2 className="font-mono text-sm font-medium">Positions</h2>
        </div>
        <span className="text-[10px] font-mono text-[var(--fg-faint)]">{positions.length} open</span>
      </CardHeader>
      <div className="p-4 pt-0">
        <Table
          columns={[
            { key: "symbol", header: "Symbol", className: "font-mono font-medium" },
            {
              key: "side",
              header: "Side",
              render: (_v, _i, row: Position) => (
                <Badge variant={row.side === "long" ? "success" : "danger"}>
                  {row.side.toUpperCase()}
                </Badge>
              ),
            },
            { key: "quantity", header: "Qty", className: "text-right font-mono" },
            { key: "entryPrice", header: "Entry", className: "text-right font-mono" },
            { key: "currentPrice", header: "Mark", className: "text-right font-mono" },
            {
              key: "unrealizedPnL",
              header: "PnL",
              className: "text-right font-mono font-medium",
              render: (_v, _i, row: Position) => {
                const pnl = row.unrealizedPnL;
                return <span className={getPnLColor(pnl)}>{formatCurrency(pnl)}</span>;
              },
            },
            {
              key: "strategy",
              header: "Strategy",
              render: (_v, _i, row: Position) => row.strategy || "—",
            },
          ]}
          data={positions}
          keyField="id"
        />
        </div>
      </Card>
  );
}