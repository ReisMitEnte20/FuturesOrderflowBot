# Inventar des Backtesting-Berichts (Bestandsaufnahme vor dem Umbau, 2026-11-26)

Grundlage sind Code, API-Modelle und gespeicherte Berichte. Die Spalte „neuer Anzeigeort“ bezieht sich auf den Umbau:
Übersicht · Ansicht **E** = „Ergebnis & Risiko“ · **R** = „Robustheit“ · **T** = „Trades & Replay“.

**Abkürzungen der Codestellen**

| Kürzel | Datei |
|---|---|
| RV | `dashboard/src/components/backtesting/ReportView.tsx` |
| BT | `dashboard/src/pages/Backtesting.tsx` |
| SB | `dashboard/src/components/sessions/SessionsBacktest.tsx` |
| RS | `dashboard/src/pages/Research.tsx` |
| BRM | `src/TradingBot.DevDashboard/Services/Quant/Report/BacktestReportModels.cs` |
| BS | `.../Quant/Report/BacktestingService.cs` |
| FR | `.../Quant/ResearchOrchestrator.FullReport.cs` (OHLC) |
| OR | `.../ResearchOrchestrator.OrderflowReport.cs` (Tick) |
| SRS | `src/TradingBot.DevDashboard/Services/Sessions/SessionRunService.cs` |

**Pfade:** OHLC = `RunFullReportAsync`, Tick = `RunOrderflowReportAsync` (DeltaFlow), LN+NY = London + New York,
MTF = neuer Multi-Timeframe-Lauf.

**Serialisierung:**
- Gespeicherte Berichte unter `artifacts/quant/registry/research/*.json` sind PascalCase.
- Die API liefert camelCase (`run.report`).
- Sessions-Läufe sind camelCase.

