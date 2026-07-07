using FluentAssertions;
using TradingBot.Domain.Enums;
using TradingBot.Infrastructure.MarketData;
using TradingBot.Infrastructure.MarketData.Import;
using Xunit;

namespace TradingBot.Tests.MarketData;

public class DataImportTests
{
    private static TextReader R(string csv) => new StringReader(csv);

    // ----------------------------- Ticks (A + B) -----------------------------

    [Fact]
    public void Minimal_tick_csv_imports_without_orderflow_capabilities()
    {
        var csv = """
        timestamp,symbol,price,volume
        2026-07-03T13:30:00Z,NQ,20000.00,3
        2026-07-03T13:30:01Z,NQ,20000.25,1
        """;

        var result = new AtasTickCsvImporter().Import(R(csv));

        result.SourceType.Should().Be(MarketDataSourceType.MinimalTick);
        result.Ticks.Should().HaveCount(2);
        result.Quality.RowsRead.Should().Be(2);
        result.Quality.RowsAccepted.Should().Be(2);
        result.Quality.HasErrors.Should().BeFalse();
        // OHLCV-only: KEINE Orderflow-Analysen erlaubt.
        result.Capabilities.Should().Be(Domain.Models.OrderFlowCapabilities.None);
    }

    [Fact]
    public void Aggressor_tick_csv_enables_delta_cvd()
    {
        var csv = """
        timestamp,symbol,price,volume,tradedirection
        2026-07-03T13:30:00Z,NQ,20000.00,3,buy
        2026-07-03T13:30:01Z,NQ,20000.25,1,sell
        """;

        var result = new AtasTickCsvImporter().Import(R(csv));

        result.SourceType.Should().Be(MarketDataSourceType.AggressorTick);
        result.Capabilities.SupportsDeltaCvd.Should().BeTrue();
        result.Capabilities.SupportsAbsorption.Should().BeTrue();
        result.Capabilities.SupportsStackedImbalances.Should().BeFalse(); // kein Footprint
        result.Capabilities.SupportsHvnLvn.Should().BeFalse();            // kein Volume Profile
    }

    [Fact]
    public void Partially_classified_ticks_disable_orderflow_capabilities()
    {
        var csv = """
        timestamp,symbol,price,volume,tradedirection
        2026-07-03T13:30:00Z,NQ,20000.00,3,buy
        2026-07-03T13:30:01Z,NQ,20000.25,1,unknown
        """;

        var result = new AtasTickCsvImporter().Import(R(csv));

        result.Capabilities.SupportsDeltaCvd.Should().BeFalse(); // alles-oder-nichts
        result.Quality.Issues.Should().Contain(i =>
            i.Code == "PartialClassification" && i.Severity == DataQualitySeverity.Warning);
    }

    [Fact]
    public void Custom_column_mapping_profile_maps_atas_style_headers()
    {
        // Simulierte "ATAS-Style"-Header - reine Konfiguration, kein Code-Change.
        var csv = """
        Time,Instrument,Last,Qty,Dir
        2026-07-03T13:30:00Z,NQ,20000.00,3,buy
        """;
        var profile = new CsvImportProfile
        {
            SourceType = MarketDataSourceType.AggressorTick,
            ColumnMap = new Dictionary<string, string>
            {
                ["timestamp"] = "Time", ["symbol"] = "Instrument",
                ["price"] = "Last", ["volume"] = "Qty", ["tradedirection"] = "Dir"
            }
        };

        var result = new AtasTickCsvImporter(profile).Import(R(csv));

        result.Ticks.Should().HaveCount(1);
        result.Ticks[0].Aggressor.Should().Be(AggressorSide.Buy);
        result.Capabilities.SupportsDeltaCvd.Should().BeTrue();
    }

    // ----------------------------- OrderFlowBars (C) -------------------------

    private const string ValidBarCsv = """
        bartimestamp,symbol,open,high,low,close,volume,bidvolume,askvolume
        2026-07-03T13:30:00Z,NQ,20000,20010,19995,20005,100,40,60
        2026-07-03T13:31:00Z,NQ,20005,20015,20000,20010,80,30,50
        """;

    [Fact]
    public void OrderFlowBar_csv_imports_and_derives_cvd()
    {
        var result = new AtasOrderFlowBarCsvImporter().Import(R(ValidBarCsv));

        result.OrderFlowBars.Should().HaveCount(2);
        result.OrderFlowBars[0].Delta.Should().Be(20m);              // 60 - 40
        result.OrderFlowBars[0].CumulativeDelta.Should().Be(20m);
        result.OrderFlowBars[1].CumulativeDelta.Should().Be(40m);    // 20 + 20
        result.Quality.Issues.Should().Contain(i => i.Code == "CvdDerived" && i.Severity == DataQualitySeverity.Info);
        result.Capabilities.SupportsDeltaCvd.Should().BeTrue();
        result.Capabilities.SupportsStackedImbalances.Should().BeFalse();
    }

    [Fact]
    public void Bar_with_bidask_sum_mismatch_is_rejected()
    {
        var csv = """
        bartimestamp,symbol,open,high,low,close,volume,bidvolume,askvolume
        2026-07-03T13:30:00Z,NQ,20000,20010,19995,20005,100,40,55
        """;

        var result = new AtasOrderFlowBarCsvImporter().Import(R(csv));

        result.OrderFlowBars.Should().BeEmpty();
        result.Quality.RowsRejected.Should().Be(1);
        result.Quality.Issues.Should().Contain(i => i.Code == "BidAskSumMismatch" && i.Severity == DataQualitySeverity.Error);
    }

