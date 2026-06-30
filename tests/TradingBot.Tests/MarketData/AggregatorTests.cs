using FluentAssertions;
using TradingBot.Application.MarketData;
using TradingBot.Domain.Enums;
using Xunit;
using static TradingBot.Tests.MarketData.Md;

namespace TradingBot.Tests.MarketData;

public class AggregatorTests
{
    // ----------------------------- Time candles ------------------------------

    [Fact]
    public void TimeCandle_groups_by_interval()
    {
        var ticks = new[]
        {
            Tick(0.0, 100m, 1),   // Sekunde 0
            Tick(0.5, 102m, 2),   // Sekunde 0
            Tick(1.2, 101m, 3),   // Sekunde 1
        };

        var candles = TimeCandleAggregator.Aggregate(ticks, TimeSpan.FromSeconds(1));

        candles.Should().HaveCount(2);
        candles[0].Open.Should().Be(100m);
        candles[0].High.Should().Be(102m);
        candles[0].Low.Should().Be(100m);
        candles[0].Close.Should().Be(102m);
        candles[0].Volume.Should().Be(3m);
        candles[1].Open.Should().Be(101m);
        candles[1].Volume.Should().Be(3m);
    }

    [Fact]
    public void TimeCandle_rejects_mixed_symbols()
    {
        var ticks = new[] { Tick(0, 100m, 1, "NQ"), Tick(1, 50m, 1, "ES") };
        var act = () => TimeCandleAggregator.Aggregate(ticks, TimeSpan.FromSeconds(1));
        act.Should().Throw<ArgumentException>();
    }

    // ----------------------------- Tick bars ---------------------------------

    [Fact]
    public void TickBar_groups_every_n_ticks_including_partial()
    {
        var ticks = new[]
        {
            Tick(0, 100m, 1), Tick(1, 101m, 1), Tick(2, 102m, 1), Tick(3, 103m, 1), Tick(4, 104m, 1)
        };

        var bars = TickBarAggregator.Aggregate(ticks, ticksPerBar: 2);

        bars.Should().HaveCount(3); // 2 + 2 + 1 (partial)
        bars[0].Open.Should().Be(100m);
        bars[0].Close.Should().Be(101m);
        bars[1].Open.Should().Be(102m);
        bars[1].Close.Should().Be(103m);
        bars[2].Open.Should().Be(104m);
        bars[2].Volume.Should().Be(1m);
    }

    // ----------------------------- Volume bars -------------------------------

    [Fact]
    public void VolumeBar_closes_when_threshold_reached()
    {
        var ticks = new[]
        {
            Tick(0, 100m, 2), Tick(1, 101m, 2), Tick(2, 102m, 3), // acc 7 >= 5 -> bar0
            Tick(3, 103m, 1), Tick(4, 104m, 4)                    // acc 5 >= 5 -> bar1
        };

        var bars = VolumeBarAggregator.Aggregate(ticks, volumePerBar: 5m);

        bars.Should().HaveCount(2);
        bars[0].Volume.Should().Be(7m);
        bars[0].Open.Should().Be(100m);
        bars[0].Close.Should().Be(102m);
        bars[1].Volume.Should().Be(5m);
        bars[1].Close.Should().Be(104m);
    }

    // ----------------------------- Order flow --------------------------------

    [Fact]
    public void OrderFlow_delta_is_correct_when_classified()
    {
        var ticks = new[]
        {
            Tick(0, 100m, 2, aggressor: AggressorSide.Buy),
            Tick(1, 100m, 5, aggressor: AggressorSide.Buy),
            Tick(2, 100m, 3, aggressor: AggressorSide.Sell),   // bar0: ask 7, bid 3, delta +4
            Tick(3, 100m, 1, aggressor: AggressorSide.Buy),
            Tick(4, 100m, 4, aggressor: AggressorSide.Sell),
            Tick(5, 100m, 2, aggressor: AggressorSide.Buy),    // bar1: ask 3, bid 4, delta -1
        };

        var bars = OrderFlowBarAggregator.AggregateByTicks(ticks, ticksPerBar: 3);

        bars.Should().HaveCount(2);
        bars[0].AskVolume.Should().Be(7m);
        bars[0].BidVolume.Should().Be(3m);
        bars[0].Delta.Should().Be(4m);
        bars[0].TotalVolume.Should().Be(10m);
        bars[0].CumulativeDelta.Should().Be(4m);

        bars[1].Delta.Should().Be(-1m);
        bars[1].CumulativeDelta.Should().Be(3m); // 4 + (-1)
    }

    [Fact]
    public void OrderFlow_throws_when_any_tick_is_unclassified()
    {
        var ticks = new[]
        {
            Tick(0, 100m, 2, aggressor: AggressorSide.Buy),
            Tick(1, 100m, 1, aggressor: AggressorSide.Unknown), // keine Klassifikation
        };

        var act = () => OrderFlowBarAggregator.AggregateByTicks(ticks, ticksPerBar: 2);

        act.Should().Throw<OrderFlowUnavailableException>();
    }
}
