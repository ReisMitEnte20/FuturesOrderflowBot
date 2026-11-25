using System.Globalization;
using System.Text.Json;
using TradingBot.Application.Sessions;

namespace TradingBot.DevDashboard.Services.Sessions;

/// <summary>Sessionplan-Datei (config/sessions/london-newyork[.local|.example].json).</summary>
public sealed record SessionScheduleFile
{
    public string Name { get; init; } = "London + New York";
    public int CloseOutMinutesBeforeEnd { get; init; } = 5;
    public List<SessionDefinitionFile> Sessions { get; init; } = new();
    public string? Note { get; init; }

    public static (SessionScheduleFile File, string Path) Load(string repoRoot)
    {
        string dir = System.IO.Path.Combine(repoRoot, "config", "sessions");
        foreach (var name in new[] { "london-newyork.local.json", "london-newyork.json", "london-newyork.example.json" })
        {
            string p = System.IO.Path.Combine(dir, name);
            if (!File.Exists(p)) continue;
            var f = JsonSerializer.Deserialize<SessionScheduleFile>(File.ReadAllText(p), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidOperationException("Sessionplan ungültig: " + p);
            if (f.Sessions.Count == 0) throw new InvalidOperationException("Sessionplan ohne Sessions: " + p);
            return (f, p);
        }
        throw new FileNotFoundException("Kein Sessionplan in config/sessions (london-newyork.example.json fehlt).");
    }

    public IReadOnlyList<SessionDefinition> Definitions() => Sessions.Select(s => new SessionDefinition(s.Key, s.Label, s.TimeZone,
        TimeOnly.ParseExact(s.Start, "HH:mm", CultureInfo.InvariantCulture), TimeOnly.ParseExact(s.End, "HH:mm", CultureInfo.InvariantCulture))).ToList();

    public TradingSessionSchedule Schedule(int? closeOutMinutes = null) =>
        new(Definitions(), TimeSpan.FromMinutes(closeOutMinutes ?? CloseOutMinutesBeforeEnd));
}

public sealed record SessionDefinitionFile(string Key, string Label, string TimeZone, string Start, string End);

/// <summary>Eine geplante Session eines Handelstags samt gefundener Aufnahme und Prüfergebnis.
/// Status: ok | missing (keine Aufnahme) | defective (Drops/unsauber/unvollständig) | conflict (mehrere Fassungen, nicht still gewählt).</summary>
public sealed record PlannedSessionDto(
    string Key, string Label, string PlannedStartUtc, string PlannedEndUtc, string CloseOutUtc, string Status, IReadOnlyList<string> Reasons,
    string? Id = null, string? JobId = null, string? Collection = null, string? CollectionsRoot = null, string? RecordingId = null,
    long? Rows = null, long? RecorderDrops = null, bool? SummaryClean = null, long? ManifestTickDrops = null,
    string? Selection = null, string? SelectionNote = null, string? WindowStartUtc = null, string? WindowEndUtc = null,
    string? EffectiveStartUtc = null);

/// <summary>Ein Kalendertag des Plans. Status: complete (alle Sessions ok – läuft) | incomplete (mind. eine Session fehlt oder
/// ist fehlerhaft – Tag wird NICHT als vollständig gewertet und ausgelassen) | no-data (keine Aufnahme; Feiertag und Lücke sind
/// aus den Daten nicht unterscheidbar) | ambiguous (Überlappung ohne eindeutige Zuordnung).</summary>
public sealed record PlannedDayDto(string Day, string Status, IReadOnlyList<string> Reasons, IReadOnlyList<PlannedSessionDto> Sessions,
    long? PlannedPauseMinutes, string? OverlapNote);

public sealed record SessionPlanDto(string From, string To, string Schedule, string SchedulePath, int CloseOutMinutes, IReadOnlyList<string> Roots,
    IReadOnlyList<PlannedDayDto> Days, int CompleteDays, int ExcludedDays, IReadOnlyList<string> Notes);

/// <summary>
/// Plant „London + New York“ über einen Datumsbereich: je Kalendertag (Mo–Fr) die erwarteten UTC-Fenster (sommerzeitrichtig) und
/// die tatsächlich übernommenen Aufnahmen aus BEIDEN Datenordnern über die bestehende Quellenwahl des Tick-Replays (keine CSV wird
/// kopiert oder zusammengeführt). Geplante Pause und fehlende Daten werden unterschieden: Fehlt eine erwartete Session oder ist sie
/// fehlerhaft, ist der Tag nicht vollständig und wird ausgelassen – er rutscht nie als „normale Pause“ durch.
/// </summary>
public sealed class SessionPlanService
{
    public const int MaxCalendarDays = 31;
    private readonly TickReplayService _replay;
    private readonly string _repoRoot;

