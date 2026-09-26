using FluentAssertions;
using TradingBot.Backtesting.Ohlc;
using Xunit;

namespace TradingBot.Tests.Backtesting;

/// <summary>Prüft den OHLC-CSV-Import: gültige Bars, OHLC-Konsistenz, Zeitordnung, Duplikate, Format.</summary>
public class OhlcCsvImporterTests
{
    private const string Header = "timestamp,open,high,low,close,volume";

    private static OhlcImportResult Import(string csv, string symbol = "MNQ")
        => new OhlcCsvImporter().Import(new StringReader(csv), symbol);

    [Fact]
    public void Imports_valid_ohlc_bars_and_infers_timeframe()
    {
        var csv = Header + "\n" +
            "2026-01-05T14:00:00Z,100.0,101.0,99.5,100.5,10\n" +
            "2026-01-05T14:05:00Z,100.5,102.0,100.0,101.5,12\n" +
            "2026-01-05T14:10:00Z,101.5,101.8,100.2,100.4,8\n";
        var r = Import(csv);

        r.Candles.Should().HaveCount(3);
        r.ValidRows.Should().Be(3);
        r.Rejected.Should().Be(0);
        r.HasVolume.Should().BeTrue();
        r.TimeframeMinutes.Should().Be(5);
        r.Candles[0].Open.Should().Be(100.0m);
        r.Candles[0].High.Should().Be(101.0m);
        r.Candles[0].CloseTime.Should().Be(r.Candles[0].OpenTime.AddMinutes(5));
    }

    [Fact]
    public void Rejects_inconsistent_ohlc_without_inventing_data()
    {
        // High < Close -> inkonsistent, muss abgelehnt (nicht stillschweigend korrigiert) werden.
        var csv = Header + "\n" +
            "2026-01-05T14:00:00Z,100.0,101.0,99.5,100.5,10\n" +
            "2026-01-05T14:05:00Z,100.5,100.6,100.0,101.5,12\n";   // High 100.6 < Close 101.5
        var r = Import(csv);

        r.ValidRows.Should().Be(1);
        r.Rejected.Should().Be(1);
        r.Issues.Should().Contain(i => i.Code == "OhlcInconsistent");
    }

    [Fact]
    public void Rejects_non_chronological_and_duplicate_timestamps()
    {
        var csv = Header + "\n" +
            "2026-01-05T14:05:00Z,100.5,102.0,100.0,101.5,12\n" +
            "2026-01-05T14:00:00Z,100.0,101.0,99.5,100.5,10\n" +   // vor Vorgänger
            "2026-01-05T14:05:00Z,101.0,101.2,100.8,101.0,5\n";    // Duplikat
        var r = Import(csv);

        r.ValidRows.Should().Be(1);
        r.Rejected.Should().Be(2);
        r.Issues.Select(i => i.Code).Should().Contain(new[] { "NonChronological", "DuplicateTimestamp" });
    }

    [Fact]
    public void Missing_required_column_throws()
    {
        var csv = "timestamp,open,high,close\n2026-01-05T14:00:00Z,100,101,100\n";
        var act = () => Import(csv);
        act.Should().Throw<InvalidDataException>().WithMessage("*low*");
    }

    [Fact]
    public void Supports_separate_date_and_time_columns_without_volume()
    {
        var csv = "date,time,open,high,low,close\n" +
            "2026-01-05,14:00:00,100.0,101.0,99.5,100.5\n" +
            "2026-01-05,14:01:00,100.5,101.2,100.1,100.8\n";
        var r = Import(csv);

        r.Candles.Should().HaveCount(2);
        r.HasVolume.Should().BeFalse();
        r.TimeframeMinutes.Should().Be(1);
    }
}
