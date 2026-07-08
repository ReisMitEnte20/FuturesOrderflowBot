using TradingBot.Domain.Enums;

namespace TradingBot.Research.Robustness;

/// <summary>Einzelner Robustheits-/Overfitting-Befund (Severity aus <see cref="DataQualitySeverity"/>).</summary>
public sealed record RobustnessFinding
{
    public required DataQualitySeverity Severity { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }
}

/// <summary>
/// HILFSMETRIK 0..100 (höher = weniger Warnsignale). KEINE Wahrheit über künftige Profitabilität –
/// nur eine gebündelte Sicht auf die gefundenen Robustheitsprobleme.
/// </summary>
public sealed record RobustnessScore
{
    public decimal Value { get; init; }            // 0..100
    public required string Interpretation { get; init; }

    public static RobustnessScore FromFindings(IReadOnlyList<RobustnessFinding> findings)
    {
        decimal penalty = 0m;
        foreach (var f in findings)
            penalty += f.Severity switch
            {
                DataQualitySeverity.Error => 25m,
                DataQualitySeverity.Warning => 10m,
                _ => 0m
            };
        decimal value = Math.Clamp(100m - penalty, 0m, 100m);
        string interpretation = value switch
        {
            >= 80m => "wenige Warnsignale (dennoch keine Gewinngarantie)",
            >= 50m => "mehrere Warnsignale – mit Vorsicht behandeln",
            _ => "viele/kritische Warnsignale – vermutlich nicht robust"
        };
        return new RobustnessScore { Value = value, Interpretation = interpretation };
    }
}

/// <summary>Bericht der Robustheits-/Overfitting-Prüfung.</summary>
public sealed record OverfittingReport
{
    public IReadOnlyList<RobustnessFinding> Findings { get; init; } = Array.Empty<RobustnessFinding>();
    public required RobustnessScore Score { get; init; }

    public bool HasCritical => Findings.Any(f => f.Severity == DataQualitySeverity.Error);
    public bool HasWarnings => Findings.Any(f => f.Severity == DataQualitySeverity.Warning);
    public bool Contains(string code) => Findings.Any(f => f.Code == code);
}
