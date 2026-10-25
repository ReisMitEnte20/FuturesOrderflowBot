import { useEffect, useRef } from "react";
import * as echarts from "echarts";
import type { ChartTrade } from "@/lib/replay";
import { tradeWindow } from "@/lib/replay";
import type { WsCandle, VolumeProfile, BarPriceOccupancy } from "@/lib/workspace";

// Grosser Kerzenchart mit Volume Profile (POC/VAH/VAL), VWAP (Anker sichtbar), optional Bar-Preis-Belegung und Trade-Bubbles.
// Reused Interaktionsmuster (Fokus/Zoom/Marker) wie BacktestChart; Volume Profile als custom series (horizontale Balken links).

export interface Bubble { barIndex: number; p: number; v: number; dir: number; }
interface Props {
  candles: WsCandle[];
  trades: ChartTrade[];
  vwap: (number | null)[]; vwapAnchorBar: number;
  vp: VolumeProfile | null; tpo: BarPriceOccupancy | null;
  bubbles: Bubble[];
  /** Bar-Index, bis zu dem das (ggf. gekappte) Tape reicht; rechts davon sind Bubbles NICHT verfuegbar. null = volles Tape. */
  bubbleCoverageBar?: number | null;
  show: { vp: boolean; vwap: boolean; tpo: boolean; bubbles: boolean };
  selected: number | null; focusKey: number;
  onSelectTrade?: (i: number) => void;
  className?: string;
}
const KEY = "#3cf0a0", RED = "#f0566a", GOLD = "#f0c35a", CYAN = "#22b8f0", FG = "#eefcf6", FAINT = "#6b8399", LINE = "#14273f", PANEL = "#0a1a30";
const SHORT_DOWN = "path://M2,2 L22,2 L12,20 Z";
const px = (v: number | null | undefined) => (typeof v === "number" ? v.toFixed(2) : "–");

