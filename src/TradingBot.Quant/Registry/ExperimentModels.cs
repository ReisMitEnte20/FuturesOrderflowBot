using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TradingBot.Domain.Models;

namespace TradingBot.Quant.Registry;

/// <summary>Woher die geprüfte Strategie stammt.</summary>
public enum StrategyOrigin
{
    /// <summary>Manuell formulierte Regel.</summary>
    Manual = 0,
    /// <summary>Aus einer wissenschaftlichen Veröffentlichung abgeleitet.</summary>
    Paper = 1,
    /// <summary>Von einem Generator (KI) vorgeschlagen.</summary>
    Ai = 2
}

/// <summary>
/// Status eines Versuchs. Negative, fehlgeschlagene und verworfene Versuche werden bewusst
/// dauerhaft erfasst — sie sind für die Mehrfachtest-Korrektur (PBO/DSR) unverzichtbar.
/// </summary>
public enum TrialStatus
{
    Planned = 0,
    Running = 1,
    /// <summary>Durchgelaufen (unabhängig davon, ob das Ergebnis gut oder schlecht war).</summary>
    Completed = 2,
    /// <summary>Technisch gescheitert (Fehler, Abbruch, keine auswertbaren Daten).</summary>
    Failed = 3,
    /// <summary>Bewusst verworfen (z. B. Regelverstoß, ungeeignete Datenlage).</summary>
    Discarded = 4
}

/// <summary>In welche Richtung die Auswahlmetrik besser wird.</summary>
public enum SelectionDirection { HigherIsBetter = 0, LowerIsBetter = 1 }

/// <summary>
/// Eindeutiger Fingerabdruck des verwendeten Datensatzes. Zwei Läufe sind nur dann vergleichbar,
/// wenn Fingerabdruck UND Kostenprofil übereinstimmen.
/// </summary>
public sealed record DataFingerprint
{
    public required string Symbol { get; init; }
    public int TimeframeMinutes { get; init; }
    public required string Source { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }
    public int BarCount { get; init; }
    /// <summary>SHA-256 über die kanonisierten OHLCV-Werte aller Bars (Hex, Kleinbuchstaben).</summary>
    public required string Sha256 { get; init; }

    /// <summary>
    /// Berechnet den Fingerabdruck deterministisch aus den tatsächlich verwendeten Kerzen.
    /// Kanonische Form je Bar: "openTicksUtc|O|H|L|C|V" mit invarianter Kultur.
    /// </summary>
    public static DataFingerprint Compute(IReadOnlyList<Candle> candles, string symbol, int timeframeMinutes, string source)
    {
        ArgumentNullException.ThrowIfNull(candles);
        using var sha = SHA256.Create();
        var sb = new StringBuilder(64);
        using var ms = new MemoryStream();
        using (var writer = new StreamWriter(ms, new UTF8Encoding(false), 4096, leaveOpen: true))
        {
            writer.Write(symbol);
            writer.Write('|');
            writer.Write(timeframeMinutes.ToString(CultureInfo.InvariantCulture));
            writer.Write('\n');
            foreach (var c in candles)
            {
                sb.Clear();
                sb.Append(c.OpenTime.ToUniversalTime().Ticks).Append('|')
                  .Append(c.Open.ToString(CultureInfo.InvariantCulture)).Append('|')
                  .Append(c.High.ToString(CultureInfo.InvariantCulture)).Append('|')
                  .Append(c.Low.ToString(CultureInfo.InvariantCulture)).Append('|')
                  .Append(c.Close.ToString(CultureInfo.InvariantCulture)).Append('|')
                  .Append(c.Volume.ToString(CultureInfo.InvariantCulture)).Append('\n');
                writer.Write(sb.ToString());
            }
        }
        var hash = sha.ComputeHash(ms.ToArray());

        return new DataFingerprint
        {
            Symbol = symbol,
            TimeframeMinutes = timeframeMinutes,
            Source = source,
            From = candles.Count > 0 ? candles[0].OpenTime : null,
            To = candles.Count > 0 ? candles[^1].CloseTime : null,
            BarCount = candles.Count,
            Sha256 = Convert.ToHexString(hash).ToLowerInvariant()
        };
    }
}

/// <summary>Kostenprofil-Abbild eines Versuchs (damit „gleiche Kosten" überprüfbar bleibt).</summary>
public sealed record CostProfileSnapshot
{
    public decimal FeePerSide { get; init; }
    public decimal SlippageTicks { get; init; }
    public decimal TickSize { get; init; }
    public decimal PointValue { get; init; }
    public bool ApplyFees { get; init; } = true;
    public string Currency { get; init; } = "USD";
    /// <summary>Stammen die Werte aus *.example.json statt aus einer echten Brokerabrechnung?</summary>
    public bool IsExampleProfile { get; init; }
}

