using TradingBot.Research.Ranking;

namespace TradingBot.Research;

/// <summary>Ergebnis eines Research-Laufs: Ergebnisse je Kandidat + Gesamtranking.</summary>
public sealed record ResearchResult
{
    public ResearchRunStatus Status { get; init; } = ResearchRunStatus.NotStarted;
    public string? Message { get; init; }

    public IReadOnlyList<StrategyRunResult> Runs { get; init; } = Array.Empty<StrategyRunResult>();
    public IReadOnlyList<StrategyRankedResult> Ranking { get; init; } = Array.Empty<StrategyRankedResult>();
}
