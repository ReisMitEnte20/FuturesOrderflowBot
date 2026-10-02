import * as echarts from "echarts";
import { cn } from "@/lib/utils";

interface TradePipelineGraphProps {
  nodes?: Array<{ id: string; label: string; type: string; status: string; rows?: number }>;
  edges?: Array<{ from: string; to: string; step?: string }>;
  height?: number;
  className?: string;
}

const NODE_COLORS: Record<string, string> = {
  source: "#3cf0a0",
  indicator: "#22b8f0",
  signal: "#f0c35a",
  position: "#9b8cff",
  pnl: "#f0566a",
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
          lineStyle: { color: "#1d3654", width: 1 },
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
          color: NODE_COLORS[nodes[i]?.type] || "#3cf0a0",
          shadowBlur: 8,
          shadowColor: "rgba(60,240,160,0.4)",
        },
      })),
      symbolSize: 36,
      label: {
        show: true,
        position: "bottom",
        formatter: (params: any) => params.name,
        color: "#a9c3cf",
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