namespace TradingBot.PaperTrading;

/// <summary>
/// Startet Paper-Trading-Sessions: dieselbe Pipeline wie der Backtest (Strategy → RiskManager →
/// OrderManager → PaperExecutionAdapter → PositionManager), aber als langlebige, steuerbare
/// Session (Start/Stop/Pause/Resume) mit live abfragbarem Zustand.
/// Kann NIEMALS echte Orders senden – Execution ist vollständig simuliert.
/// </summary>
public interface IPaperTradingEngine
{
    /// <summary>Startet eine neue Session; sie läuft im Hintergrund, bis Daten enden oder gestoppt wird.</summary>
    PaperTradingSession Start(PaperTradingRequest request, CancellationToken cancellationToken = default);
}
