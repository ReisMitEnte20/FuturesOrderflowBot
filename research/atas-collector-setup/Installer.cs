using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using OrderFlowTools;

namespace AtasCollectorSetup;

public sealed class SetupOptions
{
    public string PayloadDir { get; set; } = "";
    public string ProgramDir { get; set; } = "";
    public string DataDir { get; set; } = "";
    public bool Shortcuts { get; set; } = true;
    /// <summary>Nur fuer Tests: leitet %APPDATA%, Desktop und Startmenue in diesen Ordner um und ueberspringt die
    /// Pruefung auf ein laufendes ATAS (es wird dann nichts am echten ATAS-Benutzerordner veraendert).</summary>
    public string? TestRoot { get; set; }
}

public sealed class PayloadFile { public string path { get; set; } = ""; public long size { get; set; } public string sha256 { get; set; } = ""; }
public sealed class PayloadManifest
{
    public string product { get; set; } = "";
    public string version { get; set; } = "";
    public string commit { get; set; } = "";
    public string built { get; set; } = "";
    public List<PayloadFile> files { get; set; } = new();
}
public sealed class InstallState
{
    public string version { get; set; } = "";
    public string programDir { get; set; } = "";
    public string dataDir { get; set; } = "";
    public string installedUtc { get; set; } = "";
    public string recorderSha256 { get; set; } = "";
}

/// <summary>Benutzerbezogene Orte; im Testmodus in einen Testordner umgeleitet.</summary>
public sealed class SetupEnv
{
    public string AppData { get; }
    public string Desktop { get; }
    public string StartMenuPrograms { get; }
    public bool IsTest { get; }

    public SetupEnv(string? testRoot)
    {
        IsTest = !string.IsNullOrWhiteSpace(testRoot);
        if (IsTest)
        {
            AppData = Path.Combine(testRoot!, "Roaming");
            Desktop = Path.Combine(testRoot!, "Desktop");
            StartMenuPrograms = Path.Combine(testRoot!, "Startmenue");
        }
        else
        {
            AppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            Desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            StartMenuPrograms = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        }
    }

    public string CollectorAppData => Path.Combine(AppData, SharedDataRoot.AppFolderName);
    public string StatePath => Path.Combine(CollectorAppData, "install.json");
    public string SharedConfigPath => SharedDataRoot.ConfigPathIn(AppData);
    public string AtasUserDir => Path.Combine(AppData, "ATAS");
    public string IndicatorsDir => Path.Combine(AtasUserDir, "Indicators");
    public string RecorderTarget => Path.Combine(IndicatorsDir, Installer.RecorderDllName);
    public string RecorderBackupDir => Path.Combine(CollectorAppData, "recorder-backup");

    public InstallState? LoadState()
    {
        try { return File.Exists(StatePath) ? JsonSerializer.Deserialize<InstallState>(File.ReadAllText(StatePath)) : null; }
        catch { return null; }
    }
}

public sealed class Installer
{
    public const string ProductName = "ATAS Session Collector";
    public const string RecorderDllName = "OrderFlowReplayRecorder.dll";
    public const string GuiExe = "AtasSessionCollector.exe";
    public const string ProbeExe = "AtasReplayProbe.exe";

    private readonly SetupOptions _o;
    private readonly SetupEnv _env;
    private readonly Action<string> _log;
    private PayloadManifest _manifest = new();
    public List<string> Errors { get; } = new();

    public Installer(SetupOptions options, Action<string> log)
    {
        _o = options; _env = new SetupEnv(options.TestRoot); _log = log;
    }

    public static string DefaultProgramDir(SetupEnv env) =>
        env.LoadState()?.programDir is { Length: > 0 } p ? p
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", ProductName);

    public static string DefaultDataDir(SetupEnv env) =>
        env.LoadState()?.dataDir is { Length: > 0 } d ? d
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ATAS Collector Daten");

    private void Step(string s) => _log("\r\n== " + s);
    private void Info(string s) => _log("   " + s);
    private bool Fail(string s) { Errors.Add(s); _log("   FEHLER: " + s); return false; }

