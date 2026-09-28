# Handoff — Aktueller Projektstand

> Stand: **2026-09-27**. Ersetzt den veralteten Stand (`ab032ea`, 347 Tests). Historische Phasen siehe unten.

## Projektstand

- **Repository:** https://github.com/ReisMitEnte20/FuturesOrderflowBot.git · lokal `A:\Projects\FuturesOrderflowBot`
- **Branch:** `integrate/dashboard-backtest`, HEAD `c026d0aa`, gepusht (**kein Merge nach `main`** ohne Nutzerfreigabe)
- **`main`:** remote **`6c9cc5f1`** — **PR #6 wurde gemergt**, die OHLC-Engine und das chartzentrierte React-Dashboard sind damit in `main`. Lokales `main` (`a18599f7`) ist **veraltet** und sollte vor weiterer Arbeit aktualisiert werden.
- **Offener PR:** **#7** https://github.com/ReisMitEnte20/FuturesOrderflowBot/pull/7 (Quant-Research-Plattform, kein Draft, base `main`, MERGEABLE/CLEAN, keine CI-Checks — das Repo hat keine `.github/workflows`)
- **Commits des Quant-Ausbaus (2026-09-27, gepusht):**
  - `606bc938` feat(quant): Quant-Research-Bibliothek (neues Projekt `TradingBot.Quant`)
  - `9558adc5` test(quant): unabhängige Referenzwerte, Nullfälle und Randfälle
  - `ad70f39d` feat(devdashboard): Quant-API mit Fortschritt, Abbruch, Laufzeitgrenze
  - `e7035e97` feat(dashboard): Research-Bereich `/research`
  - `c026d0aa` docs(quant): Methoden, Konventionen, Grenzen und Handoff
