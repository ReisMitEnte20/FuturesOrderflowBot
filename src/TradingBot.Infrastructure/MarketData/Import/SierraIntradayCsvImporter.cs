using System.Globalization;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.MarketData.Import;

/// <summary>
/// Importiert Sierra-Chart-Intraday-Text/CSV-Exporte
/// (<c>Date, Time, Open, High, Low, Last, Volume, NumberOfTrades, BidVolume, AskVolume</c>).
/// Date/Time sind laut Sierra-Doku UTC und werden zu EINEM UTC-Timestamp kombiniert.
///
/// Ehrlichkeit über die Granularität (hängt vom Data-Service + Intraday Storage Time Unit ab):
/// - <b>1-Tick-Export</b> (jede Zeile <c>NumberOfTrades == 1</c>): geeignet für ernsthafte
///   Orderflow-Forschung. Dann gilt Sierras Tick-Konvention <c>High = Ask</c>, <c>Low = Bid</c>,
///   <c>Last = Trade Price</c> → Bid/Ask-Preise werden übernommen.
/// - <b>Aggregierte Records</b> (NumberOfTrades > 1 oder Spalte fehlt): KEINE Tick-Garantie;
///   High/Low sind Bar-Extreme, NICHT Ask/Bid → werden NICHT als Quote interpretiert (Warning).
///
/// Delta/CVD-Capability nur, wenn BidVolume/AskVolume vorhanden UND alle Zeilen klassifiziert sind
/// (alles-oder-nichts). Fehlt die Klassifikation → keine Fake-Orderflow-Werte.
/// Pflicht: date, time, last, volume. Sierra-CSV hat kein Symbol → wird als Parameter übergeben.
/// </summary>
public sealed class SierraIntradayCsvImporter
{
    private readonly CsvImportProfile _profile;

    public SierraIntradayCsvImporter(CsvImportProfile? profile = null)
        => _profile = profile ?? CsvImportProfile.Default(MarketDataSourceType.AggressorTick);