    /// <summary>Fuehrt die Installation aus. true nur, wenn JEDER Schritt und JEDE Abschlusspruefung bestanden ist.</summary>
    public bool Run()
    {
        try
        {
            _log($"{ProductName} - Setup  {DateTime.Now:yyyy-MM-dd HH:mm:ss}{(_env.IsTest ? "   [TESTMODUS: Benutzerordner umgeleitet]" : "")}");
            if (!VerifyPayload()) return false;
            if (!DetectAtas()) return false;
            if (!CheckNothingRunning()) return false;
            if (!PrepareDirs()) return false;
            if (!CopyProgramFiles()) return false;
            if (!WriteConfiguration()) return false;
            if (!InstallRecorder()) return false;
            if (_o.Shortcuts && !CreateShortcuts()) return false;
            if (!SaveState()) return false;
            bool ok = FinalChecks();
            _log(ok ? "\r\nINSTALLATION ERFOLGREICH ABGESCHLOSSEN."
                    : "\r\nINSTALLATION NICHT ABGESCHLOSSEN - mindestens eine Abschlusspruefung ist fehlgeschlagen (siehe FEHLER oben).");
            return ok;
        }
        catch (Exception ex)
        {
            Fail($"Unerwarteter Fehler: {ex.GetType().Name}: {ex.Message}");
            _log("\r\nINSTALLATION NICHT ABGESCHLOSSEN.");
            return false;
        }
    }

    // ---------------------------------------------------------------- 1) Paket vollstaendig?
    private bool VerifyPayload()
    {
        Step("1/10 Installationsdateien pruefen");
        var mp = Path.Combine(_o.PayloadDir, "manifest.json");
        if (!File.Exists(mp))
            return Fail($"Installationsdateien fehlen: '{mp}' nicht gefunden. Das ZIP muss VOLLSTAENDIG entpackt sein; " +
                        "Setup.exe und der Ordner 'payload' muessen nebeneinander liegen (nicht direkt aus dem ZIP starten).");
        try { _manifest = JsonSerializer.Deserialize<PayloadManifest>(File.ReadAllText(mp)) ?? new PayloadManifest(); }
        catch (Exception ex) { return Fail("manifest.json nicht lesbar: " + ex.Message); }
        if (_manifest.files.Count == 0) return Fail("manifest.json enthaelt keine Dateien.");
        int bad = 0;
        foreach (var f in _manifest.files)
        {
            var p = Path.Combine(_o.PayloadDir, f.path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(p)) { bad++; Fail("Datei fehlt im Paket: " + f.path); }
            else if (new FileInfo(p).Length != f.size || !Sha256(p).Equals(f.sha256, StringComparison.OrdinalIgnoreCase)) { bad++; Fail("Datei beschaedigt (Pruefsumme weicht ab): " + f.path); }
            if (bad >= 10) { Fail("... weitere Abweichungen nicht einzeln aufgefuehrt."); break; }
        }
        if (bad > 0) return false;
        foreach (var need in new[] { "app/" + GuiExe, "app/" + ProbeExe, "app/bridge/atas_collect.py", "app/bridge/atas_bridge.py", "app/python/python.exe", "recorder/" + RecorderDllName })
            if (!_manifest.files.Any(f => f.path.Equals(need, StringComparison.OrdinalIgnoreCase))) return Fail("Paket unvollstaendig: " + need + " ist nicht Teil des Pakets.");
        Info($"{_manifest.files.Count} Dateien vollstaendig und unbeschaedigt. Version {_manifest.version}, Quellstand {_manifest.commit}.");
        return true;
    }