| Bereich | Kennzahl / Prüfung | Backend-Feld | bisher | Pfad | Einheit | neu |
|---|---|---|---|---|---|---|
| Vor dem Start | Startentscheidung, Datenanforderungen | `BacktestingPreview.Requirements[]`, `CanStart` | BT `StartDecisionCard` | beide | erfüllt/fehlt | Lauf starten (unverändert) |
| Vor dem Start | Geplante Prüfungen je Abschnitt | `Preview.Checks[]` (`PlannedCheck`) | BT `StartDecisionCard` | beide | Status | Lauf starten, einklappbar |
| Vor dem Start | Umfang, Kosten, Zeitraum | `Preview.{From,To,Bars,Sessions,…,Costs,FeeIsExample}` | BT `StartDecisionCard` | beide | USD/Anzahl | Lauf starten |
| Übersicht | Netto nach Kosten | `Overview.Figures[net]` | RV Übersicht, BT Laufliste | beide | USD | Übersicht oben |
| Übersicht | Beobachteter max. DD | `Figures[dd]` | RV Übersicht | beide | USD geschl. Trades | Übersicht oben |
| Übersicht | WF-Testnetto | `Figures[oos]` | RV Übersicht, Historie | beide | USD | Übersicht |
| Übersicht | Max. DD P95 (simuliert) | `Figures[simdd]` | RV Übersicht | beide | USD, simuliert | Übersicht (als „simuliert“) |
| Übersicht | Trades / WF-Testsessions | `Figures[trades]`, `[sessions]` | RV Übersicht | beide | Anzahl | Übersicht |
| Übersicht | Signal-/Ausführungsdaten | `Provenance.SignalData/ExecutionData` | RV Übersicht | beide | Text | Übersicht |
| Übersicht | Holdout-Hinweis | `Provenance.Holdout*` | RV Übersicht | beide | Sessions | Übersicht |
| Herkunft | Strategie-/Engine-Version, Engine-Art | `Provenance.StrategyVersion/EngineVersion/EngineKind` | RV Herkunft | beide | Text | E |
| Herkunft | Daten-Hash, Quelle, Datenart | `Provenance.DataSha256/DataSource/DataKind` | RV Herkunft | beide | SHA | E |
| Herkunft | Zeitraum, Entwicklungsende, Holdout | `Provenance.DataFrom…HoldoutSessions` | RV Herkunft | beide | UTC | E |
| Herkunft | Sessions, Parameter, Kontrakte, Startkapital | `Provenance.SessionsUsed/Parameters/Quantity/InitialBalance` | RV Herkunft | beide | – | E |
| Herkunft | Kostenmodell, Instrument-Prüfung | `Provenance.CostModel/InstrumentCheck` | RV Herkunft, BT RunHeader | beide | USD | E |
| Herkunft | Seeds, Datenzugriffe, Fill-Modell, Analyseprofil | `Provenance.Seeds/DataFileReads/FillModel/AnalysisProfile` | RV Herkunft | beide | – | E |
| Laufstatus | Status je Abschnitt | `ResearchRunRecord.Steps[]` | RV Abschnittsköpfe | beide | Status | alle Ansichten (Kopf) |
| 01 | Datenart + Hinweise | `Data.DataKind/DataKindNotes` | RV 01 | beide | Text | E |
| 01 | Handelstage, Median Bars, verkürzte Tage | `Data.Sessions[]/MedianBarsPerSession` | RV 01 | beide | Tage | E |
| 01 | Lücken in Sessions | `Data.IntraSessionGaps/LargestGapMinutes` | RV 01 | OHLC | Anzahl/min | E |
| 01 | Sommerzeitwechsel | `Data.DstTransitions[]` | RV 01 | beide | Datum | E |
| 01 | Abschneidetest, kein rückwirkender Fill, Zeitstempel, OHLC-Mehrdeutigkeit, Delta gemessen | `Data.TemporalChecks[]` | RV 01 Tabelle | je Pfad | bestanden/Hinweis | E |
| 01 | Datenqualitäts-Befunde | `Data.Quality.Issues[]` | RV 01 | OHLC | Anzahl | E |
| 01 | Session-Definition, Grenzen | `Data.SessionDefinition/HistoryLimits` | RV 01 | beide | Text | E |
| 01 | Drops / generierte Replay-Ereignisse | `SessionPlanDto…RecorderDrops`, Audit-Herkunft | SB Plan, Chart-Banner | LN+NY/Chart | Anzahl | E (LN+NY), MTF-Datenprüfung |
| 02 | Netto, Brutto, Gebühren, Slippage, vor Kosten | `Result.Stats.*` | RV 02 | beide | USD | E |
| 02 | Trades, Long/Short, Trefferquote, W/L/BE | `Stats.Trades/Longs/Shorts/WinRate/Wins/Losses/Breakeven` | RV 02 | beide | %/Anz. | E (+ Übersicht) |
| 02 | Ø Gewinn/Verlust, Payoff, Erwartungswert, PF | `Stats.AverageWin…ProfitFactor` | RV 02 | beide | USD | E (PF in Übersicht) |
| 02 | Exposure, Haltedauer, größter Gewinn/Verlust | `Stats.Exposure/…/LargestLoss` | RV 02 | beide | %/min/USD | E |
| 02 | Realisiert vs. offen am Ende | `Stats.RealizedNetPnL/UnrealizedAtEnd/OpenAtEnd` | RV 02 | beide | USD | E |
| 02 | Signale, verworfene Einstiege, Bars | `Result.SignalsGenerated/MissedEntrySkips/…` | RV 02 | beide | Anzahl | E |
| 03 | Max. DD geschlossen / MtM (USD, %) | `Equity.ClosedTrades/MarkToMarket.MaxDrawdown(Fraction)` | RV 03 | beide | USD/% | E (Übersicht: geschl.) |
| 03 | DD-Status, Erholung, längste Zeit unter Hoch | `ClosedTrades.{MaxDrawdownOpen,…}` | RV 03 | beide | Tage | E |
| 03 | Längste Verlustserie | `Equity.LosingStreak` | RV 03 | beide | Trades/USD | E |
| 03 | Kapital- und DD-Kurve | `MarkToMarket.{T,Equity,Drawdown}` | RV 03 | beide | USD | Übersicht (Kurve) + E |
| 04 | Sharpe, Sortino, Calmar, CAGR, Vola, max. DD, ES | `Risk.Metrics[]` | RV 04 | beide | Verhältnis/% | Übersicht (S/S/C) + E |
| 04 | Frequenz, Annualisierung, Kapitalbasis | `Risk.{Frequency,…}` | RV 04 | beide | – | E |
| 05 | Sensitivität: Anwendbarkeit, Parameterpunkte, 2D-Heatmap | `Sensitivity.*` | RV 05 | beide | USD | R |
| 06 | Auswahlregel, Raster, Warm-up, Grenzen | `Oos.*Rule/Grid/WarmupBars/…` | RV 06 | beide | Text | R |
| 06 | Varianten Split/rollierend/anwachsend | `Oos.Variants[]` | RV 06 | beide | Sessions | R |
| 06 | Gewinner vs. Standard auf denselben Tagen, Folds, Tagesdetails | `Variant.*`, `Folds[]` | RV 06 | beide | USD | R |
| 06 | Tick-Stress mit Engine-Neuläufen | `Oos.TickStress` | RV 06 | Tick | USD | R (Kosten & Ausführung) |
| 07 | Basis, Wiederholungen, Seed | `Simulation.{Basis,Iterations,Seed}` | RV 07 | beide | – | R |
| 07 | Umordnung, Bootstrap, Block-Bootstrap, Session-(Block-)Bootstrap | `Simulation.Methods[]` | RV 07 | beide | USD | R |
| 07 | Ausführungsstress (Trade-Ebene), verpasste Fills | `Simulation.ExecutionStress/MissedFills` | RV 07 | beide | USD, hypothetisch | R |
| 07 | Verzögerte Fills (Engine-Neulauf) | `Simulation.DelayedFills` | RV 07 | OHLC | USD | R |
| 08 | Gewinnschwelle, Kostenszenarien (Trade-Ebene) | `Costs.Breakeven` | RV 08 | beide | USD/Ticks | R |
| 08 | Kostenraster (Engine-Neulauf) | `Costs.CostGrid` | RV 08 | OHLC | USD | R |
| 08 | OHLC-Mehrdeutigkeit, Datengrenzen | `Costs.Ambiguous*/DataLimits` | RV 08 | OHLC/beide | USD | R |
| 09 | Netto je Tag/Woche/Monat/Wochentag/Uhrzeit/Session/Richtung | `Stability.Breakdowns[]` | RV 09 | beide | USD | R (Tag/Session auch in E) |
| 09 | Regime, rollierender Erwartungswert/DD | `Stability.Regimes/Rolling` | RV 09 | beide | USD | R |
| 10 | Konzentration (beste Trades/Tag/Woche) | `Concentration.Result.*` | RV 10 | beide | USD/% | R |
| 10 | Je ein Handelstag weggelassen | `Result.LeaveOneOut*` | RV 10 | beide | USD | R |
| 11 | Kontrollläufe (kein Trade, Basis, Referenz, OOS) | `Controls.Rows[]` | RV 11 | OHLC alle / Tick 3 | USD | R |
| 11 | Zufallskontrolle, Versuchshistorie | `Controls.Random/Trials` | RV 11 | beide | USD/Anz. | R |
| 11 | PBO, PSR, DSR (mit Grund bei Fehlen) | `Controls.Overfitting` | RV 11 | OHLC | % | R |
| 11 | Externe Benchmark | `Controls.Benchmark` | RV 11 | OHLC | – | R |
| 12 | Journal, Signal-/Fill-Zeiten, MAE/MFE mit Herkunft | `Trades.Trades[]`, `ExcursionDefinition` | RV 12 | beide | USD | T |
| 12 | Fill-Preise, Brutto, Gebühren, Stop/Ziel | `TradeRow.*` | RV TradeReplay / SavedRunChart | beide | USD | T |
| Holdout | reserviert/unbenutzt, Vorschlag | `ResearchRunRecord.Holdout*/HoldoutProposal` | RV Holdout | OHLC | Zeitraum | Übersicht-Hinweis + R (unverändert, wird nicht geöffnet) |
| Holdout | Schlussprüfung | `HoldoutEvaluationRecord` | RS HoldoutBlock | OHLC | USD | Expertenansicht (unverändert) |
| Vergleich | Fairness, gemeinsame Basis, Rendite/Risiko/Stabilität | `CompareResult/CompareRow` | BT CompareResultView | beide | USD | Strategien vergleichen (unverändert) |
| Laufliste | Läufe, Historie | `ReportSummary` | BT RecentRuns/HistoryView | beide | – | Lauf starten / Alle Läufe |
| LN+NY | Plan je Tag, Ergebnis je Session + Gesamt, Konto, Abbruch, Übergänge, Sessions, Tage, Gründe/IDs, Journal, Regeln | `SessionPlanDto`, `SessionRunResult.*` | SB | LN+NY | USD/Tage | London + New York (unverändert) |
| MTF (neu) | Datenprüfung, Entscheidungsspur, Trade nachspielen | `MtfDataCheck`, `MtfRunResult`, `MtfReplayWindow` | – | MTF | – | Lauf starten / T |

