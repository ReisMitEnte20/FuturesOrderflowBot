namespace TradingBot.DevDashboard.Services.Quant;

/// <summary>
/// Status eines einzelnen Research-Schritts. "NotComputable" bedeutet: bewusst nicht berechenbar (z. B.
/// fehlende Benchmark, zu wenige PBO-Kandidaten) — kein Fehler, blockiert die unabhängigen Schritte nicht.
/// "Skipped" heißt: eine Voraussetzung (z. B. der Walk-forward) fehlt, daher wurde dieser Folgeschritt
/// nicht ausgeführt.
/// </summary>
public enum ResearchStepStatus
{
    Pending = 0,
    Running = 1,
    Completed = 2,
    NotComputable = 3,
    Failed = 4,
    Cancelled = 5,
    Skipped = 6
}

/// <summary>Zustand eines Schritts in der Ablaufsteuerung (dauerhaft mit dem Lauf gespeichert).</summary>
public sealed record ResearchStepState
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public ResearchStepStatus Status { get; init; } = ResearchStepStatus.Pending;
    /// <summary>Grund bei NotComputable/Failed/Skipped/Cancelled.</summary>
    public string? Reason { get; init; }
    /// <summary>Datenbasis dieses Ergebnisblocks (z. B. "Vollständige Historie", "Walk-forward-OOS", "Holdout").</summary>
    public string? DataBasis { get; init; }
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? CompletedUtc { get; init; }
}

/// <summary>
/// Konfiguration eines Research-Laufs. Wird beim Start eingefroren und dauerhaft gespeichert; alle
/// Teil-Anfragen (Analyse, Walk-forward, Robustheit, Monte Carlo, Overfitting) werden hieraus abgeleitet.
/// </summary>
public sealed record ResearchStartRequest
{
    public required BacktestRunRequest Run { get; init; }
    public QuantEvaluationOptions Options { get; init; } = new();
    public required CampaignInput Campaign { get; init; }

    /// <summary>Vorab festgelegte Kandidaten-Parametersätze (kein selbstlernendes Generieren).</summary>
    public IReadOnlyList<Dictionary<string, string>> Candidates { get; init; } = Array.Empty<Dictionary<string, string>>();
    public string SelectionMetric { get; init; } = "sharpe";

    // Walk-forward
    public string Mode { get; init; } = "Rolling";
    public int TrainBars { get; init; } = 2000;
    public int TestBars { get; init; } = 500;
    public int? StepBars { get; init; }
    public int LabelSpanBars { get; init; }
    public int EmbargoBars { get; init; }
    public int WarmupBars { get; init; }
    public double HoldoutFraction { get; init; } = 0.2;

    // Monte Carlo
    public string MonteCarloSource { get; init; } = "trades";
    public string MonteCarloMethod { get; init; } = "Permutation";
    public int MonteCarloIterations { get; init; } = 1000;
    public int Seed { get; init; } = 12345;
    public int? BlockLength { get; init; }
    public double? CapitalBarrier { get; init; }

    // Robustheit
    public string RobustnessMetric { get; init; } = "sharpe";

    // Overfitting
    public int OverfittingBlocks { get; init; } = 16;
    public bool EstimateEffectiveTrials { get; init; } = true;

    // Benchmark
    public string? BenchmarkId { get; init; }
    public bool StrategyIsFullyFunded { get; init; }

    /// <summary>Kennzeichnet einen Demonstrations-/Abnahmelauf (verbraucht keinen echten Forschungs-Holdout).</summary>
    public bool IsDemo { get; init; }

    /// <summary>Mindestanzahl gemeinsamer OOS-Perioden für einen fachlich belastbaren Vergleich
    /// (PRODUKT-/METHODENKONVENTION, keine universelle statistische Garantie). Darunter: kein Sieger,
    /// keine Erfolgswahrscheinlichkeit. Default 20.</summary>
    public int MinComparisonObservations { get; init; } = 20;
}

