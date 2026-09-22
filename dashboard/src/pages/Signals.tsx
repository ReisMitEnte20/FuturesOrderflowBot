import { useMemo, useState } from "react";
import { useTradingStore } from "@/stores/tradingStore";
import { Card } from "@/components/common/Card";
import { Badge } from "@/components/common/Badge";
import { EmptyState } from "@/components/common/EmptyState";
import { formatCurrency } from "@/lib/utils";
import { Filter, Zap } from "lucide-react";

export function Signals() {
  const signals = useTradingStore((s) => s.signals);
  const [filterDir, setFilterDir] = useState<string>("all");

  const filtered = useMemo(() => {
    return signals.filter((s) => filterDir === "all" || s.direction === filterDir);
  }, [signals, filterDir]);

  return (
    <div className="p-4 space-y-4">
      <div className="flex items-center gap-2 flex-wrap">
        <Filter className="h-4 w-4 text-[var(--fg-faint)]" />
        {["all", "long", "short"].map((dir) => (
          <button
            key={dir}
            onClick={() => setFilterDir(dir)}
            className={`px-3 py-1 rounded-md text-sm border transition-colors ${
              filterDir === dir ? "bg-[var(--key)]/20 text-[var(--key)] border-[var(--key)]/40" : "bg-[var(--panel)] border-[var(--line)] hover:bg-[var(--panel-2)]"
            }`}
          >
            {dir === "all" ? "All" : dir.toUpperCase()}
          </button>
        ))}
      </div>

      {filtered.length > 0 ? (
        <div className="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-3 gap-4">
          {filtered.map((signal) => (
            <Card key={signal.signalId} hover>
              <div className="p-4">
                <div className="flex items-center justify-between mb-3">
                  <span className="font-mono text-lg font-bold">{signal.symbol}</span>
                  <Badge variant={signal.direction === "long" ? "success" : "danger"} dot>
                    {signal.direction.toUpperCase()}
                  </Badge>
                </div>
                <div className="grid grid-cols-3 gap-2 mb-3 text-center">
                  <div>
                    <p className="text-[10px] text-[var(--fg-faint)] uppercase">Strategy</p>
                    <p className="text-xs font-medium truncate">{signal.strategyName}</p>
                  </div>
                  <div>
                    <p className="text-[10px] text-[var(--fg-faint)] uppercase">Conf</p>
                    <p className="text-xs font-medium">{(signal.confidence * 100).toFixed(0)}%</p>
                  </div>
                  <div>
                    <p className="text-[10px] text-[var(--fg-faint)] uppercase">Price</p>
                    <p className="text-xs font-medium font-mono">{formatCurrency(signal.referencePrice)}</p>
                  </div>
                </div>
                <p className="text-sm text-[var(--fg-dim)] mb-2">{signal.reason}</p>
                <div className="flex items-center justify-between">
                  <span className="text-[10px] text-[var(--fg-faint)]">
                    {new Date(signal.timestamp).toLocaleTimeString()}
                  </span>
                  <div className="flex gap-1">
                    {signal.suggestedQuantity && (
                      <Badge variant="info">qty: {signal.suggestedQuantity}</Badge>
                    )}
                  </div>
                </div>
              </div>
            </Card>
          ))}
        </div>
      ) : (
        <EmptyState title="No signals" description="Waiting for trade signals..." icon={<Zap className="h-8 w-8" />} />
      )}
    </div>
  );
}