// Client für die Quant-Research-API des lokalen .NET-Backends (DevDashboard, Port 5034).
// Es werden ausschließlich echte Backend-Ergebnisse angezeigt — keine Mock-Daten, keine
// clientseitig erfundenen Kennzahlen. Fehlende Werte bleiben null und werden als solche dargestellt.

import type { RunRequest, CostProfile } from "./backtestApi";

const BASE: string =
  (import.meta as any).env?.VITE_QUANT_API || "http://localhost:5034/api/quant";

export interface QuantEvaluationOptions {
  frequency: "Bar" | "Daily" | "Weekly" | "Monthly";
  annualizationBasis: "Observed" | "Fixed";
  fixedPeriodsPerYear?: number | null;
  riskFreeAnnualRate: number;
  expectedShortfallAlpha: number;
  rollingWindow: number;
  minimumPeriods: number;
}

export const defaultEvaluationOptions: QuantEvaluationOptions = {
  frequency: "Daily",
  annualizationBasis: "Observed",
  fixedPeriodsPerYear: null,
  riskFreeAnnualRate: 0,
  expectedShortfallAlpha: 0.05,
  rollingWindow: 20,
  minimumPeriods: 20,
};

/** Kennzahl inklusive Methode, Eingaben, Grenzen und Datenumfang. value === null heißt „nicht berechenbar". */
export interface QuantMetric {
  key: string;
  label: string;
  value: number | null;
  unit: string;
  method: string;
  inputs: string;
  limitation?: string | null;
  sampleSize: number;
  unavailableReason?: string | null;
}

export interface QuantCurvePoint {
  t: number; realized: number; total: number; uwRealized: number; uwTotal: number; openQty: number;
}
export interface QuantPeriodReturn { period: string; t: number; return: number; observations: number; }
export interface QuantRollingPoint { t: number; sharpe: number | null; volatility: number | null; return: number | null; }
export interface QuantDrawdown {
  fraction: number; absolute: number; peakT: number | null; troughT: number | null; recoveryT: number | null;
  longestPeriods: number; longestDays: number; underwaterAtEnd: boolean;
}
export interface QuantDataIssue { severity: string; code: string; message: string; count: number; examples: number[]; }
export interface QuantDataQuality {
  symbol: string; timeframeMinutes: number; timezone: string; barCount: number;
  firstT: number | null; lastT: number | null; spanDays: number; observedDays: number; medianBarsPerDay: number;
  leadingPartialExcluded: boolean; trailingPartialExcluded: boolean; issues: QuantDataIssue[];
}
export interface QuantBenchmark {
  available: boolean; unavailableReason?: string | null; name: string; provenance: string;
  commonPeriods: number; metrics: QuantMetric[];
  t: number[]; strategyIndex: number[]; benchmarkIndex: number[];
  assumptions: string[]; warnings: string[];
}

export interface QuantAnalyzeResponse {
  ok: boolean; error?: string | null;
  symbol: string; source: string; timeframeMinutes: number; currency: string;
  frequency: string; periodsPerYear: number | null;
  annualizationNote: string; riskFreeNote: string; markToMarketNote: string;
  trades: number; initialBalance: number; finalEquityRealized: number; finalEquityTotal: number;
  curve: QuantCurvePoint[];
  metricsRealized: QuantMetric[]; metricsTotal: QuantMetric[]; activity: QuantMetric[];
  drawdownRealized: QuantDrawdown | null; drawdownTotal: QuantDrawdown | null;
  monthly: QuantPeriodReturn[]; rolling: QuantRollingPoint[]; rollingWindow: number;
  dataQuality: QuantDataQuality | null;
  benchmark: QuantBenchmark | null;
  costs: CostProfile | null;
  notes: string[];
}

export interface CampaignInput {
  id: string; name: string; hypothesis: string; searchSpace: string;
  selectionMetric: string; trialBudget: number; holdoutFraction: number;
}

export interface QuantWalkForwardRequest {
  run: RunRequest;
  options: QuantEvaluationOptions;
  mode: "Rolling" | "Anchored";
  trainBars: number; testBars: number; stepBars?: number | null;
  labelSpanBars: number; embargoBars: number; warmupBars: number; holdoutFraction: number;
  candidates: Record<string, string>[];
  selectionMetric: string;
  campaign: CampaignInput | null;
}

export interface QuantFold {
  index: number; trainFromT: number; trainToT: number; trainBars: number;
  testFromT: number; testToT: number; testBars: number;
  purgedBars: number; embargoBars: number; selectedCandidate: string | null;
  trainValue: number | null; testValue: number | null; testTrades: number; note?: string | null;
}

