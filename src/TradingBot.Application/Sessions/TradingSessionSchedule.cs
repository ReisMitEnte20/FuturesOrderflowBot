using System.Globalization;

namespace TradingBot.Application.Sessions;

/// <summary>Eine geplante Handelssession in ihrer lokalen Börsenzeit (z. B. London 08:00–11:00 Europe/London).</summary>
public sealed record SessionDefinition(string Key, string Label, string TimeZoneId, TimeOnly Start, TimeOnly End)
{
    public TimeZoneInfo TimeZone => TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);
}

/// <summary>Konkretes UTC-Fenster einer Session an einem Kalendertag. <c>CloseOutUtc</c> = vorab festgelegte Grenze, ab der
/// keine neuen Einstiege mehr zulässig sind und eine offene Position glattgestellt wird (Ende minus Close-Out-Vorlauf).</summary>
public sealed record PlannedSessionWindow(string Key, string Label, DateOnly Day, DateTimeOffset StartUtc, DateTimeOffset EndUtc, DateTimeOffset CloseOutUtc)
{
    public string Id => $"{Key}:{Day:yyyy-MM-dd}";
}

/// <summary>Phase der Sessionregel zu einem Zeitpunkt.</summary>
public enum SessionPhase
{
    /// <summary>In einer Session vor der Close-Out-Grenze: Einstiege erlaubt.</summary>
    Open,
    /// <summary>Zwischen Close-Out-Grenze und Sessionende: keine neuen Einstiege, offene Position wird glattgestellt.</summary>
    CloseOut,
    /// <summary>Zwischen zwei Sessions (unbeobachtete Pause): keine Einstiege, keine Position.</summary>
    Pause
}

/// <summary>Zustand der Sessionregel (identisch für Backtest, Replay und – später – Live-Paper).</summary>
public sealed record SessionRuleState(SessionPhase Phase, PlannedSessionWindow? Current, PlannedSessionWindow? Next, bool NewEntriesAllowed, string Reason);

/// <summary>
/// Sessionplan „London + New York“ (oder jede andere Liste lokaler Sessions). Rechnet je Kalendertag (Mo–Fr) die
/// UTC-Fenster sommerzeitrichtig aus den lokalen Börsenzeiten – der Abstand zwischen den Sessions ist dadurch NICHT
/// fest (z. B. 2 h 30 min, wenn die USA schon Sommerzeit haben, Großbritannien noch nicht). Reine Zeitregel ohne
/// Daten-, Order- oder Broker-Bezug: Backtest und ein späterer Live-Paper-Betrieb fragen dieselbe Regel.
/// </summary>
public sealed class TradingSessionSchedule
{
    private readonly IReadOnlyList<SessionDefinition> _defs;

    public TradingSessionSchedule(IReadOnlyList<SessionDefinition> definitions, TimeSpan closeOutBeforeEnd)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        if (definitions.Count == 0) throw new ArgumentException("Mindestens eine Session.", nameof(definitions));
        if (closeOutBeforeEnd < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(closeOutBeforeEnd));
        foreach (var d in definitions)
        {
            if (d.End <= d.Start) throw new ArgumentException($"Session {d.Key}: Ende muss nach dem Start liegen (gleicher lokaler Tag).");
            if (d.End - d.Start <= closeOutBeforeEnd) throw new ArgumentException($"Session {d.Key}: Close-Out-Vorlauf ist länger als die Session.");
            _ = d.TimeZone; // fail-fast bei unbekannter Zeitzone
        }
        _defs = definitions;
        CloseOutBeforeEnd = closeOutBeforeEnd;
    }

    public IReadOnlyList<SessionDefinition> Definitions => _defs;
    public TimeSpan CloseOutBeforeEnd { get; }

    /// <summary>Geplante Fenster eines Kalendertags (Wochenende: keine), nach UTC-Start sortiert.</summary>
    public IReadOnlyList<PlannedSessionWindow> WindowsFor(DateOnly day)
    {
        if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return Array.Empty<PlannedSessionWindow>();
        var list = new List<PlannedSessionWindow>(_defs.Count);
        foreach (var d in _defs)
        {
            var tz = d.TimeZone;
            var start = ToUtc(day, d.Start, tz);
            var end = ToUtc(day, d.End, tz);
            list.Add(new PlannedSessionWindow(d.Key, d.Label, day, start, end, end - CloseOutBeforeEnd));
        }
        list.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
        return list;
    }

    /// <summary>Alle geplanten Fenster der Kalendertage [from, to] (inklusive), chronologisch.</summary>
    public IReadOnlyList<PlannedSessionWindow> WindowsBetween(DateOnly from, DateOnly to)
    {
        var all = new List<PlannedSessionWindow>();
        for (var d = from; d <= to; d = d.AddDays(1)) all.AddRange(WindowsFor(d));
        return all;
    }

    /// <summary>Regelzustand zu einem UTC-Zeitpunkt (z. B. für eine Live-Paper-Uhr während der echten Pause).</summary>
    public SessionRuleState StateAt(DateTimeOffset utc)
    {
        var local = DateOnly.FromDateTime(utc.UtcDateTime);
        // Fenster um den Zeitpunkt herum (ein Tag Puffer beidseitig genügt für Sessions innerhalb eines lokalen Tages).
        var windows = WindowsBetween(local.AddDays(-1), local.AddDays(7));
        foreach (var w in windows)
        {
            if (utc < w.StartUtc) return new(SessionPhase.Pause, null, w, false, $"Pause bis {w.Label} {Fmt(w.StartUtc)} – keine neuen Einstiege.");
            if (utc < w.CloseOutUtc) return new(SessionPhase.Open, w, Next(windows, w), true, $"{w.Label} offen bis Close-Out {Fmt(w.CloseOutUtc)}.");
            if (utc < w.EndUtc) return new(SessionPhase.CloseOut, w, Next(windows, w), false, $"{w.Label}: Close-Out seit {Fmt(w.CloseOutUtc)} – keine neuen Einstiege, Position glattstellen.");
        }
        return new(SessionPhase.Pause, null, null, false, "Keine geplante Session in Sicht – keine neuen Einstiege.");
    }

    private static PlannedSessionWindow? Next(IReadOnlyList<PlannedSessionWindow> all, PlannedSessionWindow w)
    {
        for (int i = 0; i < all.Count; i++) if (all[i].StartUtc >= w.EndUtc) return all[i];
        return null;
    }

    private static DateTimeOffset ToUtc(DateOnly day, TimeOnly time, TimeZoneInfo tz)
    {
        var local = day.ToDateTime(time, DateTimeKind.Unspecified);
        if (tz.IsInvalidTime(local)) throw new InvalidOperationException($"Lokale Zeit {local:yyyy-MM-dd HH:mm} existiert in {tz.Id} nicht (Zeitumstellung).");
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, tz), TimeSpan.Zero);
    }

    private static string Fmt(DateTimeOffset t) => t.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
}
