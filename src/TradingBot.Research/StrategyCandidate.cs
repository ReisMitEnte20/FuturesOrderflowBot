using TradingBot.Core.Interfaces;
using TradingBot.Domain.Models;

namespace TradingBot.Research;

/// <summary>
/// Eine zu untersuchende Strategie: Name, Basis-Config und eine FABRIK, die aus einer
/// (evtl. per Sweep angepassten) <see cref="StrategyConfig"/> eine FRISCHE Strategie-Instanz
/// erzeugt. Frische Instanz pro Lauf → keine Zustandslecks zwischen Backtests.
/// Der Kandidat sendet niemals Orders – er liefert nur die Strategie, die Signale erzeugt.
/// </summary>
public sealed record StrategyCandidate
{
    public required string Name { get; init; }
    public required StrategyConfig BaseConfig { get; init; }
    public required Func<StrategyConfig, IStrategy> CreateStrategy { get; init; }

    /// <summary>Erzeugt eine Strategie mit der effektiven Config (Base + Overrides).</summary>
    public IStrategy Build(StrategyConfig effectiveConfig) => CreateStrategy(effectiveConfig);
}
