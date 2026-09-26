# Handoff — Aktueller Projektstand

> Stand: **2026-09-27**. Ersetzt den veralteten Stand (`ab032ea`, 347 Tests). Historische Phasen siehe unten.

## Projektstand

- **Repository:** https://github.com/ReisMitEnte20/FuturesOrderflowBot.git · lokal `A:\Projects\FuturesOrderflowBot`
- **Branch:** `integrate/dashboard-backtest` (lokal; **kein Push**, **kein Merge nach `main`** — nur mit Nutzerfreigabe)
- **`main`:** lokal `a18599f7`, remote `20171440` (unverändert, nicht gemergt)
- **Gepusht:** `integrate/dashboard-backtest` → `origin` (HEAD `5a36d952`), Draft-PR **#6** gegen `main`
- **Uncommittet (Quant-Ausbau, 2026-09-27):** neues Projekt `TradingBot.Quant`, Quant-API im DevDashboard, Research-Seite im React-Dashboard, Tests, `docs/QUANT_RESEARCH.md` — **Freigabe für Commit steht aus**
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

Committeter Stand `5a36d952` (Engine/Dashboard): **.NET 477/477**, `npm test` 6/6.
Mit dem uncommitteten Quant-Ausbau (2026-09-27): **.NET 581/581 grün**, `npm test` **10/10 grün**,
`tsc --noEmit` fehlerfrei, `dotnet build TradingBot.sln` erfolgreich.

- **.NET:** 477/477 grün (`dotnet test tests/TradingBot.Tests/TradingBot.Tests.csproj`), Stand 2026-09-26 inkl. Gap-Stop-Slippage + Referenzfälle
- **React:** Build `tsc -b && vite build` erfolgreich; `npm test` (node --test) 6/6 grün
- **Browser-Abnahme** nach vollem Reload bei 1366×768 und 1920×1080: alle Schritte bestanden, 0 Konsolenfehler (Chart sofort sichtbar, echte Kerzen ohne Strategielauf, Replay vor/zurück/Play/Reset/Regler, Backtest 51 Trades = API, Trade-Auswahl Entry/Exit/SL/TP = API, Replay-Kennzahlen = Engine)

### Fachliche OHLC-Prüfung (2026-09-26)

- **Unabhängige Referenzfälle** (MES-artige Werte, Sollwerte HAND berechnet) in `OhlcEngineReferenceCasesTests.cs`: Long/Short inkl. PointValue×Menge/Gebühren/Slippage, Stop-Gap (beide Richtungen, jetzt mit Slippage), TP-Limit-Gap, SL&TP in einer Kerze, Entry+Exit in einer Kerze, OppositeSignal-Vorrang + Einmal-Schließung, Aggregat-Abstimmung mit separater Nachrechnung.
- **Referenzlauf MES 2026-06-12→06-17** (884 Bars, 51 Trades) unabhängig gegengerechnet: 0 Abweichungen je Trade (Entry/SL/TP/Exit/Gross/Fees/Net); Σ Net = NetProfit = **−524,78**; Initial+Net = FinalEquity **9475,22**; Gebühren 39,78 = 51×0,78; Slippage nicht doppelt; **Max-Drawdown 536,22**; PF 0,5659; realisiert vs. offen (MtM) sauber getrennt. API = Journal = CSV.
- **Gap-Stop-Slippage-Fix** ändert diesen Lauf NICHT (0 Gap-Stops im Fenster) — NetPnL/Drawdown unverändert; Wirkung durch RC4/RC4S getestet.
- **Trade #6 ExitBar = 90** (nicht 98): EntryBar 83 = Fr 2026-06-12 20:25, dann 6 Freitags-Bars (84–89 bis 20:55), Wochenend-Lücke (keine leeren Bars), ExitBar 90 = So 2026-06-14 22:00 (Globex-Reopen, TP-Gap). Der Sierra-Ladepfad ist für eine Anfrage ohne „Bis" seit `00a85e1d` unverändert (`BuildFileFrom(..., toUtc: null)`), die Bar-Indizes also identisch; das frühere „Bar 98" war ein Formulierungsfehler im Prosatext (die Daten/Automatikprüfung zeigten durchgehend 90).
- **.NET: 477/477 grün** (inkl. der neuen Referenzfälle). Kein Fehler nachgewiesen außer der bewusst umgesetzten Gap-Stop-Slippage-Erweiterung.

## Quant-Research-Plattform (2026-09-27, uncommittet)

Details und Methodenfestlegungen: **`docs/QUANT_RESEARCH.md`**.

- **Neues Projekt `src/TradingBot.Quant`** (referenziert Domain/Core/Backtesting, **nicht** Execution):
  Renditereihen + Kapitalbasen (realisiert vs. gesamt inkl. Mark-to-Market abzüglich Glattstellungskosten),
  Datenqualitätsprüfung, persistentes Versuchs-/Kampagnenregister mit Datenfingerabdruck (SHA-256),
  Kennzahlen mit Methode/Eingaben/Grenzen, Benchmarkadapter mit Beta/Alpha/IR samt Standardfehlern,
  Walk-forward (rollend/verankert) mit Purging/Embargo/Warmup/Holdout, Monte Carlo (Permutation,
  Moving-Block-, stationärer Bootstrap, gemeinsames Resampling), Stresstests, PBO über CSCV, PSR, DSR,
  Paper-Einträge sowie getrennte Generator-/Evaluator-Schnittstellen.
- **Backend-API** `http://localhost:5034/api/quant`: `status`, `benchmarks`, `analyze`,
  `jobs/{walkforward|montecarlo|robustness|overfitting}` (Fortschritt, Abbruch, Laufzeitgrenze 10 min),
  `campaigns`, `trials`, `papers`.
