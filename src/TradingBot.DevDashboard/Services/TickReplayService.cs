using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.MarketData.Import;

namespace TradingBot.DevDashboard.Services;

/// <summary>Eine abspielbare Session (ein committeter, vollstaendiger Collection-Tag).</summary>
public sealed record TickReplaySession(
    string JobId, string Day, string Label, string Timezone, string WindowStartUtc, string WindowEndUtc,
    long Rows, decimal TotalVolume);

/// <summary>Sessions der konfigurierten Collection (oder Grund, warum keine verfuegbar sind).</summary>
public sealed record TickReplaySessionList(
    bool Available, string? Reason, string CollectionDir, string ManifestPath, IReadOnlyList<TickReplaySession> Sessions);

/// <summary>Metadaten einer vorbereiteten Session. Zeiten der Trades sind 100-ns-Einheiten relativ zu AnchorUtc
/// (AnchorUtc liegt auf einer 5-Minuten-Grenze, daher sind Kerzenintervalle [Start, Ende) exakt ganzzahlig).</summary>
public sealed record TickReplaySessionMeta(
    string JobId, string Day, string Label, string Timezone,
    string WindowStartUtc, string WindowEndUtc, string AnchorUtc, long AnchorUnixMs,
    long WindowStartUnits, long WindowEndUnits, long UnitsPerMs, long BucketUnits,
    long Count, decimal TotalVolume, decimal BuyVolume, decimal SellVolume,
    int RowBytes, string CsvPath, string CsvSha256, string ManifestPath, string Cache, double PrepareSeconds,
    string TimePrecision);

/// <summary>
/// Read-only Tick-Replay-Quelle fuer das React-Research-Dashboard. Nutzt den bestehenden, fail-closed
/// <see cref="AtasCollectionImporter"/> (Fenster, Reihenfolge, Zeilen- und Volumenabgleich, keine Deduplizierung)
/// und legt je Session EINMALIG einen kompakten Binaer-Cache ausserhalb der Originaldaten an. Danach werden
/// Trades portionsweise (Seek, O(1)) ausgeliefert – die grosse Wochen-CSV gelangt nie als Ganzes in den Browser.
/// Originaldateien werden nur gelesen. Keine Order-/Broker-/Execution-Referenz.
/// </summary>
public sealed class TickReplayService
{
    /// <summary>5 Minuten in 100-ns-Einheiten.</summary>
    public const long BucketUnits = 3_000_000_000L;
    public const long UnitsPerMs = TimeSpan.TicksPerMillisecond;
    /// <summary>Zeilenformat im Cache und im Abschnitt: Float64 Zeit, Float64 Preis, Int32 Volumen, Int8 Seite.</summary>
    public const int RowBytes = 21;
    public const int MaxChunkRows = 250_000;
    private const string Magic = "TRPL-1";
    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    private readonly string _collectionDir;
    private readonly string _cacheDir;
    private readonly ConcurrentDictionary<string, Lazy<Task<TickReplaySessionMeta>>> _builds = new();

    public TickReplayService(string collectionDir, string cacheDir)
    {
        _collectionDir = collectionDir;
        _cacheDir = cacheDir;
    }

    public string ManifestPath => Path.Combine(_collectionDir, "collection_manifest.json");
    private string CacheFile(string jobId) =>
        Path.Combine(_cacheDir, SafeName(Path.GetFileName(_collectionDir.TrimEnd('\\', '/'))), SafeName(jobId) + ".bin");

