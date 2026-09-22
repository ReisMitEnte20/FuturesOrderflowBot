import { Card, CardHeader, CardBody } from "@/components/common/Card";
import { Badge } from "@/components/common/Badge";
import { TradeSignal } from "@/types";
import { formatCurrency } from "@/lib/utils";
import { Zap } from "lucide-react";

interface SignalFeedProps {
  signals?: TradeSignal[];
  maxHeight?: number;
}

export function SignalFeed({ signals, maxHeight }: SignalFeedProps) {
  return (
    <Card>
      <CardHeader className="flex items-center justify-between">
        <div className="flex items-center gap-2">
          <Zap className="h-4 w-4" />
          <h2 className="font-mono text-sm font-medium">Signals</h2>
        </div>
        <span className="text-[10px] font-mono text-[var(--fg-faint)]">{signals?.length || 0} total</span>
      </CardHeader>
      <CardBody>
        <div className={`space-y-2 overflow-y-auto ${maxHeight ? `max-h-[${maxHeight}px]` : "max-h-[400px]"}`}>
          {(!signals || signals.length === 0) ? (
            <p className="text-sm text-[var(--fg-faint)] text-center py-8">No signals</p>
          ) : (
            signals.map((signal) => (
              <div
                key={signal.signalId}
                className="p-3 rounded border border-[var(--line)] bg-[var(--bg-2)] hover:border-[var(--line-2)] transition-colors"
              >
                <div className="flex items-center justify-between mb-2">
                  <span className="font-mono font-bold text-sm">{signal.symbol}</span>
                  <Badge variant={signal.direction === "long" ? "success" : "danger"} dot>
                    {signal.direction.toUpperCase()}
                  </Badge>
                </div>
                <p className="text-xs text-[var(--fg-dim)] mb-2 leading-relaxed">{signal.reason}</p>
                <div className="flex items-center justify-between text-[10px] font-mono">
                  <span className="text-[var(--fg-faint)]">{signal.strategyName}</span>
                  <div className="flex items-center gap-2">
                    <span className="text-[var(--gold)]">{(signal.confidence * 100).toFixed(0)}%</span>
                    <span className="text-[var(--fg-dim)]">
                      {signal.referencePrice !== undefined ? formatCurrency(signal.referencePrice) : ""}
                    </span>
                  </div>
                </div>
              </div>
            ))
          )}
        </div>
      </CardBody>
    </Card>
  );
}