# samples/sierra/ — Ablage für echte Sierra-Chart-Exporte (Haupt-Quelle)

**Status: noch KEINE echten Sierra-Exporte vorhanden.** Sierra Chart ist die **bevorzugte
kurzfristige Market-Data-Quelle** des Projekts (bessere/kontrollierbare Intraday-/Tick-Exporte
als ATAS). Ablage der CSVs: **`samples/sierra/raw/`**.

## Was exportieren

Sierra Chart → Intraday Data File als **Text/CSV** exportieren. Erwartete Spalten:

```
Date, Time, Open, High, Low, Last, Volume, NumberOfTrades, BidVolume, AskVolume
```

- **Date/Time sind UTC** (laut Sierra-Doku) → werden zu einem UTC-Timestamp kombiniert.
- **1-Tick-Export bevorzugt** (siehe unten). Bei 1-Tick gilt: `Open=Last=Trade Price`,
  `High=Ask`, `Low=Bid`, `Volume=Trade Volume`.

## 1-Tick vs. aggregiert — wichtig

Die tatsächliche Granularität hängt vom Data/Trading-Service und der **Intraday Data Storage
Time Unit** ab. Records können 1 Trade bis 1 Minute sein.

- **1-Tick-Export** (`NumberOfTrades == 1` pro Zeile) → geeignet für **ernsthafte
  Orderflow-Forschung** (Delta/CVD über BidVolume/AskVolume, High/Low = Ask/Bid).
- **Aggregierte Records** (`NumberOfTrades > 1` oder Spalte fehlt) → **nur eingeschränkt**
  geeignet; der Importer behauptet dann **keine** Tick-Garantie und interpretiert High/Low
  **nicht** als Ask/Bid (Warning `SierraAggregatedRecords`).

## Sicherheits-/Datenschutzregeln

- **Keine Accountdaten, keine API-Keys, keine privaten Daten** in den Dateien.
- Nur reine Markt-/Orderflow-Daten.
- Erst **kleine Samples**: Header + erste **5–20 Zeilen** reichen fürs Mapping.

## Ablauf

1. Sierra 1-Tick-Export erzeugen (kleines Sample).
2. Unter `raw/` ablegen.
3. Mir **Header + erste ~10 Zeilen** schicken → ich passe
   `config/import-profiles/sierra-intraday.example.json` an den echten Header an.
4. Danach Import + Datenqualitätsprüfung.

Details: [../../docs/MARKET_DATA_SOURCE_GUIDE.md](../../docs/MARKET_DATA_SOURCE_GUIDE.md),
[../../docs/ATAS_EXPORT_GUIDE.md](../../docs/ATAS_EXPORT_GUIDE.md).

> Ohne echte, klassifizierte Daten meldet das System `InsufficientData` — **kein** Fake-Orderflow.
