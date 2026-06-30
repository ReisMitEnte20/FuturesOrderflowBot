using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models;

/// <summary>
/// Broker-spezifische Fähigkeiten und API-Regeln. Wird aus JSON geladen.
/// Keine Order darf ohne gültiges BrokerProfile erzeugt werden.
/// </summary>
public sealed record BrokerProfile
{
    public required string BrokerName { get; init; }
    public required string ExecutionProvider { get; init; }
    public AccountType AccountType { get; init; }
    public string AccountCurrency { get; init; } = "USD";

    public bool SupportsMarketOrders { get; init; }
    public bool SupportsLimitOrders { get; init; }
    public bool SupportsStopOrders { get; init; }
    public bool SupportsBracketOrders { get; init; }
    public bool SupportsOcoOrders { get; init; }
    public bool SupportsServerSideStops { get; init; }
    public bool SupportsClientSideStops { get; init; }
    public bool SupportsCancelReplace { get; init; }

    public int MaxOrdersPerSecond { get; init; }
    public int ApiRateLimit { get; init; }

    /// <summary>Frei konfigurierbares Verhalten bei Reconnect (z. B. "ResumeState").</summary>
    public string ReconnectBehavior { get; init; } = string.Empty;

    /// <summary>Frei konfigurierbares Verhalten bei Teilfüllungen (z. B. "TrackPartial").</summary>
    public string PartialFillBehavior { get; init; } = string.Empty;
}
