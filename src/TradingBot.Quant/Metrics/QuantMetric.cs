namespace TradingBot.Quant.Metrics;

/// <summary>
/// Eine einzelne Kennzahl mit allem, was zur ehrlichen Interpretation nötig ist: Wert, Methode,
/// Eingaben, Grenzen und Stichprobenumfang. <see cref="Value"/> = null bedeutet ausdrücklich
/// „nicht berechenbar"; der Grund steht in <see cref="UnavailableReason"/>. Es wird nie ein
/// Ersatzwert gesetzt.
/// </summary>
public sealed record QuantMetric
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public double? Value { get; init; }
    /// <summary>Einheit: "fraction", "fraction/yr", "ratio", "currency", "periods", "days", "count".</summary>
    public required string Unit { get; init; }

    /// <summary>Wie gerechnet wurde (Formel/Konvention), kurz und konkret.</summary>
    public required string Method { get; init; }
    /// <summary>Welche Eingaben verwendet wurden.</summary>
    public required string Inputs { get; init; }
    /// <summary>Bekannte Grenzen dieser Kennzahl in diesem Kontext.</summary>
    public string? Limitation { get; init; }

    /// <summary>Zahl der zugrunde liegenden Beobachtungen (Renditeperioden bzw. Trades).</summary>
    public int SampleSize { get; init; }

    public string? UnavailableReason { get; init; }

    public bool IsAvailable => Value.HasValue && !double.IsNaN(Value.Value) && !double.IsInfinity(Value.Value);

    public static QuantMetric Unavailable(string key, string label, string unit, string method, string inputs,
        string reason, int sampleSize = 0, string? limitation = null) => new()
    {
        Key = key, Label = label, Unit = unit, Method = method, Inputs = inputs,
        Value = null, UnavailableReason = reason, SampleSize = sampleSize, Limitation = limitation
    };
}

/// <summary>Kennzahlen einer Drawdown-Analyse, getrennt nach Tiefe und Dauer.</summary>
public sealed record DrawdownInfo
{
    /// <summary>Maximaler relativer Rückgang vom bisherigen Höchststand, 0..1.</summary>
    public double MaxDrawdownFraction { get; init; }
    /// <summary>Maximaler absoluter Rückgang in Währungseinheiten (≥ 0).</summary>
    public double MaxDrawdownAbsolute { get; init; }

    public DateTimeOffset? PeakTime { get; init; }
    public DateTimeOffset? TroughTime { get; init; }
    /// <summary>Zeitpunkt der Rückkehr auf den alten Höchststand; null = bis Reihenende nicht erholt.</summary>
    public DateTimeOffset? RecoveryTime { get; init; }

    /// <summary>Längste Unterwasserphase in Perioden (Peak bis Erholung bzw. Reihenende).</summary>
    public int LongestUnderwaterPeriods { get; init; }
    public double LongestUnderwaterDays { get; init; }
    /// <summary>Lag die Reihe am Ende noch unter Wasser?</summary>
    public bool UnderwaterAtEnd { get; init; }

    public static readonly DrawdownInfo Empty = new();
}
