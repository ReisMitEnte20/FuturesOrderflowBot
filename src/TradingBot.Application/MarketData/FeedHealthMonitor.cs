using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Application.MarketData;

/// <summary>
/// Heartbeat-Überwachung des Feeds. Bewertet bei jedem Zugriff den aktuellen Zustand
/// anhand der Uhr (<see cref="IClock"/>) und eines Stale-Timeouts. Fail-closed:
/// ohne empfangenen Tick oder bei überschrittenem Timeout ist der Feed NICHT gesund.
/// Thread-safe.
/// </summary>
public sealed class FeedHealthMonitor : IFeedHealthMonitor
{
    private readonly IClock _clock;
    private readonly TimeSpan _staleTimeout;
    private readonly object _sync = new();

    private bool _connected;
    private DateTimeOffset? _lastTickTimestamp;
    private DateTimeOffset? _lastTickReceivedAt;

    public FeedHealthMonitor(IClock clock, TimeSpan staleTimeout)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        if (staleTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(staleTimeout), "Stale-Timeout muss > 0 sein.");
        _staleTimeout = staleTimeout;
    }

    public void SetConnected(bool connected)
    {
        lock (_sync) _connected = connected;
    }

    public void RecordTick(MarketTick tick)
    {
        ArgumentNullException.ThrowIfNull(tick);
        lock (_sync)
        {
            _lastTickTimestamp = tick.Timestamp;
            _lastTickReceivedAt = _clock.UtcNow;
        }
    }

    public MarketDataConnectionState State
    {
        get { lock (_sync) return Evaluate(); }
    }

    public bool IsHealthy
    {
        get { lock (_sync) return Evaluate().IsHealthy; }
    }

    private MarketDataConnectionState Evaluate()
    {
        ConnectionStatus status;
        if (!_connected)
            status = ConnectionStatus.Disconnected;
        else if (_lastTickReceivedAt is null)
            status = ConnectionStatus.Unknown;               // verbunden, aber noch kein Tick -> fail-closed
        else if (_clock.UtcNow - _lastTickReceivedAt.Value > _staleTimeout)
            status = ConnectionStatus.Stale;
        else
            status = ConnectionStatus.Connected;

        return new MarketDataConnectionState
        {
            IsConnected = _connected,
            Status = status,
            LastTickTimestamp = _lastTickTimestamp,
            LastTickReceivedAt = _lastTickReceivedAt
        };
    }
}
