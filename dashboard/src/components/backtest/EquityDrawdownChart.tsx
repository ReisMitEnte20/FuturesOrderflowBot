import { useEffect, useRef } from "react";
import * as echarts from "echarts";
import type { EquityPoint } from "@/lib/backtestApi";

interface Props { equity: EquityPoint[]; initialBalance: number; height?: number; className?: string; }

const COL = { key: "#a2e65d", red: "#c1503f", line: "#232120", panel: "#161513", fg: "#f4f2ed", faint: "#8b857a" };

export function EquityDrawdownChart({ equity, initialBalance, height = 220, className }: Props) {
  const ref = useRef<HTMLDivElement | null>(null);
  const chartRef = useRef<echarts.ECharts | null>(null);

  useEffect(() => {
    if (!ref.current) return;
    const chart = chartRef.current ?? echarts.init(ref.current);
    chartRef.current = chart;

    const times = equity.map((e) => new Date(e.time).toISOString().slice(5, 16).replace("T", " "));
    const eq = equity.map((e) => e.equity);
    // Drawdown (unter dem laufenden Hoch), negativ dargestellt.
    let peak = initialBalance;
    const dd = equity.map((e) => { peak = Math.max(peak, e.equity); return e.equity - peak; });

    chart.setOption(
      {
        animation: false,
        grid: { left: 8, right: 56, top: 12, bottom: 24, containLabel: true },
        xAxis: { type: "category", data: times, axisLine: { lineStyle: { color: COL.line } }, axisLabel: { color: COL.faint, fontSize: 10 }, axisTick: { show: false } },
        yAxis: [
          { type: "value", scale: true, position: "right", splitLine: { lineStyle: { color: COL.line } }, axisLabel: { color: COL.faint, fontSize: 10 } },
          { type: "value", position: "left", max: 0, splitLine: { show: false }, axisLabel: { color: COL.faint, fontSize: 9 } },
        ],
        tooltip: { trigger: "axis", backgroundColor: COL.panel, borderColor: "#2e2b28", textStyle: { color: COL.fg, fontSize: 11 } },
        series: [
          {
            name: "Equity", type: "line", data: eq, smooth: false, symbol: "none",
            lineStyle: { color: COL.key, width: 1.5 },
            areaStyle: { color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [{ offset: 0, color: "rgba(162,230,93,0.15)" }, { offset: 1, color: "rgba(162,230,93,0)" }]) },
          },
          { name: "Drawdown", type: "line", yAxisIndex: 1, data: dd, symbol: "none", lineStyle: { color: COL.red, width: 1, opacity: 0.7 }, areaStyle: { color: "rgba(193,80,63,0.12)" } },
        ],
      },
      { notMerge: true },
    );
    const onResize = () => chart.resize();
    window.addEventListener("resize", onResize);
    return () => window.removeEventListener("resize", onResize);
  }, [equity, initialBalance]);

  useEffect(() => () => { chartRef.current?.dispose(); chartRef.current = null; }, []);

  return <div ref={ref} className={className} style={{ height, width: "100%" }} />;
}
