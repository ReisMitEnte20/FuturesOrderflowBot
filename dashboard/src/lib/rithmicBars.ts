// Reine, testbare Hilfsfunktionen für Rithmic-Marktdaten (keine React-/Store-Abhängigkeit).
import type { Candle, OrderFlowData } from "../types/index.ts";

/** Börse je Root-Symbol (Rithmic braucht Symbol + Exchange). */
export function exchangeFor(root: string): string {
  switch (root) {
    case "CL":
      return "NYMEX";
    case "GC":
      return "COMEX";
    case "ZN":
      return "CBOT";
    default:
      return "CME";
  }
}

export interface RithmicQuote {
  symbol: string;
  exchange: string;
  bid?: number | null;
  bidSize?: number | null;
  ask?: number | null;
  askSize?: number | null;
  lastPrice?: number | null;
  lastSize?: number | null;
  updatedAt?: string | null;
}

export interface RithmicTimeBar {
  symbol: string;
  endTime: string;
  open: number;
  high: number;
  low: number;
  close: number;
  volume: number;
  bidVolume: number;
  askVolume: number;
}

/**
 * Wandelt Rithmic-Bars in Kerzen + Orderflow. Rithmic liefert je Bar das am Ask gehandelte Volumen
 * (Käufer aggressiv) und das am Bid gehandelte Volumen (Verkäufer aggressiv) – echte Daten, nichts geschätzt.
 */
export function mapBars(bars: RithmicTimeBar[]): { candles: Candle[]; orderFlow: OrderFlowData[] } {
  const sorted = [...bars].sort((a, b) => a.endTime.localeCompare(b.endTime));
  let cvd = 0;
  const candles: Candle[] = [];
  const orderFlow: OrderFlowData[] = [];
  for (const b of sorted) {
    candles.push({ timestamp: b.endTime, open: b.open, high: b.high, low: b.low, close: b.close, volume: b.volume });
    const delta = b.askVolume - b.bidVolume;
    cvd += delta;
    orderFlow.push({
      symbol: b.symbol,
      timestamp: b.endTime,
      price: b.close,
      volume: b.volume,
      aggressiveBuyVolume: b.askVolume,
      aggressiveSellVolume: b.bidVolume,
      delta,
      cvd,
    });
  }
  return { candles, orderFlow };
}


/** Live-Trade fürs Tape (vom Backend: /api/rithmic/ticks). */
export interface TapeTick {
  seq: number;
  time: string;
  price: number;
  size: number;
  aggressor: "Buy" | "Sell" | "Unknown";
  bid: number;
  ask: number;
}

/** Hängt neue Ticks an (ohne Duplikate per Seq), neueste zuerst, höchstens `max` Einträge. */
export function appendTape(prev: TapeTick[], incoming: TapeTick[], max: number): TapeTick[] {
  const lastSeq = prev.length > 0 ? prev[0].seq : 0;
  const fresh = incoming.filter((t) => t.seq > lastSeq).sort((a, b) => b.seq - a.seq);
  return [...fresh, ...prev].slice(0, max);
}
