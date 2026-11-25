using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using OrderFlowTools;

namespace AtasReplayProbe;

// Gebuendelte, rein lesende Diagnose fuer einen Rechner ohne Fernzugriff ('diagpack'). Klickt nur, um den
// Indikator-Dialog zu OEFFNEN und ohne Aenderung wieder zu schliessen (Abbrechen) - und nur, wenn der Chart nicht in der
// Replay-Ansicht ist. Keine Aufnahme, kein Replay, keine Konto-/Feed-Aktion. Zugangs-/Kontodaten werden maskiert;
// Konfigurationsdateien von ATAS werden NICHT kopiert.
internal static partial class Program
{
    internal static string ProgramVersion() =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(Program).Assembly.GetName().Version?.ToString() ?? "?";

    // Konto-/Zugangsmerkmale in Logzeilen unkenntlich machen.
    internal static string MaskSensitive(string line)
    {
        line = Regex.Replace(line, @"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", "<email>");
        line = Regex.Replace(line, @"ic\[[^\]]*\]", "ic[<konto>]");
        line = Regex.Replace(line, @"Rithmic\[[^\]]*\]", "Rithmic[<konto>]");
        line = Regex.Replace(line, @"(\[[A-Z0-9]+@[A-Z]+,\s*)[^\]]+\]", "$1<konto>]");
        line = Regex.Replace(line, @"\b[A-Z0-9]{4,}-[A-Z0-9]{6,}-[A-Z0-9]{3,}\b", "<konto>");
        line = Regex.Replace(line, @"(?i)(password|passwort|token|login)\s*[:=]\s*\S+", "$1=<entfernt>");
        return line;
    }

    // Verbliebene, noch schreibende Recorder-Instanzen benennen (Ordner, runId, Start, Kennung) - zugeordnete vs. andere.
    internal static void LockedInstancesReport(Action<string> W, string? attributedFolder)
    {
        if (!Directory.Exists(RecordingsDir)) return;
        var locked = Directory.GetDirectories(RecordingsDir).Where(d => IsMetaLocked(Path.Combine(d, "meta.json"))).ToList();
        if (locked.Count == 0) { W("  aktive (gesperrte) Recorder-Instanzen: keine"); return; }
        foreach (var d in locked)
        {
            var meta = Path.Combine(d, "meta.json");
            string f = Path.GetFileName(d);
            W($"  AKTIVE Instanz: {f} runId={ReadMetaField(meta, "runId")} start={ReadMetaField(meta, "startWallUtc")} recorder='{ReadMetaField(meta, "recorder")}' " +
              $"{(f == attributedFolder ? "(= zugeordnete Aufnahme, Writer noch offen)" : "(NICHT die zugeordnete Aufnahme; wird nicht blind entfernt)")}");
        }
    }

    private static int DiagPack(string? target)
    {
        string root = target ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"ATAS-Diagnose-{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(root);
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }
        W($"# ATAS-Diagnosepaket (nur lesen)  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  Probe {ProgramVersion()}");
        W($"# Ziel: {root}");
        W($"# Aufnahmeordner: {RecordingsDir}");
        W($"# {RecorderIdentity()}");
        W($"# gemeinsame Konfiguration: {SharedDataRoot.ConfigPath} -> '{SharedDataRoot.Read() ?? "(nicht gesetzt)"}'");
        var zone = DialogZone.Resolve();
        W($"# Datumsdialog-Zeitzone (Annahme): {zone.Source}; Beispiel 2026-10-26 13:30Z -> {zone.FromUtc(new DateTime(2026, 10, 26, 13, 30, 0, DateTimeKind.Utc)):dd.MM.yyyy HH:mm:ss}");

        // 1) Chart, Prozesse, Fenster, DPI, Render-Engine
        var cd = new StringBuilder();
        try { var scan = ScanChartCandidates(); var v = SelectChart(scan.Candidates); cd.AppendLine($"ERGEBNIS: {v.Kind} ({v.Reason})"); WriteChartDiagnosis(s => cd.AppendLine(s), scan); }
        catch (Exception ex) { cd.AppendLine("Chartdiagnose fehlgeschlagen: " + ex.Message); }
        File.WriteAllText(Path.Combine(root, "1_chart_prozesse_fenster.txt"), cd.ToString(), new UTF8Encoding(true));
        W("[1] Chart/Prozesse/Fenster/DPI geschrieben.");