    [Fact]
    public void Bar_with_wrong_delta_column_is_rejected()
    {
        var csv = """
        bartimestamp,symbol,open,high,low,close,volume,bidvolume,askvolume,delta
        2026-07-03T13:30:00Z,NQ,20000,20010,19995,20005,100,40,60,99
        """;

        var result = new AtasOrderFlowBarCsvImporter().Import(R(csv));

        result.OrderFlowBars.Should().BeEmpty();
        result.Quality.Issues.Should().Contain(i => i.Code == "DeltaMismatch");
    }

    [Fact]
    public void Duplicate_bar_timestamps_are_rejected()
    {
        var csv = """
        bartimestamp,symbol,open,high,low,close,volume,bidvolume,askvolume
        2026-07-03T13:30:00Z,NQ,20000,20010,19995,20005,100,40,60
        2026-07-03T13:30:00Z,NQ,20005,20015,20000,20010,80,30,50
        """;

        var result = new AtasOrderFlowBarCsvImporter().Import(R(csv));

        result.OrderFlowBars.Should().HaveCount(1);
        result.Quality.Issues.Should().Contain(i => i.Code == "DuplicateTimestamp" && i.Severity == DataQualitySeverity.Error);
    }

    // ----------------------------- Footprint (D) -----------------------------

    private const string ValidFootprintCsv = """
        bartimestamp,symbol,pricelevel,bidvolumeatprice,askvolumeatprice,totalvolumeatprice
        2026-07-03T13:30:00Z,NQ,20000.00,10,30,40
        2026-07-03T13:30:00Z,NQ,20000.25,5,15,20
        2026-07-03T13:31:00Z,NQ,20000.50,20,10,30
        """;

    [Fact]
    public void Footprint_csv_groups_levels_into_bars_with_correct_sums()
    {
        var result = new AtasFootprintCsvImporter().Import(R(ValidFootprintCsv));

        result.SourceType.Should().Be(MarketDataSourceType.Footprint);
        result.FootprintBars.Should().HaveCount(2);

        var bar1 = result.FootprintBars[0];
        bar1.Levels.Should().HaveCount(2);
        bar1.BidVolume.Should().Be(15m);        // 10 + 5
        bar1.AskVolume.Should().Be(45m);        // 30 + 15
        bar1.Delta.Should().Be(30m);
        bar1.TotalVolume.Should().Be(60m);      // 40 + 20
        bar1.CumulativeDelta.Should().Be(30m);

        result.FootprintBars[1].CumulativeDelta.Should().Be(20m); // 30 + (10-20)
        result.Capabilities.SupportsStackedImbalances.Should().BeTrue(); // echte Footprint-Daten!
        result.Capabilities.SupportsHvnLvn.Should().BeFalse();
    }

    [Fact]
    public void Footprint_level_sum_mismatch_is_rejected()
    {
        var csv = """
        bartimestamp,symbol,pricelevel,bidvolumeatprice,askvolumeatprice,totalvolumeatprice
        2026-07-03T13:30:00Z,NQ,20000.00,10,30,99
        """;

        var result = new AtasFootprintCsvImporter().Import(R(csv));

        result.FootprintBars.Should().BeEmpty();
        result.Quality.Issues.Should().Contain(i => i.Code == "LevelSumMismatch" && i.Severity == DataQualitySeverity.Error);
    }

    [Fact]
    public void Footprint_without_ohlc_columns_warns_and_derives_high_low_from_levels()
    {
        var result = new AtasFootprintCsvImporter().Import(R(ValidFootprintCsv));

        result.Quality.Issues.Should().Contain(i => i.Code == "MissingOhlc" && i.Severity == DataQualitySeverity.Warning);
        result.FootprintBars[0].High.Should().Be(20000.25m); // echte Preisspanne der Level
        result.FootprintBars[0].Low.Should().Be(20000.00m);
        result.FootprintBars[0].Open.Should().Be(0m);        // NICHT erfunden
    }

    // ----------------------------- Volume Profile (E) ------------------------

    [Fact]
    public void Volume_profile_csv_groups_by_session_and_enables_hvn_lvn()
    {
        var csv = """
        sessiondate,symbol,pricelevel,volumeatprice,hvn,lvn
        2026-07-03,NQ,20000.00,5000,true,false
        2026-07-03,NQ,20000.25,120,false,true
        2026-07-04,NQ,20010.00,3000,,
        """;

        var result = new VolumeProfileCsvImporter().Import(R(csv));

        result.VolumeProfiles.Should().HaveCount(2);
        var day1 = result.VolumeProfiles[0];
        day1.Levels.Should().HaveCount(2);
        day1.TotalVolume.Should().Be(5120m);
        day1.PointOfControl.Should().Be(20000.00m);
        day1.Levels[0].IsHighVolumeNode.Should().BeTrue();
        result.VolumeProfiles[1].Levels[0].IsHighVolumeNode.Should().BeNull(); // nicht klassifiziert

        result.Capabilities.SupportsHvnLvn.Should().BeTrue();
        result.Capabilities.SupportsDeltaCvd.Should().BeFalse(); // Profile allein erlaubt kein Delta
    }

    // ----------------------------- Struktur-Fehler ---------------------------

    [Fact]
    public void Missing_required_column_throws_clean_exception()
    {
        var csv = "timestamp,symbol,price\n2026-07-03T13:30:00Z,NQ,20000"; // volume fehlt

        var act = () => new AtasTickCsvImporter().Import(R(csv));

        act.Should().Throw<CsvMarketDataException>().WithMessage("*volume*");
    }
}