    // ---------------------------------------------------------------- 2) ATAS vorhanden?
    public static List<string> FindAtasProgramDirs()
    {
        var hits = new List<string>();
        void Add(string? d) { try { if (!string.IsNullOrWhiteSpace(d) && File.Exists(Path.Combine(d!, "OFT.Platform.exe")) && !hits.Contains(d!, StringComparer.OrdinalIgnoreCase)) hits.Add(d!); } catch { } }
        foreach (var pf in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
            Add(Path.Combine(Environment.GetFolderPath(pf), "ATAS Platform"));
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            foreach (var key in new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" })
            {
                try
                {
                    using var k = hive.OpenSubKey(key);
                    if (k == null) continue;
                    foreach (var sub in k.GetSubKeyNames())
                    {
                        using var s = k.OpenSubKey(sub);
                        var name = s?.GetValue("DisplayName") as string;
                        if (name != null && name.StartsWith("ATAS", StringComparison.OrdinalIgnoreCase)) Add(s?.GetValue("InstallLocation") as string);
                    }
                }
                catch { }
            }
        foreach (var p in Process.GetProcessesByName("OFT.Platform"))
            try { Add(Path.GetDirectoryName(p.MainModule?.FileName)); } catch { }
        return hits;
    }

    private bool DetectAtas()
    {
        Step("2/10 ATAS erkennen");
        var progs = _env.IsTest ? new List<string>() : FindAtasProgramDirs();
        bool userDir = Directory.Exists(_env.AtasUserDir);
        foreach (var p in progs) Info("ATAS-Programm: " + p);
        Info("ATAS-Benutzerordner: " + _env.AtasUserDir + (userDir ? "" : "  (noch nicht vorhanden)"));
        if (progs.Count == 0 && !userDir)
            return Fail("ATAS wurde auf diesem Rechner nicht gefunden. ATAS ist Voraussetzung und nicht Teil dieses Pakets: " +
                        "bitte ATAS installieren, einmal starten und anmelden, wieder schliessen und dieses Setup erneut ausfuehren.");
        Info("Indikator-Ordner dieses Benutzers: " + _env.IndicatorsDir);
        return true;
    }

    // ---------------------------------------------------------------- 3) nichts in Benutzung?
    private bool CheckNothingRunning()
    {
        Step("3/10 Laufende Programme pruefen");
        if (!_env.IsTest && Process.GetProcessesByName("OFT.Platform").Length > 0)
            return Fail("ATAS laeuft. Der Recorder kann nur bei GESCHLOSSENEM ATAS installiert werden. Bitte ATAS vollstaendig beenden und das Setup erneut starten.");
        foreach (var n in new[] { "AtasSessionCollector", "AtasReplayProbe" })
            foreach (var p in Process.GetProcessesByName(n))
            {
                string? path = null; try { path = p.MainModule?.FileName; } catch { }
                if (path != null && IsUnder(path, _o.ProgramDir))
                    return Fail($"{n} laeuft noch aus dem Zielordner. Bitte den Collector beenden und das Setup erneut starten.");
            }
        Info("ATAS und Collector sind geschlossen.");
        return true;
    }

    // ---------------------------------------------------------------- 4) Ordner
    private bool PrepareDirs()
    {
        Step("4/10 Programm- und Datenordner");
        if (string.IsNullOrWhiteSpace(_o.ProgramDir) || string.IsNullOrWhiteSpace(_o.DataDir)) return Fail("Programm- und Datenordner muessen angegeben sein.");
        try { _o.ProgramDir = Path.GetFullPath(_o.ProgramDir.Trim()); _o.DataDir = Path.GetFullPath(_o.DataDir.Trim()); }
        catch (Exception ex) { return Fail("Ungueltiger Pfad: " + ex.Message); }
        if (SharedDataRoot.SamePath(_o.ProgramDir, _o.DataDir)) return Fail("Programm- und Datenordner muessen verschieden sein.");
        if (IsUnder(_o.ProgramDir, _o.PayloadDir) || IsUnder(_o.PayloadDir, _o.ProgramDir))
            return Fail("Der Programmordner darf nicht im entpackten Paket liegen (und umgekehrt).");
        foreach (var (label, dir) in new[] { ("Programmordner", _o.ProgramDir), ("Datenordner", _o.DataDir) })
        {
            var drive = Path.GetPathRoot(dir);
            if (string.IsNullOrEmpty(drive) || !Directory.Exists(drive)) return Fail($"{label}: Laufwerk '{drive}' ist nicht vorhanden ({dir}).");
            if (!WriteTest(dir, out var why)) return Fail($"{label} nicht beschreibbar ({dir}): {why}");
            Info($"{label}: {dir}");
        }
        foreach (var sub in new[] { "Collections", "Recordings", "Logs" })
            if (!WriteTest(Path.Combine(_o.DataDir, sub), out var why)) return Fail($"Datenordner\\{sub} nicht beschreibbar: {why}");
        try
        {
            long free = new DriveInfo(Path.GetPathRoot(_o.DataDir)!).AvailableFreeSpace;
            Info($"Freier Speicher im Datenlaufwerk: {free / (1024.0 * 1024 * 1024):0.0} GB (Richtwert 150-200 MB je Aufnahmetag).");
        }
        catch { }
        return true;
    }

    // ---------------------------------------------------------------- 5) Programmdateien
    private bool CopyProgramFiles()
    {
        Step("5/10 Programmdateien kopieren");
        var prev = _env.LoadState();
        if (prev != null) Info($"Vorhandene Installation gefunden (Version {prev.version}, {prev.programDir}) -> wird aktualisiert, Einstellungen und Daten bleiben erhalten.");
        int copied = 0, same = 0;
        foreach (var f in _manifest.files.Where(f => f.path.StartsWith("app/", StringComparison.OrdinalIgnoreCase)))
        {
            var src = Path.Combine(_o.PayloadDir, f.path.Replace('/', Path.DirectorySeparatorChar));
            var dst = Path.Combine(_o.ProgramDir, f.path.Substring(4).Replace('/', Path.DirectorySeparatorChar));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                if (File.Exists(dst) && new FileInfo(dst).Length == f.size && Sha256(dst).Equals(f.sha256, StringComparison.OrdinalIgnoreCase)) { same++; continue; }
                File.Copy(src, dst, true);
                copied++;
            }
            catch (Exception ex) { return Fail($"Kopieren fehlgeschlagen ({f.path}): {ex.Message}"); }
        }
        Info($"{copied} Dateien kopiert, {same} bereits aktuell.");
        return true;
    }

    // ---------------------------------------------------------------- 6) gemeinsame Konfiguration
    private bool WriteConfiguration()
    {
        Step("6/10 Gemeinsamen Datenordner konfigurieren");
        try
        {
            // Oberflaeche (und ueber sie Probe + Bridge): collector-data.json neben der EXE.
            var cfg = Path.Combine(_o.ProgramDir, "collector-data.json");
            File.WriteAllText(cfg, JsonSerializer.Serialize(new Dictionary<string, string> { ["dataDir"] = _o.DataDir }, new JsonSerializerOptions { WriteIndented = true }));
            Info("Oberflaeche/Probe/Bridge: " + cfg);
            // Recorder (in ATAS): gemeinsame Konfiguration, wird bei JEDEM Instanzstart vor dem ersten Schreibzugriff gelesen.
            var rec = Path.Combine(_o.DataDir, "Recordings");
            SharedDataRoot.Write(rec, _env.SharedConfigPath);
            Info("Recorder: " + _env.SharedConfigPath + "  ->  " + rec);
            return true;
        }
        catch (Exception ex) { return Fail("Konfiguration nicht schreibbar: " + ex.Message); }
    }

    // ---------------------------------------------------------------- 7) Recorder
    private bool InstallRecorder()
    {
        Step("7/10 Recorder in ATAS installieren");
        var entry = _manifest.files.First(f => f.path.Equals("recorder/" + RecorderDllName, StringComparison.OrdinalIgnoreCase));
        var src = Path.Combine(_o.PayloadDir, "recorder", RecorderDllName);
        try
        {
            Directory.CreateDirectory(_env.IndicatorsDir);
            var dst = _env.RecorderTarget;
            if (File.Exists(dst))
            {
                var have = Sha256(dst);
                if (have.Equals(entry.sha256, StringComparison.OrdinalIgnoreCase)) { Info("Recorder ist bereits in dieser Version installiert (keine Aenderung)."); return true; }
                Directory.CreateDirectory(_env.RecorderBackupDir);
                var bak = Path.Combine(_env.RecorderBackupDir, $"OrderFlowReplayRecorder_{have.Substring(0, 12)}.dll");
                if (!File.Exists(bak)) File.Copy(dst, bak, false);
                Info("Vorhandene Recorder-DLL gesichert: " + bak);
            }
            File.Copy(src, dst, true);
            Info("Recorder installiert: " + dst);
            return true;
        }
        catch (Exception ex) { return Fail($"Recorder nicht installierbar ({_env.RecorderTarget}): {ex.Message}"); }
    }

    // ---------------------------------------------------------------- 8) Verknuepfungen
    private bool CreateShortcuts()
    {
        Step("8/10 Verknuepfungen erstellen");
        var target = Path.Combine(_o.ProgramDir, GuiExe);
        foreach (var dir in new[] { _env.StartMenuPrograms, _env.Desktop })
        {
            try
            {
                Directory.CreateDirectory(dir);
                var lnk = Path.Combine(dir, ProductName + ".lnk");
                var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell nicht verfuegbar");
                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic sc = shell.CreateShortcut(lnk);
                sc.TargetPath = target; sc.WorkingDirectory = _o.ProgramDir; sc.Description = ProductName; sc.IconLocation = target + ",0";
                sc.Save();
                Info("Verknuepfung: " + lnk);
            }
            catch (Exception ex) { return Fail($"Verknuepfung in '{dir}' nicht erstellbar: {ex.Message}"); }
        }
        return true;
    }

    // ---------------------------------------------------------------- 9) Installationsstand
    private bool SaveState()
    {
        Step("9/10 Installationsstand speichern");
        try
        {
            Directory.CreateDirectory(_env.CollectorAppData);
            var st = new InstallState
            {
                version = _manifest.version, programDir = _o.ProgramDir, dataDir = _o.DataDir,
                installedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), recorderSha256 = Sha256(_env.RecorderTarget),
            };
            File.WriteAllText(_env.StatePath, JsonSerializer.Serialize(st, new JsonSerializerOptions { WriteIndented = true }));
            File.Copy(Path.Combine(_o.PayloadDir, "manifest.json"), Path.Combine(_o.ProgramDir, "install-manifest.json"), true);
            Info(_env.StatePath);
            return true;
        }
        catch (Exception ex) { return Fail("Installationsstand nicht speicherbar: " + ex.Message); }
    }

