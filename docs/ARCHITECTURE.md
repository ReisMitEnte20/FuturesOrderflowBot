# Architektur – FuturesOrderflowBot

Read-only Architekturübersicht (Stand Phase 8A). Die Diagramme sind in Mermaid;
GitHub rendert sie automatisch.

**Grundprinzip:** Einbahnstraße für Signale mit mehreren Sicherheits-Gates, bevor
irgendetwas ausgeführt wird. Backtest, Paper und Live unterscheiden sich **nur** in
Feed (`IMarketDataProvider`) und Adapter (`IBrokerExecutionAdapter`) – die Signal→Risk→Order-Kette
ist in allen Modi identisch.

## Diagramm 1: Gesamtarchitektur

```mermaid
flowchart LR
    MD[MarketData<br/>CSV / Replay] --> AGG[Aggregators<br/>Candle / OrderFlowBar]
    AGG --> STR[Strategy<br/>erzeugt nur Signale]
    STR -- TradeSignal --> RISK[RiskManager<br/>Gatekeeper]
    RISK -- RiskDecision --> OM[OrderManager]
    OM -- OrderRequest --> BA[BrokerExecutionAdapter<br/>Mock / spaeter Live]
    BA -- FillEvent --> PM[PositionManager]
    PM --> PNL[PnL / TradeJournal]

    HM[FeedHealthMonitor] -. Feed-Status .-> RISK
    KS[KillSwitch] -. blockt .-> RISK
    PM -. offene Kontrakte .-> RISK
```

## Diagramm 2: Safety Flow

```mermaid
flowchart TD
    S[TradeSignal] --> R{RiskManager.Evaluate}
    R -->|Approved = false| X[Keine Order<br/>Grund geloggt]
    R -->|Approved = true| Q[ApprovedContracts]
    Q --> O[OrderRequest erzeugen]

    R -. prueft .-> C1[KillSwitch aktiv?]
    R -. prueft .-> C2[Broker / Feed verbunden?]
    R -. prueft .-> C3[Profile vorhanden?]
    R -. prueft .-> C4[MaxDailyLoss / MaxContracts / Session?]
    R -. prueft .-> C5[Positions-Abgleich ok?]
```

## Diagramm 3: MarketData Flow

```mermaid
flowchart LR
    CSV[CSV / historische Ticks] --> RDR[CsvTickReader<br/>validiert + chronologisch]
    RDR --> RP[ReplayMarketDataProvider<br/>Speed-Modi, Stop/Cancel]
    RP -- MarketTick --> AGG[Aggregators]
    AGG --> TC[TimeCandle]
    AGG --> TB[TickBar]
    AGG --> VB[VolumeBar]
    AGG --> OF[OrderFlowBar<br/>nur mit echter Klassifikation]
    RP -- Tick empfangen --> HM[FeedHealthMonitor<br/>fail-closed Heartbeat]
```

## Diagramm 4: Order Flow

```mermaid
flowchart TD
    SIG[TradeSignal] --> DUP{Dedup<br/>IdempotencyKey}
    DUP -->|Duplikat| IGN[Ignoriert]
    DUP -->|neu| RC{Risk-Check}
    RC -->|abgelehnt| NO[Keine Order]
    RC -->|Approved| OF[OrderFactory.BuildEntry<br/>ApprovedContracts]
    OF --> SUB[SubmitOrderAsync<br/>Created -> Submitted -> Accepted]
    SUB --> FILL[FillEvent]
    FILL --> PM[PositionManager.ApplyFill<br/>Netting + PnL]
    SUB -->|Exception / Unknown| FAIL[Failed - fail-closed]
    SUB -->|Rejected| REJ[Rejected - keine Position]
```

## Wichtige Designregeln

- **Strategy** hat keinen Zugriff auf Broker/Order/Risk – nur Signal-Output.
- **RiskManager** entscheidet ausschließlich (kein Senden), hat keine Execution-Referenz.
- **OrderManager** ist der einzige, der Orders baut/sendet – nur bei `Approved`.
- **PositionManager** ist Single Source of Truth, ohne Execution-Referenz.
- **FillEvents** sind die einzige Wahrheit für gefüllte Menge/Position.
- **Kein Fake-Orderflow:** ohne echte Bid/Ask/TradeDirection kein Delta.
