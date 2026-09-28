export type MarketDataProvider = "rithmic" | "csv" | "sierra" | "atas";

export interface MarketDataSettings {
  provider: MarketDataProvider;
  defaultSymbol: string;
  defaultInterval: string;
  lookbackCandles: number;
  timeoutSeconds: number;
  maxRetries: number;
}

const STORAGE_KEY = "marketdata-settings";

export const DEFAULT_SETTINGS: MarketDataSettings = {
  provider: "rithmic",
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
      // Ältere Versionen speicherten Rithmic-Zugangsdaten (inkl. Passwort) im Klartext -> entfernen.
      const { rithmic, ...parsed } = JSON.parse(raw) as Partial<MarketDataSettings> & { rithmic?: unknown };
      if (rithmic !== undefined) localStorage.setItem(STORAGE_KEY, JSON.stringify(parsed));
      return { ...DEFAULT_SETTINGS, ...parsed };
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