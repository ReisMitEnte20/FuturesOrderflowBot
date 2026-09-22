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
        axisLine: { lineStyle: { color: "#232120" } },
        axisLabel: { color: "#8b857a", fontSize: 10, fontFamily: "var(--mono)" },
        splitLine: { show: false },
      },
      yAxis: {
        type: "value",
        splitLine: { lineStyle: { color: "#232120" } },
        axisLabel: { color: "#8b857a", fontSize: 10, fontFamily: "var(--mono)" },
      },
      tooltip: {
        trigger: "axis",
        backgroundColor: "#161513",
        borderColor: "#2e2b28",
        textStyle: { color: "#f4f2ed", fontFamily: "var(--mono)" },
      },
      series: [
        {
          name: "Asks",
          type: "line",
          data: asks.map((a) => [a.price, a.quantity]),
          smooth: true,
          symbol: "none",
          lineStyle: { color: "#c1503f", width: 1.5 },
          areaStyle: {
            color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [
              { offset: 0, color: "rgba(193,80,63,0.3)" },
              { offset: 1, color: "rgba(193,80,63,0)" },
            ]),
          },
        },
        {
          name: "Bids",
          type: "line",
          data: bids.map((b) => [b.price, b.quantity]),
          smooth: true,
          symbol: "none",
          lineStyle: { color: "#a2e65d", width: 1.5 },
          areaStyle: {
            color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [
              { offset: 0, color: "rgba(162,230,93,0.3)" },
              { offset: 1, color: "rgba(162,230,93,0)" },
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