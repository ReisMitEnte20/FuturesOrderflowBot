using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AtasSessionCollector;

/// <summary>
/// "Speicher / Archivierung": ruft die mitgelieferte Bridge bridge\atas_archive.py (private Python-Laufzeit) auf.
/// Die eigentliche Arbeit (verlustfreie zstd-Archivierung mit Pruefung, Wiederherstellung) liegt in Python; die
/// Oberflaeche zeigt Vorschau, Fortschritt und Ergebnis und bietet einen kontrollierten Abbruch (Stopp-Datei, kein Kill).
/// Laeuft NIE parallel zu einem Sammellauf (Oberflaeche sperrt; atas_archive.py prueft zusaetzlich auf AtasReplayProbe).
/// Schreibt NIE in eine Collection: ein Archivierungsfehler aendert den Status eines importierten Tages nicht.
/// </summary>
public sealed class ArchiveRunner
{
    public event Action<JsonElement>? Event;      // jede JSON-Zeile (block/file/recording/result)
    public event Action<string>? Log;             // nicht-JSON-Ausgabe (Fehlertexte)

    private readonly string _python;
    private readonly string _script;
    private readonly string _stopFile;
    private Process? _current;

    public ArchiveRunner(ToolPaths tools, string dataDir)
    {
        _python = tools.Python;
        _script = tools.ArchiveScript;
        _stopFile = Path.Combine(AppPaths.Logs(dataDir), "archive.stop");
    }

    public bool ScriptPresent => File.Exists(_script);

