using FluentAssertions;
using TradingBot.Application.MarketData;
using TradingBot.Domain.Enums;
using Xunit;

namespace TradingBot.Tests.MarketData;

public class FeedHealthMonitorTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void Healthy_after_fresh_tick()
    {
        var clock = new MutableClock(Md.T0);
        var sut = new FeedHealthMonitor(clock, Timeout);
        sut.SetConnected(true);

        sut.RecordTick(Md.Tick(0, 20000m, 1));

        sut.IsHealthy.Should().BeTrue();
        sut.State.Status.Should().Be(ConnectionStatus.Connected);
        sut.State.LastTickTimestamp.Should().Be(Md.T0);
    }

    [Fact]
    public void Unhealthy_when_no_tick_received_yet_failclosed()
    {
        var sut = new FeedHealthMonitor(new MutableClock(Md.T0), Timeout);
        sut.SetConnected(true); // verbunden, aber noch kein Tick

        sut.IsHealthy.Should().BeFalse();
        sut.State.Status.Should().Be(ConnectionStatus.Unknown);
    }

    [Fact]
    public void Stale_when_timeout_exceeded()
    {
        var clock = new MutableClock(Md.T0);
        var sut = new FeedHealthMonitor(clock, Timeout);
        sut.SetConnected(true);
        sut.RecordTick(Md.Tick(0, 20000m, 1));

        clock.UtcNow = Md.T0.AddSeconds(10); // > 5s Timeout

        sut.IsHealthy.Should().BeFalse();
        sut.State.Status.Should().Be(ConnectionStatus.Stale);
    }

    [Fact]
    public void Still_healthy_just_within_timeout()
    {
        var clock = new MutableClock(Md.T0);
        var sut = new FeedHealthMonitor(clock, Timeout);
        sut.SetConnected(true);
        sut.RecordTick(Md.Tick(0, 20000m, 1));

        clock.UtcNow = Md.T0.AddSeconds(4); // < 5s

        sut.IsHealthy.Should().BeTrue();
    }

    [Fact]
    public void Disconnected_when_set_disconnected()
    {
        var clock = new MutableClock(Md.T0);
        var sut = new FeedHealthMonitor(clock, Timeout);
        sut.SetConnected(true);
        sut.RecordTick(Md.Tick(0, 20000m, 1));

        sut.SetConnected(false);

        sut.IsHealthy.Should().BeFalse();
        sut.State.Status.Should().Be(ConnectionStatus.Disconnected);
    }

    [Fact]
    public void Zero_or_negative_timeout_is_rejected()
    {
        var act = () => new FeedHealthMonitor(new MutableClock(Md.T0), TimeSpan.Zero);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
