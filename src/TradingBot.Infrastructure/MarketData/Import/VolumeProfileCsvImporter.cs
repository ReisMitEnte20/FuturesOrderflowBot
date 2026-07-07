using System.Globalization;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.MarketData.Import;

/// <summary>
/// Importiert Volume-Profile-CSV (Format E): eine Zeile = ein Preislevel einer Session;
/// Zeilen werden per sessiondate zu <see cref="VolumeProfile"/>s gruppiert.
/// Pflicht: sessiondate, symbol, pricelevel, volumeatprice.
/// Optional: bidvolumeatprice, askvolumeatprice, hvn, lvn (Klassifikation des Lieferanten).
/// </summary>
public sealed class VolumeProfileCsvImporter
{
    private readonly CsvImportProfile _profile;

    public VolumeProfileCsvImporter(CsvImportProfile? profile = null)
        => _profile = profile ?? CsvImportProfile.Default(MarketDataSourceType.VolumeProfile);

    public ImportedMarketDataSet ImportFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"CSV-Datei nicht gefunden: '{path}'.", path);
        using var reader = new StreamReader(path);
        return Import(reader);
    }

    public ImportedMarketDataSet Import(TextReader reader)
    {
        var grid = CsvGrid.Parse(reader, _profile.Delimiter);
        grid.RequireColumns(_profile, "sessiondate", "symbol", "pricelevel", "volumeatprice");

        var issues = new List<DataQualityIssue>();
        int rowsRead = 0, rowsAccepted = 0;
        string symbol = string.Empty;
        var bySession = new Dictionary<DateOnly, List<VolumeProfileLevel>>();

        foreach (var (fields, line) in grid.Rows)
        {
            rowsRead++;
            int errorsBefore = issues.Count(i => i.Severity == DataQualitySeverity.Error);

            var rawDate = grid.Raw(fields, _profile, "sessiondate");
            DateOnly? date = null;
            if (rawDate is null)
                issues.Add(CsvGrid.Error("MissingSessionDate", "SessionDate fehlt.", line));
            else if (DateOnly.TryParse(rawDate, CultureInfo.InvariantCulture, out var d))
                date = d;
            else
                issues.Add(CsvGrid.Error("InvalidSessionDate", $"Ungültiges SessionDate '{rawDate}'.", line));

            var sym = grid.Raw(fields, _profile, "symbol");
            var price = grid.Dec(fields, _profile, "pricelevel", issues, line);
            var volume = grid.Dec(fields, _profile, "volumeatprice", issues, line);
            var bid = grid.Dec(fields, _profile, "bidvolumeatprice", issues, line);
            var ask = grid.Dec(fields, _profile, "askvolumeatprice", issues, line);
            var hvn = grid.Bool(fields, _profile, "hvn", issues, line);
            var lvn = grid.Bool(fields, _profile, "lvn", issues, line);

            if (string.IsNullOrWhiteSpace(sym))
                issues.Add(CsvGrid.Error("MissingSymbol", "Symbol fehlt.", line));
            if (price is <= 0m)
                issues.Add(CsvGrid.Error("NegativePrice", $"PriceLevel {price} muss > 0 sein.", line));
            if (volume is < 0m || bid is < 0m || ask is < 0m)
                issues.Add(CsvGrid.Error("NegativeVolume", "Volumen darf nicht negativ sein.", line));

            if (issues.Count(i => i.Severity == DataQualitySeverity.Error) > errorsBefore
                || date is null || sym is null || price is null || volume is null)
                continue;

            symbol = sym;
            rowsAccepted++;

            if (!bySession.TryGetValue(date.Value, out var levels))
                bySession[date.Value] = levels = new List<VolumeProfileLevel>();
            levels.Add(new VolumeProfileLevel
            {
                PriceLevel = price.Value, VolumeAtPrice = volume.Value,
                BidVolumeAtPrice = bid, AskVolumeAtPrice = ask,
                IsHighVolumeNode = hvn, IsLowVolumeNode = lvn
            });
        }

        var profiles = bySession
            .OrderBy(kv => kv.Key)
            .Select(kv => new VolumeProfile { Symbol = symbol, SessionDate = kv.Key, Levels = kv.Value })
            .ToList();

        return new ImportedMarketDataSet
        {
            SourceType = MarketDataSourceType.VolumeProfile,
            Symbol = symbol,
            VolumeProfiles = profiles,
            Quality = new OrderFlowDataQualityReport
            {
                SourceType = MarketDataSourceType.VolumeProfile,
                RowsRead = rowsRead, RowsAccepted = rowsAccepted, Issues = issues
            },
            Capabilities = profiles.Count > 0
                ? new OrderFlowCapabilities { SupportsHvnLvn = true }
                : OrderFlowCapabilities.None
        };
    }
}
