import { useEffect, useRef } from "react";
import * as echarts from "echarts";
import type { Candle } from "@/lib/backtestApi";
import { tradeWindow, type ChartTrade } from "@/lib/replay";

/** Overlay-Zustand über dem Chartbereich (der Bereich bleibt immer sichtbar). */
export interface ChartStatus {
  kind: "loading" | "error" | "empty";
  text: string;
  actionLabel?: string;
  onAction?: () => void;
}

interface Props {
  /** Sichtbare Kerzen. Im Replay bereits auf die bekannten Kerzen 0..pos gekürzt. */
  candles: Candle[];
  /** Sichtbare Trades (Replay: nur bekannte; exit = null, solange der Exit noch unbekannt ist). */
  trades: ChartTrade[];
  /** Ausgewählter Trade (Originalindex) — nur in der Übersicht. */
  selected: number | null;
  /** Wird bei jedem Klick erhöht — erzwingt erneutes Fokussieren (auch beim selben Trade / Gesamtansicht). */
  focusKey: number;
  /** Replay: gespeicherte SL/TP-Level des offenen Trades (von Entry bis zur aktuellen Kerze). */
  openLevels?: { fromBar: number; toBar: number; sl: number; tp: number } | null;
  /** Replay: Anzahl Kerzen im mitlaufenden Fenster (null = frei / Gesamtansicht). */
  follow?: number | null;
  onSelectTrade?: (index: number) => void;
  status?: ChartStatus | null;
  className?: string;
}

const COL = { key: "#a2e65d", red: "#c1503f", gold: "#e8c069", cyan: "#7fc4b4", line: "#232120", panel: "#161513", fg: "#f4f2ed", faint: "#8b857a" };
const SHORT_DOWN = "path://M2,2 L22,2 L12,20 Z"; // nach unten zeigendes Dreieck (Short-Entry)

const fmtAxis = (ms: number) => new Date(ms).toISOString().slice(5, 16).replace("T", " ");
const fmtFull = (ms: number) => new Date(ms).toISOString().slice(0, 16).replace("T", " ") + " UTC";
const entryColor = (side: string) => (side === "Long" ? COL.key : COL.red);
const exitColor = (reason: string) => (reason === "TakeProfit" ? COL.key : reason === "StopLoss" ? COL.red : COL.gold);
const px = (v: number | null | undefined) => (typeof v === "number" ? v.toFixed(2) : "–");
// Label-Text steckt im Namen des Linien-Segments (bei 2-Punkt-markLines liefert echarts kein `value`).
const lineLabel = (p: any) => String(p?.name ?? "");

