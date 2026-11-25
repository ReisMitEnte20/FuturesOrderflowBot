using System;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace AtasCollectorSetup;

internal static class Program
{
    /// <summary>
    /// Ohne Argumente: Assistent. Unbeaufsichtigt (fuer Tests/Automatisierung):
    ///   Setup.exe /silent [/programdir "..."] [/datadir "..."] [/payload "..."] [/noshortcuts] [/log "datei"] [/testroot "..."]
    /// Rueckgabe 0 = vollstaendig installiert und alle Abschlusspruefungen bestanden, 1 = nicht abgeschlossen, 2 = Aufruffehler.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        // Schalter auch als --name / -name annehmen (manche Shells schreiben /name in einen Pfad um).
        for (int n = 0; n < args.Length; n++)
            if (args[n].StartsWith("--") && args[n].Length > 2) args[n] = "/" + args[n].Substring(2);
        string Get(string k) { int i = Array.FindIndex(args, x => x.Equals(k, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : ""; }
        bool Has(string k) => Array.Exists(args, x => x.Equals(k, StringComparison.OrdinalIgnoreCase));

        string testRoot = Get("/testroot");
        var env = new SetupEnv(testRoot == "" ? null : testRoot);
        var opt = new SetupOptions
        {
            PayloadDir = Get("/payload") is { Length: > 0 } p ? p : Path.Combine(AppContext.BaseDirectory, "payload"),
            ProgramDir = Get("/programdir") is { Length: > 0 } pd ? pd : Installer.DefaultProgramDir(env),
            DataDir = Get("/datadir") is { Length: > 0 } dd ? dd : Installer.DefaultDataDir(env),
            Shortcuts = !Has("/noshortcuts"),
            TestRoot = testRoot == "" ? null : testRoot,
        };

        // Deinstallationsmodus: als Uninstall.exe gestartet (Eintrag 'Installierte Apps') oder mit /uninstall.
        bool uninstall = Has("/uninstall") || Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "").StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase);
        if (uninstall)
        {
            var sbu = new StringBuilder();
            string logU = Get("/log");
            void LogU(string s) { sbu.AppendLine(s); if (logU != "") { try { File.WriteAllText(logU, sbu.ToString(), new UTF8Encoding(false)); } catch { } } }
            if (!Has("/silent"))
            {
                ApplicationConfiguration.Initialize();
                var st = env.LoadState();
                var q = MessageBox.Show(Installer.ProductName + " deinstallieren?\n\nProgramm: " + (st?.programDir ?? "(nicht gefunden)") + "\n" +
                                        "Aufnahmen, Collections und Logs bleiben erhalten:\n" + (st?.dataDir ?? "-") + "\n\nATAS und der Collector muessen geschlossen sein.",
                                        "Deinstallation", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (q != DialogResult.OK) return 1;
            }
            bool okU;
            var un = new Uninstaller(env, LogU);
            try { okU = un.Run(); } catch (Exception ex) { LogU("FEHLER: " + ex); okU = false; }
            if (!Has("/silent"))
                MessageBox.Show(okU ? "Deinstallation abgeschlossen. Ihre Daten wurden nicht veraendert." : "Deinstallation NICHT vollstaendig:\n\n" + string.Join("\n", un.Errors),
                                "Deinstallation", MessageBoxButtons.OK, okU ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            return okU ? 0 : 1;
        }

        if (Has("/silent"))
        {
            var sb = new StringBuilder();
            string logFile = Get("/log");
            void Log(string s) { sb.AppendLine(s); if (logFile != "") { try { File.WriteAllText(logFile, sb.ToString(), new UTF8Encoding(false)); } catch { } } }
            bool ok;
            try { ok = new Installer(opt, Log).Run(); }
            catch (Exception ex) { Log("FEHLER: " + ex); ok = false; }
            return ok ? 0 : 1;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new SetupForm(opt, env));
        return 0;
    }
}
