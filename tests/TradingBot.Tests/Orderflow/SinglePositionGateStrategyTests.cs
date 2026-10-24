using FluentAssertions;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Research.Orderflow;
using Xunit;

namespace TradingBot.Tests.Orderflow;

/// <summary>
/// Belegt, dass SinglePositionGate gegen den TATSAECHLICHEN Positionszustand entscheidet (nicht gegen ein
/// Signalmodell): gleiche Richtung halten (kein Add), Flat schliesst nur bei offener Position, Gegenrichtung
/// durchreichen. Regeln der inneren Strategie unveraendert.
/// </summary>
public class SinglePositionGateStrategyTests
{
    /// <summary>Steuerbare, read-only Positionsquelle fuer den Gate.</summary>
    private sealed class FakePositionManager : IPositionManager
    {
        public PositionSide Side { get; set; } = PositionSide.Flat;
        public int Quantity { get; set; }
        public Position? GetPosition(string symbol) => new() { AccountId = "t", Symbol = symbol, Side = Side, Quantity = Quantity };
        public IReadOnlyCollection<Position> OpenPositions => System.Array.Empty<Position>();
        public Position ApplyFill(FillEvent fill, InstrumentProfile instrument, FeeProfile feeProfile) => throw new System.NotSupportedException();
        public Position MarkToMarket(string symbol, decimal lastPrice, InstrumentProfile instrument) => throw new System.NotSupportedException();
        public bool Reconcile(string symbol, Position? brokerPosition) => true;
    }

    private sealed class FixedInner : IStrategy
    {
        private readonly SignalDirection _dir;
        public FixedInner(SignalDirection dir) => _dir = dir;
        public string Name => "Fixed";
        public TradeSignal? OnOrderFlowBar(OrderFlowBar b) => new() { StrategyName = Name, Symbol = b.Symbol, Direction = _dir, SuggestedQuantity = 1 };
    }

    private static OrderFlowBar Bar() => new() { Symbol = "NQ", Open = 100, High = 100, Low = 100, Close = 100 };

    private static SignalDirection? Decide(SignalDirection inner, PositionSide actual, int qty = 1)
    {
        var pm = new FakePositionManager { Side = actual, Quantity = actual == PositionSide.Flat ? 0 : qty };
        var gate = new SinglePositionGateStrategy(new FixedInner(inner));
        gate.UsePositionManager(pm);
        return gate.OnOrderFlowBar(Bar())?.Direction;
    }

    [Theory]
    [InlineData((int)PositionSide.Flat)]
    [InlineData((int)PositionSide.Short)]
    public void Long_is_forwarded_unless_already_long(int actual)
        => Decide(SignalDirection.Long, (PositionSide)actual).Should().Be(SignalDirection.Long);

    [Fact]
    public void Long_is_suppressed_when_already_long_no_add()
        => Decide(SignalDirection.Long, PositionSide.Long).Should().BeNull();

    [Theory]
    [InlineData((int)PositionSide.Flat)]
    [InlineData((int)PositionSide.Long)]
    public void Short_is_forwarded_unless_already_short(int actual)
        => Decide(SignalDirection.Short, (PositionSide)actual).Should().Be(SignalDirection.Short);

    [Fact]
    public void Short_is_suppressed_when_already_short_no_add()
        => Decide(SignalDirection.Short, PositionSide.Short).Should().BeNull();

    [Theory]
    [InlineData((int)PositionSide.Long)]
    [InlineData((int)PositionSide.Short)]
    public void Flat_closes_only_when_a_position_is_actually_open(int actual)
        => Decide(SignalDirection.Flat, (PositionSide)actual).Should().Be(SignalDirection.Flat);

    [Fact]
    public void Flat_is_suppressed_when_actually_flat()
        => Decide(SignalDirection.Flat, PositionSide.Flat).Should().BeNull();

    [Fact]
    public void Null_inner_signal_stays_null_regardless_of_position()
    {
        var pm = new FakePositionManager { Side = PositionSide.Long, Quantity = 1 };
        var gate = new SinglePositionGateStrategy(new FixedInnerNull());
        gate.UsePositionManager(pm);
        gate.OnOrderFlowBar(Bar()).Should().BeNull();
    }

    private sealed class FixedInnerNull : IStrategy
    {
        public string Name => "Null";
        public TradeSignal? OnOrderFlowBar(OrderFlowBar b) => null;
    }
}
