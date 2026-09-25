import { useState } from "react";
import { Outlet, useLocation } from "react-router";
import { Topbar } from "./Topbar";
import { Sidebar } from "./Sidebar";
import { ChatRail } from "./ChatRail";

export function Layout() {
  const { pathname } = useLocation();
  // Auf der Backtest-Seite braucht der Chart die Breite: Session-Spalte dort standardmäßig eingeklappt.
  // Andere Seiten bleiben wie bisher (ausgeklappt). Manuelles Umschalten gilt je Bereich für die Sitzung.
  const area = pathname.startsWith("/backtest") ? "backtest" : "default";
  const [chatPref, setChatPref] = useState<Record<string, boolean>>({});
  const chatOpen = chatPref[area] ?? area !== "backtest";

  return (
    <div className="flex h-screen bg-[var(--bg)]">
      <Sidebar />
      <ChatRail collapsed={!chatOpen} onToggle={() => setChatPref((p) => ({ ...p, [area]: !chatOpen }))} />

      <div className="flex-1 flex flex-col min-w-0">
        <Topbar />

        <main className="flex-1 min-h-0 overflow-auto">
          <Outlet />
        </main>
      </div>
    </div>
  );
}
