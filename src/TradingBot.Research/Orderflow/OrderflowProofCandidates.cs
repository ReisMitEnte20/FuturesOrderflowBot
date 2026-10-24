using System.Globalization;
using TradingBot.Application.Strategies.OrderFlow;
using TradingBot.Domain.Models;

namespace TradingBot.Research.Orderflow;

/// <summary>
/// Baut die drei vorhandenen Teststrategien als Kandidaten fuer den Orderflow-Lauf, jeweils in den
/// <see cref="SinglePositionGateStrategy"/> gewickelt (max. 1 Kontrakt, kein Aufstocken wie im Harness).
/// Eine einzige Quelle fuer Konsole UND Tests -> gleiche Isolation/Reproduzierbarkeit.
/// Signalregeln der inneren Strategien bleiben unveraendert.
/// </summary>
public static class OrderflowProofCandidates
{
    public static IReadOnlyList<StrategyCandidate> BuildGated(string symbol, decimal deltaThreshold = DeltaFlowStrategy.DefaultDeltaThreshold)
    {
        StrategyConfig Cfg(string name, IReadOnlyDictionary<string, string>? pars = null) => new()
        { Name = name, Symbol = symbol, Enabled = true, SuggestedContracts = 1, Parameters = pars ?? new Dictionary<string, string>() };

        return new List<StrategyCandidate>
        {
            new()
            {
                Name = "Momentum(Price)", BaseConfig = Cfg("Momentum(Price)"),
                CreateStrategy = _ => new SinglePositionGateStrategy(new MomentumPriceStrategy())
            },
            new()
            {
                Name = "DeltaFlow(OF)",
                BaseConfig = Cfg("DeltaFlow(OF)", new Dictionary<string, string>
                { [DeltaFlowStrategy.ThresholdParameterKey] = deltaThreshold.ToString(CultureInfo.InvariantCulture) }),
                CreateStrategy = c => new SinglePositionGateStrategy(new DeltaFlowStrategy(DeltaFlowStrategy.ThresholdFrom(c)))
            },
            new()
            {
                Name = "Fade(Price)", BaseConfig = Cfg("Fade(Price)"),
                CreateStrategy = _ => new SinglePositionGateStrategy(new FadePriceStrategy())
            },
        };
    }
}
