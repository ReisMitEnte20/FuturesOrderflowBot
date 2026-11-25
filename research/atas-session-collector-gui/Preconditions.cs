using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace AtasSessionCollector;

public sealed record Check(bool Ok, string Title, string Detail);

/// <summary>Voraussetzungen vor dem Start pruefen. Veraendert NICHTS (keine Konten/Verbindungen/Zugangsdaten).</summary>
public static class Preconditions
{
    // Mindestens so viel freier Platz auf dem Ausgabelaufwerk verlangen (konservativ).
    public const long MinFreeBytes = 3L * 1024 * 1024 * 1024; // 3 GiB

    public static List<Check> CheckAll(ToolPaths t, string outputRoot)
    {
        var l = new List<Check>
        {
            AtasRunning(),
            PythonAvailable(t.Python),
            FileCheck("Aufnahme-Steuerung (Probe)", t.ProbeExe),
            FileCheck("Collector-Skript", t.CollectorScript),
            CollectorImportable(t.Python, t.CollectorScript),
            BridgeSelfcheck(t.Python, t.CollectorScript),
            RecorderRootConfigured(outputRoot),
            RecorderDllReady(outputRoot),
            OutputWritable(outputRoot),
            DiskSpace(outputRoot),
        };
        return l;
    }

    public static Check AtasRunning()
    {
        bool ok = Process.GetProcessesByName("OFT.Platform").Length > 0;
        return new Check(ok, "ATAS laeuft",
            ok ? "OFT.Platform gefunden." : "ATAS (OFT.Platform) ist nicht gestartet. Bitte ATAS oeffnen und den MNQ-Chart bereitstellen.");
    }

