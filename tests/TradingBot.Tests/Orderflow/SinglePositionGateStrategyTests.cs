using FluentAssertions;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Research.Orderflow;
using Xunit;

namespace TradingBot.Tests.Orderflow;

/// <summary>Belegt das SinglePositionGate: gleiche Richtung halten (kein Add), Flat schliesst,
/// Gegenrichtung = Close-dann-Reopen (dokumentierte Engine-Semantik). Regeln der inneren Strategie unveraendert.</summary>
public class SinglePositionGateStrategyTests
{
    /// <summary>Innere Stub-Strategie, die vorgegebene Richtungen (oder null = kein Signal) liefert.</summary>
    private sealed class QueuedStrategy : IStrategy
    {
        private readonly Queue<SignalDirection?> _q;
        public QueuedStrategy(params SignalDirection?[] seq) => _q = new Queue<SignalDirection?>(seq);
        public string Name => "Stub";
        public TradeSignal? OnOrderFlowBar(OrderFlowBar b)
        {
            var d = _q.Count > 0 ? _q.Dequeue() : null;
            return d is null ? null : new TradeSignal { StrategyName = Name, Symbol = b.Symbol, Direction = d.Value, SuggestedQuantity = 1 };
        }
    }

    private static OrderFlowBar Bar() => new() { Symbol = "NQ", Open = 100, High = 100, Low = 100, Close = 100 };

    private static List<SignalDirection?> Run(params SignalDirection?[] seq)
    {
        var gate = new SinglePositionGateStrategy(new QueuedStrategy(seq));
        var outp = new List<SignalDirection?>();
        for (int i = 0; i < seq.Length; i++) outp.Add(gate.OnOrderFlowBar(Bar())?.Direction);
        return outp;
    }

    [Fact]
    public void Same_direction_is_held_no_add()
        => Run(SignalDirection.Long, SignalDirection.Long, SignalDirection.Long)
           .Should().Equal(SignalDirection.Long, null, null);

    [Fact]
    public void Flat_closes_only_when_a_position_is_open()
        => Run(SignalDirection.Long, SignalDirection.Flat, SignalDirection.Flat)
           .Should().Equal(SignalDirection.Long, SignalDirection.Flat, null);

    [Fact]
    public void Opposite_direction_closes_then_reopens_over_two_signals()
        => Run(SignalDirection.Long, SignalDirection.Short, SignalDirection.Short)
           .Should().Equal(SignalDirection.Long, SignalDirection.Short, SignalDirection.Short);

    [Fact]
    public void Null_inner_signal_is_passed_through_as_hold()
        => Run(SignalDirection.Long, null, SignalDirection.Long)
           .Should().Equal(SignalDirection.Long, null, null); // Doji haelt; danach schon long -> kein Add
}
