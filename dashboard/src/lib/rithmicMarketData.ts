import { useEffect, useState } from "react";
import { useTradingStore } from "@/stores/tradingStore";
import { exchangeFor, mapBars, type RithmicQuote, type RithmicTimeBar } from "@/lib/rithmicBars";

export type { RithmicQuote };

const API_BASE = "/api/rithmic";
const BAR_POLL_MS = 15_000;
const QUOTE_POLL_MS = 1_000;
const LOOKBACK_HOURS = 8;

async function getJson<T>(url: string, init?: RequestInit): Promise<T> {
  const r = await fetch(url, init);
  const body = await r.json().catch(() => ({}));
  if (!r.ok) throw new Error(body.message || `HTTP ${r.status}`);
  return body as T;
}

/**
 * Lädt für ein Root-Symbol (z. B. "MES") über die bestehende Rithmic-Verbindung des Backends:
 * Front-Month → Abo (LastTrade/BBO) → Minuten-Bars (periodisch) + Live-Quote (sekündlich).
 * Schreibt Kerzen und Orderflow in den Store. Ohne Verbindung passiert nichts.
 */
export function useRithmicMarketData(root: string) {
  const connected = useTradingStore((s) => s.connectionStatus === "connected");
  const setCandles = useTradingStore((s) => s.setCandles);
  const setOrderFlow = useTradingStore((s) => s.setOrderFlow);
  const [contract, setContract] = useState<string | null>(null);
  const [quote, setQuote] = useState<RithmicQuote | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    setCandles([]);
    setOrderFlow([]);
    setContract(null);
    setQuote(null);
    setError(null);
    if (!connected) return;

    const exchange = exchangeFor(root);
    let cancelled = false;
    let barTimer: ReturnType<typeof setInterval> | undefined;
    let quoteTimer: ReturnType<typeof setInterval> | undefined;

    const loadBars = async (symbol: string) => {
      const bars = await getJson<RithmicTimeBar[]>(
        `${API_BASE}/bars?symbol=${encodeURIComponent(symbol)}&exchange=${exchange}&minutes=1&hours=${LOOKBACK_HOURS}`,
      );
      if (cancelled) return;
      const mapped = mapBars(bars);
      setCandles(mapped.candles);
      setOrderFlow(mapped.orderFlow);
    };

    const loadQuote = async (symbol: string) => {
      const quotes = await getJson<RithmicQuote[]>(`${API_BASE}/quotes`);
      if (!cancelled) setQuote(quotes.find((q) => q.symbol === symbol && q.exchange === exchange) ?? null);
    };

    (async () => {
      setLoading(true);
      try {
        const fm = await getJson<{ symbol: string | null }>(`${API_BASE}/frontmonth?root=${encodeURIComponent(root)}&exchange=${exchange}`);
        if (!fm.symbol) throw new Error(`Kein Front-Month für ${root} (${exchange}) gefunden.`);
        if (cancelled) return;
        setContract(fm.symbol);
        const symbol = fm.symbol;

        await getJson(`${API_BASE}/subscribe`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ symbol, exchange }),
        });
        await Promise.all([loadBars(symbol), loadQuote(symbol)]);
        if (cancelled) return;

        barTimer = setInterval(() => loadBars(symbol).catch((e) => setError(e.message)), BAR_POLL_MS);
        quoteTimer = setInterval(() => loadQuote(symbol).catch((e) => setError(e.message)), QUOTE_POLL_MS);
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : String(e));
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();

    return () => {
      cancelled = true;
      if (barTimer) clearInterval(barTimer);
      if (quoteTimer) clearInterval(quoteTimer);
    };
  }, [root, connected, setCandles, setOrderFlow]);

  return { connected, contract, quote, error, loading, exchange: exchangeFor(root) };
}
