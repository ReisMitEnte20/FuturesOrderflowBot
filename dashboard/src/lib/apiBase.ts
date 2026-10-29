// Zentrale API-Basisadresse des lokalen .NET-Backends (DevDashboard-API-Host, Standard-Port 5034).
// EINZIGE Stelle, an der die Backend-Wurzel konfiguriert wird — keine verteilten, hartkodierten
// localhost-Adressen mehr in einzelnen Clients/Komponenten.
//
// Konfiguration: VITE_API_URL (z. B. in dashboard/.env.local) setzt die Backend-Wurzel. So laesst sich das
// React-Dashboard spaeter auch gegen ein Backend auf einem anderen Rechner betreiben, ohne Code zu aendern.
// Die bisherigen, feiner granularen Overrides (VITE_BACKTEST_API/VITE_QUANT_API/VITE_RITHMIC_API) bleiben
// rueckwaertskompatibel erhalten und haben, wenn gesetzt, Vorrang vor der abgeleiteten Adresse.

const env = (import.meta as any).env ?? {};

/** Backend-Wurzel ohne abschliessenden Schraegstrich (Standard: lokaler .NET-Host auf 5034). */
export const API_ROOT: string = String(env.VITE_API_URL || "http://127.0.0.1:5034").replace(/\/+$/, "");

/** Haengt einen /api-Pfad an die zentrale Backend-Wurzel. */
export const apiUrl = (path: string): string => `${API_ROOT}${path.startsWith("/") ? path : `/${path}`}`;

/** OHLC-Backtest-API (Simulation-only). */
export const BACKTEST_API: string = String(env.VITE_BACKTEST_API || apiUrl("/api/backtest"));

/** Quant-Research-API (Auswertung/Validierung). */
export const QUANT_API: string = String(env.VITE_QUANT_API || apiUrl("/api/quant"));

/** Rithmic-Marktdaten-API (standardmaessig im Backend deaktiviert). */
export const RITHMIC_API: string = String(env.VITE_RITHMIC_API || apiUrl("/api/rithmic"));