- **Frühere Commits auf dem Branch (inzwischen über PR #6 in `main`):**
  - `4f3c059f` feat(dashboard): chartzentrierte Backtest-Seite mit Bar-Replay und Trade-Navigation
  - `03ac0461` feat(devdashboard): OHLC-Kerzen ohne Strategielauf laden (`/api/backtest/candles`)
  - `a8f053e2` feat(backtesting): SL/TP je Trade und Mark-to-Market je Bar
  - `a10581b5` docs(claude): Arbeitsablauf pro Prompt (Hindsight + CodeMunch)
  - `00a85e1d` / `de2d8fee` / `f938cc6c` OHLC-Engine, Backend-API, erste React-Backtest-Seite · `559f48cb` Merge `origin/dashboard` (Kollege)
  - `54adb9e1` docs(handoff): Übergabe React-Dashboard
  - `c32ab819` feat(backtesting): Gap-Stop-Slippage (Long Open−Slip, Short Open+Slip; TP/Limit unverändert) + unabhängige Referenztests `OhlcEngineReferenceCasesTests.cs`
  - `32e56457` docs(handoff): diese Datei (fachliche OHLC-Prüfrunde)

## Review-Fix-Runde PR #7 (2026-09-28) — uncommittet, wartet auf Freigabe

Sieben Code-Review-Befunde zu PR #7 (Basis `7ca36f58`) geprüft. **Alle sieben bestätigt und behoben.**
Kein Commit/Push/Merge in dieser Runde. Build grün, **.NET 599/599** (vorher 581; +18 Regressionstests),
Frontend **10/10**, `tsc`/Produktionsbuild sauber. Backend+React gestartet, Research und
Backtest/Trade-Navigation im Browser geprüft (0 Konsolenfehler); Pipeline zusätzlich per API end-to-end
belegt.

1. **Erste Periodenrendite** (`ReturnSeriesBuilder`): Nach Aggregation ging die Bewegung vom Startkapital
   zum ersten Periodenschluss verloren. Neu: expliziter **zeitbasierter** Startanker (`QuantEquityCurve.StartTime`
   aus den Engine-Metadaten) — **nicht** über Kapitalgleichheit (derselbe Wert kann nach Trades erneut auftreten).
   Erste Teilperiode wird ausgewiesen (`FirstPeriodFromStartCapital` + Hinweis). Sichtbar: Analyze zeigt nun
   **5 statt 4** Renditeperioden. Tests: `First_aggregated_period_is_measured_from_the_start_capital`,
   `First_period_is_not_dropped_just_because_it_ends_at_the_start_capital` (Round-Turn endet bei Startkapital →
   Rendite 0 bleibt erhalten), `Bar_frequency_keeps_the_first_bar_as_baseline`, `Daily_aggregation_via_builder_...`.
2. **Versuchsbudget & unveränderliche Historie** (`QuantApiService`, `JsonExperimentStore`): Kampagne wird gegen
   gesperrte Angaben geprüft (`CAMPAIGN_LOCKED_MISMATCH`); Versuche werden **vor** der Ausführung **atomar**
   reserviert (`ReserveTrialsAsync`, Budget alles-oder-nichts, parallel-sicher); eindeutige Run-Id ⇒ abgeschlossene
   Ergebnisse werden nicht überschrieben; Abbruch/Failed dauerhaft erfasst; Registerfehler sichtbar gemeldet.
   Per API belegt: Re-Run ⇒ 8 Versuche, zwei Run-Ids; Auswahlkriterium-Änderung ⇒ Ablehnung.
3. **Warmup ohne Wirkung** (`WarmupGuardStrategy`, neu): Dekorator reicht jede Kerze an die Strategie weiter
   (Indikator-Anlauf mit verfügbaren Daten), sperrt aber die **Ausführung** während des Warmups. Nachweis mit
   früh signalisierender Teststrategie (Engine: Entry Bar 1 ohne Warmup → Bar 4 mit Warmup 3).
4. **Doppelte OOS-Perioden** (`WalkForwardPlanner`): Bei `StepBars < TestBars` überlappen Testfenster; die
   Aufteilung wird jetzt nachvollziehbar **abgelehnt** (kein Doppelzählen, keine rückwärts laufenden Zeitstempel).
5. **Teilkerzen-Flags auf Teilausschnitten** (`BacktestApiService.RunEngine`): Leading/Trailing-Flags werden nur
   noch übernommen, wenn der Ausschnitt am echten Datenanfang/-ende anliegt; innenliegende Fenster behalten ihre
   vollständigen Randkerzen. Tests am `Data.EvaluatedBars`/`*PartialExcluded`.
6. **PBO-Zeitausrichtung & DSR-Grundlage** (`CandidateMatrixAligner`, neu; `QuantApiService`): Matrix wird je Fold
   **zeitstempelbasiert** (Schnittmenge) statt positional gekürzt; vorzeitige Abbrüche fallen sichtbar weg. DSR
   nutzt die **volle Kampagnenhistorie** aus dem Register; effektive Versuchszahl wird nur reduziert, wenn die
   Korrelationsbasis vollständig vorliegt, sonst konservativ. Per API belegt: „DSR-Versuchsgrundlage: 4 von 4 …
   der Kampagne …".
7. **Holdout-Schutz** (`JsonExperimentStore`, Endpoint `POST /api/quant/campaigns/{id}/holdout/consume`):
   Prüfen+Reservieren atomar unter einer Sperre (TOCTOU behoben, auch in `AddTrialAsync`); Verbrauch ist
   **einmalig** und an Kandidat/Konfiguration **gebunden**; kein „Nachsehen ohne Verbrauch". **Ehrliche Grenze:**
   der technische Schutz besteht aus reserviertem Holdout-Zeitraum + Leakage-Guard + einmaligem Verbrauch-Flag —
   keine umfassende organisatorische Garantie. Per API belegt: zweiter Verbrauch ⇒ `HOLDOUT_CONSUMED`.

Offene Punkte dieser Runde: Freigabe für Commit/Push von PR #7 steht aus; lokales `main` (`a18599f7`) weiter
veraltet ggü. `origin/main` (`6c9cc5f1`); Hindsight-Sync des Fix-Stands ausstehend (siehe unten).

## Nachprüfungsrunde PR #7 (2026-09-28, Team-Workspace-Übernahme) — uncommittet, wartet auf Freigabe

Übernahme vom bisherigen privaten Claude in den Team-Workspace. Repo, Branch `integrate/dashboard-backtest`,
HEAD `7ca36f58` und der uncommittete Working Tree (15 geändert + 4 neu) wurden gegen Git, Code und Tests
abgeglichen — deckungsgleich mit dem oben gemeldeten Fix-Stand. Kein Commit/Push/Merge. Drei offene
Nachprüfpunkte (A/B/C) bearbeitet:

**A. PBO — bei vorzeitigem Abbruch NICHT stillschweigend verkürzen (Code geändert):**
Bestätigt, dass die Zeitstempel-Schnittmenge des `CandidateMatrixAligner` bei einem vorzeitig abbrechenden
Kandidaten (Kapital ≤ 0 ⇒ `ReturnSeriesBuilder.ToReturnSeries` bricht die Reihe ab) die betroffenen Perioden
aus ALLEN Reihen entfernt. Der vorherige Stand berechnete PBO danach dennoch auf der gekürzten Basis (nur ein
Hinweis). **Neu:** `CandidateMatrixAligner.AlignByCommonTimestamps` liefert zusätzlich die Zahl verworfener
Perioden; neue Politik `CandidateMatrixAligner.PboBlockedReason(droppedPeriods, candidateErrors)`;
`QuantApiService.RebuildCandidateMatrixAsync` erfasst je Kandidat/Fold Abbrüche (Kapital ≤ 0) UND Fehler
(try/catch), `OverfittingAsync` meldet PBO dann **nachvollziehbar als nicht berechenbar** (`PboUnavailableReason`)
statt auf verkürzter Basis zu rechnen. PSR/DSR-Verhalten unverändert. Regressionstests in
`QuantValidationTests.cs`: `..._reports_dropped_periods...` (dropped=1) sowie drei `Pbo_is_(computable|blocked)_...`.

**B. Holdout — Reservierung vs. echte Auswertung (verifiziert; Grenze bleibt offen):**
`ConsumeHoldoutAsync` (Store + Service) prüft und reserviert atomar unter einer Sperre, ist einmalig und
parallel-sicher — getestet (`Parallel_holdout_consumption_succeeds_exactly_once` ⇒ genau 1 Erfolg;
`Holdout_consumption_is_one_shot_and_bound_to_a_reference`; Leakage-Guard). **Ehrliche, weiterhin OFFENE
Grenze:** Der Endpoint `POST /api/quant/campaigns/{id}/holdout/consume` setzt nur das gebundene
Reservierungs-Flag; es gibt **keine an diese Reservierung gebundene finale Holdout-Metrikberechnung**. „Zwingend
durch die Reservierung laufende Auswertung" ist damit technisch nicht gegeben, weil es (bewusst, kein neues
Feature ohne Auftrag) keinen Berechnungspfad gibt. Kein umfassender Schutz behauptet.

