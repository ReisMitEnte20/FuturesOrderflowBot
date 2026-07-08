# ATAS Export Guide (Phase 12E-A)

Anleitung, welche CSV-Exporte aus ATAS gebraucht werden und wie sie später sauber ins Projekt
kommen. **Vorbereitung — es sind noch KEINE echten ATAS-Exporte im Projekt.**

> Keine Broker-API, keine Live-Execution, keine Netzwerkcalls. Fehlt ein Datenlevel, meldet das
> System ehrlich `InsufficientData` und erzeugt **niemals** Fake-Orderflow-Daten.

## Projektziel zuerst

Ziel ist eine echte **Orderflow-/Quant-Edge**, die **nach Fees, Slippage, Drawdown und Risiko**
besser ist als passiv den S&P 500 (ES) zu halten — siehe
[MARKET_DATA_SOURCE_GUIDE.md](MARKET_DATA_SOURCE_GUIDE.md).

> **Simple OHLCV strategies are useful for infrastructure validation, but not sufficient for the
> project's main edge goal.** OHLCV reicht nur für einfache Basis-Backtests; für die eigentliche
> Edge brauchen wir echte Orderflow-Daten (Aggressor/Bid-Ask, Footprint, Volume-at-Price,
> optional Level 2 / DOM).

## Kurzüberblick: Welches Datenlevel wofür?

| Datenlevel | ATAS-Export | Erlaubt |
|---|---|---|
| **OHLCV** | Tick/Trade (nur Zeit/Preis/Volumen) | Candle-/Volume-/Tick-Bars, SMA … |
| **Aggressor / Bid-Ask** | Tick/Trade mit AggressorSide, **oder** Orderflow-Bars | **Delta, CVD, Absorption, Bar-Imbalance** |
| **Footprint (je Preislevel)** | Footprint-Export | **Stacked Imbalances** |
| **Volume Profile (je Session)** | Volume-Profile-Export | **HVN / LVN** |

Merksätze:
- **OHLCV reicht nur** für einfache Basis-Backtests / Infrastruktur-Validierung — **nicht** für das
  eigentliche Edge-Ziel.
- **Aggressor/Bid-Ask ist nötig** für Delta/CVD/Absorption.
- **Footprint-Preislevels sind nötig** für Stacked Imbalances.
- **Volume Profile ist nötig** für HVN/LVN.
- **Teilklassifiziert = nicht klassifiziert:** sind z. B. nur 95 % der Ticks mit Aggressor
  versehen, gibt es KEINE Delta/CVD-Fähigkeit (alles-oder-nichts, sonst wären Deltas verfälscht).

## Benötigte Exporte & ideale Spalten

Logische Feldnamen (links) sind fest; die realen ATAS-Spaltennamen mappst du später in den
`config/import-profiles/atas-*.example.json`. „Pflicht" = ohne diese Spalten kein Import.

### 1) Tick / Trade Export
- **Pflicht:** `timestamp`, `price`, `volume`, `symbol`
- **Für Orderflow:** `AggressorSide` / `TradeDirection` / `BidAsk` (Buy/Sell bzw. bid/ask),
  **oder** `bidvolume` + `askvolume`
- Ohne Aggressor/Bid-Ask → nur OHLCV.

### 2) Orderflow Bar Export
- **Pflicht:** `BarTimestamp`, `Open`, `High`, `Low`, `Close`, `Volume`, `BidVolume`,
  `AskVolume`, `Delta`, `symbol`
- **Optional:** `CumulativeDelta` (fehlt sie, wird CVD als laufende Summe der echten Bar-Deltas
  abgeleitet und als Info gekennzeichnet)

### 3) Footprint Export
- **Pflicht:** `BarTimestamp`, `PriceLevel`, `BidVolumeAtPrice`, `AskVolumeAtPrice`, `symbol`
- **Optional:** `DeltaAtPrice`, `VolumeAtPrice`, `ImbalanceRatio`, `StackedImbalance`, O/H/L/C
- Nötig für **Stacked Imbalances** (Bid/Ask je Preislevel).

### 4) Volume Profile Export
- **Pflicht:** `SessionTimestamp` / `TradingDate`, `PriceLevel`, `VolumeAtPrice`, `symbol`
- **Optional:** `BidVolumeAtPrice`, `AskVolumeAtPrice`, HVN/LVN-Marker
- Nötig für **HVN / LVN** (Volumen je Preislevel je Session).

## Format-Hinweise

- **Timestamp:** ISO-8601 bevorzugt (`2026-07-08T13:30:00Z`), konsistente Zeitzone.
- **Dezimaltrennzeichen `.`** (Punkt), Feldtrenner `,` (Komma). Anderer Trenner? → im Profil
  `delimiter` setzen.
- **Chronologisch** exportieren — das System sortiert NICHT still um, sondern meldet
  nicht-chronologische Zeilen als Fehler.
- Konsistenz-Regeln, die geprüft werden: `BidVolume + AskVolume == Volume`, `Delta == Ask − Bid`,
  Footprint-Level-Summen vs. Bar. Verletzungen → Data-Quality-Issue (Zeile ggf. verworfen).

## Dateien ins Projekt legen

1. Kleine Datei unter **`samples/atas/raw/`** ablegen (siehe
   [samples/atas/README.md](../samples/atas/README.md)).
2. **Keine** Accountdaten / API-Keys / privaten Daten in den CSVs.
3. Erst **kleine Samples** (Header + 5–20 Zeilen) für das Mapping.

## Was du mir danach schicken sollst

- Pro Exportart: **die Kopfzeile (Header) + die ersten ~10 Datenzeilen**.
- Welche Exportart es ist (Tick / Orderflow-Bar / Footprint / Volume-Profile).
- Zeitzone der Timestamps und Instrument (z. B. NQ/MNQ).

Damit passe ich das passende `config/import-profiles/atas-*.example.json` an deinen echten
Header an (nur Mapping-Konfiguration, **kein** Code-Change). Erst danach: Import +
Datenqualitätsprüfung, dann Research.

## Beispiel-Mapping-Profile (Vorlagen)

Unter [config/import-profiles/](../config/import-profiles) liegen vier **Beispiel**-Profile.
Sie sind Platzhalter — **keine garantierten ATAS-Spaltennamen**:

- `atas-tick.example.json`
- `atas-orderflow-bar.example.json`
- `atas-footprint.example.json`
- `atas-volume-profile.example.json`

Struktur: `sourceType`, `delimiter`, `note`, `columnMap` (logisches Feld → CSV-Spaltenname).
Die realen Werte trägst/schickst du, sobald der echte Header vorliegt.
