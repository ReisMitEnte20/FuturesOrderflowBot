import * as echarts from "echarts";

interface EquityChartProps {
  data: Array<{ timestamp: string; equity: number } | { time: string; value: number }>;
  height?: number;
  className?: string;
}

function ChartContainer({ children, height, className }: { children?: React.RefCallback<HTMLDivElement>; height: number; className?: string }) {
  return <div ref={children} className={`w-full ${className || ""}`} style={{ height }} />;
}

export function EquityChart({ data, height = 250, className }: EquityChartProps) {
  const chartRef = { current: null as HTMLDivElement | null };

  if (typeof window !== "undefined" && data.length > 0) {
    const chart = echarts.init(chartRef.current);
    const series = data.map((d) => (d as any).value ?? (d as any).equity ?? 0);
    const times = data.map((d) => (d as any).time ?? (d as any).timestamp ?? "");

    chart.setOption({
      grid: { left: 50, right: 16, top: 16, bottom: 24, containLabel: true },
      xAxis: {
        type: "category",
        data: times,
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
          data: series,
          smooth: true,
          symbol: "none",
          lineStyle: { color: "#a2e65d", width: 1.5 },
          areaStyle: {
            color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [
              { offset: 0, color: "rgba(162,230,93,0.15)" },
              { offset: 1, color: "rgba(162,230,93,0)" },
            ]),
          },
        },
      ],
    });
  }

  return ChartContainer({
    height,
    className,
    children: (el) => {
      chartRef.current = el;
    },
  });
}