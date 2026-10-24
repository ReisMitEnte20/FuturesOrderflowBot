namespace TradingBot.Core.Interfaces;

/// <summary>
/// Opt-in-Hook: eine Strategie (bzw. ein Strategie-Wrapper) erhaelt eine READ-ONLY-Referenz auf den
/// <see cref="IPositionManager"/> des laufenden Backtests, um ihren tatsaechlichen Positions-/Fillzustand
/// zu lesen. KEINE Order-/Execution-Faehigkeit: der PositionManager sendet selbst nie Orders.
/// Strategien, die diesen Hook nicht implementieren, sind unveraendert (der Backtest bindet nur, wenn vorhanden).
/// </summary>
public interface IPositionManagerAware
{
    /// <summary>Wird vom Backtest NACH dem Erstellen des PositionManagers aufgerufen (nur lesend nutzen).</summary>
    void UsePositionManager(IPositionManager positions);
}
