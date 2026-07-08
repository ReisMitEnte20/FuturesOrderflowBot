using TradingBot.Domain.Models;
using TradingBot.Research.Ranking;
using TradingBot.Research.Runner;

namespace TradingBot.Research.Sweep;

/// <summary>Ergebnis EINER Parameter-Kombination im Sweep.</summary>
public sealed record ParameterSweepResult
{
    public required ParameterCombination Combination { get; init; }
    public required StrategyRunResult Run { get; init; }
}

/// <summary>Gesamtergebnis eines Parameter-Sweeps inkl. Ranking.</summary>
public sealed record ParameterSweepReport
{
    public required IReadOnlyList<ParameterSweepResult> Results { get; init; }
    public required IReadOnlyList<StrategyRankedResult> Ranking { get; init; }
    public int TotalPossible { get; init; }
    public bool Truncated { get; init; }
}

/// <summary>
/// Testet eine Strategie systematisch über ein <see cref="ParameterGrid"/> (jede Kombination = ein
/// Backtest über den Runner). MaxRuns begrenzt hart. Keine versteckte Optimierung: es werden nur
/// alle Kombinationen ausgeführt, die Ergebnisse gesammelt und nach den Research-Kennzahlen gerankt.
/// Deterministisch bei gleicher Config + gleichem (deterministischem) Runner.
/// </summary>
public sealed class ParameterSweepRunner
{
    private readonly IStrategyBacktestRunner _runner;
    private readonly StrategyRankingService _ranking;

    public ParameterSweepRunner(IStrategyBacktestRunner runner, StrategyRankingService? ranking = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _ranking = ranking ?? new StrategyRankingService();
    }

    public async Task<ParameterSweepReport> RunAsync(
        StrategyCandidate candidate, ParameterGrid grid, StrategyRunInputs template,
        int maxRuns = 1000, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(template);

        var expansion = grid.Expand(maxRuns);
        var results = new List<ParameterSweepResult>(expansion.Combinations.Count);

        foreach (var combo in expansion.Combinations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var effectiveConfig = WithParameters(candidate.BaseConfig, combo.Values);
            var inputs = template with { Candidate = candidate, Config = effectiveConfig };
            var run = await _runner.RunAsync(inputs, cancellationToken).ConfigureAwait(false);
            results.Add(new ParameterSweepResult { Combination = combo, Run = run });
        }

        var ranking = _ranking.Rank(
            results.Select(r => ($"{candidate.Name}#{r.Combination.Index} [{r.Combination.Describe()}]", r.Run.Metrics)).ToList());

        return new ParameterSweepReport
        {
            Results = results,
            Ranking = ranking,
            TotalPossible = expansion.TotalPossible,
            Truncated = expansion.Truncated
        };
    }

    /// <summary>Merged die Sweep-Parameter in die StrategyConfig.Parameters (Overrides gewinnen).</summary>
    private static StrategyConfig WithParameters(StrategyConfig baseConfig, IReadOnlyDictionary<string, string> overrides)
    {
        var merged = new Dictionary<string, string>(baseConfig.Parameters, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in overrides) merged[k] = v;
        return baseConfig with { Parameters = merged };
    }
}
