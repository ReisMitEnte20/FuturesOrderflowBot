using TradingBot.Quant.DataQuality;
using TradingBot.Quant.Metrics;
using TradingBot.Quant.Registry;

namespace TradingBot.DevDashboard.Services.Quant;

// ---------------------------------------------------------------------------------------------
// Anfragen
// ---------------------------------------------------------------------------------------------

/// <summary>Gemeinsame Auswertungseinstellungen. Sie verändern die Kennzahlen und werden mit ausgegeben.</summary>
public sealed record QuantEvaluationOptions
{
    /// <summary>"Bar", "Daily", "Weekly", "Monthly".</summary>
    public string Frequency { get; init; } = "Daily";
    /// <summary>"Observed" (aus der Beobachtungsdichte) oder "Fixed".</summary>
    public string AnnualizationBasis { get; init; } = "Observed";
    public double? FixedPeriodsPerYear { get; init; }
    public double RiskFreeAnnualRate { get; init; }
    public double ExpectedShortfallAlpha { get; init; } = 0.05;
    public int RollingWindow { get; init; } = 20;
    public int MinimumPeriods { get; init; } = 20;
}

public sealed record QuantAnalyzeRequest
{
    public required BacktestRunRequest Run { get; init; }
    public QuantEvaluationOptions Options { get; init; } = new();
    /// <summary>Kennung der Benchmarkreihe; leer = kein Vergleich (es wird keine Kurve erfunden).</summary>
    public string? BenchmarkId { get; init; }
    /// <summary>Bestätigung, dass die Strategie-Equity ein voll finanziertes Konto abbildet.</summary>
    public bool StrategyIsFullyFunded { get; init; }
}

/// <summary>Vorab zu speichernde Kampagnen-Eckdaten. Ohne sie wird keine Suche ausgeführt.</summary>
public sealed record CampaignInput
{
    public required string Id { get; init; }
    public string Name { get; init; } = "";
    public string Hypothesis { get; init; } = "";
    public string SearchSpace { get; init; } = "";
    public string SelectionMetric { get; init; } = "sharpe";
    public int TrialBudget { get; init; }
    public double HoldoutFraction { get; init; }
}

public sealed record QuantWalkForwardRequest
{
    public required BacktestRunRequest Run { get; init; }
    public QuantEvaluationOptions Options { get; init; } = new();

    public string Mode { get; init; } = "Rolling";
    public int TrainBars { get; init; } = 2000;
    public int TestBars { get; init; } = 500;
    public int? StepBars { get; init; }
    public int LabelSpanBars { get; init; }
    public int EmbargoBars { get; init; }
    public int WarmupBars { get; init; }
    public double HoldoutFraction { get; init; } = 0.2;

    /// <summary>Kandidaten-Parametersätze. Die Auswahl je Fenster erfolgt ausschließlich im Training.</summary>
    public IReadOnlyList<Dictionary<string, string>> Candidates { get; init; } = Array.Empty<Dictionary<string, string>>();

    /// <summary>"sharpe", "sortino", "cagr", "calmar" oder "netprofit".</summary>
    public string SelectionMetric { get; init; } = "sharpe";

    /// <summary>Kampagne, unter der die Versuche erfasst werden. Pflicht — sonst keine Suche.</summary>
    public CampaignInput? Campaign { get; init; }
}

public sealed record QuantMonteCarloRequest
{
    public required BacktestRunRequest Run { get; init; }
    public QuantEvaluationOptions Options { get; init; } = new();
    /// <summary>"trades" (NetPnL je Trade, additiv) oder "returns" (Periodenrenditen, multiplikativ).</summary>
    public string Source { get; init; } = "trades";
    /// <summary>"Permutation", "MovingBlock" oder "Stationary".</summary>
    public string Method { get; init; } = "Permutation";
    public int Iterations { get; init; } = 1000;
    public int Seed { get; init; } = 12345;
    public int? BlockLength { get; init; }
    public int? Horizon { get; init; }
    public double? CapitalBarrier { get; init; }
    /// <summary>Benchmark für gemeinsames Resampling (Abhängigkeit bleibt erhalten).</summary>
    public string? JointBenchmarkId { get; init; }
}

