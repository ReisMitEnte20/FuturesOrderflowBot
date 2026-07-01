namespace TradingBot.Backtesting;

/// <summary>
/// Führt einen deterministischen Backtest durch: Marktdaten → Strategy → RiskManager →
/// OrderManager → BacktestExecutionAdapter → PositionManager → Kennzahlen.
/// Kann NIEMALS echte Orders senden (kein Live-Adapter, keine Netzwerkverbindung).
/// </summary>
public interface IBacktestEngine
{
    Task<BacktestResult> RunAsync(BacktestRequest request, CancellationToken cancellationToken = default);
}
