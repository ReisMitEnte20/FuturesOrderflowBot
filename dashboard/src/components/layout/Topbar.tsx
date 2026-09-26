import { useEffect, useState } from "react";
import { useTradingStore } from "@/stores/tradingStore";
import { RithmicLoginModal } from "@/components/trading/RithmicLoginModal";
import { Button } from "@/components/common/Button";

const API_BASE = "/api/rithmic";

// Im lokalen Backtesting-Modus ist Rithmic serverseitig deaktiviert. Die UI löst standardmäßig KEINE
// automatischen Rithmic-Requests aus; Aktivierung nur explizit über VITE_RITHMIC_ENABLED=true.
const RITHMIC_ENABLED = (import.meta as any).env?.VITE_RITHMIC_ENABLED === "true";

export function Topbar() {
  const [loginOpen, setLoginOpen] = useState(false);
  const connectionStatus = useTradingStore((s) => s.connectionStatus);
  const userId = useTradingStore((s) => s.rithmicCredentials?.userId);
  const setConnectionStatus = useTradingStore((s) => s.setConnectionStatus);
  const setCredentials = useTradingStore((s) => s.setRithmicCredentials);

  useEffect(() => {
    if (RITHMIC_ENABLED) loadStatus();
  }, []);

  const loadStatus = async () => {
    try {
      const response = await fetch(`${API_BASE}/status`);
      if (response.ok) {
        const result = await response.json();
        if (result.isConnected) {
          setConnectionStatus("connected");
        } else if (result.lastError) {
          setConnectionStatus("error");
        }
      }
    } catch {
      // Ignore - backend might not be running
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
    <header className="h-12 flex items-center justify-between px-4 border-b border-[var(--line)] bg-[var(--bg-2)]">
      <div className="flex items-center gap-4">
        <span className="font-mono text-[var(--key)] font-bold text-sm tracking-wider">QANAT</span>
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
      ? "bg-[var(--key)] shadow-[0_0_6px_#a2e65d88]"
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