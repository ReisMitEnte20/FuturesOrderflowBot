using TradingBot.Domain.Enums;

namespace TradingBot.Core.Interfaces;

/// <summary>
/// Überwacht den technischen Gesundheitszustand (Datenfeed, Broker-Verbindung,
/// Positionsabgleich). Liefert dem RiskManager die Information, ob technisch sicher
/// gehandelt werden darf.
/// </summary>
public interface ISafetyMonitor
{
    SafetyStatus Status { get; }

    /// <summary>True nur, wenn alle Sicherheitsbedingungen erfüllt sind.</summary>
    bool IsSafeToTrade { get; }

    /// <summary>Aktueller Verbindungsstatus des Datenfeeds.</summary>
    bool IsMarketDataConnected { get; }

    /// <summary>Aktueller Verbindungsstatus zum Broker.</summary>
    bool IsBrokerConnected { get; }

    /// <summary>True, wenn ein Abgleich lokale Position ≠ Broker-Position ergeben hat.</summary>
    bool HasPositionMismatch { get; }

    void ReportMarketDataStatus(bool connected);
    void ReportBrokerStatus(bool connected);
    void ReportPositionMismatch(bool mismatchDetected);

    /// <summary>Wird bei Statuswechsel ausgelöst.</summary>
    event EventHandler<SafetyStatus>? StatusChanged;
}