        // 2) Fenstergeometrie + Bildschirmfoto
        try
        {
            var g = new StringBuilder();
            foreach (var r in AtasRoots())
            {
                string dpi = "?"; try { dpi = GetDpiForWindow(new IntPtr(r.Current.NativeWindowHandle)).ToString(); } catch { }
                g.AppendLine($"'{Safe(() => r.Current.Name)}' rect={Rect(r)} dpi={dpi}");
            }
            File.WriteAllText(Path.Combine(root, "2_fenster_geometrie.txt"), g.ToString(), new UTF8Encoding(true));
            using var bmp = ScreenOcr.CapturePrimary(out _, out _);
            bmp.Save(Path.Combine(root, "2_bildschirm.png"), System.Drawing.Imaging.ImageFormat.Png);
            W("[2] Fenstergeometrie + Bildschirmfoto geschrieben.");
        }
        catch (Exception ex) { W("[2] Bildschirmfoto nicht moeglich: " + ex.Message); }

        // 3) ATAS-Protokoll: relevante Zeilen (Replay, Chart, Cache, Fehler), maskiert
        try
        {
            var dir = Path.GetDirectoryName(AtasLogPath())!;
            var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "app_*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(2).OrderBy(File.GetLastWriteTimeUtc).ToList() : new List<string>();
            var o = new StringBuilder();
            foreach (var f in files)
            {
                o.AppendLine($"===== {Path.GetFileName(f)}");
                foreach (var line in TailLines(f, 8 << 20))
                {
                    var l = AtasLogLine.Parse(line);
                    bool keep = l == null ? false
                        : l.IsReplaySource || l.Source.Contains("Chart", StringComparison.OrdinalIgnoreCase) || l.Source.StartsWith("Cache", StringComparison.Ordinal)
                          || l.Source.Contains("Render", StringComparison.OrdinalIgnoreCase) || l.Level is "ERROR" or "FATAL" or "WARN" && !l.Source.StartsWith("Latency", StringComparison.Ordinal)
                          || l.Message.Contains("Disconnect", StringComparison.OrdinalIgnoreCase) || l.Message.Contains("Indicator", StringComparison.OrdinalIgnoreCase);
                    if (keep) o.AppendLine(MaskSensitive(line.TrimEnd()));
                }
            }
            File.WriteAllText(Path.Combine(root, "3_atas_protokoll_auszug.txt"), o.ToString(), new UTF8Encoding(true));
            W($"[3] ATAS-Protokollauszug ({files.Count} Datei(en), maskiert) geschrieben.");
        }
        catch (Exception ex) { W("[3] ATAS-Protokoll nicht lesbar: " + ex.Message); }

        // 4) Indikator-Dialog: UIA-Baum + Listenzustand + Bilder; nur ausserhalb der Replay-Ansicht; ohne Aenderung schliessen
        try
        {
            if (Process.GetProcessesByName("OFT.Platform").Length == 0) W("[4] ATAS laeuft nicht - Dialog uebersprungen.");
            else if (!IndicatorsEnabled() || ReplayOpen()) W("[4] Chart ist in der Replay-Ansicht oder 'Indikatoren' inaktiv - Dialog bewusst NICHT geoeffnet (kein Zustandswechsel).");
            else
            {
                var d = new StringBuilder();
                void D(string s) => d.AppendLine(s);
                bool wasOpen = IndicatorDialog() != null;
                if (OpenIndicatorDialog(D) && IndicatorDialog() is { } dlg)
                {
                    D($"Dialog rect={Rect(dlg)} dpi={SafeDpi(dlg)} Kopfzahl 'Hinzugefuegt'={HeaderCount(dlg)}");
                    var (rows, header, state, src) = ReadAddedRows(dlg, D, ocr: true);
                    D($"Liste: Kopfzahl={header} gelesen={rows.Count} Quelle={src} Zustand={state}");
                    foreach (var r in rows) D($"  Zeile #{r.Index} recorder={r.IsRecorder} uia='{r.UiaText}' ocr='{r.OcrText}' rect={(int)r.Rect.X},{(int)r.Rect.Y},{(int)r.Rect.Width}x{(int)r.Rect.Height}");
                    D("--- UIA-Baum des Dialogs ---");
                    int n = 0; DumpDialog(dlg, 0, D, ref n);
                    var dr = dlg.Current.BoundingRectangle;
                    Shot((int)dr.X, (int)dr.Y, (int)dr.Width, (int)dr.Height, Path.Combine(root, "4_indikator_dialog.png"));
                    if (AddedList(dlg) is { } lst) { var lr = lst.Current.BoundingRectangle; if (!lr.IsEmpty) Shot((int)lr.X, (int)lr.Y, (int)lr.Width, (int)lr.Height, Path.Combine(root, "4_liste_hinzugefuegt.png")); }
                    if (!wasOpen) CancelDialog(dlg, D);
                    W("[4] Indikator-Dialog (UIA-Baum, Liste, Bilder) geschrieben; Dialog ohne Aenderung geschlossen.");
                }
                else W("[4] Indikator-Dialog nicht zu oeffnen.");
                File.WriteAllText(Path.Combine(root, "4_indikator_dialog.txt"), d.ToString(), new UTF8Encoding(true));
            }
        }
        catch (Exception ex) { W("[4] Dialogdiagnose fehlgeschlagen: " + ex.Message); }

