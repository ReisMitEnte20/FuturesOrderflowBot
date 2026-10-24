namespace TradingBot.Core.Interfaces;

/// <summary>
/// Opt-in-Hook: eine Strategie (bzw. ein Strategie-Wrapper) erhaelt eine Referenz auf den
/// <see cref="IPositionManager"/> des laufenden Backtests, um ihren tatsaechlichen Positions-/Fillzustand
/// abzufragen. Die Referenz wird AUSSCHLIESSLICH LESEND genutzt (nur <see cref="IPositionManager.GetPosition"/>).
/// Hinweis: <see cref="IPositionManager"/> ist selbst NICHT read-only (bietet auch ApplyFill/MarkToMarket/Reconcile) —
/// diese werden ueber diesen Hook bewusst NICHT aufgerufen. Keine Order-/Execution-Faehigkeit.
/// Strategien, die den Hook nicht implementieren, sind unveraendert (der Backtest bindet nur, wenn vorhanden).
/// </summary>
public interface IPositionManagerAware
{
    /// <summary>Wird vom Backtest NACH dem Erstellen des PositionManagers aufgerufen. Nur GetPosition verwenden.</summary>
    void UsePositionManager(IPositionManager positions);
}
