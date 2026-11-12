using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AtasSessionCollector;

/// <summary>
/// Session-Definitionen, Zeitzonen-Umrechnung (DST-korrekt via TimeZoneInfo) und ein
/// nachvollziehbarer (berechneter) Handelskalender. Bewusst rein und ohne Seiteneffekte,
/// damit die Fensterberechnung testbar ist. KEINE Aufnahme-Logik hier.
///
/// Wichtig: Fenster werden in der SESSION-LOKALZEIT definiert (New York / London) und je
/// historischem Datum korrekt nach UTC und Europe/Berlin umgerechnet. Die bestehende
/// Probe erwartet Berlin-Wandzeiten (startBerlin/endBerlin); diese Klasse liefert sie,
/// ohne die bestehenden Berlin-Fenster semantisch zu veraendern.
/// </summary>
public enum SessionId { NewYork, London }

public enum DayStatus { Trading, Weekend, Holiday, EarlyCloseTrading }

public sealed record SessionDef(
    SessionId Id,
    string Name,           // "NewYork" / "London" (Collector-Session + Ordnername)
    string DisplayName,    // "New York (RTH-Open)" fuer die Oberflaeche
    string TzId,           // Windows-TimeZoneInfo-Id der Session
    string TzIana,         // IANA-Name (nur zur Anzeige)
    string FolderSlug,     // "America-New-York"
    TimeSpan LocalStart,   // 09:30
    TimeSpan LocalEnd);    // 12:00

public sealed record DayPlan(
    DateOnly Date,
    DayStatus Status,
    string? Reason,                  // bei Weekend/Holiday: Grund; bei EarlyClose: Hinweis
    DateTime LocalStart, DateTime LocalEnd,     // Session-Lokalzeit (unspecified kind, nur Anzeige)
    DateTime BerlinStart, DateTime BerlinEnd,   // Europe/Berlin Wandzeit (fuer die Probe)
    DateTime UtcStart, DateTime UtcEnd)         // UTC [start,end)
{
    public bool WillRecord => Status is DayStatus.Trading or DayStatus.EarlyCloseTrading;
    public string BerlinStartString => BerlinStart.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);
    public string BerlinEndString => BerlinEnd.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture);
}

public static class SessionCalendar
{
    public const string BerlinTzId = "W. Europe Standard Time";

    public static readonly IReadOnlyList<SessionDef> Sessions = new[]
    {
        new SessionDef(SessionId.NewYork, "NewYork", "New York (Aktien-Open 09:30–12:00)",
            "Eastern Standard Time", "America/New_York", "America-New-York",
            new TimeSpan(9, 30, 0), new TimeSpan(12, 0, 0)),
        new SessionDef(SessionId.London, "London", "London (Aktien-Open 08:00–11:00)",
            "GMT Standard Time", "Europe/London", "Europe-London",
            new TimeSpan(8, 0, 0), new TimeSpan(11, 0, 0)),
    };

    public static SessionDef Get(SessionId id) => Sessions.First(s => s.Id == id);

    private static TimeZoneInfo Tz(string id)
    {
        // Windows-Id zuerst, IANA als Fallback (falls jemals auf ICU/Linux gebaut).
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch
        {
            var alt = id switch
            {
                "Eastern Standard Time" => "America/New_York",
                "GMT Standard Time" => "Europe/London",
                "W. Europe Standard Time" => "Europe/Berlin",
                _ => id,
            };
            return TimeZoneInfo.FindSystemTimeZoneById(alt);
        }
    }