        // 5) Recorder-Metadaten der letzten Aufnahmen (nur meta/summary, keine Marktdaten)
        try
        {
            var m = new StringBuilder();
            if (Directory.Exists(RecordingsDir))
                foreach (var d in Directory.GetDirectories(RecordingsDir).OrderByDescending(Directory.GetLastWriteTimeUtc).Take(12))
                {
                    m.AppendLine($"===== {Path.GetFileName(d)}  gesperrt={IsMetaLocked(Path.Combine(d, "meta.json"))}");
                    foreach (var f in new[] { "meta.json", "summary.json" })
                    { var p = Path.Combine(d, f); m.AppendLine($"--- {f}"); m.AppendLine(File.Exists(p) ? ReadAllShared(p) : "(fehlt)"); }
                    var t = Path.Combine(d, "ticks.ndjson");
                    if (File.Exists(t)) m.AppendLine($"--- ticks.ndjson: {new FileInfo(t).Length:N0} B, erste Zeile: {Trunc(FirstLine(t) ?? "", 200)}, letzte Zeile: {Trunc(ReadLastLine(t) ?? "", 200)}");
                }
            File.WriteAllText(Path.Combine(root, "5_recorder_metadaten.txt"), m.ToString(), new UTF8Encoding(true));
            W("[5] Recorder-Metadaten der letzten Aufnahmen geschrieben.");
        }
        catch (Exception ex) { W("[5] Recorder-Metadaten nicht lesbar: " + ex.Message); }

        // 6) Letzte Collector-Laufprotokolle (aus dem Datenordner und %TEMP%)
        try
        {
            var logs = new List<string>();
            var dataLogs = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(RecordingsDir)) ?? "", "Logs");
            if (Directory.Exists(dataLogs)) logs.AddRange(Directory.GetFiles(dataLogs, "*.txt"));
            logs.AddRange(Directory.GetFiles(Path.GetTempPath(), "atas_sc_*.txt"));
            foreach (var f in logs.OrderByDescending(File.GetLastWriteTimeUtc).Take(5))
                File.Copy(f, Path.Combine(root, "6_lauf_" + Path.GetFileName(f)), true);
            W($"[6] {Math.Min(5, logs.Count)} Laufprotokoll(e) kopiert.");
        }
        catch (Exception ex) { W("[6] Laufprotokolle nicht kopierbar: " + ex.Message); }

        File.WriteAllText(Path.Combine(root, "0_uebersicht.txt"), sb.ToString(), new UTF8Encoding(true));
        string zip = root.TrimEnd('\\', '/') + ".zip";
        try { if (File.Exists(zip)) File.Delete(zip); ZipFile.CreateFromDirectory(root, zip); Console.WriteLine($"ZIP: {zip}"); }
        catch (Exception ex) { Console.WriteLine("ZIP nicht erstellt: " + ex.Message); }
        return 0;
    }
}
