using FluentAssertions;
using TradingBot.Application.Orders;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using Xunit;

namespace TradingBot.Tests.Orders;

public class OrderIntentClassifierTests
{
    private static Position Pos(PositionSide side, int qty) => new()
    {
        AccountId = "A", Symbol = "NQ", Side = side, Quantity = qty, AverageEntryPrice = 20000m
    };

    [Fact]
    public void Flat_or_null_position_is_entry()
    {
        OrderIntentClassifier.Classify(null, SignalDirection.Long, 2).Should().Be((OrderIntent.Entry, 2));
        OrderIntentClassifier.Classify(Pos(PositionSide.Flat, 0), SignalDirection.Short, 3)
            .Should().Be((OrderIntent.Entry, 3));
    }

    [Fact]
    public void Same_direction_is_add()
    {
        OrderIntentClassifier.Classify(Pos(PositionSide.Long, 1), SignalDirection.Long, 2)
            .Should().Be((OrderIntent.Add, 2));
        OrderIntentClassifier.Classify(Pos(PositionSide.Short, 1), SignalDirection.Short, 1)
            .Should().Be((OrderIntent.Add, 1));
    }

    [Fact]
    public void Opposite_direction_smaller_is_reduce()
    {
        OrderIntentClassifier.Classify(Pos(PositionSide.Long, 3), SignalDirection.Short, 1)
            .Should().Be((OrderIntent.Reduce, 1));
    }

    [Fact]
    public void Opposite_direction_equal_is_close()
    {
        OrderIntentClassifier.Classify(Pos(PositionSide.Long, 2), SignalDirection.Short, 2)
            .Should().Be((OrderIntent.Close, 2));
        OrderIntentClassifier.Classify(Pos(PositionSide.Short, 1), SignalDirection.Long, 1)
            .Should().Be((OrderIntent.Close, 1));
    }

    [Fact]
    public void Opposite_direction_larger_flip_is_capped_to_close()
    {
        // Long 2, Sell 5 würde flippen -> konservativ nur schließen (2).
        OrderIntentClassifier.Classify(Pos(PositionSide.Long, 2), SignalDirection.Short, 5)
            .Should().Be((OrderIntent.Close, 2));
    }
}
