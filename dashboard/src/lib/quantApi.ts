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
  oosT: number[]; oosStartT?: number[]; oosEquity: number[]; oosMetrics: QuantMetric[];
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
  // Fächerchart: Band aus allen Läufen (Index 0 = Startkapital) + begrenzte Auswahl echter Pfade.
  bandP5: number[]; bandMedian: number[]; bandP95: number[];
  paths: number[][]; initialCapital: number; displayedPaths: number; totalPaths: number;
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

// --- Finaler Holdout (einmalige, eingefrorene, dauerhaft gespeicherte Auswertung) ---

export interface HoldoutFrozenConfig {
  campaignId: string; candidateReference: string; candidateTrialId?: string | null;
  strategyId: string; parameters: Record<string, string>;
  symbol: string; timeframeMinutes: number; initialCapital: number; quantity: number;
  stopLossTicks?: number | null; takeProfitTicks?: number | null;
  costs: {
    feePerSide: number; slippageTicks: number; tickSize: number; pointValue: number;
    applyFees: boolean; currency: string; isExampleProfile: boolean;
  };
  dataSha: string; holdoutFrom: string; holdoutTo: string; warmupBars: number; frequency: string; codeVersion: string;
}

export interface HoldoutEquityPoint { timeMs: number; barIndex: number; equity: number; totalEquity: number; }

export interface HoldoutTradeRecord {
  index: number; side: string; quantity: number; entryTimeMs: number; exitTimeMs: number;
  entryPrice: number; exitPrice: number; entryBarIndex: number; exitBarIndex: number;
  grossPnL: number; fees: number; netPnL: number; exitReason: string;
  stopLossPrice: number; takeProfitPrice: number; ambiguous: boolean; note?: string | null;
}

export type HoldoutStatus = "Reserved" | "Running" | "Completed" | "Failed" | "Cancelled";

export interface HoldoutEvaluationRecord {
  campaignId: string; runId: string; status: HoldoutStatus; config: HoldoutFrozenConfig;
  reservedUtc: string; startedUtc: string | null; completedUtc: string | null;
  usedDataSha?: string | null; holdoutBars: number; warmupBarsUsed: number;
  metrics: Record<string, number | null>;
  maxDrawdown: number | null; netProfit: number | null; finalEquity: number | null; tradeCount: number;
  equity: HoldoutEquityPoint[]; trades: HoldoutTradeRecord[];
  notes: string[]; statusReason?: string | null;
}

export interface HoldoutEvaluationResponse {
  ok: boolean; error?: string | null; campaignId: string;
  state: string; jobId?: string | null; alreadyExisted: boolean;
  holdoutFrom: string | null; holdoutTo: string | null;
  evaluation: HoldoutEvaluationRecord | null;
}

export interface HoldoutEvaluateRequest {
  run: RunRequest; options: QuantEvaluationOptions;
  candidateReference?: string | null; candidateTrialId?: string | null;
  warmupBars: number; confirm: boolean;
}

// --- Research-Lauf (eine Ablaufsteuerung koordiniert alle Prüfungen) ---

export type ResearchStepStatus =
  | "Pending" | "Running" | "Completed" | "NotComputable" | "Failed" | "Cancelled" | "Skipped";

export interface ResearchStepState {
  key: string; label: string; status: ResearchStepStatus;
  reason?: string | null; dataBasis?: string | null;
  startedUtc?: string | null; completedUtc?: string | null;
}

export interface ResearchStartRequest {
  run: RunRequest;
  options: QuantEvaluationOptions;
  campaign: CampaignInput;
  candidates: Record<string, string>[];
  selectionMetric: string;
  mode: string; trainBars: number; testBars: number; stepBars?: number | null;
  labelSpanBars: number; embargoBars: number; warmupBars: number; holdoutFraction: number;
  monteCarloSource: string; monteCarloMethod: string; monteCarloIterations: number;
  seed: number; blockLength?: number | null; capitalBarrier?: number | null;
  robustnessMetric: string;
  overfittingBlocks: number; estimateEffectiveTrials: boolean;
  benchmarkId?: string | null; strategyIsFullyFunded: boolean;
  isDemo: boolean;
}

export interface ResearchHoldoutProposal {
  candidateTrialId?: string | null; candidateReference?: string | null;
  parameters: Record<string, string>; reason: string;
  holdoutFrom?: string | null; holdoutTo?: string | null; warmupBars: number;
  available: boolean; unavailableReason?: string | null;
  existing?: HoldoutEvaluationResponse | null;
}

export interface ResearchRunRecord {
  runId: string; campaignId?: string | null; config: ResearchStartRequest;
  dataSha?: string | null; isDemo: boolean; createdUtc: string; completedUtc?: string | null;
  status: ResearchStepStatus; statusReason?: string | null; jobId?: string | null;
  // Herkunft/Datenbereiche (Befund B) + Leakage-Schutz (Befund A)
  dataFrom?: string | null; dataTo?: string | null;
  developmentToUtc?: string | null; holdoutFrom?: string | null; holdoutTo?: string | null;
  totalBars: number; developmentBars: number; holdoutBars: number;
  devRun?: RunRequest | null;
  steps: ResearchStepState[];
  analysis: QuantAnalyzeResponse | null;
  walkForward: QuantWalkForwardResponse | null;
  robustness: QuantRobustnessResponse | null;
  monteCarlo: QuantMonteCarloResponse | null;
  overfitting: QuantOverfittingResponse | null;
  holdoutProposal: ResearchHoldoutProposal | null;
  notes: string[];
  // Mehrstrategie-Vergleich: Zuordnung zu Gruppe/Familie
  campaignGroupId?: string | null; groupKey?: string | null;
  familyKey?: string | null; familyName?: string | null;
}

