using System;
using System.IO;
using System.Text;
using OrderFlowTools;

// Offline-Tests der gemeinsamen Datenordner-Konfiguration (SharedDataRoot). Kodiert die Faelle aus dem VM-Test:
// neue Recorder-Instanz ohne gesetztes Feld, Remove/Add, Neustart, Ordner mit Leerzeichen, kein Laufwerk F:.
internal static class SharedDataRootTests
{
    public static void Run(Action<string, bool, string> report)
    {
        void check(string name, bool ok, string detail = "") => report(name, ok, detail);
        var tmp = Path.Combine(Path.GetTempPath(), "sdr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var cfg = Path.Combine(tmp, "AppData mit Leerzeichen", SharedDataRoot.AppFolderName, SharedDataRoot.FileName);
            var rec = Path.Combine(tmp, "Daten Ordner mit Leerzeichen", "Recordings");

            // 1) Ohne Konfiguration und ohne Feld: klarer Abbruch, KEIN Standardpfad (insbesondere kein F:).
            bool threw = false; string msg = "";
            try { SharedDataRoot.Resolve("", out _, cfg); } catch (InvalidOperationException ex) { threw = true; msg = ex.Message; }
            check("no_config_no_setting_fails_without_default", threw && !msg.Contains("F:\\ATAS_Data"), msg);

            // 2) Schreiben + Lesen, Pfad mit Leerzeichen, Elternordner wird angelegt, keine tmp-Reste.
            SharedDataRoot.Write(rec, cfg);
            var read = SharedDataRoot.Read(cfg);
            check("write_then_read_roundtrip_with_spaces", SharedDataRoot.SamePath(read, rec), read ?? "(null)");
            check("write_is_atomic_no_tmp_left", Directory.GetFiles(Path.GetDirectoryName(cfg)!).Length == 1);

            // 3) Neue Instanz mit LEEREM Feld (Remove/Add): gemeinsame Konfiguration greift.
            var r1 = SharedDataRoot.Resolve("", out var s1, cfg);
            check("empty_setting_uses_shared_config", SharedDataRoot.SamePath(r1, rec) && s1 == SharedDataRoot.Source.SharedConfig, r1);

            // 4) Zweite und dritte Instanz (Instanzwechsel, 'Neustart' = erneutes Lesen): gleicher Pfad, kein Rueckfall.
            var r2 = SharedDataRoot.Resolve(null, out _, cfg);
            var r3 = SharedDataRoot.Resolve("   ", out _, cfg);
            check("repeated_instances_same_path", SharedDataRoot.SamePath(r1, r2) && SharedDataRoot.SamePath(r2, r3));

            // 5) Veraltetes Feld (z. B. aus einer ATAS-Vorlage) darf die gemeinsame Konfiguration NICHT uebersteuern.
            var stale = Path.Combine(tmp, "alt", "Recordings");
            var r4 = SharedDataRoot.Resolve(stale, out var s4, cfg);
            check("shared_config_wins_over_stale_setting", SharedDataRoot.SamePath(r4, rec) && s4 == SharedDataRoot.Source.SharedConfig, r4);

            // 6) Ohne gemeinsame Konfiguration zaehlt das Feld (Betrieb ohne Collector bleibt moeglich).
            var cfgMissing = Path.Combine(tmp, "fehlt", SharedDataRoot.FileName);
            var r5 = SharedDataRoot.Resolve(stale, out var s5, cfgMissing);
            check("setting_used_without_shared_config", SharedDataRoot.SamePath(r5, stale) && s5 == SharedDataRoot.Source.IndicatorSetting, r5);

            // 7) Umstellen des Datenordners wirkt fuer die naechste Instanz.
            var rec2 = Path.Combine(tmp, "Zweiter Ordner", "Recordings");
            SharedDataRoot.Write(rec2, cfg);
            check("changed_folder_applies_to_next_instance", SharedDataRoot.SamePath(SharedDataRoot.Resolve("", out _, cfg), rec2));

            // 8) Nicht vorhandenes Laufwerk: Abbruch statt Ausweichpfad.
            string? missingDrive = null;
            for (char c = 'Z'; c >= 'D' && missingDrive == null; c--) if (!Directory.Exists(c + ":\\")) missingDrive = c + ":\\";
            if (missingDrive != null)
            {
                File.WriteAllText(cfg, missingDrive + "ATAS_Data\\Recordings\r\n", new UTF8Encoding(false));
                bool dthrew = false;
                try { SharedDataRoot.Resolve("", out _, cfg); } catch (DirectoryNotFoundException) { dthrew = true; }
                check("missing_drive_fails_no_fallback", dthrew, missingDrive);
            }

            // 9) Robust gegen BOM, Anfuehrungszeichen, Kommentar- und Leerzeilen.
            File.WriteAllText(cfg, "# Kommentar\r\n\r\n\"" + rec + "\"\r\n", new UTF8Encoding(true));
            check("tolerates_bom_quotes_comments", SharedDataRoot.SamePath(SharedDataRoot.Read(cfg), rec), SharedDataRoot.Read(cfg) ?? "(null)");

            // 10) Leere/kaputte Datei == nicht konfiguriert.
            File.WriteAllText(cfg, "\r\n   \r\n");
            check("blank_file_is_unconfigured", SharedDataRoot.Read(cfg) == null);

            // 11) Pfadvergleich: Gross-/Kleinschreibung und abschliessender Trenner egal.
            check("same_path_ignores_case_and_trailing_separator",
                  SharedDataRoot.SamePath(rec, rec.ToUpperInvariant() + "\\") && !SharedDataRoot.SamePath(rec, rec2) && !SharedDataRoot.SamePath(null, rec));

            // 12) DLL-Kennung: UTF-16-Literal wird erkannt, fehlende Kennung nicht.
            var withMarker = Path.Combine(tmp, "a.dll"); var without = Path.Combine(tmp, "b.dll");
            File.WriteAllBytes(withMarker, Concat(new byte[] { 1, 2, 3 }, Encoding.Unicode.GetBytes("xx" + SharedDataRoot.RecorderCapabilityMarker + "yy")));
            File.WriteAllBytes(without, Encoding.Unicode.GetBytes("OrderFlowReplayRecorder/proto-2-bufferfix"));
            check("dll_capability_marker_detection",
                  SharedDataRoot.DllSupportsSharedRoot(withMarker) && !SharedDataRoot.DllSupportsSharedRoot(without)
                  && !SharedDataRoot.DllSupportsSharedRoot(Path.Combine(tmp, "gibtsnicht.dll")));
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length); Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
        return r;
    }
}
