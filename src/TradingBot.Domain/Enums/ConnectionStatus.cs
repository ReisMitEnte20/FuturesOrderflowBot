namespace TradingBot.Domain.Enums;

/// <summary>
/// Gesundheitsstatus eines MarketData-Feeds. Fail-closed: <see cref="Unknown"/> gilt als NICHT gesund.
/// </summary>
public enum ConnectionStatus
{
    /// <summary>Status unbekannt (noch kein Tick / nicht ausgewertet) – gilt als nicht gesund.</summary>
    Unknown = 0,
    Connected = 1,
    /// <summary>Verbunden, aber zu lange kein Tick mehr (überaltert).</summary>
    Stale = 2,
    Disconnected = 3
}
