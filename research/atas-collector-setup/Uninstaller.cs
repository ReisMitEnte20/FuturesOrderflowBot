using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;
using OrderFlowTools;

namespace AtasCollectorSetup;

/// <summary>
/// Deinstallation (Uninstall.exe = Setup.exe im Deinstallationsmodus). Entfernt NUR, was diese Installation nachweislich
/// angelegt hat: die im Installationsmanifest gelisteten Programmdateien, die eigenen Verknuepfungen, den eigenen
/// Registrierungseintrag und die Recorder-DLL nur dann, wenn sie bytegleich mit der installierten ist. Eine vom Setup
/// gesicherte fremde Vorgaenger-DLL wird zurueckkopiert. Aufnahmen, Collections und Logs (Datenordner) bleiben erhalten.
/// Keine rekursive Loeschung von Ordnern, kein Beenden laufender Programme.
/// </summary>
public sealed class Uninstaller
{
    private readonly SetupEnv _env;
    private readonly Action<string> _log;
    public List<string> Errors { get; } = new();
    public List<string> Kept { get; } = new();

    public Uninstaller(SetupEnv env, Action<string> log) { _env = env; _log = log; }

    private void Info(string s) => _log("   " + s);
    private bool Fail(string s) { Errors.Add(s); _log("   FEHLER: " + s); return false; }

