using System.Globalization;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.MarketData.Import;

/// <summary>Granularität eines Sierra-Exports (ehrlich, ohne falsche Tick-Garantie).</summary>
public enum SierraGranularity { SingleTick, AggregatedOrUnknown }

/// <summary>Read-only Report der Streaming-Validierung einer großen Sierra-Datei.</summary>
public sealed record SierraValidationReport
{
    public long FileSizeBytes { get; init; } = -1;      // -1 = unbekannt (TextReader-Overload)
    public long RowsProcessed { get; init; }
    public long ValidRows { get; init; }
    public long ParseErrors { get; init; }
    public bool Truncated { get; init; }                // MaxRows erreicht

    public DateTimeOffset? FirstTimestamp { get; init; }
    public DateTimeOffset? LastTimestamp { get; init; }

    public decimal TotalVolume { get; init; }
    public decimal SumBidVolume { get; init; }
    public decimal SumAskVolume { get; init; }
    public decimal NetDelta => SumAskVolume - SumBidVolume;

    public decimal? MinPrice { get; init; }
    public decimal? MaxPrice { get; init; }

    public SierraGranularity Granularity { get; init; } = SierraGranularity.AggregatedOrUnknown;
    public bool IsSingleTick => Granularity == SierraGranularity.SingleTick;

    public bool SupportsDeltaCvd { get; init; }
    public bool SupportsDomLevel2 => false;             // Sierra-Text-Export enthält kein DOM/Level 2

    /// <summary>Erste N Data-Quality-/Parse-Hinweise (gedeckelt, damit RAM konstant bleibt).</summary>
    public IReadOnlyList<DataQualityIssue> Issues { get; init; } = Array.Empty<DataQualityIssue>();
}

/// <summary>
/// Streaming-Validator für GROSSE lokale Sierra-Text/CSV-Exporte. Liest die Datei ZEILENWEISE
/// (kein <c>ReadAllText</c>, kein <c>ToList()</c> über die ganze Datei) und aggregiert nur Kennzahlen –
/// so bleibt der Speicher konstant, auch bei mehreren GB. Kopiert/committet nichts, nur lokales Lesen.
///
/// Fehlt der Header oder eine Pflichtspalte (date, time, last, volume) → Fehler. Einzelne kaputte
/// Zeilen crashen NICHT, sondern erhöhen ParseErrors und landen (gedeckelt) im Report.
/// </summary>
public sealed class SierraLargeFileValidator
{
    private const int ProgressEvery = 100_000;
    private const int MaxStoredIssues = 50;

    private readonly CsvImportProfile _profile;

    public SierraLargeFileValidator(CsvImportProfile? profile = null)
        => _profile = profile ?? CsvImportProfile.Default(MarketDataSourceType.AggressorTick);

