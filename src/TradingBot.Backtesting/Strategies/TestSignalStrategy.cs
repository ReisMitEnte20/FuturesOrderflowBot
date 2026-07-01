using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Backtesting.Strategies;

/// <summary>
/// DUMMY-Strategie NUR zum Testen der Backtest-Infrastruktur. KEINE Profit-Strategie.
/// Erzeugt deterministisch alle <c>intervalTicks</c> Ticks ein Signal (optional alternierend
/// Long/Short). Erzeugt ausschließlich <see cref="TradeSignal"/> – sendet niemals Orders.
/// Über <see cref="Enabled"/> abschaltbar.
/// </summary>
public sealed class TestSignalStrategy : IStrategy
{
    private readonly int _intervalTicks;
    private readonly bool _alternate;
    private readonly int _quantity;
    private readonly int _stopLossTicks;
    private int _tickCount;
    private bool _nextIsLong;

    public TestSignalStrategy(
        int intervalTicks = 2, SignalDirection firstDirection = SignalDirection.Long,
        bool alternate = true, int quantity = 1, int stopLossTicks = 40)
    {
        if (intervalTicks <= 0) throw new ArgumentOutOfRangeException(nameof(intervalTicks));
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        _intervalTicks = intervalTicks;
        _alternate = alternate;
        _quantity = quantity;
        _stopLossTicks = stopLossTicks;
        _nextIsLong = firstDirection != SignalDirection.Short;
    }

    public bool Enabled { get; set; } = true;
    public string Name => "TestSignalStrategy";

    public TradeSignal? OnBar(OrderFlowBar bar) => null;

    public TradeSignal? OnTick(MarketTick tick)
    {
        if (!Enabled) return null;

        _tickCount++;
        if (_tickCount % _intervalTicks != 0) return null;

        var direction = _nextIsLong ? SignalDirection.Long : SignalDirection.Short;
        if (_alternate) _nextIsLong = !_nextIsLong;

        return new TradeSignal
        {
            StrategyName = Name,
            Symbol = tick.Symbol,
            Direction = direction,
            Timestamp = tick.Timestamp,
            ReferencePrice = tick.Price,
            SuggestedQuantity = _quantity,
            SuggestedStopLossTicks = _stopLossTicks,
            Reason = "dummy test signal"
        };
    }
}
