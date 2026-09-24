import { useMemo, useState } from "react";
import { useTradingStore } from "@/stores/tradingStore";
import { Card, CardContent } from "@/components/common/Card";
import { Badge } from "@/components/common/Badge";
import { EmptyState } from "@/components/common/EmptyState";
import { formatCurrency } from "@/lib/utils";
import { Zap, Filter } from "lucide-react";

export function Signals() {
  const signals = useTradingStore((s) => s.signals);
  const [filterDir, setFilterDir] = useState<string>("all");
  const [filterSym, setFilterSym] = useState<string>("all");

  const symbols = useMemo(() => {
    const s = new Set(signals.map((sig) => sig.symbol));
    return Array.from(s);
  }, [signals]);

  const filtered = useMemo(() => {
    return signals.filter((s) => {
      if (filterDir !== "all" && s.direction !== filterDir) return false;
      if (filterSym !== "all" && s.symbol !== filterSym) return false;
      return true;
    });
  }, [signals, filterDir, filterSym]);

  return (
    <div className="p-4 space-y-4">
      <div className="flex items-center gap-2 flex-wrap">
        <Filter className="h-4 w-4 text-muted-foreground" />
        <button
          onClick={() => setFilterDir("all")}
          className={`px-3 py-1 rounded-md text-sm border transition-colors ${
            filterDir === "all" ? "bg-primary text-primary-foreground border-primary" : "bg-card border-border hover:bg-muted"
          }`}
        >
          All
        </button>
        <button
          onClick={() => setFilterDir("long")}
          className={`px-3 py-1 rounded-md text-sm border transition-colors ${
            filterDir === "long" ? "bg-success/20 text-success border-success/40" : "bg-card border-border hover:bg-muted"
          }`}
        >
          LONG
        </button>
        <button
          onClick={() => setFilterDir("short")}
          className={`px-3 py-1 rounded-md text-sm border transition-colors ${
            filterDir === "short" ? "bg-destructive/20 text-destructive border-destructive/40" : "bg-card border-border hover:bg-muted"
          }`}
        >
          SHORT
        </button>
        <div className="w-px h-6 bg-border mx-1" />
        <button
          onClick={() => setFilterSym("all")}
          className={`px-3 py-1 rounded-md text-sm border transition-colors ${
            filterSym === "all" ? "bg-primary text-primary-foreground border-primary" : "bg-card border-border hover:bg-muted"
          }`}
        >
          All Symbols
        </button>
        {symbols.map((sym) => (
          <button
            key={sym}
            onClick={() => setFilterSym(sym)}
            className={`px-3 py-1 rounded-md text-sm border transition-colors font-mono ${
              filterSym === sym ? "bg-primary text-primary-foreground border-primary" : "bg-card border-border hover:bg-muted"
            }`}
          >
            {sym}
          </button>
        ))}
      </div>

      {filtered.length > 0 ? (
        <div className="grid grid-cols-1 md:grid-cols-2 xl:grid-cols-3 gap-4">
          {filtered.map((signal) => (
            <Card key={signal.signalId} hover>
              <CardContent className="p-4">
                <div className="flex items-center justify-between mb-3">
                  <span className="font-mono text-lg font-bold">{signal.symbol}</span>
                  <Badge
                    variant={signal.direction === "long" ? "success" : "danger"}
                    dot
                  >
                    {signal.direction.toUpperCase()}
                  </Badge>
                </div>
                <div className="flex items-center gap-4 mb-3">
                  <div>
                    <span className="text-xs text-muted-foreground">Strategy</span>
                    <p className="text-sm font-medium">{signal.strategyName}</p>
                  </div>
                  <div>
                    <span className="text-xs text-muted-foreground">Confidence</span>
                    <p className="text-sm font-medium">{(signal.confidence * 100).toFixed(0)}%</p>
                  </div>
                  <div>
                    <span className="text-xs text-muted-foreground">Price</span>
                    <p className="text-sm font-medium font-mono">{formatCurrency(signal.referencePrice)}</p>
                  </div>
                </div>
                <p className="text-sm text-muted-foreground mb-3">{signal.reason}</p>
                {signal.suggestedQuantity && (
                  <div className="flex gap-2 text-xs">
                    <Badge variant="info">Qty: {signal.suggestedQuantity}</Badge>
                    {signal.suggestedStopLossTicks && (
                      <Badge variant="warning">SL: {signal.suggestedStopLossTicks}t</Badge>
                    )}
                    {signal.suggestedTakeProfitTicks && (
                      <Badge variant="success">TP: {signal.suggestedTakeProfitTicks}t</Badge>
                    )}
                  </div>
                )}
                <div className="mt-3 text-xs text-muted-foreground/60">
                  {new Date(signal.timestamp).toLocaleTimeString()}
                </div>
              </CardContent>
            </Card>
          ))}
        </div>
      ) : (
        <EmptyState
          title="No signals found"
          description={signals.length === 0 ? "Waiting for trade signals from strategies..." : "No signals match the selected filters"}
          icon={<Zap className="h-8 w-8" />}
        />
      )}
    </div>
  );
}