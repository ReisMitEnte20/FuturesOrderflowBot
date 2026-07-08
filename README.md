# FuturesOrderflowBot

Modularer Futures-Orderflow-Trading-Bot (C# / .NET 8).
Unterstützt Backtest, Replay, Paper Trading und – später – Live Trading.
Brokerunabhängig über JSON-Profile (Broker / Instrument / Fee / Risk).

> **Teststand: 347/347 grün · Build: 0 Warnungen / 0 Fehler**
> ⚠️ **Keine Live-Execution** vorhanden · ⚠️ **Keine Broker-API** angebunden ·
> alle Broker-/Fee-/TickValue-Werte stammen aus Config (`config/`), nichts ist hardcoded.

## Überblick

```
[MarketData] → [Strategy] → [Risk] → [Order] → [Position/PnL]
   CSV/Replay    Signale     Gate     Submit     Netting/Fees
```

**Fertig (Phase 1–12C):**
- Architektur, Solution-Skeleton, Domain-Modelle + Interfaces
- Config-/Profil-System (Broker / Instrument / Fee / Risk) mit Validierung
- Fee- + PnL-Engine (Gross/Net getrennt, `decimal`-genau)
- RiskManager (fail-closed Gatekeeper, exit-aware: Close/Reduce nie durch Entry-Limits blockiert)
- OrderManager + PositionManager (Dedup, Lifecycle, SL/TP/Bracket/BE/Trailing, Netting/PnL)
- MarketData (CSV-Reader, Replay-Feed, Heartbeat, Time/Tick/Volume/OrderFlow-Aggregation)
- Backtest Engine (deterministisch, Fill-Modell, Kennzahlen) + Paper Trading Engine (Session)
- DevDashboard mit **Paper Trading Monitor** (`/paper`, PAPER SIMULATION ONLY — Demo per Sample-CSV)
- **Strategy Framework** (Registry + Engine: Enable/Disable, Routing, Signal-Sammlung — siehe [docs/STRATEGY_FRAMEWORK.md](docs/STRATEGY_FRAMEWORK.md))
- **Orderflow Strategy Template** (modulare Checks: Divergenz/Absorption/Sweep/CVD/… — siehe [docs/ORDERFLOW_STRATEGY_TEMPLATE.md](docs/ORDERFLOW_STRATEGY_TEMPLATE.md))
- **Data Import + Quality Layer** (ATAS-CSV: Tick/Bar/Footprint/Profile, Capabilities, QualityReport — siehe [docs/DATA_IMPORT_AND_QUALITY.md](docs/DATA_IMPORT_AND_QUALITY.md))
- **Research Analytics Layer** (Monte Carlo · Walk Forward · Parameter Sweep · Sensitivity · Ranking — siehe [docs/RESEARCH_ANALYTICS.md](docs/RESEARCH_ANALYTICS.md))

**Noch offen:**
- Phase 10 (Rest): finales Dashboard
- Phase 13/14: Live-Broker-Adapter + Safety Audit

**Dashboard starten:** siehe [docs/PAPER_TRADING.md](docs/PAPER_TRADING.md) —
`dotnet watch --project src\TradingBot.DevDashboard\TradingBot.DevDashboard.csproj run`

Details: siehe [docs/PROJECT_STATUS.md](docs/PROJECT_STATUS.md) und [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Sicherheits-Grundregel
Die Strategie erzeugt **nur Signale**. Sie sendet **niemals** Orders.
Über Ausführung entscheiden ausschließlich: **RiskManager → OrderManager → BrokerExecutionAdapter**.

- Paper Mode ist Standard. Live nur manuell aktivierbar (noch nicht implementiert).
- Pflicht: Max Daily Loss, Max Contracts, Kill Switch, Emergency Flatten.
- Keine Order ohne gültiges Broker-/Instrument-/Fee-Profil.
- Keine Order bei Feed-/Broker-Disconnect oder Positions-Mismatch.
- Kein doppeltes Signal → keine zweite Order (Idempotency-Key).
- Keine Fake-Orderflow-Daten ohne echte Bid/Ask/TradeDirection.

## Solution-Struktur

| Projekt | Aufgabe | Referenziert |
|--------|---------|--------------|
| `TradingBot.Domain` | Reine Modelle + Enums | – |
| `TradingBot.Core` | Interfaces / Abstraktionen | Domain |
| `TradingBot.Application` | Risk, Order, Position, Fee, MarketData-Aggregation | Core, Domain |
| `TradingBot.Infrastructure` | Config (JSON), Logging, MarketData (CSV/Replay) | Core, Domain |
| `TradingBot.Execution` | Broker-Adapter + MockBroker | Core, Domain |
| `TradingBot.Backtesting` | Backtest / Replay Engine | Application, Core, Domain |
| `TradingBot.PaperTrading` | Paper Trading Engine | Application, Core, Domain |
| `TradingBot.Console` | Composition Root (Startpunkt) | alle |
| `TradingBot.Tests` | Unit Tests | Domain, Core, Application, Infrastructure, Execution |

**Abhängigkeitsregel:** Pfeile zeigen nach innen Richtung `Domain`.
`Application` kennt nur Interfaces aus `Core` – nie `Infrastructure` oder `Execution`.

## Build & Test

```bash
dotnet build
dotnet test
```

## Konfiguration
JSON-Profile liegen unter `config/` (brokers, instruments, fees, risk, dashboard).
Beispielwerte sind Platzhalter – echte Broker-Gebühren trägt der User selbst ein.
MarketData-CSV-Formate sind unter [samples/marketdata/README.md](samples/marketdata/README.md) dokumentiert.

## Status
Phase 8A (MarketData) abgeschlossen. Als Nächstes: Phase 8B – Backtest Engine.
