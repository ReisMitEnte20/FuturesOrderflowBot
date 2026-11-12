import { Link, useLocation } from "react-router";
import { cn } from "@/lib/utils";
import { useTradingStore } from "@/stores/tradingStore";
import { Activity, ChartCandlestick, ChartColumn, FlaskConical, Settings, Workflow, Zap, type LucideIcon } from "lucide-react";

const NAV: { to: string; icon: LucideIcon; label: string }[] = [
  { to: "/", icon: Activity, label: "Live" },
  { to: "/signals", icon: Zap, label: "Signals" },
  { to: "/backtest", icon: ChartCandlestick, label: "Backtest" },
  { to: "/research", icon: FlaskConical, label: "Research" },
  { to: "/market", icon: ChartColumn, label: "Market" },
  { to: "/pipeline", icon: Workflow, label: "Pipeline" },
];

function NavItem({ to, icon: Icon, label, active }: { to: string; icon: LucideIcon; label: string; active: boolean }) {
  return (
    <Link
      to={to}
      aria-current={active ? "page" : undefined}
      className={cn(
        "group relative flex items-center gap-3 pl-4 pr-3 py-2 rounded-lg text-[13px] transition-colors",
        active
          ? "text-[var(--fg)] font-medium bg-[linear-gradient(90deg,rgba(60,240,160,0.16),rgba(34,184,240,0.05)_70%,transparent)]"
          : "text-[var(--fg-dim)] hover:text-[var(--fg)] hover:bg-[rgba(14,33,57,0.7)]"
      )}
    >
      {/* Aktiv-Markierung: leuchtender Balken im Marken-Verlauf. */}
      <span
        className={cn(
          "absolute left-0 top-1.5 bottom-1.5 w-[3px] rounded-full bg-grad-brand-v transition-opacity",
          active ? "opacity-100 shadow-[0_0_10px_rgba(60,240,160,0.8)]" : "opacity-0"
        )}
      />
      <Icon
        className={cn(
          "h-4 w-4 flex-shrink-0 transition-colors",
          active ? "text-[var(--key)]" : "text-[var(--fg-faint)] group-hover:text-[var(--cyan)]"
        )}
        strokeWidth={1.75}
      />
      {label}
    </Link>
  );
}

export function Sidebar() {
  const location = useLocation();
  const connectionStatus = useTradingStore((s) => s.connectionStatus);
  const dot =
    connectionStatus === "connected" ? "dot-on" : connectionStatus === "connecting" ? "dot-warn animate-pulse" : "dot-off";

  return (
    <aside className="w-56 flex-shrink-0 border-r border-[var(--line)] bg-[rgba(4,16,34,0.82)] backdrop-blur flex flex-col">
      <Link to="/" className="flex items-center gap-2.5 px-3 h-14 border-b border-[var(--line)]">
        <img
          src="/logo-icon.png"
          alt=""
          className="h-9 w-9 rounded-lg flex-shrink-0 ring-1 ring-[rgba(60,240,160,0.35)] shadow-[0_0_16px_-4px_rgba(60,240,160,0.7)]"
        />
        <span className="brand-wordmark text-[13.5px] leading-none whitespace-nowrap">FutureOrderFlowBot</span>
      </Link>

      <nav className="flex-1 px-2 py-3 overflow-y-auto">
        <p className="eyebrow px-4 pb-2">Workspace</p>
        <div className="space-y-0.5">
          {NAV.map((item) => (
            <NavItem key={item.to} {...item} active={location.pathname === item.to} />
          ))}
        </div>
      </nav>

      <nav className="px-2 pb-3">
        <NavItem to="/settings" icon={Settings} label="Settings" active={location.pathname === "/settings"} />
      </nav>

      <div className="px-4 py-2.5 border-t border-[var(--line)] mt-auto">
        <div className="flex items-center gap-2">
          <span className={cn("dot", dot)} />
          <span className="eyebrow">{connectionStatus}</span>
        </div>
      </div>
    </aside>
  );
}
