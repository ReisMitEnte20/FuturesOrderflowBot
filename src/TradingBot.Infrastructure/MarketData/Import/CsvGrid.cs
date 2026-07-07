using System.Globalization;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.MarketData.Import;

/// <summary>
/// Interner CSV-Helfer für die Importer: Header-basiertes Spalten-Mapping (case-insensitive),
/// Zeilen mit Zeilennummern, typisierte Feld-Zugriffe mit Issue-Sammlung (invariant geparst).
/// Leerzeilen und '#'-Kommentare werden übersprungen. Keine stillen Fehler.
/// </summary>
internal sealed class CsvGrid
{
    private readonly Dictionary<string, int> _columns;
    public IReadOnlyList<(string[] Fields, int LineNumber)> Rows { get; }

    private CsvGrid(Dictionary<string, int> columns, List<(string[], int)> rows)
    {
        _columns = columns;
        Rows = rows;
    }

    public static CsvGrid Parse(TextReader reader, char delimiter)
    {
        ArgumentNullException.ThrowIfNull(reader);
        Dictionary<string, int>? columns = null;
        var rows = new List<(string[], int)>();

        string? line;
        int lineNo = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNo++;
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;

            var fields = line.Split(delimiter);
            if (columns is null)
            {
                columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < fields.Length; i++)
                {
                    var name = fields[i].Trim();
                    if (name.Length > 0) columns[name] = i;
                }
                continue;
            }
            rows.Add((fields, lineNo));
        }

        if (columns is null)
            throw new CsvMarketDataException("Leere Datei oder fehlende Kopfzeile.");
        return new CsvGrid(columns, rows);
    }

    public bool HasColumn(string csvColumn) => _columns.ContainsKey(csvColumn);

    /// <summary>Erzwingt vorhandene Pflichtspalten (via Profil gemappt); wirft sonst.</summary>
    public void RequireColumns(CsvImportProfile profile, params string[] logicalFields)
    {
        var missing = logicalFields
            .Select(f => (Logical: f, Column: profile.Column(f)))
            .Where(x => !HasColumn(x.Column))
            .ToList();
        if (missing.Count > 0)
            throw new CsvMarketDataException(
                "Pflichtspalte(n) fehlen im CSV-Header: " +
                string.Join(", ", missing.Select(m => $"'{m.Column}' (Feld '{m.Logical}')")));
    }

    public string? Raw(string[] fields, CsvImportProfile profile, string logicalField)
    {
        if (!_columns.TryGetValue(profile.Column(logicalField), out int i) || i >= fields.Length) return null;
        var v = fields[i].Trim();
        return v.Length == 0 ? null : v;
    }

    public decimal? Dec(string[] fields, CsvImportProfile p, string logical, List<DataQualityIssue> issues, int line)
    {
        var raw = Raw(fields, p, logical);
        if (raw is null) return null;
        if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)) return d;
        issues.Add(Error("InvalidNumber", $"Ungültiger Zahlenwert '{raw}' für '{logical}'.", line));
        return null;
    }

    public DateTimeOffset? Time(string[] fields, CsvImportProfile p, string logical, List<DataQualityIssue> issues, int line)
    {
        var raw = Raw(fields, p, logical);
        if (raw is null) return null;
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var ts)) return ts;
        issues.Add(Error("InvalidTimestamp", $"Ungültiger Zeitstempel '{raw}' für '{logical}'.", line));
        return null;
    }

    public bool? Bool(string[] fields, CsvImportProfile p, string logical, List<DataQualityIssue> issues, int line)
    {
        var raw = Raw(fields, p, logical);
        if (raw is null) return null;
        if (bool.TryParse(raw, out var b)) return b;
        if (raw is "1") return true;
        if (raw is "0") return false;
        issues.Add(Error("InvalidBoolean", $"Ungültiger Boolean-Wert '{raw}' für '{logical}'.", line));
        return null;
    }

    public static DataQualityIssue Error(string code, string message, int? line = null, DateTimeOffset? ts = null)
        => new() { Severity = DataQualitySeverity.Error, Code = code, Message = message, LineNumber = line, Timestamp = ts };

    public static DataQualityIssue Warning(string code, string message, int? line = null, DateTimeOffset? ts = null)
        => new() { Severity = DataQualitySeverity.Warning, Code = code, Message = message, LineNumber = line, Timestamp = ts };

    public static DataQualityIssue Info(string code, string message, int? line = null)
        => new() { Severity = DataQualitySeverity.Info, Code = code, Message = message, LineNumber = line };
}
