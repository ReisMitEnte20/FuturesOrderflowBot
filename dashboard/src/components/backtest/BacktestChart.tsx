import { useEffect, useRef } from "react";
import * as echarts from "echarts";
import type { Candle, OhlcTrade } from "@/lib/backtestApi";

interface Props {
  candles: Candle[];
  trades: OhlcTrade[];
  /** Auf diesen Trade fokussieren (Chart zoomt auf den Trade-Bereich). */
  focus?: { entryBarIndex: number; exitBarIndex: number } | null;
  height?: number;
  className?: string;
}

const COL = { key: "#a2e65d", red: "#c1503f", gold: "#e8c069", cyan: "#7fc4b4", line: "#232120", panel: "#161513", fg: "#f4f2ed", faint: "#8b857a" };

function fmtTime(ms: number): string {
  const d = new Date(ms);
  return d.toISOString().slice(5, 16).replace("T", " ");
}

export function BacktestChart({ candles, trades, focus, height = 380, className }: Props) {
  const ref = useRef<HTMLDivElement | null>(null);
  const chartRef = useRef<echarts.ECharts | null>(null);

  // Chart aufbauen / bei Datenwechsel neu setzen.
  useEffect(() => {
    if (!ref.current) return;
    const chart = chartRef.current ?? echarts.init(ref.current);
    chartRef.current = chart;

    const times = candles.map((c) => fmtTime(c.t));
    const ohlc = candles.map((c) => [c.o, c.c, c.l, c.h]);

    // Entry-/Exit-Marker als Scatter (x = Bar-Index, y = Preis).
    const entries = trades.map((t) => ({
      value: [t.entryBarIndex, t.entryPrice],
      symbol: t.side === "Long" ? "triangle" : "path://M2,2 L22,2 L12,20 Z", // Short: nach unten
      symbolRotate: t.side === "Long" ? 0 : 0,
      itemStyle: { color: t.side === "Long" ? COL.key : COL.red },
    }));
    const exits = trades.map((t) => ({
      value: [t.exitBarIndex, t.exitPrice],
      itemStyle: {
        color: t.exitReason === "TakeProfit" ? COL.key : t.exitReason === "StopLoss" ? COL.red : COL.gold,
        borderColor: t.ambiguous ? COL.fg : "transparent",
        borderWidth: t.ambiguous ? 2 : 0,
      },
    }));

    const total = candles.length;
    const defStart = total > 300 ? total - 300 : 0;

    chart.setOption(
      {
        animation: false,
        grid: { left: 8, right: 56, top: 12, bottom: 48, containLabel: true },
        xAxis: {
          type: "category",
          data: times,
          axisLine: { lineStyle: { color: COL.line } },
          axisLabel: { color: COL.faint, fontSize: 10 },
          axisTick: { show: false },
        },
        yAxis: {
          type: "value",
          scale: true,
          position: "right",
          splitLine: { lineStyle: { color: COL.line } },
          axisLabel: { color: COL.faint, fontSize: 10 },
        },
        tooltip: {
          trigger: "axis",
          backgroundColor: COL.panel,
          borderColor: "#2e2b28",
          textStyle: { color: COL.fg, fontSize: 11 },
        },
        dataZoom: [
          { type: "inside", startValue: defStart, endValue: total - 1 },
          { type: "slider", startValue: defStart, endValue: total - 1, height: 18, bottom: 16, borderColor: COL.line, fillerColor: "rgba(162,230,93,0.10)", handleStyle: { color: COL.key }, textStyle: { color: COL.faint, fontSize: 9 } },
        ],
        series: [
          {
            name: "OHLC",
            type: "candlestick",
            data: ohlc,
            itemStyle: { color: COL.key, color0: COL.red, borderColor: COL.key, borderColor0: COL.red, borderWidth: 1 },
          },
          { name: "Entries", type: "scatter", data: entries, symbolSize: 11, z: 5 },
          { name: "Exits", type: "scatter", data: exits, symbol: "diamond", symbolSize: 10, z: 6 },
        ],
      },
      { notMerge: true },
    );

    const onResize = () => chart.resize();
    window.addEventListener("resize", onResize);
    return () => window.removeEventListener("resize", onResize);
  }, [candles, trades]);

  // Auf einen ausgewählten Trade fokussieren.
  useEffect(() => {
    const chart = chartRef.current;
    if (!chart || !focus) return;
    const pad = Math.max(3, Math.round((focus.exitBarIndex - focus.entryBarIndex) * 1.5) + 5);
    chart.dispatchAction({
      type: "dataZoom",
      startValue: Math.max(0, focus.entryBarIndex - pad),
      endValue: Math.min(candles.length - 1, focus.exitBarIndex + pad),
    });
  }, [focus, candles.length]);

  useEffect(() => () => { chartRef.current?.dispose(); chartRef.current = null; }, []);

  return <div ref={ref} className={className} style={{ height, width: "100%" }} />;
}
