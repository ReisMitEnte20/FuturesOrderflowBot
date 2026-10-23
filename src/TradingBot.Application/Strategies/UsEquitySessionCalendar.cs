namespace TradingBot.Application.Strategies;

/// <summary>
/// Kleiner, belegter US-Aktien-Session-Kalender (NYSE/Nasdaq-Regelsessions) für die ORB-Baseline. Deckt das
/// vorgesehene Datenfenster ab (2026) und wird VOR der Strategieausführung genutzt, um Sessions ohne reguläre
/// RTH (volle Feiertage) auszuschließen und das Session-Ende an verkürzten Handelstagen (Early Close 13:00 ET)
/// korrekt zu setzen. Zeiten sind America/New_York-Ortszeit (DST-Umrechnung erfolgt beim Aufrufer).
///
/// Quelle: offizieller NYSE-Feiertags-/Verkürzungsplan 2026. Für unser Fenster 2026-03-16…2026-06-11 sind die
/// relevanten AUSGESCHLOSSENEN Sessions: Karfreitag 2026-04-03 und Memorial Day 2026-05-25 (volle Feiertage);
/// Early-Close-Tage fallen NICHT in dieses Fenster.
/// </summary>
public static class UsEquitySessionCalendar
{
    private static readonly TimeSpan RegularClose = new(16, 0, 0);
    private static readonly TimeSpan EarlyCloseTime = new(13, 0, 0);
    private static readonly TimeSpan RthOpen = new(9, 30, 0);

    /// <summary>Gemeinsame America/New_York-Zeitzone (DST-korrekt) für ORB-Strategie und Session-Grenzen.</summary>
    public static readonly TimeZoneInfo NewYork = ResolveNewYork();

    /// <summary>Volle Feiertage (keine reguläre RTH-Session) — 2026.</summary>
    private static readonly HashSet<DateOnly> FullHolidays = new()
    {
        new(2026, 1, 1),    // New Year's Day
        new(2026, 1, 19),   // Martin Luther King Jr. Day
        new(2026, 2, 16),   // Washington's Birthday
        new(2026, 4, 3),    // Good Friday
        new(2026, 5, 25),   // Memorial Day
        new(2026, 6, 19),   // Juneteenth
        new(2026, 7, 3),    // Independence Day (4. Juli = Samstag → Freitag beobachtet)
        new(2026, 9, 7),    // Labor Day
        new(2026, 11, 26),  // Thanksgiving Day
        new(2026, 12, 25),  // Christmas Day
    };

    /// <summary>Verkürzte Handelstage (Early Close 13:00 ET) — 2026.</summary>
    private static readonly HashSet<DateOnly> EarlyCloseDays = new()
    {
        new(2026, 11, 27),  // Tag nach Thanksgiving
        new(2026, 12, 24),  // Heiligabend
    };

    /// <summary>True, wenn an diesem NY-Datum eine reguläre RTH-Session stattfindet (kein voller Feiertag).</summary>
    public static bool IsRegularSession(DateOnly nyDate) => !FullHolidays.Contains(nyDate);

    /// <summary>True, wenn dieses NY-Datum ein voller Feiertag ist (ORB handelt nicht).</summary>
    public static bool IsFullHoliday(DateOnly nyDate) => FullHolidays.Contains(nyDate);

    /// <summary>Session-Ende (ET-Ortszeit) für dieses NY-Datum: 13:00 an Early-Close-Tagen, sonst 16:00.</summary>
    public static TimeSpan SessionEnd(DateOnly nyDate) => EarlyCloseDays.Contains(nyDate) ? EarlyCloseTime : RegularClose;

    /// <summary>True, wenn dieses NY-Datum ein verkürzter Handelstag ist.</summary>
    public static bool IsEarlyClose(DateOnly nyDate) => EarlyCloseDays.Contains(nyDate);

    /// <summary>
    /// Bar-Indizes, an denen eine gültige ORB-Session BEGINNT: die erste Kerze, deren NY-Open-Zeit exakt 09:30 ist,
    /// je regulärem Handelstag (volle Feiertage ausgeschlossen). Bestimmt NUR aus den Open-Zeitstempeln + Kalender —
    /// nicht aus Trades/Ergebnissen. Dient der session-ausgerichteten Walk-forward-Aufteilung.
    /// </summary>
    public static IReadOnlyList<int> OrbSessionStartIndices(IReadOnlyList<DateTimeOffset> openTimes)
    {
        ArgumentNullException.ThrowIfNull(openTimes);
        var result = new List<int>();
        DateOnly? lastAdded = null;
        for (int i = 0; i < openTimes.Count; i++)
        {
            var ny = TimeZoneInfo.ConvertTime(openTimes[i], NewYork);
            var nyDate = DateOnly.FromDateTime(ny.DateTime);
            if (ny.TimeOfDay == RthOpen && lastAdded != nyDate && IsRegularSession(nyDate))
            {
                result.Add(i);
                lastAdded = nyDate;
            }
        }
        return result;
    }

    private static TimeZoneInfo ResolveNewYork()
    {
        foreach (var id in new[] { "America/New_York", "Eastern Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        throw new InvalidOperationException("Zeitzone America/New_York nicht verfügbar.");
    }
}
