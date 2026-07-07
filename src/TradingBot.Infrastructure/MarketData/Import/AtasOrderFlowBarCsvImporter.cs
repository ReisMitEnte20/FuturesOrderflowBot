using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.MarketData.Import;

/// <summary>
/// Importiert Orderflow-Bar-CSV (Format C). Pflicht: bartimestamp, symbol, open, high, low,
/// close, volume, bidvolume, askvolume. Optional: delta, cumulativedelta.
///
/// Konsistenz-Checks (Error → Zeile verworfen):
/// - BidVolume + AskVolume == Volume
/// - Delta (falls vorhanden) == AskVolume − BidVolume
/// - chronologische, eindeutige Bar-Zeitstempel; keine negativen Preise/Volumina
/// Fehlt cumulativedelta, wird es als laufende Delta-Summe berechnet (Info-Issue –
/// legitime Ableitung aus echten Werten, keine Erfindung).
/// </summary>
public sealed class AtasOrderFlowBarCsvImporter
{
    private readonly CsvImportProfile _profile;

    public AtasOrderFlowBarCsvImporter(CsvImportProfile? profile = null)
        => _profile = profile ?? CsvImportProfile.Default(MarketDataSourceType.OrderFlowBars);

    public ImportedMarketDataSet ImportFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"CSV-Datei nicht gefunden: '{path}'.", path);
        using var reader = new StreamReader(path);
        return Import(reader);
    }

    public ImportedMarketDataSet Import(TextReader reader)
    {
        var grid = CsvGrid.Parse(reader, _profile.Delimiter);
        grid.RequireColumns(_profile, "bartimestamp", "symbol", "open", "high", "low", "close",
            "volume", "bidvolume", "askvolume");

        bool hasCvd = grid.HasColumn(_profile.Column("cumulativedelta"));
        bool hasDelta = grid.HasColumn(_profile.Column("delta"));

        var issues = new List<DataQualityIssue>();
        var bars = new List<OrderFlowBar>();
        int rowsRead = 0;
        DateTimeOffset? previous = null;
        decimal runningCvd = 0m;
        string symbol = string.Empty;

        foreach (var (fields, line) in grid.Rows)
        {
            rowsRead++;
            int errorsBefore = CountErrors(issues);

            var ts = grid.Time(fields, _profile, "bartimestamp", issues, line);
            var sym = grid.Raw(fields, _profile, "symbol");
            var open = grid.Dec(fields, _profile, "open", issues, line);
            var high = grid.Dec(fields, _profile, "high", issues, line);
            var low = grid.Dec(fields, _profile, "low", issues, line);
            var close = grid.Dec(fields, _profile, "close", issues, line);
            var volume = grid.Dec(fields, _profile, "volume", issues, line);
            var bidVol = grid.Dec(fields, _profile, "bidvolume", issues, line);
            var askVol = grid.Dec(fields, _profile, "askvolume", issues, line);
            var delta = hasDelta ? grid.Dec(fields, _profile, "delta", issues, line) : null;
            var cvd = hasCvd ? grid.Dec(fields, _profile, "cumulativedelta", issues, line) : null;

            if (ts is null && CountErrors(issues) == errorsBefore)
                issues.Add(CsvGrid.Error("MissingTimestamp", "Bar-Zeitstempel fehlt.", line));
            if (string.IsNullOrWhiteSpace(sym))
                issues.Add(CsvGrid.Error("MissingSymbol", "Symbol fehlt.", line));
            foreach (var (label, value) in new[] { ("Open", open), ("High", high), ("Low", low), ("Close", close) })
                if (value is <= 0m)
                    issues.Add(CsvGrid.Error("NegativePrice", $"{label} {value} muss > 0 sein.", line, ts));
            foreach (var (label, value) in new[] { ("Volume", volume), ("BidVolume", bidVol), ("AskVolume", askVol) })
                if (value is < 0m)
                    issues.Add(CsvGrid.Error("NegativeVolume", $"{label} {value} darf nicht negativ sein.", line, ts));

            if (ts is not null && previous is not null)
            {
                if (ts == previous)
                    issues.Add(CsvGrid.Error("DuplicateTimestamp", $"Doppelter Bar-Zeitstempel {ts:O}.", line, ts));
                else if (ts < previous)
                    issues.Add(CsvGrid.Error("NonChronological",
                        $"Bar-Zeitstempel {ts:O} liegt vor Vorgänger {previous:O}.", line, ts));
            }

            if (volume is not null && bidVol is not null && askVol is not null
                && bidVol.Value + askVol.Value != volume.Value)
                issues.Add(CsvGrid.Error("BidAskSumMismatch",
                    $"BidVolume {bidVol} + AskVolume {askVol} ≠ Volume {volume}.", line, ts));

            if (delta is not null && bidVol is not null && askVol is not null
                && delta.Value != askVol.Value - bidVol.Value)
                issues.Add(CsvGrid.Error("DeltaMismatch",
                    $"Delta {delta} ≠ AskVolume − BidVolume ({askVol - bidVol}).", line, ts));

            if (CountErrors(issues) > errorsBefore || ts is null || sym is null
                || open is null || high is null || low is null || close is null
                || volume is null || bidVol is null || askVol is null)
                continue;

            decimal barDelta = askVol.Value - bidVol.Value;
            runningCvd = cvd ?? runningCvd + barDelta;
            previous = ts;
            symbol = sym;

            bars.Add(new OrderFlowBar
            {
                Symbol = sym, OpenTime = ts.Value, CloseTime = ts.Value,
                Open = open.Value, High = high.Value, Low = low.Value, Close = close.Value,
                TotalVolume = volume.Value, BidVolume = bidVol.Value, AskVolume = askVol.Value,
                CumulativeDelta = runningCvd
            });
        }

        if (!hasCvd && bars.Count > 0)
            issues.Add(CsvGrid.Info("CvdDerived",
                "Spalte 'cumulativedelta' fehlt – CVD wurde als laufende Summe der echten Bar-Deltas berechnet."));

        return new ImportedMarketDataSet
        {
            SourceType = MarketDataSourceType.OrderFlowBars,
            Symbol = symbol,
            OrderFlowBars = bars,
            Quality = new OrderFlowDataQualityReport
            {
                SourceType = MarketDataSourceType.OrderFlowBars,
                RowsRead = rowsRead, RowsAccepted = bars.Count, Issues = issues
            },
            Capabilities = bars.Count > 0
                ? new OrderFlowCapabilities
                {
                    SupportsDeltaCvd = true, SupportsAbsorption = true, SupportsBarImbalance = true
                }
                : OrderFlowCapabilities.None
        };
    }

    private static int CountErrors(List<DataQualityIssue> issues)
        => issues.Count(i => i.Severity == DataQualitySeverity.Error);
}
