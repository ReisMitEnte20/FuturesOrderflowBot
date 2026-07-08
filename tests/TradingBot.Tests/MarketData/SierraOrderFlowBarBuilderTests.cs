using FluentAssertions;
using TradingBot.Infrastructure.MarketData;
using TradingBot.Infrastructure.MarketData.Import;
using Xunit;

namespace TradingBot.Tests.MarketData;

/// <summary>
/// Phase 12E-E: Sierra-Ticks → Time-OrderFlowBars (streamend). Nur KLEINE synthetische Eingaben.
/// Prüft OHLC, Bid/Ask, Delta, CVD, VolumeAtPrice, MaxRows, Parse-Fehler und ehrliche Capabilities.
/// </summary>
public class SierraOrderFlowBarBuilderTests
{
    private const string Header =
        "Date, Time, Open, High, Low, Last, Volume, NumberOfTrades, BidVolume, AskVolume";

    private static TextReader R(string csv) => new StringReader(csv);

    private static SierraAggregationResult Build(string csv, long? maxRows = null)
        => new SierraOrderFlowBarBuilder().Build(R(csv), "MES", TimeSpan.FromMinutes(1), maxRows);

    // Zwei Minuten-Buckets: 23:00 (3 Ticks) und 23:01 (1 Tick). BidVol/AskVol je Tick auf einer Seite.
    private const string TwoMinutes = Header + "\n" +
        "2025/12/28, 23:00:05, 100.00, 100.25, 100.00, 100.00, 5, 1, 0, 5\n" +   // Buy @100.00, ask5
        "2025/12/28, 23:00:30, 100.25, 100.50, 100.25, 100.25, 3, 1, 3, 0\n" +   // Sell @100.25, bid3
        "2025/12/28, 23:00:59, 100.50, 100.75, 100.50, 100.50, 2, 1, 0, 2\n" +   // Buy @100.50, ask2
        "2025/12/28, 23:01:10, 100.25, 100.50, 100.00, 100.25, 4, 1, 4, 0\n";    // Sell @100.25, bid4

    [Fact]
    public void Builds_one_minute_bars_with_correct_ohlc()
    {
        var r = Build(TwoMinutes);

        r.BarsCreated.Should().Be(2);
        var b0 = r.Bars[0].Bar;
        b0.OpenTime.Should().Be(new DateTimeOffset(2025, 12, 28, 23, 0, 0, TimeSpan.Zero));
        b0.CloseTime.Should().Be(new DateTimeOffset(2025, 12, 28, 23, 1, 0, TimeSpan.Zero));
        b0.Open.Should().Be(100.00m);
        b0.High.Should().Be(100.50m);   // höchster Last-Preis im Bucket
        b0.Low.Should().Be(100.00m);
        b0.Close.Should().Be(100.50m);
        r.Bars[0].MinPrice.Should().Be(100.00m);
        r.Bars[0].MaxPrice.Should().Be(100.50m);
    }

    [Fact]
    public void Bid_ask_volume_and_delta_are_aggregated_per_bar()
    {
        var r = Build(TwoMinutes);
        var b0 = r.Bars[0].Bar;

        b0.TotalVolume.Should().Be(10m);       // 5+3+2
        b0.AskVolume.Should().Be(7m);          // 5+2
        b0.BidVolume.Should().Be(3m);
        b0.Delta.Should().Be(4m);              // Ask - Bid
        r.Bars[0].NumberOfTrades.Should().Be(3m);
    }

    [Fact]
    public void Cumulative_delta_runs_across_bars()
    {
        var r = Build(TwoMinutes);

        r.Bars[0].Bar.CumulativeDelta.Should().Be(4m);        // +4
        r.Bars[1].Bar.CumulativeDelta.Should().Be(0m);        // 4 + (0-4)
        r.FinalCumulativeDelta.Should().Be(0m);
        r.NetDelta.Should().Be(0m);                            // Ask(7) - Bid(7)
    }

    [Fact]
    public void Volume_at_price_footprint_is_aggregated_from_ticks()
    {
        var r = Build(TwoMinutes);
        var levels = r.Bars[0].PriceLevels;

        levels.Should().HaveCount(3); // 100.00, 100.25, 100.50
        var l0 = levels.Single(l => l.Price == 100.00m);
        l0.AskVolume.Should().Be(5m);
        l0.BidVolume.Should().Be(0m);
        l0.Volume.Should().Be(5m);
        l0.Delta.Should().Be(5m);

        var l1 = levels.Single(l => l.Price == 100.25m);
        l1.BidVolume.Should().Be(3m);
        l1.Delta.Should().Be(-3m);
    }

