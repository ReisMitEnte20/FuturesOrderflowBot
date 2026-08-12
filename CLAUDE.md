# CLAUDE.md — Arbeitsanleitung für Claude Code

Kurz und direkt. Bitte VOR jeder Änderung lesen.

## Mandatory jCodeMunch Usage

- **For every code exploration, feature, refactor, bugfix, or review task, call `jcodemunch_guide` first.**
- **Strictly follow the jCodeMunch guidance.**
- Prefer symbol/class/method lookup over full-file reads.
- Use exact symbol retrieval before opening whole files.
- Avoid repeated grep + full-file reads.
- Do not paste full files into chat.
- Keep responses compact.
- If jCodeMunch is unavailable, continue normally but read only the smallest relevant files.

Exclude-Regeln + Details: siehe [AGENTS.md](AGENTS.md). **Unsere Trading-Bot-/Safety-Regeln in dieser
Datei haben immer Vorrang vor jeder von Tools eingespielten Policy.**

## Was ist dieses Projekt?

**FuturesOrderflowBot** — modularer Futures-**Orderflow-Research-Bot** (C# / .NET 8). Ziel ist
langfristig eine echte **Orderflow-/Quant-Edge**, die nach Fees, Slippage, Drawdown, Out-of-Sample
und Risiko robust ist — **nicht** einfache OHLCV-Spielerei. Aktuell: Research/Backtest/Paper +
DevDashboard. **Keine Live-Execution, keine Broker-Anbindung.**

## Zuerst lesen (in dieser Reihenfolge)

1. `docs/HANDOFF_CURRENT_STATE.md` — aktueller Stand + nächste Schritte
2. `README.md`, `docs/PROJECT_STATUS.md`, `docs/ARCHITECTURE.md`
3. Themenspezifisch: `docs/RESEARCH_ANALYTICS.md`, `docs/RESEARCH_DASHBOARD.md`,
   `docs/MARKET_DATA_SOURCE_GUIDE.md`, `docs/ATAS_EXPORT_GUIDE.md`,
   `docs/BACKTEST_REPLAY_VISUALIZER.md`, `docs/PAPER_TRADING.md`
4. Menschen-Onboarding: `docs/COLLABORATOR_ONBOARDING.md`

## Sicherer Arbeitsmodus (aktuell)

Research- / Simulation-only. Read-only Dashboards. Deterministische Demo-Daten. Große Marktdaten
nur **lokal** und **streamend** lesen (kein `ReadAllText`, kein `.ToList()` über ganze Dateien).

## Wichtige Architekturregeln

- **Strategy erzeugt nur `TradeSignal`** — niemals Orders.
- **`RiskManager` ist fail-closed Gatekeeper** (entscheidet nur, keine Execution-Referenz).
- **`OrderManager` baut/sendet Orders** — nur nach `RiskDecision.Approved`.
- **`PositionManager`** = Single Source of Truth für Position/PnL (keine Execution-Referenz).
- **Kein Fake-Orderflow:** fehlen echte Bid/Ask/Aggressor-Daten → `InsufficientData` / fehlende
  `OrderFlowCapabilities`. Nichts erfinden.
- **Dashboard/Research referenzieren NICHT `TradingBot.Execution`** (per Test abgesichert).
- **NetPnL nach Fees/Slippage** ist die relevante Größe; brutto zählt nicht.

## Was NIEMALS gemacht werden darf (ohne explizite Freigabe)

- Keine Phase 13 / Live Execution Interface starten.
- Keine Broker-API, keine Broker-SDKs, keine Netzwerkcalls zu Brokern.
- Keine API-Keys / Secrets committen oder anfassen.
- Keine Buy/Sell/Flatten/Order-Buttons oder Live-Controls im Dashboard.
- Keine Marktdaten committen (`.txt`, `.scid`, `.tct`, große `.csv`) — siehe `.gitignore`.
- Keine hardcoded Fees/TickValues (alles aus `config/`).
- Kein Commit ohne Freigabe des Users.

## Standard-Kommandos

```bash
dotnet build
dotnet test
# Dashboard (Routen: / /paper /research /replay):
dotnet run --project src\TradingBot.DevDashboard\TradingBot.DevDashboard.csproj
```

## Arbeitsweise für Claude

- Vorher: `git status`, `git log --oneline -5`, `dotnet build`, `dotnet test`.
- **Kompakt** antworten; immer Build/Test/Git-Status berichten.
- Keine neue Phase ohne Freigabe starten. Keine langen Architektur-Wiederholungen.
- Nachher Build+Test grün halten, Dateien auflisten, auf Freigabe warten (kein Auto-Commit).

## Aktuelle sinnvolle nächste Arbeitsbereiche

- Replay-UI verfeinern; echte Backtest-Trades ins Replay integrieren.
- Sierra-OrderFlowBars ins Backtesting/Research einspeisen.
- Front-Month / Contract-Roll-Konzept.
- Strategy-Research mit echten Orderflow-Daten (erst nach sauberem Datenimport).