/// <summary>
/// Nach den übrigen Prüfungen vorbereiteter Holdout-Kandidat. Wird NICHT automatisch ausgewertet — die
/// finale Holdout-Auswertung verlangt eine gesonderte, ausdrückliche Bestätigung.
/// </summary>
public sealed record ResearchHoldoutProposal
{
    public string? CandidateTrialId { get; init; }
    public string? CandidateReference { get; init; }
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
    /// <summary>Auswahlgrund (z. B. "in 3 von 4 Fenstern out-of-sample ausgewählt").</summary>
    public string Reason { get; init; } = "";
    public DateTimeOffset? HoldoutFrom { get; init; }
    public DateTimeOffset? HoldoutTo { get; init; }
    public int WarmupBars { get; init; }
    /// <summary>True, wenn der Holdout ausgewertet werden KANN (Kandidat gefunden, Fenster vorhanden, nicht verbraucht).</summary>
    public bool Available { get; init; }
    public string? UnavailableReason { get; init; }
    /// <summary>Bereits vorhandene Holdout-Auswertung dieser Kampagne (falls schon bestätigt/ausgewertet).</summary>
    public HoldoutEvaluationResponse? Existing { get; init; }
}

/// <summary>
/// Dauerhaft gespeicherter Research-Lauf: eingefrorene Konfiguration, Datenbezug, Schrittstatus und die
/// fertigen Teilergebnisse. Überlebt einen Server-Neustart. Ein unterbrochener Lauf wird beim Laden ehrlich
/// als solcher dargestellt (kein heimliches Wiederholen).
/// </summary>
public sealed record ResearchRunRecord
{
    public required string RunId { get; init; }
    public string? CampaignId { get; init; }
    public required ResearchStartRequest Config { get; init; }
    public string? DataSha { get; init; }
    public bool IsDemo { get; init; }

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedUtc { get; init; }

    // --- Herkunft/Datenbereiche (Befund B) + Leakage-Schutz (Befund A) ---
    /// <summary>Tatsächlich geladener Datenbereich (erste/letzte Kerze).</summary>
    public DateTimeOffset? DataFrom { get; init; }
    public DateTimeOffset? DataTo { get; init; }
    /// <summary>Exklusive Obergrenze des ENTWICKLUNGSbereichs = OpenTime der ersten Holdout-Kerze. Alle Vorprüfungen
    /// (Basis-Backtest, Benchmark, Robustheit, Monte Carlo, Kerzenchart) laufen ausschließlich VOR dieser Grenze.</summary>
    public DateTimeOffset? DevelopmentToUtc { get; init; }
    /// <summary>Reservierter finaler Holdout-Zeitraum (nur für die finale Auswertung, nie in den Vorprüfungen).</summary>
    public DateTimeOffset? HoldoutFrom { get; init; }
    public DateTimeOffset? HoldoutTo { get; init; }
    public int TotalBars { get; init; }
    public int DevelopmentBars { get; init; }
    public int HoldoutBars { get; init; }
    /// <summary>Die auf den Entwicklungsbereich begrenzte Backtest-Konfiguration (für den Kerzenchart im Frontend —
    /// KEINE Holdout-Daten). Null, wenn kein Holdout reserviert ist (dann gilt <see cref="ResearchStartRequest.Run"/>).</summary>
    public BacktestRunRequest? DevRun { get; init; }

    /// <summary>Gesamtstatus des Laufs: Running/Completed/Failed/Cancelled (bzw. Pending vor Start).</summary>
    public ResearchStepStatus Status { get; init; } = ResearchStepStatus.Pending;
    public string? StatusReason { get; init; }
    /// <summary>Job-Id des laufenden Auswertungsjobs (für Live-Fortschritt); null nach Neustart.</summary>
    public string? JobId { get; init; }

    public IReadOnlyList<ResearchStepState> Steps { get; init; } = Array.Empty<ResearchStepState>();

    // Teilergebnisse (null bis berechnet).
    public QuantAnalyzeResponse? Analysis { get; init; }
    public QuantWalkForwardResponse? WalkForward { get; init; }
    public QuantRobustnessResponse? Robustness { get; init; }
    public QuantMonteCarloResponse? MonteCarlo { get; init; }
    public QuantOverfittingResponse? Overfitting { get; init; }

