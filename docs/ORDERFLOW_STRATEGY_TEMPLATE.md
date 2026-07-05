# Orderflow Strategy Template (Phase 12)

⚠️ **Keine Gewinnversprechen. Keine Live-Execution. Keine Broker-API.**
Dieses Template ist Infrastruktur — die enthaltenen Check-Formeln sind dokumentierte
Platzhalter-Proxys, **keine profitable Strategie**.

## Was dieses Template ist

`OrderFlowSetupTemplateStrategy` (`src/TradingBot.Application/Strategies/OrderFlow/`) ist ein
professionelles Gerüst für echte Orderflow-Strategien:

- Läuft im bestehenden `IStrategy`/`StrategyEngine`-Framework (registrier-, aktivier-/deaktivierbar)
- Arbeitet **nur** auf echten `OrderFlowBar`-Daten (Bid/Ask/Aggressor-klassifiziert)
- Erzeugt ausschließlich `TradeSignal` — niemals Orders (RiskManager/OrderManager entscheiden)
- Vollständig über `StrategyConfig.Parameters` konfigurierbar, deterministisch testbar

**Signal-Logik:** Basis-Filter (`MinDelta`, `MinVolume`) und aktivierte Filter (VWAP-Distanz,
Session-High/Low-Nähe) müssen bestehen. Von den Confirmations müssen mindestens
`RequiredConfirmations` erfüllt sein. Qualifizieren beide Richtungen gleichzeitig → kein Signal.

## Modulare Checks (`OrderFlowConditionEvaluator`)

Jeder Check liefert `Met` / `NotMet` / **`InsufficientData`** — bei fehlender Datenbasis wird
**niemals geraten**:

| Check | Status | Datenbasis |
|---|---|---|
| DeltaDivergence | ✅ implementiert (Proxy) | neues Extrem + Delta läuft dagegen |
| Absorption | ✅ implementiert (Proxy) | hohes Volumen, kleine Range, Close hält |
| LiquiditySweep | ✅ implementiert (Proxy) | Session-Extrem gestoßen + zurückerobert |
| CvdConfirmation | ✅ implementiert | CumulativeDelta-Verlauf |
| VolumeSpike | ✅ implementiert | Volumen vs. Ø + Delta-Richtung |
| ReversalConfirmation | ✅ implementiert (Proxy) | Umkehr-Bar mit Delta |
| BreakoutConfirmation | ✅ implementiert (Proxy) | Close jenseits Extrem + Volumen + Delta |
| BarImbalance | ✅ implementiert | Ask/Bid-Verhältnis der **gesamten Bar** |
| VWAP Distance Filter | ✅ implementiert | bar-basierter VWAP (Näherung, kein Tick-VWAP) |
| Session High/Low Reversal | ✅ implementiert | Session-Extreme der laufenden Session |
| **StackedImbalances** | ⛔ `InsufficientData` | braucht **Footprint** (Bid/Ask je Preislevel) — OrderFlowBar hat nur Bar-Summen |
| **HVN/LVN Filter** | ⛔ `InsufficientData` | braucht **Volume Profile** (Volumen je Preislevel) |

Die beiden ⛔-Checks melden ehrlich fehlende Daten, statt Bar-Näherungen unter falschem Namen
zu liefern. Sie werden aktiviert, sobald Footprint-/Profile-Daten im Datenmodell existieren.

## Benötigte Daten (DataRequirements)

Die Strategie deklariert per `IStrategy.DataRequirements`: OrderFlowBars, BidAskVolume, Delta,
CumulativeDelta, VWAP (optional). **Ohne echte Bid/Ask-Klassifikation kein Signal** — doppelt
abgesichert: die StrategyEngine verteilt unklassifizierte Bars gar nicht erst, und die Strategie
prüft zusätzlich selbst.

**Warum echte Bid/Ask/Aggressor-Daten nötig sind:** Delta, Imbalances, Absorption und CVD
beschreiben, WER aggressiv gehandelt hat (Käufer hebt Ask vs. Verkäufer trifft Bid). Aus reinen
OHLCV-Daten ist das nicht rekonstruierbar — jede Schätzung wäre erfundener Orderflow und würde
Backtests systematisch verfälschen.

## Datenexport aus ATAS (oder anderem Orderflow-Tool)

Minimal für dieses Template (siehe `samples/marketdata/README.md`, Orderflow-CSV):

- `timestamp`, `symbol`, `price`, `volume` **plus**
- `tradedirection` (Aggressor: buy/sell) **oder** `bidvolume`/`askvolume` pro Trade

Damit baut der `OrderFlowBarAggregator` echte OrderFlowBars (Delta, CVD). Für die
⛔-Checks später zusätzlich: Footprint-Export (Bid/Ask je Preislevel je Bar) bzw.
Volume-Profile-Daten — dafür wird das Datenmodell in einer späteren Phase erweitert.

## Eigene Regeln eintragen

1. Check-Formeln in `OrderFlowConditionEvaluator` durch deine echten Regeln ersetzen/ergänzen
   (jeder Check ist eine kleine, einzeln testbare Methode).
2. Schwellen in die Config statt in den Code (siehe unten).
3. Filter-/Confirmation-Zuordnung in `OrderFlowSetupTemplateStrategy.Evaluate` anpassen.
4. Unit-Tests mit künstlichen, ECHT klassifizierten Bars schreiben (Vorlage:
   `tests/.../OrderFlowConditionEvaluatorTests.cs`).
5. Erst Backtest, dann Paper — Live erst nach Phase 15/16 + Safety-Audit.

## Beispiel-Config

```json
{
  "name": "MyOrderflowReversal",
  "symbol": "NQ",
  "enabled": true,
  "requiredDataType": "orderFlow",
  "suggestedContracts": 1,
  "stopLossTicks": 40,
  "takeProfitTicks": 60,
  "maxSignalsPerSession": 5,
  "parameters": {
    "MinDelta": "100",
    "MinVolume": "500",
    "ImbalanceRatio": "2.5",
    "AbsorptionThreshold": "2.0",
    "AbsorptionMaxRangeTicks": "8",
    "VolumeSpikeFactor": "2.0",
    "LookbackBars": "20",
    "RequiredConfirmations": "3",
    "CooldownBars": "10",
    "UseVwapFilter": "true",
    "MaxDistanceFromVwapTicks": "40",
    "UseSessionHighLowFilter": "true",
    "SessionHighLowProximityTicks": "20",
    "UseCvdConfirmation": "true"
  }
}
```

Alle Werte sind Demo-Defaults und **müssen** vor echtem Einsatz auf die eigenen Regeln
kalibriert werden. Signale erklären sich selbst: `Reason`, `TriggeredConditions`,
`FailedConditions`, `Confidence` (erfüllte/geprüfte Confirmations) und `DebugNotes`
(Checks ohne ausreichende Datenbasis) stehen am `TradeSignal`.
