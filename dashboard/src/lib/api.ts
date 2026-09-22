const apiClient = {
  async get(path: string, params?: Record<string, unknown>) {
    console.log(`[api] GET ${path}`, params);
    return { data: {} };
  },
  async post(path: string, body?: unknown) {
    console.log(`[api] POST ${path}`, body);
    return { data: {} };
  },
};

export const api = {
  async getHealth() {
    const r = await apiClient.get("/health");
    return r.data as { status: string; message: string };
  },

  async getMetrics() {
    const r = await apiClient.get("/dashboard/metrics");
    return r.data;
  },

  async getPositions() {
    const r = await apiClient.get("/positions");
    return r.data;
  },

  async getTrades() {
    const r = await apiClient.get("/trades");
    return r.data;
  },

  async getOrders() {
    const r = await apiClient.get("/orders");
    return r.data;
  },

  async getStrategies() {
    const r = await apiClient.get("/strategies");
    return r.data;
  },

  async getPipeline() {
    const r = await apiClient.get("/pipeline");
    return r.data;
  },

  async getBacktests() {
    const r = await apiClient.get("/backtests");
    return r.data;
  },

  async getSignals() {
    const r = await apiClient.get("/signals");
    return r.data;
  },

  async getCandles(symbol: string, limit = 100) {
    const r = await apiClient.get("/market-data", { params: { symbol, limit } });
    return r.data;
  },

  async getOrderFlow(symbol: string, limit = 100) {
    const r = await apiClient.get("/orderflow", { params: { symbol, limit } });
    return r.data;
  },

  async runBacktest(strategyId: string) {
    const r = await apiClient.post(`/backtests/${strategyId}`);
    return r.data;
  },

  async sendMessage(msg: { role: string; content: string }) {
    const r = await apiClient.post("/session/message", msg);
    return r.data;
  },

  async getSession() {
    const r = await apiClient.get("/session");
    return r.data;
  },
};