import { useEffect, useState } from "react";
import { useTradingStore } from "@/stores/tradingStore";
import type { SessionMessage } from "@/types";

export function ChatRail({ collapsed = false, onToggle }: { collapsed?: boolean; onToggle?: () => void }) {
  const sessionMessages = useTradingStore((s) => s.sessionMessages);
  const addSessionMessage = useTradingStore((s) => s.addSessionMessage);

  useEffect(() => {
    addSessionMessage({
      role: "assistant",
      content: "Trading session ready. Ask me about positions, signals, or run a backtest.",
    });
  }, []);

  // Eingeklappt: schmale Leiste mit Button zum Einblenden (Komponente bleibt gemountet → kein Nachrichtenverlust).
  if (collapsed) {
    return (
      <aside className="w-8 flex-shrink-0 border-r border-[var(--line)] bg-[var(--panel)] flex flex-col items-center">
        <button
          onClick={onToggle}
          title="Session-Spalte einblenden"
          aria-label="Session-Spalte einblenden"
          className="mt-2 w-6 h-6 rounded text-[var(--fg-faint)] hover:text-[var(--key)] hover:bg-[var(--bg-2)] font-mono text-xs"
        >
          »
        </button>
        <span className="mt-3 font-mono text-[9px] text-[var(--fg-faint)] uppercase tracking-widest [writing-mode:vertical-rl] rotate-180 select-none">
          Session
        </span>
      </aside>
    );
  }

  return (
    <aside className="w-80 flex-shrink-0 border-r border-[var(--line)] bg-[var(--panel)] flex flex-col min-h-0">
      <div className="flex items-center justify-between px-4 h-10 border-b border-[var(--line)]">
        <span className="font-mono text-[10px] text-[var(--fg-faint)] uppercase tracking-widest">
          Session
        </span>
        {onToggle && (
          <button
            onClick={onToggle}
            title="Session-Spalte einklappen"
            aria-label="Session-Spalte einklappen"
            className="w-6 h-6 rounded text-[var(--fg-faint)] hover:text-[var(--key)] hover:bg-[var(--bg-2)] font-mono text-xs"
          >
            «
          </button>
        )}
      </div>

      <div className="flex-1 overflow-y-auto px-3 py-3 space-y-3">
        {sessionMessages.map((msg: SessionMessage) => (
          <div
            key={msg.id}
            className={`text-sm ${msg.role === "user" ? "text-[var(--fg)]" : "text-[var(--fg-dim)]"}`}
          >
            {msg.role === "user" && (
              <div className="mb-1 flex items-center gap-2">
                <span className="w-1.5 h-1.5 rounded-full bg-[var(--key)]" />
                <span className="font-mono text-[9px] uppercase tracking-wider text-[var(--fg-faint)]">
                  You
                </span>
              </div>
            )}
            <p className="leading-relaxed pl-3.5">{msg.content}</p>
            {msg.toolLines && msg.toolLines.length > 0 && (
              <div className="ml-3.5 mt-2 space-y-1">
                {msg.toolLines.map((t: { name: string; status: string; detail?: string }, i: number) => (
                  <div key={i} className="flex items-center gap-2 text-[10px] font-mono">
                    <span
                      className={
                        t.status === "ok" ? "text-[var(--key)]" : t.status === "failed" ? "text-[var(--red)]" : "text-[var(--gold)] animate-pulse"
                      }
                    >
                      {t.status === "ok" ? "[OK]" : t.status === "failed" ? "[FAIL]" : "[RUN]"}
                    </span>
                    <span className="text-[var(--fg-faint)]">{t.name}</span>
                    {t.detail && <span className="text-[var(--fg-dim)]">{t.detail}</span>}
                  </div>
                ))}
              </div>
            )}
            <div className="ml-3.5 mt-1 text-[9px] font-mono text-[var(--fg-faint)]">
              {new Date(msg.timestamp).toLocaleTimeString()}
            </div>
          </div>
        ))}
      </div>

      <AskBar />
    </aside>
  );
}

function AskBar() {
  const [query, setQuery] = useState("");
  const addSessionMessage = useTradingStore((s) => s.addSessionMessage);

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    if (!query.trim()) return;
    addSessionMessage({ role: "user", content: query });
    addSessionMessage({
      role: "assistant",
      content: `Processing: "${query}" — this would trigger agent analysis.`,
    });
    setQuery("");
  };

  return (
    <div className="border-t border-[var(--line)] p-3">
      <form onSubmit={handleSubmit} className="flex gap-2">
        <input
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          placeholder="Ask about positions, signals..."
          className="flex-1 bg-[var(--bg)] border border-[var(--line)] rounded-md px-3 py-2 text-sm text-[var(--fg)] placeholder:text-[var(--fg-faint)] focus:border-[var(--key)] focus:outline-none"
        />
        <button
          type="submit"
          className="px-3 py-2 bg-[var(--key)] text-[var(--bg)] rounded-md text-xs font-mono font-medium hover:opacity-90"
        >
          ask
        </button>
      </form>
    </div>
  );
}