    public ImportedMarketDataSet ImportFile(string path, string symbol)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"CSV-Datei nicht gefunden: '{path}'.", path);
        using var reader = new StreamReader(path);
        return Import(reader, symbol);
    }

    public ImportedMarketDataSet Import(TextReader reader, string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("Symbol muss angegeben werden (Sierra-CSV hat keine Symbol-Spalte).", nameof(symbol));

        var grid = CsvGrid.Parse(reader, _profile.Delimiter);
        grid.RequireColumns(_profile, "date", "time", "last", "volume");

        bool hasBidAsk = grid.HasColumn(_profile.Column("bidvolume")) && grid.HasColumn(_profile.Column("askvolume"));
        bool hasNumTrades = grid.HasColumn(_profile.Column("numberoftrades"));
        bool hasHighLow = grid.HasColumn(_profile.Column("high")) && grid.HasColumn(_profile.Column("low"));

        var issues = new List<DataQualityIssue>();
        var rows = new List<(DateTimeOffset Ts, decimal Price, decimal Volume, decimal Bid, decimal Ask,
            decimal BidVol, decimal AskVol, bool SingleTrade)>();
        int rowsRead = 0, unclassified = 0;
        bool allSingleTrade = hasNumTrades;   // ohne NumberOfTrades keine Tick-Garantie
        DateTimeOffset? previous = null;

        foreach (var (fields, line) in grid.Rows)
        {
            rowsRead++;
            int errorsBefore = issues.Count(i => i.Severity == DataQualitySeverity.Error);

            var ts = CombineTimestamp(grid, fields, issues, line);
            var price = grid.Dec(fields, _profile, "last", issues, line);
            var volume = grid.Dec(fields, _profile, "volume", issues, line);

            if (price is <= 0m) issues.Add(CsvGrid.Error("NegativePrice", $"Preis {price} muss > 0 sein.", line, ts));
            if (volume is < 0m) issues.Add(CsvGrid.Error("NegativeVolume", $"Volumen {volume} darf nicht negativ sein.", line, ts));
            if (ts is not null && previous is not null && ts < previous)
                issues.Add(CsvGrid.Error("NonChronological", $"Zeitstempel {ts:O} liegt vor Vorgänger {previous:O}.", line, ts));

            if (issues.Count(i => i.Severity == DataQualitySeverity.Error) > errorsBefore
                || ts is null || price is null || volume is null)
                continue; // Zeile verworfen

            decimal numTrades = hasNumTrades ? grid.Dec(fields, _profile, "numberoftrades", issues, line) ?? 0m : 0m;
            bool singleTrade = hasNumTrades && numTrades == 1m;
            if (!singleTrade) allSingleTrade = false;

            decimal bidVol = hasBidAsk ? grid.Dec(fields, _profile, "bidvolume", issues, line) ?? 0m : 0m;
            decimal askVol = hasBidAsk ? grid.Dec(fields, _profile, "askvolume", issues, line) ?? 0m : 0m;
            decimal high = hasHighLow ? grid.Dec(fields, _profile, "high", issues, line) ?? 0m : 0m;
            decimal low = hasHighLow ? grid.Dec(fields, _profile, "low", issues, line) ?? 0m : 0m;

            previous = ts;
            rows.Add((ts.Value, price.Value, volume.Value, low, high, bidVol, askVol, singleTrade));
        }

        // Granularität steht erst nach dem Lesen aller Zeilen fest (allSingleTrade).
        bool singleTickExport = allSingleTrade && rows.Count > 0;

        var ticks = new List<MarketTick>(rows.Count);
        foreach (var r in rows)
        {
            var aggressor = Classify(hasBidAsk, r.BidVol, r.AskVol);
            if (hasBidAsk && aggressor == AggressorSide.Unknown) unclassified++;

            ticks.Add(new MarketTick
            {
                Symbol = symbol,
                Timestamp = r.Ts,
                Price = r.Price,
                Volume = r.Volume,
                Aggressor = aggressor,
                // Sierra 1-Tick: High = Ask, Low = Bid. NUR im echten Tick-Export übernehmen.
                Ask = singleTickExport ? r.Ask : 0m,
                Bid = singleTickExport ? r.Bid : 0m,
                AskSize = hasBidAsk ? r.AskVol : 0m,
                BidSize = hasBidAsk ? r.BidVol : 0m
            });
        }

        // Ehrlichkeits-Hinweise zur Granularität.
        if (singleTickExport)
            issues.Add(CsvGrid.Info("SierraSingleTick",
                "1-Tick-Export erkannt (NumberOfTrades == 1): High=Ask, Low=Bid; geeignet für Orderflow-Forschung."));
        else
            issues.Add(CsvGrid.Warning("SierraAggregatedRecords",
                "Records sind aggregiert oder Granularität unbekannt (NumberOfTrades > 1 bzw. Spalte fehlt) – " +
                "KEINE Tick-Garantie; High/Low werden NICHT als Ask/Bid interpretiert."));

        if (hasBidAsk && ticks.Count > 0 && unclassified > 0)
            issues.Add(CsvGrid.Warning("PartialClassification",
                $"{unclassified} von {ticks.Count} Records ohne eindeutige Bid/Ask-Klassifikation – " +
                "Delta/CVD mit diesem Datensatz NICHT erlaubt."));

        bool fullyClassified = hasBidAsk && ticks.Count > 0 && unclassified == 0;
        var sourceType = fullyClassified ? MarketDataSourceType.AggressorTick : MarketDataSourceType.MinimalTick;

        return new ImportedMarketDataSet
        {
            SourceType = sourceType,
            Symbol = symbol,
            Ticks = ticks,
            Quality = new OrderFlowDataQualityReport
            {
                SourceType = sourceType, RowsRead = rowsRead, RowsAccepted = ticks.Count, Issues = issues
            },
            Capabilities = fullyClassified
                ? new OrderFlowCapabilities
                {
                    SupportsDeltaCvd = true, SupportsAbsorption = true, SupportsBarImbalance = true
                }
                : OrderFlowCapabilities.None
        };
    }

    /// <summary>Kombiniert die getrennten Date-/Time-Spalten zu einem UTC-Timestamp (Sierra: UTC).</summary>
    private DateTimeOffset? CombineTimestamp(CsvGrid grid, string[] fields, List<DataQualityIssue> issues, int line)
    {
        var date = grid.Raw(fields, _profile, "date");
        var time = grid.Raw(fields, _profile, "time");
        if (date is null || time is null)
        {
            issues.Add(CsvGrid.Error("MissingTimestamp", "Date und/oder Time fehlen.", line));
            return null;
        }
        if (DateTimeOffset.TryParse($"{date} {time}", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var ts))
            return ts.ToUniversalTime();

        issues.Add(CsvGrid.Error("InvalidTimestamp", $"Ungültige Date/Time-Kombination '{date} {time}'.", line));
        return null;
    }

    private static AggressorSide Classify(bool hasBidAsk, decimal bidVol, decimal askVol)
    {
        if (!hasBidAsk) return AggressorSide.Unknown;
        if (askVol > 0m && bidVol == 0m) return AggressorSide.Buy;   // am Ask gehandelt
        if (bidVol > 0m && askVol == 0m) return AggressorSide.Sell;  // am Bid gehandelt
        return AggressorSide.Unknown;                                // beide 0 oder beide > 0 (aggregiert)
    }
}
