import * as echarts from "echarts";

interface CandlestickChartProps {
  data: Array<{ timestamp: string; open: number; close: number; low: number; high: number; volume?: number }>;
  height?: number;
  className?: string;
}

export function CandlestickChart({ data, height = 350, className }: CandlestickChartProps) {
  const chartRef = { current: null as HTMLDivElement | null };

  if (typeof window !== "undefined" && data.length > 0) {
    const chart = echarts.init(chartRef.current);
    const times = data.map((d) => d.timestamp);
    const candleData = data.map((d) => [d.open, d.close, d.low, d.high]);

    const option: any = {
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
        scale: true,
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
          type: "candlestick",
          data: candleData,
          itemStyle: {
            color: "#c1503f",
            color0: "#a2e65d",
            borderColor: "#c1503f",
            borderColor0: "#a2e65d",
            borderWidth: 1,
          },
        },
      ],
    };

    if (data[0] && data[0].volume !== undefined) {
      option.benchmark = [
        {
          name: "Volume",
          type: "bar",
          xAxisIndex: 0,
          yAxisIndex: 1,
          data: data.map((d) => d.volume),
          itemStyle: { color: "#2e2b28" },
        },
      ];
      option.yAxis = [option.yAxis, { type: "value", splitNumber: 2, axisLabel: { show: false }, splitLine: { show: false } }];
      option.grid.bottom = 60;
    }

    chart.setOption(option);
  }

  return (
    <div
      className={`w-full ${className || ""}`}
      style={{ height }}
      ref={(el) => { chartRef.current = el; }}
    />
  );
}