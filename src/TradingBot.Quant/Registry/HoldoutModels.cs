namespace TradingBot.Quant.Registry;

/// <summary>
/// Zustandsmaschine der finalen Holdout-Auswertung. Der Holdout wird bei der Reservierung EINMALIG
/// verbraucht; ein Fehler/Abbruch gibt ihn NICHT automatisch wieder frei (kein verstecktes Retry).
/// </summary>
public enum HoldoutEvaluationStatus
{
    /// <summary>Reserviert und an die eingefrorene Konfiguration gebunden, Auswertung noch nicht gestartet.</summary>
    Reserved = 0,
    /// <summary>Auswertung läuft.</summary>
    Running = 1,
    /// <summary>Erfolgreich ausgewertet; Ergebnis dauerhaft gespeichert.</summary>
    Completed = 2,
    /// <summary>Technisch gescheitert (Fehler, keine auswertbaren Daten). Holdout bleibt verbraucht.</summary>
    Failed = 3,
    /// <summary>Abgebrochen (Nutzer/Laufzeitgrenze/Prozessausfall). Holdout bleibt verbraucht.</summary>
    Cancelled = 4
}

/// <summary>
/// VOR dem Zugriff auf Holdout-Daten dauerhaft festgehaltene, vollständige Konfiguration des
/// ausgewählten Kandidaten. Kein Feld darf aus Holdout-Ergebnissen stammen — die Auswahl ist vorher
/// getroffen. Zusammen mit dem Datenfingerabdruck macht das die finale Auswertung reproduzierbar.
/// </summary>
public sealed record HoldoutFrozenConfig
{
    public required string CampaignId { get; init; }
    /// <summary>Eindeutige, menschenlesbare Referenz auf den ausgewählten Kandidaten.</summary>
    public required string CandidateReference { get; init; }
    /// <summary>Optionale Id des zugehörigen Walk-forward-Trials im Register (Herkunftsnachweis).</summary>
    public string? CandidateTrialId { get; init; }

    public required string StrategyId { get; init; }
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();

    public required string Symbol { get; init; }
    public int TimeframeMinutes { get; init; }
    public decimal InitialCapital { get; init; }
    public int Quantity { get; init; }
    public int? StopLossTicks { get; init; }
    public int? TakeProfitTicks { get; init; }
    public CostProfileSnapshot Costs { get; init; } = new();

    /// <summary>Datenfingerabdruck des zugrunde liegenden Datensatzes (muss der Kampagne entsprechen).</summary>
    public required string DataSha { get; init; }
    /// <summary>Exakte, gespeicherte Holdout-Grenzen (aus der Kampagne). NUR dieser Zeitraum wird bewertet.</summary>
    public required DateTimeOffset HoldoutFrom { get; init; }
    public required DateTimeOffset HoldoutTo { get; init; }

    /// <summary>Warmup-Bars VOR dem Holdout (nur frühere Daten; im Warmup keine Trades, keine Kennzahlen).</summary>
    public int WarmupBars { get; init; }
    /// <summary>Renditefrequenz der Kennzahlen ("Bar", "Daily", ...).</summary>
    public string Frequency { get; init; } = "Bar";
    public required string CodeVersion { get; init; }
}

/// <summary>
/// Ein Punkt der Holdout-Kapitalkurve. <see cref="Equity"/> ist die REALISIERTE Equity (Basis für
/// Drawdown), <see cref="TotalEquity"/> zusätzlich mit Mark-to-Market offener Positionen. Zeit als
/// Unix-Millisekunden für die UI.
/// </summary>
public sealed record HoldoutEquityPoint(long TimeMs, int BarIndex, double Equity, double TotalEquity);

/// <summary>Ein Holdout-Trade fürs Journal (Zeiten als Unix-Millisekunden).</summary>
public sealed record HoldoutTradeRecord(
    int Index, string Side, int Quantity, long EntryTimeMs, long ExitTimeMs,
    double EntryPrice, double ExitPrice, int EntryBarIndex, int ExitBarIndex,
    double GrossPnL, double Fees, double NetPnL, string ExitReason,
    double StopLossPrice, double TakeProfitPrice, bool Ambiguous, string? Note);

/// <summary>
/// Dauerhaft gespeicherte finale Holdout-Auswertung einer Kampagne (genau eine je Kampagne). Enthält
/// die eingefrorene Konfiguration, den Zustand, den tatsächlich verwendeten Datenbezug sowie — nach
/// Abschluss — Kennzahlen, Equity/Drawdown und Trade-Journal. Überlebt einen Server-Neustart, weil sie
/// als JSON persistiert wird.
/// </summary>
public sealed record HoldoutEvaluationRecord
{
    public required string CampaignId { get; init; }
    /// <summary>Eindeutige Lauf-Id der finalen Auswertung.</summary>
    public required string RunId { get; init; }
    public HoldoutEvaluationStatus Status { get; init; } = HoldoutEvaluationStatus.Reserved;
    public required HoldoutFrozenConfig Config { get; init; }

    public DateTimeOffset ReservedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedUtc { get; init; }
    public DateTimeOffset? CompletedUtc { get; init; }

    /// <summary>Tatsächlich verwendeter Datenbezug (Fingerabdruck der geladenen Kerzen).</summary>
    public string? UsedDataSha { get; init; }
    public int HoldoutBars { get; init; }
    public int WarmupBarsUsed { get; init; }

    public IReadOnlyDictionary<string, double?> Metrics { get; init; } = new Dictionary<string, double?>();
    public double? MaxDrawdown { get; init; }
    public double? NetProfit { get; init; }
    public double? FinalEquity { get; init; }
    public int TradeCount { get; init; }

    public IReadOnlyList<HoldoutEquityPoint> Equity { get; init; } = Array.Empty<HoldoutEquityPoint>();
    public IReadOnlyList<HoldoutTradeRecord> Trades { get; init; } = Array.Empty<HoldoutTradeRecord>();

    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    /// <summary>Fehler- oder Abbruchgrund bei <see cref="HoldoutEvaluationStatus.Failed"/>/<see cref="HoldoutEvaluationStatus.Cancelled"/>.</summary>
    public string? StatusReason { get; init; }
}
