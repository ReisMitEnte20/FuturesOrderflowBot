using System.Globalization;

namespace TradingBot.Quant.Robustness;

/// <summary>Worin sich ein Stressszenario vom Ausgangsfall unterscheidet.</summary>
public enum StressDimension
{
    /// <summary>Gebühren und/oder Slippage skaliert.</summary>
    Costs = 0,
    /// <summary>Signale um n Bars verzögert ausgeführt.</summary>
    ExecutionDelay = 1,
    /// <summary>Parameter in der Nachbarschaft des Ausgangspunkts.</summary>
    ParameterNeighborhood = 2
}

/// <summary>Ein einzelnes Stressszenario. Vollständig beschrieben, damit es reproduzierbar bleibt.</summary>
public sealed record StressScenario
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public StressDimension Dimension { get; init; }

    public double FeeMultiplier { get; init; } = 1.0;
    public double SlippageMultiplier { get; init; } = 1.0;
    public int ExecutionDelayBars { get; init; }
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();

    public bool IsBaseline => FeeMultiplier == 1.0 && SlippageMultiplier == 1.0 && ExecutionDelayBars == 0;
}

/// <summary>Ergebnis eines Szenarios für die betrachtete Kennzahl.</summary>
public sealed record StressOutcome
{
    public required StressScenario Scenario { get; init; }
    /// <summary>Wert der Kennzahl; null = nicht berechenbar (Grund in <see cref="Error"/>).</summary>
    public double? Value { get; init; }
    public int TradeCount { get; init; }
    public string? Error { get; init; }
}

/// <summary>Zusammenfassung einer Stressreihe.</summary>
public sealed record StressResult
{
    public required string MetricKey { get; init; }
    public required StressDimension Dimension { get; init; }
    public double? BaselineValue { get; init; }
    public required IReadOnlyList<StressOutcome> Outcomes { get; init; }

