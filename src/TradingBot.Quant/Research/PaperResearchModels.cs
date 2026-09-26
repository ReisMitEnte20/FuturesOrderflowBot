using System.Text.Json;

namespace TradingBot.Quant.Research;

/// <summary>Bearbeitungsstand eines Paper-Eintrags.</summary>
public enum PaperStatus
{
    /// <summary>Erfasst, noch nicht umgesetzt.</summary>
    Registered = 0,
    /// <summary>Regeln formuliert, Umsetzung läuft.</summary>
    Implementing = 1,
    /// <summary>Umgesetzt und in mindestens einer Kampagne geprüft.</summary>
    Evaluated = 2,
    /// <summary>Nicht umsetzbar (z. B. fehlende Datenart) — mit Begründung.</summary>
    NotApplicable = 3
}

/// <summary>Eine bewusst dokumentierte Abweichung von der Originalquelle.</summary>
/// <param name="Aspect">Worin wird abgewichen (z. B. „Universum", „Frequenz", „Kostenmodell").</param>
/// <param name="Original">Was die Quelle vorsieht.</param>
/// <param name="Implemented">Was hier tatsächlich umgesetzt ist.</param>
/// <param name="Reason">Warum abgewichen wird.</param>
public sealed record PaperDeviation(string Aspect, string Original, string Implemented, string Reason);

/// <summary>
/// Eintrag zu einer wissenschaftlichen Quelle. Er hält fest, was die Quelle behauptet, was davon
/// hier prüfbar ist und wo bewusst abgewichen wurde — damit ein Ergebnis später nicht fälschlich
/// als Bestätigung oder Widerlegung des Papers gelesen wird.
/// </summary>
public sealed record PaperResearchEntry
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Authors { get; init; }
    public int? Year { get; init; }
    /// <summary>DOI, URL oder andere eindeutige Fundstelle.</summary>
    public required string Source { get; init; }

    /// <summary>Die prüfbare Hypothese in einem Satz.</summary>
    public required string Hypothesis { get; init; }

    /// <summary>Welche Daten die Umsetzung braucht (Instrument, Frequenz, Zusatzdaten).</summary>
    public required string DataRequirements { get; init; }

    /// <summary>Die Handelsregeln, so konkret wie sie umgesetzt sind.</summary>
    public required string Rules { get; init; }

    public IReadOnlyList<PaperDeviation> Deviations { get; init; } = Array.Empty<PaperDeviation>();

    public PaperStatus Status { get; init; } = PaperStatus.Registered;
    /// <summary>Begründung bei <see cref="PaperStatus.NotApplicable"/>.</summary>
    public string? StatusReason { get; init; }

    /// <summary>Kampagnen im Versuchsregister, die diese Quelle geprüft haben.</summary>
    public IReadOnlyList<string> CampaignIds { get; init; } = Array.Empty<string>();

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? Notes { get; init; }
}

/// <summary>Dateibasierte Ablage der Paper-Einträge (eine JSON-Datei je Eintrag).</summary>
public sealed class JsonPaperResearchStore
{
    private readonly string _directory;

    public JsonPaperResearchStore(string directory)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        Directory.CreateDirectory(_directory);
    }

    private string PathFor(string id)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(id.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return Path.Combine(_directory, safe + ".json");
    }

    public async Task<PaperResearchEntry> SaveAsync(PaperResearchEntry entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Status == PaperStatus.NotApplicable && string.IsNullOrWhiteSpace(entry.StatusReason))
            throw new ArgumentException("Für 'NotApplicable' ist eine Begründung verpflichtend.", nameof(entry));

        var path = PathFor(entry.Id);
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp,
            JsonSerializer.Serialize(entry, Registry.JsonExperimentStore.Json), ct);
        File.Move(tmp, path, overwrite: true);
        return entry;
    }

    public async Task<PaperResearchEntry?> GetAsync(string id, CancellationToken ct = default)
    {
        var path = PathFor(id);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<PaperResearchEntry>(
            await File.ReadAllTextAsync(path, ct), Registry.JsonExperimentStore.Json);
    }

    public async Task<IReadOnlyList<PaperResearchEntry>> ListAsync(CancellationToken ct = default)
    {
        var list = new List<PaperResearchEntry>();
        if (!Directory.Exists(_directory)) return list;
        foreach (var f in Directory.EnumerateFiles(_directory, "*.json").OrderBy(x => x))
        {
            var e = JsonSerializer.Deserialize<PaperResearchEntry>(
                await File.ReadAllTextAsync(f, ct), Registry.JsonExperimentStore.Json);
            if (e is not null) list.Add(e);
        }
        return list.OrderBy(e => e.CreatedUtc).ToList();
    }
}
