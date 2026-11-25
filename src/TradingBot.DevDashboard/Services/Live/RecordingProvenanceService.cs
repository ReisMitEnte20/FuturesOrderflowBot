using System.Text.Json;

namespace TradingBot.DevDashboard.Services.Live;

/// <summary>Herkunft einer Aufnahme laut lokalem Datenqualitäts-Audit (research/data-bridge/atas_data_audit.py).</summary>
public sealed record RecordingProvenance(
    string RecordingId, bool AuditFound, string? AuditFile, string? ReplayType, string? ReplayTypeEvidence, string Banner,
    IReadOnlyDictionary<string, string> Streams, IReadOnlyDictionary<string, string> Evidence, string? TradesLabel, IReadOnlyList<string> Notes);

/// <summary>
/// Liest den neuesten lokalen Audit-Bericht (artifacts/data-audit/*/datenqualitaet.json, gitignored) und liefert je Aufnahme die
/// belegte Herkunft der Streams. Schreibt nichts; alte Berichte und Aufnahmen bleiben unverändert. Ohne Audit: „ungeklärt“.
/// </summary>
public sealed class RecordingProvenanceService
{
    public const string GeneratedBanner = "ATAS-Minutenhistorie · Tickfolge im Replay generiert";
    private readonly string _auditRoot;
    private (string File, DateTime Stamp, Dictionary<string, JsonElement> Rows)? _cache;
    private readonly object _lock = new();

    public RecordingProvenanceService(string auditRoot) => _auditRoot = auditRoot;

    public RecordingProvenance Get(string recordingId)
    {
        var (file, rows) = Load();
        if (file is null || !rows.TryGetValue(recordingId, out var r))
            return new RecordingProvenance(recordingId, false, file, null, null,
                "Herkunft ungeklärt – für diese Aufnahme liegt kein Audit-Eintrag vor",
                new Dictionary<string, string> { ["Trades"] = "ungeklärt", ["Tape"] = "ungeklärt", ["BBO"] = "ungeklärt", ["DOM"] = "ungeklärt" },
                new Dictionary<string, string>(), "Replay-Ereignisse (Herkunft ungeklärt)",
                new[] { file is null ? "Kein Audit-Bericht unter artifacts/data-audit gefunden." : "Aufnahme nicht im Audit-Bericht." });
        var prov = r.GetProperty("provenance").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "ungeklärt");
        var ev = r.GetProperty("evidence").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
        string? rt = r.TryGetProperty("replay_type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        string? rte = r.TryGetProperty("replay_type_evidence", out var te) && te.ValueKind == JsonValueKind.String ? te.GetString() : null;
        bool generated = prov.GetValueOrDefault("Trades", "").StartsWith("Minutenaggregat", StringComparison.Ordinal);
        string banner = generated ? GeneratedBanner
            : prov.GetValueOrDefault("Trades") == "historisch" ? "Historische Trades laut Replay-Typ (belegt)" : "Herkunft der Tickfolge ungeklärt";
        return new RecordingProvenance(recordingId, true, file, rt, rte, banner, prov, ev,
            generated ? "Replay-Ereignisse (aus ATAS-Minutenkerzen generiert, keine einzelnen Börsentrades)" : prov.GetValueOrDefault("Trades") == "historisch" ? "Trades" : "Replay-Ereignisse (Herkunft ungeklärt)",
            new[] { $"Audit: {Path.GetFileName(Path.GetDirectoryName(file))}" });
    }

    private (string? File, Dictionary<string, JsonElement> Rows) Load()
    {
        lock (_lock)
        {
            var file = Directory.Exists(_auditRoot)
                ? Directory.EnumerateFiles(_auditRoot, "datenqualitaet.json", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                : null;
            if (file is null) return (null, new());
            var stamp = File.GetLastWriteTimeUtc(file);
            if (_cache is { } c && c.File == file && c.Stamp == stamp) return (file, c.Rows);
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            var rows = new Dictionary<string, JsonElement>();
            foreach (var row in doc.RootElement.GetProperty("rows").EnumerateArray())
                if (row.TryGetProperty("recording_id", out var id) && id.GetString() is { } s) rows[s] = row.Clone();
            _cache = (file, stamp, rows);
            return (file, rows);
        }
    }
}
