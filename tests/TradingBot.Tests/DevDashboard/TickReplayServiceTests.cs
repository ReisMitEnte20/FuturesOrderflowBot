using System.Security.Cryptography;
using System.Text.Json;
using TradingBot.DevDashboard.Services;

namespace TradingBot.Tests.DevDashboard;

/// <summary>Tick-Replay-Quelle: exakte Zeiteinheiten an 5-Minuten-Grenzen, keine Deduplizierung, portionsweise Abschnitte,
/// Cache-Wiederverwendung/-Invalidierung, Originale unveraendert, fail-closed ueber den bestehenden Importer.</summary>
public sealed class TickReplayServiceTests
{
    private const string Day1 = "NewYork_2026_20261026T133000Z_20261026T160000Z";

    [Fact]
    public void Lists_supported_sessions_with_new_york_labels()
    {
        var (svc, _, _) = Fixture();
        var list = svc.ListSessions();
        Assert.True(list.Available);
        Assert.Equal(2, list.Sessions.Count);
        Assert.Equal("2026-10-26", list.Sessions[0].Day);
        Assert.Contains("26.10.2026", list.Sessions[0].Label);
        Assert.Contains("EDT", list.Sessions[0].Label);
        Assert.Equal(5, list.Sessions[0].Rows);
    }

    [Fact]
    public async Task Prepares_exact_units_keeps_duplicates_and_original_order()
    {
        var (svc, _, _) = Fixture();
        var meta = await svc.PrepareAsync(Day1, CancellationToken.None);
        Assert.Equal("aufgebaut", meta.Cache);
        Assert.Equal(5, meta.Count);
        Assert.Equal(0, meta.WindowStartUnits);
        Assert.Equal(90_000_000_000L, meta.WindowEndUnits); // 2,5 h in 100 ns
        Assert.Equal(3_000_000_000L, meta.BucketUnits);
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 13, 30, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(), meta.AnchorUnixMs);
        Assert.Equal(7m, meta.BuyVolume);
        Assert.Equal(2m, meta.SellVolume);

