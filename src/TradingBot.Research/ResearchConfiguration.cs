using TradingBot.Research.MonteCarlo;

namespace TradingBot.Research;

/// <summary>Einstellungen eines Research-Laufs (keine Magic Numbers im Code).</summary>
public sealed record ResearchConfiguration
{
    public bool RunMonteCarlo { get; init; } = true;
    public int MonteCarloSimulations { get; init; } = 1000;
    public int MonteCarloSeed { get; init; } = 12345;
    public MonteCarloMethod MonteCarloMethod { get; init; } = MonteCarloMethod.Reshuffle;

    public bool RunRobustness { get; init; } = true;
}
