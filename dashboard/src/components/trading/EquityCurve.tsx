import * as echarts from "echarts";
import { cn } from "@/lib/utils";

interface EquityCurveProps {
  data: Array<{ timestamp: string; equity: number }>;
  height?: number;
  className?: string;
}

export function EquityCurve({ data, height = 250, className }: EquityCurveProps) {
  const chartRef = { current: null as HTMLDivElement | null };

  if (typeof window !== "undefined" && data.length > 0) {
    const chart = echarts.init(chartRef.current);

    chart.setOption({
      grid: { left: 50, right: 16, top: 16, bottom: 24, containLabel: true },
      xAxis: {
        type: "category",
        data: data.map((d) => d.timestamp),
        axisLine: { lineStyle: { color: "#232120" } },
        axisLabel: { color: "#8b857a", fontSize: 10, fontFamily: "var(--mono)" },
        axisTick: { show: false },
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
          type: "line",
          data: data.map((d) => d.equity),
          smooth: true,
          symbol: "none",
          lineStyle: { color: "#a2e65d", width: 1.5 },
          areaStyle: {
            color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [
              { offset: 0, color: "rgba(162,230,93,0.2)" },
              { offset: 1, color: "rgba(162,230,93,0)" },
            ]),
          },
        },
      ],
    });
  }

  return (
    <div
      ref={(el) => { chartRef.current = el; }}
      className={cn(className)}
      style={{ height }}
    />
  );
}