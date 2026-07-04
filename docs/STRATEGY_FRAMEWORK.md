# Strategy Framework (Phase 11)

Read-only Doku. Es existieren **keine echten Profit-Strategien** — nur Infrastruktur +
Dummy-Strategien zum Testen.

## Was ist eine Strategy?

Eine Klasse, die `IStrategy` (Core) implementiert und Marktdaten-Events auswertet:
`OnTick(MarketTick)`, `OnCandle(Candle)`, `OnOrderFlowBar(OrderFlowBar)` — plus
`Initialize(StrategyExecutionContext)` und `Reset()`. Alle Handler haben Defaults
(kein Signal / no-op): eine Strategie implementiert nur, was sie laut
`StrategyConfig.RequiredDataType` (Tick/Candle/OrderFlow) braucht.

## Was darf eine Strategy?

- Marktdaten lesen (Tick/Candle/OrderFlowBar) und internen Zustand führen
- Kontext lesen (InstrumentProfile, StrategyConfig — read-only)
- **`TradeSignal` zurückgeben** (Richtung, Referenzpreis, Vorschlagswerte, Begründung) — oder `null`

## Was darf eine Strategy NIEMALS?

- Orders bauen oder senden (kein Zugriff auf OrderManager/Adapter — per Architektur unmöglich:
  Application-Strategien kennen keine Execution-Typen)
- Risk-Entscheidungen treffen oder umgehen
- Broker-/Netzwerk-Verbindungen öffnen
- Orderflow-Werte erfinden: ohne echte Bid/Ask/Aggressor-Daten kein Delta — die StrategyEngine
  blockt unklassifizierte OrderFlowBars fail-closed, bevor eine Strategie sie sieht

## TradeSignal vs. OrderRequest

| | TradeSignal | OrderRequest |
|---|---|---|
| Erzeugt von | Strategy | **nur** OrderManager |
| Bedeutet | „Ich sehe ein Setup" (Absicht) | konkrete Order (Symbol, Menge, Typ, Idempotency-Key) |
| Mengen/Stops | Vorschläge (`Suggested*`) | verbindlich, vom RiskManager begrenzt (`ApprovedContracts`) |
| Kann handeln? | Nein — reine Information | Ja — geht an den (simulierten) Execution-Adapter |

**Warum RiskManager/OrderManager getrennt bleiben:** Ein Bug in einer Strategie kann so nie
direkt Geld bewegen. Jedes Signal passiert erst das fail-closed Risk-Gate (Max Daily Loss,
Kill Switch, Profile, Session, Contracts …), dann baut der OrderManager daraus — dedupliziert
per Idempotency-Key — höchstens eine Order. Diese Kette ist in Backtest, Paper und später Live
identisch.

## Framework-Bausteine

- **`StrategyRegistry`** (`IStrategyRegistry`): registriert Strategien mit `StrategyConfig`,
  erzwingt eindeutige Namen, verwaltet Enable/Disable. **Deaktivierte Strategien werden von der
  Engine gar nicht erst aufgerufen** (Framework-Garantie).
- **`StrategyEngine`** (`IStrategyEngine`): verteilt Events deterministisch (Registrierungs-
  Reihenfolge) an aktive Strategien mit passendem Symbol + RequiredDataType, sammelt Signale
  (`CollectedSignals`), setzt `MaxSignalsPerSession` durch und ergänzt fehlende Vorschlagswerte
  aus der Config. Kein Order-/Broker-/Risk-Zugriff.
- **`CompositeStrategy`**: Brücke — präsentiert eine ganze StrategyEngine als EIN `IStrategy`
  für Backtest-/Paper-Engine (erstes Signal pro Event; Multi-Signal-Verarbeitung folgt später).
- **Dummy-Strategien** (`Application/Strategies`): `NoOpStrategy`, `TestSignalStrategy`,
  `MovingAverageDummyStrategy` (SMA-Cross, Candle), `OrderFlowTemplateStrategy` (leeres Template).
  Keine davon ist eine Profit-Strategie.

## Wie später eine echte Orderflow-Strategie eingebaut wird

1. `OrderFlowTemplateStrategy` kopieren, Namen vergeben, `RequiredDataType = OrderFlow` setzen.
2. In `OnOrderFlowBar` die Logik implementieren (bar.Delta, BidVolume/AskVolume,
   CumulativeDelta — z. B. Delta-Divergenz, Absorption, Stacked Imbalances, CVD-Bestätigung).
   Bei Setup ein `TradeSignal` zurückgeben — nie eine Order.
3. Parameter (Schwellen etc.) über `StrategyConfig.Parameters` konfigurieren — nichts hardcoden.
4. Über `StrategyRegistry.Register(strategy, config)` registrieren; per `CompositeStrategy`
   in Backtest/Paper fahren. RiskManager/OrderManager bleiben unverändert.
5. Erst im Backtest validieren, dann Paper — Live erst nach Phase 15/16 + Safety-Audit.
