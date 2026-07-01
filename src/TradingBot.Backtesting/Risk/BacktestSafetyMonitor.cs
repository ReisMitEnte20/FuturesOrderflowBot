using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;

namespace TradingBot.Backtesting.Risk;

/// <summary>
/// SafetyMonitor für Backtests. Standard: gesund (Broker + Feed "verbunden", kein Mismatch).
/// Werte sind einstellbar, um z. B. einen Feed-Abbruch zu simulieren.
/// </summary>
public sealed class BacktestSafetyMonitor : ISafetyMonitor
{
    public BacktestSafetyMonitor(
        bool brokerConnected = true, bool marketDataConnected = true, bool positionMismatch = false)
    {
        IsBrokerConnected = brokerConnected;
        IsMarketDataConnected = marketDataConnected;
        HasPositionMismatch = positionMismatch;
    }

    public bool IsBrokerConnected { get; set; }
    public bool IsMarketDataConnected { get; set; }
    public bool HasPositionMismatch { get; set; }

    public SafetyStatus Status =>
        !IsBrokerConnected || !IsMarketDataConnected || HasPositionMismatch
            ? SafetyStatus.Halted
            : SafetyStatus.Ok;

    public bool IsSafeToTrade => Status == SafetyStatus.Ok;

    public event EventHandler<SafetyStatus>? StatusChanged;

    public void ReportMarketDataStatus(bool connected) { IsMarketDataConnected = connected; Raise(); }
    public void ReportBrokerStatus(bool connected) { IsBrokerConnected = connected; Raise(); }
    public void ReportPositionMismatch(bool mismatchDetected) { HasPositionMismatch = mismatchDetected; Raise(); }

    private void Raise() => StatusChanged?.Invoke(this, Status);
}
