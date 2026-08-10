using TradingBot.Domain.Models;

namespace TradingBot.Core.Interfaces;

/// <summary>
/// Integration mit Filebeat zur Überwachung und Metriken-Erfassung.
/// Ergänzt FeedHealthMonitor um zentrale Logging- und Monitoring-Funktionalität
/// für Elastic Stack Integrationszenarien.
/// </summary>
public interface IFilebeatMonitor
{
    /// <summary>Protokolliert einen Feed-Health-Event für Filebeat.</summary>
    void LogFeedHealthEvent(string feedName, MarketDataConnectionState state, string details = "");

    /// <summary>Protokolliert einen Order-Execution-Event.</summary>
    void LogOrderEvent(string orderId, string status, decimal price = 0, long quantity = 0);

    /// <summary>Protokolliert einen Risk-Manager-Decision-Event.</summary>
    void LogRiskDecision(string decision, string reason, bool approved);

    /// <summary>Protokolliert einen Strategy-Signal-Event.</summary>
    void LogStrategySignal(string strategyName, string signalType, decimal value);

    /// <summary>Protokolliert einen Position-Update-Event.</summary>
    void LogPositionUpdate(string instrumentCode, long quantity, decimal pnl);

    /// <summary>Protokolliert einen Error/Critical-Event für Alerting.</summary>
    void LogCriticalEvent(string component, string message, Exception? exception = null);

    /// <summary>Gibt Metriken aus der aktuellen Session zurück.</summary>
    FilebeatMetrics GetMetrics();
}

/// <summary>
/// Aktuelle Metriken für Filebeat/Elastic Monitoring.
/// </summary>
public class FilebeatMetrics
{
    public long TotalOrdersLogged { get; set; }
    public long TotalSignalsLogged { get; set; }
    public long TotalRiskDecisionsLogged { get; set; }
    public long TotalCriticalEventsLogged { get; set; }
    public DateTimeOffset LastEventAt { get; set; }
    public string[] ActiveComponents { get; set; } = Array.Empty<string>();
}
