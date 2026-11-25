using TradingBot.Quant.Report;

namespace TradingBot.DevDashboard.Services.Quant.Report;

/// <summary>Vorab festgelegtes Analyseprofil des Ein-Knopf-Berichts. Wird mit dem Lauf eingefroren und gespeichert.</summary>
public sealed record FullReportOptions
{
    /// <summary>Mindesttradezahl für Einordnungen (Sensitivität, Simulationsbasis) — darunter "zu wenige Trades".</summary>
    public int MinTrades { get; init; } = 30;
    /// <summary>Mindesttradezahl je Trainingsfenster; Kandidaten darunter nehmen an der Auswahl nicht teil.</summary>
    public int MinTrainTrades { get; init; } = 5;
    /// <summary>Mindesttradezahl je Teilgruppe (Stabilität) — darunter als "dünn besetzt" markiert.</summary>
    public int MinTradesPerGroup { get; init; } = 20;
    public int SimulationIterations { get; init; } = TradeResampling.DefaultIterations;
    public int RandomControlIterations { get; init; } = 1000;
    public double MissedFillShare { get; init; } = 0.10;
    public double FixedSplitTrainShare { get; init; } = 0.7;
    public int FixedSplitMinTrainSessions { get; init; } = 5;
    public int FixedSplitMinTestSessions { get; init; } = 3;
    /// <summary>Mindestzahl OOS-Trades, ab der die Simulation auf OOS-Daten statt auf dem Basislauf rechnet.</summary>
    public int MinOosTradesForSimulation { get; init; } = 20;
    public int RollingWindowTrades { get; init; } = 20;
    /// <summary>Begrenzte Parallelität der Engine-Läufe (Sensitivität/Kontrollen).</summary>
    public int MaxParallelism { get; init; } = 4;
    /// <summary>Referenzstrategie für den Kontrolllauf ("auto" = Donchian 20 bzw. SMA 9/21).</summary>
    public string ReferenceStrategy { get; init; } = "auto";
    /// <summary>Strategieversion (Code-Stand der Strategie zum Startzeitpunkt).</summary>
    public string StrategyVersion { get; init; } = "";
    /// <summary>Numerische, tatsächlich wirksame Parameter für die Sensitivität (inkl. Stop/Ziel, wenn wirksam).</summary>
    public IReadOnlyList<string> SensitivityParameters { get; init; } = Array.Empty<string>();
    /// <summary>Fachlich ausgewählte Parameterpaare für die 2D-Heatmap.</summary>
    public IReadOnlyList<string[]> ParameterPairs { get; init; } = Array.Empty<string[]>();

    // ---- Tick-Pfad: vorab festgelegte Kosten-/Verzögerungs-Szenarien (Annahmen, KEINE Messwerte) ----
    /// <summary>Szenario „höhere Gebühr“: zusätzliche Gebühr je ausgeführter Seite und Kontrakt (Annahme).</summary>
    public decimal StressFeeAddPerSide { get; init; } = 0.50m;
    /// <summary>Szenario „mehr Slippage“: zusätzliche adverse Slippage in Ticks je Fill (Annahme).</summary>
    public decimal StressExtraSlippageTicks { get; init; } = 1m;
    /// <summary>Szenario „Verzögerung“: Fill frühestens n zulässige Trade-Ticks später als im Basislauf (Annahme).</summary>
    public int StressFillDelayTicks { get; init; } = 1;
}

