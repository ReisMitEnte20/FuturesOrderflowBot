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

    /// <summary>Ergebnis eines Tagesabschnitts: nur die im Manifest festgeschriebenen Bytes [SliceStart, SliceEnd) des Jobs.</summary>
    public sealed record SliceResult(ImportedMarketDataSet Data, string CsvPath, long SliceStart, long SliceEnd, string SliceSha256,
        string RecordingId, string RunId, Dictionary<string, string> Evidence);

    /// <summary>
    /// Liest GENAU den committeten Byte-Abschnitt eines Jobs (byteOffsetBefore..byteOffsetAfter laut Manifest) – ohne
    /// Durchlauf der ganzen Collection-CSV. Gleiche Regeln wie <see cref="ImportDay"/> für jede Zeile (Job, Aufnahme-/Lauf-
    /// Identität, MNQ, Fenster [start, end), chronologisch, Aggressor) und Abgleich von Zeilen und Buy/Sell/Gesamtvolumen
    /// mit dem Manifest. Der Abschnitt muss an Zeilengrenzen liegen; die Datei darf seit dem Manifest nur gewachsen sein
    /// (append-only). Der SHA-256 des Abschnitts dient als persistenter Änderungsnachweis. Fail-closed, keine Deduplizierung.
    /// </summary>
    public SliceResult ImportJobSlice(string manifestPath, string jobId)
    {
        using var manifestStream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var doc = JsonDocument.Parse(manifestStream);
        var root = doc.RootElement;
        Require(root.GetProperty("schemaVersion").GetString() == "atas-collection-trades-1", "Collection schema");
        var session = root.GetProperty("session").GetString();
        var timezone = root.GetProperty("timezone").GetString();
        // Unterstuetzte Sessions mit ihrer Boersen-Zeitzone (Manifest schreibt Windows- oder IANA-ID).
        Require(session switch
        {
            "NewYork" => timezone is "Eastern Standard Time" or "America/New_York",
            "London" => timezone is "GMT Standard Time" or "Europe/London",
            _ => false
        }, "Session/Timezone");
        Require(root.GetProperty("jobs").TryGetProperty(jobId, out var job), "Job not in manifest: " + jobId);
        Require(job.GetProperty("status").GetString() == "committed", "Job not committed");
        Require(job.GetProperty("coverageStatus").GetString() == "complete", "Incomplete coverage");
        var window = job.GetProperty("window");
        Require(window.GetProperty("interval").GetString() == "[start, end)", "Window interval");
        string start = window.GetProperty("start").GetString()!, end = window.GetProperty("end").GetString()!;
        bool hasBefore = job.TryGetProperty("byteOffsetBefore", out var ob), hasAfter = job.TryGetProperty("byteOffsetAfter", out var oa);
        Require(hasBefore && hasAfter, "Job byte offsets missing");
        long sliceStart = ob.GetInt64(), sliceEnd = oa.GetInt64();
        Require(sliceStart >= 0 && sliceEnd > sliceStart, "Job byte offsets invalid");
        string recordingId = job.GetProperty("recordingId").GetString() ?? "", runId = job.GetProperty("runId").GetString() ?? "";
        long expectedRows = job.GetProperty("rowsAppended").GetInt64();
        var expected = job.GetProperty("trades");
        Require(expected.GetProperty("rows").GetInt64() == expectedRows, "Job rows mismatch (manifest)");
        var csv = root.GetProperty("csv");
        Require(sliceEnd <= csv.GetProperty("committedBytes").GetInt64(), "Job slice beyond committedBytes");
        string path = Path.GetFullPath(csv.GetProperty("path").GetString()!, Path.GetDirectoryName(Path.GetFullPath(manifestPath))!);

        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        Require(file.Length >= csv.GetProperty("committedBytes").GetInt64(), "CSV shorter than committedBytes (truncated or replaced)");
        // Kopfzeile (Spaltennamen) vom Dateianfang.
        string headerLine = ReadFirstLine(file, out long headerBytes);
        var header = headerLine.Split(',');
        var columns = header.Select((name, i) => (name, i)).ToDictionary(x => x.name, x => x.i);
        foreach (var name in new[] { "job_id", "recording_id", "run_id", "t_market_utc", "instrument", "price", "volume", "aggressor_side" })
            Require(columns.ContainsKey(name), "Missing column: " + name);
        // Abschnitt muss an Zeilengrenzen liegen (Anfang direkt nach '\n' bzw. Dateianfang mit Kopfzeile, Ende auf '\n').
        if (sliceStart > 0) { Require(sliceStart >= headerBytes, "Job slice overlaps header"); Require(ByteAt(file, sliceStart - 1) == (byte)'\n', "Job slice does not start at a line boundary"); }
        Require(ByteAt(file, sliceEnd - 1) == (byte)'\n', "Job slice does not end at a line boundary");

        file.Position = sliceStart;
        using var hashing = new HashingSliceStream(file, sliceEnd - sliceStart);
        using var parser = new TextFieldParser(hashing) { TextFieldType = FieldType.Delimited, TrimWhiteSpace = false };
        parser.SetDelimiters(",");
        if (sliceStart == 0)
        {
            var h = parser.ReadFields() ?? throw new InvalidDataException("Missing CSV header");
            Require(h.SequenceEqual(header), "CSV header mismatch");
        }
        var ticks = new List<MarketTick>();
        decimal buy = 0, sell = 0;
        string? previous = null;
        bool hasContract = columns.ContainsKey("contract_or_series");
        string? contract = null;
        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields()!;
            Require(fields.Length == header.Length, "CSV field count");
            string Get(string name) => fields[columns[name]];
            Require(Get("job_id") == jobId, "Foreign job inside job slice");
            Require(Get("instrument") == "MNQ", "Instrument must be MNQ");
            if (hasContract)
            {
                // Kontrakt/Serie muss innerhalb eines Jobs einheitlich sein (kein stiller Roll innerhalb der Session).
                string c = Get("contract_or_series");
                Require(contract is null || c == contract, "Contract/series changes inside job slice");
                contract = c;
            }
            Require(Get("recording_id") == recordingId && Get("run_id") == runId, "Recording/run identity mismatch");
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
        Require(hashing.Remaining == 0, "Job slice not fully read");
        Require(ticks.Count > 0 && ticks.Count == expectedRows, "Imported rows mismatch");
        Require(buy == expected.GetProperty("buyVolume").GetDecimal() && sell == expected.GetProperty("sellVolume").GetDecimal()
            && buy + sell == expected.GetProperty("totalVolume").GetDecimal() && expected.GetProperty("unknownVolume").GetDecimal() == 0, "Volume reconciliation failed");
        string sliceSha = hashing.HashHex();
        var evidence = new Dictionary<string, string>
        {
            ["mode"] = "atas-collection-job-slice", ["collection.manifest"] = Path.GetFullPath(manifestPath), ["collection.jobId"] = jobId,
            ["slice.bytes"] = $"[{sliceStart}, {sliceEnd})", ["slice.sha256"] = sliceSha,
            ["window.fromUtc"] = start, ["window.toUtc"] = end, ["window.interval"] = "[start, end)",
            ["import.rows"] = ticks.Count.ToString(CultureInfo.InvariantCulture),
            ["import.reconciliation"] = "job rows and buy/sell/total volumes matched; every row belongs to this job; no deduplication",
            ["collection.session"] = session!, ["instrument"] = "MNQ", ["contract"] = contract ?? "(keine Spalte contract_or_series)",
        };
        return new SliceResult(new ImportedMarketDataSet
        {
            Symbol = "MNQ", SourceType = MarketDataSourceType.AggressorTick, Ticks = ticks,
            Quality = new OrderFlowDataQualityReport { SourceType = MarketDataSourceType.AggressorTick, RowsRead = ticks.Count, RowsAccepted = ticks.Count },
            Capabilities = new OrderFlowCapabilities { SupportsDeltaCvd = true, SupportsAbsorption = true, SupportsBarImbalance = true }
        }, path, sliceStart, sliceEnd, sliceSha, recordingId, runId, evidence);
    }

    /// <summary>SHA-256 eines Byte-Abschnitts (Änderungsnachweis ohne Parsen).</summary>
    public static string HashSlice(string csvPath, long sliceStart, long sliceEnd)
    {
        using var file = new FileStream(csvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        Require(file.Length >= sliceEnd, "CSV shorter than job slice");
        file.Position = sliceStart;
        using var s = new HashingSliceStream(file, sliceEnd - sliceStart);
        var buf = new byte[1 << 20];
        while (s.Read(buf, 0, buf.Length) > 0) { }
        return s.HashHex();
    }

    private static string ReadFirstLine(FileStream file, out long bytes)
    {
        file.Position = 0;
        var acc = new List<byte>(256);
        int b;
        while ((b = file.ReadByte()) >= 0 && b != '\n')
        {
            acc.Add((byte)b);
            Require(acc.Count < 1 << 16, "CSV header too long");
        }
        Require(b == '\n', "CSV header not terminated");
        bytes = acc.Count + 1;
        return System.Text.Encoding.UTF8.GetString(acc.ToArray()).TrimEnd('\r');
    }

    private static int ByteAt(FileStream file, long position)
    {
        file.Position = position;
        return file.ReadByte();
    }

    /// <summary>Liest höchstens <c>length</c> Bytes ab der aktuellen Position und hasht dabei alles Gelesene.</summary>
    private sealed class HashingSliceStream(Stream inner, long length) : Stream
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public long Remaining { get; private set; } = length;
        public string HashHex() => Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Remaining <= 0) return 0;
            int n = inner.Read(buffer, offset, (int)Math.Min(count, Remaining));
            if (n <= 0) throw new EndOfStreamException("CSV ended inside job slice");
            _hash.AppendData(buffer, offset, n);
            Remaining -= n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _hash.Dispose(); base.Dispose(disposing); }
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