    public ResearchHoldoutProposal? HoldoutProposal { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    // --- Mehrstrategie-Vergleich: Zuordnung eines Laufs zu einer Kampagnen-Gruppe und Strategie-Familie ---
    /// <summary>Eindeutige Id der übergreifenden Vergleichs-Kampagne (mehrere Familien-Läufe teilen sie). Null bei Einzellauf.</summary>
    public string? CampaignGroupId { get; init; }
    /// <summary>Stabiler Gruppen-Schlüssel (unveränderte Basis-Kampagnen-Id) für den Mehrfachstart-Schutz.</summary>
    public string? GroupKey { get; init; }
    /// <summary>Schlüssel der Strategie-Familie innerhalb der Gruppe (stabil, z. B. die Strategie-Id).</summary>
    public string? FamilyKey { get; init; }
    public string? FamilyName { get; init; }
}

// =================================================================================================
// Mehrstrategie-Vergleich (Sektion 2–4): mehrere Strategie-Familien, EINE Kampagne, gepaarter OOS-Vergleich.
// =================================================================================================

/// <summary>Eine Strategie-Familie im Mehrstrategie-Start: eigener Suchraum (Kandidaten) über EINER Strategie.</summary>
public sealed record ResearchFamilyInput
{
    /// <summary>Stabiler Familien-Schlüssel (z. B. die Strategie-Id). Muss innerhalb der Kampagne eindeutig sein.</summary>
    public required string Key { get; init; }
    /// <summary>Strategie-Id (z. B. "movingaverage", "donchian").</summary>
    public required string StrategyId { get; init; }
    public string? Name { get; init; }
    /// <summary>Vorab festgelegter Suchraum dieser Familie (Kandidaten-Parametersätze; keine Generierung).</summary>
    public IReadOnlyList<Dictionary<string, string>> Candidates { get; init; } = Array.Empty<Dictionary<string, string>>();
}

/// <summary>
/// Start einer Vergleichs-Kampagne über mehrere Strategie-Familien. Der Basis-Run legt Symbol/Timeframe/Daten/
/// Kosten/Kapital fest; je Familie wird nur die Strategie und deren Suchraum ausgetauscht. Alle Familien nutzen
/// DIESELBEN Splits, Fenster, Seeds und Kostenkonventionen (vor der Suche eingefroren).
/// </summary>
public sealed record ResearchCampaignStartRequest
{
    public required BacktestRunRequest Run { get; init; }
    public QuantEvaluationOptions Options { get; init; } = new();
    public required CampaignInput Campaign { get; init; }
    public required IReadOnlyList<ResearchFamilyInput> Families { get; init; }

    public string SelectionMetric { get; init; } = "sharpe";
    public string Mode { get; init; } = "Rolling";
    public int TrainBars { get; init; } = 2000;
    public int TestBars { get; init; } = 500;
    public int? StepBars { get; init; }
    public int LabelSpanBars { get; init; }
    public int EmbargoBars { get; init; }
    public int WarmupBars { get; init; }
    public double HoldoutFraction { get; init; } = 0.2;

    public string MonteCarloSource { get; init; } = "trades";
    public string MonteCarloMethod { get; init; } = "MovingBlock";
    public int MonteCarloIterations { get; init; } = 1000;
    public int Seed { get; init; } = 12345;
    public int? BlockLength { get; init; }
    public double? CapitalBarrier { get; init; }
    public string RobustnessMetric { get; init; } = "sharpe";
    public int OverfittingBlocks { get; init; } = 16;
    public bool EstimateEffectiveTrials { get; init; } = true;
    public string? BenchmarkId { get; init; }
    public bool StrategyIsFullyFunded { get; init; }
    public bool IsDemo { get; init; }
    /// <summary>Mindestanzahl gemeinsamer OOS-Perioden für einen belastbaren Vergleich (Konvention). Default 20.</summary>
    public int MinComparisonObservations { get; init; } = 20;
}

/// <summary>Kurzüberblick einer Vergleichs-Kampagne (für die Auswahlliste im Frontend).</summary>
public sealed record ResearchCampaignSummary(
    string GroupId, string Name, DateTimeOffset CreatedUtc, int Families, ResearchStepStatus Status);

/// <summary>
/// Gemeinsame finale Holdout-Sperre EINER Vergleichs-Gruppe. Schützt den EINEN reservierten Zeitraum der gesamten
/// Gruppe: sobald eine Familie mit ihrem Kandidaten reserviert ist, ist der finale Holdout für die ganze Gruppe
/// gebunden — keine zweite Familie darf unabhängig auswerten, und der gewählte Kandidat wechselt nicht mehr.
/// </summary>
public sealed record ResearchGroupHoldout
{
    public required string GroupId { get; init; }
    public bool Reserved { get; init; }
    public bool Consumed { get; init; }
    public string? SelectedFamilyKey { get; init; }
    public string? SelectedCampaignId { get; init; }
    public string? CandidateTrialId { get; init; }
    public string? CandidateReference { get; init; }
    public string? EvaluationReference { get; init; }
    public DateTimeOffset? ReservedUtc { get; init; }
    public DateTimeOffset? ConsumedUtc { get; init; }
}

/// <summary>Anfrage für die finale Holdout-Auswertung EINER Familie über die Gruppensperre.</summary>
public sealed record ResearchGroupHoldoutEvaluateRequest
{
    public required string FamilyKey { get; init; }
    public required HoldoutEvaluateRequest Request { get; init; }
}

/// <summary>Antwort auf Start/Status einer Vergleichs-Kampagne: die Familien-Läufe und (sobald möglich) der Vergleich.</summary>
public sealed record ResearchCampaignResponse
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public bool AlreadyRunning { get; init; }
    public string? GroupId { get; init; }
    public IReadOnlyList<ResearchRunRecord> Families { get; init; } = Array.Empty<ResearchRunRecord>();
    public ResearchComparison? Comparison { get; init; }
    /// <summary>Zustand der gemeinsamen finalen Holdout-Sperre der Gruppe (null = noch nicht reserviert).</summary>
    public ResearchGroupHoldout? Holdout { get; init; }
}

