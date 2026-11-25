#nullable enable
// Gemeinsame Datenordner-Konfiguration fuer Recorder (laeuft IN ATAS), Oberflaeche, Probe und Setup.
//
// Problem: Der Recorder liest seinen Ausgabeordner nur beim Start einer Instanz. Die Steuerung fuegt je
// Aufnahmefenster eine NEUE Instanz hinzu; eine neue Instanz kennt keinen von Hand gesetzten 'Output-Ordner'
// und fiel deshalb auf den eingebauten Standard zurueck. Eine Umgebungsvariable hilft nicht, weil ATAS bereits
// laeuft, wenn der Datenordner gewaehlt wird.
//
// Loesung: EINE Datei an einem festen, benutzerbezogenen Ort, die jede Komponente gleich berechnet:
//     %APPDATA%\AtasSessionCollector\recorder-output-root.txt     (erste Zeile = Recordings-Ordner, UTF-8)
// Oberflaeche/Setup schreiben sie, der Recorder liest sie bei JEDEM Instanzstart vor dem ersten Schreibzugriff.
// Sie ueberlebt Remove/Add, ATAS-Neustart und Rechner-Neustart und haengt an keinem Laufwerksbuchstaben.
//
// ATAS-frei und ohne weitere Abhaengigkeiten, damit dieselbe Datei in Recorder, Oberflaeche, Probe, Setup und
// Tests kompiliert wird (Single Source of Truth, kein Drift).
using System;
using System.IO;
using System.Text;

namespace OrderFlowTools
{
    public static class SharedDataRoot
    {
        public const string AppFolderName = "AtasSessionCollector";
        public const string FileName = "recorder-output-root.txt";

        /// <summary>Kennung, die eine Recorder-DLL mit Unterstuetzung dieser Datei im Binary traegt.</summary>
        public const string RecorderCapabilityMarker = "sharedroot-1";

        public enum Source { SharedConfig, IndicatorSetting }

        public static string ConfigPath => ConfigPathIn(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

        public static string ConfigPathIn(string appData) => Path.Combine(appData, AppFolderName, FileName);

        /// <summary>Liest den konfigurierten Recordings-Ordner; null, wenn Datei fehlt, leer oder unlesbar ist.</summary>
        public static string? Read(string? configPath = null)
        {
            try
            {
                var p = configPath ?? ConfigPath;
                if (!File.Exists(p)) return null;
                foreach (var raw in File.ReadAllLines(p, Encoding.UTF8))
                {
                    var line = raw.Trim().Trim('﻿').Trim('"').Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                    return line;
                }
                return null;
            }
            catch { return null; }
        }

        /// <summary>Schreibt den Recordings-Ordner atomar (tmp + Ersetzen). Wirft bei Fehlern.</summary>
        public static void Write(string recordingsDir, string? configPath = null)
        {
            if (string.IsNullOrWhiteSpace(recordingsDir)) throw new ArgumentException("Recordings-Ordner ist leer.");
            var full = Path.GetFullPath(recordingsDir.Trim());
            var p = configPath ?? ConfigPath;
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            var tmp = p + ".tmp_" + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, full + Environment.NewLine, new UTF8Encoding(false));
            File.Move(tmp, p, true);
        }

        /// <summary>Pfadvergleich ohne Ruecksicht auf Gross-/Kleinschreibung und abschliessende Trenner.</summary>
        public static bool SamePath(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            try { return string.Equals(Norm(a!), Norm(b!), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private static string Norm(string p) =>
            Path.GetFullPath(p.Trim()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        /// <summary>
        /// Ausgabeordner einer Recorder-Instanz bestimmen. Reihenfolge: gemeinsame Konfiguration, dann das
        /// Indikator-Feld. KEIN eingebauter Standardpfad und KEIN Ausweichen auf ein anderes Laufwerk: ohne
        /// Konfiguration oder bei nicht erreichbarem Laufwerk wird mit klarer Meldung abgebrochen.
        /// </summary>
        public static string Resolve(string? indicatorSetting, out Source source, string? configPath = null)
        {
            var shared = Read(configPath);
            string root;
            if (!string.IsNullOrWhiteSpace(shared)) { root = shared!; source = Source.SharedConfig; }
            else if (!string.IsNullOrWhiteSpace(indicatorSetting)) { root = indicatorSetting!.Trim(); source = Source.IndicatorSetting; }
            else
                throw new InvalidOperationException(
                    "OF Replay Recorder: kein Ausgabeordner konfiguriert. Erwartet wird '" + (configPath ?? ConfigPath) +
                    "' (schreibt der ATAS Session Collector) oder ein gesetztes Feld 'Output-Ordner'. Kein Standardpfad.");
            var full = Path.GetFullPath(root);
            var drive = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(drive) || !Directory.Exists(drive))
                throw new DirectoryNotFoundException(
                    "OF Replay Recorder: Ziellaufwerk '" + drive + "' fuer Ausgabeordner '" + full + "' nicht verfuegbar - kein Ausweichpfad.");
            return full;
        }

        /// <summary>Traegt die DLL die Kennung fuer die gemeinsame Konfiguration (als UTF-16-Literal)?</summary>
        public static bool DllSupportsSharedRoot(string dllPath)
        {
            try
            {
                var data = File.ReadAllBytes(dllPath);
                var needle = Encoding.Unicode.GetBytes(RecorderCapabilityMarker);
                for (int i = 0; i <= data.Length - needle.Length; i++)
                {
                    int j = 0;
                    while (j < needle.Length && data[i + j] == needle[j]) j++;
                    if (j == needle.Length) return true;
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>Installationsort der Recorder-DLL in ATAS (pro Benutzer).</summary>
        public static string InstalledRecorderDll =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ATAS", "Indicators", "OrderFlowReplayRecorder.dll");
    }
}
