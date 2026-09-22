export type MarketDataProvider = "rithmic" | "csv" | "sierra" | "atas";

export interface MarketDataSettings {
  provider: MarketDataProvider;
  rithmic: RithmicSettings;
  defaultSymbol: string;
  defaultInterval: string;
  lookbackCandles: number;
  timeoutSeconds: number;
  maxRetries: number;
}

export interface RithmicSettings {
  username: string;
  password: string;
  baseUrl: string;
}

const STORAGE_KEY = "marketdata-settings";

export const DEFAULT_SETTINGS: MarketDataSettings = {
  provider: "rithmic",
  rithmic: { username: "", password: "", baseUrl: "https://api.rithmic.com" },
  defaultSymbol: "NQ",
  defaultInterval: "1m",
  lookbackCandles: 1000,
  timeoutSeconds: 30,
  maxRetries: 3,
};

export function loadMarketDataSettings(): MarketDataSettings {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (raw) {
      const parsed = JSON.parse(raw) as Partial<MarketDataSettings>;
      return { ...DEFAULT_SETTINGS, ...parsed, rithmic: { ...DEFAULT_SETTINGS.rithmic, ...(parsed.rithmic || {}) } };
    }
  } catch {
    /* ignore */
  }
  return { ...DEFAULT_SETTINGS };
}

export function saveMarketDataSettings(settings: MarketDataSettings): void {
  localStorage.setItem(STORAGE_KEY, JSON.stringify(settings));
}

export function clearMarketDataSettings(): void {
  localStorage.removeItem(STORAGE_KEY);
}