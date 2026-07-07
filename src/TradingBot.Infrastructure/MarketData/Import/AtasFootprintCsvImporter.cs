using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.MarketData.Import;

/// <summary>
/// Importiert Footprint-CSV (Format D): eine Zeile = ein Preislevel einer Bar; Zeilen werden
/// per bartimestamp zu <see cref="FootprintBar"/>s gruppiert.
/// Pflicht: bartimestamp, symbol, pricelevel, bidvolumeatprice, askvolumeatprice.
/// Optional: totalvolumeatprice, imbalanceratio, isstackedimbalance, open/high/low/close.
///
/// Konsistenz: totalvolumeatprice (falls vorhanden) muss Bid+Ask am Level entsprechen;
/// Bar-Summen ergeben sich aus den Levels. Fehlen OHLC-Spalten, bleiben Open/Close 0
/// (Warning) und High/Low werden aus den Levels abgeleitet (echte Preisspanne der Daten).
/// </summary>
public sealed class AtasFootprintCsvImporter
{
    private readonly CsvImportProfile _profile;

    public AtasFootprintCsvImporter(CsvImportProfile? profile = null)
        => _profile = profile ?? CsvImportProfile.Default(MarketDataSourceType.Footprint);

    public ImportedMarketDataSet ImportFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"CSV-Datei nicht gefunden: '{path}'.", path);
        using var reader = new StreamReader(path);
        return Import(reader);
    }

    public ImportedMarketDataSet Import(TextReader reader)
    {
        var grid = CsvGrid.Parse(reader, _profile.Delimiter);
        grid.RequireColumns(_profile, "bartimestamp", "symbol", "pricelevel", "bidvolumeatprice", "askvolumeatprice");

        bool hasOhlc = grid.HasColumn(_profile.Column("open")) && grid.HasColumn(_profile.Column("close"))
            && grid.HasColumn(_profile.Column("high")) && grid.HasColumn(_profile.Column("low"));

        var issues = new List<DataQualityIssue>();
        int rowsRead = 0, rowsAccepted = 0;
        string symbol = string.Empty;

        // Gruppierung nach Bar-Zeitstempel (Reihenfolge des Auftretens bleibt erhalten).
        var groups = new List<(DateTimeOffset Ts, List<FootprintPriceLevel> Levels,
            decimal? O, decimal? H, decimal? L, decimal? C, int FirstLine)>();

        foreach (var (fields, line) in grid.Rows)
        {
            rowsRead++;
            int errorsBefore = issues.Count(i => i.Severity == DataQualitySeverity.Error);

            var ts = grid.Time(fields, _profile, "bartimestamp", issues, line);
            var sym = grid.Raw(fields, _profile, "symbol");
            var price = grid.Dec(fields, _profile, "pricelevel", issues, line);
            var bid = grid.Dec(fields, _profile, "bidvolumeatprice", issues, line);
            var ask = grid.Dec(fields, _profile, "askvolumeatprice", issues, line);
            var total = grid.Dec(fields, _profile, "totalvolumeatprice", issues, line);
            var ratio = grid.Dec(fields, _profile, "imbalanceratio", issues, line);
            var stacked = grid.Bool(fields, _profile, "isstackedimbalance", issues, line);

            if (ts is null && issues.Count(i => i.Severity == DataQualitySeverity.Error) == errorsBefore)
                issues.Add(CsvGrid.Error("MissingTimestamp", "Bar-Zeitstempel fehlt.", line));
            if (string.IsNullOrWhiteSpace(sym))
                issues.Add(CsvGrid.Error("MissingSymbol", "Symbol fehlt.", line));
            if (price is <= 0m)
                issues.Add(CsvGrid.Error("NegativePrice", $"PriceLevel {price} muss > 0 sein.", line, ts));
            if (bid is < 0m || ask is < 0m || total is < 0m)
                issues.Add(CsvGrid.Error("NegativeVolume", "Level-Volumen darf nicht negativ sein.", line, ts));
            if (total is not null && bid is not null && ask is not null && bid.Value + ask.Value != total.Value)
                issues.Add(CsvGrid.Error("LevelSumMismatch",
                    $"totalvolumeatprice {total} ≠ Bid {bid} + Ask {ask} am Level {price}.", line, ts));

            if (issues.Count(i => i.Severity == DataQualitySeverity.Error) > errorsBefore
                || ts is null || sym is null || price is null || bid is null || ask is null)
                continue;

            symbol = sym;
            rowsAccepted++;

            var level = new FootprintPriceLevel
            {
                PriceLevel = price.Value,
                BidVolumeAtPrice = bid.Value,
                AskVolumeAtPrice = ask.Value,
                TotalVolumeAtPrice = total ?? bid.Value + ask.Value,
                ImbalanceRatio = ratio,
                IsStackedImbalance = stacked
            };

            var group = groups.Count > 0 && groups[^1].Ts == ts.Value ? groups[^1] : default;
            if (group.Levels is null)
            {
                group = (ts.Value, new List<FootprintPriceLevel>(),
                    hasOhlc ? grid.Dec(fields, _profile, "open", issues, line) : null,
                    hasOhlc ? grid.Dec(fields, _profile, "high", issues, line) : null,
                    hasOhlc ? grid.Dec(fields, _profile, "low", issues, line) : null,
                    hasOhlc ? grid.Dec(fields, _profile, "close", issues, line) : null,
                    line);
                groups.Add(group);
            }
            group.Levels.Add(level);
        }

        // Chronologie der Bars prüfen (Gruppen in Dateireihenfolge).
        for (int i = 1; i < groups.Count; i++)
            if (groups[i].Ts <= groups[i - 1].Ts)
                issues.Add(CsvGrid.Error("NonChronological",
                    $"Bar {groups[i].Ts:O} nicht chronologisch nach {groups[i - 1].Ts:O}.", groups[i].FirstLine));

        if (!hasOhlc && groups.Count > 0)
            issues.Add(CsvGrid.Warning("MissingOhlc",
                "Keine OHLC-Spalten – Open/Close bleiben 0; High/Low aus Preisleveln abgeleitet."));

        decimal runningCvd = 0m;
        var bars = new List<FootprintBar>();
        foreach (var g in groups)
        {
            decimal bidSum = g.Levels.Sum(l => l.BidVolumeAtPrice);
            decimal askSum = g.Levels.Sum(l => l.AskVolumeAtPrice);
            runningCvd += askSum - bidSum;

            bars.Add(new FootprintBar
            {
                Symbol = symbol,
                OpenTime = g.Ts, CloseTime = g.Ts,
                Open = g.O ?? 0m,
                High = g.H ?? g.Levels.Max(l => l.PriceLevel),
                Low = g.L ?? g.Levels.Min(l => l.PriceLevel),
                Close = g.C ?? 0m,
                TotalVolume = g.Levels.Sum(l => l.TotalVolumeAtPrice),
                BidVolume = bidSum,
                AskVolume = askSum,
                CumulativeDelta = runningCvd,
                Levels = g.Levels
            });
        }

        return new ImportedMarketDataSet
        {
            SourceType = MarketDataSourceType.Footprint,
            Symbol = symbol,
            FootprintBars = bars,
            Quality = new OrderFlowDataQualityReport
            {
                SourceType = MarketDataSourceType.Footprint,
                RowsRead = rowsRead, RowsAccepted = rowsAccepted, Issues = issues
            },
            Capabilities = bars.Count > 0
                ? new OrderFlowCapabilities
                {
                    SupportsDeltaCvd = true, SupportsAbsorption = true,
                    SupportsBarImbalance = true, SupportsStackedImbalances = true
                }
                : OrderFlowCapabilities.None
        };
    }
}
