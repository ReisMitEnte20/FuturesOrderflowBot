import { useEffect, useState } from "react";
import { useLocation } from "react-router";
import { ShieldCheck } from "lucide-react";
import { useTradingStore } from "@/stores/tradingStore";
import { RithmicLoginModal } from "@/components/trading/RithmicLoginModal";
import { Button } from "@/components/common/Button";
import { RITHMIC_API } from "@/lib/apiBase";

const API_BASE = RITHMIC_API;

// /status ist ein rein LOKALER Snapshot des eigenen Backends (kein Rithmic-Netzwerkzugriff). Er wird beim Laden
// und periodisch abgefragt, damit die Anzeige den echten Backend-Zustand zeigt (z. B. nach Reload/ForcedLogout).
const STATUS_POLL_MS = 10_000;

// Titel/Untertitel je Bereich (rein beschreibend).
const PAGES: Record<string, { title: string; subtitle: string }> = {
  "/": { title: "Live Desk", subtitle: "Positionen · Signale · Orders" },
  "/signals": { title: "Signals", subtitle: "Signale der Strategien" },
  "/backtest": { title: "Backtest", subtitle: "OHLC-Simulation" },
  "/research": { title: "Research", subtitle: "Quant- und Orderflow-Auswertung" },
  "/market": { title: "Market Data", subtitle: "Rithmic · nur Marktdaten" },
  "/pipeline": { title: "Pipeline", subtitle: "Datenfluss der Strategien" },
  "/settings": { title: "Settings", subtitle: "Datenquelle und Verbindung" },
};

export function Topbar() {
  const { pathname } = useLocation();
  const page = PAGES[pathname] ?? { title: "FutureOrderFlowBot", subtitle: "Trading Dashboard" };
  const [loginOpen, setLoginOpen] = useState(false);
  const connectionStatus = useTradingStore((s) => s.connectionStatus);
  const userId = useTradingStore((s) => s.rithmicCredentials?.userId);
  const setConnectionStatus = useTradingStore((s) => s.setConnectionStatus);
  const setCredentials = useTradingStore((s) => s.setRithmicCredentials);

  useEffect(() => {
    loadStatus();
    const timer = setInterval(loadStatus, STATUS_POLL_MS);
    return () => clearInterval(timer);
  }, []);

  const loadStatus = async () => {
    try {
      const response = await fetch(`${API_BASE}/status`);
      if (!response.ok) return;
      const result = await response.json();
      const store = useTradingStore.getState();
      if (store.connectionStatus === "connecting") return; // laufenden Login nicht überschreiben
      if (result.isConnected) {
        setConnectionStatus("connected");
        if (result.username && store.rithmicCredentials?.userId !== result.username) {
          setCredentials({ userId: result.username, system: result.systemName ?? "", gateway: store.rithmicCredentials?.gateway ?? "" });
        }
      } else {
        setConnectionStatus(result.lastError ? "error" : "disconnected");
      }
    } catch {
      // Backend nicht erreichbar – Anzeige unverändert lassen.
    }
  };

  const handleDisconnect = async () => {
    try {
      await fetch(`${API_BASE}/disconnect`, { method: "POST" });
      setConnectionStatus("disconnected");
      setCredentials(null);
    } catch {
      // Ignore
    }
  };

  return (
    <header className="relative z-20 h-14 flex-shrink-0 flex items-center justify-between gap-4 px-5 bg-[rgba(4,16,34,0.78)] backdrop-blur">
      <div className="flex items-baseline gap-3 min-w-0">
        <h1 className="display text-[17px] leading-none text-[var(--fg)] whitespace-nowrap">{page.title}</h1>
        <span className="eyebrow truncate">{page.subtitle}</span>
      </div>

      <div className="flex items-center gap-3 flex-shrink-0">
        <span
          title="Research / Simulation only – keine Live-Execution, keine Order-Übermittlung"
          className="hidden md:inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full border border-[rgba(34,184,240,0.35)] bg-[rgba(34,184,240,0.08)] text-[var(--cyan)] font-mono text-[10px] uppercase tracking-[0.12em]"
        >
          <ShieldCheck className="h-3 w-3" strokeWidth={2} />
          Simulation only
        </span>
        <StatusIndicator
          status={connectionStatus}
          userId={userId}
          onConnectClick={() => setLoginOpen(true)}
          onDisconnect={handleDisconnect}
        />
      </div>

      {/* Leuchtende Verlaufslinie wie die Chart-Linien im Banner. */}
      <div className="hairline absolute inset-x-0 bottom-0" />

      <RithmicLoginModal
        open={loginOpen}
        onClose={() => setLoginOpen(false)}
      />
    </header>
  );
}

function StatusIndicator({
  status,
  userId,
  onConnectClick,
  onDisconnect,
}: {
  status: string;
  userId?: string;
  onConnectClick: () => void;
  onDisconnect: () => void;
}) {
  const dotColor =
    status === "connected"
      ? "dot-on"
      : status === "connecting"
      ? "dot-warn animate-pulse"
      : status === "error"
      ? "dot-off"
      : "";

  return (
    <div className="flex items-center gap-2 pl-3 pr-1 py-1 rounded-full bg-[rgba(14,33,57,0.8)] border border-[var(--line-2)]">
      <span className={`dot ${dotColor}`} />
      <span className="text-[10px] font-mono text-[var(--fg-dim)] uppercase tracking-[0.12em]">
        {status}
      </span>
      {userId && (
        <span className="text-[10px] font-mono text-[var(--fg-dim)]">{userId}</span>
      )}
      {status === "connected" ? (
        <Button variant="default" size="sm" onClick={onDisconnect} className="ml-1 rounded-full px-3">
          Disconnect
        </Button>
      ) : (
        <Button variant="primary" size="sm" onClick={onConnectClick} className="ml-1 rounded-full px-3">
          Connect
        </Button>
      )}
    </div>
  );
}