using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;

namespace TradingBot.PaperTrading.Risk;

/// <summary>
/// SafetyMonitor für Paper-Sessions. Standard: gesund (Broker + Feed "verbunden", kein Mismatch).
/// Zur Laufzeit veränderbar, um Disconnects/Mismatch zu simulieren – der RiskManager blockt dann
/// fail-closed jede neue Order.
/// </summary>
public sealed class PaperSafetyMonitor : ISafetyMonitor
{
    public PaperSafetyMonitor(
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
