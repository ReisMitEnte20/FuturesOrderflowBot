using FluentAssertions;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.MarketData.Import;
using TradingBot.Tests.Backtesting;
using Xunit;

namespace TradingBot.Tests.MarketData;

public class DataQualityTests
{
    private static TextReader R(string csv) => new StringReader(csv);

    // ----------------------------- Zeilen-Fehler -----------------------------

    [Fact]
    public void Negative_price_is_rejected_with_error()
    {
        var csv = """
        timestamp,symbol,price,volume
        2026-07-03T13:30:00Z,NQ,-5,3
        2026-07-03T13:30:01Z,NQ,20000.25,1
        """;

        var result = new AtasTickCsvImporter().Import(R(csv));

        result.Ticks.Should().HaveCount(1); // fehlerhafte Zeile verworfen
        result.Quality.RowsRead.Should().Be(2);
        result.Quality.RowsRejected.Should().Be(1);
        result.Quality.Issues.Should().Contain(i => i.Code == "NegativePrice" && i.Severity == DataQualitySeverity.Error);
    }

    [Fact]
    public void Negative_volume_is_rejected_with_error()
    {
        var csv = """
        timestamp,symbol,price,volume
        2026-07-03T13:30:00Z,NQ,20000,-3
        """;

        var result = new AtasTickCsvImporter().Import(R(csv));

        result.Ticks.Should().BeEmpty();
        result.Quality.Issues.Should().Contain(i => i.Code == "NegativeVolume");
    }

    [Fact]
    public void Non_chronological_ticks_are_detected_and_rejected()
    {
        var csv = """
        timestamp,symbol,price,volume
        2026-07-03T13:30:05Z,NQ,20000.00,1
        2026-07-03T13:30:01Z,NQ,20000.25,1
        """;

        var result = new AtasTickCsvImporter().Import(R(csv));

        result.Ticks.Should().HaveCount(1); // rückwärtige Zeile verworfen, nichts sortiert
        result.Quality.Issues.Should().Contain(i => i.Code == "NonChronological" && i.Severity == DataQualitySeverity.Error);
    }

    [Fact]
    public void Missing_timestamp_is_reported()
    {
        var csv = """
        timestamp,symbol,price,volume
        ,NQ,20000.00,1
        """;

        var result = new AtasTickCsvImporter().Import(R(csv));

        result.Ticks.Should().BeEmpty();
        result.Quality.Issues.Should().Contain(i => i.Code == "MissingTimestamp");
    }

    [Fact]
    public void Report_counts_and_severities_are_consistent()
    {
        var csv = """
        timestamp,symbol,price,volume
        2026-07-03T13:30:00Z,NQ,20000.00,1
        2026-07-03T13:30:01Z,NQ,-1,1
        2026-07-03T13:30:02Z,NQ,20000.50,abc
        """;

        var result = new AtasTickCsvImporter().Import(R(csv));

        result.Quality.RowsRead.Should().Be(3);
        result.Quality.RowsAccepted.Should().Be(1);
        result.Quality.RowsRejected.Should().Be(2);
        result.Quality.HasErrors.Should().BeTrue();
        result.Quality.Issues.Where(i => i.Severity == DataQualitySeverity.Error)
            .Should().HaveCountGreaterThanOrEqualTo(2);
        result.Quality.Issues.Should().OnlyContain(i => !string.IsNullOrWhiteSpace(i.Code));
    }

    // ----------------------------- Instrument-Abgleich -----------------------

    [Fact]
    public void Symbol_mismatch_against_instrument_is_error()
    {
        var csv = """
        timestamp,symbol,price,volume
        2026-07-03T13:30:00Z,ES,4000.00,1
        """;
        var data = new AtasTickCsvImporter().Import(R(csv));

        var issues = DataQualityChecks.CheckAgainstInstrument(data, BacktestTestData.Instrument()); // NQ

        issues.Should().Contain(i => i.Code == "SymbolMismatch" && i.Severity == DataQualitySeverity.Error);
    }

