using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models;

/// <summary>Einzelner Datenqualitäts-Befund (mit Zeilennummer/Zeitbezug, sofern bekannt).</summary>
public sealed record DataQualityIssue
{
    public required DataQualitySeverity Severity { get; init; }

    /// <summary>Stabiler Code (z. B. "NegativePrice", "NonChronological", "BidAskSumMismatch").</summary>
    public required string Code { get; init; }

    public required string Message { get; init; }

    public int? LineNumber { get; init; }
    public DateTimeOffset? Timestamp { get; init; }
}