    public SessionPlanService(TickReplayService replay, string repoRoot)
    {
        _replay = replay;
        _repoRoot = repoRoot;
    }

    public TickReplayService Replay => _replay;
    public (SessionScheduleFile File, string Path) LoadSchedule() => SessionScheduleFile.Load(_repoRoot);

    public SessionPlanDto Plan(DateOnly from, DateOnly to, int? closeOutMinutes = null)
    {
        if (to < from) throw new ArgumentException("Ende liegt vor dem Start.");
        if (to.DayNumber - from.DayNumber + 1 > MaxCalendarDays)
            throw new ArgumentException($"Zeitraum zu lang (max. {MaxCalendarDays} Kalendertage je Lauf).");
        var (file, path) = LoadSchedule();
        var schedule = file.Schedule(closeOutMinutes);
        var jobs = _replay.ResolvedJobs();
        var days = new List<PlannedDayDto>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            var windows = schedule.WindowsFor(d);
            if (windows.Count == 0) continue; // Wochenende: nichts geplant
            days.Add(PlanDay(d, windows, jobs));
        }
        int complete = days.Count(x => x.Status == "complete");
        var notes = new List<string>
        {
            "Fenster je Tag aus den lokalen Börsenzeiten berechnet (Sommerzeit beider Länder); verwendet werden die tatsächlichen UTC-Fenster der Manifeste.",
            "Quellenwahl je Session wie im Chart: bei mehreren Fassungen die drop-freie mit sauberem Abschluss; ungelöste Konflikte werden nicht still entschieden.",
            "Instrument (MNQ), Aufnahme-/Lauf-Identität, Fenster, Chronologie und Aggressor werden beim Laden zeilenweise geprüft; der Kontrakt muss je Session einheitlich sein.",
            "Unvollständige, mehrdeutige oder datenlose Tage werden ausgelassen und nicht als vollständig gewertet."
        };
        return new SessionPlanDto(Iso(from), Iso(to), file.Name, System.IO.Path.GetFileName(path), (int)schedule.CloseOutBeforeEnd.TotalMinutes,
            _replay.Roots, days, complete, days.Count - complete, notes);
    }

