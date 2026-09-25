# Handoff — Aktueller Projektstand

> Stand: **2026-09-26**. Ersetzt den veralteten Stand (`ab032ea`, 347 Tests). Historische Phasen siehe unten.

## Projektstand

- **Repository:** https://github.com/ReisMitEnte20/FuturesOrderflowBot.git · lokal `A:\Projects\FuturesOrderflowBot`
- **Branch:** `integrate/dashboard-backtest` (lokal; **kein Push**, **kein Merge nach `main`** — nur mit Nutzerfreigabe)
- **`main`:** `a18599f7` (unverändert)
- **Letzte Commits auf dem Branch:**
  - `4f3c059f` feat(dashboard): chartzentrierte Backtest-Seite mit Bar-Replay und Trade-Navigation
  - `03ac0461` feat(devdashboard): OHLC-Kerzen ohne Strategielauf laden (`/api/backtest/candles`)
  - `a8f053e2` feat(backtesting): SL/TP je Trade und Mark-to-Market je Bar
  - `a10581b5` docs(claude): Arbeitsablauf pro Prompt (Hindsight + CodeMunch)
  - `00a85e1d` / `de2d8fee` / `f938cc6c` OHLC-Engine, Backend-API, erste React-Backtest-Seite · `559f48cb` Merge `origin/dashboard` (Kollege)
- Diese Handoff-Datei folgt als eigener Doku-Commit auf `4f3c059f`.

## Aktuelle Nutzerentscheidung (Vorrang vor älteren Notizen)

- **Tick-Replay-Weiterentwicklung pausiert.** Priorität: **OHLCV-Backtesting im React-Dashboard `dashboard/`**, bestehende Engine-Funktionen wiederverwenden.
- Die chartzentrierte Backtest-Oberfläche wurde vom Nutzer geprüft und für diesen Arbeitsschritt **freigegeben**.
- **Nächster Schritt:** fachliche Prüfung der OHLC-Backtests (keine Strategieoptimierung ohne Auftrag).
- Keine echten Orders, keine Broker-/Marktdaten-Calls; **Rithmic bleibt deaktiviert** (`Rithmic:Enabled=false`, `/api/rithmic/connect` → 503).

## Vorhanden

- **OHLC-Engine** `src/TradingBot.Backtesting/Ohlc/` (Execution-frei): bar-basiert, deterministisch; Trades mit `StopLossPrice`/`TakeProfitPrice`; Equity je Bar realisiert, `OpenPnL` nur als Mark-to-Market-Anzeige.
- **Backend-API** (DevDashboard, `http://localhost:5034/api/backtest`): `strategies`, `instruments`, `data-sources` (mit Standard-Ausschnitt), `POST candles` (nur Kerzen, Von/Bis), `POST run` (Backtest + CostProfile).
- **React-Backtest-Seite** (`http://127.0.0.1:5899/backtest`): großer Chart im ersten Bildschirm, Werkzeugleiste, einklappbare Einstellungen, Journal neben dem Chart, Übersicht vs. Bar-Replay (ohne Zukunftsdaten), Trade-Navigation mit Entry/Exit + SL/TP, Auswertung in Tabs. Session-Spalte auf `/backtest` standardmäßig eingeklappt.
- Datenquelle lokal: Sierra-Tickdatei `A:\Projects\MARKET DATA\MESM26-CME.txt` → einmalige Aggregation echter Zeitkerzen aus **Last**-Preisen (High/Low der Datei sind Ask/Bid, nicht Handelsextreme). Standard-Ausschnitt ab 2026-06-12 13:30 UTC, 1,5 Mio. Rohzeilen ≈ 884 5-Min-Bars (~2 s).

## Ausführungsannahmen (OHLC-Engine)

- Kein Look-ahead: Signal am Bar-Schluss → Ausführung am OPEN des Folge-Bars.
- Gap-Fills nach Ordertyp: Stop → Market zum (schlechteren) OPEN; Take-Profit = Limit, Preisverbesserung zum OPEN, nie schlechter als die Grenze.
- SL und TP in derselben Kerze → konservativ Stop-Loss, als mehrdeutig markiert. Gegensignal am nächsten OPEN vor intrabar-Schutzorders.
- Slippage nur im Fill-Preis von Market-Orders (kein Doppelabzug); Gebühren aus Profilen.
- Offene Position am Datenende → Zwangsschluss `EndOfData`. Unvollständige Randkerzen standardmäßig ausgeschlossen.

