import { useEffect, useRef } from "react";
import * as echarts from "echarts";
import type { CandleStatus, ReplayCandle } from "@/lib/tickReplay";

// Kerzen-Chart des Tick-Replays. Zeigt ausschliesslich den von der Engine gelieferten Zustand zur Replay-Position:
// abgeschlossene Kerzen, die laufende Kerze (goldener Rand) und die Preislinie des letzten Trades. Kuenftige
// Intervalle bleiben leer. KEINE Animation zwischen Zustaenden (animation: false) – jede Aktualisierung ist exakt.

const UP = "#3cf0a0", DOWN = "#f0566a", RUN = "#e8c069", LINE = "#7fc4b4";

export interface ReplaySlot {
  /** Beschriftung des Intervallstarts in der Session-Zeitzone, z. B. "09:35". */
  label: string;
  /** Intervall in Session-Zeit, z. B. "09:35–09:40". */
  range: string;
  candle: ReplayCandle | null;
  status: CandleStatus | null;
}

/** Marker eines Journal-Trades – der Aufrufer uebergibt NUR bereits eingetretene Ereignisse. */
export interface ReplayMarker {
  kind: "signal" | "entry" | "exit";
  slot: number;
  price: number;
  side: string;
  label: string;
}

interface Props {
  slots: ReplaySlot[];
  currentSlot: number | null;
  lastPrice: number | null;
  follow: boolean;
  height?: number;
  markers?: ReplayMarker[];
  /** Optional: Delta (Buy − Sell) je Intervall und CVD – zusaetzliches Panel unter dem Volumen (gleiche Achse). */
  delta?: { delta: (number | null)[]; cvd: (number | null)[] } | null;
  /** Optional: Klick auf ein Intervall (z. B. Footprint dieser Kerze anzeigen). */
  onSlotClick?: (slot: number) => void;
  /** Optional: hervorgehobenes Intervall (Footprint-Auswahl). */
  selectedSlot?: number | null;
}

const markerPoint = (k: ReplayMarker) => {
  const long = k.side === "Long";
  const style = k.kind === "signal"
    ? { symbol: "diamond", symbolSize: 13, color: RUN, labelPos: "left" }
    : k.kind === "entry"
      ? { symbol: "triangle", symbolSize: 13, color: long ? UP : DOWN, labelPos: long ? "bottom" : "top", rotate: long ? 0 : 180 }
      : { symbol: "rect", symbolSize: 10, color: "#7fc4b4", labelPos: "right" };
  return {
    name: k.kind, coord: [k.slot, k.price], value: k.label, symbol: style.symbol, symbolSize: style.symbolSize,
    symbolRotate: (style as any).rotate ?? 0, itemStyle: { color: style.color, borderColor: "#0a0a0a", borderWidth: 1 },
    label: { show: true, position: style.labelPos, formatter: k.label, color: style.color, fontSize: 10, fontFamily: "var(--mono)" },
  };
};

const statusText: Record<CandleStatus, string> = {
  abgeschlossen: "abgeschlossen",
  laufend: "laufend (bis Replay-Position)",
  "rand-unvollstaendig": "Randkerze – Intervall nur teilweise im Aufnahmefenster",
};

