import * as echarts from "echarts";
import { cn } from "@/lib/utils";

interface PipelineGraphProps {
  nodes: Array<{ id: string; label: string; type: string; status: string; rows?: number }>;
  edges: Array<{ from: string; to: string; step?: string }>;
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

const STATUS_COLORS: Record<string, string> = {
  ok: "#2fd890",
  running: "#3cf0a0",
  failed: "#f0566a",
  queued: "#34557a",
  skipped: "#34557a",
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
        lineStyle: { color: "#1d3654", width: 1.5 },
        label: { show: false },
      };
    }).filter(Boolean);

    const option = {
      tooltip: {
        backgroundColor: "#0a1a30",
        borderColor: "#1d3654",
        textStyle: { color: "#eefcf6", fontFamily: "var(--mono)" },
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
              color: NODE_COLORS[nodes[i]?.type] || "#3cf0a0",
              shadowBlur: 10,
              shadowColor: (STATUS_COLORS[nodes[i]?.status] || "#3cf0a0") + "88",
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