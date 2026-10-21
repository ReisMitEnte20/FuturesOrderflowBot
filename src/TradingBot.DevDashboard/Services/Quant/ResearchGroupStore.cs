using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingBot.DevDashboard.Services.Quant;

/// <summary>
/// Dauerhafte, prozessweit serialisierte GRUPPEN-Holdout-Sperre einer Vergleichs-Kampagne unter
/// <c>artifacts/quant/registry/research/groups/&lt;groupId&gt;.json</c>. Sie schützt den EINEN gemeinsamen finalen
/// Holdout der GESAMTEN Gruppe: sobald genau eine Familie mit ihrem Kandidaten reserviert ist, kann keine andere
/// Familie derselben Gruppe eine zweite finale Auswertung starten. Überlebt Neustart (Auswahl/Verbrauch bleiben).
/// </summary>
public sealed class ResearchGroupStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _dir;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ResearchGroupStore(string registryRoot)
    {
        _dir = Path.Combine(registryRoot, "research", "groups");
        Directory.CreateDirectory(_dir);
    }

    private string PathFor(string groupId) => Path.Combine(_dir, Sanitize(groupId) + ".json");

    public async Task<ResearchGroupHoldout?> GetAsync(string groupId, CancellationToken ct = default)
    {
        var path = PathFor(groupId);
        if (!File.Exists(path)) return null;
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            var text = await sr.ReadToEndAsync(ct);
            return string.IsNullOrWhiteSpace(text) ? null : JsonSerializer.Deserialize<ResearchGroupHoldout>(text, Json);
        }
        catch { return null; }
    }

    /// <summary>
    /// Führt <paramref name="action"/> unter der prozessweiten Gruppensperre aus (atomarer Lese-/Schreib-Zyklus).
    /// Damit können parallele finale Starts zweier Familien niemals beide reservieren.
    /// </summary>
    public async Task<T> WithLockAsync<T>(string groupId, Func<ResearchGroupHoldout?, Task<(ResearchGroupHoldout? ToWrite, T Result)>> action, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var current = await GetAsync(groupId, ct);
            var (toWrite, result) = await action(current);
            if (toWrite is not null) await WriteAsync(PathFor(groupId), toWrite, ct);
            return result;
        }
        finally { _lock.Release(); }
    }

    private static async Task WriteAsync(string path, ResearchGroupHoldout record, CancellationToken ct)
    {
        var tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(record, Json), ct);
        for (int attempt = 0; ; attempt++)
        {
            try { File.Move(tmp, path, overwrite: true); return; }
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
