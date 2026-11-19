// Client für den Bereich „Backtesting“ (Ein-Knopf-Bericht + Vergleich gespeicherter Berichte).
// Zeigt ausschließlich echte Backend-Ergebnisse — fehlende Werte bleiben null und werden mit Grund dargestellt.

import { apiUrl } from "./apiBase.ts";
import type { CostProfile, RunRequest, StrategyDef as BaseStrategyDef } from "./backtestApi";
import type {
  QuantBenchmark, QuantDataQuality, QuantMetric, QuantOverfittingResponse, QuantStressBlock,
  ResearchRunRecord, ResearchStepStatus,
} from "./quantApi";

const BASE = String(((import.meta as any).env ?? {}).VITE_BACKTESTING_API || apiUrl("/api/backtesting"));

export interface StrategyDef extends BaseStrategyDef { usesFixedStops: boolean; }

export interface BacktestingStartRequest {
  strategy: string; params: Record<string, string>; symbol: string; dataSourceId: string; path?: string | null;
  timeframeMinutes: number; fromUtc?: string | null; toUtc?: string | null; maxRows?: number | null;
  quantity: number; initialBalance: number; stopLossTicks?: number | null; takeProfitTicks?: number | null;
  slippageTicks?: number | null; feePerSideOverride?: number | null; applyFees: boolean;
  holdoutShare: number; trainSessions: number; testSessions: number; seed: number; simulationIterations: number;
  minTrades: number; minTrainTrades: number; selectionMetric: string; benchmarkId?: string | null; isDemo: boolean;
}

export interface PlannedCheck { key: string; label: string; status: "possible" | "notApplicable" | "insufficient" | "reserved"; reason?: string | null; }

export interface BacktestingPreview {
  ok: boolean; error?: string | null; source: string; dataKind: string; symbol: string; timeframeMinutes: number;
  from?: string | null; to?: string | null; bars: number; sessions: number; developmentSessions: number; holdoutSessions: number;
  holdoutFrom?: string | null; holdoutFraction: number; costs?: CostProfile | null; feeIsExample: boolean;
  strategy?: StrategyDef | null; candidates: number; sensitivityParameters: string[]; estimatedEngineRuns: number;
  checks: PlannedCheck[]; elapsedMs: number;
}

