using FluentAssertions;
using TradingBot.Domain.Enums;
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
    public void Intrabar_frames_show_forming_candle_building_up()
    {
        // 4 Ticks im 5-Min-Bucket [23:00,23:05), dann 1 Tick im nächsten Bucket. frameEveryTicks=1.
        var csv = Header + "\n" +
            "2025/12/28, 23:00:10, 100.00,0,0,100.00, 1, 1, 0, 1\n" +   // buy
            "2025/12/28, 23:01:00, 101.00,0,0,101.00, 2, 1, 0, 2\n" +   // buy, neues High
            "2025/12/28, 23:02:00, 99.00,0,0,99.00, 1, 1, 1, 0\n" +     // sell, neues Low
            "2025/12/28, 23:03:00, 100.50,0,0,100.50, 1, 1, 0, 1\n" +   // buy
            "2025/12/28, 23:06:00, 102.00,0,0,102.00, 1, 1, 0, 1\n";    // neuer Bucket

        var frames = new List<SierraIntrabarFrame>();
        var agg = new SierraOrderFlowBarBuilder().Build(
            R(csv), "MES", TimeSpan.FromMinutes(5), frameEveryTicks: 1, onFrame: frames.Add);

        frames.Should().HaveCount(5);                 // je Tick ein Frame (mehr als 2 finale Bars)
        agg.BarsCreated.Should().Be(2);

        frames[0].CompletedBars.Should().Be(0);
        frames[0].High.Should().Be(100m); frames[0].Low.Should().Be(100m); frames[0].Volume.Should().Be(1m);
        frames[1].High.Should().Be(101m); frames[1].Close.Should().Be(101m); frames[1].Volume.Should().Be(3m); frames[1].Delta.Should().Be(3m);
        frames[2].Low.Should().Be(99m);   frames[2].Volume.Should().Be(4m); frames[2].Delta.Should().Be(2m); // ask3 - bid1
        frames[3].Close.Should().Be(100.50m); frames[3].Volume.Should().Be(5m);
        frames[4].CompletedBars.Should().Be(1);       // Bar-Grenze überschritten -> neue forming candle
        frames[4].Open.Should().Be(102m);
        frames[4].CumulativeDelta.Should().Be(4m);    // finalisierte Bar0-Delta 3 + forming +1
        frames.Select(f => f.TickTimeUtc.Offset).Should().OnlyContain(o => o == TimeSpan.Zero);
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

    // ---- Range-Bars (Phase: Replay Range-Chart) — Bar-Wechsel bei Preisbewegung statt Zeit ----

    private const string RangeTicks = Header + "\n" +
        "2025/12/28, 23:00:00, 0,0,0, 100.00, 1, 1, 0, 1\n" +   // buy, Bar1 open
        "2025/12/28, 23:00:01, 0,0,0, 100.30, 1, 1, 0, 1\n" +   // buy, Range 0.30
        "2025/12/28, 23:00:02, 0,0,0, 100.60, 1, 1, 1, 0\n" +   // sell, Range 0.60
        "2025/12/28, 23:00:03, 0,0,0, 101.10, 1, 1, 0, 1\n" +   // buy, Range 1.10 >= 1 -> Bar1 schließt
        "2025/12/28, 23:00:04, 0,0,0, 101.20, 1, 1, 0, 1\n" +   // buy, Bar2 open
        "2025/12/28, 23:00:05, 0,0,0, 100.00, 1, 1, 1, 0\n";    // sell, Range 1.20 >= 1 -> Bar2 schließt

    [Fact]
    public void Range_bars_close_when_high_low_reaches_target_range()
    {
        var r = new SierraOrderFlowBarBuilder().BuildRange(R(RangeTicks), "MES", rangeSize: 1m);

        r.BarsCreated.Should().Be(2);

        var b0 = r.Bars[0].Bar;
        b0.OpenTime.Should().Be(new DateTimeOffset(2025, 12, 28, 23, 0, 0, TimeSpan.Zero));
        b0.CloseTime.Should().Be(new DateTimeOffset(2025, 12, 28, 23, 0, 3, TimeSpan.Zero)); // Tick, der die Range erreicht
        b0.Open.Should().Be(100.00m);
        b0.High.Should().Be(101.10m);
        b0.Low.Should().Be(100.00m);
        b0.Close.Should().Be(101.10m);
        b0.TotalVolume.Should().Be(4m);
        b0.AskVolume.Should().Be(3m);
        b0.BidVolume.Should().Be(1m);
        b0.Delta.Should().Be(2m);

        var b1 = r.Bars[1].Bar;
        b1.OpenTime.Should().Be(new DateTimeOffset(2025, 12, 28, 23, 0, 4, TimeSpan.Zero));
        b1.CloseTime.Should().Be(new DateTimeOffset(2025, 12, 28, 23, 0, 5, TimeSpan.Zero));
        b1.Open.Should().Be(101.20m);
        b1.High.Should().Be(101.20m);
        b1.Low.Should().Be(100.00m);
        b1.Close.Should().Be(100.00m);
        b1.TotalVolume.Should().Be(2m);
    }

    [Fact]
    public void Range_bars_flush_trailing_incomplete_bar_at_end_of_data()
    {
        // 3. Tick erreicht die Zielspanne nicht mehr -> unvollständige Bar wird am Dateiende geflusht.
        var csv = RangeTicks + "2025/12/28, 23:00:06, 0,0,0, 100.10, 1, 1, 0, 1\n";
        var r = new SierraOrderFlowBarBuilder().BuildRange(R(csv), "MES", rangeSize: 1m);

        r.BarsCreated.Should().Be(3);
        r.Bars[2].Bar.Open.Should().Be(100.10m);
        r.Bars[2].Bar.Close.Should().Be(100.10m);
        r.Bars[2].Bar.TotalVolume.Should().Be(1m);
    }

    [Fact]
    public void Range_bars_reuse_same_honest_capabilities_as_time_bars()
    {
        var r = new SierraOrderFlowBarBuilder().BuildRange(R(RangeTicks), "MES", rangeSize: 1m);

        r.Capabilities.SupportsDeltaCvd.Should().BeTrue();
        r.Capabilities.SupportsStackedImbalances.Should().BeTrue();
        r.Granularity.Should().Be(SierraGranularity.SingleTick);
    }

    [Fact]
    public void Range_size_must_be_positive()
    {
        var act = () => new SierraOrderFlowBarBuilder().BuildRange(R(RangeTicks), "MES", rangeSize: 0m);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ---- StreamTicks (rohe Ticks für Replay/Backtest/Research) ----

    [Fact]
    public void StreamTicks_returns_classified_market_ticks_with_correct_aggressor()
    {
        var ticks = SierraOrderFlowBarBuilder.StreamTicks(R(RangeTicks), "MES");

        ticks.Should().HaveCount(6);
        ticks[0].Aggressor.Should().Be(AggressorSide.Buy);
        ticks[1].Aggressor.Should().Be(AggressorSide.Buy);
        ticks[2].Aggressor.Should().Be(AggressorSide.Sell);
        ticks[3].Aggressor.Should().Be(AggressorSide.Buy);
        ticks[4].Aggressor.Should().Be(AggressorSide.Buy);
        ticks[5].Aggressor.Should().Be(AggressorSide.Sell);
        ticks.All(t => t.Symbol == "MES").Should().BeTrue();
        ticks.All(t => t.Price > 0).Should().BeTrue();
        ticks.All(t => t.Volume > 0).Should().BeTrue();
    }

    [Fact]
    public void StreamTicks_respects_maxRows_and_time_filter()
    {
        var ticks = SierraOrderFlowBarBuilder.StreamTicks(R(RangeTicks), "MES", maxRows: 3);
        ticks.Should().HaveCount(3);
        ticks[0].Timestamp.Should().Be(new DateTimeOffset(2025, 12, 28, 23, 0, 0, TimeSpan.Zero));
        ticks[2].Timestamp.Should().Be(new DateTimeOffset(2025, 12, 28, 23, 0, 2, TimeSpan.Zero));
    }

    [Fact]
    public void StreamTicks_throws_on_missing_symbol_or_file()
    {
        var act = () => SierraOrderFlowBarBuilder.StreamTicks(R(RangeTicks), "");
        act.Should().Throw<ArgumentException>().WithMessage("*Symbol*");
    }
}
