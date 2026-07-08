using TradingBot.Research.Statistics;

namespace TradingBot.Research.MonteCarlo;

/// <summary>
/// Monte-Carlo-Robustheitstest auf abgeschlossenen Trades. WICHTIG: KEINE Zukunftsvorhersage –
/// es wird geprüft, wie empfindlich Endgewinn und Drawdown auf die REIHENFOLGE bzw.
/// Zusammensetzung der HISTORISCHEN Trades reagieren (Reshuffle bzw. Bootstrap).
///
/// Deterministisch: gleicher Seed → identisches Ergebnis (System.Random, Fisher-Yates).
/// Alle Werte NetPnL-basiert (Fees/Slippage stecken bereits in den Trade-NetPnLs).
/// </summary>
public sealed class MonteCarloSimulator
{
    public MonteCarloResult Run(MonteCarloRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Simulations <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "Simulations muss > 0 sein.");

        var pool = request.TradeNetPnLs;
        if (pool.Count == 0)
            return new MonteCarloResult { Method = request.Method, Statistics = MonteCarloStatistics.Empty };

        var rng = new Random(request.Seed);
        var runs = new List<MonteCarloRun>(request.Simulations);

        for (int s = 0; s < request.Simulations; s++)
        {
            var sequence = request.Method == MonteCarloMethod.Reshuffle
                ? Reshuffle(pool, rng)
                : Bootstrap(pool, rng);

            runs.Add(new MonteCarloRun(
                FinalNetProfit: EquityStatistics.FinalEquity(sequence),
                MaxDrawdown: EquityStatistics.MaxDrawdown(sequence),
                MinEquity: EquityStatistics.MinEquity(sequence)));
        }

        return new MonteCarloResult
        {
            Method = request.Method,
            Statistics = Summarize(runs, request),
            Runs = runs
        };
    }

    private static List<decimal> Reshuffle(IReadOnlyList<decimal> pool, Random rng)
    {
        var copy = pool.ToList();
        for (int i = copy.Count - 1; i > 0; i--) // Fisher-Yates
        {
            int j = rng.Next(i + 1);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }
        return copy;
    }

    private static List<decimal> Bootstrap(IReadOnlyList<decimal> pool, Random rng)
    {
        var sample = new List<decimal>(pool.Count);
        for (int i = 0; i < pool.Count; i++)
            sample.Add(pool[rng.Next(pool.Count)]);
        return sample;
    }

    private static MonteCarloStatistics Summarize(IReadOnlyList<MonteCarloRun> runs, MonteCarloRequest request)
    {
        var nets = runs.Select(r => r.FinalNetProfit).ToList();
        var drawdowns = runs.Select(r => r.MaxDrawdown).ToList();

        decimal? probDdExceed = request.DrawdownThreshold is decimal ddT
            ? (decimal)runs.Count(r => r.MaxDrawdown > ddT) / runs.Count
            : null;

        decimal? probRuin = request.RuinThreshold is decimal ruinT
            ? (decimal)runs.Count(r => r.MinEquity <= -ruinT) / runs.Count
            : null;

        return new MonteCarloStatistics
        {
            Simulations = runs.Count,
            MedianNetProfit = EquityStatistics.Median(nets),
            MeanNetProfit = EquityStatistics.Mean(nets),
            NetProfitP5 = EquityStatistics.Percentile(nets, 0.05m),
            NetProfitP95 = EquityStatistics.Percentile(nets, 0.95m),
            WorstNetProfit = nets.Min(),
            BestNetProfit = nets.Max(),
            MedianMaxDrawdown = EquityStatistics.Median(drawdowns),
            WorstDrawdown5Percent = EquityStatistics.Percentile(drawdowns, 0.95m),
            WorstMaxDrawdown = drawdowns.Max(),
            ProbabilityOfLoss = (decimal)nets.Count(n => n < 0m) / nets.Count,
            ProbabilityOfDrawdownExceeding = probDdExceed,
            ProbabilityOfRuin = probRuin
        };
    }
}
