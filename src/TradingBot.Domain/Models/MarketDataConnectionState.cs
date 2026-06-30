using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models;

/// <summary>
/// Momentaufnahme des Feed-Zustands. Basis für die Disconnect-Erkennung, die später
/// der SafetyMonitor/RiskManager auswertet. Fail-closed: nur explizit Connected ist gesund.
/// </summary>
public sealed record MarketDataConnectionState
{
    public bool IsConnected { get; init; }
    public ConnectionStatus Status { get; init; } = ConnectionStatus.Unknown;

    /// <summary>Zeitstempel des zuletzt empfangenen Ticks (Tick-eigene Zeit).</summary>
    public DateTimeOffset? LastTickTimestamp { get; init; }

    /// <summary>Wanduhr-Zeitpunkt, zu dem der letzte Tick empfangen wurde.</summary>
    public DateTimeOffset? LastTickReceivedAt { get; init; }

    /// <summary>Gesund nur, wenn verbunden UND Status == Connected.</summary>
    public bool IsHealthy => IsConnected && Status == ConnectionStatus.Connected;

    public static readonly MarketDataConnectionState Unknown = new();
}