public sealed record QuantRobustnessRequest
{
    public required BacktestRunRequest Run { get; init; }
    public QuantEvaluationOptions Options { get; init; } = new();
    public IReadOnlyList<double> FeeMultipliers { get; init; } = new[] { 1.0, 1.5, 2.0, 3.0 };
    public IReadOnlyList<double> SlippageMultipliers { get; init; } = new[] { 1.0, 2.0, 3.0 };
    public IReadOnlyList<int> ExecutionDelays { get; init; } = new[] { 0, 1, 2, 3 };
    public IReadOnlyList<int> ParameterOffsets { get; init; } = new[] { -4, -2, 2, 4 };
    /// <summary>"sharpe", "sortino", "cagr", "calmar" oder "netprofit".</summary>
    public string Metric { get; init; } = "sharpe";
}

public sealed record QuantOverfittingRequest
{
    public required QuantWalkForwardRequest WalkForward { get; init; }
    /// <summary>Zahl der Zeitblöcke S für CSCV (gerade, ≥ 2).</summary>
    public int Blocks { get; init; } = 16;
    /// <summary>Effektive Versuchszahl aus den Kandidatenkorrelationen schätzen statt die tatsächliche zu nehmen.</summary>
    public bool EstimateEffectiveTrials { get; init; } = true;
}

// ---------------------------------------------------------------------------------------------
// Antworten
// ---------------------------------------------------------------------------------------------

/// <summary>Kennzahl inklusive Methode, Eingaben, Grenzen und Datenumfang (Anzeige im Dashboard).</summary>
public sealed record QuantMetricDto(
    string Key, string Label, double? Value, string Unit,
    string Method, string Inputs, string? Limitation, int SampleSize, string? UnavailableReason)
{
    public static QuantMetricDto From(QuantMetric m) =>
        new(m.Key, m.Label, m.IsAvailable ? m.Value : null, m.Unit, m.Method, m.Inputs, m.Limitation, m.SampleSize, m.UnavailableReason);
}

/// <summary>Ein Punkt der Kapitalkurve mit beiden Basen und den zugehörigen Unterwasserwerten.</summary>
public sealed record QuantCurvePointDto(long T, double Realized, double Total, double UwRealized, double UwTotal, int OpenQty);

public sealed record QuantPeriodReturnDto(string Period, long T, double Return, int Observations);

public sealed record QuantRollingPointDto(long T, double? Sharpe, double? Volatility, double? Return);

public sealed record QuantDrawdownDto(
    double Fraction, double Absolute, long? PeakT, long? TroughT, long? RecoveryT,
    int LongestPeriods, double LongestDays, bool UnderwaterAtEnd);

public sealed record QuantDataIssueDto(string Severity, string Code, string Message, int Count, IReadOnlyList<long> Examples);

public sealed record QuantDataQualityDto(
    string Symbol, int TimeframeMinutes, string Timezone, int BarCount, long? FirstT, long? LastT,
    double SpanDays, int ObservedDays, double MedianBarsPerDay,
    bool LeadingPartialExcluded, bool TrailingPartialExcluded,
    IReadOnlyList<QuantDataIssueDto> Issues)
{
    public static QuantDataQualityDto From(QuantDataQualityReport r) => new(
        r.Symbol, r.TimeframeMinutes, r.Timezone, r.BarCount,
        r.First?.ToUnixTimeMilliseconds(), r.Last?.ToUnixTimeMilliseconds(),
        r.SpanDays, r.ObservedDays, r.MedianBarsPerDay,
        r.LeadingPartialExcluded, r.TrailingPartialExcluded,
        r.Issues.Select(i => new QuantDataIssueDto(i.Severity.ToString(), i.Code, i.Message, i.Count,
            i.Examples.Select(e => e.ToUnixTimeMilliseconds()).ToList())).ToList());
}

public sealed record QuantBenchmarkDto(
    bool Available, string? UnavailableReason, string Name, string Provenance,
    int CommonPeriods, IReadOnlyList<QuantMetricDto> Metrics,
    IReadOnlyList<long> T, IReadOnlyList<double> StrategyIndex, IReadOnlyList<double> BenchmarkIndex,
    IReadOnlyList<string> Assumptions, IReadOnlyList<string> Warnings);

public sealed record QuantAnalyzeResponse
{
    public bool Ok { get; init; }
    public string? Error { get; init; }

    public string Symbol { get; init; } = "";
    public string Source { get; init; } = "";
    public int TimeframeMinutes { get; init; }
    public string Currency { get; init; } = "USD";
    public string Frequency { get; init; } = "";
    public double? PeriodsPerYear { get; init; }
    public string AnnualizationNote { get; init; } = "";
    public string RiskFreeNote { get; init; } = "";
    public string MarkToMarketNote { get; init; } = "";