export interface QuantWalkForwardResponse {
  ok: boolean; error?: string | null;
  selectionMetric: string; mode: string; totalBars: number;
  folds: QuantFold[];
  holdoutFromT: number | null; holdoutToT: number | null; holdoutEvaluated: boolean;
  oosT: number[]; oosEquity: number[]; oosMetrics: QuantMetric[];
  candidateSharpes: Record<string, number | null>;
  campaignId: string | null; trialsRecorded: number; notes: string[];
}

export interface QuantDistribution {
  key: string; label: string; unit: string;
  min: number; p5: number; p25: number; median: number; p75: number; p95: number; max: number; mean: number;
  histogram: number[]; histogramMin: number; histogramMax: number;
}

export interface QuantMonteCarloResponse {
  ok: boolean; error?: string | null;
  method: string; sourceLabel: string; iterations: number; seed: number;
  blockLength: number; horizon: number; observations: number;
  finalCapital: QuantDistribution | null; maxDrawdown: QuantDistribution | null; losingStreak: QuantDistribution | null;
  shareOfRunsBelowStart: number; shareOfRunsBreachingBarrier: number | null; capitalBarrier: number | null;
  assumptions: string[]; notes: string[];
}

export interface QuantStressCell {
  id: string; label: string; feeMultiplier: number; slippageMultiplier: number;
  delayBars: number; value: number | null; tradeCount: number; error?: string | null;
}
export interface QuantStressBlock {
  dimension: string; metricKey: string; baseline: number | null;
  worst: number | null; shareBelowBaseline: number | null; relativeDegradation: number | null;
  cells: QuantStressCell[]; notes: string[];
}
export interface QuantRobustnessResponse {
  ok: boolean; error?: string | null;
  metric: string; baseline: number | null; blocks: QuantStressBlock[]; notes: string[];
}

export interface QuantOverfittingResponse {
  ok: boolean; error?: string | null;
  pbo: number | null; pboUnavailableReason?: string | null;
  candidates: number; blocks: number; combinations: number; observations: number;
  shareNegativeOutOfSample: number | null;
  pairs: { inSample: number; outOfSample: number }[];
  logits: number[];
  pboDefinitions: string[]; pboNotes: string[];
  psr: number | null; observedSharpePerPeriod: number | null;
  skewness: number | null; kurtosis: number | null; minimumTrackRecordLength: number | null;
  psrUnavailableReason?: string | null; psrDefinitions: string[];
  dsr: number | null; expectedMaxSharpeUnderNull: number | null;
  actualTrials: number; effectiveTrials: number; effectiveTrialsRationale: string;
  dsrUnavailableReason?: string | null; dsrDefinitions: string[]; dsrWarnings: string[];
  walkForward: QuantWalkForwardResponse | null;
  notes: string[];
}

export interface QuantCampaign {
  id: string; name: string; createdUtc: string; hypothesis: string; searchSpace: string;
  selectionMetric: string; selectionDirection: string; trialBudget: number; trialsUsed: number;
  holdoutFrom: string | null; holdoutTo: string | null; holdoutConsumed: boolean; locked: boolean;
}

export interface QuantTrial {
  id: string; campaignId: string; createdUtc: string; completedUtc: string | null;
  strategyId: string; strategyVersion: string; origin: string; originReference: string | null;
  parameters: Record<string, string>;
  dataSha256: string; dataSource: string; dataSymbol: string; dataBars: number;
  dataFrom: string | null; dataTo: string | null;
  codeVersion: string; seed: number;
  periodFrom: string | null; periodTo: string | null; periodRole: string;
  status: string; statusReason: string | null;
  metrics: Record<string, number | null>;
  costs: {
    feePerSide: number; slippageTicks: number; tickSize: number; pointValue: number;
    applyFees: boolean; currency: string; isExampleProfile: boolean;
  };
  tags: string[]; notes: string | null;
}

export interface QuantStatus {
  registryPath: string; campaigns: number; trials: number;
  benchmarks: string[]; benchmarkSource: string; benchmarkDirectory: string;
  papers: number; codeVersion: string; rithmicEnabled: boolean;
}

export interface QuantJobState {
  id: string; kind: string; status: "Running" | "Completed" | "Failed" | "Cancelled";
  progress: number; startedUtc: string; finishedUtc: string | null; elapsedMs: number;
  error?: string | null; result?: unknown;
}

