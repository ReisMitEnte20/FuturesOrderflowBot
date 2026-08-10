using Microsoft.Extensions.Logging;
using Moq;
using TradingBot.Application.MarketData;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Tests.MarketData;

public class FilebeatMonitorTests
{
    private readonly Mock<ILogger<FilebeatMonitor>> _mockLogger;
    private readonly FilebeatMonitor _monitor;

    public FilebeatMonitorTests()
    {
        _mockLogger = new Mock<ILogger<FilebeatMonitor>>();
        _monitor = new FilebeatMonitor(_mockLogger.Object);
    }

    [Fact]
    public void LogFeedHealthEvent_WhenHealthy_LogsInfoLevel()
    {
        // Arrange
        string feedName = "NQ";
        var state = MarketDataConnectionState.Healthy;

        // Act
        _monitor.LogFeedHealthEvent(feedName, state, "Feed connected successfully");

        // Assert
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once);
    }

    [Fact]
    public void LogFeedHealthEvent_WhenUnhealthy_LogsWarning()
    {
        // Arrange
        string feedName = "NQ";
        var state = MarketDataConnectionState.Stale;

        // Act
        _monitor.LogFeedHealthEvent(feedName, state, "Feed data is stale");

        // Assert
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once);
    }

    [Fact]
    public void LogOrderEvent_LogsOrderExecution()
    {
        // Arrange
        string orderId = "ORD-001";
        string status = "filled";

        // Act
        _monitor.LogOrderEvent(orderId, status, 4500.50m, 2);

        // Assert
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once);
    }

    [Fact]
    public void LogRiskDecision_LogsApprovedDecision()
    {
        // Arrange
        string decision = "entry_signal";
        bool approved = true;

        // Act
        _monitor.LogRiskDecision(decision, "Max daily loss not exceeded", approved);

        // Assert
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once);
    }

    [Fact]
    public void LogRiskDecision_LogsRejectedDecision()
    {
        // Arrange
        string decision = "entry_signal";
        bool approved = false;

        // Act
        _monitor.LogRiskDecision(decision, "Max contracts limit reached", approved);

        // Assert
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once);
    }

    [Fact]
    public void LogStrategySignal_LogsSignalGeneration()
    {
        // Arrange
        string strategyName = "OrderFlowDivergence";
        string signalType = "buy";
        decimal value = 0.85m;

        // Act
        _monitor.LogStrategySignal(strategyName, signalType, value);

        // Assert
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once);
    }

    [Fact]
    public void LogPositionUpdate_LogsPositionChange()
    {
        // Arrange
        string instrument = "MNQ";
        long quantity = 5;
        decimal pnl = 1250.75m;

        // Act
        _monitor.LogPositionUpdate(instrument, quantity, pnl);

        // Assert
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once);
    }

    [Fact]
    public void LogCriticalEvent_LogsErrorWithoutException()
    {
        // Arrange
        string component = "RiskManager";
        string message = "Broker connection lost";

        // Act
        _monitor.LogCriticalEvent(component, message);

        // Assert
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once);
    }

    [Fact]
    public void LogCriticalEvent_LogsErrorWithException()
    {
        // Arrange
        string component = "MarketDataFeed";
        string message = "CSV parsing failed";
        var exception = new InvalidOperationException("Invalid CSV format");

        // Act
        _monitor.LogCriticalEvent(component, message, exception);

        // Assert
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                exception,
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Once);
    }

    [Fact]
    public void GetMetrics_ReturnsAccurateCounts()
    {
        // Arrange - Log multiple events
        _monitor.LogOrderEvent("ORD-001", "filled");
        _monitor.LogOrderEvent("ORD-002", "rejected");
        _monitor.LogStrategySignal("Strategy1", "buy", 0.8m);
        _monitor.LogRiskDecision("entry", "reason", true);
        _monitor.LogCriticalEvent("Component", "Error message");

        // Act
        var metrics = _monitor.GetMetrics();

        // Assert
        Assert.Equal(2, metrics.TotalOrdersLogged);
        Assert.Equal(1, metrics.TotalSignalsLogged);
        Assert.Equal(1, metrics.TotalRiskDecisionsLogged);
        Assert.Equal(1, metrics.TotalCriticalEventsLogged);
        Assert.NotNull(metrics.LastEventAt);
        Assert.NotEmpty(metrics.ActiveComponents);
    }

    [Fact]
    public void GetMetrics_InitiallyReturnsZeroCounts()
    {
        // Act
        var metrics = _monitor.GetMetrics();

        // Assert
        Assert.Equal(0, metrics.TotalOrdersLogged);
        Assert.Equal(0, metrics.TotalSignalsLogged);
        Assert.Equal(0, metrics.TotalRiskDecisionsLogged);
        Assert.Equal(0, metrics.TotalCriticalEventsLogged);
    }

    [Fact]
    public void MultipleEvents_AreAllLogged()
    {
        // Arrange - Create a burst of events
        int eventCount = 10;

        // Act
        for (int i = 0; i < eventCount; i++)
        {
            _monitor.LogOrderEvent($"ORD-{i:000}", "filled", 4500m + i, 1);
        }

        var metrics = _monitor.GetMetrics();

        // Assert
        Assert.Equal(eventCount, metrics.TotalOrdersLogged);
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()),
            Times.Exactly(eventCount));
    }

    [Fact]
    public void Monitor_IsThreadSafe()
    {
        // Arrange
        var tasks = new List<Task>();
        int threadsCount = 10;
        int eventsPerThread = 20;

        // Act - Log from multiple threads
        for (int t = 0; t < threadsCount; t++)
        {
            int threadId = t;
            var task = Task.Run(() =>
            {
                for (int i = 0; i < eventsPerThread; i++)
                {
                    _monitor.LogOrderEvent($"ORD-{threadId}-{i}", "filled");
                }
            });
            tasks.Add(task);
        }

        Task.WaitAll(tasks.ToArray());

        var metrics = _monitor.GetMetrics();

        // Assert
        Assert.Equal(threadsCount * eventsPerThread, metrics.TotalOrdersLogged);
    }
}
