# TradingBot.OrderflowRun

Lokaler **Repo-Runner** (Konsolen-Tool) für einen reproduzierbaren Orderflow-Mehrstrategielauf über den
**bestehenden** Tick-Backtester. Rein technischer Funktionsnachweis — **keine Profitabilitätsaussage**,
keine Orders, keine zweite Engine, keine Optimierung/Holdout.

## Ablauf

1. Lädt EINE ATAS-Replay-Aufnahme (`ticks.csv`) über den vorhandenen `AtasTickCsvImporter`
   (Import-Profil aus `config/import-profiles/atas-tick.example.json`). Abbruch, wenn die Ticks nicht
   voll klassifiziert sind (`SupportsDeltaCvd=false`) — es wird keine Aggressor-Seite erfunden.
2. Aggregiert zu **500-Tick-OrderFlowBars** (die 85 Rest-Ticks einer Teilbar erzeugen **keine** Signal-Bar).
3. Fährt die drei vorhandenen Teststrategien (`Momentum(Price)`, `DeltaFlow(OF)`, `Fade(Price)`) je als
   **eigenen** Lauf über `BacktestStrategyRunner` + `BacktestEngine` (getrennte Ledgers, deterministisch).
4. Schreibt EIN Artefakt + Manifest und einen read-only Lauf-Index für das Dashboard.

## Aufruf

```bash
dotnet run --project src/TradingBot.OrderflowRun [pfad/zu/ticks.csv] [repoRoot]
```

Ohne Argumente wird die zuletzt genutzte Aufnahme und der Repo-Root (anhand `TradingBot.sln`) verwendet.

## SinglePositionGate (max. 1 Kontrakt, kein Aufstocken)

Die inneren **Signalregeln bleiben unverändert**. Ein lokaler Wrapper reicht ein Signal nur weiter, wenn
sich die Position ändert: gleiche Richtung → halten (kein Add), Flat → schließen, Gegenrichtung →
durchreichen (die Engine schließt konservativ; Reopen per Folgesignal). Es gibt **keinen** globalen
OrderManager-Eingriff.

Der Gate-Zustand wird aus **Signalen** modelliert — ein Signal ist noch kein Fill. Abweichen kann er nur
bei einer **abgelehnten/ungefüllten** Order (der Tick-Pfad baut **keine** SL/TP-Brackets, also keine
unbemerkten SL/TP-Exits). Absicherung:

- **Trades/Position/PnL stammen immer aus der Engine**, nie aus dem Gate-Modell.
- Harte Obergrenze **`MaxContracts=1`** (`config/risk/research-sim.example.json`) garantiert max. 1 Kontrakt
  auch bei Divergenz (Backstop; im Normalfall nie ausgelöst, da der Gate Adds vermeidet → keine
  Ablehnungs-Pollution).
- Je Strategie zeigt **`gatePreconditionHeld`** (0 Ablehnungen), ob Gate-Zustand == Engine-Zustand war.

## Endergebnis (getrennt gespeichert/angezeigt)

`realizedNetPnL` (geschlossene Trades nach Fees) · `unrealizedGrossAtLastTick` (Mark-to-Market der offenen
Position zum **letzten vorhandenen Tick**) · `openPositionEntryFees` (nur gezahlte **Eintritts**-Fee, **keine**
hypothetische Exit-Fee) · `totalFeesPaid` · `endEquityRealized` und `endEquityMarkToMarket`.
Es wird **kein** künstlicher EndOfData-Fill erzeugt.

## Ausgaben (alle gitignored — nicht committen)

- `artifacts/orderflow/runs/<id>/workspace.json` + `manifest.json`
- `dashboard/public/orderflow-run.json` (zuletzt, Fallback) und
  `dashboard/public/orderflow-runs/<id>.json` + `index.json` (read-only Lauf-Liste)

Dashboard: **Research-Tab → Orderflow-Arbeitsfläche → Lauf im Dropdown**. Ältere Läufe bleiben unverändert
auswählbar; fehlgeschlagene/unlesbare Läufe und Alt-Manifeste ohne Status („Legacy/unbekannt") werden
gekennzeichnet (nicht als „ok").

## Konfiguration (ausschließlich aus `config/`)

Instrument `instruments/mnq.example.json` · Gebühren `fees/amp-rithmic-mnq.example.json` · Broker
`brokers/amp-rithmic.example.json` · Risk `risk/research-sim.example.json` (permissiv **nur für diesen
lokalen Lauf**, `MaxContracts=1`) · Import `import-profiles/atas-tick.example.json`. Die tatsächlich
verwendeten Einstellungen werden im Artefakt (`provenance.settingsUsed`) gespeichert.

## Grenzen

Technischer Test, keine Edge-/Profitabilitätsaussage · Preisgranularität 1-Punkt (MNQ real 0,25) ungeklärt
und **nicht** korrigiert · DOM/Orderbuch-Heatmap unvalidiert → „nicht verfügbar" · kleiner Einzelabschnitt,
kein OOS/Holdout. Dokumentierte Abweichungen zum ursprünglichen Harness (volles FeeProfile statt pauschal
0,37/Seite; kein EndOfData-Zwangsschluss → offene Restposition als unrealisiert sichtbar, nicht als Trade;
Fill/Slippage/Flip aus FillModel/OrderManager) — PnL bewusst **nicht** identisch erzwungen.
