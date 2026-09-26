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
  - `54adb9e1` docs(handoff): Übergabe React-Dashboard
  - `c32ab819` feat(backtesting): Gap-Stop-Slippage (Long Open−Slip, Short Open+Slip; TP/Limit unverändert) + unabhängige Referenztests `OhlcEngineReferenceCasesTests.cs`
  - `32e56457` docs(handoff): diese Datei (fachliche OHLC-Prüfrunde)

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
- Gap-Fills nach Ordertyp: Stop → Market zum OPEN mit nachteiliger Slippage (Long Open − Slip, Short Open + Slip); Take-Profit = Limit, Preisverbesserung zum OPEN, nie schlechter als die Grenze (keine adverse Slippage auf Limit).
- SL und TP in derselben Kerze → konservativ Stop-Loss, als mehrdeutig markiert.
- **Simulationsannahme „OppositeSignal vor Schutzorder am OPEN":** ein am Vorabend erzeugtes Gegensignal wird am nächsten OPEN vor den intrabar-Schutzorders ausgeführt. Das ist eine gesetzte, deterministische Modellkonvention. Sie ist für die geprüften Fälle preisneutral — namentlich, wenn Gegensignal und ein am selben OPEN greifender Gap-Stop dieselbe Position schließen (beide füllen am OPEN ± Slippage). Das gilt **nicht pauschal** für jede Schutzorder-/Gap-Konstellation (z. B. Stop, der erst intrabar griffe) — dort ist die Priorisierung eine bewusste Modellwahl mit möglichem Ergebnisunterschied. Folge in beiden Fällen: jede Position wird genau EINMAL geschlossen; beim Drehen werden Entry/SL/TP der neuen Position neu gesetzt (keine Alt-Order-Leiche).
- Slippage nur im Fill-Preis von Market-Orders (kein Doppelabzug); Gebühren aus Profilen.
- Offene Position am Datenende → Zwangsschluss `EndOfData`. Unvollständige Randkerzen standardmäßig ausgeschlossen.

## Teststand (dem geprüften Stand zugeordnet)

Committeter UI-/Backend-Stand `4f3c059f`: **.NET 467/467**, React-Build + `npm test` 6/6, Browser-Abnahme (siehe unten). Nach der fachlichen OHLC-Prüfung (uncommitteter Gap-Stop-Fix + Referenzfälle): **.NET 477/477 grün**.

- **.NET:** 477/477 grün (`dotnet test tests/TradingBot.Tests/TradingBot.Tests.csproj`), Stand 2026-09-26 inkl. Gap-Stop-Slippage + Referenzfälle
- **React:** Build `tsc -b && vite build` erfolgreich; `npm test` (node --test) 6/6 grün
- **Browser-Abnahme** nach vollem Reload bei 1366×768 und 1920×1080: alle Schritte bestanden, 0 Konsolenfehler (Chart sofort sichtbar, echte Kerzen ohne Strategielauf, Replay vor/zurück/Play/Reset/Regler, Backtest 51 Trades = API, Trade-Auswahl Entry/Exit/SL/TP = API, Replay-Kennzahlen = Engine)

### Fachliche OHLC-Prüfung (2026-09-26)

- **Unabhängige Referenzfälle** (MES-artige Werte, Sollwerte HAND berechnet) in `OhlcEngineReferenceCasesTests.cs`: Long/Short inkl. PointValue×Menge/Gebühren/Slippage, Stop-Gap (beide Richtungen, jetzt mit Slippage), TP-Limit-Gap, SL&TP in einer Kerze, Entry+Exit in einer Kerze, OppositeSignal-Vorrang + Einmal-Schließung, Aggregat-Abstimmung mit separater Nachrechnung.
- **Referenzlauf MES 2026-06-12→06-17** (884 Bars, 51 Trades) unabhängig gegengerechnet: 0 Abweichungen je Trade (Entry/SL/TP/Exit/Gross/Fees/Net); Σ Net = NetProfit = **−524,78**; Initial+Net = FinalEquity **9475,22**; Gebühren 39,78 = 51×0,78; Slippage nicht doppelt; **Max-Drawdown 536,22**; PF 0,5659; realisiert vs. offen (MtM) sauber getrennt. API = Journal = CSV.
- **Gap-Stop-Slippage-Fix** ändert diesen Lauf NICHT (0 Gap-Stops im Fenster) — NetPnL/Drawdown unverändert; Wirkung durch RC4/RC4S getestet.
- **Trade #6 ExitBar = 90** (nicht 98): EntryBar 83 = Fr 2026-06-12 20:25, dann 6 Freitags-Bars (84–89 bis 20:55), Wochenend-Lücke (keine leeren Bars), ExitBar 90 = So 2026-06-14 22:00 (Globex-Reopen, TP-Gap). Der Sierra-Ladepfad ist für eine Anfrage ohne „Bis" seit `00a85e1d` unverändert (`BuildFileFrom(..., toUtc: null)`), die Bar-Indizes also identisch; das frühere „Bar 98" war ein Formulierungsfehler im Prosatext (die Daten/Automatikprüfung zeigten durchgehend 90).
- **.NET: 477/477 grün** (inkl. der neuen Referenzfälle). Kein Fehler nachgewiesen außer der bewusst umgesetzten Gap-Stop-Slippage-Erweiterung.

## Grenzen / offene Punkte

- Nur SMA-Crossover als Referenz-/Teststrategie (keine Edge); Kostenprofile nur `*.example.json`.
- Replay-Wert „Offen (MtM)“ ist brutto ohne Exit-Kosten; Wochenend-Lücken werden auf der Kategorie-Zeitachse gestaucht.
- Gap-Stop nimmt über den Open hinaus genau 1 konfigurierte Slippage-Distanz an (keine tiefenabhängige Modellierung).
- Orderflow-Features (Footprint, Delta, Imbalance) mit reinem OHLC bewusst nicht verfügbar.
- Push/Merge nach `main` offen (Freigabe nötig).
- Vite-Dev-Server kann unter Windows Dateiänderungen verpassen → bei Zweifel neu starten.

## Hindsight / CodeMunch

- **Hindsight synchronisiert (2026-09-26):** Die Prüfrunden-Übergabe wurde in der Bank `FuturesOrderflowBot` gespeichert (context `project-handoff-current`). Die vorherigen 429-Kontingentfehler waren vorübergehend; diese Datei bleibt die maßgebliche, ausführliche Übergabe. Frühere Übergabe: 2026-09-25 (`fe88dde9…`).
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
