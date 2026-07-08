using TradingBot.Domain.Models;

namespace TradingBot.Research.Runner;

/// <summary>
/// Vollständige Eingaben für EINEN Backtest-Lauf im Research-Layer. Über dieses eine Objekt
/// variieren alle Analysen ihre jeweilige Dimension: Sweep → Config, Sensitivity → Fee/Slippage,
/// WalkForward → Ticks-Teilbereich. So bleibt der Runner die einzige Ausführungsstelle.
/// </summary>
public sealed record StrategyRunInputs
{
    public required StrategyCandidate Candidate { get; init; }

    /// <summary>Effektive Config (Base + Overrides), wird der Strategie via Initialize gegeben.</summary>
    public required StrategyConfig Config { get; init; }

    public required IReadOnlyList<MarketTick> Ticks { get; init; }
    public required string Symbol { get; init; }
    public required InstrumentProfile Instrument { get; init; }
    public required FeeProfile Fee { get; init; }
    public required BrokerProfile Broker { get; init; }
    public required RiskConfig Risk { get; init; }
    public required TradingAccount Account { get; init; }

    /// <summary>Slippage-Override in Ticks (null = FeeProfile.EstimatedSlippageTicks).</summary>
    public decimal? SlippageTicksOverride { get; init; }

    public bool DataQualityOk { get; init; } = true;
    public bool CapabilitiesSufficient { get; init; } = true;
}
