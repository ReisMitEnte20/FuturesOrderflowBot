using TradingBot.Backtesting;

namespace TradingBot.Research.MonteCarlo;

/// <summary>Resampling-Verfahren der Monte-Carlo-Robustheitsprüfung.</summary>
public enum MonteCarloMethod
{
    /// <summary>Gleiche Trades, zufällige Reihenfolge (Permutation ohne Zurücklegen).
    /// Endgewinn bleibt konstant, nur der Equity-Pfad/Drawdown variiert.</summary>
    Reshuffle = 0,
    /// <summary>Ziehen MIT Zurücklegen (Bootstrap). Endgewinn UND Drawdown variieren.</summary>
    BootstrapWithReplacement = 1
}

/// <summary>Eingabe für die Monte-Carlo-Simulation. Arbeitet auf NetPnL abgeschlossener Trades.</summary>
public sealed record MonteCarloRequest
{
    /// <summary>NetPnL je abgeschlossenem Trade (nach Fees/Slippage).</summary>
    public required IReadOnlyList<decimal> TradeNetPnLs { get; init; }

    public int Simulations { get; init; } = 1000;
    public int Seed { get; init; } = 12345;
    public MonteCarloMethod Method { get; init; } = MonteCarloMethod.Reshuffle;

    /// <summary>Optional: Drawdown-Schwelle für ProbabilityOfDrawdownExceeding.</summary>
    public decimal? DrawdownThreshold { get; init; }

    /// <summary>Optional: Verlusthöhe, ab der ein Lauf als "Ruin" gilt (min. Equity ≤ −Wert).</summary>
    public decimal? RuinThreshold { get; init; }

    public static MonteCarloRequest FromTrades(
        IEnumerable<BacktestTrade> trades, int simulations = 1000, int seed = 12345,
        MonteCarloMethod method = MonteCarloMethod.Reshuffle) => new()
    {
        TradeNetPnLs = trades.Select(t => t.NetPnL).ToList(),
        Simulations = simulations, Seed = seed, Method = method
    };
}

/// <summary>Ergebnis eines einzelnen simulierten Laufs.</summary>
public sealed record MonteCarloRun(decimal FinalNetProfit, decimal MaxDrawdown, decimal MinEquity);

/// <summary>Aggregierte Kennzahlen über alle Simulationsläufe.</summary>
public sealed record MonteCarloStatistics
{
    public int Simulations { get; init; }

    public decimal MedianNetProfit { get; init; }
    public decimal MeanNetProfit { get; init; }
    public decimal NetProfitP5 { get; init; }
    public decimal NetProfitP95 { get; init; }
    public decimal WorstNetProfit { get; init; }
    public decimal BestNetProfit { get; init; }

    public decimal MedianMaxDrawdown { get; init; }
    /// <summary>Drawdown-Wert, den nur 5 % der Läufe ÜBERSCHREITEN (95. Perzentil der Drawdown-Verteilung).</summary>
    public decimal WorstDrawdown5Percent { get; init; }
    public decimal WorstMaxDrawdown { get; init; }

    /// <summary>Anteil der Läufe mit negativem Endgewinn.</summary>
    public decimal ProbabilityOfLoss { get; init; }

    /// <summary>Anteil der Läufe mit MaxDrawdown &gt; DrawdownThreshold (null, wenn keine Schwelle).</summary>
    public decimal? ProbabilityOfDrawdownExceeding { get; init; }

    /// <summary>Anteil der Läufe, deren Equity je ≤ −RuinThreshold fiel (null, wenn keine Schwelle).</summary>
    public decimal? ProbabilityOfRuin { get; init; }

    public static readonly MonteCarloStatistics Empty = new();
}

/// <summary>Vollständiges Monte-Carlo-Ergebnis (Statistik + alle Läufe).</summary>
public sealed record MonteCarloResult
{
    public required MonteCarloMethod Method { get; init; }
    public required MonteCarloStatistics Statistics { get; init; }
    public IReadOnlyList<MonteCarloRun> Runs { get; init; } = Array.Empty<MonteCarloRun>();

    public bool HasData => Runs.Count > 0;
}