**C. Research-UI-Rücksprung — nicht reproduzierbar:**
Code geprüft (`Research.tsx`, `router.tsx`, `Layout.tsx`): Tab ist lokaler `useState`, keine `<form>`, kein
`navigate`/`window.location` bei Interaktion. Browser-Abnahme am laufenden Dev-Server (Prod-Build zusätzlich
grün): Direktnavigation `/research`, Tabwechsel (u. a. Overfitting hin/zurück), Job-Start bis Ergebnis
(**PBO 41,4 %**, 9 Kandidaten, 70/70 Kombinationen), Weg-/Rücknavigation `/backtest`↔`/research` — alles stabil,
kein Rücksprung, kein Tab-Reset, **0 Konsolenfehler**. Der frühere Effekt war vermutlich ein transientes
Vite-HMR-Artefakt (Windows) einer langlaufenden Dev-Session. Kein Codeänderungsbedarf.

Teststand dieser Runde: `dotnet build TradingBot.sln` grün; **.NET 602/602** (vorher 599; +3 PBO-Regressionstests);
Frontend **10/10** (`node --test`); `tsc -b && vite build` erfolgreich. Backend `:5034` und React `:5899` frisch
gestartet und geprüft. Working Tree unverändert im Dateiumfang (nur Inhalte in bereits geänderten/neuen Dateien:
`CandidateMatrixAligner.cs`, `QuantApiService.cs`, `QuantValidationTests.cs`).

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
- **Hindsight (2026-09-27, erster Versuch):** Das Speichern meldete HTTP 429, hat aber **teilweise** funktioniert — Dokument **`f63ee306-db35-48a3-8cbc-5b58e236ae62`** existiert und beschreibt den Quant-Ausbau als *uncommittet*. Diese Angabe ist seit den Commits **überholt**.
- **AUSSTEHENDE Hindsight-Synchronisierung (2026-09-27):** Das Nachtragen von **Commit-Hashes, PR #7 und dem gemergten PR #6 / neuen `origin/main`** scheiterte erneut an HTTP 429 (Tageslimit der Gemini-Free-Tier-Extraktion). **Keine Retry-Schleife.** Maßgeblich sind bis dahin diese Datei, `docs/QUANT_RESEARCH.md` und die Git-Historie; bei nächster Gelegenheit in Hindsight nachtragen.
- **AUSSTEHENDE Hindsight-Synchronisierung (2026-09-28):** Das Speichern der **Review-Fix-Runde zu PR #7** (sieben behobene Befunde) scheiterte erneut an HTTP 429 (Tageslimit der Gemini-Free-Tier-Extraktion, Limit 20/Tag). **Keine Retry-Schleife.** Maßgeblich sind bis dahin diese Datei und `docs/QUANT_RESEARCH.md`; bei nächster Gelegenheit nachtragen.
- **AUSSTEHENDE Hindsight-Synchronisierung (2026-09-28, Nachprüfungsrunde / Team-Workspace):** Recall der Bank `FuturesOrderflowBot` gelang (letzter Schreibstand dort 2026-09-26; die Fix-Runde 2026-09-27/28 fehlt weiterhin — Hindsight ist also veraltet, wie erwartet). Der kompakte `sync_retain` der Nachprüfungsrunde scheiterte erneut an **HTTP 429** (Gemini Free-Tier, 20/Tag). **Speicherversuch, NICHT bestätigt.** Keine Retry-Schleife. Maßgeblich bleiben diese Datei, `docs/QUANT_RESEARCH.md` und die Git-Historie.
- **Speicherstatus-Prüfung (2026-09-28, ausdrücklicher Nachtrag): Speicherung fehlgeschlagen; bei anschließender Lesekontrolle keine neue Übergabe abrufbar. Interne Teilverarbeitung nicht abschließend nachgewiesen.** `list_documents` zeigt unverändert 15 Dokumente, neuestes `a229ed0d` (2026-09-26 22:09, event 2026-09-27, committeter PR-#7-Stand, 581 Tests). Weder die Fix-Runde noch die Nachprüfungsrunde sind als Dokument abrufbar; auch nach dem erneut fehlgeschlagenen `sync_retain` ist keine neue Übergabe/keine neue memory_unit lesbar. Ob serverintern eine Teilverarbeitung stattfand, lässt sich von außen nicht abschließend belegen — „keine neuen Dokumente sichtbar" beweist das nicht. Der Fehler betrifft die Gemini-Fakten-Extraktion. Fehlerdetails laut API-Antwort: Anbieter Google `generativelanguage.googleapis.com`, Modell `gemini-3.8-flash`, Metrik `generate_content_free_tier_requests` / quotaId `GenerateRequestsPerDayPerProjectPerModel-FreeTier`, quotaValue **20** (pro Tag/Projekt/Modell, Free-Tier, location „global"); `RetryInfo.retryDelay` ~13 s = generischer Backoff-Hinweis, **kein** bestätigter Tages-Reset. Konkrete Ursache laut Meldung: das Free-Tier-Tageslimit der Gemini-Anbindung ist erreicht — Quota/Abrechnung des verwendeten Google-Projekts prüfen. Keine Anbieter-/Modell-/Key-/Billing-Änderungen vorgenommen. Nachtragen nach Quota-Reset.
- **CodeMunch (2026-09-28, Nachprüfungsrunde):** `jcodemunch_guide` + `resolve_repo` genutzt; nach den Änderungen `index_folder` neu aufgebaut: **409 Dateien, 6118 Symbole** (vorher 405/6037) — `CandidateMatrixAligner`, `WarmupGuardStrategy` und die neuen Tests sind jetzt enthalten.
- **CodeMunch (2026-09-28):** Session-Statistik gelesen (persistiert 3139 gesparte Tokens, 15 Task-Runs). Der Index vom 2026-09-27 ist maßgeblich; die neuen Dateien (`WarmupGuardStrategy`, `CandidateMatrixAligner`) sind noch nicht indiziert (Re-Index bei nächster Gelegenheit).
- **CodeMunch-Index** am 2026-09-27 per `index_folder` neu aufgebaut: **405 Dateien, 6037 Symbole** (vorher 363/4964) — `TradingBot.Quant` und die Research-Seite sind jetzt enthalten.

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
