export interface Position {
  id: string;
  symbol: string;
  side: "long" | "short";
  quantity: number;
  entryPrice: number;
  currentPrice: number;
  unrealizedPnL: number;
  realizedPnL: number;
  stopLoss?: number;
  takeProfit?: number;
  openedAt: string;
  strategy?: string;
}

export interface Trade {
  id: string;
  symbol: string;
  side: "buy" | "sell";
  quantity: number;
  price: number;
  commission: number;
  pnl: number;
  strategy: string;
  timestamp: string;
  exitReason?: string;
}

export interface Order {
  id: string;
  symbol: string;
  side: "buy" | "sell";
  type: "market" | "limit" | "stop";
  quantity: number;
  price?: number;
  stopPrice?: number;
  status: "pending" | "filled" | "cancelled" | "rejected";
  timestamp: string;
  filledQuantity?: number;
  avgFillPrice?: number;
}

export interface TradeSignal {
  signalId: string;
  strategyName: string;
  symbol: string;
  direction: "long" | "short";
  timestamp: string;
  referencePrice: number;
  confidence: number;
  suggestedQuantity?: number;
  suggestedStopLossTicks?: number;
  suggestedTakeProfitTicks?: number;
  reason: string;
  triggeredConditions: string[];
  failedConditions: string[];
}

export interface Candle {
  timestamp: string;
  open: number;
  high: number;
  low: number;
  close: number;
  volume: number;
}

export interface OrderFlowData {
  symbol: string;
  timestamp: string;
  price: number;
  volume: number;
  aggressiveBuyVolume: number;
  aggressiveSellVolume: number;
  delta: number;
  cvd: number;
}

export interface Strategy {
  id: string;
  name: string;
  enabled: boolean;
  description: string;
  parameters: Record<string, unknown>;
  performance: {
    totalTrades: number;
    winRate: number;
    totalPnL: number;
    maxDrawdown: number;
    sharpeRatio: number;
  };
  lastSignal?: string;
  pipeline?: PipelineNode[];
}

export interface PipelineNode {
  id: string;
  label: string;
  type: "source" | "indicator" | "signal" | "position" | "pnl";
  status: "ok" | "running" | "failed" | "queued" | "skipped";
  rows?: number;
}

export interface PipelineEdge {
  from: string;
  to: string;
  step?: string;
}

export interface PipelineGraph {
  nodes: PipelineNode[];
  edges: PipelineEdge[];
}

export interface BacktestResult {
  id: string;
  strategyId: string;
  strategyName: string;
  symbol: string;
  timeframe: string;
  startDate: string;
  endDate: string;
  initialCapital: number;
  finalCapital: number;
  totalReturn: number;
  maxDrawdown: number;
  sharpeRatio: number;
  winRate: number;
  totalTrades: number;
  equityCurve: EquityPoint[];
  drawdownCurve: DrawdownPoint[];
  monthlyReturns: MonthlyReturn[];
  trades: Trade[];
}

export interface EquityPoint {
  timestamp: string;
  equity: number;
}

export interface DrawdownPoint {
  timestamp: string;
  drawdown: number;
}

export interface MonthlyReturn {
  month: string;
  return: number;
}

export interface DashboardMetrics {
  totalPnL: number;
  dailyPnL: number;
  openPositions: number;
  totalTrades: number;
  winRate: number;
  maxDrawdown: number;
  sharpeRatio: number;
  currentEquity: number;
}

export interface MarketData {
  symbol: string;
  timestamp: string;
  open: number;
  high: number;
  low: number;
  close: number;
  volume: number;
}

export interface SessionMessage {
  id: string;
  role: "user" | "assistant";
  content: string;
  timestamp: string;
  toolLines?: ToolLine[];
}

export interface ToolLine {
  name: string;
  status: "running" | "ok" | "failed";
  detail?: string;
}

export type ConnectionStatus = "connecting" | "connected" | "disconnected" | "error";

export interface RithmicCredentials {
  userId: string;
  password: string;
  system: string;
  gateway: string;
}