import { useEffect, useRef } from "react";
import * as echarts from "echarts";
import type { ChartTrade } from "@/lib/replay";
import { tradeWindow } from "@/lib/replay";

// Bid/Ask-Footprint DIREKT pro Kerze (ECharts custom series), visuell an Flowsurface orientiert.
// Je Bar + Preislevel: Bid links (rot), Ask rechts (gruen), Intensitaet = Volumen. KEINE Fenster-Summierung.
// Delta + Gesamtvolumen je Bar in der X-Achse + Tooltip. LOD: Zahlen nur bei genug Zoom, sonst nur Intensitaets-Zellen.
// Entry-/Exit-Marker + Fokus auf das Trade-Fenster bleiben erhalten. Rueckblickende Werte (siehe Hinweiszeile).

export interface FpBar { candle: { t: number; o: number; h: number; l: number; c: number; v: number; delta: number }; levels: { p: number; b: number; a: number }[]; }

interface Props {
  bars: FpBar[];
  trades: ChartTrade[];
  selected: number | null;
  focusKey: number;
  onSelectTrade?: (index: number) => void;
  className?: string;
}

const KEY = "#3cf0a0", RED = "#f0566a", FG = "#eefcf6", FAINT = "#6b8399", LINE = "#14273f", PANEL = "#0a1a30";
const SHORT_DOWN = "path://M2,2 L22,2 L12,20 Z";
const px = (v: number) => v.toFixed(2);