    /// <summary>Plant genau einen Kalendertag fuer eine Session (ohne Aufnahme).</summary>
    public static DayPlan PlanDay(DateOnly date, SessionDef s)
    {
        var tzSession = Tz(s.TzId);
        var tzBerlin = Tz(BerlinTzId);

        var localStart = date.ToDateTime(TimeOnly.FromTimeSpan(s.LocalStart));
        var localEnd = date.ToDateTime(TimeOnly.FromTimeSpan(s.LocalEnd));

        var utcStart = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localStart, DateTimeKind.Unspecified), tzSession);
        var utcEnd = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(localEnd, DateTimeKind.Unspecified), tzSession);
        var berlinStart = TimeZoneInfo.ConvertTimeFromUtc(utcStart, tzBerlin);
        var berlinEnd = TimeZoneInfo.ConvertTimeFromUtc(utcEnd, tzBerlin);

        var (status, reason) = ClassifyDay(date, s.Id);

        return new DayPlan(date, status, reason,
            localStart, localEnd, berlinStart, berlinEnd, utcStart, utcEnd);
    }

    /// <summary>Alle Tage im (inklusiven) Bereich planen.</summary>
    public static List<DayPlan> PlanRange(DateOnly from, DateOnly to, SessionDef s)
    {
        var list = new List<DayPlan>();
        for (var d = from; d <= to; d = d.AddDays(1))
            list.Add(PlanDay(d, s));
        return list;
    }

    // ------------------------------------------------------------------
    // Nachvollziehbarer Handelskalender (berechnet, kein hartkodiertes Jahr).
    // Vollstaendigkeit wird NICHT behauptet; fehlende Replay-Daten werden zusaetzlich
    // durch die Daten-Verifikation der Aufnahme abgefangen (0-Trades/kein Endsignal != Erfolg).
    // ------------------------------------------------------------------
    public static (DayStatus status, string? reason) ClassifyDay(DateOnly date, SessionId id)
    {
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return (DayStatus.Weekend, "Wochenende (keine Session)");

        var holidays = id == SessionId.NewYork ? UsHolidays(date.Year) : UkHolidays(date.Year);
        if (holidays.TryGetValue(date, out var hname))
            return (DayStatus.Holiday, "Feiertag/Boersenschluss: " + hname);

        var early = id == SessionId.NewYork ? UsEarlyClose(date.Year) : UkEarlyClose(date.Year);
        if (early.TryGetValue(date, out var ename))
            return (DayStatus.EarlyCloseTrading, "Verkuerzter Handelstag (" + ename + ") – Aufnahmefenster liegt vor dem fruehen Schluss");

        return (DayStatus.Trading, null);
    }

    // ---- Datumshelfer ----
    private static DateOnly Easter(int year)
    {
        int a = year % 19, b = year / 100, c = year % 100, d = b / 4, e = b % 4;
        int f = (b + 8) / 25, g = (b - f + 1) / 3;
        int h = (19 * a + b - d - g + 15) % 30, i = c / 4, k = c % 4;
        int l = (32 + 2 * e + 2 * i - h - k) % 7;
        int m = (a + 11 * h + 22 * l) / 451;
        int month = (h + l - 7 * m + 114) / 31;
        int day = ((h + l - 7 * m + 114) % 31) + 1;
        return new DateOnly(year, month, day);
    }

    private static DateOnly Nth(int year, int month, DayOfWeek dow, int n)
    {
        var d = new DateOnly(year, month, 1);
        int off = ((int)dow - (int)d.DayOfWeek + 7) % 7;
        return d.AddDays(off + 7 * (n - 1));
    }

    private static DateOnly Last(int year, int month, DayOfWeek dow)
    {
        var d = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        int off = ((int)d.DayOfWeek - (int)dow + 7) % 7;
        return d.AddDays(-off);
    }

    // US-Boerse: Feiertag faellt auf Sa -> Freitag davor; So -> Montag danach (Ausnahme: Neujahr auf Sa wird nicht vorgezogen).
    private static DateOnly UsObserved(DateOnly d, bool shiftSaturday = true)
    {
        if (d.DayOfWeek == DayOfWeek.Saturday) return shiftSaturday ? d.AddDays(-1) : d;
        if (d.DayOfWeek == DayOfWeek.Sunday) return d.AddDays(1);
        return d;
    }

    private static Dictionary<DateOnly, string> UsHolidays(int y)
    {
        var easter = Easter(y);
        var hs = new Dictionary<DateOnly, string>();
        void Add(DateOnly d, string n) { if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) hs[d] = n; }
        Add(UsObserved(new DateOnly(y, 1, 1), shiftSaturday: false), "Neujahr");
        Add(Nth(y, 1, DayOfWeek.Monday, 3), "Martin Luther King Jr. Day");
        Add(Nth(y, 2, DayOfWeek.Monday, 3), "Presidents' Day");
        Add(easter.AddDays(-2), "Karfreitag (Good Friday)");
        Add(Last(y, 5, DayOfWeek.Monday), "Memorial Day");
        Add(UsObserved(new DateOnly(y, 6, 19)), "Juneteenth");
        Add(UsObserved(new DateOnly(y, 7, 4)), "Independence Day");
        Add(Nth(y, 9, DayOfWeek.Monday, 1), "Labor Day");
        Add(Nth(y, 11, DayOfWeek.Thursday, 4), "Thanksgiving");
        Add(UsObserved(new DateOnly(y, 12, 25)), "Weihnachten");
        return hs;
    }

    private static Dictionary<DateOnly, string> UsEarlyClose(int y)
    {
        var es = new Dictionary<DateOnly, string>();
        void Add(DateOnly d, string n) { if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) es[d] = n; }
        Add(Nth(y, 11, DayOfWeek.Thursday, 4).AddDays(1), "Tag nach Thanksgiving, Schluss 13:00 ET");
        Add(new DateOnly(y, 12, 24), "Heiligabend, Schluss 13:00 ET");
        var jul4 = new DateOnly(y, 7, 4);
        if (jul4.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            Add(jul4.AddDays(-1), "Tag vor Independence Day, Schluss 13:00 ET");
        return es;
    }

    // UK (LSE): Ersatztag immer am naechsten Werktag, der nicht schon Feiertag ist.
    private static Dictionary<DateOnly, string> UkHolidays(int y)
    {
        var easter = Easter(y);
        var hs = new Dictionary<DateOnly, string>();
        var taken = new HashSet<DateOnly>();
        DateOnly NextFree(DateOnly d)
        {
            while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || taken.Contains(d)) d = d.AddDays(1);
            return d;
        }
        void Fixed(DateOnly d, string n)
        {
            var o = (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) ? NextFree(d) : d;
            taken.Add(o); hs[o] = n + (o != d ? " (Ersatztag)" : "");
        }
        void Exact(DateOnly d, string n) { if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) { taken.Add(d); hs[d] = n; } }

        Fixed(new DateOnly(y, 1, 1), "Neujahr");
        Exact(easter.AddDays(-2), "Karfreitag (Good Friday)");
        Exact(easter.AddDays(1), "Ostermontag");
        Exact(Nth(y, 5, DayOfWeek.Monday, 1), "Early May Bank Holiday");
        Exact(Last(y, 5, DayOfWeek.Monday), "Spring Bank Holiday");
        Exact(Last(y, 8, DayOfWeek.Monday), "Summer Bank Holiday");
        Fixed(new DateOnly(y, 12, 25), "Weihnachten");
        Fixed(new DateOnly(y, 12, 26), "Boxing Day");
        return hs;
    }

    private static Dictionary<DateOnly, string> UkEarlyClose(int y)
    {
        var es = new Dictionary<DateOnly, string>();
        void Add(DateOnly d, string n) { if (d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)) es[d] = n; }
        Add(new DateOnly(y, 12, 24), "Heiligabend, fruehe Schlusszeit");
        Add(new DateOnly(y, 12, 31), "Silvester, fruehe Schlusszeit");
        return es;
    }

    /// <summary>Ordnername gemaess Vorgabe, z.B. MNQ_NewYork_0930-1200_America-New-York_2026-10-26_bis_2026-10-28.</summary>
    public static string CollectionFolderName(string instrument, SessionDef s, DateOnly from, DateOnly to)
    {
        string win = $"{s.LocalStart:hh\\:mm}".Replace(":", "") + "-" + $"{s.LocalEnd:hh\\:mm}".Replace(":", "");
        return $"{instrument}_{s.Name}_{win}_{s.FolderSlug}_{from:yyyy-MM-dd}_bis_{to:yyyy-MM-dd}";
    }
}