export interface ReportValue { value: number | null; reason?: string | null; }
export interface SimDistribution {
  key: string; label: string; unit: string; min: number; p5: number; p25: number; median: number; p75: number; p95: number;
  max: number; mean: number; histogram: number[]; histogramMin: number; histogramMax: number;
}
export interface SimulationResult {
  key: string; title: string; question: string; method: string; observationUnit: string; observations: number;
  iterations: number; seed: number; blockLength?: number | null; finalNet?: SimDistribution | null; maxDrawdown?: SimDistribution | null;
  maxDrawdownFraction?: SimDistribution | null; losingStreak?: SimDistribution | null; shareLossPaths?: number | null;
  finalNetInvariant?: boolean | null; unavailableReason?: string | null; notes: string[];
}
export interface OverviewFigure { key: string; label: string; value: number | null; unit: string; basis: string; reason?: string | null; detail?: string | null; }
export interface CheckResult { name: string; passed: boolean | null; detail: string; }
export interface SessionInfo { key: string; bars: number; firstT: number; lastT: number; short: boolean; }
export interface TradeStats {
  trades: number; wins: number; losses: number; breakeven: number; longs: number; shorts: number;
  grossPnL: number; fees: number; modeledSlippage: number; netPnL: number; preCostPnL: number;
  winRate: ReportValue; averageWin: ReportValue; averageLoss: ReportValue; payoffRatio: ReportValue; expectancy: ReportValue;
  profitFactor: ReportValue; exposure: ReportValue; averageHoldingMinutes: ReportValue; medianHoldingMinutes: ReportValue;
  largestWin: ReportValue; largestLoss: ReportValue; realizedNetPnL: number; unrealizedAtEnd: number; openAtEnd: number; ambiguousTrades: number;
}
export interface DrawdownResult {
  basis: string; maxDrawdown: number; maxDrawdownFraction: number; peakTime?: string | null; troughTime?: string | null;
  recoveryTime?: string | null; maxDrawdownOpen: boolean; recoveryDays?: number | null; longestUnderwaterDays: number;
  longestUnderwaterOpen: boolean; underwaterAtEnd: boolean; currentDrawdown: number; t: number[]; equity: number[]; drawdown: number[];
}
export interface SensitivityPoint {
  value: number; isBase: boolean; valid: boolean; error?: string | null; net: number; trades: number; profitFactor?: number | null;
  maxDrawdown: number; positiveSessions: number; sessions: number; sessionNets: number[];
}
export interface SensitivityParam { name: string; baseValue: number; points: SensitivityPoint[]; classification: string; }
export interface SensitivityCell { x: number; y: number; valid: boolean; net: number; trades: number; error?: string | null; }
export interface SensitivityPair { x: string; y: string; xValues: number[]; yValues: number[]; cells: SensitivityCell[]; baseX: number; baseY: number; }
export interface SessionResult { key: string; net: number; trades: number; }
export interface OosFold {
  index: number; trainFromT: number; trainToT: number; trainSessions: number; testFromT: number; testToT: number; testSessions: number;
  selectedCandidate?: string | null; selectedParameters?: Record<string, string> | null; trainValue?: number | null;
  testNet?: number | null; testTrades: number; baseNet: number; baseTrades: number; note?: string | null;
}
export interface OosVariant {
  key: string; label: string; status: string; reason?: string | null; sessionsNeeded: number; sessionsAvailable: number;
  trainSessions: number; testSessions: number; campaignId?: string | null; trialsRecorded: number; folds: OosFold[];
  oosNet: number; oosTrades: number; oosSessions: number; baseNetSameSessions: number; baseTradesSameSessions: number;
  oosMaxDrawdown: number; baseMaxDrawdownSameSessions: number; oosSessionResults: SessionResult[]; baseSessionResults: SessionResult[];
}
export interface GroupRow { key: string; trades: number; net: number; expectancy?: number | null; winRate?: number | null; sessions: number; from?: string | null; to?: string | null; thin: boolean; }
export interface GroupBreakdown { dimension: string; definition: string; rows: GroupRow[]; }
export interface ControlRow { key: string; label: string; net?: number | null; trades?: number | null; maxDrawdown?: number | null; basis: string; note?: string | null; }
export interface TradeRow {
  index: number; side: string; entryT: number; exitT: number; entryPrice: number; exitPrice: number; entryBar: number; exitBar: number;
  gross: number; fees: number; net: number; exitReason: string; ambiguous: boolean; closedAtEnd: boolean; session: string;
  mae?: number | null; mfe?: number | null; excursionLowerBound: boolean; excursionBasis: string; stopLoss: number; takeProfit: number;
}