export function FootprintChart(props: Props) {
  const ref = useRef<HTMLDivElement | null>(null);
  const chartRef = useRef<echarts.ECharts | null>(null);
  const latest = useRef(props);
  latest.current = props;
  const lastFocus = useRef(props.focusKey);

  useEffect(() => {
    if (!ref.current) return;
    const chart = echarts.init(ref.current);
    chartRef.current = chart;
    const ro = new ResizeObserver(() => chart.resize());
    ro.observe(ref.current);
    const onClick = (p: any) => { const i = p?.data?.tradeIndex; if (typeof i === "number") latest.current.onSelectTrade?.(i); };
    chart.on("click", onClick);
    return () => { ro.disconnect(); chart.off("click", onClick); chart.dispose(); chartRef.current = null; };
  }, []);

  // --- Daten zeichnen ---
  useEffect(() => {
    const chart = chartRef.current;
    if (!chart) return;
    const { bars, trades } = props;
    const n = bars.length;

    // Flache Zell-Daten: [barIdx, price, bid, ask] + globale Max-Intensitaet.
    const cells: number[][] = [];
    let maxVol = 1;
    bars.forEach((b, i) => b.levels.forEach((lv) => { cells.push([i, lv.p, lv.b, lv.a]); maxVol = Math.max(maxVol, lv.b, lv.a); }));

    const entries = trades.map((t) => ({ value: [t.entryBarIndex, t.entryPrice], tradeIndex: t.index, symbol: t.side === "Long" ? "triangle" : SHORT_DOWN, itemStyle: { color: t.side === "Long" ? KEY : RED, borderColor: FG, borderWidth: 1 } }));
    const exits = trades.filter((t) => t.exit).map((t) => ({ value: [t.exit!.barIndex, t.exit!.price], tradeIndex: t.index, itemStyle: { color: "#f0c35a" } }));

    const renderItem = (_params: any, api: any) => {
      const bar = api.value(0), price = api.value(1), bid = api.value(2), ask = api.value(3);
      const c = api.coord([bar, price]);
      const s = api.size([1, 1]);
      const w = Math.max(1, s[0] - 1), h = Math.max(1, s[1] - 1);
      const x0 = c[0] - w / 2, y0 = c[1] - h / 2, cx = c[0];
      const bi = Math.min(1, bid / maxVol), ai = Math.min(1, ask / maxVol);
      const children: any[] = [
        { type: "rect", shape: { x: x0, y: y0, width: w / 2, height: h }, style: { fill: `rgba(240,86,106,${0.05 + bi * 0.55})` } },
        { type: "rect", shape: { x: cx, y: y0, width: w / 2, height: h }, style: { fill: `rgba(60,240,160,${0.05 + ai * 0.55})` } },
      ];
      // LOD: Zahlen nur, wenn Zelle gross genug (sonst reine Intensitaets-Heatmap -> keine Ueberlappung).
      if (w >= 30 && h >= 8) {
        const fs = Math.min(11, Math.max(8, h - 2));
        const tc = { fontSize: fs, fontFamily: "JetBrains Mono, monospace", fill: "#dff3ea" } as any;
        if (bid) children.push({ type: "text", style: { ...tc, text: String(bid), x: x0 + 3, y: c[1], textAlign: "left", textVerticalAlign: "middle" } });
        if (ask) children.push({ type: "text", style: { ...tc, text: String(ask), x: x0 + w - 3, y: c[1], textAlign: "right", textVerticalAlign: "middle" } });
        children.push({ type: "line", shape: { x1: cx, y1: y0, x2: cx, y2: y0 + h }, style: { stroke: "rgba(255,255,255,0.10)", lineWidth: 1 } });
      }
      return { type: "group", children };
    };

    chart.setOption(
      {
        animation: false,
        grid: { left: 8, right: 60, top: 12, bottom: 54, containLabel: true },
        xAxis: {
          type: "category",
          data: bars.map((_, i) => String(i)),
          boundaryGap: true,
          axisLine: { lineStyle: { color: LINE } },
          axisTick: { show: false },
          axisLabel: {
            color: FAINT, fontSize: 9, hideOverlap: true, lineHeight: 11, interval: "auto",
            formatter: (_v: string, idx: number) => { const b = bars[idx]; return b ? `Δ${b.candle.delta}\nV${b.candle.v}` : ""; },
          },
        },
        yAxis: { type: "value", scale: true, position: "right", splitLine: { lineStyle: { color: LINE } }, axisLabel: { color: FAINT, fontSize: 10, formatter: (v: number) => v.toFixed(0) } },
        tooltip: {
          trigger: "axis",
          axisPointer: { type: "cross", lineStyle: { color: "#2a4868" }, crossStyle: { color: "#2a4868" }, label: { backgroundColor: "#1d3654" } },
          backgroundColor: PANEL, borderColor: "#1d3654",
          textStyle: { color: FG, fontSize: 11, fontFamily: "JetBrains Mono, monospace" },
          formatter: (ps: any[]) => {
            const i = ps?.[0]?.dataIndex;
            const b = latest.current.bars[i];
            if (!b) return "";
            const d = new Date(b.candle.t).toISOString().slice(11, 19);
            let html = `<b>${d}</b> · Bar ${i}<br/>O ${px(b.candle.o)} H ${px(b.candle.h)} L ${px(b.candle.l)} C ${px(b.candle.c)}<br/>V ${b.candle.v} · Δ <span style="color:${b.candle.delta >= 0 ? KEY : RED}">${b.candle.delta}</span>`;
            const lv = [...b.levels].sort((a, z) => z.p - a.p);
            html += `<br/><span style="color:${FAINT}">Preis · Bid×Ask (Δ)</span>`;
            for (const l of lv) html += `<br/>${l.p} · <span style="color:${RED}">${l.b}</span>×<span style="color:${KEY}">${l.a}</span> (${l.a - l.b})`;
            for (const t of latest.current.trades) {
              if (t.entryBarIndex === i) html += `<br/><span style="color:${t.side === "Long" ? KEY : RED}">▲ Entry #${t.index + 1} ${t.side} ${px(t.entryPrice)}</span>`;
              if (t.exit && t.exit.barIndex === i) html += `<br/><span style="color:#f0c35a">◆ Exit #${t.index + 1} ${px(t.exit.price)}</span>`;
            }
            return html;
          },
        },
        dataZoom: [
          { type: "inside", xAxisIndex: 0, startValue: 0, endValue: Math.max(0, n - 1), filterMode: "filter" },
          { type: "slider", xAxisIndex: 0, startValue: 0, endValue: Math.max(0, n - 1), height: 16, bottom: 12, filterMode: "filter", borderColor: LINE, fillerColor: "rgba(60,240,160,0.10)", handleStyle: { color: KEY }, textStyle: { color: FAINT, fontSize: 9 } },
        ],
        series: [
          { name: "Footprint", type: "custom", renderItem, encode: { x: 0, y: 1, tooltip: [1, 2, 3] }, data: cells, z: 2 },
          { name: "Entries", type: "scatter", data: entries, symbolSize: 12, z: 6 },
          { name: "Exits", type: "scatter", data: exits, symbol: "diamond", symbolSize: 11, z: 7 },
          { name: "Selected", type: "scatter", data: [], z: 10, symbolSize: 16, markArea: { silent: true, itemStyle: { color: "rgba(34,184,240,0.08)" }, data: [] } },
        ],
      },
      { notMerge: true },
    );
  }, [props.bars, props.trades]);

  // --- Fokus auf ausgewaehlten Trade (Zoom-Fenster + Hervorhebung), ohne Neuberechnung ---
  useEffect(() => {
    const chart = chartRef.current;
    if (!chart) return;
    const { bars, trades, selected, focusKey } = latest.current;
    const n = bars.length;
    const changed = focusKey !== lastFocus.current;
    lastFocus.current = focusKey;
    const t = selected == null ? null : trades.find((x) => x.index === selected) ?? null;

    if (!t) {
      chart.setOption({ series: [{}, {}, {}, { data: [], markArea: { data: [] } }] });
      if (changed && n > 0) chart.dispatchAction({ type: "dataZoom", startValue: 0, endValue: n - 1 });
      return;
    }
    const exitBar = t.exit?.barIndex ?? t.entryBarIndex;
    const lo = Math.min(t.entryBarIndex, exitBar), hi = Math.max(t.entryBarIndex, exitBar);
    const sel: any[] = [{ value: [t.entryBarIndex, t.entryPrice], symbol: t.side === "Long" ? "triangle" : SHORT_DOWN, symbolSize: 18, itemStyle: { color: t.side === "Long" ? KEY : RED, borderColor: FG, borderWidth: 2 }, label: { show: true, position: t.side === "Long" ? "bottom" : "top", color: FG, fontSize: 10, formatter: `E ${px(t.entryPrice)}` } }];
    if (t.exit) sel.push({ value: [t.exit.barIndex, t.exit.price], symbol: "diamond", symbolSize: 16, itemStyle: { color: "#f0c35a", borderColor: FG, borderWidth: 2 }, label: { show: true, position: t.side === "Long" ? "top" : "bottom", color: FG, fontSize: 10, formatter: `X ${px(t.exit.price)}` } });
    chart.setOption({ series: [{}, {}, {}, { data: sel, markArea: { silent: true, itemStyle: { color: "rgba(34,184,240,0.08)" }, data: [[{ xAxis: lo }, { xAxis: hi }]] } }] });
    const w = tradeWindow(t.entryBarIndex, exitBar, n);
    chart.dispatchAction({ type: "dataZoom", startValue: w.start, endValue: w.end });
  }, [props.selected, props.focusKey]);

  return (
    <div className={`relative ${props.className ?? ""}`} data-fpbars={props.bars.length} data-fpentries={props.trades.length}>
      <div ref={ref} className="absolute inset-0" />
    </div>
  );
}