    public int Trades { get; init; }
    public decimal InitialBalance { get; init; }
    public decimal FinalEquityRealized { get; init; }
    public double FinalEquityTotal { get; init; }

    public IReadOnlyList<QuantCurvePointDto> Curve { get; init; } = Array.Empty<QuantCurvePointDto>();
    public IReadOnlyList<QuantMetricDto> MetricsRealized { get; init; } = Array.Empty<QuantMetricDto>();
    public IReadOnlyList<QuantMetricDto> MetricsTotal { get; init; } = Array.Empty<QuantMetricDto>();
    public IReadOnlyList<QuantMetricDto> Activity { get; init; } = Array.Empty<QuantMetricDto>();
    public QuantDrawdownDto? DrawdownRealized { get; init; }
    public QuantDrawdownDto? DrawdownTotal { get; init; }

    public IReadOnlyList<QuantPeriodReturnDto> Monthly { get; init; } = Array.Empty<QuantPeriodReturnDto>();
    public IReadOnlyList<QuantRollingPointDto> Rolling { get; init; } = Array.Empty<QuantRollingPointDto>();
    public int RollingWindow { get; init; }

    public QuantDataQualityDto? DataQuality { get; init; }
    public QuantBenchmarkDto? Benchmark { get; init; }
    public CostProfileDto? Costs { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    public string? TrialId { get; init; }
}

public sealed record QuantFoldDto(
    int Index, long TrainFromT, long TrainToT, int TrainBars, long TestFromT, long TestToT, int TestBars,
    int PurgedBars, int EmbargoBars, string? SelectedCandidate,
    double? TrainValue, double? TestValue, int TestTrades, string? Note);

public sealed record QuantWalkForwardResponse
{
    public bool Ok { get; init; }
    public string? Error { get; init; }

    public string SelectionMetric { get; init; } = "";
    public string Mode { get; init; } = "";
    public int TotalBars { get; init; }
    public IReadOnlyList<QuantFoldDto> Folds { get; init; } = Array.Empty<QuantFoldDto>();

    public long? HoldoutFromT { get; init; }
    public long? HoldoutToT { get; init; }
    public bool HoldoutEvaluated { get; init; }

    public IReadOnlyList<long> OosT { get; init; } = Array.Empty<long>();
    /// <summary>Startanker (ms) jedes OOS-Punkts — die Untergrenze des von <see cref="OosT"/> abgeschlossenen
    /// Renditeintervalls. An Fold-Anfängen der Fenster-/Warmup-Rand, sonst der vorherige Punkt. Ermöglicht einen
    /// intervallgleichen (nicht nur endzeitpunktgleichen) Vergleich mehrerer Strategien. Leer bei Alt-Ergebnissen.</summary>
    public IReadOnlyList<long> OosStartT { get; init; } = Array.Empty<long>();
    public IReadOnlyList<double> OosEquity { get; init; } = Array.Empty<double>();
    public IReadOnlyList<QuantMetricDto> OosMetrics { get; init; } = Array.Empty<QuantMetricDto>();

    /// <summary>Kandidaten-Sharpe je Periode (für DSR und die Anzeige der Versuchsstreuung).</summary>
    public IReadOnlyDictionary<string, double?> CandidateSharpes { get; init; } = new Dictionary<string, double?>();

