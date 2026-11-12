using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.MarketData.Import;

/// <summary>Reads a committed MNQ New York collection day, streaming the CSV and retaining only that day.
/// Fails closed on manifest, window, ordering, classification or reconciliation errors. Never deduplicates.</summary>
public sealed class AtasCollectionImporter
{
    public sealed record Result(ImportedMarketDataSet Data, string CsvPath, string CsvSha256,
        Dictionary<string, string> Evidence);

    public Result ImportDay(string manifestPath, DateOnly day)
    {
        using var manifestStream = File.OpenRead(manifestPath);
        using var doc = JsonDocument.Parse(manifestStream);
        var root = doc.RootElement;
        Require(root.GetProperty("schemaVersion").GetString() == "atas-collection-trades-1", "Collection schema");
        Require(root.GetProperty("session").GetString() == "NewYork", "Session");
        Require(root.GetProperty("year").GetInt32() == day.Year, "Year");
        var timezone = root.GetProperty("timezone").GetString();
        Require(timezone is "Eastern Standard Time" or "America/New_York", "Timezone");
        var ny = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        string Utc(TimeOnly time) => TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(time), ny)
            .ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + ".000000000Z";
        string start = Utc(new TimeOnly(9, 30)), end = Utc(new TimeOnly(12, 0));
        var jobs = root.GetProperty("jobs").EnumerateObject().ToArray();
        var matches = jobs.Where(j => j.Value.GetProperty("window").GetProperty("start").GetString() == start
            && j.Value.GetProperty("window").GetProperty("end").GetString() == end).ToArray();
        Require(matches.Length == 1, "Exactly one 09:30–12:00 New York job required");
        var job = matches[0];
        Require(job.Value.GetProperty("status").GetString() == "committed", "Job not committed");
        Require(job.Value.GetProperty("coverageStatus").GetString() == "complete", "Incomplete coverage");
        Require(job.Value.GetProperty("window").GetProperty("interval").GetString() == "[start, end)", "Window interval");
        var csv = root.GetProperty("csv");
        string path = Path.GetFullPath(csv.GetProperty("path").GetString()!, Path.GetDirectoryName(Path.GetFullPath(manifestPath))!);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Require(stream.Length == csv.GetProperty("committedBytes").GetInt64(), "CSV committedBytes mismatch");
        string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        stream.Position = 0;
        using var parser = new TextFieldParser(stream) { TextFieldType = FieldType.Delimited, TrimWhiteSpace = false };
        parser.SetDelimiters(",");
        var header = parser.ReadFields() ?? throw new InvalidDataException("Missing CSV header");
        var columns = header.Select((name, i) => (name, i)).ToDictionary(x => x.name, x => x.i);
        foreach (var name in new[] { "job_id", "recording_id", "run_id", "t_market_utc", "instrument", "price", "volume", "aggressor_side" })
            Require(columns.ContainsKey(name), "Missing column: " + name);
        var counts = jobs.ToDictionary(j => j.Name, _ => 0L);
        var ticks = new List<MarketTick>();
        decimal buy = 0, sell = 0;
        string? previous = null;
        long rows = 0;
        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields()!;
            Require(fields.Length == header.Length, "CSV field count");
            string Get(string name) => fields[columns[name]];
            string id = Get("job_id");
            Require(counts.ContainsKey(id), "Unknown job in CSV");
            counts[id]++; rows++;
            if (id != job.Name) continue;
            Require(Get("instrument") == "MNQ", "Instrument must be MNQ");
            Require(Get("recording_id") == job.Value.GetProperty("recordingId").GetString()
                && Get("run_id") == job.Value.GetProperty("runId").GetString(), "Recording/run identity mismatch");
            string rawTime = Get("t_market_utc");
            var timestamp = ParseNanoseconds(rawTime);
            Require(string.CompareOrdinal(rawTime, start) >= 0 && string.CompareOrdinal(rawTime, end) < 0, "Tick outside manifest window");
            Require(previous is null || string.CompareOrdinal(rawTime, previous) >= 0, "Nonchronological ticks");
            previous = rawTime;
            decimal price = decimal.Parse(Get("price"), NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            decimal volume = decimal.Parse(Get("volume"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
            Require(price > 0 && volume > 0, "Invalid price/volume");
            var side = Get("aggressor_side").ToLowerInvariant() switch
            {
                "buy" => AggressorSide.Buy,
                "sell" => AggressorSide.Sell,
                _ => throw new InvalidDataException("InsufficientData: missing/invalid aggressor classification")
            };
            if (side == AggressorSide.Buy) buy += volume; else sell += volume;
            ticks.Add(new MarketTick { Symbol = "MNQ", Timestamp = timestamp, Price = price, Volume = volume, Aggressor = side });
        }
        Require(rows == csv.GetProperty("committedRows").GetInt64() && rows == root.GetProperty("totals").GetProperty("rows").GetInt64(), "Collection rows mismatch");
        foreach (var j in jobs) Require(counts[j.Name] == j.Value.GetProperty("rowsAppended").GetInt64(), "Job rows mismatch: " + j.Name);
        var expected = job.Value.GetProperty("trades");
        Require(ticks.Count > 0 && ticks.Count == expected.GetProperty("rows").GetInt64(), "Imported rows mismatch");
        Require(buy == expected.GetProperty("buyVolume").GetDecimal() && sell == expected.GetProperty("sellVolume").GetDecimal()
            && buy + sell == expected.GetProperty("totalVolume").GetDecimal() && expected.GetProperty("unknownVolume").GetDecimal() == 0, "Volume reconciliation failed");
        var evidence = new Dictionary<string, string>
        {
            ["mode"] = "atas-collection-day", ["collection.manifest"] = Path.GetFullPath(manifestPath),
            ["collection.manifestSha256"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifestPath))).ToLowerInvariant(),
            ["collection.jobId"] = job.Name, ["window.fromUtc"] = start, ["window.toUtc"] = end,
            ["window.interval"] = "[start, end)", ["window.session"] = $"{day:yyyy-MM-dd} 09:30–12:00 America/New_York",
            ["import.rows"] = ticks.Count.ToString(CultureInfo.InvariantCulture),
            ["import.totalVolume"] = (buy + sell).ToString(CultureInfo.InvariantCulture),
            ["import.buyVolume"] = buy.ToString(CultureInfo.InvariantCulture), ["import.sellVolume"] = sell.ToString(CultureInfo.InvariantCulture),
            ["import.unknownVolume"] = "0", ["import.reconciliation"] = "manifest rows and buy/sell/total volumes matched; no deduplication",
            ["import.timePrecision"] = "Source/order/window validation: nanoseconds. Engine DateTimeOffset: floored to 100ns; original sequence retained.",
            ["contractOrSeries"] = "unknown; MNQ label is not proof of historical contract identity"
        };
        return new Result(new ImportedMarketDataSet
        {
            Symbol = "MNQ", SourceType = MarketDataSourceType.AggressorTick, Ticks = ticks,
            Quality = new OrderFlowDataQualityReport { SourceType = MarketDataSourceType.AggressorTick, RowsRead = ticks.Count, RowsAccepted = ticks.Count },
            Capabilities = new OrderFlowCapabilities { SupportsDeltaCvd = true, SupportsAbsorption = true, SupportsBarImbalance = true }
        }, path, hash, evidence);
    }

    private static DateTimeOffset ParseNanoseconds(string value)
    {
        Require(value.Length == 30 && value[19] == '.' && value[29] == 'Z'
            && value.AsSpan(20, 9).IndexOfAnyExceptInRange('0', '9') < 0, "Expected UTC nanosecond timestamp");
        return DateTimeOffset.ParseExact(value[..27] + "Z", "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    private static void Require(bool valid, string message)
    {
        if (!valid) throw new InvalidDataException(message);
    }
}
