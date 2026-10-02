import * as echarts from "echarts";
import { cn } from "@/lib/utils";

interface DepthChartProps {
  asks: Array<{ price: number; quantity: number }>;
  bids: Array<{ price: number; quantity: number }>;
  height?: number;
  className?: string;
}

export function DepthChart({ asks, bids, height = 200, className }: DepthChartProps) {
  const chartRef = { current: null as HTMLDivElement | null };

  if (typeof window !== "undefined" && asks.length > 0 && bids.length > 0) {
    const chart = echarts.init(chartRef.current);

    const option = {
      grid: { left: 50, right: 16, top: 16, bottom: 24, containLabel: true },
      xAxis: {
        type: "value",
        axisLine: { lineStyle: { color: "#14273f" } },
        axisLabel: { color: "#6b8399", fontSize: 10, fontFamily: "var(--mono)" },
        splitLine: { show: false },
      },
      yAxis: {
        type: "value",
        splitLine: { lineStyle: { color: "#14273f" } },
        axisLabel: { color: "#6b8399", fontSize: 10, fontFamily: "var(--mono)" },
      },
      tooltip: {
        trigger: "axis",
        backgroundColor: "#0a1a30",
        borderColor: "#1d3654",
        textStyle: { color: "#eefcf6", fontFamily: "var(--mono)" },
      },
      series: [
        {
          name: "Asks",
          type: "line",
          data: asks.map((a) => [a.price, a.quantity]),
          smooth: true,
          symbol: "none",
          lineStyle: { color: "#f0566a", width: 1.5 },
          areaStyle: {
            color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [
              { offset: 0, color: "rgba(240,86,106,0.3)" },
              { offset: 1, color: "rgba(240,86,106,0)" },
            ]),
          },
        },
        {
          name: "Bids",
          type: "line",
          data: bids.map((b) => [b.price, b.quantity]),
          smooth: true,
          symbol: "none",
          lineStyle: { color: "#3cf0a0", width: 1.5 },
          areaStyle: {
            color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [
              { offset: 0, color: "rgba(60,240,160,0.3)" },
              { offset: 1, color: "rgba(60,240,160,0)" },
            ]),
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