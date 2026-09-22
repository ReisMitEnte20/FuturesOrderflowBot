export interface RithmicSettings {
  apiKey: string;
  apiSecret: string;
  baseUrl: string;
}

const STORAGE_KEY = "rithmic-settings";

export function loadRithmicSettings(): RithmicSettings {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (raw) {
      const parsed = JSON.parse(raw) as RithmicSettings;
      return { apiKey: parsed.apiKey || "", apiSecret: parsed.apiSecret || "", baseUrl: parsed.baseUrl || "https://api.rithmic.com" };
    }
  } catch {
    /* ignore */
  }
  return { apiKey: "", apiSecret: "", baseUrl: "https://api.rithmic.com" };
}

export function saveRithmicSettings(settings: RithmicSettings): void {
  localStorage.setItem(STORAGE_KEY, JSON.stringify(settings));
}

export function clearRithmicSettings(): void {
  localStorage.removeItem(STORAGE_KEY);
}