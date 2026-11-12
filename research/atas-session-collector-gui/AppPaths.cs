using System;
using System.IO;
using System.Text.Json;

namespace AtasSessionCollector;

/// <summary>
/// Portable Datenordner-Verwaltung: Der Benutzer waehlt den Datenordner in der GUI; er wird dauerhaft
/// gespeichert und von Collector + Probe verwendet. Darunter liegen Collections, Recordings und Logs.
/// KEINE festen Laufwerke (F:/A:), keine Repo-Abhaengigkeit. Fehlender/unbeschreibbarer Ordner -> klarer
/// Fehler, KEIN stiller Ausweich-Speicherort.
///
/// Speicherort der Einstellung: bevorzugt neben der EXE (portabel, reist mit dem entpackten Ordner mit);
/// ist der Installationsordner nicht beschreibbar, dann unter %APPDATA%.
/// </summary>
public static class AppPaths
{
    public const string AppFolderName = "AtasSessionCollector";

    private sealed class Cfg { public string? dataDir { get; set; } }

    private static string PortableConfig => Path.Combine(AppContext.BaseDirectory, "collector-data.json");
    private static string RoamingConfig =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName, "collector-data.json");

    /// <summary>Gespeicherter Datenordner oder null, wenn noch keiner gewaehlt wurde.</summary>
    public static string? LoadDataDir()
    {
        foreach (var p in new[] { PortableConfig, RoamingConfig })
        {
            try
            {
                if (File.Exists(p))
                {
                    var c = JsonSerializer.Deserialize<Cfg>(File.ReadAllText(p));
                    if (!string.IsNullOrWhiteSpace(c?.dataDir)) return c!.dataDir;
                }
            }
            catch { /* naechsten Ort versuchen */ }
        }
        return null;
    }

    /// <summary>Datenordner dauerhaft speichern (portabel neben der EXE; sonst %APPDATA%).</summary>
    public static void SaveDataDir(string dir)
    {
        var json = JsonSerializer.Serialize(new Cfg { dataDir = dir }, new JsonSerializerOptions { WriteIndented = true });
        try
        {
            File.WriteAllText(PortableConfig, json);
            return;
        }
        catch { /* Installationsordner nicht beschreibbar -> %APPDATA% */ }
        Directory.CreateDirectory(Path.GetDirectoryName(RoamingConfig)!);
        File.WriteAllText(RoamingConfig, json);
    }

    /// <summary>Portabler Standard-Datenordner NEBEN der EXE (kein festes Laufwerk). Wird verwendet, wenn noch
    /// kein Datenordner gespeichert wurde -> "entpacken und starten" funktioniert auf jedem Laufwerk/jeder VM.</summary>
    public static string DefaultDataDir => Path.Combine(AppContext.BaseDirectory, "ATAS_Data");

    public static string Collections(string dataDir) => Path.Combine(dataDir, "Collections");
    public static string Recordings(string dataDir) => Path.Combine(dataDir, "Recordings");
    public static string Logs(string dataDir) => Path.Combine(dataDir, "Logs");

    public static void EnsureSubdirs(string dataDir)
    {
        Directory.CreateDirectory(Collections(dataDir));
        Directory.CreateDirectory(Recordings(dataDir));
        Directory.CreateDirectory(Logs(dataDir));
    }

    /// <summary>Datenordner validieren: Laufwerk vorhanden + beschreibbar. Kein stiller Fallback.</summary>
    public static bool Validate(string dir, out string reason)
    {
        reason = "";
        try
        {
            if (string.IsNullOrWhiteSpace(dir)) { reason = "Kein Ordner angegeben."; return false; }
            var drive = Path.GetPathRoot(Path.GetFullPath(dir));
            if (string.IsNullOrEmpty(drive) || !Directory.Exists(drive)) { reason = $"Laufwerk '{drive}' nicht verfuegbar."; return false; }
            Directory.CreateDirectory(dir);
            var t = Path.Combine(dir, ".wtest_" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(t, "ok"); File.Delete(t);
            return true;
        }
        catch (Exception ex) { reason = ex.Message; return false; }
    }
}
