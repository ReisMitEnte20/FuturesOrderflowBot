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

    [Fact]
    public async Task Collections_root_prefers_drop_free_recording_over_alphabetically_first_and_skips_foreign_folders()
    {
        string root = Path.Combine(Path.GetTempPath(), "tickreplay-tests", Guid.NewGuid().ToString("N"), "Collections");
        var (_, csvA, _) = Fixture(root: root, name: "A_MNQ_NewYork", drops: 36391, recording: "recA");
        var (_, csvB, _) = Fixture(root: root, name: "B_MNQ_NewYork", day2Id: "day3", day2Start: "2026-10-28T13:30:00.000000000Z", drops: 0, recording: "recB");
        Directory.CreateDirectory(Path.Combine(root, "Leer"));                                   // ohne Manifest
        Directory.CreateDirectory(Path.Combine(root, "Kaputt"));
        File.WriteAllText(Path.Combine(root, "Kaputt", "collection_manifest.json"), "{ nicht json");  // unlesbar
        string cacheDir = Path.Combine(Path.GetDirectoryName(root)!, "cache");
        string shaA = Sha(csvA), shaB = Sha(csvB);

        var svc = TickReplayService.ForCollectionsRoot(root, cacheDir);
        var list = svc.ListSessions();
        Assert.True(list.Available);
        Assert.Equal(root, list.CollectionDir);
        Assert.Empty(list.Conflicts!);
        Assert.Equal(new[] { "2026-10-26", "2026-10-27", "2026-10-28" }, list.Sessions.Select(s => s.Day));
        var d1 = list.Sessions[0];
        Assert.Equal("B_MNQ_NewYork", d1.Collection);          // NICHT der alphabetisch erste Ordner (der hat Drops)
        Assert.Equal(Day1, d1.Id);
        Assert.Equal("resolved", d1.Selection);
        Assert.Contains("36391 Recorder-Drops", d1.SelectionNote);
        Assert.Equal(2, d1.Candidates!.Count);
        Assert.Equal(0, d1.RecorderDrops);

        var meta = await svc.PrepareAsync(Day1, CancellationToken.None);
        Assert.Equal(5, meta.Count);
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "B_MNQ_NewYork", "collection_manifest.json")), meta.ManifestPath);
        Assert.True(File.Exists(Path.Combine(cacheDir, "B_MNQ_NewYork", Day1 + ".bin")));
        Assert.Equal(1, (await svc.PrepareAsync("day3", CancellationToken.None)).Count);
        Assert.Equal("wiederverwendet", (await svc.PrepareAsync(Day1, CancellationToken.None)).Cache);
        Assert.Equal(shaA, Sha(csvA));
        Assert.Equal(shaB, Sha(csvB));
    }

    [Fact]
    public async Task Two_drop_free_different_recordings_are_an_open_conflict_never_chosen_silently()
    {
        string root = Path.Combine(Path.GetTempPath(), "tickreplay-tests", Guid.NewGuid().ToString("N"), "Collections");
        Fixture(root: root, name: "A_MNQ_NewYork", drops: 0, recording: "recA");
        Fixture(root: root, name: "B_MNQ_NewYork", drops: 0, recording: "recB", day2Id: "day3", day2Start: "2026-10-28T13:30:00.000000000Z");
        var svc = TickReplayService.ForCollectionsRoot(root, Path.Combine(Path.GetDirectoryName(root)!, "cache"));
        var list = svc.ListSessions();
        Assert.Equal(new[] { "2026-10-26" }, list.Conflicts);
        var both = list.Sessions.Where(s => s.JobId == Day1).ToList();
        Assert.Equal(new[] { Day1 + "@A_MNQ_NewYork", Day1 + "@B_MNQ_NewYork" }, both.Select(s => s.Id));
        Assert.All(both, s => Assert.Equal("conflict", s.Selection));
        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.PrepareAsync(Day1, CancellationToken.None));
        Assert.Contains("Konflikt", ex.Message);
        var b = await svc.PrepareAsync(Day1 + "@B_MNQ_NewYork", CancellationToken.None);
        Assert.Equal("B_MNQ_NewYork", b.Collection);
        Assert.Equal("conflict", b.Selection);
    }

    [Fact]
    public void Same_recording_in_two_collections_is_resolved_without_conflict()
    {
        string root = Path.Combine(Path.GetTempPath(), "tickreplay-tests", Guid.NewGuid().ToString("N"), "Collections");
        Fixture(root: root, name: "A_MNQ_NewYork", drops: 0);
        Fixture(root: root, name: "B_MNQ_NewYork", drops: 0, day2Id: "day3", day2Start: "2026-10-28T13:30:00.000000000Z");
        var list = TickReplayService.ForCollectionsRoot(root, Path.Combine(Path.GetDirectoryName(root)!, "cache")).ListSessions();
        var d1 = Assert.Single(list.Sessions, s => s.JobId == Day1);
        Assert.Equal("resolved", d1.Selection);
        Assert.Equal("A_MNQ_NewYork", d1.Collection);
        Assert.Contains("dieselbe Aufnahme", d1.SelectionNote);
    }

    [Fact]
    public async Task Day_slice_import_reads_only_the_job_bytes_and_cache_survives_appends_but_not_edits()
    {
        var (svc, csv, cacheDir) = Fixture(offsets: true, drops: 0);
        var first = await svc.PrepareAsync(Day1, CancellationToken.None);
        Assert.Equal("aufgebaut", first.Cache);
        Assert.Equal(5, first.Count);
        Assert.StartsWith("[0, ", first.SliceBytes);
        Assert.Equal(64, first.SliceSha256.Length);

        // Collector haengt einen neuen Tag an (Datei waechst): nur der Abschnitt wird neu gehasht, Cache bleibt gueltig.
        File.AppendAllText(csv, "dayX,recX,runX,2026-10-29T13:30:00.000000000Z,MNQ,20010,1,Buy\n");
        // Das Anhaengen liegt eindeutig VOR dem folgenden Vermerk (ausserhalb des Racy-Fensters) – sonst wuerde zu Recht
        // neu gehasht statt „sofort gueltig“ (deterministisch statt abhaengig von der Zeitstempel-Aufloesung).
        File.SetLastWriteTimeUtc(csv, DateTime.UtcNow.AddMinutes(-5));
        Assert.StartsWith("wiederverwendet (Abschnitt", (await svc.PrepareAsync(Day1, CancellationToken.None)).Cache);
        // Neustart (neue Instanz): Dateistand wurde im Cache-Kopf vermerkt -> sofort gueltig, kein erneutes Hashen.
        var restarted = new TickReplayService(Path.GetDirectoryName(csv)!, cacheDir);
        Assert.Equal("wiederverwendet", (await restarted.PrepareAsync(Day1, CancellationToken.None)).Cache);

        // Aenderung INNERHALB des Abschnitts (gleiche Laenge) wird erkannt -> Neuaufbau mit den neuen Werten.
        File.WriteAllText(csv, File.ReadAllText(csv).Replace("20002.50", "20002.75"));
        var rebuilt = await restarted.PrepareAsync(Day1, CancellationToken.None);
        Assert.Equal("aufgebaut", rebuilt.Cache);
        Assert.Equal(20002.75, Decode(restarted.ReadTicks(Day1, 3, 1)).Price[0]);
    }

    [Theory]
    [InlineData(true)]   // Tagesabschnitt (Byte-Offsets im Manifest)
    [InlineData(false)]  // Ganzdatei-Modus
    public async Task Same_length_edit_with_unchanged_write_time_is_detected_inside_the_racy_window(bool offsets)
    {
        // Ursache des frueheren Flackerns, hier erzwungen: gleiche Laenge UND gleiche Schreibzeit wie im Vermerk.
        var (svc, csv, cacheDir) = Fixture(offsets: offsets, drops: 0);
        Assert.Equal("aufgebaut", (await svc.PrepareAsync(Day1, CancellationToken.None)).Cache);
        var stamp = File.GetLastWriteTimeUtc(csv);
        File.WriteAllText(csv, File.ReadAllText(csv).Replace("20002.50", "20002.75"));
        File.SetLastWriteTimeUtc(csv, stamp);
        var fresh = new TickReplayService(Path.GetDirectoryName(csv)!, cacheDir);
        var r = await fresh.PrepareAsync(Day1, CancellationToken.None);
        Assert.Equal("aufgebaut", r.Cache);
        Assert.Equal(20002.75, Decode(fresh.ReadTicks(Day1, 3, 1)).Price[0]);
    }

    [Fact]
    public void Day_slice_import_fails_closed_on_foreign_rows_or_misaligned_offsets()
    {
        var (_, csv, _) = Fixture(offsets: true, drops: 0);
        string manifest = Path.Combine(Path.GetDirectoryName(csv)!, "collection_manifest.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifest))!;
        long fileLen = new FileInfo(csv).Length;
        node["jobs"]![Day1]!["byteOffsetAfter"] = fileLen;                    // schliesst die Zeile von day2 ein
        File.WriteAllText(manifest, node.ToJsonString());
        var imp = new TradingBot.Infrastructure.MarketData.Import.AtasCollectionImporter();
        Assert.Throws<InvalidDataException>(() => imp.ImportJobSlice(manifest, Day1));
        node["jobs"]![Day1]!["byteOffsetAfter"] = fileLen - 3;                // mitten in einer Zeile
        File.WriteAllText(manifest, node.ToJsonString());
        Assert.Throws<InvalidDataException>(() => imp.ImportJobSlice(manifest, Day1));
    }

    [Fact]
    public void Collections_root_missing_is_reported_not_thrown()
    {
        string root = Path.Combine(Path.GetTempPath(), "tickreplay-tests", Guid.NewGuid().ToString("N"), "fehlt");
        var list = TickReplayService.ForCollectionsRoot(root, Path.Combine(root, "cache")).ListSessions();
        Assert.False(list.Available);
        Assert.Contains("nicht gefunden", list.Reason);
        Assert.Empty(list.Sessions);
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

    private static (TickReplayService Svc, string Csv, string CacheDir) Fixture(decimal day1Buy = 7m, string? root = null,
        string name = "MNQ_NewYork_fixture", string day2Id = "day2", string day2Start = "2026-10-27T13:30:00.000000000Z",
        bool offsets = false, long? drops = null, string recording = "rec")
    {
        // Wie die bestehenden Importer-Tests: Fixtures bleiben im Temp-Ordner erhalten (keine Loeschungen).
        string dir = root ?? Path.Combine(Path.GetTempPath(), "tickreplay-tests", Guid.NewGuid().ToString("N"));
        string collection = Path.Combine(dir, name);
        string day2End = day2Start[..11] + "16:00:00.000000000Z";
        string cacheDir = Path.Combine(dir, "cache");
        Directory.CreateDirectory(collection);
        string csv = Path.Combine(collection, "trades.csv");
        string header = "job_id,recording_id,run_id,t_market_utc,instrument,price,volume,aggressor_side\n";
        string day1Rows = $"{Day1},{recording},run,2026-10-26T13:30:00.000000000Z,MNQ,20000.25,2,Buy\n"
            + $"{Day1},{recording},run,2026-10-26T13:30:00.000000000Z,MNQ,20000.25,2,Buy\n"
            + $"{Day1},{recording},run,2026-10-26T13:34:59.999999999Z,MNQ,20001.00,1,Sell\n"
            + $"{Day1},{recording},run,2026-10-26T13:35:00.000000000Z,MNQ,20002.50,3,Buy\n"
            + $"{Day1},{recording},run,2026-10-26T15:59:59.975000000Z,MNQ,19999.75,1,Sell\n";
        string day2Row = $"{day2Id},rec2,run2,{day2Start},MNQ,20001,1,Sell\n";
        File.WriteAllText(csv, header + day1Rows + day2Row);
        long day1End = header.Length + day1Rows.Length, day2EndByte = day1End + day2Row.Length;
        string? recDir = null;
        if (drops is { } d)
        {
            recDir = Path.Combine(dir, "Recordings", name + "_" + recording);
            Directory.CreateDirectory(recDir);
            File.WriteAllText(Path.Combine(recDir, "summary.json"), $"{{\"totalDrops\": {d}, \"clean\": true}}");
        }
        Dictionary<string, object?> Job(string from, string end, int count, string rec, string run, decimal buy, decimal sell, long b0, long b1)
        {
            var j = new Dictionary<string, object?>
            {
                ["status"] = "committed", ["coverageStatus"] = "complete", ["recordingId"] = rec, ["runId"] = run,
                ["window"] = new { start = from, end, interval = "[start, end)" }, ["rowsAppended"] = count,
                ["trades"] = new { rows = count, totalVolume = buy + sell, buyVolume = buy, sellVolume = sell, unknownVolume = 0 },
            };
            if (offsets) { j["byteOffsetBefore"] = b0; j["byteOffsetAfter"] = b1; }
            if (recDir is not null) { j["recordingDir"] = recDir; j["reconciliation"] = new { summary_clean = true }; }
            return j;
        }
        File.WriteAllText(Path.Combine(collection, "collection_manifest.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = "atas-collection-trades-1", session = "NewYork", year = 2026, timezone = "America/New_York",
            csv = new { path = csv, committedBytes = new FileInfo(csv).Length, committedRows = 6 },
            jobs = new Dictionary<string, object>
            {
                [Day1] = Job("2026-10-26T13:30:00.000000000Z", "2026-10-26T16:00:00.000000000Z", 5, recording, "run", day1Buy, 2, 0, day1End),
                [day2Id] = Job(day2Start, day2End, 1, "rec2", "run2", 0, 1, day1End, day2EndByte)
            },
            totals = new { rows = 6 }
        }));
        return (new TickReplayService(collection, cacheDir), csv, cacheDir);
    }
}
