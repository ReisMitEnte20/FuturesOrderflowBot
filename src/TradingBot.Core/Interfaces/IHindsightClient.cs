using TradingBot.Domain.Models;

namespace TradingBot.Core.Interfaces;

/// <summary>
/// Hindsight API Integration - External trading analysis and insights service.
/// Provides methods to fetch historical analysis, signals, and market insights.
/// </summary>
public interface IHindsightClient
{
    /// <summary>
    /// Fetches historical analysis for a given instrument and time period.
    /// </summary>
    Task<HindsightAnalysis?> GetHistoricalAnalysisAsync(
        string instrumentCode,
        DateTimeOffset from,
        DateTimeOffset to,
        string analysisType = "default",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches current market signals from Hindsight for an instrument.
    /// </summary>
    Task<HindsightSignal?> GetCurrentSignalAsync(
        string instrumentCode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches pattern recognition results for a given price and volume data.
    /// </summary>
    Task<HindsightPattern?> RecognizePatternAsync(
        string instrumentCode,
        decimal[] prices,
        long[] volumes,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches backtesting results comparison from Hindsight.
    /// </summary>
    Task<HindsightBacktestComparison?> GetBacktestComparisonAsync(
        string instrumentCode,
        string strategyName,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Tests the connection to the Hindsight API.
    /// </summary>
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current API status and limits.
    /// </summary>
    Task<HindsightStatus?> GetStatusAsync(CancellationToken cancellationToken = default);
}

/// <summary>Hindsight Analysis Result</summary>
public class HindsightAnalysis
{
    public string InstrumentCode { get; set; } = string.Empty;
    public DateTimeOffset AnalyzedAt { get; set; }
    public DateTimeOffset PeriodFrom { get; set; }
    public DateTimeOffset PeriodTo { get; set; }
    public string AnalysisType { get; set; } = string.Empty;
    
    public decimal HighPrice { get; set; }
    public decimal LowPrice { get; set; }
    public decimal OpenPrice { get; set; }
    public decimal ClosePrice { get; set; }
    public long TotalVolume { get; set; }
    
    public string? Trend { get; set; }  // "uptrend", "downtrend", "consolidation"
    public decimal? Volatility { get; set; }
    public decimal? Momentum { get; set; }
    public string? KeyLevel { get; set; }
    public string? Insight { get; set; }
    
    public Dictionary<string, object>? Metadata { get; set; }
}

/// <summary>Hindsight Trading Signal</summary>
public class HindsightSignal
{
    public string InstrumentCode { get; set; } = string.Empty;
    public DateTimeOffset SignalTime { get; set; }
    public string SignalType { get; set; } = string.Empty;  // "buy", "sell", "hold"
    public decimal Confidence { get; set; }  // 0.0 to 1.0
    public decimal? TargetPrice { get; set; }
    public decimal? StopLoss { get; set; }
    public string? Reason { get; set; }
    public string? Pattern { get; set; }
    public int? TrendStrength { get; set; }  // 1-10
    public Dictionary<string, object>? AdditionalData { get; set; }
}

/// <summary>Hindsight Pattern Recognition Result</summary>
public class HindsightPattern
{
    public string InstrumentCode { get; set; } = string.Empty;
    public DateTimeOffset RecognizedAt { get; set; }
    public string PatternName { get; set; } = string.Empty;
    public decimal MatchPercentage { get; set; }  // 0.0 to 100.0
    public string PatternType { get; set; } = string.Empty;  // "reversal", "continuation", "breakout"
    public decimal? ProjectedTarget { get; set; }
    public decimal? ProjectedStopLoss { get; set; }
    public int HistoricalSuccessRate { get; set; }  // 0-100
    public string? Description { get; set; }
    public DateTime? PatternFormationDate { get; set; }
    public List<string>? SimilarHistoricalPatterns { get; set; }
}

/// <summary>Hindsight Backtest Comparison</summary>
public class HindsightBacktestComparison
{
    public string InstrumentCode { get; set; } = string.Empty;
    public string StrategyName { get; set; } = string.Empty;
    public DateTimeOffset BacktestFrom { get; set; }
    public DateTimeOffset BacktestTo { get; set; }
    
    public decimal TotalReturn { get; set; }
    public decimal Sharpe { get; set; }
    public decimal MaxDrawdown { get; set; }
    public int WinRate { get; set; }  // Percentage
    public int TotalTrades { get; set; }
    
    public HindsightBenchmark? Benchmark { get; set; }
    public List<string>? Recommendations { get; set; }
}

/// <summary>Hindsight Benchmark Data</summary>
public class HindsightBenchmark
{
    public string BenchmarkName { get; set; } = string.Empty;
    public decimal Return { get; set; }
    public decimal Sharpe { get; set; }
    public decimal MaxDrawdown { get; set; }
}

/// <summary>Hindsight API Status</summary>
public class HindsightStatus
{
    public bool IsAvailable { get; set; }
    public string Version { get; set; } = string.Empty;
    public DateTimeOffset LastUpdated { get; set; }
    public int ApiCallsRemaining { get; set; }
    public int ApiCallsLimit { get; set; }
    public DateTime ApiLimitResetTime { get; set; }
    public List<string> SupportedInstruments { get; set; } = new();
    public List<string> SupportedAnalysisTypes { get; set; } = new();
}