async function post<T>(path: string, body: unknown, signal?: AbortSignal): Promise<T> {
  const res = await fetch(`${BASE}${path}`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
    signal,
  });
  const text = await res.text();
  const json = text ? JSON.parse(text) : {};
  if (!res.ok && !("ok" in json)) throw new Error(json?.error || `HTTP ${res.status}`);
  return json as T;
}

async function get<T>(path: string, signal?: AbortSignal): Promise<T> {
  const res = await fetch(`${BASE}${path}`, { signal });
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  return (await res.json()) as T;
}

export const quantApi = {
  status: (signal?: AbortSignal) => get<QuantStatus>("/status", signal),
  benchmarks: (signal?: AbortSignal) => get<string[]>("/benchmarks", signal),

  analyze: (
    run: RunRequest,
    options: QuantEvaluationOptions,
    benchmarkId: string | null,
    strategyIsFullyFunded: boolean,
    signal?: AbortSignal
  ) => post<QuantAnalyzeResponse>("/analyze", { run, options, benchmarkId, strategyIsFullyFunded }, signal),

  startWalkForward: (req: QuantWalkForwardRequest) => post<{ jobId: string }>("/jobs/walkforward", req),
  startMonteCarlo: (req: unknown) => post<{ jobId: string }>("/jobs/montecarlo", req),
  startRobustness: (req: unknown) => post<{ jobId: string }>("/jobs/robustness", req),
  startOverfitting: (req: unknown) => post<{ jobId: string }>("/jobs/overfitting", req),

  job: (id: string, signal?: AbortSignal) => get<QuantJobState>(`/jobs/${id}`, signal),
  cancelJob: (id: string) => post<{ cancelled: boolean }>(`/jobs/${id}/cancel`, {}),

  campaigns: (signal?: AbortSignal) => get<QuantCampaign[]>("/campaigns", signal),
  trials: (campaignId?: string | null, signal?: AbortSignal) =>
    get<QuantTrial[]>(`/trials${campaignId ? `?campaignId=${encodeURIComponent(campaignId)}` : ""}`, signal),
};

/**
 * Wartet auf das Ergebnis eines Hintergrund-Jobs. Meldet Fortschritt und bricht ab, sobald der
 * Job beendet, abgebrochen oder fehlgeschlagen ist. Das Backend erzwingt zusätzlich eine harte
 * Laufzeitgrenze — der Client pollt also nie endlos.
 */
export async function awaitJob<T>(
  jobId: string,
  onProgress: (progress: number, elapsedMs: number) => void,
  signal?: AbortSignal,
  intervalMs = 600
): Promise<T> {
  for (;;) {
    if (signal?.aborted) throw new DOMException("abgebrochen", "AbortError");
    const state = await quantApi.job(jobId, signal);
    onProgress(state.progress, state.elapsedMs);
    if (state.status === "Completed") return state.result as T;
    if (state.status === "Failed") throw new Error(state.error || "Berechnung fehlgeschlagen.");
    if (state.status === "Cancelled") throw new Error(state.error || "Berechnung abgebrochen.");
    await new Promise((r) => setTimeout(r, intervalMs));
  }
}

// ---------------------------------------------------------------------------------------------
// Formatierung: fehlende Werte werden sichtbar als „nicht berechenbar" ausgewiesen, nie als 0.
// ---------------------------------------------------------------------------------------------

export function formatMetric(m: QuantMetric): string {
  if (m.value === null || m.value === undefined || !Number.isFinite(m.value)) return "n. b.";
  return formatByUnit(m.value, m.unit);
}

export function formatByUnit(value: number, unit: string): string {
  switch (unit) {
    case "fraction":
    case "fraction/yr":
      return `${(value * 100).toFixed(2)} %`;
    case "currency":
      return value.toLocaleString("de-DE", { maximumFractionDigits: 2, minimumFractionDigits: 2 });
    case "days":
      return `${value.toFixed(1)} T`;
    case "count":
    case "periods":
      return value.toLocaleString("de-DE", { maximumFractionDigits: 0 });
    default:
      return Math.abs(value) >= 1000 ? value.toExponential(2) : value.toFixed(3);
  }
}

export function formatPercent(value: number | null | undefined, digits = 1): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return "n. b.";
  return `${(value * 100).toFixed(digits)} %`;
}

export function formatNumber(value: number | null | undefined, digits = 3): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return "n. b.";
  return value.toFixed(digits);
}

export function formatDate(ms: number | null | undefined): string {
  if (ms === null || ms === undefined) return "—";
  return new Date(ms).toISOString().replace("T", " ").slice(0, 16) + " UTC";
}
