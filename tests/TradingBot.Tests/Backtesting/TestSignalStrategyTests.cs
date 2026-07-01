using FluentAssertions;
using TradingBot.Backtesting.Strategies;
using TradingBot.Domain.Enums;
using Xunit;

namespace TradingBot.Tests.Backtesting;

public class TestSignalStrategyTests
{
    [Fact]
    public void Emits_signal_every_n_ticks()
    {
        var sut = new TestSignalStrategy(intervalTicks: 3);

        sut.OnTick(BacktestTestData.Tick(0, 20000m)).Should().BeNull();
        sut.OnTick(BacktestTestData.Tick(1, 20001m)).Should().BeNull();
        sut.OnTick(BacktestTestData.Tick(2, 20002m)).Should().NotBeNull(); // 3. Tick
    }

    [Fact]
    public void Alternates_long_and_short()
    {
        var sut = new TestSignalStrategy(intervalTicks: 1, firstDirection: SignalDirection.Long, alternate: true);

        sut.OnTick(BacktestTestData.Tick(0, 20000m))!.Direction.Should().Be(SignalDirection.Long);
        sut.OnTick(BacktestTestData.Tick(1, 20000m))!.Direction.Should().Be(SignalDirection.Short);
        sut.OnTick(BacktestTestData.Tick(2, 20000m))!.Direction.Should().Be(SignalDirection.Long);
    }

    [Fact]
    public void Disabled_strategy_emits_nothing()
    {
        var sut = new TestSignalStrategy(intervalTicks: 1) { Enabled = false };
        sut.OnTick(BacktestTestData.Tick(0, 20000m)).Should().BeNull();
    }

    [Fact]
    public void Is_deterministic_for_same_inputs()
    {
        var a = new TestSignalStrategy(intervalTicks: 2);
        var b = new TestSignalStrategy(intervalTicks: 2);

        for (int i = 0; i < 6; i++)
        {
            var tick = BacktestTestData.Tick(i, 20000m + i);
            var sa = a.OnTick(tick);
            var sb = b.OnTick(tick);
            (sa?.Direction).Should().Be(sb?.Direction);
        }
    }

    [Fact]
    public void OnBar_returns_null()
    {
        var sut = new TestSignalStrategy();
        sut.OnBar(new TradingBot.Domain.Models.OrderFlowBar { Symbol = "NQ" }).Should().BeNull();
    }
}
