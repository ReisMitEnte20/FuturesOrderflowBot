using TradingBot.Core.Interfaces;
using TradingBot.Domain.Models;

namespace TradingBot.Backtesting;

/// <summary>
/// Eingaben für einen Backtest-Lauf. Marktdaten kommen über einen <see cref="IMarketDataProvider"/>
/// (z. B. ReplayMarketDataProvider aus CSV) – keine Live-Anbindung. Alle Tick-/Fee-Werte stammen
/// aus den übergebenen Profilen (nichts hardcoded).
/// </summary>
public sealed record BacktestRequest
{
    public required IMarketDataProvider MarketData { get; init; }
    public required string Symbol { get; init; }
    public required IStrategy Strategy { get; init; }

    public required InstrumentProfile Instrument { get; init; }
    public required FeeProfile Fee { get; init; }
    public required BrokerProfile Broker { get; init; }
    public required RiskConfig Risk { get; init; }
    public required TradingAccount Account { get; init; }

    public BacktestConfiguration Config { get; init; } = new();

    /// <summary>Optional: eigener KillSwitch (Standard: inaktiv). Ermöglicht Blockade-Szenarien.</summary>
    public IKillSwitchService? KillSwitch { get; init; }

    /// <summary>Optional: eigener SafetyMonitor (Standard: gesund/verbunden).</summary>
    public ISafetyMonitor? Safety { get; init; }

    /// <summary>Optional deterministisches Reject-Kriterium (simuliert abgelehnte Orders).</summary>
    public Func<OrderRequest, bool>? RejectOrder { get; init; }
}
