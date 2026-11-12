import { useState } from "react";
import { Outlet, useLocation } from "react-router";
import { Topbar } from "./Topbar";
import { Sidebar } from "./Sidebar";
import { ChatRail } from "./ChatRail";
import { MarketBackdrop } from "@/components/brand/MarketBackdrop";

export function Layout() {
  const { pathname } = useLocation();
  // Auf den chartlastigen Seiten (Backtest, Research) braucht die Auswertung die Breite:
  // Session-Spalte dort standardmäßig eingeklappt. Andere Seiten bleiben wie bisher (ausgeklappt).
  // Manuelles Umschalten gilt je Bereich für die Sitzung.
  const area = pathname.startsWith("/backtest")
    ? "backtest"
    : pathname.startsWith("/research")
    ? "research"
    : "default";
  const [chatPref, setChatPref] = useState<Record<string, boolean>>({});
  const chatOpen = chatPref[area] ?? area === "default";

  return (
    // Kein eigener Hintergrund: Raster und Leuchten des <body> bleiben wie im Banner sichtbar.
    <div className="flex h-screen">
      <Sidebar />
      <ChatRail collapsed={!chatOpen} onToggle={() => setChatPref((p) => ({ ...p, [area]: !chatOpen }))} />

      <div className="relative flex-1 flex flex-col min-w-0">
        {/* Dekoratives Banner-Motiv hinter dem Inhalt (keine Daten). Nicht auf den dichten
            Analyse-Arbeitsflächen: dort würde es nur durch die Panel-Fugen blitzen. */}
        {area === "default" && (
          <MarketBackdrop className="absolute inset-x-0 bottom-0 h-[42%] w-full opacity-50" />
        )}

        <Topbar />

        <main className="relative z-10 flex-1 min-h-0 overflow-auto">
          <Outlet />
        </main>
      </div>
    </div>
  );
}
