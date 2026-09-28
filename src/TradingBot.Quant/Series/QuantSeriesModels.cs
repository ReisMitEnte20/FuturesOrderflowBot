namespace TradingBot.Quant.Series;

/// <summary>Kontraktspezifikation, die zur Bewertung offener Positionen (Mark-to-Market) nötig ist.</summary>
/// <param name="TickSize">Kleinste Preisbewegung (z. B. 0,25).</param>
/// <param name="PointValue">Geldwert eines vollen Preispunkts je Kontrakt (z. B. 5,00 $).</param>
/// <param name="Currency">Währung der Geldbeträge.</param>
public sealed record QuantContractSpec(decimal TickSize, decimal PointValue, string Currency = "USD");

/// <summary>Auf welcher Kapitalgröße eine Renditereihe basiert.</summary>
public enum EquityBasis
{
    /// <summary>Nur realisierte, abgeschlossene Trades (kumulierter NetPnL). Offene Positionen ignoriert.</summary>
    Realized = 0,

    /// <summary>Gesamtkapital inkl. Mark-to-Market offener Positionen und deren geschätzter Glattstellungskosten.</summary>
    Total = 1
}

/// <summary>Aggregationsfrequenz einer Renditereihe.</summary>
public enum ReturnFrequency
{
    /// <summary>Ein Punkt je ausgewerteter Kerze (unregelmäßig im Kalender).</summary>
    Bar = 0,
    /// <summary>Ein Punkt je beobachtetem UTC-Kalendertag mit Bars. Tage ohne Daten werden NICHT erfunden.</summary>
    Daily = 1,
    /// <summary>Ein Punkt je beobachteter ISO-Woche.</summary>
    Weekly = 2,
    /// <summary>Ein Punkt je beobachtetem Kalendermonat.</summary>
    Monthly = 3
}

/// <summary>Wie aus der Periodenfrequenz ein Jahresfaktor gemacht wird. Immer explizit auszuweisen.</summary>
public enum AnnualizationBasis
{
    /// <summary>
    /// Empirisch: Perioden pro Jahr = Anzahl Renditeperioden / überspannte Kalenderjahre.
    /// Keine Annahme über Handelskalender; bei kurzen Reihen entsprechend unsicher.
    /// </summary>
    Observed = 0,

    /// <summary>Fester, vom Aufrufer gesetzter Wert (z. B. 252 Handelstage).</summary>
    Fixed = 1
}

/// <summary>
/// Ein Punkt der zeitlich ausgerichteten Kapitalkurve. <see cref="RealizedEquity"/> und
/// <see cref="TotalEquity"/> werden getrennt geführt, damit realisierte und gesamte
/// Drawdowns nie vermischt werden.
/// </summary>
public sealed record QuantEquityPoint
{
    /// <summary>Ende der Periode (Bar-Close bzw. letzter Bar-Close der Kalenderperiode), UTC.</summary>
    public DateTimeOffset Time { get; init; }

    /// <summary>Index der zugrunde liegenden Kerze im Original-Backtest (für Chart-Navigation).</summary>
    public int BarIndex { get; init; }

    /// <summary>Startkapital + kumulierter realisierter NetPnL.</summary>
    public decimal RealizedEquity { get; init; }

    /// <summary>
    /// <see cref="RealizedEquity"/> + Mark-to-Market einer offenen Position, abzüglich der
    /// geschätzten Glattstellungskosten (Round-Turn-Gebühren + Exit-Slippage). Dadurch sind
    /// realisierte und gesamte Equity konsistent gebührenbereinigt.
    /// </summary>
    public decimal TotalEquity { get; init; }

    /// <summary>Kontraktzahl der zum Periodenende offenen Position (0 = flat) — Basis für Exposure.</summary>
    public int OpenQuantity { get; init; }
}

/// <summary>
/// Zeitlich ausgerichtete Kapitalkurve eines Laufs, inklusive der Angaben, die zur Reproduktion
/// und zur ehrlichen Interpretation nötig sind.
/// </summary>
public sealed record QuantEquityCurve
{
    public required string Name { get; init; }
    public required IReadOnlyList<QuantEquityPoint> Points { get; init; }

    public decimal InitialCapital { get; init; }
    public ReturnFrequency Frequency { get; init; }
    public string Currency { get; init; } = "USD";

    /// <summary>
    /// Expliziter Startzeitpunkt der Kurve (Ende des ersten unaggregierten Bars, an dem das Kapital
    /// noch dem Startkapital entspricht). Er ist der Anker für die ERSTE Periodenrendite und wird aus
    /// den Engine-Metadaten übernommen — NICHT über Kapitalgleichheit erkannt, denn derselbe
    /// Kapitalwert kann nach Trades oder einer Nullrendite erneut auftreten. Null, wenn unbekannt.
    /// </summary>
    public DateTimeOffset? StartTime { get; init; }

    /// <summary>Wie die offene Position am Periodenende bewertet wurde (Dokumentation für den Bericht).</summary>
    public string MarkToMarketNote { get; init; } =
        "Offene Position zum Bar-Close bewertet, abzüglich Round-Turn-Gebühren und Exit-Slippage.";

    public int Count => Points.Count;
    public DateTimeOffset? Start => Points.Count > 0 ? Points[0].Time : null;
    public DateTimeOffset? End => Points.Count > 0 ? Points[^1].Time : null;

    public static readonly QuantEquityCurve Empty = new()
    {
        Name = "(leer)",
        Points = Array.Empty<QuantEquityPoint>()
    };
}

/// <summary>
/// Einheitliche Netto-Renditereihe: einfache Periodenrenditen einer Kapitalkurve, zeitlich
/// ausgerichtet an den Periodenenden. Länge = Anzahl Kurvenpunkte − 1
/// (<see cref="Timestamps"/>[i] gehört zu <see cref="Returns"/>[i]).
/// </summary>
public sealed record ReturnSeries
{
    public required string Name { get; init; }
    public required IReadOnlyList<DateTimeOffset> Timestamps { get; init; }
    public required IReadOnlyList<double> Returns { get; init; }

    /// <summary>Kapitalstand am Ende jeder Renditeperiode (gleiche Länge wie <see cref="Returns"/>).</summary>
    public required IReadOnlyList<double> EquityLevels { get; init; }

    public double InitialCapital { get; init; }
    public EquityBasis Basis { get; init; }
    public ReturnFrequency Frequency { get; init; }
    public string Currency { get; init; } = "USD";

    public int Count => Returns.Count;

    public static readonly ReturnSeries Empty = new()
    {
        Name = "(leer)",
        Timestamps = Array.Empty<DateTimeOffset>(),
        Returns = Array.Empty<double>(),
        EquityLevels = Array.Empty<double>()
    };

    /// <summary>
    /// Perioden pro Jahr für die Annualisierung. <see cref="AnnualizationBasis.Observed"/> leitet den
    /// Wert aus der tatsächlichen Beobachtungsdichte ab; er ist damit an die Datenlage gebunden und
    /// keine Kalenderannahme. Gibt null zurück, wenn die Reihe dafür zu kurz ist.
    /// </summary>
    public double? PeriodsPerYear(AnnualizationBasis basis, double? fixedValue = null)
    {
        if (basis == AnnualizationBasis.Fixed)
            return fixedValue is > 0 ? fixedValue : null;

        if (Timestamps.Count < 2) return null;
        double years = (Timestamps[^1] - Timestamps[0]).TotalDays / 365.25;
        if (years <= 0) return null;
        // Count Renditen überspannen (Count-1) Intervalle zwischen erstem und letztem Zeitstempel.
        return (Timestamps.Count - 1) / years;
    }
}
