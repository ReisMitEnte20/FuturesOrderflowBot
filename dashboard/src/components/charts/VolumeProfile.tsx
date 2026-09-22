import * as echarts from "echarts";
import { cn } from "@/lib/utils";

interface VolumeProfileProps {
  data: Array<{ price: number; volume: number }>;
  height?: number;
  className?: string;
}

export function VolumeProfile({ data, height = 200, className }: VolumeProfileProps) {
  const chartRef = { current: null as HTMLDivElement | null };

  if (typeof window !== "undefined" && data.length > 0) {
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
        type: "category",
        data: data.map((d) => d.price.toFixed(2)),
        axisLine: { lineStyle: { color: "#232120" } },
        axisLabel: { color: "#8b857a", fontSize: 10, fontFamily: "var(--mono)" },
        splitLine: { show: false },
      },
      tooltip: {
        trigger: "axis",
        backgroundColor: "#161513",
        borderColor: "#2e2b28",
        textStyle: { color: "#f4f2ed", fontFamily: "var(--mono)" },
      },
      series: [
        {
          type: "bar",
          data: data.map((d) => ({
            value: [d.volume, d.price],
            itemStyle: {
              color: new echarts.graphic.LinearGradient(0, 0, 1, 0, [
                { offset: 0, color: "rgba(162,230,93,0.6)" },
                { offset: 1, color: "rgba(127,196,180,0.3)" },
              ]),
            },
          })),
          barWidth: "60%",
        },
      ],
    };

    chart.setOption(option);
  }

  return (
    <div
      ref={(el) => { chartRef.current = el; }}
      className={cn(className)}
      style={{ height }}
    />
  );
}