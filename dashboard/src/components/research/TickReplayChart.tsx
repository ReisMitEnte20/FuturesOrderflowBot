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

interface Props {
  slots: ReplaySlot[];
  currentSlot: number | null;
  lastPrice: number | null;
  follow: boolean;
  height?: number;
}

const statusText: Record<CandleStatus, string> = {
  abgeschlossen: "abgeschlossen",
  laufend: "laufend (bis Replay-Position)",
  "rand-unvollstaendig": "Randkerze – Intervall nur teilweise im Aufnahmefenster",
};

export function TickReplayChart({ slots, currentSlot, lastPrice, follow, height = 440 }: Props) {
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
    c.setOption({
      animation: false,
      backgroundColor: "transparent",
      grid: [{ ...grid, top: 16, height: "62%" }, { ...grid, top: "74%", height: "16%" }],
      xAxis: [
        { type: "category", data: labels, gridIndex: 0, boundaryGap: true, axisLabel: { show: false }, axisLine: { lineStyle: { color: "#2a2a2a" } } },
        { type: "category", data: labels, gridIndex: 1, boundaryGap: true, axisLabel, axisLine: { lineStyle: { color: "#2a2a2a" } } },
      ],
      yAxis: [
        { scale: true, gridIndex: 0, position: "right", axisLabel, splitLine: { lineStyle: { color: "#1c1c1c" } } },
        { scale: true, gridIndex: 1, position: "right", splitNumber: 2, axisLabel: { ...axisLabel, fontSize: 9 }, splitLine: { show: false } },
      ],
      dataZoom: [
        { type: "inside", xAxisIndex: [0, 1] },
        { type: "slider", xAxisIndex: [0, 1], height: 14, bottom: 2, borderColor: "#2a2a2a", textStyle: { color: "#6b8399" } },
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
      series: [{ type: "candlestick", xAxisIndex: 0, yAxisIndex: 0, data: [] }, { type: "bar", xAxisIndex: 1, yAxisIndex: 1, data: [] }],
    }, { notMerge: true });
  }, [labelsKey]); // eslint-disable-line react-hooks/exhaustive-deps

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
        },
        { data: vols },
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
