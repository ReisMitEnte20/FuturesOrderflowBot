using TradingBot.Core.Interfaces;
using TradingBot.Domain.Models;

namespace TradingBot.PaperTrading;

/// <summary>
/// Eingaben für eine Paper-Trading-Session. Marktdaten kommen über einen
/// <see cref="IMarketDataProvider"/> (z. B. Replay/CSV) – KEINE Live-Broker-Anbindung.
///
/// Die Profile sind bewusst NULLABLE: Fehlt eines, startet die Session zwar, aber der
/// RiskManager lehnt JEDES Signal fail-closed ab (MissingInstrumentProfile/MissingFeeProfile/
/// MissingBrokerProfile/MissingRiskConfig). So gibt es genau EINE Durchsetzungsstelle.
/// </summary>
public sealed record PaperTradingRequest
{
    public required IMarketDataProvider MarketData { get; init; }
    public required string Symbol { get; init; }
    public required IStrategy Strategy { get; init; }
    public required TradingAccount Account { get; init; }

    public InstrumentProfile? Instrument { get; init; }
    public FeeProfile? Fee { get; init; }
    public BrokerProfile? Broker { get; init; }
    public RiskConfig? Risk { get; init; }

    public PaperTradingConfiguration Config { get; init; } = new();

    /// <summary>Optional: eigener KillSwitch (Standard: inaktiv).</summary>
    public IKillSwitchService? KillSwitch { get; init; }

    /// <summary>
    /// Optional: eigener SafetyMonitor. Wenn gesetzt, steuert der Aufrufer den Feed-/Broker-Status
    /// selbst (die Engine überschreibt ihn nicht) – z. B. um einen Disconnect zu simulieren.
    /// </summary>
    public ISafetyMonitor? Safety { get; init; }

    /// <summary>Optional deterministisches Reject-Kriterium (simuliert vom Broker abgelehnte Orders).</summary>
    public Func<OrderRequest, bool>? RejectOrder { get; init; }
}