/// <summary>Antwort auf die finale Holdout-Auswertung über die Gruppensperre.</summary>
public sealed record ResearchGroupHoldoutResponse
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public string? GroupId { get; init; }
    public ResearchGroupHoldout? Holdout { get; init; }
    public HoldoutEvaluationResponse? Evaluation { get; init; }
}

/// <summary>Eine Zeile der Vergleichstabelle (eine Strategie-Familie).</summary>
public sealed record ComparisonFamilyRow
{
    public required string FamilyKey { get; init; }
    public required string FamilyName { get; init; }
    public required string StrategyId { get; init; }
    public string? RunId { get; init; }
    public ResearchStepStatus Status { get; init; }
    public int Candidates { get; init; }
    public string? SelectedCandidate { get; init; }
    public int OosObservations { get; init; }
    /// <summary>Endrendite über die verkettete OOS-Reihe (Bruchteil). Aus der Equity, nicht aus Metrik-Keys.</summary>
    public double? OosReturn { get; init; }
    public double? OosSharpe { get; init; }
    public double? OosMaxDrawdown { get; init; }
    // Gepaarte Monte-Carlo-Kennzahlen (aus dem gemeinsamen Bootstrap, daher vergleichbar):
    public double? McMedianFinal { get; init; }
    public double? McP5Final { get; init; }
    public double? McP95Final { get; init; }
    public double? McShareBelowStart { get; init; }
    public double? McMedianMaxDrawdown { get; init; }
    public double? McMedianLosingStreak { get; init; }
    // Overfitting (auf dem EIGENEN Kandidaten-Auswahlprozess der Familie):
    public double? Pbo { get; init; }
    public double? Psr { get; init; }
    public double? Dsr { get; init; }
    /// <summary>Kapitalkurve auf der gemeinsamen Zeitachse (normiert auf das gemeinsame Startkapital).</summary>
    public IReadOnlyList<double>? SharedOosEquity { get; init; }
}

/// <summary>Eine gepaarte Differenz (Links − Rechts) über die identischen Bootstrap-Pfade.</summary>
public sealed record ComparisonDifference
{
    public required string Left { get; init; }
    public required string Right { get; init; }
    public double DeltaMedianFinal { get; init; }
    public double DeltaP5Final { get; init; }
    public double DeltaP95Final { get; init; }
    public double ShareLeftBeatsRight { get; init; }
    public double ShareTie { get; init; }
}

