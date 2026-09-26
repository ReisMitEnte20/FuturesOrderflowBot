import { create } from "zustand";
import { persist } from "zustand/middleware";
import type {
  DashboardMetrics,
  Position,
  Trade,
  Order,
  Strategy,
  BacktestResult,
  TradeSignal,
  Candle,
  OrderFlowData,
  PipelineGraph,
  ConnectionStatus,
  SessionMessage,
  RithmicCredentials,
} from "@/types";

interface TradingState {
  metrics: DashboardMetrics | null;
  positions: Position[];
  trades: Trade[];
  orders: Order[];
  strategies: Strategy[];
  pipeline: PipelineGraph | null;
  backtestResults: BacktestResult[];
  signals: TradeSignal[];
  candles: Candle[];
  orderFlow: OrderFlowData[];
  sessionMessages: SessionMessage[];
  selectedSymbol: string;
  rithmicCredentials: RithmicCredentials | null;
  activeView: "live" | "signals" | "backtest" | "market" | "pipeline";
  connectionStatus: ConnectionStatus;
  isLoading: boolean;
  error: string | null;
  lastSignalAt: string | null;

  setRithmicCredentials: (creds: RithmicCredentials | null) => void;
  clearRithmicCredentials: () => void;

  setMetrics: (metrics: DashboardMetrics) => void;
  setPositions: (positions: Position[]) => void;
  addPosition: (position: Position) => void;
  updatePosition: (id: string, updates: Partial<Position>) => void;
  removePosition: (id: string) => void;

  setTrades: (trades: Trade[]) => void;
  addTrade: (trade: Trade) => void;

  setOrders: (orders: Order[]) => void;
  addOrder: (order: Order) => void;

  setStrategies: (strategies: Strategy[]) => void;
  setPipeline: (pipeline: PipelineGraph) => void;

  setBacktestResults: (results: BacktestResult[]) => void;

  setSignals: (signals: TradeSignal[]) => void;
  addSignal: (signal: TradeSignal) => void;
  setCandles: (candles: Candle[]) => void;
  addCandle: (candle: Candle) => void;
  setOrderFlow: (data: OrderFlowData[]) => void;
  addOrderFlow: (data: OrderFlowData) => void;
  setSelectedSymbol: (symbol: string) => void;
  setLastSignalAt: (timestamp: string) => void;

  setActiveView: (view: TradingState["activeView"]) => void;
  setConnectionStatus: (status: ConnectionStatus) => void;
  setLoading: (loading: boolean) => void;
  setError: (error: string | null) => void;

  addSessionMessage: (msg: Omit<SessionMessage, "id" | "timestamp">) => void;
  setSessionMessages: (msgs: SessionMessage[]) => void;
}

export const useTradingStore = create<TradingState>()(
  persist(
    (set) => ({
      metrics: null,
      positions: [],
      trades: [],
      orders: [],
      strategies: [],
      pipeline: null,
      backtestResults: [],
      signals: [],
      candles: [],
      orderFlow: [],
      sessionMessages: [],
      selectedSymbol: "NQ",
      rithmicCredentials: null,
      activeView: "live",
      connectionStatus: "disconnected",
      isLoading: false,
      error: null,
      lastSignalAt: null,

      setMetrics: (metrics) => set({ metrics }),
      setPositions: (positions) => set({ positions }),
      addPosition: (position) => set((state) => ({ positions: [position, ...state.positions] })),
      updatePosition: (id, updates) =>
        set((state) => ({
          positions: state.positions.map((p) => (p.id === id ? { ...p, ...updates } : p)),
        })),
      removePosition: (id) =>
        set((state) => ({
          positions: state.positions.filter((p) => p.id !== id),
        })),

      setTrades: (trades) => set({ trades }),
      addTrade: (trade) => set((state) => ({ trades: [trade, ...state.trades] })),

      setOrders: (orders) => set({ orders }),
      addOrder: (order) => set((state) => ({ orders: [order, ...state.orders] })),

      setStrategies: (strategies) => set({ strategies }),
      setPipeline: (pipeline) => set({ pipeline }),

      setBacktestResults: (results) => set({ backtestResults: results }),

      setSignals: (signals) => set({ signals }),
      addSignal: (signal) =>
        set((state) => ({
          signals: [signal, ...state.signals].slice(0, 100),
          lastSignalAt: signal.timestamp,
        })),
      setCandles: (candles) => set({ candles }),
      addCandle: (candle) =>
        set((state) => ({
          candles: [...state.candles, candle].slice(-500),
        })),
      setOrderFlow: (data) => set({ orderFlow: data }),
      addOrderFlow: (data) =>
        set((state) => ({
          orderFlow: [...state.orderFlow, data].slice(-500),
        })),
      setSelectedSymbol: (symbol) => set({ selectedSymbol: symbol }),
      setLastSignalAt: (timestamp) => set({ lastSignalAt: timestamp }),

      setActiveView: (view) => set({ activeView: view }),
      setConnectionStatus: (status) => set({ connectionStatus: status }),
      setLoading: (isLoading) => set({ isLoading }),
      setError: (error) => set({ error }),

      addSessionMessage: (msg) =>
        set((state) => ({
          sessionMessages: [
            {
              ...msg,
              id: `msg-${Date.now()}-${Math.random().toString(36).slice(2, 6)}`,
              timestamp: new Date().toISOString(),
            },
            ...state.sessionMessages,
          ].slice(0, 200),
        })),
      setSessionMessages: (msgs) => set({ sessionMessages: msgs }),
      setRithmicCredentials: (creds) => set({ rithmicCredentials: creds }),
      clearRithmicCredentials: () => set({ rithmicCredentials: null }),
    }),
    {
      name: "trading-storage",
      partialize: (state) => ({
        selectedSymbol: state.selectedSymbol,
        activeView: state.activeView,
        strategies: state.strategies,
        rithmicCredentials: state.rithmicCredentials,
      }),
    }
  )
);