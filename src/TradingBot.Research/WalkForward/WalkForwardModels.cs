using TradingBot.Domain.Models;

namespace TradingBot.Research.WalkForward;

/// <summary>Fenster-Modus der Walk-Forward-Analyse.</summary>
public enum WalkForwardMode
{
    /// <summary>Rollendes IS-Fenster fester Größe.</summary>
    Rolling = 0,
    /// <summary>Verankertes IS-Fenster ab Datenbeginn, das mit jedem Schritt wächst.</summary>
    Anchored = 1
}

/// <summary>Ein Walk-Forward-Fenster: In-Sample- und disjunkter Out-of-Sample-Bereich (Tick-Indizes, End exklusiv).</summary>
public sealed record WalkForwardWindow
{
    public int Index { get; init; }
    public int InSampleStart { get; init; }
    public int InSampleEnd { get; init; }      // exklusiv
    public int OutOfSampleStart { get; init; } // == InSampleEnd (keine Überlappung)
    public int OutOfSampleEnd { get; init; }   // exklusiv

    public int InSampleCount => InSampleEnd - InSampleStart;
    public int OutOfSampleCount => OutOfSampleEnd - OutOfSampleStart;
}

/// <summary>Eingaben für die Walk-Forward-Analyse.</summary>
public sealed record WalkForwardRequest
{
    public required StrategyCandidate Candidate { get; init; }

    /// <summary>Config-Kandidaten, aus denen je Fenster AUF IS ausgewählt wird (Selektion nur auf IS!).</summary>
    public required IReadOnlyList<StrategyConfig> SelectionConfigs { get; init; }

    public int InSampleSize { get; init; } = 400;
    public int OutOfSampleSize { get; init; } = 200;
    public int Step { get; init; } = 200;
    public WalkForwardMode Mode { get; init; } = WalkForwardMode.Rolling;

    /// <summary>Warnschwelle für die Walk-Forward-Efficiency.</summary>
    public decimal EfficiencyWarnThreshold { get; init; } = 0.5m;
}

/// <summary>Ergebnis eines einzelnen Walk-Forward-Segments (Fenster).</summary>
public sealed record WalkForwardSegmentResult
{
    public required WalkForwardWindow Window { get; init; }
    public required StrategyConfig SelectedConfig { get; init; }
    public required ResearchMetricSet InSample { get; init; }
    public required ResearchMetricSet OutOfSample { get; init; }
}

/// <summary>Gesamtergebnis der Walk-Forward-Analyse. IS und OOS strikt getrennt.</summary>
public sealed record WalkForwardResult
{
    public IReadOnlyList<WalkForwardSegmentResult> Segments { get; init; } = Array.Empty<WalkForwardSegmentResult>();

    public decimal InSampleNetProfit { get; init; }
    public decimal OutOfSampleNetProfit { get; init; }
    public int InSampleTrades { get; init; }
    public int OutOfSampleTrades { get; init; }

    /// <summary>OOS-NetPnL-pro-Trade / IS-NetPnL-pro-Trade (null, wenn IS ≤ 0 oder keine Trades).</summary>
    public decimal? WalkForwardEfficiency { get; init; }

    public bool OverfittingSuspected { get; init; }
    public string? OverfittingNote { get; init; }
}