    [Fact]
    public void Price_not_aligned_to_tick_size_is_warning()
    {
        var csv = """
        timestamp,symbol,price,volume
        2026-07-03T13:30:00Z,NQ,20000.13,1
        """;
        var data = new AtasTickCsvImporter().Import(R(csv));

        var issues = DataQualityChecks.CheckAgainstInstrument(data, BacktestTestData.Instrument()); // TickSize 0.25

        issues.Should().Contain(i => i.Code == "PriceNotTickAligned" && i.Severity == DataQualitySeverity.Warning);
    }

    [Fact]
    public void Clean_data_produces_no_instrument_issues()
    {
        var csv = """
        timestamp,symbol,price,volume
        2026-07-03T13:30:00Z,NQ,20000.25,1
        """;
        var data = new AtasTickCsvImporter().Import(R(csv));

        var issues = DataQualityChecks.CheckAgainstInstrument(data, BacktestTestData.Instrument());

        issues.Where(i => i.Severity >= DataQualitySeverity.Warning).Should().BeEmpty();
    }

    // ----------------------------- Lücken ------------------------------------

    [Fact]
    public void Large_gap_is_reported_as_warning()
    {
        var t0 = new DateTimeOffset(2026, 7, 3, 13, 30, 0, TimeSpan.Zero);
        var timestamps = new List<DateTimeOffset>
        {
            t0, t0.AddSeconds(1), t0.AddSeconds(2), t0.AddSeconds(3),
            t0.AddSeconds(300), // riesige Lücke (>10x Median 1s)
            t0.AddSeconds(301)
        };

        var issues = DataQualityChecks.CheckGaps(timestamps);

        issues.Should().ContainSingle(i => i.Code == "DataGap" && i.Severity == DataQualitySeverity.Warning);
    }

    [Fact]
    public void Regular_data_has_no_gap_warnings()
    {
        var t0 = new DateTimeOffset(2026, 7, 3, 13, 30, 0, TimeSpan.Zero);
        var timestamps = Enumerable.Range(0, 20).Select(i => t0.AddSeconds(i)).ToList();

        DataQualityChecks.CheckGaps(timestamps).Should().BeEmpty();
    }

    // ----------------------------- Keine Fake-Daten --------------------------

    [Fact]
    public void Ohlcv_only_data_never_gets_orderflow_capabilities()
    {
        var csv = """
        timestamp,symbol,price,volume
        2026-07-03T13:30:00Z,NQ,20000.00,3
        """;

        var result = new AtasTickCsvImporter().Import(R(csv));

        result.Capabilities.SupportsDeltaCvd.Should().BeFalse();
        result.Capabilities.SupportsAbsorption.Should().BeFalse();
        result.Capabilities.SupportsBarImbalance.Should().BeFalse();
        result.Capabilities.SupportsStackedImbalances.Should().BeFalse();
        result.Capabilities.SupportsHvnLvn.Should().BeFalse();
        // Und die Ticks tragen keine erfundene Klassifikation:
        result.Ticks.Should().OnlyContain(t => t.Aggressor == AggressorSide.Unknown);
    }

    [Fact]
    public void OrderFlowBars_do_not_enable_footprint_or_profile_analyses()
    {
        var csv = """
        bartimestamp,symbol,open,high,low,close,volume,bidvolume,askvolume
        2026-07-03T13:30:00Z,NQ,20000,20010,19995,20005,100,40,60
        """;

        var result = new AtasOrderFlowBarCsvImporter().Import(R(csv));

        result.Capabilities.SupportsDeltaCvd.Should().BeTrue();          // Bid/Ask vorhanden
        result.Capabilities.SupportsStackedImbalances.Should().BeFalse(); // KEIN Footprint
        result.Capabilities.SupportsHvnLvn.Should().BeFalse();            // KEIN Volume Profile
    }
}
