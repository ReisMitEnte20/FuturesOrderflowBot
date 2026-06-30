# FuturesOrderflowBot

Modularer Futures-Orderflow-Trading-Bot (C# / .NET 8).
Unterstützt Backtest, Replay, Paper Trading und – später – Live Trading.
Brokerunabhängig über JSON-Profile (Broker / Instrument / Fee / Risk).

## Sicherheits-Grundregel
Die Strategie erzeugt **nur Signale**. Sie sendet **niemals** Orders.
Über Ausführung entscheiden ausschließlich: **RiskManager → OrderManager → BrokerExecutionAdapter**.

- Paper Mode ist Standard. Live nur manuell aktivierbar.
- Pflicht: Max Daily Loss, Max Contracts, Kill Switch, Emergency Flatten.
- Keine Order ohne gültiges Broker-/Instrument-/Fee-Profil.
- Keine Order bei Feed-/Broker-Disconnect oder Positions-Mismatch.

## Solution-Struktur

| Projekt | Aufgabe | Referenziert |
|--------|---------|--------------|
| `TradingBot.Domain` | Reine Modelle + Enums | – |
| `TradingBot.Core` | Interfaces / Abstraktionen | Domain |
| `TradingBot.Application` | Risk, Order, Position, Fee Logik | Core, Domain |
| `TradingBot.Infrastructure` | Config (JSON), Logging, Clock | Core, Domain |
| `TradingBot.Execution` | Broker-Adapter + MockBroker | Core, Domain |
| `TradingBot.Backtesting` | Backtest / Replay Engine | Application, Core, Domain |
| `TradingBot.PaperTrading` | Paper Trading Engine | Application, Core, Domain |
| `TradingBot.Console` | Composition Root (Startpunkt) | alle |
| `TradingBot.Tests` | Unit Tests | Domain, Core, Application, Execution |

**Abhängigkeitsregel:** Pfeile zeigen nach innen Richtung `Domain`.
`Application` kennt nur Interfaces aus `Core` – nie `Infrastructure` oder `Execution`.

## Build & Test

```bash
dotnet build
dotnet test
```

## Konfiguration
JSON-Profile liegen unter `config/` (broker, instrument, fee, risk).
Beispielwerte sind Platzhalter – echte Broker-Gebühren trägt der User selbst ein.

## Status
Skeleton (Phase 2 abgeschlossen). Domain-Modelle folgen in Phase 3.
