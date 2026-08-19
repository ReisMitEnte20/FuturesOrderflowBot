using TradingBot.Core.Interfaces;

namespace TradingBot.Application.Strategies.OrderFlow;

/// <summary>
/// Marker-Interface für Strategien, die OrderFlowBars verarbeiten (OnOrderFlowBar).
/// Dient zur automatischen Erkennung in BacktestStrategyRunner, um einen
/// OrderFlowBarAggregatorStrategy-Wrapper einzuschalten.
/// </summary>
public interface IOrderFlowStrategy : IStrategy
{
}