export function BacktestChart(props: Props) {
  const ref = useRef<HTMLDivElement | null>(null);
  const chartRef = useRef<echarts.ECharts | null>(null);
  const latest = useRef(props);
  latest.current = props;
  const lastFocusKey = useRef(props.focusKey);

  // --- Einmalig initialisieren: Resize-Beobachtung (Spalten ein-/ausklappen) + Marker-Klick. ---
  useEffect(() => {
    if (!ref.current) return;
    const chart = echarts.init(ref.current);
    chartRef.current = chart;
    const ro = new ResizeObserver(() => chart.resize());
    ro.observe(ref.current);
    const onClick = (p: any) => {
      const idx = p?.data?.tradeIndex;
      if (typeof idx === "number") latest.current.onSelectTrade?.(idx);
    };
    chart.on("click", onClick);
    return () => {
      ro.disconnect();
      chart.off("click", onClick);
      chart.dispose();
      chartRef.current = null;
    };
  }, []);

  // --- Daten (neu) zeichnen: Kerzen, Marker, im Replay SL/TP des offenen Trades + mitlaufendes Fenster. ---
  useEffect(() => {
    const chart = chartRef.current;
    if (!chart) return;
    const { candles, trades, openLevels, follow } = props;
    const n = candles.length;

    const entries = trades.map((t) => ({
      value: [t.entryBarIndex, t.entryPrice],
      tradeIndex: t.index,
      symbol: t.side === "Long" ? "triangle" : SHORT_DOWN,
      itemStyle: { color: entryColor(t.side) },
    }));
    const exits = trades.filter((t) => t.exit).map((t) => ({
      value: [t.exit!.barIndex, t.exit!.price],
      tradeIndex: t.index,
      itemStyle: {
        color: exitColor(t.exit!.reason),
        borderColor: t.exit!.ambiguous ? COL.fg : "transparent",
        borderWidth: t.exit!.ambiguous ? 2 : 0,
      },
    }));
    const levelLines = openLevels
      ? [
          [{ coord: [openLevels.fromBar, openLevels.sl], name: `SL ${px(openLevels.sl)}`, lineStyle: { color: COL.red } }, { coord: [openLevels.toBar, openLevels.sl] }],
          [{ coord: [openLevels.fromBar, openLevels.tp], name: `TP ${px(openLevels.tp)}`, lineStyle: { color: COL.key } }, { coord: [openLevels.toBar, openLevels.tp] }],
        ]
      : [];
    const start = follow ? Math.max(0, n - follow) : 0;
    const end = Math.max(0, n - 1);

    chart.setOption(
      {
        animation: false,
        grid: { left: 8, right: 64, top: 14, bottom: 46, containLabel: true },
        xAxis: {
          type: "category",
          data: candles.map((c) => fmtAxis(c.t)),
          boundaryGap: true,
          axisLine: { lineStyle: { color: COL.line } },
          axisLabel: { color: COL.faint, fontSize: 10, hideOverlap: true, alignMinLabel: "left", alignMaxLabel: "right" },
          axisTick: { show: false },
        },
        yAxis: {
          type: "value",
          scale: true, // refittet auf den sichtbaren Bereich (dataZoom filtert die Kerzen)
          position: "right",
          splitLine: { lineStyle: { color: COL.line } },
          axisLabel: { color: COL.faint, fontSize: 10, formatter: (v: number) => v.toFixed(2) },
        },
        tooltip: {
          trigger: "axis",
          axisPointer: { type: "cross", lineStyle: { color: "#3a3632" }, crossStyle: { color: "#3a3632" }, label: { backgroundColor: "#2e2b28" } },
          backgroundColor: COL.panel,
          borderColor: "#2e2b28",
          textStyle: { color: COL.fg, fontSize: 11, fontFamily: "JetBrains Mono, monospace" },
          formatter: (ps: any[]) => {
            const p = ps.find((x) => x.seriesType === "candlestick") ?? ps[0];
            const i = p?.dataIndex;
            const c = latest.current.candles[i];
            if (!c) return "";
            let html = `<b>${fmtFull(c.t)}</b> · Bar ${i}<br/>O ${px(c.o)} &nbsp;H ${px(c.h)}<br/>L ${px(c.l)} &nbsp;C ${px(c.c)}<br/>V ${c.v}`;
            for (const t of latest.current.trades) {
              if (t.entryBarIndex === i)
                html += `<br/><span style="color:${entryColor(t.side)}">▲ Entry #${t.index + 1} ${t.side} ${px(t.entryPrice)}</span>`;
              if (t.exit && t.exit.barIndex === i)
                html += `<br/><span style="color:${exitColor(t.exit.reason)}">◆ Exit #${t.index + 1} ${px(t.exit.price)} (${t.exit.reason}${t.exit.ambiguous ? ", mehrdeutig" : ""})</span>`;
            }
            return html;
          },
        },
        dataZoom: [
          { type: "inside", startValue: start, endValue: end, filterMode: "filter" },
          { type: "slider", startValue: start, endValue: end, height: 16, bottom: 12, filterMode: "filter", borderColor: COL.line, fillerColor: "rgba(162,230,93,0.10)", handleStyle: { color: COL.key }, textStyle: { color: COL.faint, fontSize: 9 }, labelFormatter: (v: number) => (candles[v] ? fmtAxis(candles[v].t) : "") },
        ],
        series: [
          {
            name: "OHLC",
            type: "candlestick",
            data: candles.map((c) => [c.o, c.c, c.l, c.h]),
            itemStyle: { color: COL.key, color0: COL.red, borderColor: COL.key, borderColor0: COL.red, borderWidth: 1 },
            markLine: {
              silent: true,
              symbol: "none",
              lineStyle: { type: "dashed", width: 1 },
              label: { show: true, color: COL.faint, fontSize: 9, position: "insideEndTop", formatter: lineLabel },
              data: levelLines,
            },
          },
          { name: "Entries", type: "scatter", data: entries, symbolSize: 11, z: 5 },
          { name: "Exits", type: "scatter", data: exits, symbol: "diamond", symbolSize: 10, z: 6 },
          // Hervorhebung des ausgewählten Trades (Effekt unten).
          { name: "Selected", type: "scatter", data: [], z: 10, symbolSize: 16 },
        ],
      },
      { notMerge: true },
    );
  }, [props.candles, props.trades, props.openLevels, props.follow]);

  // --- Auswahl/Fokus (Übersicht): springt zu Entry/Exit, hebt hervor, blendet SL/TP ein. ---
  useEffect(() => {
    const chart = chartRef.current;
    if (!chart) return;
    const { candles, trades, selected, focusKey } = latest.current;
    const n = candles.length;
    const focusChanged = focusKey !== lastFocusKey.current;
    lastFocusKey.current = focusKey;
    const t = selected == null ? null : trades.find((x) => x.index === selected) ?? null;

    if (!t) {
      chart.setOption({ series: [{}, {}, {}, { data: [], markArea: { data: [] }, markLine: { data: [] } }] });
      if (focusChanged && n > 0) chart.dispatchAction({ type: "dataZoom", startValue: 0, endValue: n - 1 });
      return;
    }

    const exitBar = t.exit?.barIndex ?? t.entryBarIndex;
    const lo = Math.min(t.entryBarIndex, exitBar);
    const hi = Math.max(t.entryBarIndex, exitBar);
    const selData: any[] = [
      {
        value: [t.entryBarIndex, t.entryPrice],
        symbol: t.side === "Long" ? "triangle" : SHORT_DOWN,
        symbolSize: 18,
        itemStyle: { color: entryColor(t.side), borderColor: COL.fg, borderWidth: 2 },
        label: { show: true, position: t.side === "Long" ? "bottom" : "top", color: COL.fg, fontSize: 10, formatter: `E ${px(t.entryPrice)}` },
      },
    ];
    if (t.exit)
      selData.push({
        value: [t.exit.barIndex, t.exit.price],
        symbol: "diamond",
        symbolSize: 16,
        itemStyle: { color: exitColor(t.exit.reason), borderColor: COL.fg, borderWidth: 2 },
        // Bei Entry+Exit in derselben Kerze Labels auf gegenüberliegende Seiten legen (unterscheidbar).
        label: { show: true, position: t.side === "Long" ? "top" : "bottom", color: COL.fg, fontSize: 10, formatter: `X ${px(t.exit.price)} (${t.exit.reason})` },
      });

    // SL/TP über die Trade-Dauer; bei Entry+Exit in derselben Kerze eine Kerze beidseitig, damit sichtbar.
    const l0 = lo === hi ? Math.max(0, lo - 1) : lo;
    const l1 = lo === hi ? Math.min(n - 1, hi + 1) : hi;
    const ml: any[] = [];
    if (t.stopLossPrice > 0)
      ml.push([{ coord: [l0, t.stopLossPrice], name: `SL ${px(t.stopLossPrice)}`, lineStyle: { color: COL.red } }, { coord: [l1, t.stopLossPrice] }]);
    if (t.takeProfitPrice > 0)
      ml.push([{ coord: [l0, t.takeProfitPrice], name: `TP ${px(t.takeProfitPrice)}`, lineStyle: { color: COL.key } }, { coord: [l1, t.takeProfitPrice] }]);

    chart.setOption({
      series: [
        {}, {}, {},
        {
          data: selData,
          markArea: { silent: true, itemStyle: { color: "rgba(127,196,180,0.10)" }, data: [[{ xAxis: lo }, { xAxis: hi }]] },
          markLine: {
            silent: true,
            symbol: "none",
            lineStyle: { type: "dashed", width: 1 },
            label: { show: true, color: COL.faint, fontSize: 9, position: "insideEndTop", formatter: lineLabel },
            data: ml,
          },
        },
      ],
    });
    const w = tradeWindow(t.entryBarIndex, exitBar, n);
    chart.dispatchAction({ type: "dataZoom", startValue: w.start, endValue: w.end });
  }, [props.selected, props.focusKey]);

  const st = props.status;
  return (
    <div
      className={`relative ${props.className ?? ""}`}
      // Sichtbarer Umfang (für Prüfungen/Automatisierung): Kerzen, Entry-/Exit-Marker, Auswahl.
      data-bars={props.candles.length}
      data-entries={props.trades.length}
      data-exits={props.trades.filter((t) => t.exit).length}
      data-selected={props.selected ?? ""}
    >
      <div ref={ref} className="absolute inset-0" />
      {st && (
        <div className={`absolute inset-0 flex items-center justify-center ${st.kind === "loading" && props.candles.length > 0 ? "bg-black/40" : ""}`}>
          <div className="max-w-md text-center px-4 py-3 rounded-lg border border-[var(--line)] bg-[var(--panel)] shadow-lg">
            {st.kind === "loading" && <div className="mx-auto mb-2 h-1 w-40 overflow-hidden rounded bg-[var(--bg-2)]"><div className="h-full w-1/3 animate-[bt-indet_1.1s_ease-in-out_infinite] bg-[var(--key)]" /></div>}
            <p className={`text-xs mono ${st.kind === "error" ? "text-[var(--red)]" : "text-[var(--fg-dim,#c9c4ba)]"}`}>{st.text}</p>
            {st.actionLabel && st.onAction && (
              <button onClick={st.onAction} className="mt-2 px-3 py-1 rounded bg-[var(--key)] text-black text-xs font-bold">{st.actionLabel}</button>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
