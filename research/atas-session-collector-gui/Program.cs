using System;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace AtasSessionCollector;

internal static class Program
{
    public const string AppVersion = "1.2.5";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
            return SelfTest.Run();
        if (args.Length > 0 && args[0].Equals("--run", StringComparison.OrdinalIgnoreCase))
            return Headless.Run(args);

        // Parallel-Schutz: nur EINE Instanz (und damit nur ein Aufnahmelauf) gleichzeitig.
        using var mutex = new Mutex(true, @"Global\AtasSessionCollector_SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show(
                "Der ATAS-Session-Collector laeuft bereits. Es ist immer nur ein Aufnahmelauf gleichzeitig moeglich.",
                "Bereits gestartet", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }
}

/// <summary>Headless-Abnahme der Zeitzonen-/Kalenderlogik (ohne ATAS). Rueckgabe 0=alle bestanden.</summary>
internal static class SelfTest
{
    private static int _fail;

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    private static readonly System.Text.StringBuilder _report = new();

    public static int Run()
    {
        try { AttachConsole(-1); } catch { }  // an aufrufende Shell anheften (WinExe hat sonst keine Konsole)
        Line("== ATAS-Session-Collector Selftest (Zeitzonen & Kalender) ==\n");

        // --- Zeitzonenfaelle rund um die versetzten Sommerzeitwechsel 2026 ---
        // EU-Sommerzeit endet 2026-10-25, US-Sommerzeit endet 2026-11-01.
        var ny = SessionCalendar.Get(SessionId.NewYork);
        var lo = SessionCalendar.Get(SessionId.London);

        // Vor beiden Herbstwechseln: US EDT(-4), EU CEST(+2).
        Tz(ny, new DateOnly(2026, 10, 23), "13:30", "16:00", "15:30", "18:00");
        // Nach EU-Wechsel, vor US-Wechsel: US EDT(-4), EU CET(+1).
        Tz(ny, new DateOnly(2026, 10, 26), "13:30", "16:00", "14:30", "17:00");
        // Nach beiden Herbstwechseln: US EST(-5), EU CET(+1).
        Tz(ny, new DateOnly(2026, 11, 2), "14:30", "17:00", "15:30", "18:00");

        // Fruehjahr: US-Sommerzeit startet 2026-03-08, EU 2026-03-29.
        // Vor beiden: US EST(-5), EU CET(+1).
        Tz(ny, new DateOnly(2026, 3, 2), "14:30", "17:00", "15:30", "18:00");
        // Nach US-, vor EU-Wechsel: US EDT(-4), EU CET(+1).
        Tz(ny, new DateOnly(2026, 3, 9), "13:30", "16:00", "14:30", "17:00");

        // London: UK-Sommerzeit startet 2026-03-29 (gleich mit EU).
        // Vor dem Wechsel: UK GMT(0), EU CET(+1).
        Tz(lo, new DateOnly(2026, 3, 27), "08:00", "11:00", "09:00", "12:00");
        // Nach dem Wechsel: UK BST(+1), EU CEST(+2).
        Tz(lo, new DateOnly(2026, 3, 30), "07:00", "10:00", "09:00", "12:00");
        // Herbst vor EU-Wechsel: UK BST(+1), EU CEST(+2).
        Tz(lo, new DateOnly(2026, 10, 23), "07:00", "10:00", "09:00", "12:00");
        // Herbst nach beiden EU/UK-Wechseln (gleicher Termin): UK GMT(0), EU CET(+1).
        Tz(lo, new DateOnly(2026, 10, 26), "08:00", "11:00", "09:00", "12:00");

        // --- Kalender ---
        Cal(SessionId.NewYork, new DateOnly(2026, 1, 1), DayStatus.Holiday, "Neujahr");
        Cal(SessionId.NewYork, new DateOnly(2026, 11, 26), DayStatus.Holiday, "Thanksgiving");
        Cal(SessionId.NewYork, new DateOnly(2026, 12, 25), DayStatus.Holiday, "Weihnachten");
        Cal(SessionId.NewYork, new DateOnly(2026, 5, 25), DayStatus.Holiday, "Memorial");
        Cal(SessionId.NewYork, new DateOnly(2026, 11, 27), DayStatus.EarlyCloseTrading, "nach Thanksgiving");
        Cal(SessionId.NewYork, new DateOnly(2026, 10, 24), DayStatus.Weekend, "Wochenende");
        Cal(SessionId.NewYork, new DateOnly(2026, 10, 26), DayStatus.Trading, null);
        Cal(SessionId.NewYork, new DateOnly(2026, 10, 27), DayStatus.Trading, null);
        Cal(SessionId.NewYork, new DateOnly(2026, 10, 28), DayStatus.Trading, null);

        Cal(SessionId.London, new DateOnly(2026, 1, 1), DayStatus.Holiday, "Neujahr");
        Cal(SessionId.London, new DateOnly(2026, 4, 3), DayStatus.Holiday, "Karfreitag");
        Cal(SessionId.London, new DateOnly(2026, 4, 6), DayStatus.Holiday, "Ostermontag");
        Cal(SessionId.London, new DateOnly(2026, 12, 25), DayStatus.Holiday, "Weihnachten");
        Cal(SessionId.London, new DateOnly(2026, 12, 28), DayStatus.Holiday, "Boxing Day");
        Cal(SessionId.London, new DateOnly(2026, 8, 31), DayStatus.Holiday, "Summer Bank Holiday");

        // --- Ordnername gemaess Vorgabe ---
        string fn = SessionCalendar.CollectionFolderName("MNQ", ny, new DateOnly(2026, 10, 26), new DateOnly(2026, 10, 28));
        Expect(fn == "MNQ_NewYork_0930-1200_America-New-York_2026-10-26_bis_2026-10-28",
            "Ordnername", fn);

        Line("");
        Line(_fail == 0 ? "ERGEBNIS: ALLE BESTANDEN" : $"ERGEBNIS: {_fail} FEHLGESCHLAGEN");
        string outPath = Path.Combine(Path.GetTempPath(), "atas_sc_selftest.txt");
        try { File.WriteAllText(outPath, _report.ToString()); Console.WriteLine("Report: " + outPath); } catch { }
        return _fail == 0 ? 0 : 1;
    }

