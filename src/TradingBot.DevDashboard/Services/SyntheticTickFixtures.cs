using System.Globalization;
using System.Text;

namespace TradingBot.DevDashboard.Services;

/// <summary>
/// SYNTHETISCHE MNQ-Trade-Ticks (keine Marktdaten) für die technische Abnahme des Tick-Orderflow-Pfads. Deterministisch im
/// Code erzeugt (keine eingecheckte CSV), im ATAS-Tickformat, damit sie durch denselben Importer laufen wie echte Aufnahmen.
/// Jede Bar hat genau 500 Ticks mit Volumen 1; das Bar-Delta ist exakt vorgegeben (Käufer − Verkäufer), der Preis bewegt sich
/// je Käufer-Tick +0,25 und je Verkäufer-Tick −0,25. So erzeugen die Delta-Schwellen 24/30/36 absichtlich unterschiedliche
/// Signale (Bars mit Delta ±28 und ±34 liegen zwischen den Schwellen).
/// </summary>
public static class SyntheticTickFixtures
{
    public const string OneDayId = "synthetic:mnq-1tag";
    public const string MultiDayId = "synthetic:mnq-mehrtage";
    public const int TicksPerBar = 500;
    /// <summary>Symbol jedes erzeugten Ticks.</summary>
    public const string Symbol = "MNQ";

    /// <summary>Handelstage der Mehrtages-Fixture: Werktage 01.–16.06.2026, der 10.06. fehlt absichtlich (fehlender Datentag).</summary>
    public static IReadOnlyList<DateOnly> MultiDayDates { get; } = new[]
    {
        new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 2), new DateOnly(2026, 6, 3), new DateOnly(2026, 6, 4), new DateOnly(2026, 6, 5),
        new DateOnly(2026, 6, 8), new DateOnly(2026, 6, 9), new DateOnly(2026, 6, 11), new DateOnly(2026, 6, 12),
        new DateOnly(2026, 6, 15), new DateOnly(2026, 6, 16)
    };

    // Bar-Deltas je Tagesmuster (6 Bars à 500 Ticks). F = schwache Signale laufen in Richtung des folgenden starken Moves
    // (niedrige Schwelle profitiert), K = schwache Signale sind Fehlsignale vor einer Gegenbewegung (hohe Schwelle profitiert),
    // N = ausgeglichen (Delta 0, keine Einstiege).
    private static readonly IReadOnlyDictionary<char, int[]> Patterns = new Dictionary<char, int[]>
    {
        ['F'] = new[] { 28, 500, 500, -28, -500, -500 },
        ['G'] = new[] { 34, 500, 400, -34, -500, -400 },
        ['K'] = new[] { 28, -500, -500, -34, 500, 500 },
        ['L'] = new[] { 34, -400, -500, -28, 400, 500 },
        ['N'] = new[] { 0, 0, 0, 0, 0, 0 }
    };

    /// <summary>Tagesmuster der Mehrtages-Fixture (gleiche Reihenfolge wie <see cref="MultiDayDates"/>); der 08.06. ist ein Tag ohne Einstiegssignal.</summary>
    public const string MultiDayPattern = "FGKFLNKGFLK";

    public static string OneDayCsv()
    {
        // Unverändert die bisherige Ein-Tages-Sequenz (buy/sell/buy/neutral/sell/buy, je 500 Ticks gleicher Aggressor).
        var sb = new StringBuilder("Time,Instrument,Price,Volume,AggressorSide\n");
        var t0 = new DateTimeOffset(2026, 6, 23, 13, 30, 0, TimeSpan.Zero);
        decimal price = 20000m; int idx = 0;
        foreach (var typ in new[] { "buy", "sell", "buy", "neutral", "sell", "buy" })
        {
            for (int i = 0; i < 500; i++)
            {
                decimal p = typ == "buy" ? price + i * 0.25m : typ == "sell" ? price - i * 0.25m : price;
                string ag = typ == "buy" ? "Buy" : typ == "sell" ? "Sell" : (i % 2 == 0 ? "Buy" : "Sell");
                Row(sb, t0.AddMilliseconds(100 * idx++), p, ag);
            }
            price = typ == "buy" ? price + 125m : typ == "sell" ? price - 125m : price;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Mehrtages-Fixture. <paramref name="mutateDays"/>: an diesen Handelstagen werden die Aggressoren invertiert und die
    /// Preisverlauf dieses Tages gespiegelt — der KALENDER (alle Zeitstempel, Tickanzahl je Tag) bleibt identisch. Für die Nachweise
    /// „kein Blick in die Zukunft“ und „Holdout bleibt zu“.
    /// </summary>
    public static string MultiDayCsv(IReadOnlyCollection<DateOnly>? mutateDays = null)
    {
        var sb = new StringBuilder("Time,Instrument,Price,Volume,AggressorSide\n");
        for (int d = 0; d < MultiDayDates.Count; d++)
        {
            var date = MultiDayDates[d];
            // Jeder Tag startet auf einem festen Preisniveau — eine Änderung an einem Tag verändert keinen anderen Tag.
            decimal price = 20000m + 50m * d;
            bool flip = mutateDays?.Contains(date) == true;
            var t0 = new DateTimeOffset(date.ToDateTime(new TimeOnly(13, 30)), TimeSpan.Zero);
            int idx = 0;
            foreach (int delta0 in Patterns[MultiDayPattern[d]])
            {
                int delta = flip ? -delta0 : delta0;
                int buys = (TicksPerBar + delta) / 2;
                // Käufer und Verkäufer gleichmäßig verteilt (Bresenham), damit der Preisverlauf glatt ist.
                int b = 0;
                for (int i = 0; i < TicksPerBar; i++)
                {
                    bool isBuy = (long)(i + 1) * buys / TicksPerBar > b;
                    if (isBuy) b++;
                    price += isBuy ? 0.25m : -0.25m;
                    Row(sb, t0.AddMilliseconds(100 * idx++), price, isBuy ? "Buy" : "Sell");
                }
            }
        }
        return sb.ToString();
    }

    public static string? CsvFor(string dataSourceId) => dataSourceId switch
    {
        OneDayId => OneDayCsv(),
        MultiDayId => MultiDayCsv(),
        _ => null
    };

    private static void Row(StringBuilder sb, DateTimeOffset t, decimal price, string aggressor) =>
        sb.Append(t.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture)).Append("Z,").Append(Symbol).Append(',')
          .Append(price.ToString("F2", CultureInfo.InvariantCulture)).Append(",1,").Append(aggressor).Append('\n');
}
