import { useEffect, useRef } from "react";
import * as echarts from "echarts";
import { cn } from "@/lib/utils";
import {
  formatDate,
  formatNumber,
  formatPercent,
  type QuantCurvePoint,
  type QuantDistribution,
  type QuantFold,
  type QuantPeriodReturn,
  type QuantRollingPoint,
  type QuantStressBlock,
} from "@/lib/quantApi";

const COL = {
  key: "#3cf0a0",
  gold: "#f0c35a",
  red: "#f0566a",
  cyan: "#22b8f0",
  purple: "#9b8cff",
  line: "#14273f",
  line2: "#1d3654",
  panel: "#0a1a30",
  fg: "#eefcf6",
  faint: "#6b8399",
};

const baseAxis = {
  axisLine: { lineStyle: { color: COL.line } },
  axisLabel: { color: COL.faint, fontSize: 10 },
  axisTick: { show: false },
  splitLine: { lineStyle: { color: COL.line } },
};

const baseTooltip = {
  trigger: "axis" as const,
  backgroundColor: COL.panel,
  borderColor: COL.line2,
  textStyle: { color: COL.fg, fontSize: 11 },
};

/** Gemeinsame echarts-Einbindung mit ResizeObserver und sauberem Abbau. */
function useChart(option: echarts.EChartsOption | null, deps: unknown[]) {
  const ref = useRef<HTMLDivElement | null>(null);
  const chartRef = useRef<echarts.ECharts | null>(null);

  useEffect(() => {
    if (!ref.current) return;
    const chart = chartRef.current ?? echarts.init(ref.current);
    chartRef.current = chart;
    if (option) chart.setOption(option, true);
    const ro = new ResizeObserver(() => chart.resize());
    ro.observe(ref.current);
    return () => ro.disconnect();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps);

  useEffect(() => () => {
    chartRef.current?.dispose();
    chartRef.current = null;
  }, []);

  return ref;
}

const axisTime = (t: number) => new Date(t).toISOString().slice(0, 16).replace("T", " ");

// ---------------------------------------------------------------------------------------------
// Equity + Drawdown (realisiert und gesamt getrennt) und optional die Benchmark
// ---------------------------------------------------------------------------------------------

export function EquityDrawdownPanel({
  curve,
  benchmark,
  height = 300,
}: {
  curve: QuantCurvePoint[];
  benchmark?: { t: number[]; strategyIndex: number[]; benchmarkIndex: number[]; name: string } | null;
  height?: number;
}) {
  const times = curve.map((p) => axisTime(p.t));
  const option: echarts.EChartsOption | null = curve.length
    ? {
        animation: false,
        legend: {
          data: ["Equity gesamt (MTM)", "Equity realisiert", "Unterwasser gesamt"],
          textStyle: { color: COL.faint, fontSize: 10 },
          top: 0,
          itemHeight: 8,
        },
        grid: [
          { left: 8, right: 16, top: 28, height: "56%", containLabel: true },
          { left: 8, right: 16, bottom: 18, height: "20%", containLabel: true },
        ],
        xAxis: [
          { ...baseAxis, type: "category", data: times, gridIndex: 0, axisLabel: { show: false } },
          { ...baseAxis, type: "category", data: times, gridIndex: 1 },
        ],
        yAxis: [
          { ...baseAxis, type: "value", scale: true, gridIndex: 0 },
          {
            ...baseAxis,
            type: "value",
            gridIndex: 1,
            max: 0,
            axisLabel: { color: COL.faint, fontSize: 9, formatter: (v: number) => `${(v * 100).toFixed(0)} %` },
          },
        ],
        tooltip: { ...baseTooltip, axisPointer: { type: "cross" } },
        dataZoom: [{ type: "inside", xAxisIndex: [0, 1] }],
        series: [
          {
            name: "Equity gesamt (MTM)",
            type: "line",
            data: curve.map((p) => p.total),
            symbol: "none",
            lineStyle: { color: COL.key, width: 1.6 },
          },
          {
            name: "Equity realisiert",
            type: "line",
            data: curve.map((p) => p.realized),
            symbol: "none",
            lineStyle: { color: COL.cyan, width: 1.1, type: "dashed" },
          },
          {
            name: "Unterwasser gesamt",
            type: "line",
            xAxisIndex: 1,
            yAxisIndex: 1,
            data: curve.map((p) => p.uwTotal),
            symbol: "none",
            lineStyle: { color: COL.red, width: 1 },
            areaStyle: { color: "rgba(240,86,106,0.15)" },
          },
        ],
      }
    : null;

  const ref = useChart(option, [curve]);

  const benchOption: echarts.EChartsOption | null =
    benchmark && benchmark.t.length > 1
      ? {
          animation: false,
          legend: {
            data: ["Strategie (indexiert)", `${benchmark.name} (indexiert)`],
            textStyle: { color: COL.faint, fontSize: 10 },
            top: 0,
            itemHeight: 8,
          },
          grid: { left: 8, right: 16, top: 28, bottom: 24, containLabel: true },
          xAxis: { ...baseAxis, type: "category", data: benchmark.t.map(axisTime) },
          yAxis: { ...baseAxis, type: "value", scale: true },
          tooltip: baseTooltip,
          dataZoom: [{ type: "inside" }],
          series: [
            {
              name: "Strategie (indexiert)",
              type: "line",
              data: benchmark.strategyIndex,
              symbol: "none",
              lineStyle: { color: COL.key, width: 1.6 },
            },
            {
              name: `${benchmark.name} (indexiert)`,
              type: "line",
              data: benchmark.benchmarkIndex,
              symbol: "none",
              lineStyle: { color: COL.gold, width: 1.4 },
            },
          ],
        }
      : null;

  const benchRef = useChart(benchOption, [benchmark]);

  return (
    <div className="space-y-3">
      <div ref={ref} style={{ height }} data-testid="quant-equity-chart" data-points={curve.length} />
      {benchOption && (
        <div ref={benchRef} style={{ height: 220 }} data-testid="quant-benchmark-chart" data-points={benchmark?.t.length ?? 0} />
      )}
    </div>
  );
}

// ---------------------------------------------------------------------------------------------
// Rollierende Kennzahlen
// ---------------------------------------------------------------------------------------------

export function RollingChart({ points, window, height = 200 }: { points: QuantRollingPoint[]; window: number; height?: number }) {
  const option: echarts.EChartsOption | null = points.length
    ? {
        animation: false,
        legend: {
          data: [`Sharpe (${window} Perioden)`, `Volatilität p. a. (${window} Perioden)`],
          textStyle: { color: COL.faint, fontSize: 10 },
          top: 0,
          itemHeight: 8,
        },
        grid: { left: 8, right: 16, top: 28, bottom: 24, containLabel: true },
        xAxis: { ...baseAxis, type: "category", data: points.map((p) => axisTime(p.t)) },
        yAxis: [
          { ...baseAxis, type: "value", scale: true },
          {
            ...baseAxis,
            type: "value",
            scale: true,
            position: "right",
            splitLine: { show: false },
            axisLabel: { color: COL.faint, fontSize: 9, formatter: (v: number) => `${(v * 100).toFixed(0)} %` },
          },
        ],
        tooltip: baseTooltip,
        series: [
          {
            name: `Sharpe (${window} Perioden)`,
            type: "line",
            data: points.map((p) => p.sharpe),
            symbol: "none",
            connectNulls: false,
            lineStyle: { color: COL.purple, width: 1.3 },
          },
          {
            name: `Volatilität p. a. (${window} Perioden)`,
            type: "line",
            yAxisIndex: 1,
            data: points.map((p) => p.volatility),
            symbol: "none",
            connectNulls: false,
            lineStyle: { color: COL.gold, width: 1.1 },
          },
        ],
      }
    : null;

  const ref = useChart(option, [points]);
  if (!points.length) return null;
  return <div ref={ref} style={{ height }} data-testid="quant-rolling-chart" data-points={points.length} />;
}

// ---------------------------------------------------------------------------------------------
// Monatsrenditen als Tabelle mit Farbskala
// ---------------------------------------------------------------------------------------------

export function MonthlyReturnsTable({ monthly }: { monthly: QuantPeriodReturn[] }) {
  if (monthly.length === 0)
    return <div className="text-xs text-[var(--fg-faint)] px-3 py-3">Keine vollständigen Monate im Zeitraum.</div>;

  const years = Array.from(new Set(monthly.map((m) => m.period.slice(0, 4)))).sort();
  const byKey = new Map(monthly.map((m) => [m.period, m]));
  const maxAbs = Math.max(...monthly.map((m) => Math.abs(m.return)), 1e-9);

  return (
    <div className="overflow-x-auto">
      <table className="w-full text-[11px] mono">
        <thead>
          <tr className="text-[var(--fg-faint)]">
            <th className="text-left font-normal px-2 py-1">Jahr</th>
            {Array.from({ length: 12 }, (_, i) => (
              <th key={i} className="text-right font-normal px-2 py-1">
                {String(i + 1).padStart(2, "0")}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {years.map((y) => (
            <tr key={y} className="border-t border-[var(--line)]">
              <td className="px-2 py-1 text-[var(--fg-dim)]">{y}</td>
              {Array.from({ length: 12 }, (_, i) => {
                const key = `${y}-${String(i + 1).padStart(2, "0")}`;
                const m = byKey.get(key);
                if (!m) return <td key={i} className="px-2 py-1 text-right text-[var(--fg-faint)]">·</td>;
                const intensity = Math.min(0.42, (Math.abs(m.return) / maxAbs) * 0.42);
                const bg = m.return >= 0 ? `rgba(60,240,160,${intensity})` : `rgba(240,86,106,${intensity})`;
                return (
                  <td
                    key={i}
                    className="px-2 py-1 text-right tabular-nums text-[var(--fg)]"
                    style={{ backgroundColor: bg }}
                    title={`${key}: ${formatPercent(m.return, 2)} aus ${m.observations} Perioden`}
                  >
                    {(m.return * 100).toFixed(1)}
                  </td>
                );
              })}
            </tr>
          ))}
        </tbody>
      </table>
      <div className="text-[10px] text-[var(--fg-faint)] px-2 pt-1">
        Werte in Prozent. „·" = in diesem Monat keine beobachteten Perioden (es wird nichts ergänzt).
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------------------------
// Walk-forward-Zeitachse
// ---------------------------------------------------------------------------------------------

export function WalkForwardTimeline({
  folds,
  totalBars,
  holdoutFromT,
  holdoutToT,
}: {
  folds: QuantFold[];
  totalBars: number;
  holdoutFromT: number | null;
  holdoutToT: number | null;
}) {
  if (folds.length === 0) return null;
  const min = Math.min(...folds.map((f) => f.trainFromT));
  const max = Math.max(holdoutToT ?? 0, ...folds.map((f) => f.testToT));
  const span = Math.max(1, max - min);
  const pct = (t: number) => ((t - min) / span) * 100;

  return (
    <div className="space-y-1.5">
      {folds.map((f) => (
        <div key={f.index} className="flex items-center gap-2">
          <span className="mono text-[10px] text-[var(--fg-faint)] w-10 flex-shrink-0">#{f.index}</span>
          <div className="relative h-4 flex-1 bg-[var(--panel-2)] rounded-sm overflow-hidden">
            <div
              className="absolute inset-y-0 bg-[var(--cyan)]/35"
              style={{ left: `${pct(f.trainFromT)}%`, width: `${Math.max(0.4, pct(f.trainToT) - pct(f.trainFromT))}%` }}
              title={`Training: ${formatDate(f.trainFromT)} – ${formatDate(f.trainToT)} (${f.trainBars} Bars, ${f.purgedBars} gepurged)`}
            />
            <div
              className="absolute inset-y-0 bg-[var(--key)]/55"
              style={{ left: `${pct(f.testFromT)}%`, width: `${Math.max(0.4, pct(f.testToT) - pct(f.testFromT))}%` }}
              title={`Test: ${formatDate(f.testFromT)} – ${formatDate(f.testToT)} (${f.testBars} Bars)`}
            />
            {holdoutFromT !== null && holdoutToT !== null && (
              <div
                className="absolute inset-y-0 bg-[var(--gold)]/25 border-l border-[var(--gold)]/50"
                style={{ left: `${pct(holdoutFromT)}%`, width: `${Math.max(0.4, pct(holdoutToT) - pct(holdoutFromT))}%` }}
                title={`Finaler Holdout (gesperrt): ${formatDate(holdoutFromT)} – ${formatDate(holdoutToT)}`}
              />
            )}
          </div>
          <span className="mono text-[10px] w-[86px] text-right text-[var(--fg-dim)] truncate" title={f.selectedCandidate ?? ""}>
            {f.selectedCandidate ?? "—"}
          </span>
          <span className="mono text-[10px] w-14 text-right text-[var(--fg-faint)]">{formatNumber(f.trainValue, 2)}</span>
          <span
            className={cn(
              "mono text-[10px] w-14 text-right",
              f.testValue === null ? "text-[var(--fg-faint)] italic" : f.testValue >= 0 ? "text-[var(--key)]" : "text-[var(--red)]"
            )}
            title={
              f.testValue === null
                ? `Im Testfenster nicht berechenbar (${f.testTrades} Trades). Bei 0 Trades bleibt die Kurve flach — dann ist die Kennzahl nicht definiert.`
                : undefined
            }
          >
            {formatNumber(f.testValue, 2)}
          </span>
          <span className="mono text-[10px] w-16 text-right text-[var(--fg-faint)]" title="Trades im Testfenster">
            {f.testTrades} Tr.
          </span>
        </div>
      ))}
      <div className="flex items-center gap-4 text-[10px] text-[var(--fg-faint)] pt-1">
        <Legend color="var(--cyan)" label="Training (Auswahl nur hier)" />
        <Legend color="var(--key)" label="Test (out-of-sample)" />
        <Legend color="var(--gold)" label="Finaler Holdout — gesperrt" />
        <span className="ml-auto">{totalBars} Bars gesamt · Spalten rechts: Trainingswert / Testwert / Trades im Test</span>
      </div>
    </div>
  );
}

function Legend({ color, label }: { color: string; label: string }) {
  return (
    <span className="flex items-center gap-1.5">
      <span className="inline-block h-2 w-3 rounded-sm" style={{ backgroundColor: color, opacity: 0.6 }} />
      {label}
    </span>
  );
}

// ---------------------------------------------------------------------------------------------
// Verteilung (Monte Carlo)
// ---------------------------------------------------------------------------------------------

export function DistributionChart({ dist, height = 180 }: { dist: QuantDistribution; height?: number }) {
  const step = (dist.histogramMax - dist.histogramMin) / Math.max(1, dist.histogram.length);
  const labels = dist.histogram.map((_, i) => (dist.histogramMin + i * step).toPrecision(4));

  const option: echarts.EChartsOption = {
    animation: false,
    grid: { left: 8, right: 16, top: 16, bottom: 24, containLabel: true },
    xAxis: { ...baseAxis, type: "category", data: labels, axisLabel: { color: COL.faint, fontSize: 9, interval: 6 } },
    yAxis: { ...baseAxis, type: "value" },
    tooltip: { ...baseTooltip, trigger: "axis" },
    series: [
      {
        name: dist.label,
        type: "bar",
        data: dist.histogram,
        itemStyle: { color: COL.cyan },
        barCategoryGap: "10%",
        markLine: {
          symbol: "none",
          silent: true,
          data: [
            { xAxis: nearestIndex(labels, dist.p5), lineStyle: { color: COL.red, type: "dashed", width: 1 }, label: { formatter: "P5", color: COL.faint, fontSize: 9 } },
            { xAxis: nearestIndex(labels, dist.median), lineStyle: { color: COL.key, width: 1 }, label: { formatter: "Median", color: COL.faint, fontSize: 9 } },
            { xAxis: nearestIndex(labels, dist.p95), lineStyle: { color: COL.gold, type: "dashed", width: 1 }, label: { formatter: "P95", color: COL.faint, fontSize: 9 } },
          ],
        },
      },
    ],
  };

  const ref = useChart(option, [dist]);
  return <div ref={ref} style={{ height }} data-testid={`quant-dist-${dist.key}`} />;
}

function nearestIndex(labels: string[], value: number): number {
  let best = 0;
  let bestD = Infinity;
  labels.forEach((l, i) => {
    const d = Math.abs(Number(l) - value);
    if (d < bestD) { bestD = d; best = i; }
  });
  return best;
}

// ---------------------------------------------------------------------------------------------
// Kosten-Heatmap
// ---------------------------------------------------------------------------------------------

export function CostHeatmap({ block }: { block: QuantStressBlock }) {
  const fees = Array.from(new Set(block.cells.map((c) => c.feeMultiplier))).sort((a, b) => a - b);
  const slips = Array.from(new Set(block.cells.map((c) => c.slippageMultiplier))).sort((a, b) => a - b);
  const values = block.cells.map((c) => c.value).filter((v): v is number => v !== null);
  const min = values.length ? Math.min(...values) : 0;
  const max = values.length ? Math.max(...values) : 1;

  return (
    <div className="overflow-x-auto">
      <table className="text-[11px] mono">
        <thead>
          <tr className="text-[var(--fg-faint)]">
            <th className="px-2 py-1 text-left font-normal">Gebühren ↓ / Slippage →</th>
            {slips.map((s) => (
              <th key={s} className="px-3 py-1 text-right font-normal">×{s}</th>
            ))}
          </tr>
        </thead>
        <tbody>
          {fees.map((f) => (
            <tr key={f} className="border-t border-[var(--line)]">
              <td className="px-2 py-1 text-[var(--fg-dim)]">×{f}</td>
              {slips.map((s) => {
                const cell = block.cells.find((c) => c.feeMultiplier === f && c.slippageMultiplier === s);
                if (!cell || cell.value === null)
                  return (
                    <td key={s} className="px-3 py-1 text-right text-[var(--fg-faint)] italic" title={cell?.error ?? "nicht berechenbar"}>
                      n. b.
                    </td>
                  );
                const norm = max > min ? (cell.value - min) / (max - min) : 1;
                const bg = cell.value >= 0 ? `rgba(60,240,160,${0.10 + norm * 0.32})` : `rgba(240,86,106,${0.10 + (1 - norm) * 0.32})`;
                return (
                  <td
                    key={s}
                    className="px-3 py-1 text-right tabular-nums text-[var(--fg)]"
                    style={{ backgroundColor: bg }}
                    title={`${cell.label} — ${cell.tradeCount} Trades`}
                  >
                    {formatNumber(cell.value, 2)}
                  </td>
                );
              })}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

// ---------------------------------------------------------------------------------------------
// PBO: In-Sample gegen Out-of-Sample
// ---------------------------------------------------------------------------------------------

export function PboScatter({
  pairs,
  logits,
  height = 240,
}: {
  pairs: { inSample: number; outOfSample: number }[];
  logits: number[];
  height?: number;
}) {
  const scatterOption: echarts.EChartsOption | null = pairs.length
    ? {
        animation: false,
        grid: { left: 8, right: 16, top: 16, bottom: 32, containLabel: true },
        xAxis: { ...baseAxis, type: "value", scale: true, name: "In-Sample", nameTextStyle: { color: COL.faint, fontSize: 10 } },
        yAxis: { ...baseAxis, type: "value", scale: true, name: "Out-of-Sample", nameTextStyle: { color: COL.faint, fontSize: 10 } },
        tooltip: {
          ...baseTooltip,
          trigger: "item",
          formatter: (p: any) => `IS ${p.value[0].toFixed(3)} → OOS ${p.value[1].toFixed(3)}`,
        },
        series: [
          {
            type: "scatter",
            symbolSize: 5,
            data: pairs.map((p) => [p.inSample, p.outOfSample]),
            itemStyle: { color: COL.cyan, opacity: 0.65 },
            markLine: {
              symbol: "none",
              silent: true,
              data: [{ yAxis: 0, lineStyle: { color: COL.red, type: "dashed", width: 1 } }],
            },
          },
        ],
      }
    : null;

  const scatterRef = useChart(scatterOption, [pairs]);

  const bins = 21;
  const lo = logits.length ? Math.min(...logits) : -1;
  const hi = logits.length ? Math.max(...logits) : 1;
  const hist = new Array(bins).fill(0);
  logits.forEach((l) => {
    const i = hi > lo ? Math.floor(((l - lo) / (hi - lo)) * (bins - 1)) : 0;
    hist[Math.max(0, Math.min(bins - 1, i))]++;
  });
  const labels = hist.map((_, i) => (lo + ((hi - lo) * i) / (bins - 1)).toFixed(2));

  const histOption: echarts.EChartsOption | null = logits.length
    ? {
        animation: false,
        grid: { left: 8, right: 16, top: 16, bottom: 28, containLabel: true },
        xAxis: { ...baseAxis, type: "category", data: labels, axisLabel: { color: COL.faint, fontSize: 9, interval: 3 } },
        yAxis: { ...baseAxis, type: "value" },
        tooltip: baseTooltip,
        series: [
          {
            type: "bar",
            data: hist.map((v, i) => ({ value: v, itemStyle: { color: Number(labels[i]) < 0 ? COL.red : COL.key } })),
            barCategoryGap: "8%",
          },
        ],
      }
    : null;

  const histRef = useChart(histOption, [logits]);

  return (
    <div className="grid gap-3 lg:grid-cols-2">
      <div>
        <div className="text-[11px] text-[var(--fg-faint)] px-1 pb-1">
          In-Sample gegen Out-of-Sample je CSCV-Kombination. Punkte unter der roten Linie verlieren out-of-sample.
        </div>
        <div ref={scatterRef} style={{ height }} data-testid="quant-pbo-scatter" data-points={pairs.length} />
      </div>
      <div>
        <div className="text-[11px] text-[var(--fg-faint)] px-1 pb-1">
          Verteilung des Logits λ. Rot (λ &lt; 0) = die in-sample beste Variante lag out-of-sample unter dem Median.
        </div>
        <div ref={histRef} style={{ height }} data-testid="quant-pbo-logits" />
      </div>
    </div>
  );
}