**Vom Backend berechnet, bisher nicht angezeigt** (bleibt unverändert, kein Verlust):
- `Provenance.CodeVersion`
- `Result.OrdersSubmitted/OpenPositionAtEnd/Costs`
- `DrawdownResult.Basis/RecoveryTime/CurrentDrawdown/UnderwaterAtEnd`
- `LosingStreak.From/To`
- `QuantMetricDto.Inputs/Limitation`
- `Sensitivity.SessionKeys`
- `Fold.TestTradeRows`, `Oos.DevelopmentSessionIds`
- `SimDistribution` P25/P75/Min/Max/Mittel, `Method.MaxDrawdownFraction/Notes`
- `Costs.AmbiguousRows`
- `LeaveOneOut[].SessionNet`
- `Overfitting.Combinations/ShareNegativeOutOfSample/Pairs`
- `Benchmark.Metrics`
- `TickStressRow.Windows`
- `Issues[].Examples`
- LN+NY `SignalsGenerated/OrdersSubmitted/OrdersUnfilledAtEnd`

**Im Tick-Pfad nicht angebunden** (zeigt einen Grund):
- verzögerte Fills als Engine-Neulauf
- Kostenraster als Neulauf
- PBO/PSR/DSR
- Benchmark
- Versuchsregister
- Holdout-Schlussprüfung
- Datenqualitätstabelle und Lückenberechnung

**Gespeicherte Referenzberichte für den Vorher/Nachher-Abgleich:**
- OHLC: `artifacts/quant/registry/research/research-1795117015701-3f81bbee.json`
- Tick: `…/research-1795133923380-21edac65.json`
- LN+NY: `artifacts/sessions-runs/lnny-20261125-114639-20261117-20261117.json`
