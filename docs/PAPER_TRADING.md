# Paper Trading Engine (Phase 9)

Read-only Doku. Es gibt **keine Live-Execution** und **keine Broker-API** — Paper Trading ist
vollständig simuliert (`TradingBot.PaperTrading`).

## Backtest vs. Paper Trading

| | Backtest | Paper Trading |
|---|---|---|
| Ziel | Historische Auswertung, Kennzahlen | Pipeline-Probelauf „wie live", nur simuliert |
| Lauf | Einmal durch, `RunAsync` → Ergebnis | **Langlebige Session**: Start / Stop / Pause / Resume |
| Zustand | Erst am Ende (BacktestResult) | **Jederzeit abfragbar** (`GetState()`-Snapshot) |
| Geschwindigkeit | AsFastAsPossible | Replay in RealTime/Faster (oder Fast für Tests) |
| Uhr | Tick-Zeit (SimulatedClock) | Tick-Zeit (deterministisch mit Replay) |
| Fill-Semantik | `Application.Simulation.FillModel` | **dieselbe** `FillModel`-Klasse — Paper füllt nie „lockerer" |
| Pipeline | Strategy → Risk → Order → Position | **identisch** — gleiche Klassen, gleiche Gates |

Beide Engines unterscheiden sich nur in Lebenszyklus + Feed-Geschwindigkeit, **nicht** in der
Sicherheits- oder Fill-Logik.

## Warum der PaperExecutionAdapter keine Live-Verbindung hat

- Rein in-memory Order-Book; implementiert `IBrokerExecutionAdapter` ohne Netzwerk, SDKs oder Keys.
- Die PaperTrading-Assembly referenziert **keine** `System.Net.*`-Assemblies (per Test abgesichert).
- Market-Orders füllen erst am **nächsten** Tick (kein Lookahead), Limit/Stop nur bei Preisberührung,
  Slippage advers aus dem FeeProfile — keine perfekten Fills.
- Rejected Orders sind deterministisch simulierbar; Cancel/Replace unterstützt; Teilfüllungen vorbereitet.

## Safety-Checks, die im Paper Mode bereits greifen

Der **RiskManager läuft unverändert** — Paper ist nicht lockerer als später Live:

- Kill Switch aktiv → keine neue Order
- Feed/Broker disconnected (SafetyMonitor) → keine neue Order
- Fehlendes Instrument-/Fee-/Broker-Profil oder RiskConfig → fail-closed abgelehnt
- Max Daily Loss / Max Trades / Max Contracts / Verlustserie → Entry blockiert
- **Exit-aware**: Reduce/Close bleiben trotz Entry-Limits möglich (offene Position schließbar)
- Idempotency-Key → kein Doppelsignal zur Doppelorder
- Pause stoppt **neue Signale**; offene Orders können weiter füllen (wie in der Realität)
- Stop/CancellationToken beendet die Session sauber; Ergebnis + Journal bleiben erhalten

## Was erst später kommt (Live-spezifisch)

- Echter Broker-Adapter (Rithmic/CQG/Tradovate/…) hinter `IBrokerExecutionAdapter` (Phase 15/16)
- Echte Reconciliation lokale Position ↔ Broker-Position (im Paper ist lokal die einzige Wahrheit)
- Wanduhr-basierte Feed-Staleness (mit Replay ist die Uhr tick-gesteuert)
- Emergency Flatten mit echter Orderausführung + Kill-Switch-Integration
- Live-Mode-Aktivierung mit Safety-Audit (Phase 19/20) — bis dahin existiert kein Live-Pfad im Code

## Paper Trading Monitor (DevDashboard, Phase 10A)

Das read-only DevDashboard hat unter **`/paper`** einen Live-Monitor für Paper-Sessions.

**Dashboard starten:**

```powershell
dotnet run --project src\TradingBot.DevDashboard\TradingBot.DevDashboard.csproj
# oder mit Live-Reload:
dotnet watch --project src\TradingBot.DevDashboard\TradingBot.DevDashboard.csproj run
```

Dann im Browser die angezeigte URL öffnen (Standard `http://localhost:5034`) → „Paper Monitor".

**Paper-Demo starten:** Button **„Start Demo Paper Session"**. Die Demo spielt die lokale
Sample-CSV `samples/marketdata/demo-paper-session.csv` (120 Ticks, ~60 s) in Echtzeit ab —
mit der deterministischen `TestSignalStrategy` (keine Profit-Strategie) und Profilen aus `config/`.
Pause/Resume/Stop wirken ausschließlich auf diese lokale Simulation.
Der Monitor pollt den Session-State alle 500 ms (reiner Read-only-Snapshot).

**Was „PAPER SIMULATION ONLY" bedeutet:** Es existiert im gesamten Projekt kein Code-Pfad zu
einem echten Broker. Der `PaperExecutionAdapter` ist ein In-Memory-Simulator (keine Netzwerk-
Referenzen — per Test abgesichert), das Dashboard referenziert das Execution-Projekt nicht
(ebenfalls per Test abgesichert), und es gibt keine Buy/Sell-/Order-Buttons. Jede angezeigte
Order, jeder Fill und jeder PnL-Wert ist simuliert. Eine echte Broker-Verbindung entsteht erst
in Phase 15/16 hinter demselben Interface — nach separatem Safety-Audit.

## Nutzung (Beispiel)

```csharp
var engine = new PaperTradingEngine();
var session = engine.Start(new PaperTradingRequest
{
    MarketData = new CsvMarketDataProvider("ticks.csv", ReplayOptions.Realtime),
    Symbol = "NQ",
    Strategy = new TestSignalStrategy(),          // Dummy – keine Profit-Strategie
    Account = account, Instrument = nq, Fee = fees, Broker = broker, Risk = risk
});

var state = session.GetState();    // jederzeit: Position, PnL, Orders, Feed, RiskStatus
session.Pause(); session.Resume();
var result = await session.StopAsync();  // oder: await session.Completion (Datenende)
```
