import { useEffect, useState } from "react";
import { useTradingStore } from "@/stores/tradingStore";
import { RithmicLoginModal } from "@/components/trading/RithmicLoginModal";
import { Button } from "@/components/common/Button";

const API_BASE = "/api/rithmic";

// /status ist ein rein LOKALER Snapshot des eigenen Backends (kein Rithmic-Netzwerkzugriff). Er wird beim Laden
// und periodisch abgefragt, damit die Anzeige den echten Backend-Zustand zeigt (z. B. nach Reload/ForcedLogout).
const STATUS_POLL_MS = 10_000;

export function Topbar() {
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
    <header className="h-12 flex items-center justify-between px-4 border-b border-[var(--line)] bg-[rgba(5,19,39,0.88)] backdrop-blur">
      <div className="flex items-center gap-4">
        <img src="/logo-wordmark.png" alt="FutureOrderFlowBot" className="h-9 w-auto rounded" />
        <span className="text-[var(--fg-faint)] text-xs mono uppercase tracking-widest">
          Trading Dashboard
        </span>
      </div>

      <div className="flex items-center gap-2">
        <StatusIndicator
          status={connectionStatus}
          userId={userId}
          onConnectClick={() => setLoginOpen(true)}
          onDisconnect={handleDisconnect}
        />
      </div>

      <div className="w-20" />

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
      ? "bg-[var(--key)] shadow-[0_0_6px_#3cf0a088]"
      : status === "connecting"
      ? "bg-[var(--gold)] animate-pulse"
      : status === "error"
      ? "bg-[var(--red)]"
      : "bg-[var(--fg-faint)]";

  return (
    <div className="flex items-center gap-2 px-2 py-1 rounded bg-[var(--panel)] border border-[var(--line)]">
      <span className={`w-1.5 h-1.5 rounded-full ${dotColor}`} />
      <span className="text-[10px] font-mono text-[var(--fg-faint)] uppercase">
        {status}
      </span>
      {userId && (
        <span className="text-[10px] font-mono text-[var(--fg-dim)]">{userId}</span>
      )}
      {status === "connected" ? (
        <Button variant="default" size="sm" onClick={onDisconnect} className="ml-1">
          Disconnect
        </Button>
      ) : (
        <Button variant="primary" size="sm" onClick={onConnectClick} className="ml-1">
          Connect
        </Button>
      )}
    </div>
  );
}