- **React-Seite** `http://127.0.0.1:5899/research` mit den Bereichen Übersicht · Benchmark ·
  Walk-forward · Monte Carlo · Robustheit · Overfitting · Experimente. Session-Spalte dort (wie auf
  `/backtest`) standardmäßig eingeklappt.
- **Ablage:** Register unter `artifacts/quant/` (gitignoriert), Benchmark-CSV unter
  `data/benchmarks/` (gitignoriert, README vorhanden).
- **Querprüfung gegen die verifizierte Engine:** realisierter Max-Drawdown **536,22** und
  Netto-PnL **−524,78** des Referenzlaufs werden über die neue Auswertungsschicht exakt reproduziert.
- **Erzwungene Regeln:** Kampagne (Budget, Suchraum, Kriterium) wird VOR der Suche gespeichert und
  gesperrt; Holdout ist gegen Leakage geschützt und nach einmaliger Auswertung verbraucht;
  Holdout-Kennzahlen erreichen den Generator nicht; negative/fehlgeschlagene/verworfene Versuche
  bleiben erfasst.

## Grenzen / offene Punkte

- Nur SMA-Crossover als Referenz-/Teststrategie (keine Edge); Kostenprofile nur `*.example.json`.
- **Datenbestand zu kurz für belastbare Quant-Aussagen:** der lokale Ausschnitt umfasst ~5 Tage
  (884 5-Min-Bars). Auf Tagesbasis entstehen dadurch 4 Renditeperioden — die Plattform meldet das
  und rechnet nichts schön, aber Sharpe/CAGR/PBO sind damit nicht belastbar.
- **Kein Benchmark hinterlegt:** ohne Datei in `data/benchmarks/` gibt es keinen S&P-500-Vergleich —
  es wird bewusst keine Ersatzkurve erzeugt.
- **SPA/Reality Check und CPCV** sind bewusst noch nicht implementiert (eigene Bausteine).
- Effektive Versuchszahl für DSR wird heuristisch aus Kandidatenkorrelationen geschätzt — als
  Annahme gekennzeichnet, nicht Teil der PSR/DSR-Originalquellen.
- Replay-Wert „Offen (MtM)“ ist brutto ohne Exit-Kosten; Wochenend-Lücken werden auf der Kategorie-Zeitachse gestaucht.
- Gap-Stop nimmt über den Open hinaus genau 1 konfigurierte Slippage-Distanz an (keine tiefenabhängige Modellierung).
- Orderflow-Features (Footprint, Delta, Imbalance) mit reinem OHLC bewusst nicht verfügbar.
- Push/Merge nach `main` offen (Freigabe nötig).
- Vite-Dev-Server kann unter Windows Dateiänderungen verpassen → bei Zweifel neu starten.

## Hindsight / CodeMunch

- **Hindsight (2026-09-26):** Die Prüfrunden-Übergabe ist in der Bank `FuturesOrderflowBot` gespeichert — Dokument **`4b94247d-ccf5-4163-bac0-849a4ba6b22a`** (context `project-handoff-current`, `state: valid`). Dieses Dokument entstand VOR den Commits und nennt daher noch „uncommitted / HEAD 54adb9e1".
- **AUSSTEHENDE Hindsight-Synchronisierung:** Das Nachtragen der Commit-Hashes (c32ab819, 32e56457, 4c928eb2; HEAD 4c928eb2) in Hindsight scheiterte am 2026-09-26 an einem Kontingentfehler (HTTP 429, Tageslimit). Keine Retry-Schleife. Maßgeblich sind bis dahin diese Datei und die Git-Historie; die Hashes bei nächster Gelegenheit in Hindsight nachtragen. Frühere Übergaben: 2026-09-26 02:00 (`4b94247d…`), 2026-09-25 (`fe88dde9…`).
- **Hindsight (2026-09-26, später am Tag):** Übergabe nach dem Push gespeichert — Dokument **`1aba4910-6788-40f9-b396-7c466a086cc5`** (Branch gepusht, Draft-PR #6, Teststand 477).
- **AUSSTEHENDE Hindsight-Synchronisierung (2026-09-27):** Die Übergabe zum **Quant-Ausbau** konnte nicht gespeichert werden — erneut Kontingentfehler (HTTP 429, Tageslimit der Gemini-Free-Tier-Extraktion). **Keine Retry-Schleife.** Maßgeblich sind bis dahin diese Datei und `docs/QUANT_RESEARCH.md`; die Quant-Übergabe bei nächster Gelegenheit in Hindsight nachtragen.
- **CodeMunch-Index** am 2026-09-26 per `index_folder` neu aufgebaut (363 Dateien, 4964 Symbole, inkl. TypeScript/TSX). Das neue Projekt `TradingBot.Quant` und die Research-Seite sind darin noch **nicht** enthalten — Index vor der nächsten Codeanalyse aktualisieren.

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

> Lies CLAUDE.md, diese Datei und `docs/QUANT_RESEARCH.md`. Prüfe Branch, HEAD und Working Tree.
> Der Quant-Ausbau (Projekt `TradingBot.Quant`, `/api/quant`, Seite `/research`) ist uncommittet und
> wartet auf Freigabe. Danach sinnvoll: längere Datenhistorie bereitstellen, echte Kostenprofile
> hinterlegen, S&P-500-Total-Return-CSV nach `data/benchmarks/` legen; erst dann SPA/Reality Check
> und CPCV als eigene Bausteine. Kein Push/Merge ohne Freigabe, Rithmic bleibt deaktiviert.