export function TickReplayChart({ slots, currentSlot, lastPrice, follow, height = 440, markers = [], delta = null, onSlotClick, selectedSlot = null }: Props) {
  const withDelta = !!delta;
  const clickRef = useRef(onSlotClick);
  clickRef.current = onSlotClick;
  const el = useRef<HTMLDivElement | null>(null);
  const chart = useRef<echarts.ECharts | null>(null);
  const zoom = useRef<{ start: number; end: number } | null>(null);
  const slotsRef = useRef(slots);
  slotsRef.current = slots;
  const labelsKey = slots.map((s) => s.label).join("|");

  useEffect(() => {
    if (!el.current) return;
    const c = echarts.init(el.current);
    chart.current = c;
    const onResize = () => c.resize();
    window.addEventListener("resize", onResize);
    c.getZr().on("click", (e: any) => {
      if (!clickRef.current) return;
      const pt = [e.offsetX, e.offsetY];
      if (!c.containPixel({ gridIndex: 0 }, pt)) return;
      const v = c.convertFromPixel({ xAxisIndex: 0 }, pt) as any;
      const i = Array.isArray(v) ? v[0] : v;
      if (typeof i === "number" && i >= 0 && i < slotsRef.current.length) clickRef.current(Math.round(i));
    });
    c.on("datazoom", () => {
      const dz = (c.getOption() as any).dataZoom?.[0];
      const n = slotsRef.current.length;
      if (!dz || n === 0) return;
      const start = dz.startValue ?? Math.round(((dz.start ?? 0) / 100) * (n - 1));
      const end = dz.endValue ?? Math.round(((dz.end ?? 100) / 100) * (n - 1));
      zoom.current = { start, end };
    });
    return () => { window.removeEventListener("resize", onResize); c.dispose(); chart.current = null; };
  }, []);

  // Struktur (Achsen, Zoom) nur bei neuer Session/Intervallliste – Zoom/Pan des Nutzers bleibt sonst erhalten.
  useEffect(() => {
    const c = chart.current;
    if (!c) return;
    const labels = slots.map((s) => s.label);
    const axisLabel = { color: "#6b8399", fontSize: 10, fontFamily: "var(--mono)" };
    const grid = { left: 64, right: 72 };
    zoom.current = null;
    const axes = withDelta ? [0, 1, 2] : [0, 1];
    const xa = (gridIndex: number, show: boolean) => ({ type: "category", data: labels, gridIndex, boundaryGap: true, axisLabel: show ? axisLabel : { show: false }, axisLine: { lineStyle: { color: "#2a2a2a" } } });
    c.setOption({
      animation: false,
      backgroundColor: "transparent",
      grid: withDelta
        ? [{ ...grid, top: 16, height: "50%" }, { ...grid, top: "60%", height: "12%" }, { ...grid, top: "76%", height: "14%" }]
        : [{ ...grid, top: 16, height: "62%" }, { ...grid, top: "74%", height: "16%" }],
      xAxis: withDelta ? [xa(0, false), xa(1, false), xa(2, true)] : [xa(0, false), xa(1, true)],
      yAxis: [
        { scale: true, gridIndex: 0, position: "right", axisLabel, splitLine: { lineStyle: { color: "#1c1c1c" } } },
        { scale: true, gridIndex: 1, position: "right", splitNumber: 2, axisLabel: { ...axisLabel, fontSize: 9 }, splitLine: { show: false } },
        ...(withDelta ? [
          { scale: true, gridIndex: 2, position: "right", splitNumber: 2, axisLabel: { ...axisLabel, fontSize: 9 }, splitLine: { show: false }, name: "Δ", nameTextStyle: { color: "#6b8399", fontSize: 9 } },
          { scale: true, gridIndex: 2, position: "left", splitNumber: 2, axisLabel: { ...axisLabel, fontSize: 9, color: "#22b8f0" }, splitLine: { show: false }, name: "CVD", nameTextStyle: { color: "#22b8f0", fontSize: 9 } },
        ] : []),
      ],
      dataZoom: [
        { type: "inside", xAxisIndex: axes },
        { type: "slider", xAxisIndex: axes, height: 14, bottom: 2, borderColor: "#2a2a2a", textStyle: { color: "#6b8399" } },
      ],
      tooltip: {
        trigger: "axis", axisPointer: { type: "cross" }, backgroundColor: "#161513", borderColor: "#2a2a2a",
        textStyle: { color: "#ddd", fontSize: 11, fontFamily: "var(--mono)" },
        formatter: (params: any) => {
          const i = Array.isArray(params) ? params[0]?.dataIndex : params?.dataIndex;
          const s = slotsRef.current[i];
          if (!s) return "";
          if (!s.candle) return `${s.range}<br/>noch keine Trades bis zur Replay-Position`;
          const k = s.candle;
          return `${s.range} · ${s.status ? statusText[s.status] : ""}<br/>O ${k.open} · H ${k.high} · L ${k.low} · C ${k.close}`
            + `<br/>Vol ${k.volume} (Buy ${k.buyVolume} / Sell ${k.sellVolume}) · ${k.trades} Trades`;
        },
      },
      series: [
        { type: "candlestick", xAxisIndex: 0, yAxisIndex: 0, data: [] }, { type: "bar", xAxisIndex: 1, yAxisIndex: 1, data: [] },
        ...(withDelta ? [
          { type: "bar", name: "Delta", xAxisIndex: 2, yAxisIndex: 2, data: [] },
          { type: "line", name: "CVD", xAxisIndex: 2, yAxisIndex: 3, data: [], symbol: "none", lineStyle: { color: "#22b8f0", width: 1.2 }, connectNulls: false },
        ] : []),
      ],
    }, { notMerge: true });
  }, [labelsKey, withDelta]); // eslint-disable-line react-hooks/exhaustive-deps

  // Daten: gebuendelt (hoechstens einmal je Frame vom Aufrufer) – nur Serien, Zoom bleibt.
  useEffect(() => {
    const c = chart.current;
    if (!c) return;
    const candles = slots.map((s) => {
      if (!s.candle) return "-";
      const k = s.candle;
      const up = k.close >= k.open;
      const running = s.status === "laufend";
      const edge = s.status === "rand-unvollstaendig";
      return {
        value: [k.open, k.close, k.low, k.high],
        itemStyle: {
          color: up ? UP : DOWN, color0: up ? UP : DOWN,
          borderColor: running ? RUN : up ? UP : DOWN, borderColor0: running ? RUN : up ? UP : DOWN,
          borderWidth: running ? 2 : 1, opacity: edge ? 0.45 : 1,
        },
      };
    });
    const vols = slots.map((s) => {
      if (!s.candle) return "-";
      const k = s.candle;
      const col = s.status === "laufend" ? RUN : k.close >= k.open ? UP : DOWN;
      return { value: k.volume, itemStyle: { color: col, opacity: s.status === "laufend" ? 0.8 : 0.55 } };
    });
    c.setOption({
      series: [
        {
          data: candles,
          markLine: lastPrice == null ? { data: [] } : {
            symbol: "none", silent: true, animation: false,
            lineStyle: { color: LINE, type: "dashed", width: 1 },
            label: { position: "end", color: "#0a0a0a", backgroundColor: LINE, padding: [2, 4], formatter: () => String(lastPrice) },
            data: [{ yAxis: lastPrice }],
          },
          // Immer vollstaendig ersetzen (auch leer): Wechsel des Trades oder Zuruecksprung hinterlaesst keine alten Marker.
          markPoint: { silent: true, animation: false, data: markers.map(markerPoint) },
          markArea: selectedSlot == null || !slots[selectedSlot] ? { data: [] } : {
            silent: true, itemStyle: { color: "rgba(34,184,240,0.10)", borderColor: "rgba(34,184,240,0.6)", borderWidth: 1 },
            data: [[{ xAxis: selectedSlot }, { xAxis: selectedSlot }]],
          },
        },
        { data: vols },
        ...(delta ? [
          { data: delta.delta.map((d) => (d == null ? "-" : { value: d, itemStyle: { color: d >= 0 ? UP : DOWN, opacity: 0.75 } })) },
          { data: delta.cvd.map((v) => (v == null ? "-" : v)) },
        ] : []),
      ],
    }, { lazyUpdate: true });

    // Optional dem aktuellen Preis folgen: laufende Kerze im (gezoomten) Sichtbereich halten.
    if (follow && currentSlot != null && zoom.current) {
      const { start, end } = zoom.current;
      const span = Math.max(1, end - start);
      if (currentSlot > end || currentSlot < start) {
        const ns = Math.max(0, Math.min(slots.length - 1 - span, currentSlot - span + 1));
        zoom.current = { start: ns, end: ns + span };
        c.dispatchAction({ type: "dataZoom", startValue: ns, endValue: ns + span });
      }
    }
  });

  return <div ref={el} style={{ height }} className="w-full" />;
}
