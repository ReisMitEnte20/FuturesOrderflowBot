import { useEffect, useRef, useState } from "react";
import { Card } from "@/components/common/Card";
import { appendTape, type TapeTick } from "@/lib/rithmicBars";

const API_BASE = "/api/rithmic";
const POLL_MS = 500;
const MAX_ROWS = 300;
const BIG_PRINT = 10;

interface TradeTapeProps {
  symbol: string | null;
  exchange: string;
  enabled: boolean;
}

function formatTime(iso: string) {
  const d = new Date(iso);
  return `${d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" })}.${String(d.getMilliseconds()).padStart(3, "0")}`;
}

/**
 * Time & Sales aus echten Rithmic-Trades. Seite (Buy/Sell) = Aggressor laut Rithmic, nicht geschätzt.
 * Holt inkrementell über ?since=<lastSeq>, damit kein Trade doppelt oder verloren ist.
 */
export function TradeTape({ symbol, exchange, enabled }: TradeTapeProps) {
  const [tape, setTape] = useState<TapeTick[]>([]);
  const [error, setError] = useState<string | null>(null);
  const sinceRef = useRef(0);

  useEffect(() => {
    setTape([]);
    setError(null);
    sinceRef.current = 0;
    if (!enabled || !symbol) return;

    let cancelled = false;
    const poll = async () => {
      try {
        const r = await fetch(`${API_BASE}/ticks?symbol=${encodeURIComponent(symbol)}&exchange=${exchange}&since=${sinceRef.current}&limit=${MAX_ROWS}`);
        const body = await r.json();
        if (cancelled) return;
        if (!r.ok) {
          setError(body.message || `HTTP ${r.status}`);
          return;
        }
        setError(null);
        sinceRef.current = body.lastSeq;
        if (body.ticks.length > 0) setTape((prev) => appendTape(prev, body.ticks, MAX_ROWS));
      } catch (e) {
        if (!cancelled) setError(e instanceof Error ? e.message : String(e));
      }
    };
    poll();
    const timer = setInterval(poll, POLL_MS);
    return () => {
      cancelled = true;
      clearInterval(timer);
    };
  }, [symbol, exchange, enabled]);

  return (
    <Card>
      <div className="mb-2 flex items-center justify-between">
        <h2 className="text-sm font-mono font-medium">Tape · {symbol ?? "–"}</h2>
        <span className="text-[10px] font-mono text-[var(--fg-faint)]">{tape.length} Trades</span>
      </div>
      {error && <p className="text-xs font-mono text-[var(--red)] mb-2">{error}</p>}
      <div className="grid grid-cols-[1fr_auto_auto_auto] gap-x-3 text-[10px] font-mono text-[var(--fg-faint)] uppercase tracking-wider pb-1 border-b border-[var(--line)]">
        <span>Zeit</span>
        <span className="text-right">Preis</span>
        <span className="text-right">Größe</span>
        <span className="text-right">Seite</span>
      </div>
      <div className="h-[420px] overflow-y-auto">
        {tape.length === 0 ? (
          <p className="text-xs font-mono text-[var(--fg-faint)] py-4 text-center">
            {enabled ? "Warte auf Trades…" : "Rithmic nicht verbunden"}
          </p>
        ) : (
          tape.map((t) => {
            const color = t.aggressor === "Buy" ? "text-[var(--key)]" : t.aggressor === "Sell" ? "text-[var(--red)]" : "text-[var(--fg-dim)]";
            return (
              <div
                key={t.seq}
                className={`grid grid-cols-[1fr_auto_auto_auto] gap-x-3 text-xs font-mono py-0.5 ${color} ${t.size >= BIG_PRINT ? "font-bold" : ""}`}
              >
                <span className="text-[var(--fg-dim)]">{formatTime(t.time)}</span>
                <span className="text-right">{t.price.toFixed(2)}</span>
                <span className="text-right">{t.size}</span>
                <span className="text-right">{t.aggressor === "Unknown" ? "?" : t.aggressor}</span>
              </div>
            );
          })
        )}
      </div>
    </Card>
  );
}
