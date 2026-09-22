import { Link, useLocation } from "react-router";
import { cn } from "@/lib/utils";
import { useTradingStore } from "@/stores/tradingStore";
import { Settings } from "lucide-react";

const NAV = [
  { to: "/", icon: "◉", label: "Live" },
  { to: "/signals", icon: "▲", label: "Signals" },
  { to: "/backtest", icon: "◈", label: "Backtest" },
  { to: "/market", icon: "◆", label: "Market" },
  { to: "/pipeline", icon: "⬡", label: "Pipeline" },
] as const;

export function Sidebar() {
  const location = useLocation();
  const connectionStatus = useTradingStore((s) => s.connectionStatus);

  return (
    <aside className="w-52 flex-shrink-0 border-r border-[var(--line)] bg-[var(--bg-2)] flex flex-col">
      <div className="flex items-center gap-2 px-4 h-12 border-b border-[var(--line)]">
        <span className="font-mono text-[var(--key)] font-bold text-sm tracking-wider">QANAT</span>
        <span className="text-[var(--fg-faint)] text-xs">Trading</span>
      </div>

      <nav className="flex-1 px-2 py-3 space-y-0.5 overflow-y-auto">
        {NAV.map(({ to, icon, label }) => {
          const isActive = location.pathname === to;
          return (
            <Link
              key={to}
              to={to}
              className={cn(
                "flex items-center gap-3 px-3 py-2 rounded-md text-sm transition-colors",
                isActive
                  ? "bg-[var(--panel)] text-[var(--key)] font-medium"
                  : "text-[var(--fg-dim)] hover:bg-[var(--panel)] hover:text-[var(--fg)]"
              )}
            >
              <span className="text-xs w-4 text-center">{icon}</span>
              {label}
            </Link>
          );
        })}
      </nav>

      <nav className="px-2 pb-3 space-y-0.5">
        <Link
          to="/settings"
          className={cn(
            "flex items-center gap-3 px-3 py-2 rounded-md text-sm transition-colors",
            location.pathname === "/settings"
              ? "bg-[var(--panel)] text-[var(--key)] font-medium"
              : "text-[var(--fg-dim)] hover:bg-[var(--panel)] hover:text-[var(--fg)]"
          )}
        >
          <Settings className="h-3.5 w-3.5" />
          Settings
        </Link>
      </nav>

      <div className="px-3 py-2 border-t border-[var(--line)] mt-auto">
        <div className="flex items-center gap-2">
          <span
            className={cn(
              "w-1.5 h-1.5 rounded-full",
              connectionStatus === "connected" ? "bg-[var(--key)]" : "bg-[var(--red)]"
            )}
          />
          <span className="text-[10px] font-mono text-[var(--fg-faint)] uppercase tracking-wider">
            {connectionStatus}
          </span>
        </div>
      </div>
    </aside>
  );
}