    [Fact]
    public void Capabilities_are_honest_delta_cvd_true_footprint_true_no_dom()
    {
        var r = Build(TwoMinutes);

        r.Capabilities.SupportsDeltaCvd.Should().BeTrue();
        r.Capabilities.SupportsAbsorption.Should().BeTrue();
        r.Capabilities.SupportsBarImbalance.Should().BeTrue();
        r.Capabilities.SupportsStackedImbalances.Should().BeTrue();  // echt aus Ticks aggregiert
        r.Capabilities.SupportsHvnLvn.Should().BeFalse();            // kein Session-Profile gebaut
        r.SupportsDomLevel2.Should().BeFalse();
        r.Granularity.Should().Be(SierraGranularity.SingleTick);
    }

    [Fact]
    public void Footprint_can_be_disabled_and_then_no_stacked_capability()
    {
        var r = new SierraOrderFlowBarBuilder().Build(R(TwoMinutes), "MES", TimeSpan.FromMinutes(1), buildFootprint: false);

        r.Bars[0].PriceLevels.Should().BeEmpty();
        r.Capabilities.SupportsStackedImbalances.Should().BeFalse();
        r.Capabilities.SupportsDeltaCvd.Should().BeTrue(); // Delta/CVD weiterhin echt
    }

    [Fact]
    public void Ohlcv_only_produces_no_fake_capabilities()
    {
        var csv = "Date, Time, Last, Volume\n" +
                  "2025/12/28, 23:00:05, 100.00, 5\n";
        var r = new SierraOrderFlowBarBuilder().Build(R(csv), "MES", TimeSpan.FromMinutes(1));

        r.Capabilities.Should().Be(TradingBot.Domain.Models.OrderFlowCapabilities.None);
        r.Bars[0].Bar.TotalVolume.Should().Be(5m);
    }

    [Fact]
    public void MaxRows_limits_processing_cleanly()
    {
        var r = Build(TwoMinutes, maxRows: 2);

        r.RowsProcessed.Should().Be(2);
        r.Truncated.Should().BeTrue();
        r.BarsCreated.Should().Be(1); // nur der erste Minuten-Bucket (2 Ticks) wird geflusht
    }

    [Fact]
    public void Parse_errors_are_counted_and_do_not_crash()
    {
        var csv = Header + "\n" +
                  "2025/12/28, 23:00:05, 100.00, 100.25, 100.00, NOTANUMBER, 5, 1, 0, 5\n" +
                  "2025/12/28, 23:00:30, 100.25, 100.50, 100.25, 100.25, 3, 1, 3, 0\n";
        var r = Build(csv);

        r.ParseErrors.Should().Be(1);
        r.ValidTicks.Should().Be(1);
        r.Issues.Should().Contain(i => i.Code == "ParseError");
    }

    [Fact]
    public void Missing_required_column_throws()
    {
        var csv = "Date, Time, Last\n2025/12/28, 23:00:00, 100.00\n";
        var act = () => Build(csv);
        act.Should().Throw<CsvMarketDataException>().WithMessage("*Volume*");
    }

    [Fact]
    public void Five_minute_interval_groups_ticks()
    {
        var csv = Header + "\n" +
                  "2025/12/28, 23:00:10, 100.00, 100.00, 100.00, 100.00, 1, 1, 0, 1\n" +
                  "2025/12/28, 23:04:59, 100.50, 100.50, 100.50, 100.50, 1, 1, 0, 1\n" +
                  "2025/12/28, 23:05:01, 101.00, 101.00, 101.00, 101.00, 1, 1, 0, 1\n";
        var r = new SierraOrderFlowBarBuilder().Build(R(csv), "MES", TimeSpan.FromMinutes(5));

        r.BarsCreated.Should().Be(2);       // [23:00,23:05) und [23:05,23:10)
        r.Bars[0].Bar.TotalVolume.Should().Be(2m);
        r.Bars[1].Bar.TotalVolume.Should().Be(1m);
    }
}
