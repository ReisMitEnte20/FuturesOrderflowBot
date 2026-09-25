import { Outlet } from "react-router";
import { Topbar } from "./Topbar";
import { Sidebar } from "./Sidebar";
import { ChatRail } from "./ChatRail";

export function Layout() {
  return (
    <div className="flex h-screen bg-[var(--bg)]">
      <Sidebar />
      <ChatRail />

      <div className="flex-1 flex flex-col min-w-0">
        <Topbar />

        <main className="flex-1 min-h-0 overflow-auto">
          <Outlet />
        </main>
      </div>
    </div>
  );
}