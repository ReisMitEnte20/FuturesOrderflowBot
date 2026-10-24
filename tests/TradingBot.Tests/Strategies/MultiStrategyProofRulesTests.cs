using FluentAssertions;
using TradingBot.Application.Strategies.OrderFlow;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using Xunit;

namespace TradingBot.Tests.Strategies;

/// <summary>Sichert die UNVERAENDERTEN Signalregeln der drei portierten Teststrategien.</summary>
public class MultiStrategyProofRulesTests
{
    private static OrderFlowBar Bar(decimal open, decimal close, decimal bid = 0m, decimal ask = 0m) => new()
    {
        Symbol = "NQ", OpenTime = DateTimeOffset.UnixEpoch, CloseTime = DateTimeOffset.UnixEpoch.AddSeconds(1),
        Open = open, High = Math.Max(open, close), Low = Math.Min(open, close), Close = close,
        TotalVolume = bid + ask, BidVolume = bid, AskVolume = ask
    };

    [Theory]
    [InlineData(100, 101, (int)SignalDirection.Long)]
    [InlineData(101, 100, (int)SignalDirection.Short)]
    public void Momentum_follows_price_direction(decimal open, decimal close, int dir)
        => new MomentumPriceStrategy().OnOrderFlowBar(Bar(open, close))!.Direction.Should().Be((SignalDirection)dir);

    [Fact]
    public void Momentum_gives_no_signal_on_doji()
        => new MomentumPriceStrategy().OnOrderFlowBar(Bar(100, 100)).Should().BeNull();

    [Theory]
    [InlineData(100, 101, (int)SignalDirection.Short)]
    [InlineData(101, 100, (int)SignalDirection.Long)]
    public void Fade_opposes_price_direction(decimal open, decimal close, int dir)
        => new FadePriceStrategy().OnOrderFlowBar(Bar(open, close))!.Direction.Should().Be((SignalDirection)dir);

    [Fact]
    public void Fade_gives_no_signal_on_doji()
        => new FadePriceStrategy().OnOrderFlowBar(Bar(100, 100)).Should().BeNull();

    [Theory]
    [InlineData(0, 31, (int)SignalDirection.Long)]    // Delta +31 > 30
    [InlineData(31, 0, (int)SignalDirection.Short)]   // Delta -31 < -30
    [InlineData(0, 30, (int)SignalDirection.Flat)]    // Delta +30 nicht > 30 -> Flat
    [InlineData(30, 0, (int)SignalDirection.Flat)]    // Delta -30 nicht < -30 -> Flat
    [InlineData(10, 10, (int)SignalDirection.Flat)]   // Delta 0 -> Flat
    public void DeltaFlow_uses_threshold_and_emits_explicit_flat(decimal bid, decimal ask, int dir)
        => new DeltaFlowStrategy(30m).OnOrderFlowBar(Bar(100, 100, bid, ask))!.Direction.Should().Be((SignalDirection)dir);

    [Fact]
    public void DeltaFlow_threshold_is_read_from_config()
        => DeltaFlowStrategy.ThresholdFrom(new StrategyConfig
        {
            Name = "DeltaFlow(OF)", Symbol = "NQ",
            Parameters = new Dictionary<string, string> { ["DeltaThreshold"] = "50" }
        }).Should().Be(50m);
}
