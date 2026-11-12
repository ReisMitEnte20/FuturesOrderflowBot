import { useEffect, useState } from "react";
import { ArrowUp, PanelLeftClose, PanelLeftOpen } from "lucide-react";
import { useTradingStore } from "@/stores/tradingStore";
import type { SessionMessage } from "@/types";

const RAIL_BG = "bg-[rgba(6,20,40,0.78)] backdrop-blur";
const TOGGLE =
  "w-7 h-7 grid place-items-center rounded-md text-[var(--fg-faint)] hover:text-[var(--key)] hover:bg-[rgba(14,33,57,0.9)] transition-colors";

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
      <aside className={`w-9 flex-shrink-0 border-r border-[var(--line)] ${RAIL_BG} flex flex-col items-center`}>
        <button
          onClick={onToggle}
          title="Session-Spalte einblenden"
          aria-label="Session-Spalte einblenden"
          className={`mt-3.5 ${TOGGLE}`}
        >
          <PanelLeftOpen className="h-3.5 w-3.5" strokeWidth={1.75} />
        </button>
        <span className="mt-3 eyebrow [writing-mode:vertical-rl] rotate-180 select-none">Session</span>
      </aside>
    );
  }

  return (
    <aside className={`w-80 flex-shrink-0 border-r border-[var(--line)] ${RAIL_BG} flex flex-col min-h-0`}>
      <div className="flex items-center justify-between pl-4 pr-3 h-14 border-b border-[var(--line)]">
        <div className="flex items-center gap-2">
          <span className="dot dot-on" />
          <span className="eyebrow">Session</span>
        </div>
        {onToggle && (
          <button
            onClick={onToggle}
            title="Session-Spalte einklappen"
            aria-label="Session-Spalte einklappen"
            className={TOGGLE}
          >
            <PanelLeftClose className="h-3.5 w-3.5" strokeWidth={1.75} />
          </button>
        )}
      </div>

      <div className="flex-1 overflow-y-auto px-3 py-3 space-y-2.5">
        {sessionMessages.map((msg: SessionMessage) => {
          const isUser = msg.role === "user";
          return (
            <div
              key={msg.id}
              className={
                isUser
                  ? "ml-6 rounded-xl rounded-br-sm border border-[rgba(60,240,160,0.3)] bg-[linear-gradient(180deg,rgba(60,240,160,0.12),rgba(34,184,240,0.06))] px-3 py-2 text-[var(--fg)]"
                  : "mr-6 rounded-xl rounded-bl-sm border border-[var(--line)] bg-[rgba(14,33,57,0.65)] px-3 py-2 text-[var(--fg-dim)]"
              }
            >
              {isUser && <div className="eyebrow mb-1 text-[var(--key)]">You</div>}
              <p className="text-[13px] leading-relaxed">{msg.content}</p>
              {msg.toolLines && msg.toolLines.length > 0 && (
                <div className="mt-2 space-y-1">
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
              <div className="mt-1 text-[9px] font-mono text-[var(--fg-faint)]">
                {new Date(msg.timestamp).toLocaleTimeString()}
              </div>
            </div>
          );
        })}
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
      <form
        onSubmit={handleSubmit}
        className="flex items-center gap-2 rounded-xl border border-[var(--line-2)] bg-[rgba(2,13,30,0.8)] pl-3 pr-1.5 py-1.5 transition-colors focus-within:border-[rgba(60,240,160,0.6)] focus-within:shadow-[0_0_18px_-8px_rgba(60,240,160,0.8)]"
      >
        <input
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          placeholder="Ask about positions, signals..."
          aria-label="Frage an die Session"
          className="flex-1 min-w-0 bg-transparent text-sm text-[var(--fg)] placeholder:text-[var(--fg-faint)] focus:outline-none"
        />
        <button
          type="submit"
          title="Senden"
          aria-label="Senden"
          className="h-8 w-8 grid place-items-center rounded-lg bg-grad-brand text-[var(--bg)] shadow-brand hover:brightness-110 transition"
        >
          <ArrowUp className="h-4 w-4" strokeWidth={2.5} />
        </button>
      </form>
    </div>
  );
}
