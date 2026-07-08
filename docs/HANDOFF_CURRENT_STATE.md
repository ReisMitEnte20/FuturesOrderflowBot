# Handoff — Aktueller Projektstand

## Projektstand

- **Repository URL:** https://github.com/ReisMitEnte20/FuturesOrderflowBot.git
- **Lokaler Pfad:** `A:\Projects\FuturesOrderflowBot`
- **Aktueller Branch:** `main`
- **Letzter Commit (Code):** `ab032ea` — `feat: add research analytics layer`
- **Teststand:** 347/347 bestanden (xUnit)
- **Build-Status:** grün — 0 Warnungen, 0 Fehler (.NET 8, SDK 8.0.422)

## Fertige Phasen

- **Phase 1–7 — Core Trading Bot Foundation:** Architektur, .NET-8-Solution/Skeleton, Domain-Modelle + Interfaces, Config-/Profil-System (Broker/Instrument/Fee/Risk aus JSON, validiert), Fee-+PnL-Engine (Gross/Net getrennt, `decimal`), RiskManager (fail-closed Gatekeeper), OrderManager + PositionManager (Dedup/Idempotency, Lifecycle, SL/TP/Bracket/BE/Trailing, Netting/PnL).
- **Phase 8A — MarketData:** CSV-Reader, Replay-Feed, Heartbeat/Disconnect, Time/Tick/Volume/OrderFlow-Aggregation.
- **Phase 8B — Deterministic Backtest Engine:** Fill-Modell, Slippage/Fees, Kennzahlen; Market-Fill am Folge-Tick (kein Lookahead).
- **Phase 8C — Exit-aware Risk Handling:** OrderIntent (Entry/Add/Reduce/Close/Flatten); Exits nicht durch Entry-Limits blockiert; technische Hard-Stops blocken weiter.
- **Phase 9 — Paper Trading Engine:** langlebige Session (Start/Stop/Pause/Resume), simulierte Fills, In-Memory-Journal — vollständig simuliert.
- **Phase 10A — Paper Trading Monitor (DevDashboard):** read-only Live-Monitor (`/paper`), lokale Demo per Sample-CSV, „PAPER SIMULATION ONLY".
- **Phase 11 — Strategy Framework:** Registry + Engine (Enable/Disable, Symbol-/Datentyp-Routing, Signal-Sammlung), Dummy-Strategien, CompositeStrategy.
- **Phase 12 — Orderflow Strategy Template:** modulare Checks (Delta-Divergenz/Absorption/Sweep/CVD/…), konfigurierbar; Stacked-Imbalances/HVN-LVN ehrlich `InsufficientData`.
- **Phase 12B — ATAS / Data Import + Data Quality Layer:** CSV-Import (Tick/Bar/Footprint/Profile) mit Mapping-Profil, `OrderFlowCapabilities` als Fake-Daten-Sperre, `OrderFlowDataQualityReport`.
- **Phase 12C — Research Analytics Layer:** Monte Carlo · Walk Forward · Parameter Sweep · Sensitivity · Robustness/Overfitting · Strategy Ranking (nur read-only Analyse, nutzt bestehende BacktestEngine).

## Wichtige Architekturentscheidungen

- Strategy erzeugt **nur `TradeSignal`**, niemals `OrderRequest`.
- **RiskManager ist Gatekeeper** — entscheidet nur, sendet nie Orders, hat keine Execution-Referenz.
- **OrderManager** baut/sendet Orders **nur nach `RiskDecision.Approved`** (Idempotency-Key gegen Doppelorder).
- **PositionManager** berechnet Position/PnL (Netting), **keine Execution-Referenz**.
- **PaperTrading ist simuliert** — keine Broker-API, keine Netzwerkcalls (per Test abgesichert).
- **Backtest füllt Market-Orders erst am NÄCHSTEN Tick** (offene Orders werden vor der Strategie verarbeitet) → kein Lookahead-Bias.
- **Slippage steckt im Fill-Preis** (→ in GrossPnL) und wird **nicht doppelt** abgezogen; `TotalSlippage` ist rein informativ.
- **NetPnL ist immer nach Fees/Slippage** die relevante Größe (`NetPnL = GrossPnL − TotalFees`).
- **Exit-aware Risk:** Entry/Add streng geprüft; Reduce/Close/Flatten risiko-reduzierend (Entry-Limits übersprungen, technische Hard-Stops bleiben).
- **`OrderFlowCapabilities` verhindern Fake-Orderflow** — ohne echte Aggressor/Bid-Ask/Footprint-Daten kein Delta/Stacked-Imbalance/HVN-LVN.
- **Research Analytics bewertet Robustheit, keine Gewinnversprechen** — `RobustnessScore` ist Hilfsmetrik; OOS/Monte-Carlo werden höher gewichtet als roher Profit.

## Was auf keinen Fall geändert werden darf

- **Keine Broker-API** ohne explizite Freigabe.
- **Keine Live-Execution** ohne Safety-Phase/Audit.
- **Keine API-Keys oder Secrets committen.**
- **Keine Fake-Orderflow-Daten** erzeugen (fehlende Daten → `InsufficientData`).
- **Keine Strategy darf Orders senden.**
- **Dashboard darf keine echten Order-Buttons** bekommen (read-only; Paper-Demo-Controls nur für lokale Simulation).
- **Keine hardcoded Fees/TickValues** — alles aus Config/Profilen.
- **Keine `LiveTradingMode`-Funktion vor dem Safety-Audit.**

## Aktueller Stand der Tests

**347 / 347 bestanden** · Build 0 Warnungen / 0 Fehler.

## Offene nächste Schritte (Empfehlung, noch NICHT gestartet)

1. **Phase 12D — Research Dashboard / Backtest Report Viewer** (read-only): Monte Carlo, Walk Forward, Strategy Ranking sichtbar machen; Equity-Curve-/Drawdown-Chart. Keine Live-Controls.
2. **Phase 12E — echte ATAS-CSV-Imports** mit den realen Export-Dateien testen (Mapping-Profil kalibrieren).
3. **Phase 13 — Live Execution Interface** (nur Interface/Struktur, noch **keine** echte Broker-Anbindung).
4. **Safety Audit** vor jeglichem Live-Betrieb.

## Nächster Prompt für neue Claude-Session

> Bitte lies zuerst diese Dateien:
>
> - docs/HANDOFF_CURRENT_STATE.md
> - README.md
> - docs/PROJECT_STATUS.md
> - docs/ARCHITECTURE.md
> - docs/RESEARCH_ANALYTICS.md
> - docs/DATA_IMPORT_AND_QUALITY.md
> - docs/ORDERFLOW_STRATEGY_TEMPLATE.md
> - docs/PAPER_TRADING.md
>
> Danach fasse den Projektstand zusammen und warte auf meine Freigabe. Starte keine neue Phase ohne meine Bestätigung.