    /// <summary>Kontrollierter Abbruch: die laufende Datei wird verworfen (Original bleibt), danach endet der Lauf.</summary>
    public void RequestStop()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(_stopFile)!); File.WriteAllText(_stopFile, DateTime.Now.ToString("o")); } catch { }
    }

    public Task<(int code, JsonElement? result)> AnalyzeAsync(string dataDir) =>
        RunAsync(new[] { "analyze", "--data-dir", dataDir });

    public Task<(int code, JsonElement? result)> CompressAsync(string dataDir, IReadOnlyCollection<string>? only = null)
    {
        var a = new List<string> { "compress", "--data-dir", dataDir, "--level", "3", "--stop-file", _stopFile };
        if (only != null) { a.Add("--only"); a.AddRange(only); }
        return RunAsync(a);
    }

    public Task<(int code, JsonElement? result)> RestoreAsync(string recordingDir) =>
        RunAsync(new[] { "restore", "--recording", recordingDir, "--stop-file", _stopFile });

    private async Task<(int, JsonElement?)> RunAsync(IEnumerable<string> args)
    {
        try { if (File.Exists(_stopFile)) File.Delete(_stopFile); } catch { }
        var psi = new ProcessStartInfo(_python)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-I");
        psi.ArgumentList.Add(_script);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        JsonElement? result = null;
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            try
            {
                using var doc = JsonDocument.Parse(e.Data);
                var el = doc.RootElement.Clone();
                if (el.TryGetProperty("event", out var ev) && ev.GetString() == "result") result = el;
                Event?.Invoke(el);
            }
            catch { Log?.Invoke(e.Data); }
        };
        p.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log?.Invoke(e.Data); };
        p.Start();
        _current = p;
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        await p.WaitForExitAsync();
        _current = null;
        try { if (File.Exists(_stopFile)) File.Delete(_stopFile); } catch { }
        return (p.ExitCode, result);
    }

    public void KillCurrentTree()
    {
        try { var p = _current; if (p != null && !p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }

    // ------------------------------------------------------------------ reine Helfer (Selbsttest)

    public static string Gb(long bytes) => (bytes / 1e9).ToString("0.00", CultureInfo.GetCultureInfo("de-DE")) + " GB";

    /// <summary>Lesbare Zusammenfassung eines 'analyze'-Ergebnisses (Vorschau).</summary>
    public static string DescribeAnalysis(JsonElement r, bool withSkippedList = true)
    {
        var sb = new StringBuilder();
        long rec = r.GetProperty("recordingsBytes").GetInt64();
        var disk = r.GetProperty("disk");
        sb.AppendLine($"Aufnahmen belegen {Gb(rec)}. Laufwerk: {Gb(disk.GetProperty("used").GetInt64())} belegt, {Gb(disk.GetProperty("free").GetInt64())} frei.");
        foreach (var k in r.GetProperty("byKind").EnumerateObject().OrderByDescending(k => k.Value.GetProperty("rawBytes").GetInt64() + k.Value.GetProperty("archivedBytes").GetInt64()))
        {
            long raw = k.Value.GetProperty("rawBytes").GetInt64(), arc = k.Value.GetProperty("archivedBytes").GetInt64();
            if (raw + arc < 1_000_000) continue;
            sb.AppendLine($"  {k.Name}: roh {Gb(raw)}, archiviert {Gb(arc)}");
        }
        var el = r.GetProperty("eligible").EnumerateArray().ToList();
        long elBytes = el.Sum(e => e.TryGetProperty("rawBytes", out var rb) ? rb.GetInt64() : e.GetProperty("bytes").GetInt64());
        sb.AppendLine($"Geeignet zum Komprimieren: {el.Count} Aufnahme(n), {Gb(elBytes)} Rohdaten.");
        sb.AppendLine($"Bereits archiviert: {r.GetProperty("archived").GetArrayLength()} Aufnahme(n).");
        var unclear = r.GetProperty("unclear").EnumerateArray().ToList();
        if (unclear.Count > 0)
            sb.AppendLine($"Unklarer Zustand (Original UND Archiv vorhanden, wird nur nach voller Pruefung bereinigt): {string.Join(", ", unclear.Select(u => u.GetProperty("recording").GetString()))}");
        var sk = r.GetProperty("skipped").EnumerateArray().ToList();
        sb.AppendLine($"Uebersprungen: {sk.Count} Aufnahme(n), {Gb(sk.Sum(s => s.GetProperty("bytes").GetInt64()))}:");
        foreach (var g in sk.GroupBy(s => ReasonGroup(s.GetProperty("reason").GetString() ?? "")).OrderByDescending(g => g.Count()))
            sb.AppendLine($"  {g.Count(),4} x {g.Key}  ({Gb(g.Sum(s => s.GetProperty("bytes").GetInt64()))})");
        if (withSkippedList)
            foreach (var s in sk.Where(s => s.GetProperty("bytes").GetInt64() >= 50_000_000))
                sb.AppendLine($"     {s.GetProperty("recording").GetString()}  {s.GetProperty("date").GetString()}  {Gb(s.GetProperty("bytes").GetInt64())}  - {s.GetProperty("reason").GetString()}");
        return sb.ToString();
    }

    private static string ReasonGroup(string reason) =>
        reason.StartsWith("Recorder-Drops", StringComparison.Ordinal) ? "Recorder-Drops gemeldet" :
        reason.StartsWith("Auftrag ", StringComparison.Ordinal) && reason.Contains("coverageStatus") ? "Auftrag nicht 'complete'" : reason;

    /// <summary>Kurzbericht eines 'compress'-Ergebnisses.</summary>
    public static string DescribeCompress(JsonElement r)
    {
        var sb = new StringBuilder();
        string status = r.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "";
        var recs = r.TryGetProperty("recordings", out var rr) ? rr.EnumerateArray().ToList() : new List<JsonElement>();
        int ok = 0; long orig = 0, zst = 0; var problems = new List<string>();
        foreach (var rec in recs)
        {
            bool allOk = true;
            foreach (var f in rec.GetProperty("files").EnumerateArray())
            {
                string res = f.GetProperty("result").GetString() ?? "";
                if (res == "archived")
                {
                    orig += f.TryGetProperty("origSize", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt64() : 0;
                    zst += f.TryGetProperty("zstSize", out var z) && z.ValueKind == JsonValueKind.Number ? z.GetInt64() : 0;
                }
                else if (res is not ("already_archived" or "kept_not_smaller"))
                {
                    allOk = false;
                    problems.Add($"{rec.GetProperty("recording").GetString()} {f.GetProperty("file").GetString()}: {res}" +
                                 (f.TryGetProperty("reason", out var why) ? " - " + why.GetString() : "") + " (Original bleibt erhalten)");
                }
            }
            if (allOk && !rec.TryGetProperty("error", out _)) ok++;
        }
        long freed = r.TryGetProperty("freedBytes", out var fb) ? fb.GetInt64() : 0;
        sb.AppendLine(status switch
        {
            "completed" => "Archivierung abgeschlossen.",
            "stopped" => "Archivierung kontrolliert abgebrochen. Erneut 'Vorhandene Aufnahmen komprimieren' setzt fort.",
            "out_of_space" => "Archivierung wegen Platzmangel beendet. Originale sind erhalten.",
            "refused" => "Archivierung abgelehnt: " + (r.TryGetProperty("error", out var er) ? er.GetString() : ""),
            _ => "Archivierung beendet: " + status,
        });
        sb.AppendLine($"Aufnahmen vollstaendig archiviert: {ok} von {recs.Count}.  Rohdaten {Gb(orig)} -> Archive {Gb(zst)}.");
        sb.AppendLine($"Tatsaechlich frei geworden (Laufwerk, gemessen): {Gb(freed)}.");
        foreach (var p in problems) sb.AppendLine("  " + p);
        return sb.ToString();
    }

    /// <summary>Recording-IDs der Auftraege, die in diesem Sammellauf (seit sinceUtc) committed wurden ("vorgemerkt").</summary>
    public static List<string> RecordingsCommittedSince(string manifestJson, DateTime sinceUtc)
    {
        var ids = new List<string>();
        using var doc = JsonDocument.Parse(manifestJson);
        if (!doc.RootElement.TryGetProperty("jobs", out var jobs)) return ids;
        foreach (var j in jobs.EnumerateObject())
        {
            var v = j.Value;
            if (!v.TryGetProperty("status", out var s) || s.GetString() != "committed") continue;
            if (!v.TryGetProperty("appendedAtUtc", out var at) || !DateTime.TryParse(at.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t) || t < sinceUtc) continue;
            if (v.TryGetProperty("recordingId", out var rid) && rid.GetString() is { Length: > 0 } id && !ids.Contains(id)) ids.Add(id);
        }
        return ids;
    }
}

/// <summary>Speicherbare Optionen der Oberflaeche. Eigene Datei, weil das Setup collector-data.json bei jedem
/// Update neu schreibt. Portabel neben der EXE, sonst %APPDATA%.</summary>
public sealed class CollectorOptions
{
    public bool autoArchiveAfterRun { get; set; }

    private static string Portable => Path.Combine(AppContext.BaseDirectory, "collector-options.json");
    private static string Roaming => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppPaths.AppFolderName, "collector-options.json");

    public static CollectorOptions Load()
    {
        foreach (var p in new[] { Portable, Roaming })
            try { if (File.Exists(p)) return JsonSerializer.Deserialize<CollectorOptions>(File.ReadAllText(p)) ?? new(); } catch { }
        return new CollectorOptions();
    }

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        try { File.WriteAllText(Portable, json); return; } catch { }
        Directory.CreateDirectory(Path.GetDirectoryName(Roaming)!);
        File.WriteAllText(Roaming, json);
    }
}