export interface BacktestReport {
  provenance: {
    runId: string; strategyId: string; strategyName: string; strategyVersion: string; engineVersion: string; codeVersion: string;
    dataSha256: string; dataSource: string; dataKind: string; symbol: string; timeframeMinutes: number;
    dataFrom?: string | null; dataTo?: string | null; developmentTo?: string | null; holdoutFrom?: string | null; holdoutTo?: string | null;
    sessionsUsed: string[]; holdoutSessions: number; parameters: Record<string, string>; quantity: number; initialBalance: number;
    costModel?: CostProfile | null; fillModel: string[]; seeds: Record<string, number>; analysisProfile: Record<string, string>;
    dataFileReads: number; dataCacheHits: number;
  };
  overview: { figures: OverviewFigure[] };
  data?: {
    quality?: QuantDataQuality | null; dataKind: string; dataKindNotes: string[]; sessionDefinition: string; sessions: SessionInfo[];
    medianBarsPerSession: number; dstTransitions: string[]; intraSessionGaps: number; largestGapMinutes: number;
    temporalChecks: CheckResult[]; historyLimits: string[];
  } | null;
  result?: { basis: string; stats: TradeStats; costs?: CostProfile | null; evaluatedBars: number; signalsGenerated: number; missedEntrySkips: number; invalidStopSkips: number; notes: string[] } | null;
  equity?: { basis: string; closedTrades: DrawdownResult; markToMarket: DrawdownResult; losingStreak?: { longestCount: number; longestSum: number; from?: string | null; to?: string | null; longestSessionCount: number } | null; notes: string[] } | null;
  risk?: { basis: string; frequency: string; periodsPerYear?: number | null; annualizationNote: string; capitalBase: string; metrics: QuantMetric[]; notes: string[] } | null;
  sensitivity?: { basis: string; applicable: boolean; reason?: string | null; minTrades: number; definition: string; sessionKeys: string[]; parameters: SensitivityParam[]; pairs: SensitivityPair[]; engineRuns: number } | null;
  oos?: { selectionRule: string; tieRule: string; minTrainTrades: number; grid: Record<string, string>[]; warmupBars: number; leakageRules: string; holdoutNote: string; variants: OosVariant[]; primaryVariant?: string | null } | null;
  simulation?: {
    basis: string; basisReason: string; iterations: number; seed: number; methods: SimulationResult[];
    executionStress: { id: string; label: string; net: number; maxDrawdown: number; trades: number; kind: string }[];
    missedFills?: SimulationResult | null; delayedFills?: QuantStressBlock | null; notes: string[];
  } | null;
  costs?: {
    basis: string;
    breakeven: { trades: number; preCostExpectancy?: number | null; costPerTrade?: number | null; breakevenCostPerTrade: ReportValue; breakevenTicksPerSide: ReportValue; headroom: ReportValue;
      scenarios: { id: string; label: string; net: number; expectancy?: number | null; totalCosts: number; hypothetical: boolean }[] };
    costGrid?: QuantStressBlock | null; ambiguousTrades: number; ambiguousAssumedNet: number; ambiguousAlternativeNet: number;
    ambiguousRows: { tradeIndex: number; assumedNet: number; alternativeNet: number }[]; dataLimits: string[];
  } | null;
  stability?: { basis: string; minTradesPerGroup: number; breakdowns: GroupBreakdown[]; regimes: GroupBreakdown[]; rollingWindow: number; rolling: { tradeNumber: number; t: number; expectancy: number; drawdown: number }[]; notes: string[] } | null;
  concentration?: { basis: string; result: {
    totalNet: number; top5TradesShare: ReportValue; top5TradesNet: number; bestDayShare: ReportValue; bestDay?: string | null; bestDayNet: number;
    bestWeekShare: ReportValue; bestWeek?: string | null; bestWeekNet: number; netWithoutBestDay: number; netWithoutTop5Trades: number; netWithoutBestWeek: number;
    leaveOneOutAvailable: boolean; leaveOneOutReason?: string | null; leaveOneOut: { sessionKey: string; sessionNet: number; netWithout: number }[];
    leaveOneOutMin?: number | null; leaveOneOutMax?: number | null; signFlippingDays: number; } } | null;
  controls?: {
    basis: string; rows: ControlRow[];
    random?: { available: boolean; unavailableReason?: string | null; iterations: number; seed: number; tradesPerRun: number; net?: SimDistribution | null; shareRandomAtLeastStrategy?: number | null; strategyNet: number; definition: string } | null;
    benchmark?: QuantBenchmark | null; trials?: { variantsTested: number; selectionAttempts: number; registryTrials: number; breakdown: string[] } | null;
    overfitting?: QuantOverfittingResponse | null; notes: string[];
  } | null;
  trades?: { basis: string; candleRequest?: RunRequest | null; trades: TradeRow[]; excursionDefinition: string; tickReplayNote: string } | null;
}

export type ReportRun = ResearchRunRecord & { report?: BacktestReport | null; campaignKey?: string | null };
export interface ReportRunResponse { ok: boolean; error?: string | null; alreadyRunning?: boolean; jobId?: string | null; run?: ReportRun | null; }

export interface ReportSummary {
  runId: string; profile: string; createdUtc: string; completedUtc?: string | null; status: ResearchStepStatus; statusReason?: string | null;
  strategy: string; strategyName: string; parameters: Record<string, string>; symbol: string; fromUtc?: string | null; toUtc?: string | null;
  dataSha256?: string | null; stepsDone: number; stepsTotal: number; jobId?: string | null; figures: OverviewFigure[];
}

