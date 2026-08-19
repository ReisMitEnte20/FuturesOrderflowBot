using FluentAssertions;
using TradingBot.Application.Strategies;
using TradingBot.Application.Strategies.OrderFlow;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using Xunit;

namespace TradingBot.Tests.Strategies;

/// <summary>
/// Tests für <see cref="OrderFlowBarAggregatorStrategy"/>: bündelt rohe <see cref="MarketTick"/>
/// zu <see cref="OrderFlowBar"/> und delegiert an eine innere OrderFlow-Strategie.
/// Verifiziert, dass die BacktestEngine (die nur OnTick aufruft) echte OrderFlow-Strategien
/// fahren kann, ohne Architektur-Änderung.
/// </summary>
public class OrderFlowBarAggregatorStrategyTests
{
    private static MarketTick Tick(int i, decimal price, AggressorSide aggressor, decimal vol = 1m)
        => new()
        {
            Symbol = "NQ",
            Timestamp = new DateTimeOffset(2025, 12, 28, 23, 0, 0, TimeSpan.Zero).AddSeconds(i),
            Price = price,
            Bid = aggressor == AggressorSide.Sell ? vol : 0m,
            Ask = aggressor == AggressorSide.Buy ? vol : 0m,
            BidSize = aggressor == AggressorSide.Sell ? vol : 0m,
            AskSize = aggressor == AggressorSide.Buy ? vol : 0m,
            Volume = vol,
            Aggressor = aggressor
        };

    [Fact]
    public void Aggregates_ticks_into_bars_and_delegates_to_inner_strategy()
    {
        // Innere Strategie zählt Anzahl der empfangenen OrderFlowBars.
        var inner = new CountingOrderFlowStrategy();
        var wrapper = new OrderFlowBarAggregatorStrategy(inner, ticksPerBar: 3);
        wrapper.Initialize(new StrategyExecutionContext { Symbol = "NQ" });

        // 6 Ticks -> 2 Bars (bei ticksPerBar=3)
        for (int i = 0; i < 6; i++)
            wrapper.OnTick(Tick(i, 21000m + i, i % 2 == 0 ? AggressorSide.Buy : AggressorSide.Sell));

        inner.BarCount.Should().Be(2);
        inner.LastBar.Should().NotBeNull();
        inner.LastBar!.TotalVolume.Should().Be(3m);
    }

    [Fact]
    public void No_signal_until_first_bar_is_complete()
    {
        var inner = new CountingOrderFlowStrategy();
        var wrapper = new OrderFlowBarAggregatorStrategy(inner, ticksPerBar: 3);
        wrapper.Initialize(new StrategyExecutionContext { Symbol = "NQ" });

        wrapper.OnTick(Tick(0, 21000m, AggressorSide.Buy)).Should().BeNull();
        wrapper.OnTick(Tick(1, 21001m, AggressorSide.Sell)).Should().BeNull();
        inner.BarCount.Should().Be(0);

        wrapper.OnTick(Tick(2, 21002m, AggressorSide.Buy)).Should().BeNull(); // Bar fertig, inner gibt kein Signal
        inner.BarCount.Should().Be(1);
    }

    [Fact]
    public void Initializes_inner_strategy_on_initialize()
    {
        var inner = new CountingOrderFlowStrategy();
        var wrapper = new OrderFlowBarAggregatorStrategy(inner, ticksPerBar: 2);

        wrapper.Initialize(new StrategyExecutionContext { Symbol = "NQ" });
        inner.Initialized.Should().BeTrue();
    }

    [Fact]
    public void Rejects_invalid_ticks_per_bar()
    {
        var act = () => new OrderFlowBarAggregatorStrategy(new CountingOrderFlowStrategy(), 0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>Einfache Test-Strategie, die empfangene OrderFlowBars zählt (kein Signal).</summary>
    private sealed class CountingOrderFlowStrategy : IOrderFlowStrategy
    {
        public bool Initialized { get; private set; }
        public int BarCount { get; private set; }
        public OrderFlowBar? LastBar { get; private set; }

        public string Name => "Counting";
        public StrategyDataRequirements DataRequirements => new() { NeedsOrderFlowBars = true };
        public void Initialize(StrategyExecutionContext context) => Initialized = true;
        public TradeSignal? OnTick(MarketTick tick) => null;
        public TradeSignal? OnCandle(Candle candle) => null;
        public TradeSignal? OnOrderFlowBar(OrderFlowBar bar)
        {
            BarCount++;
            LastBar = bar;
            return null;
        }
        public void Reset() { BarCount = 0; LastBar = null; Initialized = false; }
    }
}