    /// <summary>Alle vom Importer unterstuetzten Sessions (NewYork 09:30–12:00 America/New_York, committed, complete).</summary>
    public TickReplaySessionList ListSessions()
    {
        if (!File.Exists(ManifestPath))
            return new(false, "Collection-Manifest nicht gefunden: " + ManifestPath, _collectionDir, ManifestPath, Array.Empty<TickReplaySession>());
        var list = new List<TickReplaySession>();
        foreach (var job in ReadJobs()) list.Add(new(job.JobId, job.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), job.Label,
            "America/New_York", job.StartIso, job.EndIso, job.Rows, job.TotalVolume));
        list.Sort((a, b) => string.CompareOrdinal(a.WindowStartUtc, b.WindowStartUtc));
        return new(list.Count > 0, list.Count > 0 ? null : "Keine vollstaendigen NewYork-09:30–12:00-Sessions im Manifest.",
            _collectionDir, ManifestPath, list);
    }

    /// <summary>Stellt sicher, dass der Cache der Session gueltig ist (sonst einmaliger Aufbau ueber den Importer).</summary>
    public async Task<TickReplaySessionMeta> PrepareAsync(string jobId, CancellationToken ct)
    {
        var job = ReadJobs().FirstOrDefault(j => j.JobId == jobId)
            ?? throw new KeyNotFoundException("Unbekannte oder nicht unterstuetzte Session: " + jobId);
        var cached = TryReadValidMeta(job);
        if (cached is not null) return cached;
        // Aufbau laeuft unabhaengig von der Anfrage weiter (ein Abbruch durch Datumswechsel verwirft das Ergebnis
        // nur auf Client-Seite); parallele Anfragen derselben Session teilen sich einen Aufbau.
        var lazy = _builds.GetOrAdd(jobId, _ => new Lazy<Task<TickReplaySessionMeta>>(() => Task.Run(() => Build(job))));
        try { return await lazy.Value.WaitAsync(ct); }
        finally { if (lazy.IsValueCreated && lazy.Value.IsCompleted) _builds.TryRemove(new KeyValuePair<string, Lazy<Task<TickReplaySessionMeta>>>(jobId, lazy)); }
    }

    /// <summary>Liest Trades [offset, offset+count) als Spaltenblock [t f64 x n][Preis f64 x n][Vol i32 x n][Seite i8 x n].</summary>
    public byte[] ReadTicks(string jobId, long offset, int count)
    {
        var job = ReadJobs().FirstOrDefault(j => j.JobId == jobId)
            ?? throw new KeyNotFoundException("Unbekannte oder nicht unterstuetzte Session: " + jobId);
        var meta = TryReadValidMeta(job) ?? throw new InvalidOperationException("Session ist nicht vorbereitet (zuerst /meta abrufen).");
        if (offset < 0 || offset > meta.Count) throw new ArgumentOutOfRangeException(nameof(offset));
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        int n = (int)Math.Min(Math.Min(count, MaxChunkRows), meta.Count - offset);
        var rows = new byte[n * RowBytes];
        using (var fs = new FileStream(CacheFile(jobId), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            int headerLen = ReadHeaderLength(fs);
            fs.Position = 4 + headerLen + offset * RowBytes;
            fs.ReadExactly(rows);
        }
        var cols = new byte[n * RowBytes];
        for (int i = 0; i < n; i++)
        {
            int src = i * RowBytes;
            Buffer.BlockCopy(rows, src, cols, i * 8, 8);
            Buffer.BlockCopy(rows, src + 8, cols, 8 * n + i * 8, 8);
            Buffer.BlockCopy(rows, src + 16, cols, 16 * n + i * 4, 4);
            cols[20 * n + i] = rows[src + 20];
        }
        return cols;
    }

    // ------------------------------------------------------------------------------------------------------------

    private sealed record Job(string JobId, DateOnly Day, string Label, string StartIso, string EndIso,
        long StartNs, long EndNs, long Rows, decimal TotalVolume);

    private List<Job> ReadJobs()
    {
        if (!File.Exists(ManifestPath)) throw new FileNotFoundException("Collection-Manifest nicht gefunden.", ManifestPath);
        using var fs = new FileStream(ManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var doc = JsonDocument.Parse(fs);
        var root = doc.RootElement;
        var jobs = new List<Job>();
        if (root.GetProperty("schemaVersion").GetString() != "atas-collection-trades-1" || root.GetProperty("session").GetString() != "NewYork")
            return jobs;
        foreach (var j in root.GetProperty("jobs").EnumerateObject())
        {
            var v = j.Value;
            if (v.GetProperty("status").GetString() != "committed" || v.GetProperty("coverageStatus").GetString() != "complete") continue;
            var w = v.GetProperty("window");
            if (w.GetProperty("interval").GetString() != "[start, end)") continue;
            string startIso = w.GetProperty("start").GetString()!, endIso = w.GetProperty("end").GetString()!;
            long startNs = w.TryGetProperty("startNs", out var sn) ? sn.GetInt64() : ParseNs(startIso);
            long endNs = w.TryGetProperty("endNs", out var en) ? en.GetInt64() : ParseNs(endIso);
            var startLocal = TimeZoneInfo.ConvertTimeFromUtc(FromUnixNs(startNs).UtcDateTime, NewYork);
            var endLocal = TimeZoneInfo.ConvertTimeFromUtc(FromUnixNs(endNs).UtcDateTime, NewYork);
            // Nur Fenster, die der bestehende Importer unterstuetzt (09:30–12:00 America/New_York desselben Tages).
            if (startLocal.TimeOfDay != new TimeSpan(9, 30, 0) || endLocal.TimeOfDay != new TimeSpan(12, 0, 0) || startLocal.Date != endLocal.Date) continue;
            var day = DateOnly.FromDateTime(startLocal);
            var trades = v.GetProperty("trades");
            string label = day.ToString("ddd dd.MM.yyyy", German) + " · New York 09:30–12:00 (" + (NewYork.IsDaylightSavingTime(startLocal) ? "EDT" : "EST") + ")";
            jobs.Add(new Job(j.Name, day, label, startIso, endIso, startNs, endNs,
                trades.GetProperty("rows").GetInt64(), trades.GetProperty("totalVolume").GetDecimal()));
        }
        return jobs;
    }

    private TickReplaySessionMeta Build(Job job)
    {
        var sw = Stopwatch.StartNew();
        var result = new AtasCollectionImporter().ImportDay(ManifestPath, job.Day);
        if (result.Evidence.GetValueOrDefault("collection.jobId") != job.JobId)
            throw new InvalidDataException("Importer lieferte eine andere Session als angefordert.");
        var ticks = result.Data.Ticks;
        if (ticks.Count != job.Rows) throw new InvalidDataException("Zeilenzahl weicht vom Manifest ab.");

        long windowStartTicks = FromUnixNs(job.StartNs).UtcTicks, windowEndTicks = FromUnixNs(job.EndNs).UtcTicks;
        long anchorTicks = windowStartTicks - (windowStartTicks % BucketUnits); // 5-Minuten-Grenze (UTC = NY-Raster)
        decimal buy = 0, sell = 0;
        string cache = CacheFile(job.JobId);
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        string tmp = cache + ".tmp-" + Guid.NewGuid().ToString("N");
        var csvInfo = new FileInfo(result.CsvPath);
        var manInfo = new FileInfo(ManifestPath);
        var meta = new MetaHeader(job.JobId, csvInfo.Length, csvInfo.LastWriteTimeUtc.Ticks, manInfo.Length, manInfo.LastWriteTimeUtc.Ticks,
            result.CsvSha256, result.CsvPath, anchorTicks, windowStartTicks, windowEndTicks, ticks.Count, 0, 0, 0);
        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var bw = new BinaryWriter(fs))
        {
            // Volumen-Summen stehen erst nach dem Durchlauf fest -> Header am Ende mit fester Laenge neu schreiben.
            byte[] placeholder = SerializeHeader(meta);
            bw.Write(placeholder.Length);
            bw.Write(placeholder);
            long prev = long.MinValue;
            foreach (var t in ticks)
            {
                long off = t.Timestamp.UtcTicks - anchorTicks;
                if (off < 0 || off >= (1L << 53)) throw new InvalidDataException("Zeit ausserhalb des darstellbaren Bereichs.");
                if (off < prev) throw new InvalidDataException("Nicht chronologische Trades.");
                prev = off;
                if (t.Volume != decimal.Truncate(t.Volume) || t.Volume <= 0 || t.Volume > int.MaxValue)
                    throw new InvalidDataException("Volumen ist keine positive Ganzzahl: " + t.Volume);
                sbyte side = t.Aggressor switch { AggressorSide.Buy => (sbyte)1, AggressorSide.Sell => (sbyte)-1, _ => throw new InvalidDataException("InsufficientData: Aggressor fehlt") };
                if (side > 0) buy += t.Volume; else sell += t.Volume;
                bw.Write((double)off);
                bw.Write((double)t.Price);
                bw.Write((int)t.Volume);
                bw.Write(side);
            }
            meta = meta with { TotalVolume = buy + sell, BuyVolume = buy, SellVolume = sell };
            byte[] header = SerializeHeader(meta);
            if (header.Length != placeholder.Length) throw new InvalidOperationException("Header-Laenge geaendert.");
            fs.Position = 4;
            bw.Write(header);
        }
        File.Move(tmp, cache, overwrite: true);
        sw.Stop();
        return ToMeta(job, meta, "aufgebaut", sw.Elapsed.TotalSeconds);
    }

    private TickReplaySessionMeta? TryReadValidMeta(Job job)
    {
        string cache = CacheFile(job.JobId);
        if (!File.Exists(cache)) return null;
        try
        {
            MetaHeader h;
            using (var fs = new FileStream(cache, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                int len = ReadHeaderLength(fs);
                var buf = new byte[len];
                fs.ReadExactly(buf);
                h = DeserializeHeader(buf);
                if (fs.Length != 4 + len + h.Count * RowBytes) return null;
            }
            var csv = new FileInfo(h.CsvPath);
            var man = new FileInfo(ManifestPath);
            bool valid = h.JobId == job.JobId && h.Count == job.Rows && h.TotalVolume == job.TotalVolume
                && csv.Exists && csv.Length == h.CsvLength && csv.LastWriteTimeUtc.Ticks == h.CsvWriteTicks
                && man.Length == h.ManifestLength && man.LastWriteTimeUtc.Ticks == h.ManifestWriteTicks;
            return valid ? ToMeta(job, h, "wiederverwendet", 0) : null;
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException or IOException or FormatException)
        {
            return null; // defekter/fremder Cache -> neu aufbauen
        }
    }

    private TickReplaySessionMeta ToMeta(Job job, MetaHeader h, string cache, double seconds) => new(
        job.JobId, job.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), job.Label, "America/New_York",
        job.StartIso, job.EndIso, new DateTime(h.AnchorTicks, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        (h.AnchorTicks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond,
        h.WindowStartTicks - h.AnchorTicks, h.WindowEndTicks - h.AnchorTicks, UnitsPerMs, BucketUnits,
        h.Count, h.TotalVolume, h.BuyVolume, h.SellVolume, RowBytes, h.CsvPath, h.CsvSha256, Path.GetFullPath(ManifestPath),
        cache, Math.Round(seconds, 2),
        "Quelle in Nanosekunden; Replay-Zeit auf 100 ns abgerundet (kann keine 5-Min-Grenze ueberschreiten); Originalreihenfolge erhalten.");

    // --- Cache-Header ---------------------------------------------------------------------------------------------

    private sealed record MetaHeader(string JobId, long CsvLength, long CsvWriteTicks, long ManifestLength, long ManifestWriteTicks,
        string CsvSha256, string CsvPath, long AnchorTicks, long WindowStartTicks, long WindowEndTicks, long Count,
        decimal TotalVolume, decimal BuyVolume, decimal SellVolume);

    private static byte[] SerializeHeader(MetaHeader h)
    {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write(Magic); bw.Write(h.JobId); bw.Write(h.CsvLength); bw.Write(h.CsvWriteTicks); bw.Write(h.ManifestLength);
            bw.Write(h.ManifestWriteTicks); bw.Write(h.CsvSha256); bw.Write(h.CsvPath); bw.Write(h.AnchorTicks); bw.Write(h.WindowStartTicks);
            bw.Write(h.WindowEndTicks); bw.Write(h.Count); bw.Write(h.TotalVolume); bw.Write(h.BuyVolume); bw.Write(h.SellVolume);
        }
        return ms.ToArray();
    }

    private static MetaHeader DeserializeHeader(byte[] buf)
    {
        using var br = new BinaryReader(new MemoryStream(buf));
        if (br.ReadString() != Magic) throw new InvalidDataException("Unbekanntes Cache-Format.");
        return new MetaHeader(br.ReadString(), br.ReadInt64(), br.ReadInt64(), br.ReadInt64(), br.ReadInt64(), br.ReadString(), br.ReadString(),
            br.ReadInt64(), br.ReadInt64(), br.ReadInt64(), br.ReadInt64(), br.ReadDecimal(), br.ReadDecimal(), br.ReadDecimal());
    }

    private static int ReadHeaderLength(Stream fs)
    {
        Span<byte> b = stackalloc byte[4];
        fs.Position = 0;
        fs.ReadExactly(b);
        int len = BitConverter.ToInt32(b);
        if (len <= 0 || len > 1 << 20) throw new InvalidDataException("Ungueltige Header-Laenge.");
        return len;
    }

    private static DateTimeOffset FromUnixNs(long ns) => new(DateTime.UnixEpoch.Ticks + ns / 100, TimeSpan.Zero);

    private static long ParseNs(string iso)
    {
        if (iso.Length != 30 || iso[19] != '.' || iso[29] != 'Z') throw new InvalidDataException("Erwartet UTC-Nanosekunden: " + iso);
        var whole = DateTime.ParseExact(iso[..19], "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        return (whole.Ticks - DateTime.UnixEpoch.Ticks) * 100 + long.Parse(iso.AsSpan(20, 9), CultureInfo.InvariantCulture);
    }

    private static string SafeName(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
}
