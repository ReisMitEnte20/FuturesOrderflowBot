using TradingBot.Research.MonteCarlo;
using TradingBot.Research.Ranking;
using TradingBot.Research.Robustness;
using TradingBot.Research.Runner;

namespace TradingBot.Research;

/// <summary>
/// Standard-ResearchEngine: führt je Kandidat einen Backtest (über den Runner) aus, ergänzt
/// Monte-Carlo-Robustheit und einen Overfitting-Report und rankt am Ende alle Kandidaten
/// (Robustheit höher gewichtet als roher Profit). Keine Live-/Broker-/Order-Aktion.
/// </summary>
public sealed class ResearchEngine : IResearchEngine
{
    private readonly IStrategyBacktestRunner _runner;
    private readonly MonteCarloSimulator _monteCarlo = new();
    private readonly RobustnessAnalyzer _robustness;
    private readonly StrategyRankingService _ranking;

    public ResearchEngine(
        IStrategyBacktestRunner runner,
        RobustnessAnalyzer? robustness = null,
        StrategyRankingService? ranking = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _robustness = robustness ?? new RobustnessAnalyzer();
        _ranking = ranking ?? new StrategyRankingService();
    }

    public async Task<ResearchResult> RunAsync(ResearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Candidates.Count == 0)
            return new ResearchResult { Status = ResearchRunStatus.Completed, Message = "Keine Kandidaten." };

        var runs = new List<StrategyRunResult>();

        try
        {
            foreach (var candidate in request.Candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var inputs = new StrategyRunInputs
                {
                    Candidate = candidate,
                    Config = candidate.BaseConfig,
                    Ticks = request.Ticks,
                    Symbol = request.Symbol,
                    Instrument = request.Instrument,
                    Fee = request.Fee,
                    Broker = request.Broker,
                    Risk = request.Risk,
                    Account = request.Account,
                    SlippageTicksOverride = request.SlippageTicksOverride,
                    DataQualityOk = request.DataQualityOk,
                    CapabilitiesSufficient = request.CapabilitiesSufficient
                };

                var run = await _runner.RunAsync(inputs, cancellationToken).ConfigureAwait(false);
                var metrics = run.Metrics;

                MonteCarloResult? mc = null;
                if (request.Configuration.RunMonteCarlo && run.Trades.Count > 0)
                {
                    mc = _monteCarlo.Run(MonteCarloRequest.FromTrades(
                        run.Trades, request.Configuration.MonteCarloSimulations,
                        request.Configuration.MonteCarloSeed, request.Configuration.MonteCarloMethod));
                    metrics = metrics with
                    {
                        MonteCarloWorstDrawdown5 = mc.Statistics.WorstDrawdown5Percent,
                        MonteCarloMedianNetProfit = mc.Statistics.MedianNetProfit,
                        MonteCarloProbabilityOfLoss = mc.Statistics.ProbabilityOfLoss
                    };
                }

                OverfittingReport? robustness = request.Configuration.RunRobustness
                    ? _robustness.Analyze(metrics, run.Trades, mc)
                    : null;

                runs.Add(run with { Metrics = metrics, MonteCarlo = mc, Robustness = robustness });
            }
        }
        catch (OperationCanceledException)
        {
            return new ResearchResult { Status = ResearchRunStatus.Cancelled, Runs = runs };
        }

        var ranking = _ranking.Rank(runs.Select(r => (r.StrategyName, r.Metrics)).ToList());
        return new ResearchResult { Status = ResearchRunStatus.Completed, Runs = runs, Ranking = ranking };
    }
}
