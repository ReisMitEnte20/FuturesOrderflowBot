import * as echarts from "echarts";
import { cn } from "@/lib/utils";

interface TradePipelineGraphProps {
  nodes?: Array<{ id: string; label: string; type: string; status: string; rows?: number }>;
  edges?: Array<{ from: string; to: string; step?: string }>;
  height?: number;
  className?: string;
}

const NODE_COLORS: Record<string, string> = {
  source: "#a2e65d",
  indicator: "#7fc4b4",
  signal: "#e8c069",
  position: "#c2b6d8",
  pnl: "#c1503f",
};

export function TradePipelineGraph({ nodes = [], edges = [], height = 250, className }: TradePipelineGraphProps) {
  const chartRef = { current: null as HTMLDivElement | null };

  if (typeof window !== "undefined" && nodes.length > 0) {
    const chart = echarts.init(chartRef.current);

    const colCount = Math.ceil(Math.sqrt(nodes.length));
    const positions = nodes.map((node, i) => ({
      name: node.label,
      x: 100 + (i % colCount) * 200,
      y: 60 + Math.floor(i / colCount) * 100,
    }));

    const series: any[] = [];

    edges.forEach((edge) => {
      const sIdx = nodes.findIndex((n) => n.id === edge.from || n.label === edge.from);
      const tIdx = nodes.findIndex((n) => n.id === edge.to || n.label === edge.to);
      if (sIdx >= 0 && tIdx >= 0) {
        series.push({
          type: "lines",
          coordinateSystem: "none",
          data: [{ coords: [[positions[sIdx].x, positions[sIdx].y], [positions[tIdx].x, positions[tIdx].y]] }],
          lineStyle: { color: "#2e2b28", width: 1 },
        });
      }
    });

    series.push({
      type: "effectScatter",
      coordinateSystem: "none",
      data: positions.map((p, i) => ({
        name: nodes[i]?.label || p.name,
        value: [p.x, p.y],
        itemStyle: {
          color: NODE_COLORS[nodes[i]?.type] || "#a2e65d",
          shadowBlur: 8,
          shadowColor: "rgba(162,230,93,0.4)",
        },
      })),
      symbolSize: 36,
      label: {
        show: true,
        position: "bottom",
        formatter: (params: any) => params.name,
        color: "#cbc6bc",
        fontSize: 10,
        fontFamily: "var(--mono)",
      },
    });

    chart.setOption({ series });
  }

  return (
    <div
      ref={(el) => { chartRef.current = el; }}
      className={cn(className)}
      style={{ height }}
    />
  );
}