## Teststand (dem geprüften Stand zugeordnet)

Ausgeführt am 2026-09-25/26 auf genau dem Code von `4f3c059f` (Artefakt-Zeitstempel nach der letzten Quelländerung, danach keine Codeänderung):

- **.NET:** 467/467 grün (`dotnet test tests/TradingBot.Tests/TradingBot.Tests.csproj`)
- **React:** Build `tsc -b && vite build` erfolgreich; `npm test` (node --test) 6/6 grün
- **Browser-Abnahme** nach vollem Reload bei 1366×768 und 1920×1080: alle Schritte bestanden, 0 Konsolenfehler (Chart sofort sichtbar, echte Kerzen ohne Strategielauf, Replay vor/zurück/Play/Reset/Regler, Backtest 51 Trades = API, Trade-Auswahl Entry/Exit/SL/TP = API, Replay-Kennzahlen = Engine)

## Grenzen / offene Punkte

- Nur SMA-Crossover als Referenz-/Teststrategie (keine Edge); Kostenprofile nur `*.example.json`.
- Replay-Wert „Offen (MtM)“ ist brutto ohne Exit-Kosten; Wochenend-Lücken werden auf der Kategorie-Zeitachse gestaucht.
- Orderflow-Features (Footprint, Delta, Imbalance) mit reinem OHLC bewusst nicht verfügbar.
- Push/Merge nach `main` offen (Freigabe nötig).
- Vite-Dev-Server kann unter Windows Dateiänderungen verpassen → bei Zweifel neu starten.

## Hindsight / CodeMunch

- **Hindsight-Synchronisierung AUSSTEHEND:** Das Speichern dieser Übergabe in der Bank `FuturesOrderflowBot` ist am 2026-09-26 zweimal an einem Kontingentfehler des Hindsight-Servers gescheitert (HTTP 429, Tageslimit des verwendeten Sprachmodells). Diese Datei ist die maßgebliche Übergabe; sobald das Kontingent wieder verfügbar ist, ihren Inhalt als Übergabe in Hindsight speichern. Letzte erfolgreich gespeicherte Hindsight-Übergabe: 2026-09-25 (Dokument `fe88dde9…`).
- **CodeMunch-Index** am 2026-09-26 per `index_folder` neu aufgebaut (363 Dateien, 4964 Symbole, inkl. TypeScript/TSX).

## Betrieb (lokal, Simulation-only)

```bash
dotnet run --project src/TradingBot.DevDashboard/TradingBot.DevDashboard.csproj --launch-profile http   # Backend :5034
npm --prefix dashboard run dev                                                                          # React :5899
```

## Was auf keinen Fall geändert werden darf

- Keine Broker-API / Live-Execution ohne explizite Freigabe; keine Secrets committen.
- Strategy erzeugt nur `TradeSignal`; RiskManager fail-closed ohne Execution-Referenz; Dashboard/Research referenzieren `TradingBot.Execution` nicht (per Test abgesichert).
- Kein Fake-Orderflow; keine erfundenen Kurse/Trades; keine hardcoded Fees/TickValues.
- Keine Order-/Buy-/Sell-Buttons im Dashboard; keine Roh-Marktdaten committen.

## Historie (abgeschlossen)

Phasen 1–12C (Core, MarketData, Tick-Backtest, Exit-aware Risk, Paper Trading, Strategy Framework, Orderflow-Template, Data Import, Research Analytics), Research Dashboard, Sierra-Streaming-Import, Tick→OrderFlow-Aggregation und Blazor-Replay-Visualizer (Tick-/Intrabar-Replay, jetzt pausiert).

## Nächster Prompt für eine neue Session

> Lies CLAUDE.md und diese Datei. Prüfe Branch, HEAD und Working Tree. Danach: fachliche Prüfung der OHLC-Backtests (Engine-Regeln, Kennzahlen, Datenbasis) — ohne Optimierung, ohne Push/Merge, Rithmic deaktiviert.