export function CandleProfileChart(props: Props) {
  const ref = useRef<HTMLDivElement | null>(null);
  const chartRef = useRef<echarts.ECharts | null>(null);
  const latest = useRef(props); latest.current = props;
  const lastFocus = useRef(props.focusKey);

  useEffect(() => {
    if (!ref.current) return;
    const chart = echarts.init(ref.current); chartRef.current = chart;
    const ro = new ResizeObserver(() => chart.resize()); ro.observe(ref.current);
    const onClick = (p: any) => { const i = p?.data?.tradeIndex; if (typeof i === "number") latest.current.onSelectTrade?.(i); };
    chart.on("click", onClick);
    return () => { ro.disconnect(); chart.off("click", onClick); chart.dispose(); chartRef.current = null; };
  }, []);

  useEffect(() => {
    const chart = chartRef.current; if (!chart) return;
    const { candles, trades, vwap, vp, tpo, bubbles, show, vwapAnchorBar, bubbleCoverageBar } = props;
    const n = candles.length;
    const entries = trades.map((t) => ({ value: [t.entryBarIndex, t.entryPrice], tradeIndex: t.index, symbol: t.side === "Long" ? "triangle" : SHORT_DOWN, itemStyle: { color: t.side === "Long" ? KEY : RED } }));
    const exits = trades.filter((t) => t.exit).map((t) => ({ value: [t.exit!.barIndex, t.exit!.price], tradeIndex: t.index, itemStyle: { color: GOLD } }));
    const maxVp = vp ? Math.max(1, ...vp.levels.map((l) => l.vol)) : 1;
    const maxTpo = tpo ? Math.max(1, ...tpo.levels.map((l) => l.count)) : 1;
    const maxBub = Math.max(1, ...bubbles.map((b) => b.v));

    const vpRender = (params: any, api: any) => {
      const price = api.value(1), vol = api.value(2);
      const y = api.coord([0, price])[1];
      const cs = params.coordSys; const x0 = cs.x; const h = Math.max(1, api.size([0, 1])[1] - 1);
      const w = (vol / maxVp) * cs.width * 0.2;
      const atPoc = vp && Math.abs(price - vp.poc) < 1e-9;
      return { type: "rect", shape: { x: x0, y: y - h / 2, width: w, height: h }, style: { fill: atPoc ? "rgba(240,195,90,0.5)" : "rgba(34,184,240,0.22)" } };
    };
    const tpoRender = (params: any, api: any) => {
      const price = api.value(1), cnt = api.value(2);
      const y = api.coord([0, price])[1];
      const cs = params.coordSys; const h = Math.max(1, api.size([0, 1])[1] - 1);
      const w = (cnt / maxTpo) * cs.width * 0.1;
      return { type: "rect", shape: { x: cs.x + cs.width * 0.2 + 2, y: y - h / 2, width: w, height: h }, style: { fill: "rgba(192,140,255,0.3)" } };
    };

    const vaLines: any[] = [];
    if (show.vp && vp) {
      vaLines.push({ yAxis: vp.poc, lineStyle: { color: GOLD, type: "solid", width: 1 }, label: { show: true, formatter: `POC ${px(vp.poc)}`, color: GOLD, fontSize: 9, position: "insideStartTop" } });
      vaLines.push({ yAxis: vp.vah, lineStyle: { color: CYAN, type: "dashed", width: 1 }, label: { show: true, formatter: `VAH ${px(vp.vah)}`, color: CYAN, fontSize: 9, position: "insideStartTop" } });
      vaLines.push({ yAxis: vp.val, lineStyle: { color: CYAN, type: "dashed", width: 1 }, label: { show: true, formatter: `VAL ${px(vp.val)}`, color: CYAN, fontSize: 9, position: "insideStartBottom" } });
    }
    // Gekapptes Tape: vertikale Grenze, ab der Bubbles NICHT verfuegbar sind (kein irrefuehrendes "keine grossen Trades").
    if (show.bubbles && bubbleCoverageBar != null && bubbleCoverageBar >= 0 && bubbleCoverageBar < n - 1) {
      vaLines.push({ xAxis: bubbleCoverageBar, lineStyle: { color: RED, type: "dotted", width: 1 }, label: { show: true, formatter: "Tape-Ende → Bubbles n/a", color: RED, fontSize: 8, position: "insideEndTop" } });
    }

    chart.setOption({
      animation: false,
      grid: { left: 8, right: 60, top: 12, bottom: 46, containLabel: true },
      xAxis: { type: "category", data: candles.map((_, i) => String(i)), boundaryGap: true, axisLine: { lineStyle: { color: LINE } }, axisTick: { show: false }, axisLabel: { color: FAINT, fontSize: 9, hideOverlap: true } },
      yAxis: { type: "value", scale: true, position: "right", splitLine: { lineStyle: { color: LINE } }, axisLabel: { color: FAINT, fontSize: 10, formatter: (v: number) => v.toFixed(0) } },
      tooltip: {
        trigger: "axis", axisPointer: { type: "cross", lineStyle: { color: "#2a4868" }, crossStyle: { color: "#2a4868" }, label: { backgroundColor: "#1d3654" } },
        backgroundColor: PANEL, borderColor: "#1d3654", textStyle: { color: FG, fontSize: 11, fontFamily: "JetBrains Mono, monospace" },
        formatter: (ps: any[]) => {
          const p = ps.find((x) => x.seriesType === "candlestick") ?? ps[0]; const i = p?.dataIndex; const c = latest.current.candles[i]; if (!c) return "";
          let html = `<b>${new Date(c.t).toISOString().slice(11, 19)}</b> · Bar ${i}<br/>O ${px(c.o)} H ${px(c.h)} L ${px(c.l)} C ${px(c.c)}<br/>V ${c.v} · Δ <span style="color:${c.delta >= 0 ? KEY : RED}">${c.delta}</span>`;
          const vw = latest.current.vwap[i]; if (latest.current.show.vwap && vw != null) html += `<br/>VWAP ${px(vw)}`;
          for (const t of latest.current.trades) { if (t.entryBarIndex === i) html += `<br/><span style="color:${t.side === "Long" ? KEY : RED}">▲ Entry #${t.index + 1} ${t.side} ${px(t.entryPrice)}</span>`; if (t.exit && t.exit.barIndex === i) html += `<br/><span style="color:${GOLD}">◆ Exit #${t.index + 1} ${px(t.exit.price)}</span>`; }
          return html;
        },
      },
      dataZoom: [
        { type: "inside", xAxisIndex: 0, startValue: 0, endValue: Math.max(0, n - 1), filterMode: "none" },
        { type: "slider", xAxisIndex: 0, startValue: 0, endValue: Math.max(0, n - 1), height: 16, bottom: 12, filterMode: "none", borderColor: LINE, fillerColor: "rgba(60,240,160,0.10)", handleStyle: { color: KEY }, textStyle: { color: FAINT, fontSize: 9 } },
      ],
      series: [
        ...(show.tpo && tpo ? [{ name: "Bars/Preis", type: "custom", renderItem: tpoRender, data: tpo.levels.map((l) => [0, l.p, l.count]), z: 1, silent: true }] : []),
        ...(show.vp && vp ? [{ name: "VolumeProfile", type: "custom", renderItem: vpRender, data: vp.levels.map((l) => [0, l.p, l.vol]), z: 1, silent: true }] : []),
        { name: "OHLC", type: "candlestick", data: candles.map((c) => [c.o, c.c, c.l, c.h]), itemStyle: { color: KEY, color0: RED, borderColor: KEY, borderColor0: RED, borderWidth: 1 }, z: 3, markLine: { silent: true, symbol: "none", data: vaLines } },
        ...(show.vwap ? [{ name: "VWAP", type: "line", data: vwap.map((v) => v), showSymbol: false, connectNulls: true, lineStyle: { color: GOLD, width: 1.5 }, z: 4, markPoint: { symbol: "pin", symbolSize: 1, data: [{ coord: [vwapAnchorBar, vwap[vwapAnchorBar] ?? candles[vwapAnchorBar]?.c], label: { show: true, formatter: "VWAP⚓", color: GOLD, fontSize: 9 } }] } }] : []),
        ...(show.bubbles ? [{ name: "Bubbles", type: "scatter", data: bubbles.map((b) => ({ value: [b.barIndex, b.p], symbolSize: 6 + (b.v / maxBub) * 26, itemStyle: { color: b.dir > 0 ? "rgba(60,240,160,0.35)" : "rgba(240,86,106,0.35)", borderColor: b.dir > 0 ? KEY : RED } })), z: 2 }] : []),
        { name: "Entries", type: "scatter", data: entries, symbolSize: 11, z: 6 },
        { name: "Exits", type: "scatter", data: exits, symbol: "diamond", symbolSize: 10, z: 7 },
        { name: "Selected", type: "scatter", data: [], z: 10, symbolSize: 16, markArea: { silent: true, itemStyle: { color: "rgba(34,184,240,0.08)" }, data: [] } },
      ],
    }, { notMerge: true });
  }, [props.candles, props.trades, props.vwap, props.vp, props.tpo, props.bubbles, props.show, props.bubbleCoverageBar]);

  useEffect(() => {
    const chart = chartRef.current; if (!chart) return;
    const { candles, trades, selected, focusKey } = latest.current; const n = candles.length;
    const changed = focusKey !== lastFocus.current; lastFocus.current = focusKey;
    const t = selected == null ? null : trades.find((x) => x.index === selected) ?? null;
    const s = latest.current.show;
    const selIdx = 3 + (s.vwap ? 1 : 0) + (s.bubbles ? 1 : 0) + (s.vp ? 1 : 0) + (s.tpo ? 1 : 0);
    if (!t) { if (changed && n > 0) chart.dispatchAction({ type: "dataZoom", startValue: 0, endValue: n - 1 }); chart.setOption({ series: Array.from({ length: selIdx }, () => ({})).concat([{ data: [], markArea: { data: [] } }]) as any }); return; }
    const exitBar = t.exit?.barIndex ?? t.entryBarIndex;
    const lo = Math.min(t.entryBarIndex, exitBar), hi = Math.max(t.entryBarIndex, exitBar);
    const sel: any[] = [{ value: [t.entryBarIndex, t.entryPrice], symbol: t.side === "Long" ? "triangle" : SHORT_DOWN, symbolSize: 18, itemStyle: { color: t.side === "Long" ? KEY : RED, borderColor: FG, borderWidth: 2 }, label: { show: true, position: t.side === "Long" ? "bottom" : "top", color: FG, fontSize: 10, formatter: `E ${px(t.entryPrice)}` } }];
    if (t.exit) sel.push({ value: [t.exit.barIndex, t.exit.price], symbol: "diamond", symbolSize: 16, itemStyle: { color: GOLD, borderColor: FG, borderWidth: 2 }, label: { show: true, position: t.side === "Long" ? "top" : "bottom", color: FG, fontSize: 10, formatter: `X ${px(t.exit.price)}` } });
    const patch: any[] = Array.from({ length: selIdx }, () => ({}));
    patch.push({ data: sel, markArea: { silent: true, itemStyle: { color: "rgba(34,184,240,0.08)" }, data: [[{ xAxis: lo }, { xAxis: hi }]] } });
    chart.setOption({ series: patch });
    const w = tradeWindow(t.entryBarIndex, exitBar, n);
    chart.dispatchAction({ type: "dataZoom", startValue: w.start, endValue: w.end });
  }, [props.selected, props.focusKey]);

  return <div className={`relative ${props.className ?? ""}`} data-cpbars={props.candles.length}><div ref={ref} className="absolute inset-0" /></div>;
}
