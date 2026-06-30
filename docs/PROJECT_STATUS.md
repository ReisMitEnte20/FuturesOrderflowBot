# Projektstatus – FuturesOrderflowBot

Stand: Phase 8A abgeschlossen · **Tests: 146/146 grün** · Build: 0 Warnungen / 0 Fehler

> Read-only Übersicht. Es existiert **keine Live-Execution** und **keine Broker-API**.
> Alle Broker-/Fee-/Tick-Werte stammen aus Config-Profilen (`config/`), nichts ist hardcoded.

## Status-Übersicht

| Phase | Modul | Status | Tests | Kurzbeschreibung | Sicherheitsstatus |
|-------|-------|--------|-------|------------------|-------------------|
| 1 | Architektur | ✅ Fertig | – | Gesamtarchitektur, Datenfluss, Sicherheits-Gates definiert | Trennung Strategy/Risk/Execution festgelegt |
| 2 | Solution / Skeleton | ✅ Fertig | – | .NET-8-Solution, 9 Projekte, Referenzregeln (Abhängigkeiten nach innen) | Application kennt keine Execution/Infrastructure |
| 3 | Domain Models + Interfaces | ✅ Fertig | – | 10 Enums, 20+ Records, 14 Core-Interfaces; `decimal` für Geld, `DateTimeOffset` für Zeit | Signal ≠ Order strikt getrennt |
| 4 | Config / Profile System | ✅ Fertig | ✓ | JsonConfigService + Broker/Instrument/Fee-Provider + Validierung | Keine stillen Fehler, fehlende Profile werfen |
| 5 | Fee + PnL Engine | ✅ Fertig | ✓ | FeeCalculator + PnLCalculator; Gross/Net strikt getrennt | Keine hardcoded Fees/TickValues, `decimal`-genau |
| 6 | RiskManager | ✅ Fertig | ✓ | Fail-closed Gatekeeper: 15+ Prüfungen, RiskDecision mit Auslastung | Blockt KillSwitch / MaxDailyLoss / Disconnect / fehlende Profile; **keine Execution-Referenz** |
| 7 | OrderManager + PositionManager | ✅ Fertig | ✓ | Dedup/Idempotency, Lifecycle, OrderFactory (SL/TP/Bracket/BE/Trailing), Netting/PnL | Keine Order ohne Approved; Fills = einzige Wahrheit; PositionManager ohne Execution |
| 8A | MarketData | ✅ Fertig | ✓ | CSV-Reader, Replay-Provider, Heartbeat, Time/Tick/Volume/OrderFlow-Aggregation | Fail-closed Heartbeat; **keine Fake-Orderflow-Daten**; keine stillen CSV-Fehler |
| 8B | Backtest Engine | ⏳ Offen | – | Ticks durch dieselbe Pipeline (AsFastAsPossible), deterministisch | – |
| 9 | Paper Trading | ⏳ Offen | – | Gleiche Pipeline in RealTime, Feed-Status → SafetyMonitor | – |
| 10 | Dashboard | ⏳ Offen | – | Read-only Monitoring (kein Order-Button geplant in früher Phase) | – |
| 13/14 | Live Adapter / Safety Audit | ⏳ Offen | – | Echte Broker-Adapter + finaler Safety-Audit vor Live | Erst nach vollständigem Audit |

Legende: ✅ Fertig · ⏳ Offen · ✓ getestet · – nicht zutreffend

## Testabdeckung (Schwerpunkte)

| Bereich | Geprüft u. a. |
|---------|---------------|
| Config | Laden/Speichern, fehlende Datei, ungültiges JSON, Validierung |
| Fee/PnL | Long/Short, Verlust, Multi-Contract, NQ/MNQ, Slippage, Rundungsfreiheit |
| Risk | KillSwitch, MaxDailyLoss, MaxContracts, Session, Disconnect, fehlende Profile, ApprovedContracts |
| Order | Dedup, Risk-Ablehnung, Lifecycle, Partial Fill, Cancel/Replace, Rejected/Failed |
| Position | Average Entry, Teilverkauf, Flat, Flip, Unrealized/Realized, NetPnL nach Fees |
| MarketData | CSV-Validierung, Replay-Reihenfolge/Stop/Cancellation, Heartbeat, Aggregation, kein Fake-Orderflow |

## Sicherheits-Grundregeln (durchgehend umgesetzt)

- Strategie erzeugt **nur Signale**, niemals Orders.
- Ausführung nur über **RiskManager → OrderManager → BrokerExecutionAdapter**.
- Paper ist Standard; Live nur manuell (noch nicht implementiert).
- Keine Order ohne gültiges Broker-/Instrument-/Fee-Profil.
- Keine Order bei Feed-/Broker-Disconnect oder Positions-Mismatch.
- Kein doppeltes Signal → keine zweite Order (Idempotency-Key).
- Keine Fake-Orderflow-Daten ohne echte Bid/Ask/TradeDirection.