public sealed record ReportProvenance
{
    public string RunId { get; init; } = "";
    public string StrategyId { get; init; } = "";
    public string StrategyName { get; init; } = "";
    public string StrategyVersion { get; init; } = "";
    public string EngineVersion { get; init; } = "";
    public string CodeVersion { get; init; } = "";
    public string DataSha256 { get; init; } = "";
    public string DataSource { get; init; } = "";
    public string DataKind { get; init; } = "";
    public string Symbol { get; init; } = "";
    public int TimeframeMinutes { get; init; }
    public DateTimeOffset? DataFrom { get; init; }
    public DateTimeOffset? DataTo { get; init; }
    public DateTimeOffset? DevelopmentTo { get; init; }
    public DateTimeOffset? HoldoutFrom { get; init; }
    public DateTimeOffset? HoldoutTo { get; init; }
    public IReadOnlyList<string> SessionsUsed { get; init; } = Array.Empty<string>();
    public int HoldoutSessions { get; init; }
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
    public int Quantity { get; init; }
    public decimal InitialBalance { get; init; }
    public CostProfileDto? CostModel { get; init; }
    /// <summary>Instrument-Prüfung zum Startzeitpunkt (Formular-Instrument gegen Datensatz). null = älterer Bericht ohne Prüfung.</summary>
    public string? InstrumentCheck { get; init; }
    public IReadOnlyList<string> FillModel { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, int> Seeds { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, string> AnalysisProfile { get; init; } = new Dictionary<string, string>();
    public int DataFileReads { get; init; }
    public int DataCacheHits { get; init; }
    /// <summary>"ohlc-bars" (bar-basierte OHLC-Engine) oder "orderflow-ticks" (bestehender Tick-Orderflow-Pfad).</summary>
    public string EngineKind { get; init; } = "";
    /// <summary>Welche Daten das SIGNAL erzeugt haben.</summary>
    public string SignalData { get; init; } = "";
    /// <summary>Welche Daten/Regeln die simulierte AUSFÜHRUNG bestimmt haben.</summary>
    public string ExecutionData { get; init; } = "";
}

/// <summary>Eine Übersichtskennzahl mit ausdrücklicher Herkunft (Basislauf, Training, OOS, Simulation).</summary>
public sealed record OverviewFigure(string Key, string Label, double? Value, string Unit, string Basis, string? Reason = null, string? Detail = null);

public sealed record ReportOverview
{
    public IReadOnlyList<OverviewFigure> Figures { get; init; } = Array.Empty<OverviewFigure>();
}

// ---- 01 ----
public sealed record SessionInfo(string Key, int Bars, long FirstT, long LastT, bool Short);
public sealed record CheckResult(string Name, bool? Passed, string Detail);

public sealed record ReportDataSection
{
    public QuantDataQualityDto? Quality { get; init; }
    public string DataKind { get; init; } = "";
    public IReadOnlyList<string> DataKindNotes { get; init; } = Array.Empty<string>();
    public string SessionDefinition { get; init; } = "";
    public IReadOnlyList<SessionInfo> Sessions { get; init; } = Array.Empty<SessionInfo>();
    public double MedianBarsPerSession { get; init; }
    public IReadOnlyList<string> DstTransitions { get; init; } = Array.Empty<string>();
    public int IntraSessionGaps { get; init; }
    public double LargestGapMinutes { get; init; }
    public IReadOnlyList<CheckResult> TemporalChecks { get; init; } = Array.Empty<CheckResult>();
    public IReadOnlyList<string> HistoryLimits { get; init; } = Array.Empty<string>();
}

// ---- 02 / 03 / 04 ----
public sealed record ReportResultSection
{
    public string Basis { get; init; } = "";
    public TradeStatisticsResult Stats { get; init; } = new();
    public CostProfileDto? Costs { get; init; }
    public int EvaluatedBars { get; init; }
    public int SignalsGenerated { get; init; }
    public int MissedEntrySkips { get; init; }
    public int InvalidStopSkips { get; init; }
    /// <summary>Tick-Pfad: von der Engine angenommene Orders bzw. am Datenende offene Position (Diagnostik der Engine).</summary>
    public int? OrdersSubmitted { get; init; }
    public string? OpenPositionAtEnd { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed record ReportEquitySection
{
    public string Basis { get; init; } = "";
    public DrawdownResult ClosedTrades { get; init; } = new();
    public DrawdownResult MarkToMarket { get; init; } = new();
    public LosingStreakResult? LosingStreak { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed record ReportRiskSection
{
    public string Basis { get; init; } = "";
    public string Frequency { get; init; } = "";
    public double? PeriodsPerYear { get; init; }
    public string AnnualizationNote { get; init; } = "";
    public string CapitalBase { get; init; } = "";
    public IReadOnlyList<QuantMetricDto> Metrics { get; init; } = Array.Empty<QuantMetricDto>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

// ---- 05 ----
public sealed record SensitivityPoint(double Value, bool IsBase, bool Valid, string? Error, decimal Net, int Trades,
    double? ProfitFactor, double MaxDrawdown, int PositiveSessions, int Sessions, IReadOnlyList<decimal> SessionNets);

public sealed record SensitivityParam(string Name, double BaseValue, IReadOnlyList<SensitivityPoint> Points, string Classification);

public sealed record SensitivityCell(double X, double Y, bool Valid, decimal Net, int Trades, string? Error);

public sealed record SensitivityPair(string X, string Y, IReadOnlyList<double> XValues, IReadOnlyList<double> YValues,
    IReadOnlyList<SensitivityCell> Cells, double BaseX, double BaseY);

public sealed record ReportSensitivitySection
{
    public string Basis { get; init; } = "";
    public bool Applicable { get; init; }
    public string? Reason { get; init; }
    public int MinTrades { get; init; }
    public string Definition { get; init; } = SensitivityRules.Definition;
    public IReadOnlyList<string> SessionKeys { get; init; } = Array.Empty<string>();
    public IReadOnlyList<SensitivityParam> Parameters { get; init; } = Array.Empty<SensitivityParam>();
    public IReadOnlyList<SensitivityPair> Pairs { get; init; } = Array.Empty<SensitivityPair>();
    public int EngineRuns { get; init; }
}

// ---- 06 ----
/// <summary>
/// Ergebnis je Handelstag. Tick-Walk-forward (neue Berichte): <paramref name="Net"/> = Netto der geschlossenen Trades des Tages
/// PLUS am letzten Tag eines Fensters die offene Fensterend-Bewertung (<paramref name="OpenValuation"/>, getrennt ausgewiesen).
/// Alte Berichte haben <paramref name="OpenValuation"/> = null und bleiben unverändert (nur geschlossene Trades).
/// </summary>
public sealed record SessionResult(string Key, decimal Net, int Trades, decimal? OpenValuation = null);

public sealed record OosFold
{
    public int Index { get; init; }
    public long TrainFromT { get; init; }
    public long TrainToT { get; init; }
    public int TrainSessions { get; init; }
    public long TestFromT { get; init; }
    public long TestToT { get; init; }
    public int TestSessions { get; init; }
    public string? SelectedCandidate { get; init; }
    public IReadOnlyDictionary<string, string>? SelectedParameters { get; init; }
    public double? TrainValue { get; init; }
    public decimal? TestNet { get; init; }
    public int TestTrades { get; init; }
    public decimal BaseNet { get; init; }
    public int BaseTrades { get; init; }
    public string? Note { get; init; }

    // --- Tick-Pfad (Session-genau gespeichert) ---
    /// <summary>Tatsächliche Handelstage (CME-Trade-Date) des Trainings bzw. Tests.</summary>
    public IReadOnlyList<string> TrainSessionIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> TestSessionIds { get; init; } = Array.Empty<string>();
    /// <summary>Werktage im Testzeitraum OHNE Daten (fehlender Datentag ≠ getesteter Tag ohne Trade).</summary>
    public IReadOnlyList<string> MissingDataDays { get; init; } = Array.Empty<string>();
    /// <summary>Je getestetem Handelstag: Netto/Trades des eingefrorenen Gewinners (0 Trades = getestet, kein Einstieg).</summary>
    public IReadOnlyList<SessionResult> TestSessionResults { get; init; } = Array.Empty<SessionResult>();
    public IReadOnlyList<SessionResult> BaseTestSessionResults { get; init; } = Array.Empty<SessionResult>();
    /// <summary>Trainingswert (Auswahlkennzahl) je Kandidat; null = nicht teilnahmeberechtigt (z. B. zu wenige Trades).</summary>
    public IReadOnlyDictionary<string, double?> CandidateTrainValues { get; init; } = new Dictionary<string, double?>();
    public IReadOnlyDictionary<string, int> CandidateTrainTrades { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, string>? BaseParameters { get; init; }
    public decimal? TestFees { get; init; }
    public int TestSignals { get; init; }
    /// <summary>Am Ende des Testfensters offene Position des Gewinners (kein Trade, kein Fill — nur bewertet, siehe <see cref="TestOpenValuation"/>).</summary>
    public string? OpenPositionAtTestEnd { get; init; }

    // --- Fensterend-Bewertung (Tick-Pfad, neue Berichte; null = alter Bericht, nicht nachträglich umgedeutet) ---
    // TestNet/BaseNet bleiben das Netto der GESCHLOSSENEN Trades. Die offene Position wird zum letzten Trade-Tick des Fensters
    // BEWERTET (kein Exit, kein Journal-Fill, keine Exit-Gebühr): Bewertung = unrealisiert brutto − bereits gezahlte Einstiegsgebühr.
    /// <summary>Unrealisiert brutto der offenen Position zum letzten Trade-Tick des Testfensters.</summary>
    public decimal? TestOpenUnrealized { get; init; }
    /// <summary>Bereits gezahlte Einstiegsgebühr der offenen Position (genau eine Seite; keine hypothetische Exit-Gebühr).</summary>
    public decimal? TestOpenEntryFees { get; init; }
    /// <summary>Offene Bewertung = <see cref="TestOpenUnrealized"/> − <see cref="TestOpenEntryFees"/> (0 = flach am Fensterende).</summary>
    public decimal? TestOpenValuation { get; init; }
    /// <summary>Wirtschaftlicher Fensterwert = geschlossenes Netto + offene Bewertung.</summary>
    public decimal? TestValue { get; init; }
    public decimal? BaseOpenValuation { get; init; }
    public decimal? BaseValue { get; init; }
    public string? BaseOpenPositionAtTestEnd { get; init; }
    /// <summary>Trades des eingefrorenen Gewinners im Testfenster (Signale, Fills, Gebühren, Netto) — zum direkten Nachvollzug.</summary>
    public IReadOnlyList<TradeRow> TestTradeRows { get; init; } = Array.Empty<TradeRow>();
}

public sealed record OosVariant
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public string Status { get; init; } = "Pending";
    public string? Reason { get; init; }
    public int SessionsNeeded { get; init; }
    public int SessionsAvailable { get; init; }
    public int TrainSessions { get; init; }
    public int TestSessions { get; init; }
    public string? CampaignId { get; init; }
    public int TrialsRecorded { get; init; }
    public IReadOnlyList<OosFold> Folds { get; init; } = Array.Empty<OosFold>();
    public decimal OosNet { get; init; }
    public int OosTrades { get; init; }
    public int OosSessions { get; init; }
    public decimal BaseNetSameSessions { get; init; }
    public int BaseTradesSameSessions { get; init; }
    public double OosMaxDrawdown { get; init; }
    public double BaseMaxDrawdownSameSessions { get; init; }
    public IReadOnlyList<SessionResult> OosSessionResults { get; init; } = Array.Empty<SessionResult>();
    public IReadOnlyList<SessionResult> BaseSessionResults { get; init; } = Array.Empty<SessionResult>();

    // --- Fensterend-Bewertung (Tick-Pfad, neue Berichte; null = alter Bericht) ---
    /// <summary>Summe der offenen Fensterend-Bewertungen der gewählten Parameter über alle Testfenster.</summary>
    public decimal? OosOpenValuation { get; init; }
    /// <summary>Netto inkl. offener Bewertung = <see cref="OosNet"/> (geschlossen) + <see cref="OosOpenValuation"/>.</summary>
    public decimal? OosValue { get; init; }
    public decimal? BaseOpenValuationSameSessions { get; init; }
    public decimal? BaseValueSameSessions { get; init; }
    /// <summary>Anzahl Testfenster, die mit offener Position endeten (gewählte Parameter).</summary>
    public int? OosOpenWindowEnds { get; init; }
}

public sealed record ReportOosSection
{
    public string SelectionRule { get; init; } = "";
    public string TieRule { get; init; } = "";
    public int MinTrainTrades { get; init; }
    public IReadOnlyList<IReadOnlyDictionary<string, string>> Grid { get; init; } = Array.Empty<IReadOnlyDictionary<string, string>>();
    public int WarmupBars { get; init; }
    public string LeakageRules { get; init; } = "";
    public string HoldoutNote { get; init; } = "";
    public IReadOnlyList<OosVariant> Variants { get; init; } = Array.Empty<OosVariant>();
    /// <summary>Primärvariante (rollend) — Basis für Übersicht, Simulation und Vergleich.</summary>
    public string? PrimaryVariant { get; init; }
    /// <summary>Tick-Pfad: Handelstage des Entwicklungsbereichs, fehlende Werktage (ohne Daten) und reservierte Holdout-Tage.</summary>
    public IReadOnlyList<string> DevelopmentSessionIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MissingDataDays { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> HoldoutSessionIds { get; init; } = Array.Empty<string>();
    public string? SelectionMetric { get; init; }
    /// <summary>Ausdrückliche Regel für offene Positionen am Fensterende (Tick-Pfad, neue Berichte).</summary>
    public string? WindowEndRule { get; init; }
    /// <summary>Welche Zahl die Parameterauswahl steuert (Tick-Pfad, neue Berichte).</summary>
    public string? SelectionFigure { get; init; }
    /// <summary>Kosten-/Verzögerungs-Stress als echte Engine-Neuläufe der eingefrorenen Gewinner (Tick-Pfad, neue Berichte).</summary>
    public ReportTickStressSection? TickStress { get; init; }
}

/// <summary>Eine Zeile der Stresstabelle (Summe über alle Testfenster der Primärvariante).</summary>
public sealed record TickStressRow(string Key, string Label, string Assumption, int ClosedTrades, int OpenEndPositions, decimal Fees,
    decimal ClosedNet, decimal OpenValuation, decimal Value, decimal DiffToBase, int UnfilledOrdersAtEnd, int Windows);

/// <summary>Ein konkreter Trade: welcher Fill sich durch die Verzögerung verschoben hat (Tick-Index im Testfenster, Zeit, Preis).</summary>
public sealed record TickStressFillExample
{
    public string Window { get; init; } = "";
    public string Side { get; init; } = "";
    public string? EntrySignalUtc { get; init; }
    public int? BaseEntryTick { get; init; }
    public string? BaseEntryUtc { get; init; }
    public decimal BaseEntryPrice { get; init; }
    public int? DelayedEntryTick { get; init; }
    public string? DelayedEntryUtc { get; init; }
    public decimal DelayedEntryPrice { get; init; }
    public string? ExitSignalUtc { get; init; }
    public int? BaseExitTick { get; init; }
    public string? BaseExitUtc { get; init; }
    public decimal BaseExitPrice { get; init; }
    public int? DelayedExitTick { get; init; }
    public string? DelayedExitUtc { get; init; }
    public decimal DelayedExitPrice { get; init; }
    public decimal BaseNet { get; init; }
    public decimal DelayedNet { get; init; }
}

public sealed record ReportTickStressSection
{
    public string Status { get; init; } = "Pending";
    public string? Reason { get; init; }
    public string Basis { get; init; } = "";
    public string Variant { get; init; } = "";
    public IReadOnlyList<TickStressRow> Rows { get; init; } = Array.Empty<TickStressRow>();
    /// <summary>true = die Basiszeile reproduziert das Walk-forward-Ergebnis exakt (Trades, Gebühren, Netto, offene Bewertung).</summary>
    public bool BaseReproducesWalkForward { get; init; }
    public TickStressFillExample? FillExample { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

// ---- 07 ----
public sealed record StressRow(string Id, string Label, decimal Net, double MaxDrawdown, int Trades, string Kind);

public sealed record ReportSimulationSection
{
    public string Basis { get; init; } = "";
    public string BasisReason { get; init; } = "";
    public int Iterations { get; init; }
    public int Seed { get; init; }
    public IReadOnlyList<SimulationResult> Methods { get; init; } = Array.Empty<SimulationResult>();
    public IReadOnlyList<StressRow> ExecutionStress { get; init; } = Array.Empty<StressRow>();
    public SimulationResult? MissedFills { get; init; }
    public QuantStressBlockDto? DelayedFills { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

// ---- 08 ----
public sealed record AmbiguityRow(int TradeIndex, decimal AssumedNet, decimal AlternativeNet);

public sealed record ReportCostSection
{
    public string Basis { get; init; } = "";
    public CostBreakevenResult Breakeven { get; init; } = new();
    public QuantStressBlockDto? CostGrid { get; init; }
    public int AmbiguousTrades { get; init; }
    public decimal AmbiguousAssumedNet { get; init; }
    public decimal AmbiguousAlternativeNet { get; init; }
    public IReadOnlyList<AmbiguityRow> AmbiguousRows { get; init; } = Array.Empty<AmbiguityRow>();
    public IReadOnlyList<string> DataLimits { get; init; } = Array.Empty<string>();
}

// ---- 09 / 10 ----
public sealed record ReportStabilitySection
{
    public string Basis { get; init; } = "";
    public int MinTradesPerGroup { get; init; }
    public IReadOnlyList<GroupBreakdown> Breakdowns { get; init; } = Array.Empty<GroupBreakdown>();
    public IReadOnlyList<GroupBreakdown> Regimes { get; init; } = Array.Empty<GroupBreakdown>();
    public int RollingWindow { get; init; }
    public IReadOnlyList<RollingPoint> Rolling { get; init; } = Array.Empty<RollingPoint>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

public sealed record ReportConcentrationSection
{
    public string Basis { get; init; } = "";
    public ConcentrationResult Result { get; init; } = new();
}

// ---- 11 ----
public sealed record ControlRow(string Key, string Label, decimal? Net, int? Trades, double? MaxDrawdown, string Basis, string? Note);

public sealed record TrialHistory(int VariantsTested, int SelectionAttempts, int RegistryTrials, IReadOnlyList<string> Breakdown);

public sealed record ReportControlSection
{
    public string Basis { get; init; } = "";
    public IReadOnlyList<ControlRow> Rows { get; init; } = Array.Empty<ControlRow>();
    public RandomControlResult? Random { get; init; }
    public QuantBenchmarkDto? Benchmark { get; init; }
    public TrialHistory? Trials { get; init; }
    public QuantOverfittingResponse? Overfitting { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

// ---- 12 ----
public sealed record TradeRow(int Index, string Side, long EntryT, long ExitT, decimal EntryPrice, decimal ExitPrice,
    int EntryBar, int ExitBar, decimal Gross, decimal Fees, decimal Net, string ExitReason, bool Ambiguous, bool ClosedAtEnd,
    string Session, double? Mae, double? Mfe, bool ExcursionLowerBound, string ExcursionBasis, decimal StopLoss, decimal TakeProfit,
    string? EntrySignalUtc = null, string? EntrySignalDirection = null, string? ExitSignalUtc = null, string? ExitSignalDirection = null,
    string? EntryTimeUtc = null, string? ExitTimeUtc = null, bool ExcursionMeasured = false);

public sealed record ReportTradeSection
{
    public string Basis { get; init; } = "";
    public BacktestRunRequest? CandleRequest { get; init; }
    public IReadOnlyList<TradeRow> Trades { get; init; } = Array.Empty<TradeRow>();
    public string ExcursionDefinition { get; init; } = "";
    public string TickReplayNote { get; init; } = "";
}

/// <summary>Der vollständige, gespeicherte Bericht eines Ein-Knopf-Laufs.</summary>
public sealed record BacktestReport
{
    public ReportProvenance Provenance { get; init; } = new();
    public ReportOverview Overview { get; init; } = new();
    public ReportDataSection? Data { get; init; }
    public ReportResultSection? Result { get; init; }
    public ReportEquitySection? Equity { get; init; }
    public ReportRiskSection? Risk { get; init; }
    public ReportSensitivitySection? Sensitivity { get; init; }
    public ReportOosSection? Oos { get; init; }
    public ReportSimulationSection? Simulation { get; init; }
    public ReportCostSection? Costs { get; init; }
    public ReportStabilitySection? Stability { get; init; }
    public ReportConcentrationSection? Concentration { get; init; }
    public ReportControlSection? Controls { get; init; }
    public ReportTradeSection? Trades { get; init; }
}
