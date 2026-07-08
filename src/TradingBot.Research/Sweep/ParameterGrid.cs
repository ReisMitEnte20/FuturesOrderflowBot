namespace TradingBot.Research.Sweep;

/// <summary>Eine konkrete Parameter-Kombination (logische Parameter → Wert).</summary>
public sealed record ParameterCombination
{
    public int Index { get; init; }
    public required IReadOnlyDictionary<string, string> Values { get; init; }

    public string Describe() => string.Join(", ", Values.Select(kv => $"{kv.Key}={kv.Value}"));
}

/// <summary>Ergebnis der Grid-Expansion (mit MaxRuns-Begrenzung).</summary>
public sealed record ParameterGridExpansion
{
    public required IReadOnlyList<ParameterCombination> Combinations { get; init; }
    public int TotalPossible { get; init; }
    public bool Truncated { get; init; }
}

/// <summary>
/// Kartesisches Produkt mehrerer <see cref="ParameterRange"/>s in fester Reihenfolge (deterministisch).
/// <see cref="Expand"/> begrenzt hart über MaxRuns, damit kein endloser Sweep entsteht.
/// </summary>
public sealed class ParameterGrid
{
    private readonly IReadOnlyList<ParameterRange> _ranges;

    public ParameterGrid(IReadOnlyList<ParameterRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        var names = ranges.Select(r => r.Name).ToList();
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
            throw new ArgumentException("Doppelte Parameternamen im Grid.", nameof(ranges));
        _ranges = ranges;
    }

    public int TotalCombinations => _ranges.Count == 0 ? 0 : _ranges.Aggregate(1, (acc, r) => acc * r.Values.Count);

    public ParameterGridExpansion Expand(int maxRuns = 1000)
    {
        if (maxRuns <= 0) throw new ArgumentOutOfRangeException(nameof(maxRuns), "maxRuns muss > 0 sein.");

        int total = TotalCombinations;
        var combos = new List<ParameterCombination>();
        if (total == 0)
            return new ParameterGridExpansion { Combinations = combos, TotalPossible = 0, Truncated = false };

        var indices = new int[_ranges.Count]; // Zähler je Dimension
        int produced = 0;
        while (produced < total && combos.Count < maxRuns)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int d = 0; d < _ranges.Count; d++)
                dict[_ranges[d].Name] = _ranges[d].Values[indices[d]];
            combos.Add(new ParameterCombination { Index = produced, Values = dict });

            produced++;
            // Odometer-Inkrement (letzte Dimension zuerst).
            for (int d = _ranges.Count - 1; d >= 0; d--)
            {
                if (++indices[d] < _ranges[d].Values.Count) break;
                indices[d] = 0;
            }
        }

        return new ParameterGridExpansion
        {
            Combinations = combos,
            TotalPossible = total,
            Truncated = total > combos.Count
        };
    }
}
