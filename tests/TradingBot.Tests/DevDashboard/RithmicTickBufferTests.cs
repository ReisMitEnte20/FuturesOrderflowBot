using FluentAssertions;
using TradingBot.DevDashboard.Services;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using Xunit;

namespace TradingBot.Tests.DevDashboard;

public class RithmicTickBufferTests
{
    private static MarketTick Tick(decimal price, AggressorSide side = AggressorSide.Buy) =>
        new() { Symbol = "MESZ6", Timestamp = DateTimeOffset.UnixEpoch, Price = price, Volume = 1, Aggressor = side };

    [Fact]
    public void Get_returns_only_newer_ticks_in_order_with_last_seq()
    {
        var buffer = new RithmicTickBuffer();
        buffer.Add(Tick(1));
        buffer.Add(Tick(2));
        buffer.Add(Tick(3, AggressorSide.Sell));

        var page = buffer.Get(since: 1, limit: 100);

        page.Ticks.Select(t => t.Price).Should().Equal(2m, 3m);
        page.Ticks.Select(t => t.Seq).Should().Equal(2L, 3L);
        page.Ticks[^1].Aggressor.Should().Be(AggressorSide.Sell);
        page.LastSeq.Should().Be(3);
        page.Truncated.Should().BeFalse();
        buffer.Get(page.LastSeq, 100).Ticks.Should().BeEmpty();
    }

    [Fact]
    public void Limit_keeps_newest_ticks_and_flags_truncation()
    {
        var buffer = new RithmicTickBuffer();
        for (var i = 1; i <= 10; i++) buffer.Add(Tick(i));

        var page = buffer.Get(0, limit: 3);

        page.Ticks.Select(t => t.Price).Should().Equal(8m, 9m, 10m);
        page.Truncated.Should().BeTrue();
    }

    [Fact]
    public void Capacity_drops_oldest_but_seq_keeps_counting()
    {
        var buffer = new RithmicTickBuffer(capacity: 3);
        for (var i = 1; i <= 5; i++) buffer.Add(Tick(i));

        var page = buffer.Get(0, 100);

        page.Ticks.Select(t => t.Seq).Should().Equal(3L, 4L, 5L);
        page.LastSeq.Should().Be(5);
    }
}
