namespace TradingBot.Research.Runner;

/// <summary>
/// Führt EINEN Backtest für gegebene <see cref="StrategyRunInputs"/> aus und liefert ein
/// <see cref="StrategyRunResult"/>. Abstraktion, damit die Research-Orchestrierung (Engine,
/// Sweep, WalkForward, Sensitivity) deterministisch mit einem Fake testbar ist und produktiv
/// die bestehende BacktestEngine nutzt. Sendet niemals echte Orders.
/// </summary>
public interface IStrategyBacktestRunner
{
    Task<StrategyRunResult> RunAsync(StrategyRunInputs inputs, CancellationToken cancellationToken = default);
}
