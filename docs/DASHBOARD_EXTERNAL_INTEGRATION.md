# Externe Dashboard-Integration (HKUDS/Vibe-Trading)

Read-only Integrationsnotiz für das externe Dashboard.  
Das interne `TradingBot.DevDashboard` existiert nicht mehr.

## Zielbild

- UI-Modernisierung findet im externen Repo **HKUDS/Vibe-Trading** statt.
- Dieses Repository liefert dafür den read-only Datenvertrag aus der Paper-Pipeline.
- Keine Order-Buttons, keine Live-Execution-Steuerung, keine Broker-Controls.

## Gelieferter Datenvertrag

Quelle: `PaperTradingSession.GetDashboardSnapshot(int maxTicks = 1000)`

- Session-/Monitoring-Daten: Status, Symbol, TradingMode, TicksProcessed
- Position/PnL: aktuelle Position, Realized/Unrealized, Fees, Slippage
- Safety/Health: FeedHealthStatus, RiskStatus, KillSwitchActive
- Tick-Chart-Daten: `DashboardTickPoint` (Zeit, Preis, Bid/Ask, Volumen, Positionskontext)
- Overlay-Events: `DashboardPositionEvent`
  - Typen: `Entry`, `Add`, `Reduce`, `Exit`, `Flip`
  - inkl. SL/TP-Marker (`StopLossPrice`, `TakeProfitPrice`) sofern verfügbar

## Datenfluss

1. Replay-/Tick-Feed läuft durch die bestehende Paper-Pipeline.
2. Nach jedem verarbeiteten Tick wird ein read-only Tick-Chart-Punkt erzeugt.
3. Positionsänderungen erzeugen Overlay-Events (inkl. Partial-Änderungen).
4. Externe UI kann zyklisch Snapshots abrufen und direkt rendern.

## Funktionale Abnahme (Kernkriterien)

- Positionen sind im Chart pro Tick nachvollziehbar (Entry/Exit/Add/Reduce/Flip).
- Tick-Reihenfolge bleibt chronologisch konsistent.
- Statusanzeigen (Feed/Risk/KillSwitch/Session) entsprechen dem Sessionzustand.
- Snapshot bleibt read-only (`IsReadOnly=true`, `IsExecutionControlEnabled=false`).
- Aktualisierung ist bei Replay stabil; Tick-Buffer bleibt begrenzt.

## Qualität / Rollout

- End-to-end mit Replay-Daten im Test abgedeckt.
- Tick-Buffer-Größe über `PaperTradingConfiguration.DashboardTickBufferSize` konfigurierbar.
- UI-spezifische Modernisierung und Feinschliff erfolgen im externen Dashboard-Repo.
