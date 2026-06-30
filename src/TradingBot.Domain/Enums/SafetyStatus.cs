namespace TradingBot.Domain.Enums;

/// <summary>
/// Gesamtzustand der technischen Sicherheit (Feed, Broker-Verbindung, Positionsabgleich).
/// Bei <see cref="Halted"/> darf nicht gehandelt werden.
/// </summary>
public enum SafetyStatus
{
    Ok = 0,
    Degraded = 1,
    Halted = 2
}