    private static void Line(string s) { _report.AppendLine(s); Console.WriteLine(s); }

    private static void Tz(SessionDef s, DateOnly d, string utcStart, string utcEnd, string berlinStart, string berlinEnd)
    {
        var p = SessionCalendar.PlanDay(d, s);
        string gotUtc = $"{p.UtcStart:HH:mm}", gotUtcE = $"{p.UtcEnd:HH:mm}";
        string gotBer = $"{p.BerlinStart:HH:mm}", gotBerE = $"{p.BerlinEnd:HH:mm}";
        bool ok = gotUtc == utcStart && gotUtcE == utcEnd && gotBer == berlinStart && gotBerE == berlinEnd;
        Report(ok, $"{s.Name} {d:yyyy-MM-dd}",
            $"UTC [{gotUtc},{gotUtcE}) Berlin [{gotBer},{gotBerE})  (erwartet UTC [{utcStart},{utcEnd}) Berlin [{berlinStart},{berlinEnd}))");
    }

    private static void Cal(SessionId id, DateOnly d, DayStatus expected, string? needle)
    {
        var (st, reason) = SessionCalendar.ClassifyDay(d, id);
        bool ok = st == expected && (needle == null || (reason ?? "").Contains(needle, StringComparison.OrdinalIgnoreCase));
        Report(ok, $"{id} {d:yyyy-MM-dd}", $"{st} / {reason ?? "-"} (erwartet {expected}{(needle == null ? "" : " ~ " + needle)})");
    }

    private static void Expect(bool ok, string title, string detail) => Report(ok, title, detail);

    private static void Report(bool ok, string title, string detail)
    {
        if (!ok) _fail++;
        Line($"[{(ok ? "PASS" : "FAIL")}] {title,-22} {detail}");
    }
}