    public static Check PythonAvailable(string python)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = python, Arguments = "--version", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(8000);
            bool ok = p.HasExited && p.ExitCode == 0 && o.Contains("Python");
            return new Check(ok, "Python verfuegbar", ok ? o.Trim() : "Python nicht gefunden. Bitte Python 3 installieren (python.org) und 'python' in den PATH aufnehmen.");
        }
        catch (Exception ex)
        {
            return new Check(false, "Python verfuegbar", "Python nicht startbar: " + ex.Message + ". Bitte Python 3 installieren.");
        }
    }

    /// <summary>Prueft, dass die Collector-Module (inkl. der Drittanbieter-Abhaengigkeit pyarrow) importierbar sind –
    /// mit derselben Python-Aufrufweise wie zur Laufzeit.</summary>
    public static Check CollectorImportable(string python, string script)
    {
        if (!File.Exists(script)) return new Check(false, "Collector-Bibliotheken", "Collector-Skript fehlt: " + script);
        string dir = Path.GetDirectoryName(script)!;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = python,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add($"import sys; sys.path.insert(0, r'{dir}'); import atas_bridge, atas_collect, atas_archive; print('IMPORTS_OK')");
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd();
            string e = p.StandardError.ReadToEnd();
            p.WaitForExit(15000);
            bool ok = p.HasExited && p.ExitCode == 0 && o.Contains("IMPORTS_OK");
            if (ok) return new Check(true, "Collector-Bibliotheken", "atas_bridge/atas_collect inkl. pyarrow importierbar.");
            string hint = e.Contains("pyarrow") ? "Python-Paket 'pyarrow' fehlt. Bitte installieren:  python -m pip install pyarrow"
                                                 : "Collector-Module nicht importierbar.";
            return new Check(false, "Collector-Bibliotheken", hint + (e.Trim() == "" ? "" : "  (" + e.Trim().Split('\n')[^1].Trim() + ")"));
        }
        catch (Exception ex)
        {
            return new Check(false, "Collector-Bibliotheken", "Pruefung nicht moeglich: " + ex.Message);
        }
    }

    /// <summary>Startet den Collector GENAU so wie zur Laufzeit (python &lt;skript&gt; selfcheck, fremdes Arbeitsverzeichnis).
    /// Deckt den VM-Befund ab, dass die isolierte Laufzeit den Skriptordner nicht im Suchpfad hat.</summary>
    public static Check BridgeSelfcheck(string python, string script)
    {
        const string title = "Bridge-Selbsttest";
        if (!File.Exists(script)) return new Check(false, title, "Collector-Skript fehlt: " + script);
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = python, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath(),
            };
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add("selfcheck");
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd();
            string e = p.StandardError.ReadToEnd();
            p.WaitForExit(30000);
            bool ok = p.HasExited && p.ExitCode == 0 && o.Contains("\"result\": \"ok\"");
            return ok ? new Check(true, title, o.Trim())
                      : new Check(false, title, "Collector startet nicht mit der vorgesehenen Python-Laufzeit" +
                                                (e.Trim() == "" ? "." : ": " + e.Trim().Split((char)10)[^1].Trim()));
        }
        catch (Exception ex) { return new Check(false, title, "Pruefung nicht moeglich: " + ex.Message); }
    }

    /// <summary>Schreibt die gemeinsame Recorder-Konfiguration (idempotent) und liest sie zurueck.</summary>
    public static Check RecorderRootConfigured(string dataDir)
    {
        const string title = "Recorder-Ausgabeordner";
        try
        {
            string want = AppPaths.Recordings(dataDir);
            OrderFlowTools.SharedDataRoot.Write(want);
            string? got = OrderFlowTools.SharedDataRoot.Read();
            return OrderFlowTools.SharedDataRoot.SamePath(got, want)
                ? new Check(true, title, $"{want}  (gemeinsame Konfiguration: {OrderFlowTools.SharedDataRoot.ConfigPath})")
                : new Check(false, title, $"Gemeinsame Konfiguration enthaelt '{got ?? "(nichts)"}' statt '{want}'.");
        }
        catch (Exception ex) { return new Check(false, title, "Gemeinsame Konfiguration nicht schreibbar: " + ex.Message); }
    }

    /// <summary>Installierte Recorder-DLL vorhanden und faehig, die gemeinsame Konfiguration zu lesen? Eine aeltere DLL
    /// (fester Standardpfad) ist nur zulaessig, wenn der Datenordner genau diesem alten Standard entspricht.</summary>
    public static Check RecorderDllReady(string dataDir)
    {
        const string title = "Recorder in ATAS";
        string dll = OrderFlowTools.SharedDataRoot.InstalledRecorderDll;
        if (!File.Exists(dll)) return new Check(false, title, "Recorder-DLL nicht installiert: " + dll + ". Bitte Setup ausfuehren (ATAS geschlossen).");
        if (OrderFlowTools.SharedDataRoot.DllSupportsSharedRoot(dll)) return new Check(true, title, "Recorder mit gemeinsamem Datenordner installiert.");
        string legacy = Path.Combine("F:" + Path.DirectorySeparatorChar, "ATAS_Data", "Recordings");
        return OrderFlowTools.SharedDataRoot.SamePath(legacy, AppPaths.Recordings(dataDir))
            ? new Check(true, title, "Aeltere Recorder-DLL (fester Pfad) passt zum gewaehlten Datenordner " + legacy + ".")
            : new Check(false, title, "Installierte Recorder-DLL ist eine aeltere Version mit festem Ausgabepfad " + legacy +
                                      " und wuerde NICHT in den gewaehlten Datenordner schreiben. Bitte Setup ausfuehren (ATAS geschlossen).");
    }

    private static Check FileCheck(string title, string path)
    {
        bool ok = File.Exists(path);
        return new Check(ok, title, ok ? path : $"Datei fehlt: {path}");
    }

    // Portabel: KEIN festes F: mehr. Laufwerk muss existieren und der Ordner beschreibbar sein;
    // sonst klarer Fehler (kein stiller Fallback auf einen anderen Speicherort).
    private static Check OutputWritable(string root)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(root))
                return new Check(false, "Datenordner", "Kein Datenordner gewaehlt.");
            var drive = Path.GetPathRoot(Path.GetFullPath(root));
            if (string.IsNullOrEmpty(drive) || !Directory.Exists(drive))
                return new Check(false, "Datenordner", $"Laufwerk '{drive}' nicht verfuegbar (Datenordner {root}). Kein Ausweich-Speicherort.");
            Directory.CreateDirectory(root);
            string probe = Path.Combine(root, ".write_test_" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return new Check(true, "Datenordner beschreibbar", root);
        }
        catch (Exception ex)
        {
            return new Check(false, "Datenordner beschreibbar", $"Nicht beschreibbar ({root}): {ex.Message}");
        }
    }

    private static Check DiskSpace(string root)
    {
        try
        {
            var di = new DriveInfo(Path.GetPathRoot(root)!);
            long free = di.AvailableFreeSpace;
            bool ok = free >= MinFreeBytes;
            return new Check(ok, "Freier Speicher",
                $"{free / (1024.0 * 1024 * 1024):0.0} GB frei auf {di.Name}" + (ok ? "" : $" (zu wenig, mind. {MinFreeBytes / (1024 * 1024 * 1024)} GB)"));
        }
        catch (Exception ex)
        {
            return new Check(false, "Freier Speicher", "Speicher nicht pruefbar: " + ex.Message);
        }
    }
}

/// <summary>Pfade zu den mitgelieferten Werkzeugen; neben der EXE erwartet.</summary>
public sealed class ToolPaths
{
    public string AppDir { get; }
    public string ProbeExe { get; }
    public string Python { get; }
    public string CollectorScript { get; }
    public string ArchiveScript { get; }

    public ToolPaths()
    {
        AppDir = AppContext.BaseDirectory;
        ProbeExe = Path.Combine(AppDir, "AtasReplayProbe.exe");
        CollectorScript = Path.Combine(AppDir, "bridge", "atas_collect.py");
        ArchiveScript = Path.Combine(AppDir, "bridge", "atas_archive.py");
        // Private, mitgelieferte Python-Laufzeit bevorzugen (kein globales Python / kein PATH noetig);
        // sonst ATAS_PYTHON, sonst "python" aus dem PATH.
        var bundled = Path.Combine(AppDir, "python", "python.exe");
        Python = File.Exists(bundled) ? bundled
                 : (Environment.GetEnvironmentVariable("ATAS_PYTHON") ?? "python");
    }
}
