using TradingBot.Core.Interfaces;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Strategies;

/// <summary>
/// Brücke zwischen StrategyEngine und den bestehenden Single-Strategy-Engines
/// (Backtest/Paper): präsentiert eine ganze <see cref="IStrategyEngine"/> als EIN
/// <see cref="IStrategy"/>. Pro Event wird das ERSTE erzeugte Signal weitergereicht
/// (dokumentierte Vereinfachung – Multi-Signal-Verarbeitung pro Event folgt später,
/// wenn Backtest/Paper mehrere Signale je Tick unterstützen).
/// </summary>
public sealed class CompositeStrategy : IStrategy
{
    private readonly IStrategyEngine _engine;

    public CompositeStrategy(IStrategyEngine engine, string name = "CompositeStrategy")
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        Name = name;
    }

    public string Name { get; }

    public void Initialize(StrategyExecutionContext context) => _engine.Initialize(context);

    public TradeSignal? OnTick(MarketTick tick)
        => _engine.OnTick(tick).FirstOrDefault(r => r.HasSignal)?.Signal;

    public TradeSignal? OnCandle(Candle candle)
        => _engine.OnCandle(candle).FirstOrDefault(r => r.HasSignal)?.Signal;

    public TradeSignal? OnOrderFlowBar(OrderFlowBar bar)
        => _engine.OnOrderFlowBar(bar).FirstOrDefault(r => r.HasSignal)?.Signal;

    public void Reset() => _engine.Reset();
}
