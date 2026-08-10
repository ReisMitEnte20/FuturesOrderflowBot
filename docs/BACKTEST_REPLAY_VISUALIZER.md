# Backtest Replay Visualizer (Phase 12F)

Read-only Doku. **RESEARCH / SIMULATION ONLY** — keine Broker-API, keine Live-Execution, keine
echten Orders, keine Buy/Sell-Buttons, keine Netzwerkcalls. Route im DevDashboard: **`/replay`**.

## Was die Seite zeigt

Ein visuelles Backtest-Replay (Zeitraffer) über deterministische Demo-Daten:

- **Candlestick-Chart** mit gleitendem Fenster (letzte ~60 Bars bis zur aktuellen Replay-Position).
- **Replay-Steuerung:** Play / Pause / Reset, Speed-Auswahl (x1 / x10 / x100 / x1000) und ein
  Seek-Slider.
- **Trade-Marker:** Long-Entry (blaues Dreieck), Short-Entry (oranges Dreieck), Exit (graue Raute);
  bei offener Position werden **SL/TP als gestrichelte Linien** eingezeichnet und die offene
  Position hervorgehoben.
- **Info-Leiste:** aktuelle Replay-Zeit, aktueller Preis, Status (FLAT / LONG / SHORT),
  PnL (realisiert + unrealisiert), realisierter PnL, Anzahl geschlossener Trades.
- **Equity Curve** (realisiert, bis zum aktuellen Bar) als kleine Inline-SVG-Linie.
- **Trade Journal:** Tabelle aller Demo-Trades; **Klick auf eine Zeile springt zum Entry** im Replay
  (pausiert und setzt die Position).

## Datenbasis (Demo)

Alle Daten stammen aus `ReplayDemoService` — **deterministisch** (fester Seed), read-only, gecacht:

- Bars: deterministischer Random-Walk (OHLC + Volumen + Delta), 180 Ein-Minuten-Bars.
- Trades: ein fester Demo-Fahrplan (abwechselnd Long/Short, feste Haltedauer); Entry/Exit auf echten
  Bar-Close-Preisen; NetPnL aus Preisdifferenz × Demo-Multiplikator − Demo-Kosten.

> Es ist **keine echte Strategie-Performance** — die Trades illustrieren nur die Visualisierung.
> Kein Live-Feed, keine echten Orders, kein Broker.

## Warum das Dashboard keine Live-Trades senden kann

- `TradingBot.DevDashboard` referenziert **nicht** `TradingBot.Execution` (per Test abgesichert).
- Keine Broker-SDKs, keine API-Keys, keine Netzwerkcalls, keine Buy-/Sell-/Flatten-/Order-Buttons.
- Der `ReplayDemoService` ist read-only und rein lokal; die Replay-Steuerung bewegt nur den Zeiger
  über bereits vorhandene Demo-Daten.

## Starten

```powershell
dotnet run --project src\TradingBot.DevDashboard\TradingBot.DevDashboard.csproj
```

Dann im Browser **„Backtest Replay"** wählen (Route `/replay`). Weitere Seiten: `/` (Status),
`/paper` (Paper Monitor), `/research` (Research Dashboard).