    public bool Run()
    {
        _log($"{Installer.ProductName} - Deinstallation  {DateTime.Now:yyyy-MM-dd HH:mm:ss}{(_env.IsTest ? "   [TESTMODUS]" : "")}");
        var st = _env.LoadState();
        if (st == null || string.IsNullOrWhiteSpace(st.programDir))
            return Fail($"Keine Installation gefunden ({_env.StatePath}). Es wurde nichts entfernt.");
        string prog = st.programDir;
        Info($"Programmordner: {prog}");
        Info($"Datenordner (bleibt erhalten): {st.dataDir}");

        // 1) Nichts in Benutzung (kein Erzwingen)
        if (!_env.IsTest && Process.GetProcessesByName("OFT.Platform").Length > 0)
            return Fail("ATAS laeuft. Bitte ATAS schliessen und die Deinstallation erneut starten (die Recorder-DLL ist sonst in Benutzung).");
        foreach (var n in new[] { "AtasSessionCollector", "AtasReplayProbe" })
            foreach (var p in Process.GetProcessesByName(n))
            {
                string? path = null; try { path = p.MainModule?.FileName; } catch { }
                if (path != null && Installer.IsUnderPublic(path, prog))
                    return Fail($"{n} laeuft noch. Bitte den Collector beenden und die Deinstallation erneut starten.");
            }

        // 2) Programmdateien laut Installationsmanifest (nur diese)
        var manifestPath = Path.Combine(prog, "install-manifest.json");
        var files = new List<string>();
        try
        {
            var m = JsonSerializer.Deserialize<PayloadManifest>(File.ReadAllText(manifestPath));
            files = m?.files.Where(f => f.path.StartsWith("app/", StringComparison.OrdinalIgnoreCase))
                            .Select(f => f.path.Substring(4).Replace('/', Path.DirectorySeparatorChar)).ToList() ?? new();
        }
        catch (Exception ex) { return Fail($"Installationsmanifest nicht lesbar ({manifestPath}): {ex.Message}. Es wurde nichts entfernt."); }
        int removed = 0;
        foreach (var rel in files.Concat(new[] { "collector-data.json", "install-manifest.json" }))
        {
            if (rel.Equals("Uninstall.exe", StringComparison.OrdinalIgnoreCase)) continue;   // laeuft ggf. gerade; wird zuletzt entfernt
            var full = Path.Combine(prog, rel);
            if (!Installer.IsUnderPublic(full, prog)) continue;
            try { if (File.Exists(full)) { File.Delete(full); removed++; } }
            catch (Exception ex) { Fail($"Datei nicht entfernbar: {full}: {ex.Message}"); }
        }
        Info($"{removed} Programmdatei(en) entfernt.");
        RemoveEmptyDirs(prog);

        // 3) Verknuepfungen (nur unsere: Ziel = installierte Oberflaeche)
        foreach (var dir in new[] { _env.StartMenuPrograms, _env.Desktop })
        {
            var lnk = Path.Combine(dir, Installer.ProductName + ".lnk");
            if (!File.Exists(lnk)) continue;
            string target = "";
            try
            {
                var shellType = Type.GetTypeFromProgID("WScript.Shell")!;
                dynamic shell = Activator.CreateInstance(shellType)!;
                target = (string)shell.CreateShortcut(lnk).TargetPath;
            }
            catch { }
            if (Installer.IsUnderPublic(target, prog)) { try { File.Delete(lnk); Info("Verknuepfung entfernt: " + lnk); } catch (Exception ex) { Fail($"Verknuepfung nicht entfernbar: {lnk}: {ex.Message}"); } }
            else { Kept.Add(lnk); Info($"Verknuepfung bleibt (zeigt nicht auf diese Installation): {lnk}"); }
        }

        // 4) Recorder-DLL: nur entfernen, wenn bytegleich mit der installierten; gesicherte Vorgaenger-DLL zurueck
        bool recorderOurs = false;
        try
        {
            if (File.Exists(_env.RecorderTarget))
            {
                var have = Installer.Sha256(_env.RecorderTarget);
                if (!string.IsNullOrEmpty(st.recorderSha256) && have.Equals(st.recorderSha256, StringComparison.OrdinalIgnoreCase))
                {
                    recorderOurs = true;
                    if (!string.IsNullOrEmpty(st.recorderBackup) && File.Exists(st.recorderBackup))
                    { File.Copy(st.recorderBackup, _env.RecorderTarget, true); Info("Vorgaenger-Recorder-DLL wiederhergestellt aus: " + st.recorderBackup); }
                    else { File.Delete(_env.RecorderTarget); Info("Recorder-DLL entfernt: " + _env.RecorderTarget); }
                }
                else { Kept.Add(_env.RecorderTarget); Info("Recorder-DLL wurde seit der Installation ersetzt -> bleibt unveraendert: " + _env.RecorderTarget); }
            }
        }
        catch (Exception ex) { Fail("Recorder-DLL nicht bearbeitbar: " + ex.Message); }

        // 5) Eigene Einstellungen: gemeinsame Recorder-Konfiguration nur, wenn unser Recorder entfernt wurde
        try
        {
            if (recorderOurs && File.Exists(_env.SharedConfigPath)) { File.Delete(_env.SharedConfigPath); Info("Recorder-Konfiguration entfernt."); }
            if (File.Exists(_env.StatePath)) File.Delete(_env.StatePath);
        }
        catch (Exception ex) { Fail("Einstellungen nicht entfernbar: " + ex.Message); }

        // 6) Eintrag unter 'Installierte Apps'
        try { Registry.CurrentUser.DeleteSubKeyTree(_env.UninstallKey, false); Info("Eintrag 'Installierte Apps' entfernt."); }
        catch (Exception ex) { Fail("Registrierung nicht entfernbar: " + ex.Message); }

        // 7) Uninstall.exe selbst + leerer Programmordner (nach Prozessende, ohne rekursives Loeschen)
        ScheduleSelfRemoval(Path.Combine(prog, "Uninstall.exe"), prog);

        Info($"Datenordner unveraendert: {st.dataDir}");
        if (Kept.Count > 0) Info("Bewusst nicht entfernt: " + string.Join("; ", Kept));
        _log(Errors.Count == 0 ? "\r\nDEINSTALLATION ABGESCHLOSSEN." : "\r\nDEINSTALLATION MIT FEHLERN - siehe oben.");
        return Errors.Count == 0;
    }

    private void RemoveEmptyDirs(string root)
    {
        try
        {
            foreach (var d in Directory.GetDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length))
                try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d, false); } catch { }
            var left = Directory.EnumerateFileSystemEntries(root).Where(e => !Path.GetFileName(e).Equals("Uninstall.exe", StringComparison.OrdinalIgnoreCase)).ToList();
            if (left.Count > 0) { Kept.AddRange(left); Info($"Im Programmordner verbleiben {left.Count} fremde Eintraege (nicht von dieser Installation)."); }
        }
        catch { }
    }

    private void ScheduleSelfRemoval(string uninstallExe, string progDir)
    {
        try
        {
            // Nach ~3 s (wenn dieser Prozess beendet ist): Uninstall.exe loeschen, dann Programmordner NUR falls leer (rmdir ohne /s).
            string cmd = $"/c ping -n 4 127.0.0.1 >nul & del /f /q \"{uninstallExe}\" & rmdir \"{progDir}\"";
            Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        }
        catch { }
    }
}
