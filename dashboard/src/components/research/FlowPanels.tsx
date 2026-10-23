import { useEffect, useRef } from "react";
import * as echarts from "echarts";
import type { ChartTrade } from "@/lib/replay";
import { tradeWindow } from "@/lib/replay";
import type { WsCandle } from "@/lib/workspace";

// Synchronisierte Panels: Volumen, Delta, Cumulative Delta (CVD) je Bar. Gleicher Zeitbezug (Bar-Index) wie die
// anderen Charts; Fokus folgt der Trade-Auswahl. CVD-Anker ist im Workspace dokumentiert.
interface Props { candles: WsCandle[]; cvd: number[]; cvdAnchorBar: number; trades: ChartTrade[]; selected: number | null; focusKey: number; className?: string; }
const KEY = "#3cf0a0", RED = "#f0566a", GOLD = "#f0c35a", FAINT = "#6b8399", LINE = "#14273f", PANEL = "#0a1a30", FG = "#eefcf6";

export function FlowPanels(props: Props) {
  const ref = useRef<HTMLDivElement | null>(null);
  const chartRef = useRef<echarts.ECharts | null>(null);
  const latest = useRef(props); latest.current = props;
  const lastFocus = useRef(props.focusKey);

  useEffect(() => {
    if (!ref.current) return;
    const chart = echarts.init(ref.current); chartRef.current = chart;
    const ro = new ResizeObserver(() => chart.resize()); ro.observe(ref.current);
    return () => { ro.disconnect(); chart.dispose(); chartRef.current = null; };
  }, []);

  useEffect(() => {
    const chart = chartRef.current; if (!chart) return;
    const { candles, cvd, cvdAnchorBar } = props; const n = candles.length;
    const cats = candles.map((_, i) => String(i));
    const grid = [{ left: 50, right: 12, top: 16, height: "22%" }, { left: 50, right: 12, top: "40%", height: "22%" }, { left: 50, right: 12, top: "66%", height: "22%" }];
    const axCommon = { type: "category", data: cats, boundaryGap: true, axisLine: { lineStyle: { color: LINE } }, axisTick: { show: false } } as const;
    chart.setOption({
      animation: false,
      grid,
      xAxis: [
        { ...axCommon, gridIndex: 0, axisLabel: { show: false } },
        { ...axCommon, gridIndex: 1, axisLabel: { show: false } },
        { ...axCommon, gridIndex: 2, axisLabel: { color: FAINT, fontSize: 9, hideOverlap: true } },
      ],
      yAxis: [
        { gridIndex: 0, type: "value", name: "Volumen", nameTextStyle: { color: FAINT, fontSize: 9 }, splitLine: { lineStyle: { color: LINE } }, axisLabel: { color: FAINT, fontSize: 9 } },
        { gridIndex: 1, type: "value", name: "Delta", nameTextStyle: { color: FAINT, fontSize: 9 }, splitLine: { lineStyle: { color: LINE } }, axisLabel: { color: FAINT, fontSize: 9 } },
        { gridIndex: 2, type: "value", name: "CVD", nameTextStyle: { color: FAINT, fontSize: 9 }, splitLine: { lineStyle: { color: LINE } }, axisLabel: { color: FAINT, fontSize: 9 } },
      ],
      axisPointer: { link: [{ xAxisIndex: [0, 1, 2] }], lineStyle: { color: "#2a4868" } },
      tooltip: {
        trigger: "axis", backgroundColor: PANEL, borderColor: "#1d3654", textStyle: { color: FG, fontSize: 11, fontFamily: "JetBrains Mono, monospace" },
        formatter: (ps: any[]) => { const i = ps?.[0]?.dataIndex; const c = latest.current.candles[i]; if (!c) return ""; return `Bar ${i}<br/>V ${c.v}<br/>Δ <span style="color:${c.delta >= 0 ? KEY : RED}">${c.delta}</span><br/>CVD ${latest.current.cvd[i]}`; },
      },
      dataZoom: [
        { type: "inside", xAxisIndex: [0, 1, 2], startValue: 0, endValue: Math.max(0, n - 1), filterMode: "none" },
        { type: "slider", xAxisIndex: [0, 1, 2], startValue: 0, endValue: Math.max(0, n - 1), height: 14, bottom: 4, filterMode: "none", borderColor: LINE, fillerColor: "rgba(60,240,160,0.10)", handleStyle: { color: KEY }, textStyle: { color: FAINT, fontSize: 9 } },
      ],
      series: [
        { name: "Volumen", type: "bar", xAxisIndex: 0, yAxisIndex: 0, data: candles.map((c) => c.v), itemStyle: { color: "rgba(34,184,240,0.5)" } },
        { name: "Delta", type: "bar", xAxisIndex: 1, yAxisIndex: 1, data: candles.map((c) => ({ value: c.delta, itemStyle: { color: c.delta >= 0 ? KEY : RED } })) },
        { name: "CVD", type: "line", xAxisIndex: 2, yAxisIndex: 2, data: cvd, showSymbol: false, lineStyle: { color: GOLD, width: 1.5 }, areaStyle: { color: "rgba(240,195,90,0.08)" }, markLine: { silent: true, symbol: "none", data: [{ xAxis: cvdAnchorBar, lineStyle: { color: GOLD, type: "dashed", width: 1 }, label: { show: true, formatter: "CVD⚓", color: GOLD, fontSize: 9 } }] } },
      ],
    }, { notMerge: true });
  }, [props.candles, props.cvd]);

  useEffect(() => {
    const chart = chartRef.current; if (!chart) return;
    const { candles, trades, selected, focusKey } = latest.current; const n = candles.length;
    if (focusKey === lastFocus.current) return; lastFocus.current = focusKey;
    const t = selected == null ? null : trades.find((x) => x.index === selected) ?? null;
    if (!t) { chart.dispatchAction({ type: "dataZoom", startValue: 0, endValue: n - 1 }); return; }
    const exitBar = t.exit?.barIndex ?? t.entryBarIndex;
    const w = tradeWindow(t.entryBarIndex, exitBar, n);
    chart.dispatchAction({ type: "dataZoom", startValue: w.start, endValue: w.end });
  }, [props.focusKey]);

  return <div className={`relative ${props.className ?? ""}`} data-flowbars={props.candles.length}><div ref={ref} className="absolute inset-0" /></div>;
}
