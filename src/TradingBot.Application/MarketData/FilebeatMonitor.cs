using System.Collections.Concurrent;
using System.Text.Json;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Models;

namespace TradingBot.Application.MarketData;

/// <summary>
/// Filebeat-Integration für zentralisiertes Monitoring und Logging.
/// Protokolliert Events für Elastic Stack und Filebeat Shippers.
/// Thread-safe.
/// </summary>
public sealed class FilebeatMonitor : IFilebeatMonitor
{
    private readonly ILogger<FilebeatMonitor> _logger;
    private readonly ConcurrentDictionary<string, int> _eventCounts = new();
    private DateTimeOffset _lastEventAt = DateTimeOffset.UtcNow;
    private readonly object _metricSync = new();

    public FilebeatMonitor(ILogger<FilebeatMonitor> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void LogFeedHealthEvent(string feedName, MarketDataConnectionState state, string details = "")
    {
        var logEntry = new
        {
            timestamp = DateTimeOffset.UtcNow,
            event_type = "feed_health",
            feed_name = feedName,
            connection_state = state.ToString(),
            details = details,
            severity = state == MarketDataConnectionState.Healthy ? "info" : "warning"
        };

        _logger.LogInformation(
            "Feed Health Event: {@Event}",
            logEntry
        );

        RecordMetric("feed_health");
    }

    public void LogOrderEvent(string orderId, string status, decimal price = 0, long quantity = 0)
    {
        var logEntry = new
        {
            timestamp = DateTimeOffset.UtcNow,
            event_type = "order_execution",
            order_id = orderId,
            status = status,
            price = price,
            quantity = quantity,
            severity = status.Contains("rejected", StringComparison.OrdinalIgnoreCase) ? "warning" : "info"
        };

        _logger.LogInformation(
            "Order Event: {@Event}",
            logEntry
        );

        RecordMetric("order");
    }

    public void LogRiskDecision(string decision, string reason, bool approved)
    {
        var logEntry = new
        {
            timestamp = DateTimeOffset.UtcNow,
            event_type = "risk_decision",
            decision = decision,
            reason = reason,
            approved = approved,
            severity = approved ? "info" : "warning"
        };

        _logger.LogInformation(
            "Risk Decision: {@Event}",
            logEntry
        );

        RecordMetric("risk_decision");
    }

    public void LogStrategySignal(string strategyName, string signalType, decimal value)
    {
        var logEntry = new
        {
            timestamp = DateTimeOffset.UtcNow,
            event_type = "strategy_signal",
            strategy_name = strategyName,
            signal_type = signalType,
            value = value,
            severity = "info"
        };

        _logger.LogInformation(
            "Strategy Signal: {@Event}",
            logEntry
        );

        RecordMetric("signal");
    }

    public void LogPositionUpdate(string instrumentCode, long quantity, decimal pnl)
    {
        var logEntry = new
        {
            timestamp = DateTimeOffset.UtcNow,
            event_type = "position_update",
            instrument = instrumentCode,
            quantity = quantity,
            pnl = pnl,
            severity = "info"
        };

        _logger.LogInformation(
            "Position Update: {@Event}",
            logEntry
        );

        RecordMetric("position");
    }

    public void LogCriticalEvent(string component, string message, Exception? exception = null)
    {
        var logEntry = new
        {
            timestamp = DateTimeOffset.UtcNow,
            event_type = "critical_event",
            component = component,
            message = message,
            exception = exception?.ToString() ?? "",
            severity = "error"
        };

        if (exception != null)
        {
            _logger.LogError(
                exception,
                "Critical Event: {@Event}",
                logEntry
            );
        }
        else
        {
            _logger.LogError(
                "Critical Event: {@Event}",
                logEntry
            );
        }

        RecordMetric("critical_event");
    }

    public FilebeatMetrics GetMetrics()
    {
        lock (_metricSync)
        {
            return new FilebeatMetrics
            {
                TotalOrdersLogged = GetCount("order"),
                TotalSignalsLogged = GetCount("signal"),
                TotalRiskDecisionsLogged = GetCount("risk_decision"),
                TotalCriticalEventsLogged = GetCount("critical_event"),
                LastEventAt = _lastEventAt,
                ActiveComponents = _eventCounts.Keys.ToArray()
            };
        }
    }

    private void RecordMetric(string metricName)
    {
        lock (_metricSync)
        {
            _eventCounts.AddOrUpdate(metricName, 1, (_, count) => count + 1);
            _lastEventAt = DateTimeOffset.UtcNow;
        }
    }

    private int GetCount(string metricName)
    {
        return _eventCounts.TryGetValue(metricName, out var count) ? count : 0;
    }
}
