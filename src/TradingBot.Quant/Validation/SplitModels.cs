namespace TradingBot.Quant.Validation;

/// <summary>Rolle eines Datenabschnitts. Wird bis ins Versuchsregister durchgereicht.</summary>
public enum SplitRole
{
    Train = 0,
    Validation = 1,
    Test = 2,
    /// <summary>Finaler Holdout — während der Suche gesperrt.</summary>
    Holdout = 3
}

/// <summary>
/// Ein zusammenhängender Datenabschnitt über Bar-Indizes (<see cref="End"/> exklusiv) mit den
/// zugehörigen Zeitstempeln.
/// </summary>
public sealed record DataSplit
{
    public required string Label { get; init; }
    public SplitRole Role { get; init; }
    public int Start { get; init; }
    public int End { get; init; }
    public DateTimeOffset FromTime { get; init; }
    public DateTimeOffset ToTime { get; init; }

    public int Count => Math.Max(0, End - Start);
    public bool Contains(int index) => index >= Start && index < End;
}

/// <summary>
/// Ein Walk-Forward-Fenster. Das Training kann durch Purging in mehrere Bereiche zerfallen,
/// deshalb ist es als Liste geführt.
/// </summary>
public sealed record WalkForwardFold
{
    public int Index { get; init; }
    public required IReadOnlyList<DataSplit> Train { get; init; }
    public required DataSplit Test { get; init; }

    /// <summary>Wegen überlappender Labels aus dem Training entfernte Bars.</summary>
    public int PurgedBars { get; init; }
    /// <summary>Nach dem Testfenster gesperrte Bars (Embargo).</summary>
    public int EmbargoBars { get; init; }
    /// <summary>Am Anfang jedes Abschnitts für Indikator-Warmup benötigte Bars.</summary>
    public int WarmupBars { get; init; }

    public int TrainBars => Train.Sum(t => t.Count);
}

public enum WalkForwardMode
{
    /// <summary>Trainingsfenster fester Länge, das mitwandert.</summary>
    Rolling = 0,
    /// <summary>Trainingsfenster ab Datenbeginn, das mit jedem Schritt wächst.</summary>
    Anchored = 1
}

/// <summary>Einstellungen der Walk-Forward-Aufteilung. Alle Angaben in Bars.</summary>
public sealed record WalkForwardOptions
{
    public WalkForwardMode Mode { get; init; } = WalkForwardMode.Rolling;
    public int TrainBars { get; init; } = 2000;
    public int TestBars { get; init; } = 500;
    /// <summary>Schrittweite zwischen zwei Fenstern; Standard = Testlänge (lückenlose, disjunkte Tests).</summary>
    public int? StepBars { get; init; }

    /// <summary>
    /// Länge des Informationsintervalls eines Trainingslabels in Bars (z. B. maximale Haltedauer
    /// eines Trades). Trainingsbars, deren Label in das Testfenster hineinreicht, werden entfernt.
    /// 0 = keine Überlappung, kein Purging nötig.
    /// </summary>
    public int LabelSpanBars { get; init; }

    /// <summary>Nach dem Testfenster gesperrte Bars (Embargo gegen Autokorrelation an der Grenze).</summary>
    public int EmbargoBars { get; init; }

    /// <summary>Bars am Anfang jedes Abschnitts, die nur dem Indikator-Warmup dienen.</summary>
    public int WarmupBars { get; init; }

    /// <summary>Anteil der Daten am Ende, der als finaler Holdout reserviert und nicht aufgeteilt wird.</summary>
    public double HoldoutFraction { get; init; }

    /// <summary>
    /// SESSION-AUSGERICHTETE Aufteilung (für Session-Strategien wie ORB): sortierte Bar-Indizes, an denen eine
    /// Session BEGINNT — ausschließlich aus Zeitstempeln + Session-Kalender bestimmt, NIEMALS aus Trades/Ergebnissen.
    /// Ist dies gesetzt, legt der Planer Fold-Grenzen NUR auf diese Session-Anfänge und misst Train/Test in GANZEN
    /// Sessions (<see cref="TrainSessions"/>/<see cref="TestSessions"/>). Jeder Abschnitt beginnt an einem Session-Start
    /// und enthält vollständige Sessions (inkl. Session-Schluss). Purging/Embargo/Warmup bleiben wie im Bar-Modus
    /// wirksam; OOS-Abschnitte sind disjunkt. Null ⇒ normaler bar-basierter Modus.
    /// </summary>
    public IReadOnlyList<int>? SessionStarts { get; init; }

    /// <summary>Anzahl GANZER Sessions je Trainingsabschnitt (nur session-ausgerichtet).</summary>
    public int TrainSessions { get; init; }

    /// <summary>Anzahl GANZER Sessions je Testabschnitt (nur session-ausgerichtet).</summary>
    public int TestSessions { get; init; }
}

/// <summary>Vollständiger Aufteilungsplan inklusive Holdout und der dokumentierten Regeln.</summary>
public sealed record WalkForwardPlan
{
    public required IReadOnlyList<WalkForwardFold> Folds { get; init; }
    public DataSplit? Holdout { get; init; }
    public required WalkForwardOptions Options { get; init; }
    public int TotalBars { get; init; }

    /// <summary>Hinweise, die im Bericht erscheinen müssen (z. B. „zu wenige Bars für ein Fenster").</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    public bool IsEmpty => Folds.Count == 0;
}
