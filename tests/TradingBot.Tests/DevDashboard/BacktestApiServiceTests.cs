using FluentAssertions;
using TradingBot.DevDashboard.Services;
using Xunit;

namespace TradingBot.Tests.DevDashboard;

/// <summary>
/// Prüft „Daten laden" (<see cref="BacktestApiService.LoadDataAsync"/>): liefert echte OHLC-Kerzen ohne
/// Strategielauf, respektiert den Zeitraum und meldet Fehler, statt Daten zu erfinden.
/// </summary>
public class BacktestApiServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("bt-api-").FullName;

    private string WriteCsv(string content)
    {
        string path = Path.Combine(_dir, "bars.csv");
        File.WriteAllText(path, content);
        return path;
    }

    private const string Csv =
        "timestamp,open,high,low,close,volume\n" +
        "2026-01-05T14:00:00Z,100.0,101.0,99.5,100.5,10\n" +
        "2026-01-05T14:05:00Z,100.5,102.0,100.0,101.5,12\n" +
        "2026-01-05T14:10:00Z,101.5,101.8,100.2,100.4,8\n" +
        "2026-01-05T14:15:00Z,100.4,100.9,99.9,100.8,9\n";

    [Fact]
    public async Task Load_data_returns_real_candles_without_running_a_strategy()
    {
        var svc = new BacktestApiService(_dir);
        var r = await svc.LoadDataAsync(new BacktestRunRequest { DataSourceId = "csv", Path = WriteCsv(Csv), Symbol = "MES", TimeframeMinutes = 5 });

        r.Ok.Should().BeTrue(r.Error);
        r.Candles.Should().HaveCount(4);
        r.Candles[1].O.Should().Be(100.5m);
        r.Candles[1].H.Should().Be(102.0m);
        r.Data!.BarCount.Should().Be(4);
        r.Data.Timezone.Should().Be("UTC");
        r.Data.From.Should().Be(new DateTimeOffset(2026, 1, 5, 14, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Load_data_respects_from_and_to_range()
    {
        var svc = new BacktestApiService(_dir);
        var r = await svc.LoadDataAsync(new BacktestRunRequest
        {
            DataSourceId = "csv", Path = WriteCsv(Csv), Symbol = "MES", TimeframeMinutes = 5,
            FromUtc = "2026-01-05 14:05", ToUtc = "2026-01-05 14:15"
        });

        r.Ok.Should().BeTrue(r.Error);
        r.Candles.Select(c => c.T).Should().Equal(
            new DateTimeOffset(2026, 1, 5, 14, 5, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            new DateTimeOffset(2026, 1, 5, 14, 10, 0, TimeSpan.Zero).ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Load_data_reports_errors_instead_of_inventing_data()
    {
        var svc = new BacktestApiService(_dir);

        var invalidRange = await svc.LoadDataAsync(new BacktestRunRequest
        {
            DataSourceId = "csv", Path = WriteCsv(Csv), Symbol = "MES", FromUtc = "2026-01-05 14:10", ToUtc = "2026-01-05 14:00"
        });
        invalidRange.Ok.Should().BeFalse();
        invalidRange.Error.Should().Contain("Zeitraum");
        invalidRange.Candles.Should().BeEmpty();

        var missing = await svc.LoadDataAsync(new BacktestRunRequest { DataSourceId = "csv", Path = Path.Combine(_dir, "fehlt.csv"), Symbol = "MES" });
        missing.Ok.Should().BeFalse();
        missing.Candles.Should().BeEmpty();

        var empty = await svc.LoadDataAsync(new BacktestRunRequest
        {
            DataSourceId = "csv", Path = WriteCsv(Csv), Symbol = "MES", FromUtc = "2027-01-01"
        });
        empty.Ok.Should().BeFalse();
        empty.Candles.Should().BeEmpty();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
