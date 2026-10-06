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
        axisLine: { lineStyle: { color: "#14273f" } },
        axisLabel: { color: "#6b8399", fontSize: 10, fontFamily: "var(--mono)" },
        splitLine: { show: false },
      },
      yAxis: {
        type: "category",
        data: data.map((d) => d.price.toFixed(2)),
        axisLine: { lineStyle: { color: "#14273f" } },
        axisLabel: { color: "#6b8399", fontSize: 10, fontFamily: "var(--mono)" },
        splitLine: { show: false },
      },
      tooltip: {
        trigger: "axis",
        backgroundColor: "#0a1a30",
        borderColor: "#1d3654",
        textStyle: { color: "#eefcf6", fontFamily: "var(--mono)" },
      },
      series: [
        {
          type: "bar",
          data: data.map((d) => ({
            value: [d.volume, d.price],
            itemStyle: {
              color: new echarts.graphic.LinearGradient(0, 0, 1, 0, [
                { offset: 0, color: "rgba(60,240,160,0.6)" },
                { offset: 1, color: "rgba(34,184,240,0.3)" },
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