export interface CompareRow {
  runId: string; label: string; strategy: string; net: number; trades: number; netPerSession?: number | null; expectancyPerTrade?: number | null;
  maxDrawdown: number; worstSession: number; sessionStdDev?: number | null; netToDrawdown?: number | null;
  positiveSessionShare?: number | null; bestSessionShare?: number | null; sessions: number; equity: number[];
  ownBaseNet?: number | null; ownBaseDrawdown?: number | null; ownSimP95Drawdown?: number | null;
}
export interface CompareResult {
  ok: boolean; error?: string | null; fair: boolean; basis: string; conditions: string[]; problems: string[]; commonSessions: string[];
  rows: CompareRow[]; findings: string[]; engineRunsBefore: number; engineRunsAfter: number;
}

export interface JobState { id: string; status: string; progress: number; elapsedMs: number; error?: string | null; }

async function get<T>(path: string, signal?: AbortSignal): Promise<T> {
  const r = await fetch(`${BASE}${path}`, { headers: { Accept: "application/json" }, signal });
  if (!r.ok && r.status !== 404) throw new Error(`GET ${path} -> ${r.status}`);
  return (await r.json()) as T;
}
async function post<T>(path: string, body: unknown, signal?: AbortSignal): Promise<T> {
  const r = await fetch(`${BASE}${path}`, {
    method: "POST", headers: { "Content-Type": "application/json", Accept: "application/json" }, body: JSON.stringify(body), signal,
  });
  return (await r.json()) as T;
}

export const backtestingApi = {
  strategies: (signal?: AbortSignal) => get<StrategyDef[]>("/strategies", signal),
  preview: (req: BacktestingStartRequest, signal?: AbortSignal) => post<BacktestingPreview>("/preview", req, signal),
  start: (req: BacktestingStartRequest) => post<ReportRunResponse>("/start", req),
  reports: (signal?: AbortSignal) => get<ReportSummary[]>("/reports", signal),
  report: (id: string, signal?: AbortSignal) => get<ReportRunResponse>(`/reports/${encodeURIComponent(id)}`, signal),
  cancel: (id: string) => post<{ cancelled: boolean }>(`/reports/${encodeURIComponent(id)}/cancel`, {}),
  compare: (runIds: string[]) => post<CompareResult>("/compare", { runIds }),
  job: async (id: string): Promise<JobState | null> => {
    const r = await fetch(apiUrl(`/api/quant/jobs/${encodeURIComponent(id)}`), { headers: { Accept: "application/json" } });
    return r.ok ? ((await r.json()) as JobState) : null;
  },
};

// ---------------------------------------------------------------------------------------------
// Formatierung (rein darstellend)

export const STATUS_TEXT: Record<string, string> = {
  Pending: "wartet", Running: "läuft", Completed: "fertig", NotComputable: "nicht berechenbar", Failed: "technisch fehlgeschlagen",
  Cancelled: "abgebrochen", Skipped: "übersprungen", NotApplicable: "nicht anwendbar", InsufficientData: "zu wenig Daten",
};

export function money(v: number | null | undefined, digits = 2): string {
  if (v == null || !Number.isFinite(v)) return "–";
  return v.toLocaleString("de-DE", { minimumFractionDigits: digits, maximumFractionDigits: digits });
}
export function pct(v: number | null | undefined, digits = 1): string {
  if (v == null || !Number.isFinite(v)) return "–";
  return (v * 100).toLocaleString("de-DE", { minimumFractionDigits: digits, maximumFractionDigits: digits }) + " %";
}
export function num(v: number | null | undefined, digits = 2): string {
  if (v == null || !Number.isFinite(v)) return "–";
  return v.toLocaleString("de-DE", { minimumFractionDigits: 0, maximumFractionDigits: digits });
}
export function dt(v: string | number | null | undefined): string {
  if (v == null) return "–";
  const d = typeof v === "number" ? new Date(v) : new Date(v);
  if (Number.isNaN(d.getTime())) return "–";
  return d.toISOString().slice(0, 16).replace("T", " ");
}
export function day(v: string | number | null | undefined): string {
  if (v == null) return "–";
  const d = typeof v === "number" ? new Date(v) : new Date(v);
  return Number.isNaN(d.getTime()) ? "–" : d.toISOString().slice(0, 10);
}
