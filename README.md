<p align="center">
  <img src="docs/assets/logo-banner.jpg" alt="FutureOrderFlowBot" width="100%" />
</p>

# FuturesOrderflowBot

Modularer **Futures-Orderflow-Research-Bot** in C# / .NET 8 mit React-Dashboard. Ziel ist eine
Orderflow-/Quant-Edge, die nach Fees, Slippage, Drawdown und Out-of-Sample-Prüfung robust ist.

> **Research / Simulation only.** Keine Live-Execution, keine Order-Übermittlung, keine Broker-Execution-API.
> Rithmic wird ausschließlich als **Marktdatenquelle** genutzt (standardmäßig deaktiviert).
> Fees, TickValues und Risk-Limits kommen aus `config/`, nichts davon ist hardcoded.

| Stand 2026-10-07 (`main` @ `2a06a0c7`) | |
|---|---|
| `dotnet build` | 0 Fehler, 3 Warnungen (CS8604, CS1998, CS0219) |
| `dotnet test` | **652/652 bestanden** (`PaperDemoServiceTests.Double_start_returns_same_running_session` ist bekannt sporadisch rot; im ersten Lauf 651/652) |
| `cd dashboard && npm test` | **14/14 bestanden** (`node --test`) |

## Schnellstart

Voraussetzungen: .NET 8 SDK, Node.js mit npm (für `dashboard/`).

```bash
dotnet build
dotnet test

cd dashboard
npm install
npm test
```

### Dashboards starten

**Backend + Blazor-DevDashboard** (`http://localhost:5034`, Profil `http`):

```bash
dotnet run --project src/TradingBot.DevDashboard/TradingBot.DevDashboard.csproj --launch-profile http
# mit Rithmic-Marktdaten (sonst liefert /api/rithmic nur "deaktiviert"):
Rithmic__Enabled=true dotnet run --project src/TradingBot.DevDashboard/TradingBot.DevDashboard.csproj --launch-profile http
```

**React-Dashboard** (Vite, `http://127.0.0.1:5899`):

```bash
cd dashboard
npm run dev      # Dev-Server
npm run build    # tsc -b + vite build → dashboard/dist/
```

Das React-Dashboard ruft die Backtest- und Quant-API direkt unter `http://localhost:5034/api/backtest`
bzw. `/api/quant` auf (überschreibbar per `VITE_BACKTEST_API` / `VITE_QUANT_API`; CORS erlaubt
`127.0.0.1:5899`/`localhost:5899` und `:4173`).

> **Bekannte Einschränkung Rithmic im Dev-Modus:** Die Rithmic-Komponenten rufen `/api/rithmic/*`
> **relativ** auf. `vite.config.ts` enthält **keinen** Proxy, d. h. vom Vite-Dev-Server (5899) aus
> erreichen diese Aufrufe das Backend nicht ohne zusätzliche Weiterleitung. `dashboard/rithmic-proxy.cjs`
> (`node dashboard/rithmic-proxy.cjs`) ist ein lokaler Node-Proxy auf Port 3001, der `/api/rithmic/*` an
> das Backend (Port 5034, `BACKEND_PORT`) weiterreicht; er ist aber nicht automatisch in den Vite-Server
> eingebunden.

## Architektur

```
[MarketData] → [Strategy] → [Risk] → [Order] → [Position/PnL]
 CSV/Replay/    TradeSignal   Gate     (nur       Netting/Fees
 Rithmic                              Simulation)
```

Grundregeln (Details in [CLAUDE.md](CLAUDE.md)):

- **Strategy erzeugt nur `TradeSignal`** — niemals Orders.
- **`RiskManager`** ist fail-closed Gatekeeper; **`OrderManager`** baut Orders nur nach `RiskDecision.Approved`.
- **`PositionManager`** ist Single Source of Truth für Position/PnL.
- **Kein Fake-Orderflow:** fehlen echte Bid/Ask-/Aggressor-Daten → `InsufficientData`.
- **Dashboard/Research referenzieren nicht `TradingBot.Execution`** (per Test abgesichert).
- Maßgeblich ist **NetPnL nach Fees/Slippage**.

### Solution (`TradingBot.sln`)