/// <summary>
/// Eine Suchkampagne. Budget, Suchraum und Auswahlkriterium werden VOR den Versuchen festgelegt
/// und danach gesperrt (<see cref="Locked"/>); das macht die effektive Versuchszahl nachprüfbar
/// und verhindert nachträgliches Umdefinieren des Erfolgskriteriums.
/// </summary>
public sealed record CampaignRecord
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public required string Hypothesis { get; init; }
    public required string SearchSpace { get; init; }
    public required string SelectionMetric { get; init; }
    public SelectionDirection SelectionDirection { get; init; } = SelectionDirection.HigherIsBetter;
    /// <summary>Maximale Zahl an Versuchen. Wird beim Hinzufügen erzwungen.</summary>
    public required int TrialBudget { get; init; }

    /// <summary>
    /// Datenbezug der Kampagne (SHA-256-Fingerabdruck des zugrunde liegenden Datensatzes). Wird beim
    /// Anlegen gesperrt; eine spätere Wiederverwendung mit anderem Datenbezug wird abgelehnt. Null, wenn
    /// beim Anlegen kein Fingerabdruck vorlag.
    /// </summary>
    public string? DataSha { get; init; }

    /// <summary>Finaler Holdout, der während der Suche nicht ausgewertet werden darf.</summary>
    public DateTimeOffset? HoldoutFrom { get; init; }
    public DateTimeOffset? HoldoutTo { get; init; }
    /// <summary>Wurde der Holdout bereits ausgewertet? Danach ist er verbraucht.</summary>
    public bool HoldoutConsumed { get; init; }
    /// <summary>Zeitpunkt des (einmaligen) Holdout-Verbrauchs.</summary>
    public DateTimeOffset? HoldoutConsumedUtc { get; init; }
    /// <summary>
    /// An welchen Kandidaten und welche Konfiguration die finale Holdout-Auswertung gebunden war
    /// (Nachvollziehbarkeit: eine finale Auswertung ist eindeutig einem Kandidaten zugeordnet).
    /// </summary>
    public string? HoldoutEvaluatedReference { get; init; }

    /// <summary>Nach dem Sperren sind Budget/Suchraum/Kriterium unveränderlich.</summary>
    public bool Locked { get; init; }

    public string? Notes { get; init; }
}

/// <summary>Ein einzelner Versuch (Trial) — vollständig reproduzierbar beschrieben.</summary>
public sealed record TrialRecord
{
    public required string Id { get; init; }
    public required string CampaignId { get; init; }
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedUtc { get; init; }

    public required string StrategyId { get; init; }
    public required string StrategyVersion { get; init; }
    public StrategyOrigin Origin { get; init; } = StrategyOrigin.Manual;
    /// <summary>Quelle: Paper-Referenz/DOI/URL, Generator-Lauf-ID oder manuelle Notiz.</summary>
    public string? OriginReference { get; init; }

    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
    public required DataFingerprint Data { get; init; }
    public CostProfileSnapshot Costs { get; init; } = new();

    /// <summary>Git-Commit oder anderer Codestand, mit dem der Versuch lief.</summary>
    public required string CodeVersion { get; init; }
    public int Seed { get; init; }

    /// <summary>Ausgewerteter Zeitraum (kann enger sein als der Datenfingerabdruck, z. B. ein WF-Fenster).</summary>
    public DateTimeOffset? PeriodFrom { get; init; }
    public DateTimeOffset? PeriodTo { get; init; }
    /// <summary>Rolle des Zeitraums: "train", "validation", "test", "holdout", "full".</summary>
    public string PeriodRole { get; init; } = "full";

    public TrialStatus Status { get; init; } = TrialStatus.Planned;
    /// <summary>Grund bei <see cref="TrialStatus.Failed"/>/<see cref="TrialStatus.Discarded"/> — Pflicht für die Nachvollziehbarkeit.</summary>
    public string? StatusReason { get; init; }

    /// <summary>Ergebniskennzahlen. Null-Werte bedeuten ausdrücklich „nicht berechenbar".</summary>
    public IReadOnlyDictionary<string, double?> Metrics { get; init; } = new Dictionary<string, double?>();

    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public string? Notes { get; init; }
}
