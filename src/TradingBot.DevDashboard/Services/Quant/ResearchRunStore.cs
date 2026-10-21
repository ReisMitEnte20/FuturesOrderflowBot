using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingBot.DevDashboard.Services.Quant;

/// <summary>
/// Dauerhafte Ablage der Research-Läufe als JSON unter <c>artifacts/quant/registry/research/&lt;id&gt;.json</c>.
/// Spiegelt das Muster des Versuchsregisters: atomare Schreibvorgänge (tmp + move), ein prozessweiter Lock
/// für Schreibzugriffe, lock-freie Lesezugriffe. So überleben Läufe einen Server-Neustart.
/// </summary>
public sealed class ResearchRunStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _dir;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ResearchRunStore(string registryRoot)
    {
        _dir = Path.Combine(registryRoot, "research");
        Directory.CreateDirectory(_dir);
    }

    private string PathFor(string runId) => Path.Combine(_dir, Sanitize(runId) + ".json");

    public async Task<ResearchRunRecord> CreateAsync(ResearchRunRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _lock.WaitAsync(ct);
        try
        {
            var path = PathFor(record.RunId);
            if (File.Exists(path))
                throw new InvalidOperationException($"Research-Lauf '{record.RunId}' existiert bereits.");
            await WriteAsync(path, record, ct);
            return record;
        }
        finally { _lock.Release(); }
    }

    public async Task<ResearchRunRecord> UpdateAsync(ResearchRunRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _lock.WaitAsync(ct);
        try
        {
            await WriteAsync(PathFor(record.RunId), record, ct);
            return record;
        }
        finally { _lock.Release(); }
    }

    public async Task<ResearchRunRecord?> GetAsync(string runId, CancellationToken ct = default)
    {
        var path = PathFor(runId);
        if (!File.Exists(path)) return null;
        return await ReadAsync(path, ct);
    }

    /// <summary>Alle Läufe (optional einer Kampagne), neueste zuerst.</summary>
    public async Task<IReadOnlyList<ResearchRunRecord>> ListAsync(string? campaignId = null, CancellationToken ct = default)
    {
        if (!Directory.Exists(_dir)) return Array.Empty<ResearchRunRecord>();
        var runs = new List<ResearchRunRecord>();
        foreach (var f in Directory.EnumerateFiles(_dir, "*.json"))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var r = await ReadAsync(f, ct);
                if (r is null) continue;
                if (campaignId is null || string.Equals(r.CampaignId, campaignId, StringComparison.Ordinal))
                    runs.Add(r);
            }
            catch { /* beschädigte Datei überspringen, nicht den ganzen Abruf scheitern lassen */ }
        }
        return runs.OrderByDescending(r => r.CreatedUtc).ToList();
    }

    private static async Task WriteAsync(string path, ResearchRunRecord record, CancellationToken ct)
    {
        // Eindeutige tmp-Datei je Schreibvorgang (verhindert Kollision paralleler Writer), dann atomar umbenennen.
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        var json = JsonSerializer.Serialize(record, Json);
        await File.WriteAllTextAsync(tmp, json, ct);
        // File.Move(overwrite) kann auf Windows scheitern, wenn ein Leser (Frontend-Polling) das Ziel gerade offen
        // hält. Kurz erneut versuchen; die Leser öffnen mit FileShare.Delete, sodass das Ersetzen gelingt.
        for (int attempt = 0; ; attempt++)
        {
            try { File.Move(tmp, path, overwrite: true); return; }
            catch (IOException) when (attempt < 10) { await Task.Delay(15, ct); }
            catch (UnauthorizedAccessException) when (attempt < 10) { await Task.Delay(15, ct); }
        }
    }

    private static async Task<ResearchRunRecord?> ReadAsync(string path, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                // FileShare.Delete erlaubt, dass ein gleichzeitiger Writer die Datei per Move ersetzt.
                await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                var text = await sr.ReadToEndAsync(ct);
                if (string.IsNullOrWhiteSpace(text)) { if (attempt < 10) { await Task.Delay(15, ct); continue; } return null; }
                return JsonSerializer.Deserialize<ResearchRunRecord>(text, Json);
            }
            catch (IOException) when (attempt < 10) { await Task.Delay(15, ct); }
            catch (UnauthorizedAccessException) when (attempt < 10) { await Task.Delay(15, ct); }
        }
    }

    private static string Sanitize(string id)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) id = id.Replace(c, '_');
        return id;
    }
}
