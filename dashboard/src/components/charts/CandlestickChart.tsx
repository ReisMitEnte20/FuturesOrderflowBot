import { useEffect, useRef } from "react";
import * as echarts from "echarts";

interface CandlestickChartProps {
  data: Array<{ timestamp: string; open: number; close: number; low: number; high: number; volume?: number }>;
  height?: number;
  className?: string;
}

const UP = "#a2e65d";
const DOWN = "#c1503f";

export function CandlestickChart({ data, height = 350, className }: CandlestickChartProps) {
  const containerRef = useRef<HTMLDivElement | null>(null);
  const chartRef = useRef<echarts.ECharts | null>(null);

  // Chart einmal initialisieren, bei Unmount freigeben, bei Größenänderung anpassen.
  useEffect(() => {
    if (!containerRef.current) return;
    const chart = echarts.init(containerRef.current);
    chartRef.current = chart;
    const onResize = () => chart.resize();
    window.addEventListener("resize", onResize);
    return () => {
      window.removeEventListener("resize", onResize);
      chart.dispose();
      chartRef.current = null;
    };
  }, []);

  useEffect(() => {
    const chart = chartRef.current;
    if (!chart) return;

    const hasVolume = data.length > 0 && data[0].volume !== undefined;
    const axisLabel = { color: "#8b857a", fontSize: 10, fontFamily: "var(--mono)" };
    const times = data.map((d) => d.timestamp);

    chart.setOption(
      {
        animation: false,
        grid: hasVolume
          ? [
              { left: 56, right: 16, top: 16, height: "62%" },
              { left: 56, right: 16, top: "76%", bottom: 24 },
            ]
          : [{ left: 56, right: 16, top: 16, bottom: 24 }],
        xAxis: (hasVolume ? [0, 1] : [0]).map((i) => ({
          type: "category",
          gridIndex: i,
          data: times,
          axisLine: { lineStyle: { color: "#232120" } },
          axisLabel: i === 0 && hasVolume ? { show: false } : axisLabel,
          axisTick: { show: false },
        })),
        yAxis: (hasVolume ? [0, 1] : [0]).map((i) => ({
          type: "value",
          gridIndex: i,
          scale: true,
          splitNumber: i === 1 ? 2 : 5,
          splitLine: { lineStyle: { color: "#232120" } },
          axisLabel,
        })),
        tooltip: {
          trigger: "axis",
          axisPointer: { type: "cross" },
          backgroundColor: "#161513",
          borderColor: "#2e2b28",
          textStyle: { color: "#f4f2ed", fontFamily: "var(--mono)" },
        },
        series: [
          {
            name: "OHLC",
            type: "candlestick",
            data: data.map((d) => [d.open, d.close, d.low, d.high]),
            // ECharts: color/borderColor = steigende Kerze, color0/borderColor0 = fallende Kerze.
            itemStyle: { color: UP, color0: DOWN, borderColor: UP, borderColor0: DOWN, borderWidth: 1 },
          },
          ...(hasVolume
            ? [
                {
                  name: "Volume",
                  type: "bar",
                  xAxisIndex: 1,
                  yAxisIndex: 1,
                  data: data.map((d) => ({
                    value: d.volume,
                    itemStyle: { color: d.close >= d.open ? "rgba(162,230,93,0.45)" : "rgba(193,80,63,0.45)" },
                  })),
                },
              ]
            : []),
        ],
      },
      { notMerge: true },
    );
  }, [data]);

  return <div ref={containerRef} className={`w-full ${className || ""}`} style={{ height }} />;
}
