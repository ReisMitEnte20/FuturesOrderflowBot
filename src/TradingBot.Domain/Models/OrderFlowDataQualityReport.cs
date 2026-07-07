using TradingBot.Domain.Enums;

namespace TradingBot.Domain.Models;

/// <summary>
/// Ergebnis der Datenqualitätsprüfung eines Imports. RowsRead = gelesene Datenzeilen,
/// RowsAccepted = übernommen; verworfene Zeilen haben immer ein Error-Issue.
/// </summary>
public sealed record OrderFlowDataQualityReport
{
    public required MarketDataSourceType SourceType { get; init; }
    public int RowsRead { get; init; }
    public int RowsAccepted { get; init; }
    public IReadOnlyList<DataQualityIssue> Issues { get; init; } = Array.Empty<DataQualityIssue>();

    public int RowsRejected => RowsRead - RowsAccepted;
    public bool HasErrors => Issues.Any(i => i.Severity == DataQualitySeverity.Error);
    public bool HasWarnings => Issues.Any(i => i.Severity == DataQualitySeverity.Warning);
}