    // ---------------------------------------------------------------- 10) Abschlusspruefungen (ALLE laufen)
    private bool FinalChecks()
    {
        Step("10/10 Abschlusspruefungen");
        int before = Errors.Count;
        void Check(string title, bool ok, string detail) { if (ok) Info($"[OK]    {title}: {detail}"); else Fail($"{title}: {detail}"); }

        // a) Dateivollstaendigkeit am Zielort
        int missing = 0;
        foreach (var f in _manifest.files.Where(f => f.path.StartsWith("app/", StringComparison.OrdinalIgnoreCase)))
        {
            var dst = Path.Combine(_o.ProgramDir, f.path.Substring(4).Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(dst) || new FileInfo(dst).Length != f.size || !Sha256(dst).Equals(f.sha256, StringComparison.OrdinalIgnoreCase)) { missing++; if (missing <= 5) Fail("Installierte Datei fehlt/abweichend: " + f.path); }
        }
        Check("Dateivollstaendigkeit", missing == 0, missing == 0 ? "alle Programmdateien vorhanden und unveraendert" : $"{missing} Datei(en) fehlen oder weichen ab");

        // b) private Python-Laufzeit (ohne globale Python-Umgebung)
        var py = Path.Combine(_o.ProgramDir, "python", "python.exe");
        var script = Path.Combine(_o.ProgramDir, "bridge", "atas_collect.py");
        var (c1, o1, e1) = RunTool(py, new[] { "--version" }, 30);
        Check("Private Python-Laufzeit", c1 == 0 && (o1 + e1).Contains("Python 3"), c1 == 0 ? (o1 + e1).Trim() : $"startet nicht (rc={c1}) {e1.Trim()}");

        // c) Bridge GENAU wie zur Laufzeit: python <skript> selfcheck, fremdes Arbeitsverzeichnis
        var (c2, o2, e2) = RunTool(py, new[] { script, "selfcheck" }, 60);
        bool bridgeOk = c2 == 0 && o2.Contains("\"result\": \"ok\"");
        Check("Bridge-Selbsttest (atas_collect -> atas_bridge -> pyarrow)", bridgeOk, bridgeOk ? o2.Trim() : $"rc={c2} {LastLine(e2)}");
        var (c3, o3, e3) = RunTool(py, new[] { "-c", "import sys, json, csv, decimal, hashlib, zoneinfo, pyarrow, pyarrow.parquet; print('IMPORTS_OK', pyarrow.__version__)" }, 60);
        Check("Python-Pakete", c3 == 0 && o3.Contains("IMPORTS_OK"), c3 == 0 ? o3.Trim() : $"rc={c3} {LastLine(e3)}");
        var (c4, o4, e4) = RunTool(py, new[] { script, "status", "--collection", Path.Combine(_o.DataDir, "Collections", "_setup_pruefung_nicht_vorhanden") }, 60);
        Check("Bridge-Statusaufruf im Datenordner", c4 == 0 && o4.Contains("\"jobs\""), c4 == 0 ? "ok" : $"rc={c4} {LastLine(e4)}");

        // d) Programme starten (mitgelieferte .NET-Laufzeit, kein installiertes .NET noetig)
        var (c5, o5, e5) = RunTool(Path.Combine(_o.ProgramDir, GuiExe), new[] { "--selftest" }, 120);
        Check("Oberflaeche startet (Selbsttest)", c5 == 0, c5 == 0 ? "bestanden" : $"rc={c5} {LastLine(o5 + e5)}");
        // Arbeitsordner der Regression ausserhalb des Programmordners (sonst wuechse er mit jedem Setup-Lauf).
        var regressDir = Path.Combine(Path.GetTempPath(), "asc_setup_regress_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        var (c6, o6, e6) = RunTool(Path.Combine(_o.ProgramDir, ProbeExe), new[] { "regress", regressDir }, 300);
        try { if (Directory.Exists(regressDir)) Directory.Delete(regressDir, true); } catch { }
        Check("Aufnahme-Steuerung startet (Regressionstest)", c6 == 0 && o6.Contains("ALLE BESTANDEN"), c6 == 0 ? "bestanden" : $"rc={c6} {LastLine(o6 + e6)}");

        // e) Datenordner + gemeinsame Konfiguration
        foreach (var sub in new[] { "Collections", "Recordings", "Logs" })
        { bool w = WriteTest(Path.Combine(_o.DataDir, sub), out var why); Check($"Schreibrecht {sub}", w, w ? Path.Combine(_o.DataDir, sub) : why); }
        var rec = Path.Combine(_o.DataDir, "Recordings");
        var shared = SharedDataRoot.Read(_env.SharedConfigPath);
        Check("Gemeinsame Recorder-Konfiguration", SharedDataRoot.SamePath(shared, rec), $"'{shared ?? "(nichts)"}' erwartet '{rec}'");
        string guiCfg = "";
        try { using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_o.ProgramDir, "collector-data.json"))); guiCfg = doc.RootElement.GetProperty("dataDir").GetString() ?? ""; } catch { }
        Check("Datenordner der Oberflaeche", SharedDataRoot.SamePath(guiCfg, _o.DataDir), $"'{guiCfg}'");
        try
        {
            var resolved = SharedDataRoot.Resolve("", out var srcKind, _env.SharedConfigPath);
            Check("Recorder-Pfadaufloesung (neue Instanz, leeres Feld)", SharedDataRoot.SamePath(resolved, rec) && srcKind == SharedDataRoot.Source.SharedConfig, resolved);
        }
        catch (Exception ex) { Check("Recorder-Pfadaufloesung (neue Instanz, leeres Feld)", false, ex.Message); }

        // f) Recorder-DLL
        var entry = _manifest.files.First(f => f.path.Equals("recorder/" + RecorderDllName, StringComparison.OrdinalIgnoreCase));
        bool recOk = File.Exists(_env.RecorderTarget) && Sha256(_env.RecorderTarget).Equals(entry.sha256, StringComparison.OrdinalIgnoreCase);
        Check("Recorder-DLL installiert", recOk, _env.RecorderTarget);
        Check("Recorder unterstuetzt gemeinsamen Datenordner", SharedDataRoot.DllSupportsSharedRoot(_env.RecorderTarget), "Kennung " + SharedDataRoot.RecorderCapabilityMarker);

        // g) Verknuepfungen
        if (_o.Shortcuts)
            foreach (var dir in new[] { _env.StartMenuPrograms, _env.Desktop })
            { var lnk = Path.Combine(dir, ProductName + ".lnk"); Check("Verknuepfung", File.Exists(lnk), lnk); }

        return Errors.Count == before;
    }

    // ---------------------------------------------------------------- Helfer
    private static string LastLine(string s) { var t = s.Trim(); int i = t.LastIndexOf('\n'); return i >= 0 ? t.Substring(i + 1).Trim() : t; }

    private static (int code, string stdout, string stderr) RunTool(string exe, string[] args, int timeoutSec)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
                CreateNoWindow = true, WorkingDirectory = Path.GetTempPath(),
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            // Beweist die Eigenstaendigkeit der privaten Laufzeit: keine geerbten Python-Einstellungen.
            foreach (var k in new[] { "PYTHONPATH", "PYTHONHOME", "PYTHONSTARTUP", "ATAS_PYTHON" }) psi.Environment.Remove(k);
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var so = p.StandardOutput.ReadToEndAsync(); var se = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutSec * 1000)) { try { p.Kill(true); } catch { } return (-1, "", $"Zeitueberschreitung nach {timeoutSec}s"); }
            return (p.ExitCode, so.Result, se.Result);
        }
        catch (Exception ex) { return (-2, "", ex.GetType().Name + ": " + ex.Message); }
    }

    public static string Sha256(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    private static bool WriteTest(string dir, out string why)
    {
        why = "";
        try
        {
            Directory.CreateDirectory(dir);
            var t = Path.Combine(dir, ".setup_schreibtest_" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(t, "ok"); File.Delete(t);
            return true;
        }
        catch (Exception ex) { why = ex.Message; return false; }
    }

    private static bool IsUnder(string path, string dir)
    {
        try
        {
            var p = Path.GetFullPath(path).TrimEnd('\\', '/') + "\\";
            var d = Path.GetFullPath(dir).TrimEnd('\\', '/') + "\\";
            return p.StartsWith(d, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
