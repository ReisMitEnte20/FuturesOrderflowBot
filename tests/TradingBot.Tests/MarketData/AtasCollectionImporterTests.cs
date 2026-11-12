using System.Text.Json;
using TradingBot.Infrastructure.MarketData.Import;

namespace TradingBot.Tests.MarketData;

public sealed class AtasCollectionImporterTests
{
    [Fact]
    public void Selects_day_preserves_duplicates_and_reconciles_volume()
    {
        var result = new AtasCollectionImporter().ImportDay(Fixture(), new DateOnly(2026, 10, 26));
        Assert.Equal(2, result.Data.Ticks.Count);
        Assert.Equal(4m, result.Data.Ticks.Sum(t => t.Volume));
        Assert.Equal(result.Data.Ticks[0], result.Data.Ticks[1]);
        Assert.True(result.Data.Capabilities.SupportsDeltaCvd);
        Assert.Equal("2026-10-26T13:30:00.000000000Z", result.Evidence["window.fromUtc"]);
        Assert.Equal("4", result.Evidence["import.buyVolume"]);
    }

    [Theory]
    [InlineData("Unknown", "2026-10-26T13:30:00.000000001Z", 4, 2)]
    [InlineData("Buy", "2026-10-26T16:00:00.000000000Z", 4, 2)]
    [InlineData("Buy", "2026-10-26T13:29:59.999999999Z", 4, 2)]
    [InlineData("Buy", "2026-10-26T13:30:00.000000001Z", 5, 2)]
    [InlineData("Buy", "2026-10-26T13:30:00.000000001Z", 4, 3)]
    public void Rejects_classification_window_volume_or_row_mismatch(string side, string time, int volume, int rows)
    {
        Assert.Throws<InvalidDataException>(() => new AtasCollectionImporter().ImportDay(
            Fixture(side, time, volume, rows), new DateOnly(2026, 10, 26)));
    }

    [Fact]
    public void Rejects_backward_nanoseconds_even_within_same_engine_tick()
    {
        Assert.Throws<InvalidDataException>(() => new AtasCollectionImporter().ImportDay(
            Fixture(secondTime: "2026-10-26T13:30:00.000000000Z"), new DateOnly(2026, 10, 26)));
    }

    [Fact]
    public void Rejects_legacy_window()
    {
        Assert.Throws<InvalidDataException>(() => new AtasCollectionImporter().ImportDay(
            Fixture(start: "2026-10-26T14:30:00.000000000Z"), new DateOnly(2026, 10, 26)));
    }

    private static string Fixture(string side = "Buy", string time = "2026-10-26T13:30:00.000000001Z",
        int volume = 4, int rows = 2, string? secondTime = null, string start = "2026-10-26T13:30:00.000000000Z")
    {
        // Retained under the OS temp folder: this task explicitly forbids deletion.
        string dir = Path.Combine(Path.GetTempPath(), "atas-collection-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string csv = Path.Combine(dir, "trades.csv");
        File.WriteAllText(csv, "job_id,recording_id,run_id,t_market_utc,instrument,price,volume,aggressor_side\n"
            + $"day1,rec,run,{time},MNQ,20000.25,2,{side}\n"
            + $"day1,rec,run,{secondTime ?? time},MNQ,20000.25,2,{side}\n"
            + "day2,rec2,run2,2026-10-27T13:30:00.000000000Z,MNQ,20001,1,Sell\n");
        object Job(string from, string end, int count, string recording, string run, decimal buy, decimal sell) => new
        {
            status = "committed", coverageStatus = "complete", recordingId = recording, runId = run,
            window = new { start = from, end, interval = "[start, end)" }, rowsAppended = count,
            trades = new { rows = count, totalVolume = buy + sell, buyVolume = buy, sellVolume = sell, unknownVolume = 0 }
        };
        string manifest = Path.Combine(dir, "collection_manifest.json");
        File.WriteAllText(manifest, JsonSerializer.Serialize(new
        {
            schemaVersion = "atas-collection-trades-1", session = "NewYork", year = 2026, timezone = "Eastern Standard Time",
            csv = new { path = csv, committedBytes = new FileInfo(csv).Length, committedRows = rows + 1 },
            jobs = new Dictionary<string, object>
            {
                ["day1"] = Job(start, "2026-10-26T16:00:00.000000000Z", rows, "rec", "run", volume, 0),
                ["day2"] = Job("2026-10-27T13:30:00.000000000Z", "2026-10-27T16:00:00.000000000Z", 1, "rec2", "run2", 0, 1)
            }, totals = new { rows = rows + 1 }
        }));
        return manifest;
    }
}