/// <summary>Der gemeinsame Vergleich über die zeitlich ausgerichteten Netto-OOS-Renditen der Familien.</summary>
public sealed record ResearchComparison
{
    /// <summary>Technisch BERECHENBAR (genug ausgerichtete Punkte, um überhaupt zu resamplen). Sagt NICHTS über
    /// die fachliche Belastbarkeit aus — dafür siehe <see cref="Sufficient"/>.</summary>
    public bool Available { get; init; }
    public string? UnavailableReason { get; init; }
    /// <summary>Anzahl ECHTER gemeinsamer Renditeperioden mit übereinstimmendem Zeitintervall (kein Null-Fill).</summary>
    public int CommonObservations { get; init; }
    public long? FromT { get; init; }
    public long? ToT { get; init; }
    /// <summary>Frequenz der verglichenen Renditen (z. B. "Bar", "Daily").</summary>
    public string Frequency { get; init; } = "";
    /// <summary>Fachlich BELASTBAR: genug gemeinsame Perioden für eine ernsthafte Bewertung (Konvention, siehe
    /// <see cref="MinObservations"/>). Ist dies false, gibt es KEINEN Sieger, keine Erfolgswahrscheinlichkeit und
    /// keinen positiven Freigabestatus — nur eine technische Demonstration des Verfahrens.</summary>
    public bool Sufficient { get; init; }
    /// <summary>Mindestanzahl gemeinsamer Perioden als PRODUKT-/METHODENKONVENTION (keine universelle statistische
    /// Garantie). Konfigurierbar über die Kampagne.</summary>
    public int MinObservations { get; init; }
    public string? InsufficientReason { get; init; }
    /// <summary>Wegen Intervall-Nichtübereinstimmung ausgeschlossene gemeinsame Endzeitpunkte (kein Null-Fill).</summary>
    public int ExcludedIntervalMismatch { get; init; }
    /// <summary>Gemeinsame Zeitachse (ms) der ausgerichteten OOS-Punkte — passend zu <see cref="ComparisonFamilyRow.SharedOosEquity"/>.</summary>
    public IReadOnlyList<long> AxisT { get; init; } = Array.Empty<long>();
    public double InitialCapital { get; init; }
    public string Method { get; init; } = "";
    public int Iterations { get; init; }
    public int Seed { get; init; }
    public int BlockLength { get; init; }
    public IReadOnlyList<ComparisonFamilyRow> Families { get; init; } = Array.Empty<ComparisonFamilyRow>();
    public IReadOnlyList<ComparisonDifference> Differences { get; init; } = Array.Empty<ComparisonDifference>();
    public IReadOnlyList<string> Assumptions { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Entwicklungs-/Holdout-Aufteilung rein aus geladenen Kerzen + Holdout-Fraktion (identisch zur Reservierung im
/// WalkForwardPlanner: die letzten floor(total·fraction) Bars). <see cref="DevelopmentToUtc"/> ist die OpenTime der
/// ersten Holdout-Kerze (Anzeige-/Herkunftswert: „Entwicklung bis / Holdout ab" — eine Entwicklungsbar darf hier
/// noch schließen). <see cref="DevelopmentLoadToUtc"/> ist die daraus abgeleitete LADE-Obergrenze für den
/// Entwicklungs-Backtest.
/// <para>Warum eine eigene Ladegrenze? Der CSV-Lader filtert <c>OpenTime &lt; ToUtc</c> (exklusiv), der Sierra-Lader
/// dagegen die zugrunde liegenden Ticks mit <c>ts &gt; ToUtc</c> (also zeitstempel-INKLUSIVE Obergrenze). Ein Tick
/// exakt auf dem Holdout-Start würde beim Sierra-Pfad eine (Teil-)Kerze mit <c>OpenTime == HoldoutStart</c> erzeugen
/// und damit Holdout-Zeit in die Vorprüfungen ziehen. <see cref="DevelopmentLoadToUtc"/> = HoldoutStart − 1 Tick
/// (100 ns) schließt jeden Tick ab dem Holdout-Start sicher aus und lässt für beide Lader exakt die
/// Entwicklungsbars [0..UsableBars) übrig — bewiesen durch Regressionstests.</para>
/// </summary>
public sealed record HoldoutSplit(
    int TotalBars, int HoldoutBars, int UsableBars,
    DateTimeOffset? DataFrom, DateTimeOffset? DataTo,
    DateTimeOffset? DevelopmentToUtc, DateTimeOffset? HoldoutFrom, DateTimeOffset? HoldoutTo,
    DateTimeOffset? DevelopmentLoadToUtc = null);

/// <summary>Antwort auf Start-/Statusabfragen eines Research-Laufs.</summary>
public sealed record ResearchRunResponse
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public bool AlreadyRunning { get; init; }
    public string? JobId { get; init; }
    public ResearchRunRecord? Run { get; init; }
}