    private static PlannedDayDto PlanDay(DateOnly day, IReadOnlyList<PlannedSessionWindow> windows, IReadOnlyList<TickReplayJobInfo> jobs)
    {
        var sessions = new List<PlannedSessionDto>();
        var reasons = new List<string>();
        foreach (var w in windows)
        {
            var found = jobs.Where(j => j.Session == w.Key && j.Day == day).ToList();
            var baseDto = new PlannedSessionDto(w.Key, w.Label, Utc(w.StartUtc), Utc(w.EndUtc), Utc(w.CloseOutUtc), "missing", Array.Empty<string>());
            if (found.Count == 0)
            {
                sessions.Add(baseDto with { Reasons = new[] { $"Keine übernommene {w.Label}-Aufnahme für {Iso(day)} in den Collections." } });
                continue;
            }
            if (found.Any(j => j.Selection == "conflict"))
            {
                var c = found[0];
                sessions.Add(baseDto with
                {
                    Status = "conflict", Reasons = new[] { c.SelectionNote ?? "Mehrere Fassungen – bitte im Chart bewusst wählen." },
                    JobId = c.JobId, Selection = "conflict", SelectionNote = c.SelectionNote
                });
                continue;
            }
            var j = found[0];
            var r = new List<string>();
            if (j.RecorderDrops is null) r.Add("Drop-Status der Rohaufnahme nicht feststellbar (summary.json fehlt).");
            else if (j.RecorderDrops > 0) r.Add($"{j.RecorderDrops} Recorder-Drops – Ticks fehlen.");
            if (j.ManifestTickDrops is > 0) r.Add($"Manifest meldet {j.ManifestTickDrops} Tick-Drops.");
            if (j.SummaryClean != true) r.Add(j.SummaryClean is null ? "Writer-Abschluss (summary_clean) unbekannt." : "Writer nicht sauber abgeschlossen.");
            if (j.StartUtc != w.StartUtc || j.EndUtc != w.EndUtc) r.Add($"Manifest-Fenster {Utc(j.StartUtc)}–{Utc(j.EndUtc)} weicht vom Plan ab.");
            sessions.Add(baseDto with
            {
                Status = r.Count == 0 ? "ok" : "defective", Reasons = r, Id = j.Id, JobId = j.JobId, Collection = j.Collection,
                CollectionsRoot = j.CollectionsRoot, RecordingId = j.RecordingId, Rows = j.Rows, RecorderDrops = j.RecorderDrops,
                SummaryClean = j.SummaryClean, ManifestTickDrops = j.ManifestTickDrops, Selection = j.Selection, SelectionNote = j.SelectionNote,
                WindowStartUtc = Utc(j.StartUtc), WindowEndUtc = Utc(j.EndUtc), EffectiveStartUtc = Utc(j.StartUtc)
            });
        }

        long? pause = null;
        for (int i = 1; i < windows.Count; i++)
        {
            long m = (long)(windows[i].StartUtc - windows[i - 1].EndUtc).TotalMinutes;
            pause = pause is null ? m : Math.Min(pause.Value, m);
        }

        // Überlappung zwischen tatsächlichen Fenstern: gleiche Aufnahme → spätere Session beginnt am Ende der früheren
        // (dieselben Marktereignisse nicht doppelt); verschiedene Aufnahmen → nicht eindeutig zuordenbar.
        string? overlap = null;
        bool ambiguous = false;
        var okSessions = sessions.Where(s => s.Status == "ok").OrderBy(s => s.WindowStartUtc, StringComparer.Ordinal).ToList();
        for (int i = 1; i < okSessions.Count; i++)
        {
            var a = okSessions[i - 1]; var b = okSessions[i];
            if (string.CompareOrdinal(b.WindowStartUtc, a.WindowEndUtc) >= 0) continue;
            if (a.RecordingId == b.RecordingId)
            {
                overlap = $"{a.Label} und {b.Label} überlappen ({b.WindowStartUtc} < {a.WindowEndUtc}); gleiche Aufnahme {a.RecordingId} → {b.Label} beginnt am Ende von {a.Label}, Ticks davor werden nicht doppelt verarbeitet.";
                int idx = sessions.IndexOf(b);
                sessions[idx] = b with { EffectiveStartUtc = a.WindowEndUtc };
            }
            else
            {
                ambiguous = true;
                overlap = $"{a.Label} und {b.Label} überlappen aus verschiedenen Aufnahmen ({a.RecordingId} / {b.RecordingId}) – dieselben Marktereignisse sind nicht eindeutig zuordenbar.";
            }
        }

        string status;
        if (sessions.All(s => s.Status == "missing"))
        {
            status = "no-data";
            reasons.Add("Keine Aufnahme für diesen Tag (Feiertag oder Lücke – aus den Daten nicht unterscheidbar).");
        }
        else if (ambiguous)
        {
            status = "ambiguous";
            reasons.Add(overlap!);
        }
        else if (sessions.Any(s => s.Status != "ok"))
        {
            status = "incomplete";
            foreach (var s in sessions.Where(s => s.Status != "ok"))
                reasons.Add($"{s.Label}: {(s.Status == "missing" ? "fehlt" : s.Status == "conflict" ? "Quellenkonflikt" : "fehlerhaft")} – {string.Join(" ", s.Reasons)}");
            reasons.Add("Tag wird ausgelassen (nicht als vollständig gewertet).");
        }
        else status = "complete";
        return new PlannedDayDto(Iso(day), status, reasons, sessions, pause, overlap);
    }

    internal static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    internal static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
