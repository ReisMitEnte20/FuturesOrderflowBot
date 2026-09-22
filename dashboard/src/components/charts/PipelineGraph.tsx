import * as echarts from "echarts";
import { cn } from "@/lib/utils";

interface PipelineGraphProps {
  nodes: Array<{ id: string; label: string; type: string; status: string; rows?: number }>;
  edges: Array<{ from: string; to: string; step?: string }>;
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

const STATUS_COLORS: Record<string, string> = {
  ok: "#8fce6a",
  running: "#a2e65d",
  failed: "#c1503f",
  queued: "#4a463f",
  skipped: "#4a463f",
};

export function PipelineGraph({ nodes, edges, height = 300, className }: PipelineGraphProps) {
  const chartRef = { current: null as HTMLDivElement | null };

  if (typeof window !== "undefined" && nodes.length > 0) {
    const chart = echarts.init(chartRef.current);

    const positions = nodes.map((node, i) => {
      const row = Math.floor(i / 4);
      const col = i % 4;
      return {
        name: node.label,
        x: 100 + col * 220,
        y: 50 + row * 120,
      };
    });

    const edgesOption = edges.map((edge) => {
      const sourceIdx = nodes.findIndex((n) => n.label === edge.from || n.id === edge.from);
      const targetIdx = nodes.findIndex((n) => n.label === edge.to || n.id === edge.to);
      if (sourceIdx < 0 || targetIdx < 0) return null;
      return {
        lines: [
          {
            name: `${edge.from}→${edge.to}`,
            coords: [[positions[sourceIdx].x, positions[sourceIdx].y], [positions[targetIdx].x, positions[targetIdx].y]],
          },
        ],
        lineStyle: { color: "#2e2b28", width: 1.5 },
        label: { show: false },
      };
    }).filter(Boolean);

    const option = {
      tooltip: {
        backgroundColor: "#161513",
        borderColor: "#2e2b28",
        textStyle: { color: "#f4f2ed", fontFamily: "var(--mono)" },
      },
      series: [
        ...edgesOption.map((e) => ({
          type: "lines",
          coordinateSystem: "none",
          ...e,
        })),
        {
          type: "effectScatter",
          coordinateSystem: "none",
          data: positions.map((p, i) => ({
            name: nodes[i]?.label || p.name,
            value: [p.x, p.y],
            itemStyle: {
              color: NODE_COLORS[nodes[i]?.type] || "#a2e65d",
              shadowBlur: 10,
              shadowColor: (STATUS_COLORS[nodes[i]?.status] || "#a2e65d") + "88",
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
          emphasis: {
            scale: 1.5,
          },
        },
      ],
    };

    chart.setOption(option);
  }

  return (
    <div
      ref={(el) => { chartRef.current = el; }}
      className={cn("w-full", className)}
      style={{ height }}
    />
  );
}