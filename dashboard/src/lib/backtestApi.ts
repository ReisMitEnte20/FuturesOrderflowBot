// Realer Client für die OHLC-Backtest-API des lokalen .NET-Backends (DevDashboard, Port 5034).
// Keine Mock-Ergebnisse — spricht das echte Backend an.

const BASE: string =
  (import.meta as any).env?.VITE_BACKTEST_API || "http://localhost:5034/api/backtest";

export interface StrategyParamDef { key: string; label: string; type: string; default: string; }
export interface StrategyDef { id: string; name: string; description: string; isReference: boolean; params: StrategyParamDef[]; }
export interface InstrumentDef {
  symbol: string; tickSize: number; tickValue: number; pointValue: number;
  maxContracts: number; defaultStopLossTicks: number; defaultTakeProfitTicks: number;
}
export interface DataSourceDef { id: string; kind: string; label: string; available: boolean; note?: string | null; }

export interface RunRequest {
  dataSourceId: string;
  path?: string | null;
  symbol: string;
  timeframeMinutes: number;
  fromUtc?: string | null;
  maxRows: number;
  strategy: string;
  params: Record<string, string>;
  quantity: number;
  initialBalance: number;
  stopLossTicks?: number | null;
  takeProfitTicks?: number | null;
  slippageTicks?: number | null;
  feePerSideOverride?: number | null;
  applyFees: boolean;
  excludePartialEdges: boolean;
}

export interface BacktestStatistics {
  totalTrades: number; winningTrades: number; losingTrades: number; breakEvenTrades: number;
  grossProfit: number; netProfit: number; totalFees: number; totalSlippage: number;
  maxDrawdown: number; profitFactor: number | null; winRate: number;
  averageWinner: number; averageLoser: number; expectancy: number; averageNetPnLPerTrade: number;
  maxLosingStreak: number; maxWinningStreak: number; tradesPerDay: number;
  largestWin: number; largestLoss: number;
}

export type ExitReason = "StopLoss" | "TakeProfit" | "OppositeSignal" | "EndOfData" | "SessionClose";

export interface OhlcTrade {
  symbol: string; side: string; quantity: number;
  entryTime: string; exitTime: string; entryPrice: number; exitPrice: number;
  entryBarIndex: number; exitBarIndex: number;
  grossPnL: number; fees: number; netPnL: number;
  exitReason: ExitReason; ambiguous: boolean; note?: string | null;
}

export interface EquityPoint { barIndex: number; time: string; realizedNetPnL: number; equity: number; }

export interface DataInfo {
  source: string; symbol: string; timeframeMinutes: number; timezone: string;
  from?: string | null; to?: string | null; barCount: number; evaluatedBars: number;
  leadingPartialExcluded: boolean; trailingPartialExcluded: boolean;
}

export interface OhlcResult {
  status: string; message?: string | null;
  statistics: BacktestStatistics;
  trades: OhlcTrade[];
  equity: EquityPoint[];
  signalsGenerated: number; ambiguousTrades: number;
  data: DataInfo; strategyName: string;
  effectiveStopLossTicks: number; effectiveTakeProfitTicks: number; effectiveSlippageTicks: number;
  feePerSide: number; initialBalance: number; finalEquity: number;
}

export interface Candle { t: number; o: number; h: number; l: number; c: number; v: number; }
export interface ImportIssue { line: number; code: string; message: string; }

/** Tatsächlich verwendete Instrument-/Kostenwerte eines Laufs (mit Einheiten anzeigbar). */
export interface CostProfile {
  tickSize: number; tickValue: number; pointValue: number; currency: string;
  feePerSide: number; feeRoundTrip: number;
  slippageTicks: number; slippagePerSideDollars: number;
  applyFees: boolean; instrumentIsExample: boolean; feeIsExample: boolean;
}

export interface RunResponse {
  ok: boolean; error?: string | null;
  result?: OhlcResult | null;
  costProfile?: CostProfile | null;
  candles: Candle[];
  dataIssues: ImportIssue[];
}

async function get<T>(path: string): Promise<T> {
  const r = await fetch(`${BASE}${path}`, { headers: { Accept: "application/json" } });
  if (!r.ok) throw new Error(`GET ${path} -> ${r.status}`);
  return (await r.json()) as T;
}

export const backtestApi = {
  base: BASE,
  strategies: () => get<StrategyDef[]>("/strategies"),
  instruments: () => get<InstrumentDef[]>("/instruments"),
  dataSources: () => get<DataSourceDef[]>("/data-sources"),
  async run(req: RunRequest): Promise<RunResponse> {
    const r = await fetch(`${BASE}/run`, {
      method: "POST",
      headers: { "Content-Type": "application/json", Accept: "application/json" },
      body: JSON.stringify(req),
    });
    const body = (await r.json()) as RunResponse;
    // Backend liefert bei fachlichem Fehler 400 mit { ok:false, error }.
    return body;
  },
};