    /// <summary>Schlechtester gültiger Wert (Richtung: höher = besser).</summary>
    public double? Worst { get; init; }
    /// <summary>Anteil der Szenarien, die den Ausgangswert nicht halten (nur über gültige Werte).</summary>
    public double? ShareBelowBaseline { get; init; }
    /// <summary>Relativer Rückgang vom Ausgangswert zum schlechtesten Szenario (nur bei positivem Ausgangswert).</summary>
    public double? RelativeDegradation { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Baut Stressszenarien und fasst deren Ergebnisse zusammen.
///
/// Die Auswertung sagt nichts darüber aus, wie wahrscheinlich ein Szenario ist. Sie zeigt nur,
/// wie empfindlich ein Ergebnis auf veränderte Kosten, verzögerte Ausführung oder benachbarte
/// Parameter reagiert. Ein Ergebnis, das nur an einem Punkt des Parameterraums funktioniert,
/// ist ein Warnsignal.
/// </summary>
public static class StressAnalysis
{
    /// <summary>Gitter aus Gebühren- und Slippage-Faktoren (inklusive Ausgangsfall 1,0/1,0).</summary>
    public static IReadOnlyList<StressScenario> CostGrid(IReadOnlyList<double> feeMultipliers, IReadOnlyList<double> slippageMultipliers)
    {
        ArgumentNullException.ThrowIfNull(feeMultipliers);
        ArgumentNullException.ThrowIfNull(slippageMultipliers);
        var list = new List<StressScenario>();
        foreach (var f in feeMultipliers)
            foreach (var s in slippageMultipliers)
                list.Add(new StressScenario
                {
                    Id = $"cost-f{Fmt(f)}-s{Fmt(s)}",
                    Label = $"Gebühren ×{f:0.##}, Slippage ×{s:0.##}",
                    Dimension = StressDimension.Costs,
                    FeeMultiplier = f,
                    SlippageMultiplier = s
                });
        return list;
    }

    /// <summary>Szenarien mit verzögerter Signalausführung.</summary>
    public static IReadOnlyList<StressScenario> ExecutionDelays(IReadOnlyList<int> delaysInBars)
    {
        ArgumentNullException.ThrowIfNull(delaysInBars);
        return delaysInBars.Select(d => new StressScenario
        {
            Id = $"delay-{d}",
            Label = d == 0 ? "ohne Verzögerung" : $"Signal {d} Bar(s) später",
            Dimension = StressDimension.ExecutionDelay,
            ExecutionDelayBars = d
        }).ToList();
    }

    /// <summary>
    /// Parameter-Nachbarschaft: variiert je ganzzahligem Parameter einzeln um ±Schrittweiten.
    /// Nicht-numerische Parameter bleiben unverändert.
    /// </summary>
    public static IReadOnlyList<StressScenario> ParameterNeighborhood(
        IReadOnlyDictionary<string, string> baseParameters, IReadOnlyList<int> offsets)
    {
        ArgumentNullException.ThrowIfNull(baseParameters);
        ArgumentNullException.ThrowIfNull(offsets);

        var list = new List<StressScenario>
        {
            new()
            {
                Id = "param-base", Label = "Ausgangsparameter",
                Dimension = StressDimension.ParameterNeighborhood,
                Parameters = new Dictionary<string, string>(baseParameters)
            }
        };

        foreach (var (key, value) in baseParameters.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) continue;
            foreach (var off in offsets)
            {
                if (off == 0) continue;
                int nv = v + off;
                if (nv <= 0) continue;
                var p = new Dictionary<string, string>(baseParameters) { [key] = nv.ToString(CultureInfo.InvariantCulture) };
                list.Add(new StressScenario
                {
                    Id = $"param-{key}-{(off > 0 ? "+" : "")}{off}",
                    Label = $"{key} = {nv} (Ausgang {v})",
                    Dimension = StressDimension.ParameterNeighborhood,
                    Parameters = p
                });
            }
        }
        return list;
    }

    /// <summary>
    /// Fasst Szenarioergebnisse zusammen. Szenarien ohne gültigen Wert zählen NICHT als schlechtes
    /// Ergebnis, sondern werden getrennt ausgewiesen.
    /// </summary>
    public static StressResult Summarize(string metricKey, StressDimension dimension,
        IReadOnlyList<StressOutcome> outcomes, double? baselineValue)
    {
        ArgumentNullException.ThrowIfNull(outcomes);
        var notes = new List<string>();

        var valid = outcomes.Where(o => o.Value is { } v && !double.IsNaN(v) && !double.IsInfinity(v)).ToList();
        int invalid = outcomes.Count - valid.Count;
        if (invalid > 0) notes.Add($"{invalid} von {outcomes.Count} Szenarien lieferten keinen gültigen Wert und sind nicht eingerechnet.");
        if (valid.Count == 0)
        {
            notes.Add("Kein Szenario lieferte einen gültigen Wert — keine Aussage möglich.");
            return new StressResult
            {
                MetricKey = metricKey, Dimension = dimension, BaselineValue = baselineValue,
                Outcomes = outcomes, Notes = notes
            };
        }

        double worst = valid.Min(o => o.Value!.Value);
        double? share = baselineValue is { } bv ? valid.Count(o => o.Value!.Value < bv) / (double)valid.Count : null;
        double? degradation = baselineValue is { } b2 && b2 > 0 ? (b2 - worst) / b2 : null;

        if (degradation is > 1.0)
            notes.Add("Im schlechtesten Szenario kippt die Kennzahl ins Negative — das Ergebnis trägt die Stressannahmen nicht.");

        return new StressResult
        {
            MetricKey = metricKey,
            Dimension = dimension,
            BaselineValue = baselineValue,
            Outcomes = outcomes,
            Worst = worst,
            ShareBelowBaseline = share,
            RelativeDegradation = degradation,
            Notes = notes
        };
    }

    private static string Fmt(double v) => v.ToString("0.##", CultureInfo.InvariantCulture).Replace('.', '_');
}
