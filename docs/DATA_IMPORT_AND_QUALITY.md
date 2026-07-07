# Data Import & Quality Layer (Phase 12B)

Read-only Doku. **Keine Broker-API, keine Live-Daten** — nur CSV-Import + Validierung.
Grundsatz: **Fehlende Daten werden ehrlich gemeldet (`InsufficientData`), niemals erfunden.**

## Unterstützte Datenformate

| Format | Pflichtfelder (logisch) | Ergebnis |
|---|---|---|
| **A — Minimal Tick** | timestamp, symbol, price, volume | `MarketTick` (OHLCV-only) |
| **B — Aggressor Tick** | A + tradedirection *oder* bidvolume/askvolume | `MarketTick` mit Aggressor |
| **C — Orderflow Bars** | bartimestamp, symbol, O/H/L/C, volume, bidvolume, askvolume (+delta, +cumulativedelta) | `OrderFlowBar` |
| **D — Footprint** | bartimestamp, symbol, pricelevel, bidvolumeatprice, askvolumeatprice (+total, +ratio, +stacked, +OHLC) | `FootprintBar` mit Levels |
| **E — Volume Profile** | sessiondate, symbol, pricelevel, volumeatprice (+bid/ask, +hvn/lvn) | `VolumeProfile` |

Importer: `AtasTickCsvImporter`, `AtasOrderFlowBarCsvImporter`, `AtasFootprintCsvImporter`,
`VolumeProfileCsvImporter` (`Infrastructure/MarketData/Import`). Jeder liefert ein
`ImportedMarketDataSet` mit Daten + `OrderFlowDataQualityReport` + `OrderFlowCapabilities`.

## Welches Datenlevel erlaubt welche Analyse?

| Analyse | braucht mindestens |
|---|---|
| OHLCV (Time/Tick/Volume-Bars, SMA …) | Format A |
| **Delta / CVD** | Format B (100 % klassifiziert!) oder C |
| **Absorption** (Bar-Ebene) | Format B/C (Bid/Ask + Delta) |
| **Bar-Imbalance** | Format B/C |
| **Stacked Imbalances** | Format D (Bid/Ask **je Preislevel**) |
| **HVN / LVN** | Format E (Volumen **je Preislevel** je Session) |

Das `OrderFlowCapabilities`-Objekt am Import-Ergebnis kodiert genau das — abgeleitet aus den
**tatsächlich vorhandenen** Daten. Teilklassifizierte Ticks (z. B. 95 % mit Aggressor) ergeben
**keine** Delta/CVD-Fähigkeit (alles-oder-nichts, sonst wären Delta-Werte verfälscht).

## ATAS-CSV-Mapping (keine hardcodierten Annahmen)

Echte ATAS-Spaltennamen sind versionsabhängig — deshalb ist das Mapping **Konfiguration**:

```csharp
var profile = new CsvImportProfile
{
    SourceType = MarketDataSourceType.AggressorTick,
    ColumnMap = new Dictionary<string, string>
    {
        ["timestamp"] = "Time", ["symbol"] = "Instrument",
        ["price"] = "Last", ["volume"] = "Qty", ["tradedirection"] = "Dir"
    }
};
var result = new AtasTickCsvImporter(profile).ImportFile("atas-export.csv");
```

`CsvImportProfile` ist ein einfaches Record → später als JSON via `IConfigService` ladbar.
Sobald der echte ATAS-Export vorliegt: Header anschauen, Profil-JSON schreiben, fertig —
kein Code-Change.

## Data-Quality-Checks

**Beim Import** (Zeile mit Error wird verworfen, `RowsRead` vs. `RowsAccepted` im Report):
fehlende/kaputte Timestamps · nicht-chronologische Daten (es wird NICHT still sortiert) ·
negative Preise/Volumina · doppelte Bar-Timestamps · `BidVolume+AskVolume == Volume` ·
`Delta == Ask−Bid` · Footprint-Level-Summen vs. Bar · fehlende Aggressor-Klassifikation (Warning).

**Nach dem Import** (`DataQualityChecks`): Symbol vs. InstrumentProfile (Error) ·
Preis-Ausrichtung auf TickSize (Warning) · Session-Plausibilität (Info) ·
Lücken-Erkennung (> 10× Median-Abstand → Warning).

Einzige Ableitung: fehlt `cumulativedelta`, wird CVD als laufende Summe der **echten**
Bar-Deltas berechnet — als Info-Issue dokumentiert. Fehlende OHLC im Footprint bleiben 0
(Warning); High/Low kommen aus der echten Preisspanne der Levels.

## Warum Datenqualität vor Optimierung kommt

Monte Carlo, Walk-Forward und Parameter-Optimierung **verstärken** jeden Datenfehler:
ein Optimizer findet zuverlässig genau die Parameter, die Datenlücken, falsche Deltas oder
erfundene Klassifikationen am besten ausnutzen — das Ergebnis sieht profitabel aus und ist
wertlos (Garbage in, optimiertes Garbage out). Ein Backtest auf geschätztem Orderflow testet
eine Strategie, die es in der Realität nicht gibt. Deshalb gilt die Reihenfolge:
**saubere, ehrlich klassifizierte Daten → Backtests → erst dann Research-Werkzeuge**
(Monte Carlo / Optimizer folgen in einer späteren Phase — bewusst noch nicht gebaut).
