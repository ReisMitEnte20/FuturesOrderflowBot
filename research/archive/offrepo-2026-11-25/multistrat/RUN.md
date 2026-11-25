# Mehrstrategie-Funktionsnachweis — Harness (gesichert)

Technischer Funktionsnachweis: mehrere Strategien laufen unabhaengig (getrennte Position/Kapital/Gebuehren/PnL)
ueber DIESELBE unveraenderliche Datenbasis (aufgenommene ATAS-Ticks -> OrderFlowBars). KEINE Profitabilitaetsaussage.

## Startbefehl
```
dotnet run -c Release --project "%USERPROFILE%\ATAS_OF_Recorder\multistrat\multistrat.csproj" -- "<pfad-zu-ticks.csv>"
```
Ohne Argument wird standardmaessig die 1x-Aufnahme MNQ_20261023_084204Z\ticks.csv genutzt.
Voraussetzung: Repo unter A:\Projects\FuturesOrderflowBot (ProjectReferences auf Infrastructure/Application/Core/Domain).
.NET 8 SDK genuegt.

## Konfiguration (im Code, ausdruecklich)
- ticksPerBar = 500
- Kapital = 10.000 USD je Strategie
- Fee = 0,37 USD / Kontrakt / Seite (ANNAHME; Round-Turn 0,74)
- Slippage = 1 Tick (0,25 Pkt) adverse je Fill
- Instrument MNQ: TickSize 0,25 / TickValue 0,50 / PointValue 2,00 (config/instruments/mnq.example.json)
- Strategien: Momentum(Price), DeltaFlow(OF, nutzt Delta=Ask-Bid, Schwelle 30), Fade(Price)

## Ausfuehrungsmodell
- Signal auf GESCHLOSSENER voller Bar i -> Ausfuehrung am OPEN von Bar i+1 (keine Rueckwirkung auf Signalpreis).
- Teilbar (Rest-Ticks) wird NICHT signalgebend genutzt; letzte volle Bar wird per EndOfData zum Close glattgestellt.
- 62.585 Ticks = 125 volle 500-Tick-Bars + 85 Rest-Ticks (Teilbar Index 125 ausgeschlossen).

## Datenfingerabdruck (SHA-256) der genutzten Rohaufnahmen (unveraendert)
- 1x   MNQ_20261023_084204Z\ticks.csv : 6A5842D0C38398C1B3069747AAF0A1848B21E8DE3512084E51066F7EC94E5478
       62.586 Zeilen (inkl. Header), 2.909.647 Bytes
- 100x MNQ_20261023_090001Z\ticks.csv : 41B6D92115B41DA1BD3EFAF7934269EE1C5E1A1232EF867D283DCA6A28F77F76
       71.667 Zeilen, 3.332.429 Bytes

## Ergebnisbericht
Siehe results.txt (Lauf vom 2026-10-23). Kernaussagen:
- Reproduzierbarkeit: Lauf 1 == Lauf 2 (exakt).
- Ledger-Isolation: jede Strategie ALLEIN == im kombinierten Lauf (exakt) -> keine Quer-Beeinflussung.
- Gegensaetzliche Trades real moeglich (z. B. Bar 0: Momentum/DeltaFlow LONG, Fade SHORT).
- PnL sind kleine Verluste (Gebuehren/Slippage/Rauschen) -> KEINE Edge-Aussage.

## Grenze (ausdruecklich)
Reproduzierbare Simulation auf aufgenommenen Daten + keine registrierten lokalen Drops.
Das beweist NICHT die vollstaendige Erfassung aller ATAS-/Anbieter-Ereignisse.
Natives bars.ndjson (Footprint) ist fehlerhaft und wird NICHT genutzt; DOM ist unvalidiert und wird NICHT genutzt.