    public string? CampaignId { get; init; }
    public int TrialsRecorded { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed record QuantDistributionDto(
    string Key, string Label, string Unit,
    double Min, double P5, double P25, double Median, double P75, double P95, double Max, double Mean,
    IReadOnlyList<double> Histogram, double HistogramMin, double HistogramMax);

public sealed record QuantMonteCarloResponse
{
    public bool Ok { get; init; }
    public string? Error { get; init; }

    public string Method { get; init; } = "";
    public string SourceLabel { get; init; } = "";
    public int Iterations { get; init; }
    public int Seed { get; init; }
    public int BlockLength { get; init; }
    public int Horizon { get; init; }
    public int Observations { get; init; }

    public QuantDistributionDto? FinalCapital { get; init; }
    public QuantDistributionDto? MaxDrawdown { get; init; }
    public QuantDistributionDto? LosingStreak { get; init; }

    public double ShareOfRunsBelowStart { get; init; }
    public double? ShareOfRunsBreachingBarrier { get; init; }
    public double? CapitalBarrier { get; init; }

    /// <summary>Simulationsband der Kapitalpfade (P5/Median/P95 je Schritt) — aus ALLEN Läufen. Index 0 = Startkapital.</summary>
    public IReadOnlyList<double> BandP5 { get; init; } = Array.Empty<double>();
    public IReadOnlyList<double> BandMedian { get; init; } = Array.Empty<double>();
    public IReadOnlyList<double> BandP95 { get; init; } = Array.Empty<double>();

    /// <summary>Begrenzte Auswahl tatsächlich berechneter Kapitalpfade (Darstellung). Jeder Pfad: Horizont+1 Punkte, Start = Startkapital.</summary>
    public IReadOnlyList<IReadOnlyList<double>> Paths { get; init; } = Array.Empty<IReadOnlyList<double>>();
    public double InitialCapital { get; init; }
    /// <summary>Zahl der dargestellten Pfade (Darstellungsgrenze).</summary>
    public int DisplayedPaths { get; init; }
    /// <summary>Zahl aller berechneten Läufe, aus denen Band und Kennzahlen stammen.</summary>
    public int TotalPaths { get; init; }

    public IReadOnlyList<string> Assumptions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed record QuantStressCellDto(string Id, string Label, double FeeMultiplier, double SlippageMultiplier,
    int DelayBars, double? Value, int TradeCount, string? Error);

public sealed record QuantStressBlockDto(string Dimension, string MetricKey, double? Baseline,
    double? Worst, double? ShareBelowBaseline, double? RelativeDegradation,
    IReadOnlyList<QuantStressCellDto> Cells, IReadOnlyList<string> Notes);

public sealed record QuantRobustnessResponse
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public string Metric { get; init; } = "";
    public double? Baseline { get; init; }
    public IReadOnlyList<QuantStressBlockDto> Blocks { get; init; } = Array.Empty<QuantStressBlockDto>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed record QuantPboPairDto(double InSample, double OutOfSample);

public sealed record QuantOverfittingResponse
{
    public bool Ok { get; init; }
    public string? Error { get; init; }

    public double? Pbo { get; init; }
    public string? PboUnavailableReason { get; init; }
    public int Candidates { get; init; }
    public int Blocks { get; init; }
    public int Combinations { get; init; }
    public int Observations { get; init; }
    public double? ShareNegativeOutOfSample { get; init; }
    public IReadOnlyList<QuantPboPairDto> Pairs { get; init; } = Array.Empty<QuantPboPairDto>();
    public IReadOnlyList<double> Logits { get; init; } = Array.Empty<double>();
    public IReadOnlyList<string> PboDefinitions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PboNotes { get; init; } = Array.Empty<string>();

    public double? Psr { get; init; }
    public double? ObservedSharpePerPeriod { get; init; }
    public double? Skewness { get; init; }
    public double? Kurtosis { get; init; }
    public double? MinimumTrackRecordLength { get; init; }
    public string? PsrUnavailableReason { get; init; }
    public IReadOnlyList<string> PsrDefinitions { get; init; } = Array.Empty<string>();

    public double? Dsr { get; init; }
    public double? ExpectedMaxSharpeUnderNull { get; init; }
    public int ActualTrials { get; init; }
    public double EffectiveTrials { get; init; }
    public string EffectiveTrialsRationale { get; init; } = "";
    public string? DsrUnavailableReason { get; init; }
    public IReadOnlyList<string> DsrDefinitions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DsrWarnings { get; init; } = Array.Empty<string>();

    public QuantWalkForwardResponse? WalkForward { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed record QuantCampaignDto(string Id, string Name, DateTimeOffset CreatedUtc, string Hypothesis,
    string SearchSpace, string SelectionMetric, string SelectionDirection, int TrialBudget, int TrialsUsed,
    DateTimeOffset? HoldoutFrom, DateTimeOffset? HoldoutTo, bool HoldoutConsumed, bool Locked,
    DateTimeOffset? HoldoutConsumedUtc = null, string? HoldoutEvaluatedReference = null, string? DataSha = null);

/// <summary>Anfrage für den einmaligen Holdout-Verbrauch: Bindung an Kandidat/Konfiguration.</summary>
public sealed record HoldoutConsumeRequest(string? CandidateReference);

/// <summary>Antwort auf den einmaligen, gebundenen Holdout-Verbrauch einer Kampagne.</summary>
public sealed record QuantHoldoutConsumeResponse
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public string? CampaignId { get; init; }
    public bool HoldoutConsumed { get; init; }
    public DateTimeOffset? HoldoutConsumedUtc { get; init; }
    public string? EvaluationReference { get; init; }
    public DateTimeOffset? HoldoutFrom { get; init; }
    public DateTimeOffset? HoldoutTo { get; init; }
}

/// <summary>
/// Anfrage für die EINMALIGE finale Holdout-Auswertung eines bereits ausgewählten Kandidaten. Die
/// Konfiguration (Run) wird eingefroren; der Kandidat muss zur Kampagne gehören. <see cref="Confirm"/>
/// muss true sein — die Auswertung verbraucht den Holdout unwiderruflich.
/// </summary>
public sealed record HoldoutEvaluateRequest
{
    public required BacktestRunRequest Run { get; init; }
    public QuantEvaluationOptions Options { get; init; } = new();
    /// <summary>Eindeutige, menschenlesbare Referenz auf den ausgewählten Kandidaten (Pflicht).</summary>
    public string? CandidateReference { get; init; }
    /// <summary>Optionale Id des zugehörigen Walk-forward-Trials (Herkunftsnachweis, Kampagnenzugehörigkeit).</summary>
    public string? CandidateTrialId { get; init; }
    /// <summary>Warmup-Bars VOR dem Holdout (nur frühere Daten, keine Trades/Kennzahlen im Warmup).</summary>
    public int WarmupBars { get; init; }
    /// <summary>Ausdrückliche Bestätigung, dass dieser Holdout dadurch verbraucht wird.</summary>
    public bool Confirm { get; init; }
}

/// <summary>Antwort auf Statusabfrage bzw. Start der finalen Holdout-Auswertung.</summary>
public sealed record HoldoutEvaluationResponse
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public required string CampaignId { get; init; }
    /// <summary>Available | Reserved | Running | Completed | Failed | Cancelled | ConsumedNoResult | NoHoldout | UnknownCampaign.</summary>
    public string State { get; init; } = "";
    /// <summary>Job-Id des laufenden Auswertungslaufs (für Live-Fortschritt); null nach Neustart oder ohne Lauf.</summary>
    public string? JobId { get; init; }
    /// <summary>True, wenn bereits eine Auswertung existierte und KEIN neuer Lauf gestartet wurde.</summary>
    public bool AlreadyExisted { get; init; }
    /// <summary>Reservierter Holdout-Zeitraum der Kampagne (auch wenn noch nichts ausgewertet wurde).</summary>
    public DateTimeOffset? HoldoutFrom { get; init; }
    public DateTimeOffset? HoldoutTo { get; init; }
    public HoldoutEvaluationRecord? Evaluation { get; init; }
}

public sealed record QuantTrialDto(string Id, string CampaignId, DateTimeOffset CreatedUtc, DateTimeOffset? CompletedUtc,
    string StrategyId, string StrategyVersion, string Origin, string? OriginReference,
    IReadOnlyDictionary<string, string> Parameters, string DataSha256, string DataSource, string DataSymbol,
    int DataBars, DateTimeOffset? DataFrom, DateTimeOffset? DataTo,
    string CodeVersion, int Seed, DateTimeOffset? PeriodFrom, DateTimeOffset? PeriodTo, string PeriodRole,
    string Status, string? StatusReason, IReadOnlyDictionary<string, double?> Metrics,
    CostProfileSnapshot Costs, IReadOnlyList<string> Tags, string? Notes)
{
    public static QuantTrialDto From(TrialRecord t) => new(
        t.Id, t.CampaignId, t.CreatedUtc, t.CompletedUtc, t.StrategyId, t.StrategyVersion,
        t.Origin.ToString(), t.OriginReference, t.Parameters,
        t.Data.Sha256, t.Data.Source, t.Data.Symbol, t.Data.BarCount, t.Data.From, t.Data.To,
        t.CodeVersion, t.Seed, t.PeriodFrom, t.PeriodTo, t.PeriodRole,
        t.Status.ToString(), t.StatusReason, t.Metrics, t.Costs, t.Tags, t.Notes);
}

public sealed record QuantStatusDto(
    string RegistryPath, int Campaigns, int Trials,
    IReadOnlyList<string> Benchmarks, string BenchmarkSource, string BenchmarkDirectory,
    int Papers, string CodeVersion, bool RithmicEnabled);
