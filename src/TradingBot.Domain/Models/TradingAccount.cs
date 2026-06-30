using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models;

/// <summary>
/// Handelskonto. Trennt <see cref="AccountId"/> klar von <see cref="BrokerName"/>.
/// </summary>
public sealed record TradingAccount
{
    public required string AccountId { get; init; }
    public required string BrokerName { get; init; }
    public AccountType AccountType { get; init; }
    public string Currency { get; init; } = "USD";

    /// <summary>Startkapital / Kontostand zu Tagesbeginn (für Drawdown-Berechnung).</summary>
    public decimal StartingBalance { get; init; }
}
