using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TradingBot.Tests.Sessions;

/// <summary>Synthetische ATAS-Collections (KEINE Marktdaten) im Temp-Ordner: getrennte Datenordner für London und New York,
/// Manifest mit Byte-Abschnitten je Job und Rohaufnahme-summary.json (Drops). Format wie atas_collect.</summary>
internal static class SessionFixtures
{
    public sealed record JobSpec(string Session, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string RecordingId,
        IReadOnlyList<(DateTimeOffset T, decimal Price, int Vol, bool Buy)> Ticks, long Drops = 0, bool Clean = true, string Contract = "unknown");

    public static string NewRoot() => Path.Combine(Path.GetTempPath(), "lnny-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Schreibt EINE Collection (ein Manifest + trades.csv) unter root/Collections/name.</summary>
    public static string WriteCollection(string dataRoot, string name, string session, string timezone, IEnumerable<JobSpec> jobs)
    {
        string collections = Path.Combine(dataRoot, "Collections");
        string dir = Path.Combine(collections, name);
        Directory.CreateDirectory(dir);
        string csv = Path.Combine(dir, "trades.csv");
        const string header = "session,year,job_id,recording_id,run_id,src_seq,recorder_seq,t_market_utc,instrument,contract_or_series,price,volume,aggressor_side,exch_order_id,aggr_exch_order_id,open_interest\n";
        var sb = new StringBuilder(header);
        var jobDict = new Dictionary<string, object>();
        long offset = Encoding.UTF8.GetByteCount(header);
        long rowsTotal = 0;
        foreach (var j in jobs)
        {
            string jobId = $"{j.Session}_{j.StartUtc.Year}_{j.StartUtc:yyyyMMdd'T'HHmmss'Z'}_{j.EndUtc:yyyyMMdd'T'HHmmss'Z'}";
            string recDir = Path.Combine(dataRoot, "Recordings", j.RecordingId);
            Directory.CreateDirectory(recDir);
            File.WriteAllText(Path.Combine(recDir, "summary.json"), $"{{\"totalDrops\": {j.Drops}, \"synthetic\": true}}");
            var rows = new StringBuilder();
            decimal buy = 0, sell = 0; int seq = 0;
            foreach (var (t, p, v, isBuy) in j.Ticks)
            {
                seq++;
                if (isBuy) buy += v; else sell += v;
                rows.Append(CultureInfo.InvariantCulture, $"{j.Session},{j.StartUtc.Year},{jobId},{j.RecordingId},run-{j.RecordingId},{seq},{seq},{Ns(t)},MNQ,{j.Contract},{p.ToString("0.00", CultureInfo.InvariantCulture)},{v},{(isBuy ? "Buy" : "Sell")},,,0\n");
            }
            string rowText = rows.ToString();
            long b0 = offset, b1 = offset + Encoding.UTF8.GetByteCount(rowText);
            offset = b1;
            sb.Append(rowText);
            rowsTotal += j.Ticks.Count;
            jobDict[jobId] = new Dictionary<string, object?>
            {
                ["status"] = "committed", ["coverageStatus"] = "complete", ["recordingId"] = j.RecordingId, ["runId"] = "run-" + j.RecordingId,
                ["recordingDir"] = recDir, ["rowsAppended"] = j.Ticks.Count,
                ["window"] = new { start = Ns(j.StartUtc), end = Ns(j.EndUtc), interval = "[start, end)" },
                ["trades"] = new { rows = j.Ticks.Count, totalVolume = buy + sell, buyVolume = buy, sellVolume = sell, unknownVolume = 0 },
                ["reconciliation"] = new { summary_clean = j.Clean, tick_drops = new { total = j.Drops } },
                ["byteOffsetBefore"] = b0, ["byteOffsetAfter"] = b1
            };
        }
        File.WriteAllText(csv, sb.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, "collection_manifest.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = "atas-collection-trades-1", session, year = 2026, timezone,
            csv = new { path = csv, committedBytes = new FileInfo(csv).Length, committedRows = rowsTotal },
            jobs = jobDict, totals = new { rows = rowsTotal }, synthetic = "KEINE MARKTDATEN"
        }));
        return collections;
    }

    /// <summary>Deterministische Tick-Folge im Fenster [start, end): Zufallspfad mit gemessenem Aggressor (Seed fest).</summary>
    public static List<(DateTimeOffset, decimal, int, bool)> Ticks(DateTimeOffset start, DateTimeOffset end, int count, int seed, decimal startPrice = 20000m)
    {
        var rnd = new Random(seed);
        var list = new List<(DateTimeOffset, decimal, int, bool)>(count);
        double span = (end - start).TotalSeconds - 1;
        decimal price = startPrice;
        for (int i = 0; i < count; i++)
        {
            var t = start.AddSeconds(span * i / count).AddTicks(rnd.Next(0, 1000));
            bool buy = rnd.NextDouble() < 0.5 + 0.2 * Math.Sin(i / 40.0);
            price = Math.Max(1000m, price + (buy ? 0.25m : -0.25m) * rnd.Next(0, 3));
            list.Add((t, price, rnd.Next(1, 5), buy));
        }
        return list;
    }

    public static string Ns(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "." + ((t.UtcTicks % TimeSpan.TicksPerSecond) * 100).ToString("D9", CultureInfo.InvariantCulture) + "Z";

    public static DateTimeOffset Utc(string iso) => DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    /// <summary>Repo-Wurzel (für config/ der Profile und des Sessionplans).</summary>
    public static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "TradingBot.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("Repo-Wurzel nicht gefunden.");
    }

    /// <summary>Temp-„Repo“ mit eigenem Sessionplan (für Sonderfälle wie überlappende Fenster).</summary>
    public static string RepoWithSchedule(string json)
    {
        string root = NewRoot();
        Directory.CreateDirectory(Path.Combine(root, "config", "sessions"));
        File.WriteAllText(Path.Combine(root, "config", "sessions", "london-newyork.example.json"), json);
        return root;
    }
}