        var rows = Decode(svc.ReadTicks(Day1, 0, 100));
        Assert.Equal(new double[] { 0, 0, 2_999_999_999, 3_000_000_000, 89_999_750_000 }, rows.T);
        // 13:34:59.999999999 wird auf 100 ns abgerundet und bleibt in der ALTEN Kerze; 13:35:00 beginnt die NEUE.
        Assert.Equal(0, Math.Floor(rows.T[2] / meta.BucketUnits));
        Assert.Equal(1, Math.Floor(rows.T[3] / meta.BucketUnits));
        Assert.Equal(new double[] { 20000.25, 20000.25, 20001.00, 20002.50, 19999.75 }, rows.Price);
        Assert.Equal(new[] { 2, 2, 1, 3, 1 }, rows.Vol);
        Assert.Equal(new sbyte[] { 1, 1, -1, 1, -1 }, rows.Side);
    }

    [Fact]
    public async Task Serves_slices_and_rejects_out_of_range()
    {
        var (svc, _, _) = Fixture();
        await svc.PrepareAsync(Day1, CancellationToken.None);
        var slice = Decode(svc.ReadTicks(Day1, 3, 100));
        Assert.Equal(new double[] { 3_000_000_000, 89_999_750_000 }, slice.T);
        Assert.Empty(Decode(svc.ReadTicks(Day1, 5, 10)).T);
        Assert.Throws<ArgumentOutOfRangeException>(() => svc.ReadTicks(Day1, 6, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => svc.ReadTicks(Day1, 0, 0));
    }

    [Fact]
    public async Task Unknown_or_unprepared_sessions_are_rejected()
    {
        var (svc, _, _) = Fixture();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.PrepareAsync("nope", CancellationToken.None));
        Assert.Throws<KeyNotFoundException>(() => svc.ReadTicks("nope", 0, 1));
        Assert.Throws<InvalidOperationException>(() => svc.ReadTicks(Day1, 0, 1));
    }

    [Fact]
    public async Task Reuses_cache_and_rebuilds_after_source_change_or_corruption_without_touching_originals()
    {
        var (svc, csv, cacheDir) = Fixture();
        string before = Sha(csv);
        Assert.Equal("aufgebaut", (await svc.PrepareAsync(Day1, CancellationToken.None)).Cache);
        Assert.Equal("wiederverwendet", (await svc.PrepareAsync(Day1, CancellationToken.None)).Cache);

        File.SetLastWriteTimeUtc(csv, File.GetLastWriteTimeUtc(csv).AddMinutes(1));
        Assert.Equal("aufgebaut", (await svc.PrepareAsync(Day1, CancellationToken.None)).Cache);

        var cacheFile = Directory.GetFiles(cacheDir, "*.bin", SearchOption.AllDirectories).Single(f => f.Contains("20261026"));
        File.WriteAllBytes(cacheFile, new byte[] { 1, 2, 3 });
        Assert.Equal("aufgebaut", (await svc.PrepareAsync(Day1, CancellationToken.None)).Cache);
        Assert.Equal(5, Decode(svc.ReadTicks(Day1, 0, 100)).T.Length);
        Assert.Equal(before, Sha(csv));
    }

    [Fact]
    public async Task Fails_closed_through_existing_importer_on_volume_mismatch()
    {
        var (svc, _, _) = Fixture(day1Buy: 8m);
        await Assert.ThrowsAsync<InvalidDataException>(() => svc.PrepareAsync(Day1, CancellationToken.None));
    }

    // ------------------------------------------------------------------------------------------------------------

    private sealed record Rows(double[] T, double[] Price, int[] Vol, sbyte[] Side);

    private static Rows Decode(byte[] cols)
    {
        Assert.Equal(0, cols.Length % TickReplayService.RowBytes);
        int n = cols.Length / TickReplayService.RowBytes;
        var t = new double[n]; var p = new double[n]; var v = new int[n]; var s = new sbyte[n];
        for (int i = 0; i < n; i++)
        {
            t[i] = BitConverter.ToDouble(cols, i * 8);
            p[i] = BitConverter.ToDouble(cols, 8 * n + i * 8);
            v[i] = BitConverter.ToInt32(cols, 16 * n + i * 4);
            s[i] = unchecked((sbyte)cols[20 * n + i]);
        }
        return new Rows(t, p, v, s);
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static (TickReplayService Svc, string Csv, string CacheDir) Fixture(decimal day1Buy = 7m)
    {
        // Wie die bestehenden Importer-Tests: Fixtures bleiben im Temp-Ordner erhalten (keine Loeschungen).
        string dir = Path.Combine(Path.GetTempPath(), "tickreplay-tests", Guid.NewGuid().ToString("N"));
        string collection = Path.Combine(dir, "MNQ_NewYork_fixture");
        string cacheDir = Path.Combine(dir, "cache");
        Directory.CreateDirectory(collection);
        string csv = Path.Combine(collection, "trades.csv");
        File.WriteAllText(csv, "job_id,recording_id,run_id,t_market_utc,instrument,price,volume,aggressor_side\n"
            + $"{Day1},rec,run,2026-10-26T13:30:00.000000000Z,MNQ,20000.25,2,Buy\n"
            + $"{Day1},rec,run,2026-10-26T13:30:00.000000000Z,MNQ,20000.25,2,Buy\n"
            + $"{Day1},rec,run,2026-10-26T13:34:59.999999999Z,MNQ,20001.00,1,Sell\n"
            + $"{Day1},rec,run,2026-10-26T13:35:00.000000000Z,MNQ,20002.50,3,Buy\n"
            + $"{Day1},rec,run,2026-10-26T15:59:59.975000000Z,MNQ,19999.75,1,Sell\n"
            + "day2,rec2,run2,2026-10-27T13:30:00.000000000Z,MNQ,20001,1,Sell\n");
        object Job(string from, string end, int count, string recording, string run, decimal buy, decimal sell) => new
        {
            status = "committed", coverageStatus = "complete", recordingId = recording, runId = run,
            window = new { start = from, end, interval = "[start, end)" }, rowsAppended = count,
            trades = new { rows = count, totalVolume = buy + sell, buyVolume = buy, sellVolume = sell, unknownVolume = 0 }
        };
        File.WriteAllText(Path.Combine(collection, "collection_manifest.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = "atas-collection-trades-1", session = "NewYork", year = 2026, timezone = "America/New_York",
            csv = new { path = csv, committedBytes = new FileInfo(csv).Length, committedRows = 6 },
            jobs = new Dictionary<string, object>
            {
                [Day1] = Job("2026-10-26T13:30:00.000000000Z", "2026-10-26T16:00:00.000000000Z", 5, "rec", "run", day1Buy, 2),
                ["day2"] = Job("2026-10-27T13:30:00.000000000Z", "2026-10-27T16:00:00.000000000Z", 1, "rec2", "run2", 0, 1)
            },
            totals = new { rows = 6 }
        }));
        return (new TickReplayService(collection, cacheDir), csv, cacheDir);
    }
}