export interface ResearchRunResponse {
  ok: boolean; error?: string | null; alreadyRunning: boolean;
  jobId?: string | null; run: ResearchRunRecord | null;
}

// --- Mehrstrategie-Vergleich (eine Kampagne, mehrere Strategie-Familien) ---

export interface ResearchFamilyInput {
  key: string; strategyId: string; name?: string | null;
  candidates: Record<string, string>[];
}

export interface ResearchCampaignStartRequest {
  run: RunRequest; options: QuantEvaluationOptions; campaign: CampaignInput;
  families: ResearchFamilyInput[];
  selectionMetric: string;
  mode: string; trainBars: number; testBars: number; stepBars?: number | null;
  labelSpanBars: number; embargoBars: number; warmupBars: number; holdoutFraction: number;
  monteCarloSource: string; monteCarloMethod: string; monteCarloIterations: number;
  seed: number; blockLength?: number | null; capitalBarrier?: number | null;
  robustnessMetric: string; overfittingBlocks: number; estimateEffectiveTrials: boolean;
  benchmarkId?: string | null; strategyIsFullyFunded: boolean; isDemo: boolean;
}

export interface ComparisonFamilyRow {
  familyKey: string; familyName: string; strategyId: string;
  runId?: string | null; status: ResearchStepStatus;
  candidates: number; selectedCandidate?: string | null;
  oosObservations: number;
  oosReturn?: number | null; oosSharpe?: number | null; oosMaxDrawdown?: number | null;
  mcMedianFinal?: number | null; mcP5Final?: number | null; mcP95Final?: number | null;
  mcShareBelowStart?: number | null; mcMedianMaxDrawdown?: number | null; mcMedianLosingStreak?: number | null;
  pbo?: number | null; psr?: number | null; dsr?: number | null;
  sharedOosEquity?: number[] | null;
}

export interface ComparisonDifference {
  left: string; right: string;
  deltaMedianFinal: number; deltaP5Final: number; deltaP95Final: number;
  shareLeftBeatsRight: number; shareTie: number;
}

export interface ResearchComparison {
  available: boolean; unavailableReason?: string | null;
  commonObservations: number; fromT?: number | null; toT?: number | null;
  frequency: string; sufficient: boolean; minObservations: number;
  insufficientReason?: string | null; excludedIntervalMismatch: number;
  axisT: number[]; initialCapital: number;
  method: string; iterations: number; seed: number; blockLength: number;
  families: ComparisonFamilyRow[]; differences: ComparisonDifference[];
  assumptions: string[]; notes: string[];
}

export interface ResearchGroupHoldout {
  groupId: string; reserved: boolean; consumed: boolean;
  selectedFamilyKey?: string | null; selectedCampaignId?: string | null;
  candidateTrialId?: string | null; candidateReference?: string | null;
  evaluationReference?: string | null; reservedUtc?: string | null; consumedUtc?: string | null;
}

export interface ResearchCampaignResponse {
  ok: boolean; error?: string | null; alreadyRunning: boolean;
  groupId?: string | null;
  families: ResearchRunRecord[];
  comparison?: ResearchComparison | null;
  holdout?: ResearchGroupHoldout | null;
}

export interface ResearchGroupHoldoutResponse {
  ok: boolean; error?: string | null; groupId?: string | null;
  holdout?: ResearchGroupHoldout | null;
  evaluation?: HoldoutEvaluationResponse | null;
}

export interface ResearchCampaignSummary {
  groupId: string; name: string; createdUtc: string; families: number; status: ResearchStepStatus;
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

  holdout: (campaignId: string, signal?: AbortSignal) =>
    get<HoldoutEvaluationResponse>(`/campaigns/${encodeURIComponent(campaignId)}/holdout`, signal),
  evaluateHoldout: (campaignId: string, req: HoldoutEvaluateRequest, signal?: AbortSignal) =>
    post<HoldoutEvaluationResponse>(`/campaigns/${encodeURIComponent(campaignId)}/holdout/evaluate`, req, signal),

  // Research-Ablaufsteuerung
  startResearch: (req: ResearchStartRequest, signal?: AbortSignal) =>
    post<ResearchRunResponse>("/research/start", req, signal),
  research: (runId: string, signal?: AbortSignal) =>
    get<ResearchRunResponse>(`/research/${encodeURIComponent(runId)}`, signal),
  researchList: (campaignId?: string | null, signal?: AbortSignal) =>
    get<ResearchRunRecord[]>(`/research${campaignId ? `?campaignId=${encodeURIComponent(campaignId)}` : ""}`, signal),
  cancelResearch: (runId: string) => post<{ cancelled: boolean }>(`/research/${encodeURIComponent(runId)}/cancel`, {}),

  // Mehrstrategie-Vergleich
  startCampaign: (req: ResearchCampaignStartRequest, signal?: AbortSignal) =>
    post<ResearchCampaignResponse>("/research/campaign/start", req, signal),
  campaign: (groupId: string, signal?: AbortSignal) =>
    get<ResearchCampaignResponse>(`/research/campaign/${encodeURIComponent(groupId)}`, signal),
  campaignList: (signal?: AbortSignal) =>
    get<ResearchCampaignSummary[]>("/research/campaign", signal),
  evaluateGroupHoldout: (groupId: string, familyKey: string, request: HoldoutEvaluateRequest, signal?: AbortSignal) =>
    post<ResearchGroupHoldoutResponse>(`/research/campaign/${encodeURIComponent(groupId)}/holdout/evaluate`,
      { familyKey, request }, signal),
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
