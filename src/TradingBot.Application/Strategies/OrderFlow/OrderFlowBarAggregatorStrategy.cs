using TradingBot.Core.Interfaces;
using TradingBot.Application.MarketData;
using TradingBot.Domain.Models;
using TradingBot.Domain.Enums;

namespace TradingBot.Application.Strategies.OrderFlow;

/// <summary>
/// Wrapper-Strategie: aggregiert eingehende <see cref="MarketTick"/> zu <see cref="OrderFlowBar"/>
/// und delegiert an eine innere OrderFlow-Strategie (<see cref="IStrategy.OnOrderFlowBar"/>).
/// Ermöglicht die Nutzung von OrderFlow-Strategien mit der bestehenden <see cref="BacktestEngine"/>
/// (die nur <see cref="IStrategy.OnTick"/> aufruft), ohne Architektur-Änderung.
/// </summary>
public sealed class OrderFlowBarAggregatorStrategy : IStrategy
{
    private readonly IStrategy _innerStrategy;
    private readonly int _ticksPerBar;
    private readonly List<MarketTick> _currentBarTicks = new();
    private readonly OrderFlowBarAggregator _aggregator = new();

    public OrderFlowBarAggregatorStrategy(IStrategy innerStrategy, int ticksPerBar = 100)
    {
        _innerStrategy = innerStrategy ?? throw new ArgumentNullException(nameof(innerStrategy));
        if (ticksPerBar <= 0) throw new ArgumentOutOfRangeException(nameof(ticksPerBar), "ticksPerBar muss > 0 sein.");
        _ticksPerBar = ticksPerBar;
    }

    public string Name => $"OrderFlowAggregator({_innerStrategy.Name}, {_ticksPerBar}t)";

    public StrategyDataRequirements DataRequirements => new()
    {
        NeedsTicks = true,
        NeedsOrderFlowBars = false // wir brauchen nur Ticks, machen OrderFlowBars selbst
    };

    public void Initialize(StrategyExecutionContext context)
    {
        _innerStrategy.Initialize(context);
        _currentBarTicks.Clear();
    }

    public TradeSignal? OnTick(MarketTick tick)
    {
        _currentBarTicks.Add(tick);

        if (_currentBarTicks.Count >= _ticksPerBar)
        {
            var bar = _aggregator.BuildBar(tick.Symbol, _currentBarTicks, ref _cumulativeDelta);
            _currentBarTicks.Clear();

            // Fail-closed: Bar ohne echte Bid/Ask-Klassifikation wird verworfen (wie in StrategyEngine)
            if (bar.TotalVolume > 0m && bar.BidVolume + bar.AskVolume <= 0m)
                return null;

            return _innerStrategy.OnOrderFlowBar(bar);
        }

        return null;
    }

    public TradeSignal? OnCandle(Candle candle) => null;

    public TradeSignal? OnOrderFlowBar(OrderFlowBar bar) => null; // wir aggregieren selbst

    public void Reset()
    {
        _innerStrategy.Reset();
        _currentBarTicks.Clear();
        _cumulativeDelta = 0m;
    }

    private decimal _cumulativeDelta = 0m;
}

// Minimaler Aggregator hier (kopiert die BuildBar-Logik, um keine Abhängigkeit auf Application.MarketData zu erzwingen)
sealed class OrderFlowBarAggregator
{
    public OrderFlowBar BuildBar(string symbol, IReadOnlyList<MarketTick> bar, ref decimal cumulativeDelta)
    {
        ArgumentNullException.ThrowIfNull(bar);
        if (bar.Count == 0) throw new ArgumentException("Bar muss mindestens einen Tick enthalten.", nameof(bar));

        var first = bar[0];
        var last = bar[^1];
        decimal open = first.Price;
        decimal high = first.Price;
        decimal low = first.Price;
        decimal totalVolume = 0m;
        decimal bidVolume = 0m;
        decimal askVolume = 0m;

        foreach (var t in bar)
        {
            if (t.Price > high) high = t.Price;
            if (t.Price < low) low = t.Price;
            totalVolume += t.Volume;
            if (t.Aggressor == AggressorSide.Buy)
                askVolume += t.Volume;
            else if (t.Aggressor == AggressorSide.Sell)
                bidVolume += t.Volume;
            else
            {
                // Unknown: nicht klassifizierbar -> wir zählen nicht zu Bid/Ask (ehrlich)
            }
        }

        decimal close = last.Price;
        decimal delta = askVolume - bidVolume;
        cumulativeDelta += delta;

        return new OrderFlowBar
        {
            Symbol = symbol,
            OpenTime = first.Timestamp,
            CloseTime = last.Timestamp,
            Open = open,
            High = high,
            Low = low,
            Close = close,
            TotalVolume = totalVolume,
            BidVolume = bidVolume,
            AskVolume = askVolume,
            CumulativeDelta = cumulativeDelta
        };
    }
}