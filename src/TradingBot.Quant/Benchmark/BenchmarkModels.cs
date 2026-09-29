using System.Globalization;

namespace TradingBot.Quant.Benchmark;

/// <summary>Eine Benchmark-Beobachtung (Indexstand zu einem Zeitpunkt).</summary>
public sealed record BenchmarkObservation(DateTimeOffset Time, double Level);

/// <summary>
/// Benchmark-Kursreihe. Für einen fairen Vergleich muss es eine TOTAL-RETURN-Reihe sein
/// (Dividenden reinvestiert); eine reine Kursreihe unterschätzt die Vergleichsrendite.
/// </summary>
public sealed record BenchmarkSeries
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<BenchmarkObservation> Observations { get; init; }
    public string Currency { get; init; } = "USD";

    /// <summary>Handelt es sich um eine Total-Return-Reihe? Muss aus den Quelldaten belegt sein.</summary>
    public bool IsTotalReturn { get; init; }

    /// <summary>Herkunftsangabe (Datei, Anbieter, Abrufzeitpunkt) — gehört in jeden Bericht.</summary>
    public required string Provenance { get; init; }

    public int Count => Observations.Count;
}

/// <summary>
/// Austauschbare Quelle für Benchmarkdaten. Liefert null, wenn keine echten Daten vorliegen —
/// es wird ausdrücklich keine Ersatzkurve erzeugt.
/// </summary>
public interface IBenchmarkDataSource
{
    /// <summary>Kennung dieser Quelle (für die Anzeige).</summary>
    string SourceName { get; }

    /// <summary>Verfügbare Benchmark-Kennungen.</summary>
    Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default);

    /// <summary>Reihe laden. null = keine echten Daten vorhanden.</summary>
    Task<BenchmarkSeries?> GetAsync(string id, DateTimeOffset? from = null, DateTimeOffset? to = null,
        CancellationToken ct = default);
}

/// <summary>
/// Benchmarkquelle aus lokalen CSV-Dateien (Spalten: date,close — optionaler Header).
/// Die Datei muss vom Nutzer bereitgestellt werden; ohne Datei gibt es keine Vergleichskurve.
/// Erwartetes Namensschema: &lt;id&gt;.csv, z. B. "sp500tr.csv".
/// </summary>
public sealed class CsvBenchmarkDataSource : IBenchmarkDataSource
{
    private readonly string _directory;
    private readonly bool _assumeTotalReturn;

    /// <param name="directory">Verzeichnis mit den CSV-Dateien.</param>
    /// <param name="assumeTotalReturn">
    /// Nur auf true setzen, wenn die abgelegten Dateien nachweislich Total-Return-Reihen sind.
    /// Standard false — dann weist der Vergleich die fehlende Bestätigung ausdrücklich aus.
    /// </param>
    public CsvBenchmarkDataSource(string directory, bool assumeTotalReturn = false)
    {
        _directory = directory;
        _assumeTotalReturn = assumeTotalReturn;
    }

    public string SourceName => $"CSV ({_directory})";

    public Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default)
    {
        IReadOnlyList<string> ids = Directory.Exists(_directory)
            ? Directory.EnumerateFiles(_directory, "*.csv").Select(Path.GetFileNameWithoutExtension).OfType<string>().OrderBy(x => x).ToList()
            : Array.Empty<string>();
        return Task.FromResult(ids);
    }

    public async Task<BenchmarkSeries?> GetAsync(string id, DateTimeOffset? from = null, DateTimeOffset? to = null,
        CancellationToken ct = default)
    {
        var path = Path.Combine(_directory, id + ".csv");
        if (!File.Exists(path)) return null;

        var obs = new List<BenchmarkObservation>();
        foreach (var raw in await File.ReadAllLinesAsync(path, ct))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split(',', ';');
            if (parts.Length < 2) continue;
            if (!DateTimeOffset.TryParse(parts[0], CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t)) continue;
            if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lvl)) continue;
            if (lvl <= 0) continue;
            if (from is not null && t < from) continue;
            if (to is not null && t > to) continue;
            obs.Add(new BenchmarkObservation(t, lvl));
        }
        if (obs.Count == 0) return null;
        obs.Sort((a, b) => a.Time.CompareTo(b.Time));

        return new BenchmarkSeries
        {
            Id = id,
            Name = id,
            Observations = obs,
            IsTotalReturn = _assumeTotalReturn,
            Provenance = $"Lokale Datei {Path.GetFileName(path)} ({obs.Count} Beobachtungen), gelesen {DateTimeOffset.UtcNow:u}."
        };
    }
}
