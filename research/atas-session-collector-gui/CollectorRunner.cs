using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AtasSessionCollector;

public sealed class RunPlan
{
    public required string Instrument { get; init; }
    public required SessionDef Session { get; init; }
    public required string Speed { get; init; }
    public required string OutputRoot { get; init; }
    public required DateOnly From { get; init; }
    public required DateOnly To { get; init; }
    public required List<DayPlan> AllDays { get; init; }   // ganzer Bereich (inkl. uebersprungener)
    public required string ProbeExe { get; init; }
    public required string Python { get; init; }
    public required string CollectorScript { get; init; }
    // Portabel: Rohaufnahme-Ordner (unter dem gewaehlten Datenordner). Wird der Probe per Umgebungsvariable
    // ATAS_RECORDINGS_DIR uebergeben; dieselbe Pfadangabe setzt der Benutzer einmalig als Recorder-OutputRoot.
    public required string RecordingsDir { get; init; }

    public List<DayPlan> RecordDays => AllDays.Where(d => d.WillRecord).OrderBy(d => d.UtcStart).ToList();
    public string CollectionDir => Path.Combine(OutputRoot, SessionCalendar.CollectionFolderName(Instrument, Session, From, To));
}

public enum DayOutcome { Ok, NoData, Reused, ReusedNoData, Failed, Stopped, Skipped }

public sealed record DayResult(
    DateOnly Date, DayOutcome Outcome, long Trades, long Volume, long Buy, long Sell,
    double RuntimeSec, string Detail)
{
    public string OutcomeText => Outcome switch
    {
        DayOutcome.Ok => "OK",
        DayOutcome.NoData => "KEINE DATEN",
        DayOutcome.Reused => "uebernommen",
        DayOutcome.ReusedNoData => "uebernommen (keine Daten)",
        DayOutcome.Failed => "FEHLER",
        DayOutcome.Stopped => "gestoppt",
        DayOutcome.Skipped => "uebersprungen (Kalender)",
        _ => "?",
    };
}

public sealed record RunSummary(
    bool Complete, string StopReason, List<DayResult> Days, string CollectionDir,
    long TotalTrades, long TotalVolume, long CsvBytes);

public sealed class CollectorRunner
{
    public event Action<string>? Log;
    public event Action<string>? Step;                  // menschlich lesbarer aktueller Schritt
    public event Action<DayPlan, int, int>? DayStarted; // (tag, index1, total)
    public event Action<DayResult>? DayFinished;

    private Process? _current;
    private volatile bool _stopRequested;

    /// <summary>Kontrolliert stoppen: wirkt nach dem aktuell laufenden Fenster. KEIN Prozess-Kill.</summary>
    public void RequestStop() => _stopRequested = true;