| Projekt | Zweck | Referenziert |
|---|---|---|
| `TradingBot.Domain` | Modelle + Enums | – |
| `TradingBot.Core` | Interfaces / Abstraktionen (u. a. `IBrokerExecutionAdapter`) | Domain |
| `TradingBot.Application` | Risk, Orders, Positions, Fees, PnL, MarketData-Aggregation, Strategies (Registry/Engine, Orderflow-Template) | Core, Domain |
| `TradingBot.Infrastructure` | Config (JSON), CSV/Replay, Sierra-/ATAS-Importer, Rithmic-R\|Protocol-Marktdatenclient | Core, Domain |
| `TradingBot.Execution` | Platzhalter für spätere Broker-Adapter — **derzeit leer** (nur `.gitkeep`) | Core, Domain |
| `TradingBot.Backtesting` | Backtest-Engine inkl. OHLC-Engine (`Ohlc/`: SL/TP je Trade, Gap-Stop-Slippage, Mark-to-Market) | Application, Core, Domain |
| `TradingBot.PaperTrading` | Paper-Trading-Engine (Simulation) | Application, Core, Domain |
| `TradingBot.Research` | Monte Carlo, Walk Forward, Parameter Sweep, Sensitivity, Robustness, Ranking | Application, Backtesting, Infrastructure, Core, Domain |
| `TradingBot.Quant` | Quant-Research: Walk-forward, Monte Carlo, Overfitting (CSCV/PBO, PSR/DSR), Robustness, Versuchsregister, Holdout | Backtesting, Core, Domain |
| `TradingBot.DevDashboard` | Blazor-DevDashboard + lokale HTTP-API für das React-Dashboard | Domain, Core, Infrastructure, Backtesting, PaperTrading, Research, Quant |
| `TradingBot.Console` | Konsolen-Einstieg (Research-Demo mit Sierra-Orderflow-Ticks) | alle außer DevDashboard/Quant |
| `TradingBot.Tests` | xUnit-Tests | alle |

Abhängigkeiten zeigen nach innen Richtung `Domain`; `Application` kennt nur Interfaces aus `Core`.

## Dashboards

**React-Dashboard** (`dashboard/`, Vite + React + TypeScript + Tailwind + ECharts) — Seiten:
`/` Trading Desk · `/signals` · `/backtest` · `/research` · `/market` · `/pipeline` · `/settings`.
Screenshots: [picture/README.md](picture/README.md). Keine Order-/Buy-/Sell-Buttons.

**Blazor-DevDashboard** (`src/TradingBot.DevDashboard`, Port 5034) — Seiten:
`/` Status · `/paper` Paper Monitor · `/research` Research Dashboard · `/replay` Backtest Replay.

**Backend-API** (im DevDashboard):

| Gruppe | Inhalt |
|---|---|
| `/api/backtest/*` | `strategies`, `instruments`, `data-sources`, `candles`, `run` (OHLC-Backtest, Simulation) |
| `/api/quant/*` | `status`, `benchmarks`, `analyze`, Jobs (`walkforward`, `montecarlo`, `robustness`, `overfitting`, Abbruch), Kampagnen/Versuche, Holdout, Paper-Einträge |
| `/api/rithmic/*` | `options`, `connect`, `disconnect`, `status`, `subscribe`, `ticks`, `quotes`, `frontmonth`, `bars`, `conformance/*` — nur mit `Rithmic:Enabled=true`, sonst Stub „deaktiviert" |

## Marktdaten

**Lokale Dateien** (streamend gelesen, nie committen):

- Sierra Chart Intraday-CSV (`SierraIntradayCsvImporter`, `SierraOrderFlowBarBuilder`)
- ATAS-CSV: Tick, Orderflow-Bar, Footprint, Volume Profile (+ Data-Quality-Checks)
- Import-Profile: `config/import-profiles/*.example.json`; CSV-Formate: [samples/marketdata/README.md](samples/marketdata/README.md)

**Rithmic (R|Protocol, nur Marktdaten)** — `src/TradingBot.Infrastructure/MarketData/Rithmic/Protocol`:

- Nur **Ticker Plant** und **History Plant**, kein Order-/PnL-Plant; es werden keine Order-Protos kompiliert.
- Standardmäßig **deaktiviert** (`Rithmic:Enabled=false`); Netzwerkzugriff erst nach explizitem `POST /connect`
  (Login per „Connect" im React-Dashboard, z. B. mit einem Prop-Firm-Rithmic-Konto).
- Gateways in `src/TradingBot.DevDashboard/appsettings.json` (Chicago, Frankfurt, Rithmic Test);
  lokale Ergänzungen in `config/marketdata/rithmic.local.json` (gitignored, **keine Passwörter**).
- **Liefert:** Live-Ticks mit Aggressor, Top-of-Book-Quotes, Front-Month-Ermittlung, Minuten-Bars mit Bid-/Ask-Volumen.
- **Liefert nicht:** historische Ticks, Footprint, Markttiefe (DOM).
- Conformance-Modus (`RithmicConformanceSession`): nur Login + Heartbeat am Order Plant von „Rithmic Test",
  keine Order-Nachrichten.

Die übrigen Dateien unter `MarketData/Rithmic/*.cs` (außerhalb von `Protocol/`) sind ein älterer,
ungenutzter Stand und nicht Teil der aktiven Integration.

## Konfiguration

JSON-Profile unter `config/` — eingecheckt sind nur `*.example.json`:

| Ordner | Inhalt |
|---|---|
| `config/brokers/` | Broker-Profil (Beispiel) |
| `config/instruments/` | MES, MNQ, NQ |
| `config/fees/` | Fee-Profile je Instrument |
| `config/risk/` | Risk-Profil (Max Daily Loss, Max Contracts, …) |
| `config/dashboard/` | Dashboard-Defaults |
| `config/import-profiles/` | Sierra-/ATAS-Importprofile |
| `config/marketdata/` | Rithmic-Beispielkonfiguration |

Beispielwerte sind Platzhalter. Lokale Overrides als `*.local.json` (per `.gitignore` ausgeschlossen).

## Mitwirken

- **Menschen:** [docs/COLLABORATOR_ONBOARDING.md](docs/COLLABORATOR_ONBOARDING.md)
- **KI-Agenten:** [CLAUDE.md](CLAUDE.md) und [AGENTS.md](AGENTS.md); Code-Exploration per jCodeMunch: [docs/JCODEMUNCH_SETUP.md](docs/JCODEMUNCH_SETUP.md)
- **Nie committen:** Marktdaten (`.txt`, `.scid`, `.tct`, große `.csv`, `samples/*/raw/`), Secrets/Zugangsdaten,
  `config/**/*.local.json`, `artifacts/`, `dashboard/dist/`, `node_modules/`.
- Keine Live-Execution, keine Broker-Order-APIs, keine Order-Buttons im Dashboard ohne ausdrückliche Freigabe.
- Build + Tests grün halten; **Commit/Push/Merge nur mit Freigabe des Repo-Owners**.

## Dokumentation

| Datei | Thema |
|---|---|
| [docs/HANDOFF_CURRENT_STATE.md](docs/HANDOFF_CURRENT_STATE.md) | Übergabe / letzter dokumentierter Stand |
| [docs/PROJECT_STATUS.md](docs/PROJECT_STATUS.md) | Phasenübersicht (teilweise veraltet) |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Architektur |
| [docs/QUANT_RESEARCH.md](docs/QUANT_RESEARCH.md) | Quant-Research: Methoden, Konventionen, Grenzen |
| [docs/RESEARCH_ANALYTICS.md](docs/RESEARCH_ANALYTICS.md) | Research Analytics Layer |
| [docs/RESEARCH_DASHBOARD.md](docs/RESEARCH_DASHBOARD.md) | Blazor Research Dashboard |
| [docs/BACKTEST_REPLAY_VISUALIZER.md](docs/BACKTEST_REPLAY_VISUALIZER.md) | Backtest Replay |
| [docs/PAPER_TRADING.md](docs/PAPER_TRADING.md) | Paper Trading |
| [docs/STRATEGY_FRAMEWORK.md](docs/STRATEGY_FRAMEWORK.md) | Strategy Registry/Engine |
| [docs/ORDERFLOW_STRATEGY_TEMPLATE.md](docs/ORDERFLOW_STRATEGY_TEMPLATE.md) | Orderflow-Strategie-Template |
| [docs/DATA_IMPORT_AND_QUALITY.md](docs/DATA_IMPORT_AND_QUALITY.md) | Datenimport + Qualitätsprüfung |
| [docs/MARKET_DATA_SOURCE_GUIDE.md](docs/MARKET_DATA_SOURCE_GUIDE.md) | Marktdatenquellen |
| [docs/ATAS_EXPORT_GUIDE.md](docs/ATAS_EXPORT_GUIDE.md) | ATAS-Export |
| [docs/COLLABORATOR_ONBOARDING.md](docs/COLLABORATOR_ONBOARDING.md) | Onboarding |
| [docs/JCODEMUNCH_SETUP.md](docs/JCODEMUNCH_SETUP.md) | jCodeMunch-Setup |
