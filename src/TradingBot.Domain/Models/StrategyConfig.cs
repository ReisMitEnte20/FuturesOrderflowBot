using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models;

/// <summary>
/// Konfiguration einer Strategie-Instanz. Parameter bleiben generisch (Key/Value),
/// damit neue Orderflow-Strategien ohne Modelländerung konfigurierbar sind.
/// Vorschlagswerte (Contracts/Stop/TakeProfit) sind KEINE Order-Parameter – der
/// RiskManager/OrderManager entscheidet, was tatsächlich ausgeführt wird.
/// </summary>
public sealed record StrategyConfig
{
    public required string Name { get; init; }
    public required string Symbol { get; init; }
    public bool Enabled { get; init; }

    /// <summary>Primär benötigter Datentyp; steuert das Event-Routing der StrategyEngine.</summary>
    public StrategyDataType RequiredDataType { get; init; } = StrategyDataType.Tick;

    /// <summary>Max. Signale pro Session (null = unbegrenzt); wird von der Engine durchgesetzt.</summary>
    public int? MaxSignalsPerSession { get; init; }

    /// <summary>Vorgeschlagene Kontraktanzahl, falls die Strategie keine setzt.</summary>
    public int SuggestedContracts { get; init; } = 1;

    public int? StopLossTicks { get; init; }
    public int? TakeProfitTicks { get; init; }

    /// <summary>Strategie-spezifische Parameter (z. B. "MinDelta" -&gt; "500").</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; }
        = new Dictionary<string, string>();
}
