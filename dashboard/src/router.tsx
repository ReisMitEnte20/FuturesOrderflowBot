import { Suspense, lazy, type ComponentType } from "react";
import { createBrowserRouter } from "react-router";
import { Layout } from "@/components/layout/Layout";

const TradingDesk = lazy(() => import("@/pages/TradingDesk").then((m) => ({ default: m.TradingDesk })));
const Signals = lazy(() => import("@/pages/Signals").then((m) => ({ default: m.Signals })));
const Backtest = lazy(() => import("@/pages/Backtest").then((m) => ({ default: m.Backtest })));
const MarketData = lazy(() => import("@/pages/MarketData").then((m) => ({ default: m.MarketData })));
const Pipeline = lazy(() => import("@/pages/Pipeline").then((m) => ({ default: m.Pipeline })));
const Settings = lazy(() => import("@/pages/Settings").then((m) => ({ default: m.Settings })));

function PageLoader() {
  return (
    <div className="flex h-[60vh] items-center justify-center text-[var(--fg-faint)] mono text-sm">
      Loading…
    </div>
  );
}

function wrap(Component: ComponentType) {
  return (
    <Suspense fallback={<PageLoader />}>
      <Component />
    </Suspense>
  );
}

export const router = createBrowserRouter([
  {
    element: <Layout />,
    children: [
      { path: "/", element: wrap(TradingDesk) },
      { path: "/signals", element: wrap(Signals) },
      { path: "/backtest", element: wrap(Backtest) },
      { path: "/market", element: wrap(MarketData) },
      { path: "/pipeline", element: wrap(Pipeline) },
      { path: "/settings", element: wrap(Settings) },
    ],
  },
]);