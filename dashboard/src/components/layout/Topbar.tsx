import { ConnectionStatus } from "@/types";

interface TopbarProps {
  activeView: string;
  connectionStatus: ConnectionStatus;
}

export function Topbar({ connectionStatus }: TopbarProps) {
  return (
    <header className="h-12 flex items-center justify-between px-4 border-b border-[var(--line)] bg-[var(--bg-2)]">
      <div className="flex items-center gap-4">
        <span className="font-mono text-[var(--key)] font-bold text-sm tracking-wider">QANAT</span>
        <span className="text-[var(--fg-faint)] text-xs mono uppercase tracking-widest">
          Trading Dashboard
        </span>
      </div>

      <div className="flex items-center gap-2">
        <StatusIndicator status={connectionStatus} />
      </div>

      <div className="w-20" />
    </header>
  );
}

function StatusIndicator({ status }: { status: ConnectionStatus }) {
  return (
    <div className="flex items-center gap-1.5 px-2 py-1 rounded bg-[var(--panel)] border border-[var(--line)]">
      <span
        className={
          status === "connected"
            ? "w-1.5 h-1.5 rounded-full bg-[var(--key)] shadow-[0_0_6px_#a2e65d88]"
            : "w-1.5 h-1.5 rounded-full bg-[var(--red)]"
        }
      />
      <span className="text-[10px] font-mono text-[var(--fg-faint)] uppercase">{status}</span>
    </div>
  );
}