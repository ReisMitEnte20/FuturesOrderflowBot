namespace TradingBot.Research.Ranking;

/// <summary>
/// Rankt Strategie-Ergebnisse über einen NORMALISIERTEN gewichteten Score (Min-Max je Kennzahl
/// über die Kandidatenmenge). Fehlende optionale Kennzahlen zählen neutral (0.5), damit sie
/// weder belohnt noch bestraft werden. Zu wenige Trades / schlechte Datenqualität /
/// unzureichende Capabilities werten den Score ab (Penalty). Deterministisch.
/// </summary>
public sealed class StrategyRankingService
{
    private readonly RankingWeights _w;

    public StrategyRankingService(RankingWeights? weights = null) => _w = weights ?? RankingWeights.Default;

    public IReadOnlyList<StrategyRankedResult> Rank(IReadOnlyList<(string Name, ResearchMetricSet Metrics)> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0) return Array.Empty<StrategyRankedResult>();

        var m = candidates.Select(c => c.Metrics).ToList();

        // Normalisierer je Kennzahl (higher = better; lower-better wird invertiert).
        var netProfit = Norm(m.Select(x => x.NetProfit).ToList(), higherBetter: true);
        var drawdown = Norm(m.Select(x => x.MaxDrawdown).ToList(), higherBetter: false);
        var profitFactor = Norm(m.Select(x => x.ProfitFactor).ToList(), higherBetter: true);
        var expectancy = Norm(m.Select(x => x.Expectancy).ToList(), higherBetter: true);
        var avgPerTrade = Norm(m.Select(x => x.AverageNetPnLPerTrade).ToList(), higherBetter: true);
        var winRate = Norm(m.Select(x => (decimal?)x.WinRate).ToList(), higherBetter: true);
        var mcWorstDd = Norm(m.Select(x => x.MonteCarloWorstDrawdown5).ToList(), higherBetter: false);
        var oosNet = Norm(m.Select(x => x.OutOfSampleNetProfit).ToList(), higherBetter: true);
        var stability = Norm(m.Select(x => x.ParameterStability).ToList(), higherBetter: true);

        var scored = new List<StrategyRankedResult>();
        for (int i = 0; i < candidates.Count; i++)
        {
            decimal composite =
                _w.NetProfit * netProfit[i] +
                _w.MaxDrawdown * drawdown[i] +
                _w.ProfitFactor * profitFactor[i] +
                _w.Expectancy * expectancy[i] +
                _w.AverageNetPnLPerTrade * avgPerTrade[i] +
                _w.WinRate * winRate[i] +
                _w.MonteCarloWorstDrawdown * mcWorstDd[i] +
                _w.OutOfSampleNetProfit * oosNet[i] +
                _w.ParameterStability * stability[i];

            var (penalized, penalties) = ApplyPenalties(composite, m[i]);
            scored.Add(new StrategyRankedResult
            {
                StrategyName = candidates[i].Name,
                CompositeScore = penalized,
                Metrics = m[i],
                Penalties = penalties
            });
        }

        return scored
            .OrderByDescending(r => r.CompositeScore)
            .ThenByDescending(r => r.Metrics.NetProfit) // stabiler Tie-Breaker
            .Select((r, idx) => r with { Rank = idx + 1 })
            .ToList();
    }

    private (decimal Score, IReadOnlyList<string> Penalties) ApplyPenalties(decimal composite, ResearchMetricSet metrics)
    {
        decimal factor = 1m;
        var notes = new List<string>();
        // Wenige Trades = statistisch unzuverlässig -> harte Abwertung (wie schlechte Datenqualität).
        if (metrics.TradeCount < _w.MinTrades) { factor *= 0.3m; notes.Add($"TooFewTrades(<{_w.MinTrades})"); }
        if (!metrics.DataQualityOk) { factor *= 0.3m; notes.Add("PoorDataQuality"); }
        if (!metrics.CapabilitiesSufficient) { factor *= 0.3m; notes.Add("InsufficientCapabilities"); }
        return (composite * factor, notes);
    }

    /// <summary>Min-Max-Normalisierung auf [0,1]; null → 0.5 (neutral); max==min → 0.5.</summary>
    private static decimal[] Norm(IReadOnlyList<decimal?> values, bool higherBetter)
    {
        var present = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        var result = new decimal[values.Count];
        if (present.Count == 0)
        {
            Array.Fill(result, 0.5m);
            return result;
        }
        decimal min = present.Min(), max = present.Max();
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] is not decimal v) { result[i] = 0.5m; continue; }
            decimal n = max > min ? (v - min) / (max - min) : 0.5m;
            result[i] = higherBetter ? n : 1m - n;
        }
        return result;
    }

    private static decimal[] Norm(IReadOnlyList<decimal> values, bool higherBetter)
        => Norm(values.Select(v => (decimal?)v).ToList(), higherBetter);
}