    public SierraValidationReport ValidateFile(string path, long? maxRows = null, Action<long>? onProgress = null)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Datei nicht gefunden: '{path}'.", path);
        long size = new FileInfo(path).Length;
        using var reader = new StreamReader(path);
        return Validate(reader, maxRows, onProgress) with { FileSizeBytes = size };
    }

    public SierraValidationReport Validate(TextReader reader, long? maxRows = null, Action<long>? onProgress = null)
    {
        ArgumentNullException.ThrowIfNull(reader);

        // --- Header (erste nicht-leere Zeile) ---
        string? headerLine = ReadNonEmptyLine(reader);
        if (headerLine is null)
            throw new CsvMarketDataException("Leere Datei oder fehlende Kopfzeile.");
        var columns = IndexColumns(headerLine);
        RequireColumns(columns, "date", "time", "last", "volume");

        bool hasBidAsk = Has(columns, "bidvolume") && Has(columns, "askvolume");
        bool hasNumTrades = Has(columns, "numberoftrades");

        long rows = 0, valid = 0, parseErrors = 0, unclassified = 0;
        bool truncated = false, allSingleTrade = hasNumTrades;
        decimal totalVol = 0m, sumBid = 0m, sumAsk = 0m;
        decimal? minPrice = null, maxPrice = null;
        DateTimeOffset? first = null, last = null, previous = null;
        var issues = new List<DataQualityIssue>();

        void AddIssue(DataQualityIssue i) { if (issues.Count < MaxStoredIssues) issues.Add(i); }

        string? line;
        int lineNo = 1; // Header war Zeile 1 (logisch)
        while ((line = reader.ReadLine()) is not null)
        {
            lineNo++;
            if (line.Length == 0 || string.IsNullOrWhiteSpace(line)) continue;

            if (maxRows is long max && rows >= max) { truncated = true; break; }
            rows++;

            var f = SplitTrim(line);
            var ts = CombineTimestamp(f, columns);
            var price = Dec(f, columns, "last");
            var volume = Dec(f, columns, "volume");

            if (ts is null || price is null || price <= 0m || volume is null || volume < 0m)
            {
                parseErrors++;
                AddIssue(CsvGrid.Error("ParseError", $"Zeile nicht verwertbar (ts/last/volume).", lineNo));
                continue;
            }
            if (previous is not null && ts < previous)
                AddIssue(CsvGrid.Warning("NonChronological", $"Zeitstempel {ts:O} vor Vorgänger {previous:O}.", lineNo, ts));

            decimal numTrades = hasNumTrades ? Dec(f, columns, "numberoftrades") ?? 0m : 0m;
            if (!(hasNumTrades && numTrades == 1m)) allSingleTrade = false;

            decimal bid = hasBidAsk ? Dec(f, columns, "bidvolume") ?? 0m : 0m;
            decimal ask = hasBidAsk ? Dec(f, columns, "askvolume") ?? 0m : 0m;
            if (hasBidAsk && !((ask > 0m && bid == 0m) || (bid > 0m && ask == 0m))) unclassified++;

            valid++;
            totalVol += volume.Value;
            sumBid += bid;
            sumAsk += ask;
            minPrice = minPrice is null ? price : Math.Min(minPrice.Value, price.Value);
            maxPrice = maxPrice is null ? price : Math.Max(maxPrice.Value, price.Value);
            first ??= ts;
            last = ts;
            previous = ts;

            if (rows % ProgressEvery == 0) onProgress?.Invoke(rows);
        }

        bool singleTick = allSingleTrade && valid > 0;
        if (singleTick)
            AddIssue(CsvGrid.Info("SierraSingleTick", "1-Tick-Export (NumberOfTrades == 1) – geeignet für Orderflow-Forschung."));
        else
            AddIssue(CsvGrid.Warning("SierraAggregatedRecords",
                "Aggregiert oder Granularität unbekannt (NumberOfTrades > 1 bzw. Spalte fehlt) – KEINE Tick-Garantie."));

        if (hasBidAsk && valid > 0 && unclassified > 0)
            AddIssue(CsvGrid.Warning("PartialClassification",
                $"{unclassified} Zeilen ohne eindeutige Bid/Ask-Klassifikation – Delta/CVD NICHT erlaubt."));

        bool fullyClassified = hasBidAsk && valid > 0 && unclassified == 0;

        return new SierraValidationReport
        {
            RowsProcessed = rows,
            ValidRows = valid,
            ParseErrors = parseErrors,
            Truncated = truncated,
            FirstTimestamp = first,
            LastTimestamp = last,
            TotalVolume = totalVol,
            SumBidVolume = sumBid,
            SumAskVolume = sumAsk,
            MinPrice = minPrice,
            MaxPrice = maxPrice,
            Granularity = singleTick ? SierraGranularity.SingleTick : SierraGranularity.AggregatedOrUnknown,
            SupportsDeltaCvd = fullyClassified,
            Issues = issues
        };
    }

    // ------------------------- Helper (alle streaming-tauglich) -------------------------

    private static string? ReadNonEmptyLine(TextReader reader)
    {
        string? line;
        while ((line = reader.ReadLine()) is not null)
            if (!string.IsNullOrWhiteSpace(line)) return line;
        return null;
    }

    private Dictionary<string, int> IndexColumns(string headerLine)
    {
        var parts = SplitTrim(headerLine);
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < parts.Length; i++)
            if (parts[i].Length > 0) map[parts[i]] = i;
        return map;
    }

    private bool Has(Dictionary<string, int> columns, string logical) => columns.ContainsKey(_profile.Column(logical));

    private void RequireColumns(Dictionary<string, int> columns, params string[] logicalFields)
    {
        var missing = logicalFields.Where(f => !columns.ContainsKey(_profile.Column(f)))
            .Select(f => $"'{_profile.Column(f)}' (Feld '{f}')").ToList();
        if (missing.Count > 0)
            throw new CsvMarketDataException("Pflichtspalte(n) fehlen im CSV-Header: " + string.Join(", ", missing));
    }

    private string[] SplitTrim(string line)
    {
        var raw = line.Split(_profile.Delimiter);
        for (int i = 0; i < raw.Length; i++) raw[i] = raw[i].Trim();
        return raw;
    }

    private string? Raw(string[] fields, Dictionary<string, int> columns, string logical)
    {
        if (!columns.TryGetValue(_profile.Column(logical), out int i) || i >= fields.Length) return null;
        var v = fields[i];
        return v.Length == 0 ? null : v;
    }

    private decimal? Dec(string[] fields, Dictionary<string, int> columns, string logical)
    {
        var raw = Raw(fields, columns, logical);
        if (raw is null) return null;
        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private DateTimeOffset? CombineTimestamp(string[] fields, Dictionary<string, int> columns)
    {
        var date = Raw(fields, columns, "date");
        var time = Raw(fields, columns, "time");
        if (date is null || time is null) return null;
        return DateTimeOffset.TryParse($"{date} {time}", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var ts)
            ? ts.ToUniversalTime() : null;
    }
}