    /// <summary>Not-Aus: toetet den laufenden Probe-Prozess SAMT Kindprozessen (Python) sofort.
    /// KEIN sauberes Finalisieren. ATAS (OFT.Platform) ist NICHT Teil dieses Prozessbaums und bleibt unberuehrt.</summary>
    public void KillCurrentTree()
    {
        _stopRequested = true;
        var p = _current;
        if (p == null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
    }

    public async Task<RunSummary> RunAsync(RunPlan plan, CancellationToken ct)
    {
        _stopRequested = false;
        Directory.CreateDirectory(plan.CollectionDir);
        string combined = Path.Combine(plan.CollectionDir, "run_protocol.txt");
        AppendCombined(combined, $"\n================ GUI-SAMMELLAUF  {DateTime.Now:yyyy-MM-dd HH:mm:ss} ================\n" +
            $"Instrument={plan.Instrument}  Session={plan.Session.Name} ({plan.Session.TzIana})  Speed={plan.Speed}\n" +
            $"Angeforderter Bereich (Soll): {plan.From:yyyy-MM-dd} bis {plan.To:yyyy-MM-dd} (inklusive)\n" +
            $"Aufnahmefenster (Session-Lokalzeit): {plan.Session.LocalStart:hh\\:mm}–{plan.Session.LocalEnd:hh\\:mm} {plan.Session.TzIana}\n" +
            $"Collection: {plan.CollectionDir}\n");

        long csvBefore = CsvBytes(plan);
        var recordDays = plan.RecordDays;
        var results = new List<DayResult>();
        string stopReason = "";

        for (int i = 0; i < recordDays.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var day = recordDays[i];
            if (_stopRequested)
            {
                stopReason = "Kontrolliert gestoppt (vor dem naechsten Fenster). Bereits abgeschlossene Tage bleiben erhalten.";
                results.Add(new DayResult(day.Date, DayOutcome.Stopped, 0, 0, 0, 0, 0, "nicht gestartet (Stopp)"));
                for (int k = i + 1; k < recordDays.Count; k++)
                    results.Add(new DayResult(recordDays[k].Date, DayOutcome.Stopped, 0, 0, 0, 0, 0, "nicht gestartet (Stopp)"));
                break;
            }

            DayStarted?.Invoke(day, i + 1, recordDays.Count);
            Step?.Invoke($"Tag {day.Date:yyyy-MM-dd}: Vorbereitung");
            var sw = Stopwatch.StartNew();

            string cfgPath = Path.Combine(Path.GetTempPath(), $"atas_sc_{Guid.NewGuid():N}.json");
            string protoPath = Path.Combine(Path.GetTempPath(), $"atas_sc_{Guid.NewGuid():N}.txt");
            WriteWindowConfig(cfgPath, plan, day);

            var captured = new StringBuilder();
            string appendJson = "";
            bool sawSkip = false;
            int rc;
            try
            {
                rc = await RunProbeAsync(plan.ProbeExe, cfgPath, protoPath, plan.RecordingsDir, line =>
                {
                    captured.AppendLine(line);
                    Log?.Invoke(line);
                    var friendly = MapStep(line, day.Date);
                    if (friendly != null) Step?.Invoke(friendly);
                    if (line.Contains("[SKIP]") && line.Contains("bereits committed")) sawSkip = true;
                    int idx = line.IndexOf("[collect] append", StringComparison.Ordinal);
                    if (idx >= 0)
                    {
                        int brace = line.IndexOf('{', idx);
                        if (brace >= 0) appendJson = line[brace..];
                    }
                }, ct);
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                throw;
            }
            finally
            {
                TryDelete(cfgPath);
            }

            AppendCombined(combined,
                $"\n----- Fenster {i + 1}/{recordDays.Count}  {day.Date:yyyy-MM-dd}  " +
                $"UTC [{day.UtcStart:yyyy-MM-ddTHH:mm:ssZ},{day.UtcEnd:HH:mm:ssZ})  Berlin [{day.BerlinStartString},{day.BerlinEndString}) -----\n" +
                captured.ToString());
            TryDelete(protoPath);
            sw.Stop();

            var res = Classify(day, rc, sawSkip, appendJson, sw.Elapsed.TotalSeconds);
            results.Add(res);
            DayFinished?.Invoke(res);

            if (res.Outcome is DayOutcome.Failed)
            {
                stopReason = $"Aufnahmefehler am {day.Date:yyyy-MM-dd} ({res.Detail}). Sicher gestoppt; abgeschlossene Tage bleiben erhalten.";
                for (int k = i + 1; k < recordDays.Count; k++)
                    results.Add(new DayResult(recordDays[k].Date, DayOutcome.Stopped, 0, 0, 0, 0, 0, "nicht gestartet (vorheriger Fehler)"));
                break;
            }
            if (res.Outcome is DayOutcome.NoData)
            {
                stopReason = $"Keine Replay-Daten am {day.Date:yyyy-MM-dd} (0 Trades) – nicht als Erfolg gewertet. Sicher gestoppt.";
                for (int k = i + 1; k < recordDays.Count; k++)
                    results.Add(new DayResult(recordDays[k].Date, DayOutcome.Stopped, 0, 0, 0, 0, 0, "nicht gestartet (keine Daten zuvor)"));
                break;
            }
        }

        // Kalender-uebersprungene Tage (Wochenende/Feiertag) ins Ergebnis aufnehmen (Transparenz).
        foreach (var d in plan.AllDays.Where(d => !d.WillRecord))
            results.Add(new DayResult(d.Date, DayOutcome.Skipped, 0, 0, 0, 0, 0, d.Reason ?? "kein Handelstag"));

        // Endgueltige Zahlen aus dem Manifest (deckt auch uebernommene Tage ab).
        EnrichFromManifest(plan, results);

        long csvAfter = CsvBytes(plan);
        var ordered = results.OrderBy(r => r.Date).ToList();
        bool complete = stopReason == "" &&
                        recordDays.All(rd => ordered.Any(r => r.Date == rd.Date &&
                            r.Outcome is DayOutcome.Ok or DayOutcome.Reused));
        long totTrades = ordered.Where(r => r.Outcome is DayOutcome.Ok or DayOutcome.Reused or DayOutcome.NoData or DayOutcome.ReusedNoData).Sum(r => r.Trades);
        long totVol = ordered.Where(r => r.Outcome is DayOutcome.Ok or DayOutcome.Reused or DayOutcome.NoData or DayOutcome.ReusedNoData).Sum(r => r.Volume);

        WriteCoverageReport(plan, ordered, complete, stopReason, totTrades, totVol, csvAfter);
        AppendCombined(combined, $"\n[Ergebnis] {(complete ? "VOLLSTAENDIG" : "UNVOLLSTAENDIG")}  Trades={totTrades}  Volumen={totVol}  CSV={csvAfter} B" +
            (stopReason == "" ? "" : $"\n[Grund] {stopReason}") +
            (csvBefore == csvAfter && recordDays.Count > 0 && ordered.All(r => r.Outcome != DayOutcome.Ok) ? "\n[Hinweis] CSV unveraendert (alle Tage uebernommen/uebersprungen)." : "") + "\n");

        return new RunSummary(complete, stopReason, ordered, plan.CollectionDir, totTrades, totVol, csvAfter);
    }

    // ---------- Fenster-Konfig fuer die bestehende Probe (Berlin-Fenster) ----------
    private static void WriteWindowConfig(string path, RunPlan plan, DayPlan day)
    {
        var cfg = new
        {
            _hinweis = "Von der ATAS-Session-Collector-Oberflaeche erzeugt. Ein Fenster je Aufruf; Resume/Dedup via Collector-Manifest.",
            session = plan.Session.Name,
            year = day.Date.Year,
            timezone = plan.Session.TzId,   // Provenance: Session-Lokalzeit (UTC bleibt autoritativ)
            speed = plan.Speed,
            collectionDir = plan.CollectionDir,
            python = plan.Python,
            collectorScript = plan.CollectorScript,
            keepRecorderAttached = false,
            windows = new[] { new { startBerlin = day.BerlinStartString, endBerlin = day.BerlinEndString } },
        };
        File.WriteAllText(path, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
    }

    // ---------- Probe als Kindprozess; Zeilen live melden; kein Prozess-Kill bei Stopp ----------
    private async Task<int> RunProbeAsync(string probeExe, string cfgPath, string protoPath, string recordingsDir, Action<string> onLine, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = probeExe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Portabel: Rohaufnahme-Ordner unter dem gewaehlten Datenordner an die Probe uebergeben (kein festes F:).
        psi.Environment["ATAS_RECORDINGS_DIR"] = recordingsDir;
        psi.ArgumentList.Add("collect");
        psi.ArgumentList.Add(cfgPath);
        psi.ArgumentList.Add(protoPath);

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine("stderr: " + e.Data); };
        _current = p;
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync(ct);
        _current = null;
        return p.ExitCode;
    }

    private static DayResult Classify(DayPlan day, int rc, bool sawSkip, string appendJson, double runtimeSec)
    {
        if (sawSkip)
            return new DayResult(day.Date, DayOutcome.Reused, 0, 0, 0, 0, runtimeSec, "bereits committed – wiederverwendet (kein Doppel-Anhaengen)");

        string result = "";
        long rows = 0, vol = 0, buy = 0, sell = 0;
        if (appendJson != "")
        {
            try
            {
                using var doc = JsonDocument.Parse(appendJson);
                var r = doc.RootElement;
                result = r.TryGetProperty("result", out var vr) ? (vr.GetString() ?? "") : "";
                if (r.TryGetProperty("trades", out var t) && t.ValueKind == JsonValueKind.Object)
                {
                    rows = t.TryGetProperty("rows", out var vrr) ? vrr.GetInt64() : 0;
                    vol = t.TryGetProperty("totalVolume", out var vv) ? vv.GetInt64() : 0;
                    buy = t.TryGetProperty("buyVolume", out var vb) ? vb.GetInt64() : 0;
                    sell = t.TryGetProperty("sellVolume", out var vs) ? vs.GetInt64() : 0;
                }
            }
            catch { /* unparsbar -> als Fehler behandeln */ }
        }

        if (rc != 0)
            return new DayResult(day.Date, DayOutcome.Failed, rows, vol, buy, sell, runtimeSec, $"Probe rc={rc}, result='{result}'");
        if (result == "committed" && rows > 0)
            return new DayResult(day.Date, DayOutcome.Ok, rows, vol, buy, sell, runtimeSec, "committed");
        if (result is "empty" || (result == "committed" && rows == 0))
            return new DayResult(day.Date, DayOutcome.NoData, 0, 0, 0, 0, runtimeSec, "0 Trades (keine Replay-Daten im Fenster)");
        // committed/skipped ohne erkennbare Zahlen, aber rc==0:
        return new DayResult(day.Date, DayOutcome.Ok, rows, vol, buy, sell, runtimeSec, result == "" ? "abgeschlossen" : result);
    }

    // Manifest lesen und Trades/Volumen je Tag ergaenzen (vor allem fuer uebernommene Tage).
    private static void EnrichFromManifest(RunPlan plan, List<DayResult> results)
    {
        string mpath = Path.Combine(plan.CollectionDir, "collection_manifest.json");
        if (!File.Exists(mpath)) return;
        Dictionary<DateOnly, (long rows, long vol, long buy, long sell)> byDate;
        try
        {
            byDate = new();
            using var doc = JsonDocument.Parse(File.ReadAllText(mpath));
            if (!doc.RootElement.TryGetProperty("jobs", out var jobs) || jobs.ValueKind != JsonValueKind.Object) return;
            foreach (var jp in jobs.EnumerateObject())
            {
                // job_id = Session_Jahr_<startUtc>_<endUtc>; Datum aus startUtc ableiten.
                var parts = jp.Name.Split('_');
                var su = parts.FirstOrDefault(x => x.EndsWith("Z") && x.Length == 16);
                if (su == null || !DateTime.TryParseExact(su, "yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var sdt))
                    continue;
                var dt = DateOnly.FromDateTime(sdt);
                long rows = 0, vol = 0, buy = 0, sell = 0;
                if (jp.Value.TryGetProperty("trades", out var t) && t.ValueKind == JsonValueKind.Object)
                {
                    rows = t.TryGetProperty("rows", out var a) ? a.GetInt64() : 0;
                    vol = t.TryGetProperty("totalVolume", out var b) ? b.GetInt64() : 0;
                    buy = t.TryGetProperty("buyVolume", out var c) ? c.GetInt64() : 0;
                    sell = t.TryGetProperty("sellVolume", out var e) ? e.GetInt64() : 0;
                }
                byDate[dt] = (rows, vol, buy, sell);
            }
        }
        catch { return; }

        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            if ((r.Trades == 0 && r.Volume == 0) && byDate.TryGetValue(r.Date, out var s) && (s.rows > 0 || s.vol > 0))
            {
                var outc = r.Outcome == DayOutcome.Reused && s.rows == 0 ? DayOutcome.ReusedNoData : r.Outcome;
                results[i] = r with { Trades = s.rows, Volume = s.vol, Buy = s.buy, Sell = s.sell, Outcome = outc };
            }
        }
    }

    private static long CsvBytes(RunPlan plan)
    {
        var f = Path.Combine(plan.CollectionDir, "trades.csv");
        return File.Exists(f) ? new FileInfo(f).Length : 0;
    }

    private static void WriteCoverageReport(RunPlan plan, List<DayResult> days, bool complete, string stopReason, long totTrades, long totVol, long csvBytes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ATAS-Session-Collector – Abdeckungsbericht");
        sb.AppendLine($"Erstellt: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine("== Angefordert (Soll) ==");
        sb.AppendLine($"Instrument : {plan.Instrument}");
        sb.AppendLine($"Session    : {plan.Session.Name} ({plan.Session.DisplayName}) / {plan.Session.TzIana}");
        sb.AppendLine($"Fenster    : {plan.Session.LocalStart:hh\\:mm}–{plan.Session.LocalEnd:hh\\:mm} Session-Lokalzeit");
        sb.AppendLine($"Bereich    : {plan.From:yyyy-MM-dd} bis {plan.To:yyyy-MM-dd} (inklusive)");
        sb.AppendLine($"Geschw.    : {plan.Speed}");
        int planned = plan.RecordDays.Count, skipped = plan.AllDays.Count - planned;
        sb.AppendLine($"Geplante Handelstage: {planned}   Kalendarisch uebersprungen: {skipped}");
        sb.AppendLine();
        sb.AppendLine("== Tatsaechliche Abdeckung (Ist) ==");
        sb.AppendLine($"Gesamtstatus: {(complete ? "VOLLSTAENDIG" : "UNVOLLSTAENDIG")}");
        if (stopReason != "") sb.AppendLine($"Grund       : {stopReason}");
        sb.AppendLine($"Summe Trades: {totTrades}   Summe Volumen: {totVol}   trades.csv: {csvBytes} B");
        sb.AppendLine();
        sb.AppendLine("Datum       | Ergebnis                 | Trades    | Volumen   | Laufzeit | Detail");
        sb.AppendLine("------------|--------------------------|-----------|-----------|----------|------------------------------");
        foreach (var r in days)
            sb.AppendLine($"{r.Date:yyyy-MM-dd} | {r.OutcomeText,-24} | {r.Trades,9} | {r.Volume,9} | {FormatDur(r.RuntimeSec),8} | {r.Detail}");
        File.WriteAllText(Path.Combine(plan.CollectionDir, "coverage_report.txt"), sb.ToString());
    }

    public static string FormatDur(double sec) => sec <= 0 ? "-" : TimeSpan.FromSeconds(sec).ToString(@"mm\:ss");

    private static string? MapStep(string line, DateOnly date)
    {
        string p = $"Tag {date:yyyy-MM-dd}: ";
        if (line.Contains("[2]") && line.Contains("editierbar")) return p + "Chart bereit";
        if (line.Contains("[2]")) return p + "Chart vorbereiten";
        if (line.Contains("[3]") && line.Contains("hinzufuegen")) return p + "Recorder hinzufuegen";
        if (line.Contains("[3]")) return p + "Recorder pruefen";
        if (line.Contains("[4]")) return p + "Replay oeffnen";
        if (line.Contains("[5]")) return p + "Datum/Modul waehlen";
        if (line.Contains("[6]")) return p + "Zeitraum zuruecklesen";
        if (line.Contains("[7]") && line.Contains("Laden")) return p + "Zeitraum laden";
        if (line.Contains("[7]")) return p + "Instanz zugeordnet";
        if (line.Contains("[8]") && line.Contains("Speed")) return p + "Geschwindigkeit setzen/pruefen";
        if (line.Contains("[8]") && line.Contains("Play")) return p + "Wiedergabe gestartet";
        if (line.Contains("[9]") && line.Contains("Endstatus")) return p + "Endsignal pruefen";
        if (line.Contains("[9]")) return p + "Wiedergabe laeuft";
        if (line.Contains("[10]")) return p + "Replay verlassen";
        if (line.Contains("[11]")) return p + "Recorder entfernen / Writer finalisieren";
        if (line.Contains("[collect] append")) return p + "Daten uebernehmen";
        if (line.Contains("[SKIP]")) return p + "bereits vorhanden – wird uebersprungen";
        return null;
    }

    private static void AppendCombined(string path, string text)
    {
        try { File.AppendAllText(path, text); } catch { }
    }

    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
