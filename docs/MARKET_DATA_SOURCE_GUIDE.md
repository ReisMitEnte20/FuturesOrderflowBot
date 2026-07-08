# Market Data Source Guide

Welche Marktdaten das Projekt braucht — ausgerichtet am **eigentlichen Projektziel**, nicht an
einfachen Candle-Strategien.

> Keine Broker-API, keine Live-Execution, keine Netzwerkcalls. Fehlen echte Orderflow-Daten,
> meldet das System `InsufficientData` / fehlende Capabilities und erzeugt **niemals**
> Fake-Orderflow-Signale.

## Projektziel (Edge-Goal)

Langfristig eine echte **Orderflow-/Quant-Edge** entwickeln, die **nach Fees, Slippage, Drawdown
und Risiko** sinnvoller ist als passiv den **S&P 500 (ES) buy-and-hold** zu halten.

> **Simple OHLCV strategies are useful for infrastructure validation, but not sufficient for the project's main edge goal.**

## Bevorzugte Quelle: Sierra Chart

**Sierra Chart ist die bevorzugte kurzfristige Market-Data-Quelle** — es exportiert Intraday Data
Files als Text/CSV mit echter Orderflow-Info und (bei passender Einstellung) **Tick-Granularität**:

```
Date, Time, Open, High, Low, Last, Volume, NumberOfTrades, BidVolume, AskVolume
```

- **Date/Time = UTC** (laut Sierra-Doku) → werden zu einem UTC-Timestamp kombiniert.
- **1-Tick-Export:** `Open=Last=Trade Price`, `High=Ask`, `Low=Bid`, `Volume=Trade Volume`.
- **Granularität variiert:** hängt vom Data/Trading-Service und der **Intraday Data Storage Time
  Unit** ab (1 Trade … 1 Minute pro Record). Deshalb:
  - **1-Tick-Export** (`NumberOfTrades == 1`) → geeignet für ernsthafte Orderflow-Forschung.
  - **aggregierte Records** → nur eingeschränkt geeignet; **keine** Tick-Garantie.
- **Nächster konkreter Schritt:** einen **Sierra 1-Tick-Export testen** (kleines Sample) und das
  Mapping [`sierra-intraday.example.json`](../config/import-profiles/sierra-intraday.example.json)
  am echten Header kalibrieren. Ablage: [../samples/sierra/README.md](../samples/sierra/README.md).
- Sierra-Doku: HistoricalIntradayData & ImportExport (sierrachart.com).

**ATAS** bleibt nützlich als **Analyse-/Charting-Tool**, ist aber **keine garantierte Exportquelle**
für unser Import-Format (Spaltennamen/Verfügbarkeit versionsabhängig). ATAS-Import nur, falls
passende CSVs möglich sind — Haupttest ist jetzt der Sierra-Export.

## Datenlevel: was reicht wofür?

| Level | Daten | Reicht für | Für Edge-Ziel? |
|---|---|---|---|
| **OHLCV** | Timestamp, Price, Volume | einfache Basis-Backtests, Infrastruktur-Validierung | ❌ nur Validierung, **nicht** das Hauptziel |
| **Aggressor / Bid-Ask** | + AggressorSide/TradeDirection **oder** BidVolume/AskVolume | Delta, CVD, Absorption, Bar-Imbalance | ✅ Basis der Edge |
| **Footprint** | Bid/Ask **je Preislevel** je Bar | Stacked Imbalances | ✅ |
| **Volume Profile** | Volume-at-Price **je Session** | HVN / LVN | ✅ |
| **Level 2 / DOM** (optional) | Orderbuch-Tiefe | Liquidity Research | ✅ optional/später |

## Was wir für das Edge-Ziel wirklich brauchen

- Tick/Trade-Daten mit **Timestamp, Price, Volume**
- besser: **AggressorSide / BidAsk / TradeDirection**
- **BidVolume / AskVolume**
- **Footprint** Price-Level Bid/Ask
- **Volume-at-Price** für HVN/LVN
- optional **Level 2 / DOM** für Liquidity Research

Export-Details & Spalten: siehe [ATAS_EXPORT_GUIDE.md](ATAS_EXPORT_GUIDE.md). Ablage der echten
CSVs: [../samples/atas/README.md](../samples/atas/README.md). Mapping-Vorlagen:
[../config/import-profiles/](../config/import-profiles).

## Benchmarking gegen S&P 500 / ES buy-and-hold

Eine Strategie ist nur dann interessant, wenn sie **risikoadjustiert nach Kosten** besser ist als
passives Halten. Später zu vergleichen (geplant, noch nicht gebaut):

- **NetPnL nach Fees/Slippage** (brutto zählt nicht)
- **Max Drawdown**
- **Sharpe / Sortino** (oder vergleichbare risk-adjusted Metrics)
- **MAR / Return-to-Drawdown**
- **Stability über Walk-Forward** (IS vs. OOS)
- **Monte-Carlo-Worst-Case**
- **Out-of-Sample-Performance**
- **Time-in-market / Capital Efficiency** (eine Edge, die selten im Markt ist, bindet weniger
  Kapital/Risiko als dauerhaftes Halten)

## Robustheits-Regel

Sieht eine Strategie **nur brutto** gut aus, ist aber **nach Kosten, Slippage oder Out-of-Sample**
schlecht, zählt sie als **nicht robust** — unabhängig vom In-Sample-Gewinn. Details:
[RESEARCH_ANALYTICS.md](RESEARCH_ANALYTICS.md) und [RESEARCH_DASHBOARD.md](RESEARCH_DASHBOARD.md).
