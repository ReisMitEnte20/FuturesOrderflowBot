using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.MarketData.Import;

/// <summary>
/// Importiert Tick-CSV (Minimal- oder Aggressor-Format, Spalten via <see cref="CsvImportProfile"/>).
/// Pflicht: timestamp, symbol, price, volume. Optional: tradedirection bzw. bidvolume/askvolume.
///
/// Capabilities: Delta/CVD & Co. NUR, wenn ALLE übernommenen Ticks klassifiziert sind
/// (alles-oder-nichts – teilklassifizierte Daten würden Orderflow-Werte verfälschen).
/// Zeilen mit Fehlern (negativer Preis/Volumen, kaputte Werte) werden verworfen + Error-Issue.
/// Nicht-chronologische Zeitstempel werden als Error gemeldet (Zeile verworfen, nichts sortiert).
/// </summary>
public sealed class AtasTickCsvImporter
{
    private readonly CsvImportProfile _profile;

    public AtasTickCsvImporter(CsvImportProfile? profile = null)
    {
        _profile = profile ?? CsvImportProfile.Default(MarketDataSourceType.MinimalTick);
    }

    public ImportedMarketDataSet ImportFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"CSV-Datei nicht gefunden: '{path}'.", path);
        using var reader = new StreamReader(path);
        return Import(reader);
    }

    public ImportedMarketDataSet Import(TextReader reader)
    {
        var grid = CsvGrid.Parse(reader, _profile.Delimiter);
        grid.RequireColumns(_profile, "timestamp", "symbol", "price", "volume");

        bool hasDirection = grid.HasColumn(_profile.Column("tradedirection"));
        bool hasBidAsk = grid.HasColumn(_profile.Column("bidvolume")) && grid.HasColumn(_profile.Column("askvolume"));

        var issues = new List<DataQualityIssue>();
        var ticks = new List<MarketTick>();
        int rowsRead = 0, unclassified = 0;
        DateTimeOffset? previous = null;
        string symbol = string.Empty;

        foreach (var (fields, line) in grid.Rows)
        {
            rowsRead++;
            int errorsBefore = issues.Count(i => i.Severity == DataQualitySeverity.Error);

            var ts = grid.Time(fields, _profile, "timestamp", issues, line);
            var sym = grid.Raw(fields, _profile, "symbol");
            var price = grid.Dec(fields, _profile, "price", issues, line);
            var volume = grid.Dec(fields, _profile, "volume", issues, line);

            if (ts is null && issues.Count(i => i.Severity == DataQualitySeverity.Error) == errorsBefore)
                issues.Add(CsvGrid.Error("MissingTimestamp", "Zeitstempel fehlt.", line));
            if (string.IsNullOrWhiteSpace(sym))
                issues.Add(CsvGrid.Error("MissingSymbol", "Symbol fehlt.", line));
            if (price is <= 0m)
                issues.Add(CsvGrid.Error("NegativePrice", $"Preis {price} muss > 0 sein.", line, ts));
            if (volume is < 0m)
                issues.Add(CsvGrid.Error("NegativeVolume", $"Volumen {volume} darf nicht negativ sein.", line, ts));

            if (ts is not null && previous is not null && ts < previous)
                issues.Add(CsvGrid.Error("NonChronological",
                    $"Zeitstempel {ts:O} liegt vor Vorgänger {previous:O}.", line, ts));

            if (issues.Count(i => i.Severity == DataQualitySeverity.Error) > errorsBefore || ts is null
                || sym is null || price is null || volume is null)
                continue; // Zeile verworfen (Issues bereits erfasst)

            var aggressor = Classify(fields, grid, issues, line, hasDirection, hasBidAsk);
            if (aggressor == AggressorSide.Unknown) unclassified++;

            previous = ts;
            symbol = sym;
            ticks.Add(new MarketTick
            {
                Symbol = sym, Timestamp = ts.Value, Price = price.Value, Volume = volume.Value,
                Aggressor = aggressor
            });
        }

        bool anyClassification = hasDirection || hasBidAsk;
        bool fullyClassified = anyClassification && ticks.Count > 0 && unclassified == 0;
        if (anyClassification && unclassified > 0)
            issues.Add(CsvGrid.Warning("PartialClassification",
                $"{unclassified} von {ticks.Count} Ticks ohne Aggressor-Klassifikation – " +
                "Orderflow-Analysen (Delta/CVD) sind mit diesem Datensatz NICHT erlaubt."));

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

    private AggressorSide Classify(
        string[] fields, CsvGrid grid, List<DataQualityIssue> issues, int line, bool hasDirection, bool hasBidAsk)
    {
        if (hasDirection)
        {
            var raw = grid.Raw(fields, _profile, "tradedirection")?.ToLowerInvariant();
            switch (raw)
            {
                case "buy" or "b" or "ask" or "1" or "+1": return AggressorSide.Buy;
                case "sell" or "s" or "bid" or "-1": return AggressorSide.Sell;
                case null or "unknown" or "none" or "n" or "0": return AggressorSide.Unknown;
                default:
                    issues.Add(CsvGrid.Error("InvalidTradeDirection", $"Unbekannte TradeDirection '{raw}'.", line));
                    return AggressorSide.Unknown;
            }
        }

        if (hasBidAsk)
        {
            var bid = grid.Dec(fields, _profile, "bidvolume", issues, line) ?? 0m;
            var ask = grid.Dec(fields, _profile, "askvolume", issues, line) ?? 0m;
            if (ask > 0m && bid == 0m) return AggressorSide.Buy;
            if (bid > 0m && ask == 0m) return AggressorSide.Sell;
        }

        return AggressorSide.Unknown;
    }
}
