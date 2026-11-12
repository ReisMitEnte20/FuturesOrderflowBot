using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace AtasSessionCollector;

/// <summary>
/// Verborgener Headless-Modus fuer Abnahme/Automatisierung. Nutzt EXAKT dieselbe Engine
/// (Preconditions + RunPlan + CollectorRunner) wie die Oberflaechen-Schaltflaechen, damit der
/// End-to-End-Test denselben Collector und dieselbe gemeinsame Ausgabe erzeugt.
/// Nicht Teil des normalen Bedienflusses.
///   --run --session NewYork|London --from yyyy-MM-dd --to yyyy-MM-dd
///         [--speed 100x] [--output F:\ATAS_Data\Collections] [--instrument MNQ] [--stop-after N]
/// </summary>
internal static class Headless
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    public static int Run(string[] args)
    {
        try { AttachConsole(-1); } catch { }
        string Get(string key, string def = "") { int i = Array.FindIndex(args, a => a.Equals(key, StringComparison.OrdinalIgnoreCase)); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }

        var sessName = Get("--session", "NewYork");
        var s = SessionCalendar.Sessions.FirstOrDefault(x => x.Name.Equals(sessName, StringComparison.OrdinalIgnoreCase)) ?? SessionCalendar.Get(SessionId.NewYork);
        if (!DateOnly.TryParse(Get("--from"), CultureInfo.InvariantCulture, out var from) ||
            !DateOnly.TryParse(Get("--to"), CultureInfo.InvariantCulture, out var to))
        { Console.WriteLine("FEHLER: --from/--to (yyyy-MM-dd) erforderlich."); return 3; }
        if (to < from) (from, to) = (to, from);
        string speed = Get("--speed", "100x");
        string instrument = Get("--instrument", "MNQ");
        int stopAfter = int.TryParse(Get("--stop-after", "0"), out var n) ? n : 0;
        // Portabel: Datenordner aus --datadir, sonst gespeicherte Wahl. KEIN festes F:.
        string dataDir = Get("--datadir", AppPaths.LoadDataDir() ?? "");
        if (!AppPaths.Validate(dataDir, out var dWhy)) { Console.WriteLine("ABBRUCH: Datenordner fehlt/ungueltig (" + dWhy + "). --datadir <Pfad> angeben oder in der GUI waehlen."); return 3; }
        AppPaths.EnsureSubdirs(dataDir);

        var tools = new ToolPaths();
        var checks = Preconditions.CheckAll(tools, dataDir);
        foreach (var c in checks) Console.WriteLine($"[{(c.Ok ? "OK" : "FEHLT")}] {c.Title}: {c.Detail}");
        if (checks.Any(c => !c.Ok)) { Console.WriteLine("ABBRUCH: Voraussetzungen nicht erfuellt."); return 3; }

        var days = SessionCalendar.PlanRange(from, to, s);
        var plan = new RunPlan
        {
            Instrument = instrument, Session = s, Speed = speed, OutputRoot = AppPaths.Collections(dataDir),
            From = from, To = to, AllDays = days,
            ProbeExe = tools.ProbeExe, Python = tools.Python, CollectorScript = tools.CollectorScript,
            RecordingsDir = AppPaths.Recordings(dataDir),
        };
        Console.WriteLine($"# Collection: {plan.CollectionDir}");
        Console.WriteLine($"# Aufnahme-Tage: {plan.RecordDays.Count}  Speed={speed}  stopAfter={stopAfter}");

        var runner = new CollectorRunner();
        int finished = 0;
        runner.Step += st => Console.WriteLine("  » " + st);
        runner.DayFinished += r =>
        {
            Console.WriteLine($"  >> {r.Date:yyyy-MM-dd}  {r.OutcomeText}  Trades={r.Trades}  Vol={r.Volume}  {CollectorRunner.FormatDur(r.RuntimeSec)}  {r.Detail}");
            if (r.Outcome is DayOutcome.Ok or DayOutcome.Reused) { finished++; if (stopAfter > 0 && finished >= stopAfter) { Console.WriteLine("  [TEST] stop-after erreicht -> kontrolliert stoppen."); runner.RequestStop(); } }
        };

        var sum = runner.RunAsync(plan, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine($"\n=== {(sum.Complete ? "VOLLSTAENDIG" : "UNVOLLSTAENDIG")}  Trades={sum.TotalTrades}  Volumen={sum.TotalVolume}  CSV={sum.CsvBytes} B ===");
        if (sum.StopReason != "") Console.WriteLine("Grund: " + sum.StopReason);
        Console.WriteLine("Ausgabe: " + sum.CollectionDir);
        return sum.Complete ? 0 : 8;
    }
}
