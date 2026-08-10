# Collaborator Onboarding

Für neue menschliche Mitarbeiter, die (mit oder ohne Claude Code) an diesem Repo weiterarbeiten.

## Projektziel

**FuturesOrderflowBot** — ein Futures-/**Orderflow-Research-Bot** (C# / .NET 8). Das langfristige
Ziel ist eine echte **Orderflow-/Quant-Edge**, nicht einfache OHLCV-Spielerei. Eine Strategie zählt
nur, wenn sie **nach Fees, Slippage, Drawdown, Out-of-Sample und Risiko robust** ist — messbar
besser als passiv den S&P 500 (ES) zu halten. Einfache OHLCV-Strategien dienen nur der
Infrastruktur-Validierung.

> Aktueller Modus: **Research / Simulation only.** Keine Live-Execution, keine Broker-Anbindung.

## Repo-Start

```bash
git clone https://github.com/ReisMitEnte20/FuturesOrderflowBot.git
cd FuturesOrderflowBot
dotnet build
dotnet test        # sollte grün sein
# Dashboard starten:
dotnet run --project src\TradingBot.DevDashboard\TradingBot.DevDashboard.csproj
```

### Dashboard-Routen
- `/` — Projektstatus / Safety / Config-Übersicht (read-only)
- `/paper` — Paper Trading Monitor (PAPER SIMULATION ONLY)
- `/research` — Research Dashboard (Monte Carlo · Walk Forward · Ranking · Robustness · Sensitivity)
- `/replay` — Backtest Replay Visualizer (Candlestick-Replay mit Trade-Markern)

## Wichtigste Projektphasen (fertig)

Foundation → MarketData → Backtest Engine → Paper Trading → Strategy Framework →
Orderflow Strategy Template → Data Import / Data Quality → Research Analytics →
Research Dashboard → Sierra Import + Streaming Validator → Backtest Replay Visualizer.

Details: `docs/HANDOFF_CURRENT_STATE.md`, `docs/PROJECT_STATUS.md`, `docs/ARCHITECTURE.md`.

## Architekturregeln (verbindlich)

- **Strategy erzeugt nur `TradeSignal`** — niemals Orders.
- **`OrderManager`** erstellt/sendet Orders — nur nach `RiskManager`-Freigabe (`Approved`).
- **`RiskManager`** ist fail-closed **Gatekeeper**, ohne Execution-Referenz.
- **Keine Execution-Referenzen** in Dashboard/Research (per Test abgesichert).
- **Kein Fake-Orderflow.** Fehlen echte Bid/Ask/Aggressor-Daten → `InsufficientData` / fehlende
  `OrderFlowCapabilities`.
- Abhängigkeiten zeigen nach innen Richtung `Domain`; `Application` kennt nur `Core`-Interfaces.

## Market-Data-Regeln

- Große Marktdaten liegen **lokal außerhalb des Repos**, z. B. `A:\Projects\MARKET DATA\`.
- **Keine großen Marktdaten committen.** Nur kleine Samples, Config und Mapping-Templates.
- `.gitignore` blockt `samples/**/raw/*.{txt,csv,scid,tct}` (nur `.gitkeep` bleibt).
- Sierra-`.txt`-Dateien **nur streaming/chunked** lesen (`SierraLargeFileValidator`,
  `SierraOrderFlowBarBuilder`) — kein `ReadAllText`, kein `.ToList()` über die ganze Datei.
- Quellen: `docs/MARKET_DATA_SOURCE_GUIDE.md` (Sierra bevorzugt), `docs/ATAS_EXPORT_GUIDE.md`.

## Für Claude Code

- **Erst** Handoff/Docs lesen (`CLAUDE.md` fasst das zusammen).
- **Nicht ungefragt neue Phasen starten.**
- **Kompakt** antworten; keine langen Architektur-Wiederholungen.
- Immer **Build / Test / Git-Status** berichten; **kein Commit ohne Freigabe**.

## Nächste mögliche Arbeiten

- Replay-UI verbessern.
- Echte Backtest-Trades ins Replay integrieren.
- Sierra-OrderFlowBars ins Backtesting/Research einspeisen.
- Front-Month / Contract-Roll-Konzept.
- Strategy-Research mit echten Orderflow-Daten.

## Warnungen

- **Kein Live-Trading. Keine Broker-Anbindung. Keine API-Keys. Keine Secrets. Keine echten Orders.**
- **Keine Phase 13** (Live Execution Interface) ohne explizite Freigabe.
