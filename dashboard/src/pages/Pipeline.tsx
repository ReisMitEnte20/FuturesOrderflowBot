import { Card } from "@/components/common/Card";
import { EmptyState } from "@/components/common/EmptyState";
import { Badge } from "@/components/common/Badge";
import { useTradingStore } from "@/stores/tradingStore";

export function Pipeline() {
  const pipeline = useTradingStore((s) => s.pipeline);
  const strategies = useTradingStore((s) => s.strategies);

  const nodes = pipeline?.nodes || [];
  const edges = pipeline?.edges || [];

  const typeColors: Record<string, string> = {
    source: "#a2e65d",
    indicator: "#7fc4b4",
    signal: "#e8c069",
    position: "#c2b6d8",
    pnl: "#c1503f",
  };

  const typeLabels: Record<string, string> = {
    source: "SOURCE",
    indicator: "FEATURE",
    signal: "SIGNAL",
    position: "POSITION",
    pnl: "PNL",
  };

  if (nodes.length === 0) {
    return (
      <div className="p-4">
        <EmptyState
          title="No pipeline"
          description="Define your strategy pipeline to visualize data flow"
        />
      </div>
    );
  }

  return (
    <div className="p-4 space-y-4">
      <div className="grid grid-cols-1 lg:grid-cols-3 gap-4">
        <Card className="lg:col-span-2 min-h-[400px]">
          <p className="text-xs font-mono text-[var(--fg-faint)] mb-4">Pipeline Graph</p>
          <div className="flex flex-wrap gap-3 items-center justify-center min-h-[300px]">
            {nodes.map((node) => (
              <PipelineNode key={node.id} node={node} color={typeColors[node.type] || "#a2e65d"} label={typeLabels[node.type] || node.type} />
            ))}
          </div>
        </Card>

        <Card>
          <p className="text-xs font-mono text-[var(--fg-faint)] mb-4">Strategies</p>
          {strategies.length > 0 ? (
            <div className="space-y-2">
              {strategies.map((s) => (
                <div key={s.id} className="p-3 rounded border border-[var(--line)] bg-[var(--bg-2)]">
                  <div className="flex items-center justify-between">
                    <span className="font-mono text-sm">{s.name}</span>
                    <Badge variant={s.enabled ? "success" : "neutral"}>
                      {s.enabled ? "ON" : "OFF"}
                    </Badge>
                  </div>
                  <div className="grid grid-cols-2 gap-2 mt-2 text-[10px]">
                    <div>
                      <span className="text-[var(--fg-faint)]">Trades: </span>
                      <span className="font-mono">{s.performance.totalTrades}</span>
                    </div>
                    <div>
                      <span className="text-[var(--fg-faint)]">Win Rate: </span>
                      <span className="font-mono">{(s.performance.winRate * 100).toFixed(0)}%</span>
                    </div>
                    <div>
                      <span className="text-[var(--fg-faint)]">PnL: </span>
                      <span className={`font-mono ${s.performance.totalPnL >= 0 ? "text-[var(--key)]" : "text-[var(--red)]"}`}>
                        {s.performance.totalPnL.toFixed(0)}
                      </span>
                    </div>
                    <div>
                      <span className="text-[var(--fg-faint)]">Sharpe: </span>
                      <span className="font-mono">{s.performance.sharpeRatio.toFixed(2)}</span>
                    </div>
                  </div>
                </div>
              ))}
            </div>
          ) : (
            <EmptyState title="No strategies" description="Load strategies from config" />
          )}
        </Card>
      </div>

      <Card>
        <p className="text-xs font-mono text-[var(--fg-faint)] mb-4">Data Flow ({edges.length} steps)</p>
        <div className="space-y-1">
          {edges.map((e: { from: string; to: string; step?: string }, i: number) => (
            <div key={i} className="flex items-center gap-2 p-2 rounded bg-[var(--bg-2)] text-xs font-mono">
              <span className="text-[var(--cyan)]">→</span>
              <span className="text-[var(--fg-dim)]">{e.from}</span>
              <span className="text-[var(--fg-faint)]">through</span>
              <span className="text-[var(--key)]">{e.step || e.to}</span>
              <span className="text-[var(--fg-dim)]">→</span>
              <span>{e.to}</span>
            </div>
          ))}
        </div>
      </Card>
    </div>
  );
}

function PipelineNode({ node, color, label }: { node: { id: string; label: string; type: string; status: string; rows?: number }; color: string; label: string }) {
  const statusColor = {
    ok: "#8fce6a",
    running: "#a2e65d",
    failed: "#c1503f",
    queued: "#4a463f",
    skipped: "#4a463f",
  }[node.status] || "#4a463f";

  return (
    <div className="flex flex-col items-center gap-1 p-3 rounded border border-[var(--line)] bg-[var(--bg-2)] min-w-[100px]">
      <div
        className="w-3 h-3 rounded-full"
        style={{ backgroundColor: statusColor, boxShadow: `0 0 6px ${statusColor}66` }}
      />
      <span className="text-[10px] font-mono uppercase" style={{ color }}>{label}</span>
      <span className="text-sm font-mono font-medium">{node.label}</span>
      {node.rows !== undefined && (
        <span className="text-[10px] text-[var(--fg-faint)] font-mono">{node.rows.toLocaleString()} rows</span>
      )}
    </div>
  );
}