using OrderFlowTools;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace AtasReplayProbe;

/// <summary>
/// Mach­barkeits-Werkzeug fuer die automatische ATAS-Replay-Steuerung. Zwei read-only Inspektionen:
///   api-scan  -> Metadaten-Reflection (MetadataLoadContext) ueber die INSTALLIERTEN ATAS/OFT-DLLs:
///                sucht oeffentliche Replay-/Playback-/MarketDataMode-/History-Oberflaeche mit Signaturen.
///   uia-dump  -> Windows UI Automation ueber das laufende ATAS-Fenster: actionable Controls + Keyword-Treffer
///                (AutomationId/Name/ControlType/Patterns) des Replay-Bereichs.
/// Es wird NICHTS in ATAS gepatcht/injiziert und NICHTS geklickt. Reine Analyse.
/// </summary>
internal static partial class Program
{
    private const string AtasDir = @"C:\Program Files (x86)\ATAS Platform";

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);

    private static int Main(string[] args)
    {
        // Per-Monitor-DPI (V2): UIA-Rechtecke, Screenshots und Mausposition liegen dann im selben physischen Pixelraum,
        // auch bei 125/150 % Skalierung oder RDP. Schlaegt es fehl (aelteres Windows), bleibt die Voreinstellung.
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
        var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        try
        {
            return cmd switch
            {
                "api-scan" => ApiScan(args.Length > 1 ? args[1] : null),
                "uia-dump" => UiaDump(args.Length > 1 ? args[1] : null),
                "chartdiag" => ChartDiag(args.Skip(1).FirstOrDefault(a => !a.StartsWith("-"))),
                "diagpack" => DiagPack(args.Skip(1).FirstOrDefault(a => !a.StartsWith("-"))),
                "stoptest" => StopTest(args.Skip(1).FirstOrDefault(a => !a.StartsWith("-"))),
                "xdiag" => XDiag(args.Skip(1).FirstOrDefault(a => !a.StartsWith("-"))),
                "drive" => Drive(args.Contains("--go"), args.FirstOrDefault(a => a.EndsWith(".txt"))),
                "inspect2" => Inspect2(),
                "inspect-date" => InspectDate(args.FirstOrDefault(a => a.EndsWith(".txt"))),
                "load" => Load(args.FirstOrDefault(a => a.EndsWith(".txt"))),
                "inspect-menu" => InspectMenu(args.Skip(1).FirstOrDefault(a => !a.StartsWith("-") && !a.EndsWith(".txt")) ?? "ModuleMenuButton", args.FirstOrDefault(a => a.EndsWith(".txt"))),
                "mainwin" => MainWin(args.Contains("--restore"), args.FirstOrDefault(a => a.EndsWith(".txt"))),
                "selectmodule" => SelectModule(args.FirstOrDefault(a => a.EndsWith(".txt"))),
                "prepare" => Prepare(args.FirstOrDefault(a => a.EndsWith(".txt"))),
                "toggle-header" => ToggleHeader(args.FirstOrDefault(a => a.EndsWith(".txt"))),
                "diag" => Diag(args),
                "open-replay" => OpenReplay(args.FirstOrDefault(a => a.EndsWith(".txt"))),
                "replay-off" => ReplayOff(args.FirstOrDefault(a => a.EndsWith(".txt"))),
                "replay-toggle" => ReplayToggle(),
                "replayfind" => ReplayFind(args.FirstOrDefault(a => a.EndsWith(".txt"))),
                "ocrfind" => OcrFind(args.FirstOrDefault(a => a.EndsWith(".txt"))),
                "normalize" => Normalize(),
                "dump-window" => DumpWindow(args.Skip(1).FirstOrDefault(a => !a.EndsWith(".txt")) ?? "Indikatoren", args.FirstOrDefault(a => a.EndsWith(".txt"))),
                "rm-indicator" => RemoveIndicator(args.Skip(1).FirstOrDefault(a => !a.EndsWith(".txt")) ?? "Recorder", args.Contains("--go"), args.FirstOrDefault(a => a.EndsWith(".txt"))),
                "end-recorder" => EndRecorder(args.Contains("--go"), args.FirstOrDefault(a => a.EndsWith(".txt"))),
                "poke" => Poke(args),
                "typeat" => TypeAt(args),
                "allctrls" => AllCtrls(args.FirstOrDefault(a => a.EndsWith(".txt"))),
                "replayrect" => ReplayRect(args.FirstOrDefault(a => a.EndsWith(".png"))),
                "setdate" => SetDate(args),
            "datediag" => DateDiag(args),
            "xblob" => XBlob(args),
                "series" => Series(args.FirstOrDefault(a => a.EndsWith(".txt"))),
                "autorun" => AutoRun(args.FirstOrDefault(a => a.EndsWith(".txt"))),
                "liveproof" => LiveProof(args.FirstOrDefault(a => a.EndsWith(".txt"))),
                "twojobs" => TwoJobs(args.FirstOrDefault(a => a.EndsWith(".txt"))),
                "collect" => Collect(args.Skip(1).FirstOrDefault(a => a.EndsWith(".json")), args.FirstOrDefault(a => a.EndsWith(".txt"))),
                "speedprobe" => SpeedProbe(args.FirstOrDefault(a => a.EndsWith(".txt"))),
                "rmrec" => RemoveRecorderCommand(args.Skip(1).FirstOrDefault(a => !a.StartsWith("-"))),
                "playerprobe" => PlayerProbe(args.FirstOrDefault(a => a.EndsWith(".txt"))),
                "replaymenu" => ReplayMenu(args.FirstOrDefault(a => a.EndsWith(".txt"))),
                "exitview" => ExitViewCmd(),
                "indprobe" => IndProbe(args.FirstOrDefault(a => a.EndsWith(".txt"))),
                "addrec" => AddRecTest(args.FirstOrDefault(a => a.EndsWith(".txt"))),
                "addonly" => AddOnly(args.FirstOrDefault(a => a.EndsWith(".txt"))),
                "watchend" => WatchEnd(args.FirstOrDefault(a => a.EndsWith(".txt"))),
                "regress" => RunRegress(args.Skip(1).FirstOrDefault(a => !a.StartsWith("-") && !a.EndsWith(".txt"))),
                "selftest" => RunRegress(args.Skip(1).FirstOrDefault(a => !a.StartsWith("-") && !a.EndsWith(".txt"))),
                "dump-date" => DumpDate(args.FirstOrDefault(a => a.EndsWith(".txt"))),
                "close-atas" => CloseAtas(args.FirstOrDefault(a => a.EndsWith(".txt"))),
                "chart-tools" => ChartTools(args.FirstOrDefault(a => a.EndsWith(".txt"))),
                "indicators" => Indicators(args.FirstOrDefault(a => a.EndsWith(".txt"))),
                "close-window" => CloseWindow(args.Skip(1).FirstOrDefault(a => !a.EndsWith(".txt")) ?? "", args.FirstOrDefault(a => a.EndsWith(".txt"))),
                "rclick-chart" => RClickChart(args.FirstOrDefault(a => a.EndsWith(".txt"))),
                "capture" => Capture(args),
                _ => Help(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FEHLER ({cmd}): {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 2;
        }
    }

    private static int Help()
    {
        Console.WriteLine("AtasReplayProbe <api-scan|uia-dump> [outFile]");
        Console.WriteLine("  api-scan : oeffentliche ATAS/OFT-API auf Replay-/Playback-/Status-Steuerung pruefen");
        Console.WriteLine("  uia-dump : laufendes ATAS-Fenster per UI Automation inventarisieren");
        Console.WriteLine("  chartdiag [datei.txt] : Zielchart-Erkennung pruefen (nur lesen): Prozesse, Fenster, UIA, Chart-Kandidaten, Recorder");
        return 1;
    }

    // ----------------------------------------------------------------------------------------------
    // 1) API-SURFACE via MetadataLoadContext (fuehrt KEINEN ATAS-Code aus)
    // ----------------------------------------------------------------------------------------------
    private static int ApiScan(string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); }

        W($"# ATAS API-Scan  ({DateTime.Now:yyyy-MM-dd HH:mm:ss})");
        W($"ATAS-Verzeichnis: {AtasDir}");

        // Resolver: alle DLLs aus dem ATAS-Ordner + die installierte .NET-Shared-Framework-Core-Assembly.
        var paths = new List<string>();
        if (Directory.Exists(AtasDir))
            paths.AddRange(Directory.GetFiles(AtasDir, "*.dll"));
        var core = FindCoreAssemblyDir();
        W($"Core-Framework: {core ?? "(nicht gefunden)"}");
        if (core != null)
            paths.AddRange(Directory.GetFiles(core, "System.*.dll"));
        // Deduplicate by filename (ATAS-Ordner gewinnt, steht zuerst).
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
        {
            var n = Path.GetFileName(p);
            if (!byName.ContainsKey(n)) byName[n] = p;
        }

        using var mlc = new System.Reflection.MetadataLoadContext(
            new System.Reflection.PathAssemblyResolver(byName.Values));

        // Assemblies, deren oeffentliche Oberflaeche fuer Replay/Status/History relevant ist.
        string[] targets =
        {
            "ATAS.DataFeedsCore.dll", "ATAS.Indicators.dll", "ATAS.Types.dll",
            "ATAS.Strategies.dll", "OFT.Platform.dll", "OFT.Platform.Core.dll",
            "OFT.Core.dll", "OFT.Models.dll", "OFT.Attributes.dll",
        };

        // Keyword-Regex fuer Typ-/Mitgliedsnamen (Replay-Steuerung, Status, History-Export, Speed).
        var rx = new Regex(
            "replay|playback|rewind|marketdatamode|isreplay|datafeedexport|historyexport|requesthistory|loadhistory|playspeed|speedmultiplier",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        int typesMatched = 0, asmLoaded = 0;
        var hitLines = new List<string>();

        foreach (var t in targets)
        {
            var full = Path.Combine(AtasDir, t);
            if (!File.Exists(full)) { W($"\n## {t}  (NICHT vorhanden)"); continue; }
            Assembly asm;
            try { asm = mlc.LoadFromAssemblyPath(full); asmLoaded++; }
            catch (Exception ex) { W($"\n## {t}  (Ladefehler: {ex.GetType().Name})"); continue; }

            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException rtl) { types = rtl.Types.Where(x => x != null).ToArray()!; }

            var local = new List<string>();
            foreach (var ty in types)
            {
                if (ty is null || !(ty.IsPublic || ty.IsNestedPublic)) continue;
                bool typeNameHit = rx.IsMatch(ty.Name);
                var memberHits = new List<string>();
                MemberInfo[] members;
                try
                {
                    members = ty.GetMembers(BindingFlags.Public | BindingFlags.Instance |
                                            BindingFlags.Static | BindingFlags.DeclaredOnly);
                }
                catch { members = Array.Empty<MemberInfo>(); }

                foreach (var m in members)
                {
                    if (!rx.IsMatch(m.Name)) continue;
                    memberHits.Add("      " + DescribeMember(m));
                }

                if (typeNameHit || memberHits.Count > 0)
                {
                    var kind = ty.IsInterface ? "interface" : ty.IsEnum ? "enum" : ty.IsValueType ? "struct" : "class";
                    local.Add($"    [{kind}] {ty.FullName}");
                    local.AddRange(memberHits);
                    typesMatched++;
                }
            }

            if (local.Count > 0)
            {
                W($"\n## {t}");
                foreach (var l in local) W(l);
            }
        }

        // Gezielt: IMarketDataModeProvider / IsReplay / PlaybackRange ueber ALLE geladenen Assemblies.
        W("\n## Gezielte Statusquellen (IsReplay / PlaybackRange / IMarketDataMode*)");
        foreach (var t in targets)
        {
            var full = Path.Combine(AtasDir, t);
            if (!File.Exists(full)) continue;
            Assembly asm;
            try { asm = mlc.LoadFromAssemblyPath(full); } catch { continue; }
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException rtl) { types = rtl.Types.Where(x => x != null).ToArray()!; }
            foreach (var ty in types)
            {
                if (ty is null) continue;
                if (ty.Name.Contains("MarketDataMode", StringComparison.OrdinalIgnoreCase) ||
                    ty.Name.Contains("IReplay", StringComparison.OrdinalIgnoreCase))
                {
                    var kind = ty.IsInterface ? "interface" : "type";
                    W($"    [{kind}] {ty.FullName}  (asm {t})");
                    try
                    {
                        foreach (var m in ty.GetMembers(BindingFlags.Public | BindingFlags.Instance |
                                                        BindingFlags.Static | BindingFlags.DeclaredOnly))
                            W("      " + DescribeMember(m));
                    }
                    catch { }
                }
            }
        }

        W($"\n# Zusammenfassung: Assemblies geladen={asmLoaded}, Treffer-Typen={typesMatched}");

        var text = sb.ToString();
        Console.WriteLine(text);
        if (outFile != null) { File.WriteAllText(outFile, text); Console.WriteLine($"\n[geschrieben: {outFile}]"); }
        return 0;
    }

    private static string DescribeMember(MemberInfo m)
    {
        switch (m)
        {
            case PropertyInfo p:
                var get = p.GetGetMethod(false) != null ? "get" : "";
                var set = p.GetSetMethod(false) != null ? "set" : "";
                var acc = (get + " " + set).Trim();
                var ctrl = p.GetSetMethod(false) != null ? "  <-- SETTER (potenziell steuernd)" : "  (read-only)";
                return $"property {SafeName(p.PropertyType)} {p.Name} {{ {acc} }}{ctrl}";
            case MethodInfo me when !me.IsSpecialName:
                var ps = string.Join(", ", me.GetParameters().Select(x => $"{SafeName(x.ParameterType)} {x.Name}"));
                return $"method   {SafeName(me.ReturnType)} {me.Name}({ps})";
            case EventInfo e:
                return $"event    {SafeName(e.EventHandlerType)} {e.Name}";
            case FieldInfo f when f.IsPublic:
                return $"field    {SafeName(f.FieldType)} {f.Name}";
            default:
                return $"member   {m.Name}";
        }
    }

    private static string SafeName(Type? t)
    {
        if (t is null) return "void";
        try { return t.Name; } catch { return "?"; }
    }

    private static string? FindCoreAssemblyDir()
    {
        var roots = new[]
        {
            @"C:\Program Files\dotnet\shared\Microsoft.NETCore.App",
            @"C:\Program Files (x86)\dotnet\shared\Microsoft.NETCore.App",
        };
        foreach (var r in roots)
        {
            if (!Directory.Exists(r)) continue;
            var best = Directory.GetDirectories(r)
                .Select(d => (dir: d, ver: ParseVer(Path.GetFileName(d))))
                .Where(x => x.ver != null)
                .OrderByDescending(x => x.ver)
                .FirstOrDefault();
            if (best.dir != null && File.Exists(Path.Combine(best.dir, "System.Private.CoreLib.dll")))
                return best.dir;
        }
        return null;
    }

    private static Version? ParseVer(string s)
    {
        var m = Regex.Match(s, @"^\d+\.\d+\.\d+");
        return m.Success && Version.TryParse(m.Value, out var v) ? v : null;
    }

    // ----------------------------------------------------------------------------------------------
    // 2) UI AUTOMATION des laufenden ATAS-Fensters (reiner Client, kein Klick)
    // ----------------------------------------------------------------------------------------------
    private static int UiaDump(string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }

        W($"# ATAS UIA-Dump  ({DateTime.Now:yyyy-MM-dd HH:mm:ss})");

        var procs = Process.GetProcessesByName("OFT.Platform");
        if (procs.Length == 0) { W("ATAS (OFT.Platform) laeuft nicht."); return 3; }

        var windows = new List<AutomationElement>();
        foreach (var p in procs)
        {
            W($"Prozess OFT.Platform pid={p.Id}  MainWindowTitle='{p.MainWindowTitle}'  hwnd={p.MainWindowHandle}");
            if (p.MainWindowHandle != IntPtr.Zero)
            {
                try { var el = AutomationElement.FromHandle(p.MainWindowHandle); if (el != null) windows.Add(el); }
                catch (Exception ex) { W($"  FromHandle Fehler: {ex.Message}"); }
            }
        }
        // Zusaetzlich: alle Top-Level-Fenster desselben Prozesses (Replay kann separates Fenster sein).
        try
        {
            var pids = procs.Select(p => p.Id).ToHashSet();
            var top = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
            foreach (AutomationElement w in top)
            {
                try
                {
                    if (pids.Contains(w.Current.ProcessId) && !windows.Any(x => SameRuntimeId(x, w)))
                        windows.Add(w);
                }
                catch { }
            }
        }
        catch (Exception ex) { W($"RootElement-Enumeration Fehler: {ex.Message}"); }

        W($"Zu inspizierende Fenster: {windows.Count}");

        int visited = 0; const int MaxNodes = 12000, MaxDepth = 32;
        var actionable = new List<string>();
        var keywordHits = new List<string>();
        var rx = new Regex("replay|wiedergabe|abspiel|\\bplay\\b|pause|\\bstop|stopp|geschwindig|speed|\\bladen|\\bload|download|herunter|rewind|zurueckspul|vorspul|fortschritt|progress|datum|\\bvon\\b|\\bbis\\b|zeitraum|bereich",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        void Walk(AutomationElement el, int depth, string path)
        {
            if (el == null || visited >= MaxNodes || depth > MaxDepth) return;
            visited++;
            string name = "", autoId = "", ct = "";
            try { name = el.Current.Name ?? ""; } catch { }
            try { autoId = el.Current.AutomationId ?? ""; } catch { }
            try { ct = el.Current.ControlType?.ProgrammaticName?.Replace("ControlType.", "") ?? ""; } catch { }

            var pats = SupportedPatterns(el);
            string label = $"[{ct}] Name='{Trunc(name, 48)}' AutoId='{Trunc(autoId, 40)}' pat=[{string.Join(",", pats)}]";
            string myPath = depth <= 6 ? (path.Length > 0 ? path + " > " : "") + (ct.Length > 0 ? ct : "?") : path;

            bool isActionable = pats.Contains("Invoke") || pats.Contains("Toggle") ||
                                pats.Contains("RangeValue") || (pats.Contains("Value") && ct is "Edit" or "Spinner");
            if (isActionable)
                actionable.Add($"  {label}  rect={Rect(el)}  @ {myPath}");

            if ((!string.IsNullOrEmpty(name) && rx.IsMatch(name)) ||
                (!string.IsNullOrEmpty(autoId) && rx.IsMatch(autoId)))
                keywordHits.Add($"  {label}  rect={Rect(el)}  @ {myPath}");

            AutomationElement? child = null;
            try { child = TreeWalker.ControlViewWalker.GetFirstChild(el); } catch { }
            while (child != null && visited < MaxNodes)
            {
                Walk(child, depth + 1, myPath);
                try { child = TreeWalker.ControlViewWalker.GetNextSibling(child); } catch { break; }
            }
        }

        foreach (var w in windows)
        {
            string wt = ""; try { wt = w.Current.Name; } catch { }
            W($"\n== Fenster: '{wt}' ==");
            Walk(w, 0, "");
        }

        W($"\n--- Actionable Controls (Invoke/Toggle/RangeValue/Value) : {actionable.Count} ---");
        foreach (var a in actionable.Take(200)) W(a);

        W($"\n--- Keyword-Treffer (Replay/Play/Pause/Speed/Datum/...) : {keywordHits.Count} ---");
        foreach (var k in keywordHits.Take(200)) W(k);

        W($"\n# Zusammenfassung: besuchte Knoten={visited}, actionable={actionable.Count}, keyword={keywordHits.Count}");

        if (outFile != null) { File.WriteAllText(outFile, sb.ToString()); Console.WriteLine($"[geschrieben: {outFile}]"); }
        return 0;
    }

    // ----------------------------------------------------------------------------------------------
    // 3) STEUERUNGSNACHWEIS: Play -> Fortschritt (Recorder-Dateien) -> Pause. Standard = Dry-Run (kein Klick).
    //    --go fuehrt genau EINEN kontrollierten Play/Pause-Zyklus aus (begrenzte Timeouts, max 1 Retry).
    // ----------------------------------------------------------------------------------------------
    // Aufnahme-Ausgabe: PORTABEL (kein festes Laufwerk). Primaer via Umgebungsvariable ATAS_RECORDINGS_DIR
    // (setzt die GUI je nach gewaehltem Datenordner). Ohne env ein portabler Default NEBEN der EXE
    // (<ExeDir>\ATAS_Data\Recordings). Fehlt das Ziel oder ist es nicht beschreibbar, bricht DriveReadyWritable
    // mit klarer Meldung ab (kein stilles Umlenken auf C:).
    private static readonly string RecordingsDir =
        Environment.GetEnvironmentVariable("ATAS_RECORDINGS_DIR") ?? SharedDataRoot.Read() ?? Path.Combine(AppContext.BaseDirectory, "ATAS_Data", "Recordings");

    // Laufwerk vorhanden + Ordner beschreibbar? Klarer Fehler statt stillem Fallback (portabel, kein festes F:).
    private static bool DriveReadyWritable(string dir, out string reason)
    {
        reason = "";
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(dir));
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) { reason = $"Ziellaufwerk fuer '{dir}' nicht verfuegbar."; return false; }
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".wtest_" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "ok"); File.Delete(probe);
            return true;
        }
        catch (Exception ex) { reason = $"Ordner '{dir}' nicht beschreibbar: {ex.Message}"; return false; }
    }

    // Ergebnis des zuletzt gelaufenen RunRecordingJob (fuer den Sammellauf: welche Instanz an die
    // gemeinsame CSV angehaengt werden soll). Wird am Ende jedes Auftrags gesetzt.
    private static string? _lastJobFolder; private static string _lastJobRunId = ""; private static bool _lastJobFinalizedClean;

    private static int Drive(bool go, string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }

        W($"# ATAS Drive ({(go ? "GO/live" : "DRY-RUN")})  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        var procs = Process.GetProcessesByName("OFT.Platform");
        if (procs.Length == 0) { W("BLOCKER: ATAS (OFT.Platform) laeuft nicht."); return 3; }

        // Replay-Panel-Fenster finden (Name beginnt mit "Replay").
        AutomationElement? replayWin = null;
        var pids = procs.Select(p => p.Id).ToHashSet();
        foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
        {
            try
            {
                if (!pids.Contains(w.Current.ProcessId)) continue;
                var nm = w.Current.Name ?? "";
                if (nm.StartsWith("Replay", StringComparison.OrdinalIgnoreCase)) { replayWin = w; break; }
            }
            catch { }
        }
        if (replayWin == null) { W("BLOCKER: Replay-Panel-Fenster (Name beginnt 'Replay') nicht gefunden. Replay-Panel oeffnen."); return 4; }

        string title0 = Safe(() => replayWin.Current.Name);
        W($"Replay-Fenster: '{title0}'");

        // Steuerelemente aufloesen (stabile AutomationIds bzw. Namensmuster).
        var play = FindByAutomationId(replayWin, "PlayButton");
        var speed = FindButtonByNameRegex(replayWin, new Regex(@"^\s*\d+(\.\d+)?x\s*$", RegexOptions.IgnoreCase));
        var datum = FindButtonByName(replayWin, "Datum auswählen");
        var modul = FindButtonByName(replayWin, "Modul auswählen");

        W($"Control PlayButton     : {(play != null ? "gefunden  " + PatInfo(play) : "NICHT gefunden")}");
        W($"Control Speed (NNx)    : {(speed != null ? $"gefunden '{Safe(() => speed.Current.Name)}'  " + PatInfo(speed) : "NICHT gefunden")}");
        W($"Control 'Datum auswählen': {(datum != null ? "gefunden (Range/Load)" : "nicht gefunden")}");
        W($"Control 'Modul auswählen': {(modul != null ? "gefunden (Instrument)" : "nicht gefunden")}");

        // Recorder-Kontext erfassen (aktive Instanz = juengster Ordner mit meta.json).
        var snap0 = SnapshotRecordings();
        W($"\nRecorder-Verzeichnis: {RecordingsDir}");
        W($"Vorhandene Aufnahmen: {snap0.Count}. Juengste:");
        foreach (var r in snap0.OrderByDescending(x => x.Meta).Take(3))
            W($"  {r.Name}  meta={r.Meta:HH:mm:ss}  ticks.csv={r.TicksCsv} B  instrument={r.Instrument}  startWall={r.StartWall}");

        if (play == null)
        { W("\nBLOCKER: PlayButton nicht aufloesbar -> Steuerung nicht nachweisbar. Stopp."); Flush(sb, outFile); return 5; }

        bool invokable = SupportedPatterns(play).Contains("Invoke");
        W($"\nPlayButton Invoke-Pattern: {(invokable ? "JA (steuerbar)" : "NEIN")}");

        if (!go)
        {
            W("\n[DRY-RUN] Kein Klick ausgefuehrt. Mit '--go' wird genau EIN Play->Pause-Zyklus gefahren.");
            W("Hinweis: Dieser Lauf laesst den aktuell geladenen Replay-Abschnitt unveraendert (keine Datumsauswahl).");
            Flush(sb, outFile);
            return 0;
        }

        // ---- GO: genau ein kontrollierter Zyklus ----
        if (!invokable) { W("BLOCKER: PlayButton ohne Invoke-Pattern. Stopp."); Flush(sb, outFile); return 6; }

        W("\n== Steuerungsnachweis (1 Zyklus) ==");
        W($"[t0] Zustand (Titel): '{title0}'");
        long baseBytes = snap0.OrderByDescending(x => x.Meta).FirstOrDefault()?.TicksCsv ?? -1;
        string? baseFolder = snap0.OrderByDescending(x => x.Meta).FirstOrDefault()?.Name;

        W("[play] Invoke PlayButton …");
        if (!Invoke(play)) { W("BLOCKER: Invoke(PlayButton) fehlgeschlagen. Stopp."); Flush(sb, outFile); return 7; }

        // Zwei getrennte Signale:
        //  (a) CONTROL  = Replay-Transport reagiert (Fenstertitel wechselt den Zustand) -> Steuerung wirkt.
        //  (b) CAPTURE  = Recorder schreibt tatsaechlich (neuer Ordner ODER ticks.csv waechst) -> End-to-End.
        // Auf CAPTURE warten (dateibasiert, ~25 s), Titelwechsel nur protokollieren. Max 1 Retry.
        bool control = false, capture = false; string lastTitle = title0, capEv = "";
        for (int i = 0; i < 25 && !capture; i++)
        {
            Thread.Sleep(1000);
            string t = Safe(() => replayWin.Current.Name);
            if (!string.Equals(t, lastTitle, StringComparison.Ordinal)) { W($"  [t+{i + 1}s] Titel: '{t}'"); lastTitle = t; }
            if (!string.Equals(t, title0, StringComparison.Ordinal)) control = true;
            var newest = SnapshotRecordings().OrderByDescending(x => x.Meta).FirstOrDefault();
            bool newFolder = newest != null && newest.Name != baseFolder;
            bool grew = newest != null && newest.Name == baseFolder && newest.TicksCsv > baseBytes;
            if (newFolder || grew)
            {
                capture = true;
                capEv = $"newFolder={newFolder} ({newest?.Name}), ticks.csv {baseBytes}->{newest?.TicksCsv} B";
            }
        }
        W($"[control] {(control ? $"JA – Transport reagiert (Titel '{title0}' -> '{lastTitle}')" : "NEIN – Titel unveraendert")}");
        W($"[capture] {(capture ? $"JA – Recorder schreibt: {capEv}" : "NEIN – keine Recorder-Dateien gewachsen (Recorder evtl. nicht am aktiven Chart)")}");
        bool progress = control; // Steuerungsnachweis haengt am Transport; Capture ist das zusaetzliche End-to-End-Signal.

        // Pause (zweiter Invoke auf denselben Play/Pause-Toggle).
        W("[pause] Invoke PlayButton (Pause/Toggle) …");
        Invoke(play);
        Thread.Sleep(1500);
        W($"[t2] Zustand (Titel): '{Safe(() => replayWin.Current.Name)}'");

        W("\nHinweis: Es wurde bewusst NUR Play->Pause gefahren (kein Stop/Teardown), um die geladene");
        W("Session des Nutzers nicht zu zerstoeren. 'summary.json' entsteht erst beim Replay-Stop/Dispose.");
        if (control && capture) W("\nERGEBNIS: VOLLSTAENDIG – Steuerung (Play/Pause) UND Recorder-Aufnahme nachgewiesen.");
        else if (control)       W("\nERGEBNIS: STEUERUNG nachgewiesen (Play->Pause wirkt, Transportzustand wechselt). " +
                                   "Recorder-Capture NICHT belegt -> Recorder am aktiven Replay-Chart anbringen (konkreter naechster Schritt).");
        else                    W("\nERGEBNIS: UNVOLLSTAENDIG – Transport reagierte nicht (Titel unveraendert). Stopp.");
        Flush(sb, outFile);
        return control ? 0 : 8;
    }

    // Oeffnet die Replay-Steuerung ueber den Ribbon-Button 'RibbonReplayButton' (AutomationId, kein blinder Klick).
    // Liest Toggle-Status vor/nach und meldet, ob das Replay-Transportfenster erscheint. Startet NICHT Play.
    private static int OpenReplay(string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }
        W($"# ATAS OpenReplay  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        var procs = Process.GetProcessesByName("OFT.Platform");
        if (procs.Length == 0) { W("BLOCKER: ATAS (OFT.Platform) laeuft nicht."); Flush(sb, outFile); return 3; }
        var pids = procs.Select(p => p.Id).ToHashSet();

        var existing = ReplayWindow();
        if (existing != null) { W($"[info] Replay-Fenster bereits offen: '{Safe(() => existing.Current.Name)}'. Nichts zu tun."); Flush(sb, outFile); return 0; }

        var main = TopWindows(pids).FirstOrDefault(w => Safe(() => w.Current.Name).StartsWith("ATAS -", StringComparison.OrdinalIgnoreCase));
        if (main == null) { W("BLOCKER: ATAS-Hauptfenster (Name beginnt 'ATAS -') nicht gefunden."); Flush(sb, outFile); return 4; }
        W($"Hauptfenster: '{Safe(() => main.Current.Name)}'");

        var btn = FindByAutomationId(main, "RibbonReplayButton");
        if (btn == null) { W("BLOCKER: 'RibbonReplayButton' nicht gefunden."); Flush(sb, outFile); return 4; }
        string state0 = "?";
        try { if (btn.TryGetCurrentPattern(TogglePattern.Pattern, out var tp)) state0 = ((TogglePattern)tp).Current.ToggleState.ToString(); } catch { }
        W($"RibbonReplayButton: gefunden  ToggleState(vorher)={state0}  pat=[{string.Join(",", SupportedPatterns(btn))}]");

        var before = TopWindows(pids).Count;
        W("[go] Invoke 'RibbonReplayButton' (Replay-Steuerung oeffnen) …");
        Invoke(btn);

        AutomationElement? rw = null;
        for (int i = 0; i < 12 && rw == null; i++) { Thread.Sleep(1000); rw = ReplayWindow(); }
        string state1 = "?";
        try { if (btn.TryGetCurrentPattern(TogglePattern.Pattern, out var tp2)) state1 = ((TogglePattern)tp2).Current.ToggleState.ToString(); } catch { }
        W($"ToggleState(nachher)={state1}   Top-Fenster: {before} -> {TopWindows(pids).Count}");
        if (rw != null) { W($"ERGEBNIS: Replay-Fenster offen: '{Safe(() => rw.Current.Name)}'"); Flush(sb, outFile); return 0; }
        W("ERGEBNIS: Kein Replay-Fenster erschienen (ggf. Dropdown statt Toggle). Stopp.");
        Flush(sb, outFile);
        return 8;
    }

    // Inventarisiert ein benanntes ATAS-Top-Fenster vollstaendig (z.B. 'Indikatoren') – reine Inspektion.
    private static int DumpWindow(string winName, string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }
        W($"# ATAS DumpWindow '{winName}'  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        var pids = Process.GetProcessesByName("OFT.Platform").Select(p => p.Id).ToHashSet();
        var win = TopWindows(pids).FirstOrDefault(w => Safe(() => w.Current.Name).Equals(winName, StringComparison.OrdinalIgnoreCase))
               ?? TopWindows(pids).FirstOrDefault(w => Safe(() => w.Current.Name).Contains(winName, StringComparison.OrdinalIgnoreCase));
        if (win == null) { W($"Fenster '{winName}' nicht gefunden (offen?)."); Flush(sb, outFile); return 4; }
        W($"Fenster: '{Safe(() => win.Current.Name)}' Id='{Safe(() => win.Current.AutomationId)}'");
        int n = 0; DumpDialog(win, 0, W, ref n);
        W($"\n[info] Knoten={n}");
        Flush(sb, outFile);
        return 0;
    }

    // Entfernt im offenen 'Indikatoren'-Dialog genau die 'Hinzugefuegt'-Zeile, deren Name das Muster enthaelt.
    // Sucht das Entfernen-Element (Button/X) in/neben der Zeile; DRY-RUN ohne --go. Danach 'Uebernehmen'.
    private static int RemoveIndicator(string namePart, bool go, string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }
        W($"# ATAS RemoveIndicator '{namePart}' ({(go ? "GO" : "DRY-RUN")})  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        var pids = Process.GetProcessesByName("OFT.Platform").Select(p => p.Id).ToHashSet();
        var win = TopWindows(pids).FirstOrDefault(w => Safe(() => w.Current.Name).Equals("Indikatoren", StringComparison.OrdinalIgnoreCase));
        if (win == null) { W("BLOCKER: 'Indikatoren'-Dialog nicht offen."); Flush(sb, outFile); return 4; }

        // Zeile in der 'Hinzugefuegt'-Liste finden (ListItem/DataItem/Text, deren Name das Muster + typisch '(Bars' enthaelt).
        var rows = win.FindAll(TreeScope.Descendants, Condition.TrueCondition);
        AutomationElement? row = null;
        foreach (AutomationElement e in rows)
        {
            var nm = Safe(() => e.Current.Name); var ct = Safe(() => e.Current.ControlType?.ProgrammaticName ?? "");
            if (nm.Contains(namePart, StringComparison.OrdinalIgnoreCase) && nm.Contains("Recorder", StringComparison.OrdinalIgnoreCase)
                && (ct.Contains("ListItem") || ct.Contains("DataItem") || ct.Contains("Text") || ct.Contains("Custom")))
            { row = e; W($"[zeile] '{Trunc(nm,50)}' ct={ct.Replace("ControlType.","")} rect={Rect(e)}"); break; }
        }
        if (row == null) { W($"BLOCKER: Zeile mit '{namePart}' in 'Hinzugefuegt' nicht gefunden."); Flush(sb, outFile); return 4; }

        // Entfernen-Element: Button innerhalb/neben der Zeile (X/Trash). Zuerst Nachkommen, dann Geschwister rechts.
        AutomationElement? rm = null;
        foreach (AutomationElement b in row.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
        { var h = Safe(() => b.Current.HelpText); var id = Safe(() => b.Current.AutomationId); W($"  [row-btn] Id='{id}' Help='{Trunc(h,30)}' rect={Rect(b)}"); if (rm == null) rm = b; }
        // Parent-Container nach Button rechts der Zeile durchsuchen (gleiche Y, groesseres X)
        if (rm == null)
        {
            System.Windows.Rect rr; try { rr = row.Current.BoundingRectangle; } catch { rr = System.Windows.Rect.Empty; }
            var parent = TreeWalker.ControlViewWalker.GetParent(row);
            if (parent != null && !rr.IsEmpty)
                foreach (AutomationElement b in parent.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
                {
                    System.Windows.Rect br2; try { br2 = b.Current.BoundingRectangle; } catch { continue; }
                    if (br2.IsEmpty) continue;
                    if (Math.Abs(br2.Y - rr.Y) < rr.Height && br2.X > rr.X + rr.Width * 0.6)
                    { W($"  [neben-btn] Id='{Safe(()=>b.Current.AutomationId)}' Help='{Trunc(Safe(()=>b.Current.HelpText),30)}' rect={Rect(b)}"); if (rm == null) rm = b; }
                }
        }
        if (rm == null) { W("[hinweis] Kein Entfernen-Button per UIA gefunden. (Zeile ggf. per Hover-X oder Kontextmenue entfernbar.)"); }
        else W($"[entfernen-kandidat] rect={Rect(rm)} pat=[{string.Join(",", SupportedPatterns(rm))}]");

        if (!go) { W("\n[DRY-RUN] Nichts geklickt. Mit --go wird der Entfernen-Kandidat + 'Übernehmen' ausgeloest."); Flush(sb, outFile); return 0; }
        if (rm == null) { W("BLOCKER: ohne Entfernen-Element kein --go moeglich."); Flush(sb, outFile); return 5; }
        W("[go] Invoke Entfernen …"); Invoke(rm); Thread.Sleep(800);
        var apply = FindButtonByName(win, "Übernehmen");
        if (apply != null) { W("[go] Invoke 'Übernehmen' …"); Invoke(apply); Thread.Sleep(1200); }
        else W("[warn] 'Übernehmen' nicht gefunden.");
        Flush(sb, outFile);
        return 0;
    }

    // EIN Prozess: Indikatoren-Button des sichtbaren Charts klicken -> Dialog -> Recorder-Zeile in 'Hinzugefuegt'
    // finden -> Entfernen-Element (UIA-Button, sonst Hover-X an verifizierter Zeilenposition) -> 'Übernehmen'.
    // Ohne --go reine Inspektion inkl. Volldump. Zweck: genau EINE Recorder-Instanz sauber beenden (OnDispose->summary.json).
    private static int EndRecorder(bool go, string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }
        W($"# ATAS EndRecorder ({(go ? "GO" : "DRY-RUN")})  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        var pids = Process.GetProcessesByName("OFT.Platform").Select(p => p.Id).ToHashSet();

        var btn = FindInRoots(TopWindows(pids), ControlType.Button)
            .Where(b => { var n = Safe(() => b.Current.Name); var id = Safe(() => b.Current.AutomationId); return n == "Indikatoren" || id.Contains("IndicatorsButton", StringComparison.OrdinalIgnoreCase); })
            .Select(b => { System.Windows.Rect r; try { r = b.Current.BoundingRectangle; } catch { r = System.Windows.Rect.Empty; } return (b, r); })
            .Where(t => !t.r.IsEmpty && t.r.Y >= 0).OrderBy(t => t.r.Y).Select(t => t.b).FirstOrDefault();
        if (btn == null) { W("BLOCKER: 'Indikatoren'-Button nicht gefunden."); Flush(sb, outFile); return 4; }
        W("[open] Klick 'Indikatoren' …"); ClickElement(btn); Thread.Sleep(1800);

        var win = TopWindows(pids).FirstOrDefault(w => Safe(() => w.Current.Name).Equals("Indikatoren", StringComparison.OrdinalIgnoreCase));
        if (win == null) { W("BLOCKER: 'Indikatoren'-Dialog nicht erschienen."); Flush(sb, outFile); return 4; }
        W($"[dialog] offen: '{Safe(() => win.Current.Name)}'");

        // Recorder-Zeile in der 'Hinzugefuegt'-Liste (unterer Bereich, groessere Y) finden.
        AutomationElement? row = null; System.Windows.Rect rr = System.Windows.Rect.Empty;
        foreach (AutomationElement e in win.FindAll(TreeScope.Descendants, Condition.TrueCondition))
        {
            var nm = Safe(() => e.Current.Name);
            if (!nm.Contains("Recorder", StringComparison.OrdinalIgnoreCase)) continue;
            System.Windows.Rect r; try { r = e.Current.BoundingRectangle; } catch { r = System.Windows.Rect.Empty; }
            var ct = Safe(() => e.Current.ControlType?.ProgrammaticName ?? "").Replace("ControlType.", "");
            W($"  [recorder-node] '{Trunc(nm,46)}' ct={ct} rect={Rect(e)} pat=[{string.Join(",", SupportedPatterns(e))}]");
            // 'Hinzugefuegt'-Zeile = die mit '(Bars' und groesserem Y (unten), nicht der Katalog-Eintrag oben.
            if (nm.Contains("(", StringComparison.Ordinal) && !r.IsEmpty && r.Y > rr.Y) { row = e; rr = r; }
        }
        if (row == null) { W("[hinweis] Keine 'Hinzugefuegt'-Recorder-Zeile ((Bars…)) eindeutig gefunden."); }
        else W($"[zeile] gewaehlt rect={(int)rr.X},{(int)rr.Y},{(int)rr.Width}x{(int)rr.Height}");

        // Entfernen-Button in/neben der Zeile?
        AutomationElement? rm = null;
        if (row != null)
            foreach (AutomationElement b in row.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
            { W($"  [row-btn] Id='{Safe(()=>b.Current.AutomationId)}' Help='{Trunc(Safe(()=>b.Current.HelpText),28)}' rect={Rect(b)}"); if (rm == null) rm = b; }

        if (!go)
        {
            if (outFile != null) { var png = outFile.Replace(".txt", ".png"); Shot(0, 0, GetSystemMetrics(0), GetSystemMetrics(1), png); W($"[shot] {png}"); }
            W("\n[DRY-RUN] Nichts entfernt. Mit --go: Entfernen (UIA-Button, sonst Hover-X am rechten Zeilenrand) + 'Übernehmen'.");
            Flush(sb, outFile); return 0;
        }

        if (rm != null) { W("[go] Invoke Row-Entfernen-Button …"); Invoke(rm); Thread.Sleep(700); }
        else if (!rr.IsEmpty)
        {
            // Hover auf Zeile (X erscheint), dann Klick an verifizierter Position am rechten Rand der Zeile.
            int hx = (int)(rr.X + rr.Width - 14), hy = (int)(rr.Y + rr.Height / 2);
            W($"[go] Kein UIA-Button -> Hover Zeile + Klick Hover-X @ ({hx},{hy}) …");
            Foreground(win); SetCursorPos((int)(rr.X + rr.Width / 2), hy); Thread.Sleep(500); SetCursorPos(hx, hy); Thread.Sleep(500);
            ClickScreen(hx, hy); Thread.Sleep(800);
        }
        else { W("BLOCKER: weder Button noch Zeilenrect -> kein sicheres Entfernen."); Flush(sb, outFile); return 5; }

        var apply = FindButtonByName(win, "Übernehmen");
        if (apply != null) { W("[go] Invoke 'Übernehmen' …"); Invoke(apply); Thread.Sleep(1500); }
        else W("[warn] 'Übernehmen' nicht gefunden (Entfernen ggf. nicht angewendet).");
        if (outFile != null) { var png = outFile.Replace(".txt", ".png"); Shot(0, 0, GetSystemMetrics(0), GetSystemMetrics(1), png); W($"[shot] {png}"); }
        Flush(sb, outFile);
        return 0;
    }

    // Kontrollierte, VERIFIZIERTE On-Screen-Interaktion an genau einer Koordinate (aus zuvor gesehenem Screenshot):
    //   poke <hover|click|rclick> <x> <y> [region x0 y0 x1 y1] [out.png]
    // Bringt ATAS in den Vordergrund, fuehrt die eine Aktion aus und macht danach einen (ggf. Zoom-)Screenshot.
    private static int Poke(string[] args)
    {
        string act = args.Skip(1).FirstOrDefault(a => a is "hover" or "click" or "rclick" or "dclick") ?? "hover";
        var nums = args.Where(a => int.TryParse(a, out _)).Select(int.Parse).ToList();
        if (nums.Count < 2) { Console.WriteLine("poke <hover|click|rclick> <x> <y> [x0 y0 x1 y1] [out.png]"); return 2; }
        int x = nums[0], y = nums[1];
        string? png = args.FirstOrDefault(a => a.EndsWith(".png"));
        var pids = Process.GetProcessesByName("OFT.Platform").Select(p => p.Id).ToHashSet();
        var main = TopWindows(pids).FirstOrDefault(w => Safe(() => w.Current.Name).StartsWith("ATAS -", StringComparison.OrdinalIgnoreCase));
        if (main != null) Foreground(main);
        Thread.Sleep(300);
        Console.WriteLine($"[poke] {act} @ ({x},{y})");
        if (act == "hover") { SetCursorPos(x, y); Thread.Sleep(700); }
        else if (act == "click") { SetCursorPos(x, y); Thread.Sleep(300); ClickScreen(x, y); Thread.Sleep(800); }
        else if (act == "dclick") { SetCursorPos(x, y); Thread.Sleep(250); DoubleClickFast(x, y); Thread.Sleep(900); }
        else if (act == "rclick") { RightClickScreen(x, y); Thread.Sleep(800); }
        if (png != null)
        {
            if (nums.Count >= 6) { int x0 = nums[2], y0 = nums[3], x1 = nums[4], y1 = nums[5]; Shot(x0, y0, x1 - x0, y1 - y0, png); }
            else Shot(0, 0, GetSystemMetrics(0), GetSystemMetrics(1), png);
            Console.WriteLine($"[shot] {png}");
        }
        return 0;
    }

    // Diagnose: Rect aller ATAS-Top-Fenster + Screenshot der Replay-Fenster-Region (Transport evtl. hinter Hauptfenster).
    private static int ReplayRect(string? png)
    {
        var roots = AtasRoots().ToList();
        foreach (var r in roots)
        {
            string nm = Safe(() => r.Current.Name);
            System.Windows.Rect rc; try { rc = r.Current.BoundingRectangle; } catch { rc = System.Windows.Rect.Empty; }
            int h = 0; try { h = r.Current.NativeWindowHandle; } catch { }
            Console.WriteLine($"[win] '{nm}' hwnd={h} rect={(rc.IsEmpty ? "empty" : $"{(int)rc.X},{(int)rc.Y},{(int)rc.Width}x{(int)rc.Height}")}");
        }
        var rw = ReplayWindow();
        if (rw != null)
        {
            try { ForceForeground(rw); } catch { }
            Thread.Sleep(400);
            System.Windows.Rect rc; try { rc = rw.Current.BoundingRectangle; } catch { rc = System.Windows.Rect.Empty; }
            Console.WriteLine($"[replay] rect={(rc.IsEmpty ? "empty" : $"{(int)rc.X},{(int)rc.Y},{(int)rc.Width}x{(int)rc.Height}")}");
            if (png != null && !rc.IsEmpty) { Shot((int)rc.X, (int)rc.Y, (int)rc.Width, (int)rc.Height, png); Console.WriteLine($"[shot] {png}"); }
            else if (png != null) { Shot(0, 0, GetSystemMetrics(0), GetSystemMetrics(1), png); Console.WriteLine($"[shot-full] {png}"); }
        }
        return 0;
    }

    // Diagnose: alle Buttons/Edits/Checkboxes ueber ALLE ATAS-Fenster (Name|AutoId|Typ|enabled|rect) — findet Transport.
    private static int AllCtrls(string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }
        W($"# ATAS AllCtrls {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        var roots = AtasRoots().ToList();
        W($"[roots] {roots.Count} Top-Fenster: {string.Join(" | ", roots.Select(r => Safe(() => r.Current.Name)))}");
        string? filter = Environment.GetEnvironmentVariable("ALLCTRLS_FILTER");
        foreach (var ct in new[] { ControlType.Button, ControlType.Edit, ControlType.CheckBox, ControlType.ComboBox,
                                   ControlType.TabItem, ControlType.ListItem, ControlType.Text, ControlType.Custom })
        {
            foreach (var el in FindInRoots(roots, ct))
            {
                string nm = Safe(() => el.Current.Name), aid = Safe(() => el.Current.AutomationId);
                if (string.IsNullOrWhiteSpace(nm) && string.IsNullOrWhiteSpace(aid)) continue;
                if (filter != null && !((nm ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase) || (aid ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase))) continue;
                bool en = IsEnabledSafe(el);
                W($"[{ct.ProgrammaticName.Replace("ControlType.", "")}] name='{nm}' aid='{aid}' enabled={en} rect={Rect(el)}");
            }
        }
        Flush(sb, outFile);
        return 0;
    }

    // typeat <x> <y> <text> [--clear] [out.png] : fokussiert ATAS, klickt (x,y), tippt Text (optional Feld leeren), Screenshot.
    private static int TypeAt(string[] args)
    {
        if (args.Length < 4 || !int.TryParse(args[1], out int x) || !int.TryParse(args[2], out int y))
        { Console.WriteLine("typeat <x> <y> <text> [--clear] [out.png]"); return 2; }
        string text = args[3];
        bool clear = args.Contains("--clear");
        string? png = args.FirstOrDefault(a => a.EndsWith(".png"));
        var pids = Process.GetProcessesByName("OFT.Platform").Select(p => p.Id).ToHashSet();
        var main = TopWindows(pids).FirstOrDefault(w => Safe(() => w.Current.Name).StartsWith("ATAS -", StringComparison.OrdinalIgnoreCase));
        if (main != null) Foreground(main);
        Thread.Sleep(300);
        SetCursorPos(x, y); Thread.Sleep(200); ClickScreen(x, y); Thread.Sleep(450);
        if (clear)
        {
            keybd_event(0x11, 0, 0, UIntPtr.Zero); keybd_event(0x41, 0, 0, UIntPtr.Zero);   // Ctrl down, A down
            keybd_event(0x41, 0, 2, UIntPtr.Zero); keybd_event(0x11, 0, 2, UIntPtr.Zero);   // A up, Ctrl up
            Thread.Sleep(120);
            keybd_event(0x2E, 0, 0, UIntPtr.Zero); keybd_event(0x2E, 0, 2, UIntPtr.Zero);   // Delete
            Thread.Sleep(120);
        }
        Console.WriteLine($"[typeat] '{text}' @ ({x},{y}) clear={clear}");
        TypeUnicode(text);
        Thread.Sleep(600);
        if (png != null) { Shot(0, 0, GetSystemMetrics(0), GetSystemMetrics(1), png); Console.WriteLine($"[shot] {png}"); }
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    // Erzwingt zuverlaessig den Vordergrund (SetForegroundWindow wird sonst oft geblockt): AttachThreadInput + Alt-Trick.
    private static bool ForceForeground(AutomationElement el)
    {
        try
        {
            var top = TopLevelOf(el) ?? el;
            int h = 0; try { h = top.Current.NativeWindowHandle; } catch { }
            if (h == 0) return false;
            var hwnd = new IntPtr(h);
            var fg = GetForegroundWindow();
            uint fgThread = GetWindowThreadProcessId(fg, out _);
            uint myThread = GetCurrentThreadId();
            uint tgtThread = GetWindowThreadProcessId(hwnd, out _);
            AttachThreadInput(myThread, fgThread, true); AttachThreadInput(myThread, tgtThread, true);
            keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero); // Alt tap -> Foreground-Lock loesen
            ShowWindow(hwnd, 5 /*SW_SHOW*/); BringWindowToTop(hwnd); SetForegroundWindow(hwnd);
            Thread.Sleep(250);
            AttachThreadInput(myThread, fgThread, false); AttachThreadInput(myThread, tgtThread, false);
            return GetForegroundWindow() == hwnd;
        }
        catch { return false; }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct INPUT { public uint type; public KEYBDINPUT ki; public long pad; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    private const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 2, KEYEVENTF_UNICODE = 4;

    private static void SendUnicode(char c)
    {
        var dn = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE } };
        var up = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } };
        SendInput(2, new[] { dn, up }, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }
    private static void TypeUnicode(string s) { foreach (var c in s) { SendUnicode(c); Thread.Sleep(18); } }
    private static void KeyVk(byte vk, bool ctrl = false)
    {
        if (ctrl) keybd_event(0x11, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, 0, UIntPtr.Zero); Thread.Sleep(30); keybd_event(vk, 0, 2, UIntPtr.Zero);
        if (ctrl) keybd_event(0x11, 0, 2, UIntPtr.Zero);
        Thread.Sleep(120);
    }
    private static void PressTab() => KeyVk(0x09);

    private static bool SetValueOf(AutomationElement? el, string value)
    {
        if (el == null) return false;
        try { if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vp)) { var p = (ValuePattern)vp; if (p.Current.IsReadOnly) return false; p.SetValue(value); return true; } }
        catch { }
        return false;
    }

    // Findet ein offenes modales Top-Level-Fenster (Name) im ATAS-Prozess (z.B. "Datum auswählen").
    private static AutomationElement? FindModalByName(string name)
        => AtasRoots().FirstOrDefault(r => string.Equals(Safe(() => r.Current.Name), name, StringComparison.OrdinalIgnoreCase));

    // Schliesst den Datum-Dialog kontrolliert per UIA-Invoke (kein Fokus/Koordinaten noetig). true wenn danach zu.
    private static bool CloseDateDialogIfOpen(Action<string> W)
    {
        if (FindIdAny("StartEdit") == null && FindModalByName("Datum auswählen") == null) return true;
        var cancel = FindModalByName("Datum auswählen") is { } dlg
            ? dlg.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button), new PropertyCondition(AutomationElement.NameProperty, "Abbrechen")))
            : FindBtnAny("Abbrechen");
        if (cancel != null) { W("[modal] offenen 'Datum auswählen'-Dialog schließen (Abbrechen)"); Invoke(cancel); Thread.Sleep(900); }
        bool closed = FindIdAny("StartEdit") == null;
        W($"[modal] Datum-Dialog zu: {closed}");
        return closed;
    }

    // Normalisiert ATAS: offenen Datum-Dialog schließen, Replay-Toggle mit tatsaechlichem Fensterzustand abgleichen.
    private static int Normalize()
    {
        void W(string s) => Console.WriteLine(s);
        CloseDateDialogIfOpen(W);
        var btn = FindIdAny("RibbonReplayButton");
        string st = "?"; try { if (btn != null && btn.TryGetCurrentPattern(TogglePattern.Pattern, out var tp)) st = ((TogglePattern)tp).Current.ToggleState.ToString(); } catch { }
        bool win = ReplayWindow() != null;
        W($"[normalize] RibbonReplay toggle={st} replayWin={(win ? "offen" : "zu")}");
        // Verlaessliches Signal fuer 'Replay aktiv' = ReplayWindow vorhanden (nicht der Fenstertitel, nicht nur der Toggle).
        if (win) { W("[normalize] Replay ist aktiv -> ReplayWindow offen (lasse unveraendert; autorun steuert selbst)."); }
        else W("[normalize] Replay inaktiv (kein Replay-Fenster).");
        int locked = Directory.Exists(RecordingsDir) ? Directory.GetDirectories(RecordingsDir).Count(d => IsMetaLocked(Path.Combine(d, "meta.json"))) : 0;
        W($"[normalize] aktive (gesperrte) Recorder-Instanzen: {locked}");
        return 0;
    }

    // UNBEDINGTER Toggle des RibbonReplayButton (fuer Desync-Reparatur: ToggleState vs. tatsaechlicher Wiedergabemodus).
    private static int ReplayToggle()
    {
        var pids = Process.GetProcessesByName("OFT.Platform").Select(p => p.Id).ToHashSet();
        var main = TopWindows(pids).FirstOrDefault(w => Safe(() => w.Current.Name).StartsWith("ATAS", StringComparison.OrdinalIgnoreCase));
        string title0 = main != null ? Safe(() => main.Current.Name) : "?";
        var btn = FindByAutomationId(main!, "RibbonReplayButton") ?? FindIdAny("RibbonReplayButton");
        if (btn == null) { Console.WriteLine("RibbonReplayButton nicht gefunden."); return 4; }
        string st0 = "?"; try { if (btn.TryGetCurrentPattern(TogglePattern.Pattern, out var tp)) st0 = ((TogglePattern)tp).Current.ToggleState.ToString(); } catch { }
        Console.WriteLine($"[toggle] vorher title='{title0}' toggle={st0}");
        Invoke(btn); Thread.Sleep(2500);
        var main2 = TopWindows(Process.GetProcessesByName("OFT.Platform").Select(p => p.Id).ToHashSet()).FirstOrDefault(w => Safe(() => w.Current.Name).StartsWith("ATAS", StringComparison.OrdinalIgnoreCase));
        string title1 = main2 != null ? Safe(() => main2.Current.Name) : "?";
        string st1 = "?"; try { var b2 = FindIdAny("RibbonReplayButton"); if (b2 != null && b2.TryGetCurrentPattern(TogglePattern.Pattern, out var tp2)) st1 = ((TogglePattern)tp2).Current.ToggleState.ToString(); } catch { }
        Console.WriteLine($"[toggle] nachher title='{title1}' toggle={st1}  replayWin={(ReplayWindow() == null ? "zu" : "offen")}");
        return 0;
    }

    // Verlaesst den Replay-Modus (RibbonReplayButton On->Off), damit Indikator-Aenderungen wieder moeglich sind.
    private static int ReplayOff(string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }
        var pids = Process.GetProcessesByName("OFT.Platform").Select(p => p.Id).ToHashSet();
        var main = TopWindows(pids).FirstOrDefault(w => Safe(() => w.Current.Name).StartsWith("ATAS -", StringComparison.OrdinalIgnoreCase));
        if (main == null) { W("BLOCKER: Hauptfenster nicht gefunden."); Flush(sb, outFile); return 4; }
        var btn = FindByAutomationId(main, "RibbonReplayButton");
        if (btn == null) { W("BLOCKER: RibbonReplayButton nicht gefunden."); Flush(sb, outFile); return 4; }
        string st = "?"; try { if (btn.TryGetCurrentPattern(TogglePattern.Pattern, out var tp)) st = ((TogglePattern)tp).Current.ToggleState.ToString(); } catch { }
        W($"ToggleState(vorher)={st}");
        if (st == "On") { Invoke(btn); Thread.Sleep(2500); }
        string st2 = "?"; try { if (btn.TryGetCurrentPattern(TogglePattern.Pattern, out var tp2)) st2 = ((TogglePattern)tp2).Current.ToggleState.ToString(); } catch { }
        var rw = ReplayWindow();
        W($"ToggleState(nachher)={st2}  Replay-Fenster {(rw == null ? "GESCHLOSSEN (Live)" : "noch offen")}");
        Flush(sb, outFile);
        return rw == null ? 0 : 8;
    }

    // A1 (Finding 1): Fenster-GEBUNDENER Marktfortschritt = spaetester tMarket ueber die kontinuierlichen Streams,
    // ABER nur Zeitstempel innerhalb [winStart-Slack, winEnd+Slack]. So koennen weder ein Live-Ereignis (heutiges
    // Datum) noch ein ferner Preload-Zeitstempel die Enderreichung vortaeuschen. Kein Trade exakt an der Grenze noetig.
    private const int ProgressSlackSec = 5;
    private static DateTime? ReplayProgress(Dictionary<string, Sstat> s, DateTime winStartUtc, DateTime winEndUtc)
    {
        var lo = winStartUtc.ToUniversalTime().AddSeconds(-ProgressSlackSec);
        var hi = winEndUtc.ToUniversalTime().AddSeconds(ProgressSlackSec);
        DateTime? m = null;
        foreach (var k in new[] { "dom.ndjson", "bbo.ndjson", "cumtrades.ndjson", "ticks.ndjson" })
            if (s.TryGetValue(k, out var v) && v.LastMt is { } lm)
            {
                var u = lm.ToUniversalTime();
                if (u < lo || u > hi) continue;         // Live/ferner Preload ausgeschlossen (nicht dem Fenster zugeordnet)
                if (m == null || u > m) m = u;
            }
        return m;
    }

    // (GenuineEndReached entfernt — ersetzt durch PlayedEndReached, das Herkunft über tRec>=Play bindet, Recheck Finding 1.)
    // A4: einzelnes Feld aus meta.json lesen (String-Wert).
    private static string ReadMetaField(string metaPath, string field)
    {
        try { var t = ReadAllShared(metaPath); var m = Regex.Match(t, "\"" + Regex.Escape(field) + "\"\\s*:\\s*\"([^\"]*)\""); return m.Success ? m.Groups[1].Value : ""; }
        catch { return ""; }
    }

    // Offline-REGRESSION der Part-A-Entscheidungslogik gegen den TATSAECHLICHEN Produktionsablauf:
    // Datei-Fixtures auf Platte -> echte FolderStreams() -> echte ReplayProgress/GenuineEndReached/EndReached
    // (A1), echte Kandidaten-/Token-Zuordnung + AttributionVerdict (A4), echte RecorderFinalizedClean (A2).
    // Keine isolierten booleschen Helfer als Ersatz fuer den Ablauf. Exitcode != 0 bei Fehler.
    private static int RunRegress(string? arg)
    {
        int fails = 0;
        void Check(string name, bool ok, string detail = "") { Console.WriteLine($"  [{(ok ? "OK  " : "FAIL")}] {name}{(detail == "" ? "" : "  -> " + detail)}"); if (!ok) fails++; }
        // Fixtures standardmaessig neben die Assembly (auf A:), NICHT nach C:-Temp (Projektvorgabe).
        var baseDir = !string.IsNullOrWhiteSpace(arg) ? arg!
            : Path.Combine(AppContext.BaseDirectory, "regress_tmp_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        if (Directory.Exists(baseDir)) Directory.Delete(baseDir, true);
        Directory.CreateDirectory(baseDir);
        Console.WriteLine($"# ATAS Probe Regression (Findings 1/2/4)  fixtures={baseDir}");

        var start = new DateTime(2026, 10, 26, 8, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 10, 26, 8, 1, 0, DateTimeKind.Utc);

        // Fixture-Helfer: schreibt eine NDJSON-Zeile je Stream mit gegebenem tMarket (letzte Zeile zaehlt fuer LastMt).
        string IsoUtc(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
        string Fx(string name) { var d = Path.Combine(baseDir, name); Directory.CreateDirectory(d); return d; }
        void PutStream(string dir, string stream, string lastTMarketIso)
            => File.WriteAllText(Path.Combine(dir, stream), "{\"tMarket\":\"" + lastTMarketIso + "\"}\n");
        void PutMeta(string dir, string instrument, string runId)
            => File.WriteAllText(Path.Combine(dir, "meta.json"),
                "{\"instrument\":\"" + instrument + "\",\"startWallUtc\":\"" + IsoUtc(DateTime.UtcNow) + "\",\"runId\":\"" + runId + "\"}");

        // ---- A1 (Recheck2): Erfolg NUR aus verifiziertem Replay-Signal; tMarket/tRecv sind KEIN Herkunftsbeweis ----
        Console.WriteLine("# A1 Enderkennung (DecideEnd/AssessEnd: verifiziertes Replay-Signal; Daten nur Protokoll)");
        var play = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);          // Play-Wanduhr-Referenz
        string playedRecv = IsoUtc(play.AddSeconds(5));                             // nach Play empfangen
        var loadedEnd = new DateTime(2026, 10, 26, 8, 4, 0, DateTimeKind.Utc);      // tatsächlich geladene Spanne (Ende+3min)
        void PutTR(string dir, string stream, string tMarketIso, string tRecvIso)
            => File.WriteAllText(Path.Combine(dir, stream), "{\"tMarket\":\"" + tMarketIso + "\",\"tRecv\":\"" + tRecvIso + "\"}\n");

        // (i) CODEX-GEGENBEISPIEL: nach Play empfangener Preload am Ende -> Daten-Hinweis true, aber Verdikt Unproven (KEIN falscher Erfolg).
        var pre = Fx("a1_post_play_preload");
        PutTR(pre, "bbo.ndjson", "2026-10-26T08:01:00Z", playedRecv);
        Check("Preload nach Play: Daten-Hinweis true ABER DecideEnd=Unproven (Daten nicht als Erfolg)",
              PlayedBoundaryDataHint(pre, play, end, loadedEnd)
              && DecideEnd(pre, play, end, loadedEnd, null, true, _ => { }) == EndVerdict.Unproven);
        // (ii) Ende+10min: ausserhalb der geladenen Spanne -> nicht einmal Daten-Hinweis; Verdikt Unproven.
        var p10 = Fx("a1_end_plus_10");
        PutTR(p10, "bbo.ndjson", "2026-10-26T08:11:00Z", playedRecv);
        Check("Ende+10min ausserhalb geladener Spanne -> kein Daten-Hinweis", !PlayedBoundaryDataHint(p10, play, end, loadedEnd));
        Check("Ende+10min -> DecideEnd=Unproven", DecideEnd(p10, play, end, loadedEnd, null, true, _ => { }) == EndVerdict.Unproven);
        // (iii) kein verifiziertes Signal -> Unproven (auch bei Grenzdaten); (iv) nicht playing -> Unproven.
        Check("kein verifiziertes Replay-Signal -> Unproven", AssessEnd(null, true, end) == EndVerdict.Unproven);
        Check("Signal vorhanden aber nicht playing -> Unproven", AssessEnd(end, false, end) == EndVerdict.Unproven);
        // (v) VERIFIZIERTES Signal >= Ende und playing -> Reached (Erfolgspfad existiert, wenn ein echtes Signal vorliegt).
        Check("verifiziertes Replay-Signal >= Ende + playing -> Reached", AssessEnd(end, true, end) == EndVerdict.Reached);
        // (vi) verifiziertes Signal < Ende -> StoppedBeforeEnd (kein Trade an der Grenze nötig, aber Grenze nicht erreicht).
        Check("verifiziertes Signal < Ende -> StoppedBeforeEnd", AssessEnd(end.AddSeconds(-1), true, end) == EndVerdict.StoppedBeforeEnd);

        // ---- A4 (Recheck2): erzwungener Ein-Recorder-Betrieb + Token; ein atomarer Gewinner ist NICHT der richtige Gewinner ----
        Console.WriteLine("# A4 Zuordnung (genau EINE aktive Instanz, neu, Auftragstoken; fremder Token-Gewinner abgelehnt)");
        string jobToken = Guid.NewGuid().ToString("N");
        List<(string runId, bool isNew)> L(params (string, bool)[] xs) => new List<(string runId, bool isNew)>(xs.Select(x => (x.Item1, x.Item2)));
        // CODEX-GEGENBEISPIEL: fremder Consume gewinnt das Token, Zielinstanz bekommt null -> ZWEI aktive Instanzen -> Ambiguous (abgelehnt).
        Check("foreign-first (2 aktive Instanzen) -> Ambiguous (abgelehnt)",
              AttributionVerdict(L((jobToken, true), (Guid.NewGuid().ToString("N"), true)), jobToken) == Attrib.Ambiguous);
        Check("genau 1 neue Instanz mit Auftragstoken -> Unique", AttributionVerdict(L((jobToken, true)), jobToken) == Attrib.Unique);
        Check("1 Instanz, fremdes Token -> None", AttributionVerdict(L((Guid.NewGuid().ToString("N"), true)), jobToken) == Attrib.None);
        Check("1 Instanz mit Token, aber nicht neu -> None", AttributionVerdict(L((jobToken, false)), jobToken) == Attrib.None);
        Check("0 aktive Instanzen -> None", AttributionVerdict(L(), jobToken) == Attrib.None);
        Check("2 Instanzen (beide Token) -> Ambiguous", AttributionVerdict(L((jobToken, true), (jobToken, true)), jobToken) == Attrib.Ambiguous);
        Check("leeres Auftragstoken -> None", AttributionVerdict(L((jobToken, true)), "") == Attrib.None);
        // ECHTES Codex-Szenario über den tatsächlichen TokenHandshake: Publish -> fremder Consume -> Ziel-Consume(null).
        var thRoot = Fx("a4_foreign_first");
        OrderFlowTools.TokenHandshake.Publish(thRoot, jobToken);
        var foreignGot = OrderFlowTools.TokenHandshake.Consume(thRoot, "foreign");   // fremd gewinnt
        var targetGot = OrderFlowTools.TokenHandshake.Consume(thRoot, "target");     // Ziel bekommt null
        var instances = L((foreignGot ?? "", true), (targetGot ?? Guid.NewGuid().ToString("N"), true));
        Check("Codex foreign-first via TokenHandshake -> Ambiguous (NICHT Unique)",
              foreignGot == jobToken && targetGot == null && AttributionVerdict(instances, jobToken) == Attrib.Ambiguous,
              $"foreign={foreignGot ?? "null"} target={targetGot ?? "null"}");
        // Exklusiver Konsum + Owner-Retract (weiterhin gültig).
        var thRoot2 = Fx("a4_token_owner");
        OrderFlowTools.TokenHandshake.Publish(thRoot2, "foreign-token");
        OrderFlowTools.TokenHandshake.RetractIfOwner(thRoot2, "mine");
        Check("RetractIfOwner lässt fremdes Token stehen", File.Exists(OrderFlowTools.TokenHandshake.TokenPath(thRoot2)));

        // ---- A2 (Finding 2): Finalisierung honoriert clean + runId + Writer-Ende ----
        Console.WriteLine("# A2 Finalisierung (clean + runId + Writer beendet verbindlich)");
        string goodSummary = "{\"schemaVersion\":\"ofrec-summary-2\",\"runId\":\"" + jobToken + "\",\"clean\":true,\"writerFaulted\":false,\"joinTimedOut\":false}";
        Check("clean + passende runId + entsperrt -> finalisiert", RecorderFinalizedClean(true, false, goodSummary, jobToken));
        Check("verbleibender Writer (gesperrt) -> NICHT finalisiert", !RecorderFinalizedClean(true, true, goodSummary, jobToken));
        Check("clean=false -> NICHT finalisiert", !RecorderFinalizedClean(true, false, "{\"clean\":false,\"runId\":\"" + jobToken + "\"}", jobToken));
        Check("alte Summary ohne clean -> NICHT finalisiert", !RecorderFinalizedClean(true, false, "{\"totalEvents\":10,\"runId\":\"" + jobToken + "\"}", jobToken));
        Check("fremde runId -> NICHT finalisiert", !RecorderFinalizedClean(true, false, goodSummary, Guid.NewGuid().ToString("N")));
        Check("keine Summary -> NICHT finalisiert", !RecorderFinalizedClean(false, false, "", jobToken));
        // Recheck: Cleanup-Finalisierung ist robust — ein Fehler bei EINEM Auftrag wirft nicht, die übrigen werden geprüft.
        var jobs = new List<(string folder, string token)> { ("okFolder", jobToken), ("boomFolder", jobToken), ("badFolder", jobToken) };
        bool bothSeen = false;
        var seen = new System.Collections.Generic.HashSet<string>();
        bool robustAll = AllFinalizedClean(jobs, folder =>
        {
            seen.Add(folder);
            if (folder == "boomFolder") throw new IOException("synthetic IO error mid-cleanup");
            if (folder == "badFolder") return (true, false, "{\"totalEvents\":1}");   // alte Summary ohne clean
            return (true, false, goodSummary);                                         // ok
        }, _ => { });
        bothSeen = seen.Contains("okFolder") && seen.Contains("boomFolder") && seen.Contains("badFolder");
        Check("cleanup robust: Fehler mitten im Ablauf wirft nicht; alle Aufträge geprüft; Gesamt NICHT ok",
              !robustAll && bothSeen, $"robustAll={robustAll} seen={seen.Count}");
        bool allOk = AllFinalizedClean(new List<(string, string)> { ("a", jobToken), ("b", jobToken) },
                                       _ => (true, false, goodSummary), _ => { });
        Check("cleanup robust: alle sauber -> ok", allOk);
        // Recheck2: Pause-Fehler überspringt NICHT das Replay-Verlassen (getrennte Schritte).
        var stepsPauseFail = CleanupSteps(pause: () => throw new InvalidOperationException("synthetic pause failure"),
                                          exit: () => true, W: _ => { });
        Check("Pause-Fehler -> Replay-Verlassen dennoch versucht + erfolgreich",
              !stepsPauseFail.pauseOk && stepsPauseFail.exitAttempted && stepsPauseFail.exitOk);
        var stepsBothFail = CleanupSteps(pause: () => throw new IOException("pause"),
                                         exit: () => throw new IOException("exit"), W: _ => { });
        Check("Pause- und Exit-Fehler -> beide gemeldet, Exit versucht, nicht erfolgreich",
              !stepsBothFail.pauseOk && stepsBothFail.exitAttempted && !stepsBothFail.exitOk);

        // ---- A3 (unverändert korrekt): SummaryCleanClose ----
        Console.WriteLine("# A3 SummaryCleanClose (vorhandene Summary != sauberer Abschluss)");
        Check("clean:true -> clean", SummaryCleanClose("{\"clean\":true}"));
        Check("clean:true + joinTimedOut:true -> nicht clean", !SummaryCleanClose("{\"clean\":true,\"joinTimedOut\":true}"));
        Check("alte Summary ohne clean -> nicht belegt", !SummaryCleanClose("{\"totalEvents\":10}"));

        // ---- Verdikt-Gating (Runde 8): GESAMT-PASS nur bei Aufnahme + unabh. Endsignal + Cleanup ----
        Console.WriteLine("# Verdikt-Gating (recordingSaved & endProven & cleanupDone)");
        Check("alle Teile erfuellt -> PASS", LiveProofPass(true, true, true));
        Check("fehlendes unabh. Endsignal -> KEIN PASS", !LiveProofPass(true, false, true));
        Check("fehlgeschlagenes Recorder-Remove -> KEIN PASS", !LiveProofPass(true, true, CleanupDone(false, true)));
        Check("Replay-Ansicht nicht verlassen -> KEIN PASS", !LiveProofPass(true, true, CleanupDone(true, false)));
        Check("Aufnahme nicht sauber finalisiert -> KEIN PASS", !LiveProofPass(false, true, true));
        Check("cleanupDone nur bei remove UND view-left", CleanupDone(true, true) && !CleanupDone(true, false) && !CleanupDone(false, true));
        Check("Cleanup-Fehlschlag: Aufnahme gilt weiter als gespeichert, aber KEIN PASS (Aufnahme bleibt erhalten)",
              !LiveProofPass(recordingSaved: true, endProven: true, cleanupDone: false));

        // ---- Execution-scoped Enderkennung: Erfolg NUR mit belegter Abschlusssequenz der aktuellen Ausfuehrung ----
        Console.WriteLine("# ClassifyExecutionEnd (Abschlusssequenz erforderlich; Rewind/Daten nur unterstuetzend)");
        List<string> Lg(params string[] xs) => new List<string>(xs);
        const string PF1 = "Replay DS : Playing from 26.10.2026 08:00:00 to 26.10.2026 08:01:00 type TradesAndDepths.";
        const string STOP = "Replay : State changed Started -> Stopped.";
        const string FIN = "Replay : Data processing finished.";
        // Abschlusssequenz + Auto-Loop auf DENSELBEN Bereich -> NaturalEnd.
        Check("Abschlusssequenz + Auto-Rewind(gleicher Bereich) -> NaturalEnd",
              ClassifyExecutionEnd(Lg(PF1, STOP, FIN, "start path = rewind", PF1), false) == ReplayEndClass.NaturalEnd);
        // Abschlusssequenz + Recorder-Ende (unterstuetzend) -> NaturalEnd.
        Check("Abschlusssequenz + Recorder-Marktzeit am Ende (unterstuetzend) -> NaturalEnd",
              ClassifyExecutionEnd(Lg(PF1, STOP, FIN), true) == ReplayEndClass.NaturalEnd);
        // GEGENBEISPIEL: Rewind/2. Playing-from OHNE Abschlusssequenz -> NotFinished (Rewind allein ist kein Erfolg).
        Check("Auto-Rewind/2. Playing-from OHNE Abschlusssequenz -> NotFinished",
              ClassifyExecutionEnd(Lg(PF1, "start path = rewind", PF1), false) == ReplayEndClass.NotFinished);
        // GEGENBEISPIEL: Recorder-Ende ALLEIN (ohne Abschlusssequenz) -> NotFinished (Daten nur unterstuetzend).
        Check("Recorder-Marktzeit am Ende ALLEIN (ohne Abschlusssequenz) -> NotFinished",
              ClassifyExecutionEnd(Lg(PF1, "MNQ 5m Chart : Replay chart state: Starting -> Play"), true) == ReplayEndClass.NotFinished);
        // GEGENBEISPIEL: manueller Reset/Restart auf ANDEREN Bereich -> StoppedEarly (kein natuerliches Ende).
        Check("Reset/Restart auf anderen Bereich (2. Playing-from != Start) -> StoppedEarly",
              ClassifyExecutionEnd(Lg(PF1, STOP, FIN, "Replay DS : Playing from 26.10.2026 14:30:00 to 26.10.2026 14:31:00 type TradesAndDepths."), false) == ReplayEndClass.StoppedEarly);
        // GEGENBEISPIEL: manueller Wechsel zu Realtime -> StoppedEarly.
        Check("manueller Wechsel zu Realtime (online state) -> StoppedEarly",
              ClassifyExecutionEnd(Lg(PF1, STOP, FIN, "Replay MD : Loading historical data to online state for #MNQZ6@CME."), true) == ReplayEndClass.StoppedEarly);
        // GEGENBEISPIEL: echter MID-PLAY-Abbruch (Disconnect, Ende NICHT erreicht) -> Disconnected.
        Check("Mid-Play-Disconnect (Ende nicht erreicht) -> Disconnected",
              ClassifyExecutionEnd(Lg(PF1, "Replay : State changed Connected => Disconnecting.", "Replay : Disconnected."), false) == ReplayEndClass.Disconnected);
        // ABGRENZUNG: natuerliches Ende MIT End-/Rewind-Cache-Disconnect (Ende erreicht + Abschluss + Auto-Rewind) -> NaturalEnd.
        Check("Natuerliches Ende trotz End-/Rewind-Cache-Disconnect -> NaturalEnd",
              ClassifyExecutionEnd(Lg(PF1, "Cache1 US : Disconnecting...", "Cache1 US : Disconnected.", STOP, FIN, "start path = rewind", PF1), true) == ReplayEndClass.NaturalEnd);
        // GEGENBEISPIEL: Replay-Fehler -> Errored.
        Check("Replay-Fehler -> Errored",
              ClassifyExecutionEnd(Lg(PF1, "ERROR Replay DS : provider load failed.", STOP, FIN), true) == ReplayEndClass.Errored);
        // GEGENBEISPIEL: Stop mittig (nur Stop, keine Enderreichung) -> StoppedEarly.
        Check("Stop mittig ohne Enderreichung -> StoppedEarly",
              ClassifyExecutionEnd(Lg(PF1, STOP, FIN), false) == ReplayEndClass.StoppedEarly);
        // GEGENBEISPIEL: nur Stop ohne 'Data processing finished' -> NotFinished (unvollstaendige Abschlusssequenz).
        Check("nur '-> Stopped' ohne 'Data processing finished' -> NotFinished",
              ClassifyExecutionEnd(Lg(PF1, STOP), true) == ReplayEndClass.NotFinished);
        Check("laeuft noch (kein Stop) -> NotFinished", ClassifyExecutionEnd(Lg(PF1, "Replay chart state: Starting -> Play"), false) == ReplayEndClass.NotFinished);
        Check("leer / kein eigener Start -> NoStart", ClassifyExecutionEnd(Lg(), false) == ReplayEndClass.NoStart);
        Check("fremde Zeilen ohne 'Playing from' -> NoStart", ClassifyExecutionEnd(Lg("irgendwas", FIN), true) == ReplayEndClass.NoStart);

        // ---- TIMING: Disconnect ist Hinweis, kein Abbruch-Timer; Fortschritt bis Ende + Puffer; Abbruch nur bei Stillstand ----
        Console.WriteLine("# PlayLoopStep (Disconnect != Abbruch-Uhr; Fortschritt zaehlt; Erfolg nur per Endsignal)");
        const int SFL = 10, SETTLE = 8;
        // Szenario (Auftrag-2-Fall): Disconnect bei :45, Fortschritt (stall=0) bis :57/:59 -> waehrend des Fortschritts NIE Abbruch.
        Check("Disconnect + Fortschritt (stall=0, Ende noch nicht erreicht) -> Continue (NICHT abbrechen)",
              PlayLoopStep(ReplayEndClass.Disconnected, dataReachedEnd: false, stall: 0, SFL, SETTLE) == LoopAction.Continue);
        Check("Disconnect kurz vor Ende, leichter Stillstand (stall=5) -> weiter Continue",
              PlayLoopStep(ReplayEndClass.Disconnected, dataReachedEnd: false, stall: 5, SFL, SETTLE) == LoopAction.Continue);
        // Abschluss kommt (Ende erreicht + NaturalEnd) -> Success (auch wenn zuvor Disconnect-Hinweise kamen).
        Check("Ende erreicht + NaturalEnd -> Success", PlayLoopStep(ReplayEndClass.NaturalEnd, true, 1, SFL, SETTLE) == LoopAction.Success);
        // Tatsaechlicher Stillstand OHNE Abschluss (Ende nicht erreicht) -> begrenzt fehlschlagen (erst an der Grenze).
        Check("Stillstand < Grenze (stall=9) -> Continue", PlayLoopStep(ReplayEndClass.Disconnected, false, 9, SFL, SETTLE) == LoopAction.Continue);
        Check("Stillstand an Grenze (stall=10) ohne Abschluss -> Abort (begrenzt)", PlayLoopStep(ReplayEndClass.Disconnected, false, 10, SFL, SETTLE) == LoopAction.Abort);
        Check("Stuck ohne Daten/Abschluss (NotFinished, stall=10) -> Abort", PlayLoopStep(ReplayEndClass.NotFinished, false, 10, SFL, SETTLE) == LoopAction.Abort);
        // Ende erreicht, aber KEIN Abschluss in Frist -> Abort (Korroboration, KEIN Erfolg); Tickfortschritt allein genuegt nie.
        Check("Ende erreicht aber kein Abschluss (stall=8) -> Abort (kein Erfolg)", PlayLoopStep(ReplayEndClass.StoppedEarly, true, 8, SFL, SETTLE) == LoopAction.Abort);
        Check("Ende erreicht, Abschluss-Puffer laeuft (stall=7) -> Continue", PlayLoopStep(ReplayEndClass.StoppedEarly, true, 7, SFL, SETTLE) == LoopAction.Continue);
        // Fehler/manueller Stopp -> nie Success.
        Check("Errored -> Abort (nie Success)", PlayLoopStep(ReplayEndClass.Errored, true, 0, SFL, SETTLE) == LoopAction.Abort);
        // Progressive Snapshots: der konkrete Auftrag-2-Fall darf NICHT bei :57 abbrechen (Fortschritt, stall=0).
        bool abortedDuringProgress = false;
        foreach (var st in new[] { 0, 0, 0, 0, 0 })   // :45,:48,:51,:54,:57 jeweils mit frischem Fortschritt
            if (PlayLoopStep(ReplayEndClass.Disconnected, false, st, SFL, SETTLE) == LoopAction.Abort) abortedDuringProgress = true;
        Check("Auftrag-2-Fall: Disconnect:45 + Fortschritt bis :57 -> KEIN vorzeitiger Abbruch", !abortedDuringProgress);

        // ---- SPEED: setzen+zuruecklesen verifizieren; Watchdog an Geschwindigkeit angepasst (NICHT naiv /speed) ----
        Console.WriteLine("# Speed-Readback + Watchdog (konfigurierbare Geschwindigkeit, 100x)");
        Check("Readback '100x' == '100x' -> ok", SpeedReadbackOk("100x", "100x"));
        Check("Readback '100' ~ '100x' (normalisiert) -> ok", SpeedReadbackOk("100x", "100"));
        Check("Readback ' 100X ' ~ '100x' -> ok", SpeedReadbackOk("100x", " 100X "));
        Check("Readback '1x' != '100x' -> NICHT ok (kein stilles falsches Tempo)", !SpeedReadbackOk("100x", "1x"));
        Check("Readback leer -> NICHT ok", !SpeedReadbackOk("100x", ""));
        Check("SpeedToFactor('100x') == 100", Math.Abs(SpeedToFactor("100x") - 100.0) < 1e-9);
        Check("SpeedToFactor('1x') == 1", Math.Abs(SpeedToFactor("1x") - 1.0) < 1e-9);
        Check("SpeedToFactor(unbekannt) == 1 (Fallback)", Math.Abs(SpeedToFactor("schnell") - 1.0) < 1e-9);
        // Watchdog: fester Boden bleibt bei 100x erhalten (NICHT bloss 60/100 = <1s); nur der Marktanteil skaliert.
        Check("Watchdog 60s@1x = 200+60 = 260", PlayWatchdogSeconds(60, 1.0, 200) == 260);
        Check("Watchdog 60s@100x = 200+1 = 201 (NICHT ~0.6s)", PlayWatchdogSeconds(60, 100.0, 200) == 201);
        Check("Watchdog 100x NICHT gleich naiver Division 60/100", PlayWatchdogSeconds(60, 100.0, 200) != (int)Math.Ceiling(60 / 100.0));

        // ---- Vorpruefungen des Sammellaufs (VM-Befunde): Bridge-Selbsttest + Recorder-Ausgabeordner ----
        // ---- 1.3.4: Stop-Pfad (VM-Befund: nach erkanntem Ende Auto-Rewind-Loop, Exit-Pfad ohne Protokoll) ----
        {
            var hk = Path.Combine(baseDir, "GlobalHotKeys_test.cnf");
            File.WriteAllText(hk, "{\"StopMarketReplay\":[\"LeftCtrl\",\"LeftAlt\",\"D\"],\"DisableEnableMarketReplay\":[\"LeftCtrl\",\"LeftAlt\",\"R\"],\"Odd\":[\"LeftCtrl\",\"OemTilde\"]}");
            var stop = ReadAtasHotkey("StopMarketReplay", hk);
            Check("Stop: ATAS-Kuerzel StopMarketReplay aus Konfiguration gelesen", stop != null && string.Join("+", stop) == "LeftCtrl+LeftAlt+D");
            var vk = stop == null ? null : HotkeyToVk(stop);
            Check("Stop: Kuerzel -> Tastencodes Strg(0x11)+Alt(0x12)+D(0x44)", vk != null && vk.SequenceEqual(new byte[] { 0x11, 0x12, 0x44 }));
            Check("Stop: nicht sicher abbildbare Taste -> nicht senden", HotkeyToVk(ReadAtasHotkey("Odd", hk)!) == null);
            Check("Stop: nicht belegtes Kuerzel -> null (kein Raten)", ReadAtasHotkey("GibtEsNicht", hk) == null && ReadAtasHotkey("StopMarketReplay", Path.Combine(baseDir, "fehlt.cnf")) == null);
            File.Delete(hk);
        }
        {
            var swb = Stopwatch.StartNew();
            var r = Bounded("Test blockiert", () => { Thread.Sleep(5000); return true; }, 300, false);
            Check("Stop: blockierender Aufruf wird nach Zeitgrenze abgebrochen (Ersatzwert)", !r && swb.ElapsedMilliseconds < 1500, $"{swb.ElapsedMilliseconds} ms");
            Check("Stop: Ausnahme im Aufruf -> Ersatzwert", Bounded<int>("Test Ausnahme", () => throw new InvalidOperationException("x"), 1000, -1) == -1);
            Check("Stop: normaler Aufruf liefert Wert", Bounded("Test ok", () => 42, 1000, 0) == 42);
        }
        // ---- 1.3.7: Ende -> X der Replay-Leiste (VM 18.11.: 21 Stopp-Kuerzel, obwohl das erste wirkte) ----
        {
            AtasLogLine P(string s) => AtasLogLine.Parse(s)!;
            Check("X: Stopp-Beleg von der CHART-Quelle ('Replay chart state: LoadingModules -> Stopped', Zeile aus lokalem ATAS-Log) wird erkannt",
                  IsStopEvidence(P("2026-11-18 14:25:28,100 [2         ] INFO  MNQ 5m Chart   : Replay chart state: LoadingModules -> Stopped (connected=True, playContract=False)")));
            Check("X: Stopp-Beleg der Replay-Quelle ('State changed Started -> Stopped') wird erkannt",
                  IsStopEvidence(P("2026-11-18 14:25:28,100 [ReplayLoad] INFO  Replay         : State changed Started -> Stopped.")));
            Check("X: andere Chart-/Replay-Zeilen sind KEIN Stopp-Beleg",
                  !IsStopEvidence(P("2026-11-18 14:25:28,100 [2         ] INFO  MNQ 5m Chart   : Replay chart state: LoadingHistory -> LoadingModules (connected=True)"))
                  && !IsStopEvidence(P("2026-11-18 14:25:28,100 [2         ] INFO  Replay         : State changed Stopped -> Initializing.")));
            Check("X: Titel 'Replay - Wiedergabe gestoppt' = gestoppt; 'Module laden' / 'Historienwiedergabe' nicht",
                  TitleStopped("Replay - Wiedergabe gestoppt") && !TitleStopped("Replay - Module laden...") && !TitleStopped("Replay - Historienwiedergabe..."));
            Check("X: Schliessen-Schaltflaeche erkannt (Schließen / Close / PART_CloseButton / x), nicht 'Alle schließen' / 'Schließen und speichern'",
                  IsCloseControl("Schließen", "", "") && IsCloseControl("Close", "", "") && IsCloseControl("", "PART_CloseButton", "") && IsCloseControl("x", "", "")
                  && IsCloseControl("", "", "Schließen") && !IsCloseControl("Alle schließen", "", "") && !IsCloseControl("Schließen und speichern", "CloseAllButtonX", "")
                  && !IsCloseControl("Zur Echtzeit wechseln", "", "") && !IsCloseControl("Play", "PlayButton", ""));
            Check("X: Leiste weg + editierbar + kein Neustart -> Replay-Ansicht verlassen", JudgeAfterClose(true, true, 0, true).v == AfterCloseVerdict.Left);
            Check("X: Leiste weg, Chart NICHT editierbar -> 'X blendet nur die Leiste aus' (gemeldet, kein Erfolg)", JudgeAfterClose(true, false, 0, true).v == AfterCloseVerdict.OnlyHidden);
            Check("X: neuer Durchlauf nach dem X ohne Stopp -> Loop laeuft weiter (kein Erfolg)", JudgeAfterClose(true, true, 1, false).v == AfterCloseVerdict.LoopContinues);
            Check("X: Durchlauf nach dem X, danach Stopp + editierbar -> verlassen", JudgeAfterClose(true, true, 1, true).v == AfterCloseVerdict.Left);
            Check("X: Leiste nach dem X noch sichtbar -> kein Erfolg", JudgeAfterClose(false, true, 0, true).v == AfterCloseVerdict.BarStillVisible);
        }
        // ---- 1.3.5: Instanz-Zuordnung nach dem Laden (VM 17.11.: keine neue Instanz -> stummes Warten) ----
        {
            var d0 = Path.Combine(baseDir, "inst_leer"); Directory.CreateDirectory(d0);
            File.WriteAllText(Path.Combine(d0, "meta.json"), "{\"instrument\":\"MNQ\"}");
            File.WriteAllText(Path.Combine(d0, "ticks.ndjson"), "");
            Check("Laden: Instanz nur mit meta.json + leerem Strom = 0 Daten (darf uebernommen werden)", DataBytes(d0) == 0);
            File.WriteAllText(Path.Combine(d0, "dom.ndjson"), "{\"dataType\":\"Depth\"}\n");
            Check("Laden: Instanz mit Echtzeitdaten (beliebiger Strom) -> NICHT leer (keine Mischung)", DataBytes(d0) > 0);
            Check("Laden: fehlender Ordner -> 0 ohne Ausnahme", DataBytes(Path.Combine(baseDir, "gibtsnicht")) == 0);
            // 1.3.8: Instanz aus Schritt 3 am Folgetag (VM 18.11.: Laden + Play bereit, keine neue Instanz)
            var we = new DateTime(2026, 10, 27, 13, 32, 0, DateTimeKind.Utc);
            Check("Laden: Instanz aus Schritt 3 ohne Trades -> verwendbar", ClassifyPreload(Array.Empty<DateTime>(), we, false).ok);
            Check("Laden: nur Live-Trades (heute, weit nach dem Fenster) -> verwendbar (Bridge schliesst Live-Vorlauf aus)",
                  ClassifyPreload(new[] { new DateTime(2026, 11, 18, 15, 36, 31, DateTimeKind.Utc), new DateTime(2026, 11, 18, 15, 36, 40, DateTimeKind.Utc) }, we, false).ok);
            Check("Laden: ein Trade im Replay-Bereich vor dem Laden -> NICHT verwendbar",
                  !ClassifyPreload(new[] { new DateTime(2026, 11, 18, 15, 36, 31, DateTimeKind.Utc), new DateTime(2026, 10, 27, 13, 31, 0, DateTimeKind.Utc) }, we, false).ok);
            Check("Laden: Datenstrom zu gross fuer die Pruefung -> NICHT verwendbar", !ClassifyPreload(Array.Empty<DateTime>(), we, true).ok);
            var dl = Path.Combine(baseDir, "inst_live"); Directory.CreateDirectory(dl);
            File.WriteAllText(Path.Combine(dl, "ticks.ndjson"),
                "{\"seq\":1,\"tMarket\":\"2026-11-18T15:36:31.0000000Z\",\"tRecv\":\"2026-11-18T15:36:31.1Z\",\"dataType\":\"Trade\",\"price\":27000}\n" +
                "{\"seq\":2,\"tMarket\":\"2026-11-18T15:36:32.0000000Z\",\"dataType\":\"BestBid\",\"price\":26999}\n");
            var pl = PreloadTradesCheck(dl, we);
            Check("Laden: Datei mit 1 Live-Trade + 1 Nicht-Trade -> 1 Live-Trade erkannt, verwendbar", pl.ok && pl.why.StartsWith("1 Live-Trades"), pl.why);
            Directory.Delete(dl, true);
            Directory.Delete(d0, true);
        }
        Check("Stop: Titel 'Module laden' = Ladezustand, 'Historienwiedergabe' = spielt",
              TitleLoading("Replay - Module laden...") && !TitleLoading("Replay - Historienwiedergabe...") && TitlePlaying("Replay - Historienwiedergabe...") && !TitlePlaying("Replay - Wiedergabe gestoppt"));

        // ---- 1.3.3: Ende/Fehler-Klassifikation aus ATAS-Protokollzeilen (Format wie im echten app_*.log) ----
        string Ln(string t, string th, string lvl, string src, string msg) => $"2026-11-12 {t} [{th,-10}] {lvl,-5} {src,-15}: {msg}";
        string PF(string t, string d = "26.10.2026") => Ln(t, "2", "INFO", "Replay DS", $"Playing from {d} 13:30:00 to {d} 16:00:00 type CandlesAndGeneratedBestPrices.");
        var natural = new[]
        {
            PF("20:29:10,147"),
            Ln("20:29:10,147", "ReplayLoad", "INFO", "Replay", "State changed Initialized -> Started."),
            Ln("20:29:56,597", "29", "INFO", "Cache1 US", "Disconnecting..."),
            Ln("20:29:56,598", "78", "INFO", "Cache1 US", "Disconnected."),
            Ln("20:30:40,147", "ReplayLoad", "INFO", "Replay DS", "Playing historical data stopped."),
            Ln("20:30:40,148", "ReplayLoad", "INFO", "Replay", "State changed Started -> Stopped."),
            Ln("20:30:40,148", "ReplayLoad", "INFO", "Replay", "Data processing finished."),
            Ln("20:30:40,311", "2", "INFO", "Market Replay", "Replay timing: start path = rewind (history re-request skipped)."),
            Ln("20:30:42,107", "78", "INFO", "Replay", "State changed Stopped -> Initializing."),
            PF("20:30:44,506"),
            Ln("20:30:44,506", "ReplayLoad", "INFO", "Replay", "State changed Initialized -> Started."),
        };
        {
            var ex = new ReplayExecution(); ex.Feed(natural.Take(4));
            Check("Ende: Cache-Disconnect waehrend Wiedergabe -> weiter Running (nur Hinweis)", ex.State == ReplayEnd.Running && ex.Hints.Count == 2);
            ex.Feed(natural.Skip(4).Take(4));
            Check("Ende: Abschluss + Auto-Rewind -> NaturalEnd mit Belegzeilen", ex.State == ReplayEnd.NaturalEnd && ex.Evidence.Count == 3 && ex.Evidence[2].Message.Contains("rewind"), ex.Rule);
            ex.Feed(natural.Skip(8));
            Check("Ende: anschliessender Auto-Loop loescht den Zustand NICHT (Latch)", ex.State == ReplayEnd.NaturalEnd && ex.LoopRestarted);
            Check("Ende: Ladebereich aus 'Playing from' (UTC)", ex.Range == (new DateTime(2026, 10, 26, 13, 30, 0, DateTimeKind.Utc), new DateTime(2026, 10, 26, 16, 0, 0, DateTimeKind.Utc)));
        }
        {
            var ex = new ReplayExecution(); ex.Feed(natural.Take(2));
            ex.Feed(new[] { Ln("20:29:40,000", "31", "ERROR", "Auth", "Instrument 29210 for crosstrading group 9 (Ether) not found."),
                            Ln("20:29:41,000", "Process", "ERROR", "Main window", "Notification title 'Order register failed' message 'Account is done for day'."),
                            "System.Exception: Account is done for day",
                            Ln("20:29:42,000", "40", "INFO", "Websocket", "Exception while reading frame (socket closed)") });
            Check("Fehler: ERROR/Exception ANDERER Module -> kein Errored (VM-Befund t+99s)", ex.State == ReplayEnd.Running, ex.State.ToString());
            ex.Feed(new[] { Ln("20:29:50,000", "ReplayLoad", "ERROR", "Replay", "Data source faulted: connection to replay server lost.") });
            Check("Fehler: ERROR der Replay-Quelle -> Errored mit Originalzeile", ex.State == ReplayEnd.Errored && ex.Evidence[0].Source == "Replay" && ex.Rule.Contains("Replay"));
        }
        {
            var ex = new ReplayExecution(); ex.Feed(natural.Take(2));
            ex.Feed(new[] { Ln("20:30:00,000", "ReplayLoad", "INFO", "Replay", "State changed Started -> Stopped."), Ln("20:30:00,000", "ReplayLoad", "INFO", "Replay", "Data processing finished.") });
            Check("Ende: Stopp ohne Rewind und ohne Datenende -> KEIN natuerliches Ende", ex.State == ReplayEnd.Running);
            ex.DataReachedEnd = true; ex.Recheck();
            Check("Ende: Stopp + Recorder am Fensterende (unterstuetzend) -> NaturalEnd", ex.State == ReplayEnd.NaturalEnd && ex.Rule.Contains("unterstuetzend"));
        }
        {
            var ex = new ReplayExecution(); ex.Feed(natural.Take(2));
            ex.MarkOurStop(new DateTime(2026, 11, 12, 20, 29, 59));
            ex.Feed(new[] { Ln("20:30:00,000", "ReplayLoad", "INFO", "Replay", "State changed Started -> Stopped."), Ln("20:30:00,001", "ReplayLoad", "INFO", "Replay", "Data processing finished."),
                            Ln("20:30:00,100", "2", "INFO", "Replay MD", "Loading historical data to online state for #MNQZ6@CME."), Ln("20:30:00,200", "2", "INFO", "Market Replay", "Replay timing: start path = rewind") });
            Check("Ende: eigener Stopp -> StoppedByUs, nie NaturalEnd", ex.State == ReplayEnd.StoppedByUs);
        }
        {
            var ex = new ReplayExecution(); ex.Feed(natural.Take(2));
            ex.Feed(new[] { Ln("20:30:00,100", "2", "INFO", "Replay MD", "Loading historical data to online state for #MNQZ6@CME.") });
            Check("Ende: Wechsel zu Echtzeit (nicht durch uns) -> OnlineExit", ex.State == ReplayEnd.OnlineExit);
            var ex2 = new ReplayExecution(); ex2.Feed(natural.Take(2)); ex2.Feed(new[] { PF("20:31:00,000", "27.10.2026") });
            Check("Ende: neuer Bereich ohne Abschluss -> OtherRangeRestart", ex2.State == ReplayEnd.OtherRangeRestart);
        }
        {
            var ex = new ReplayExecution();
            ex.Feed(new[] { Ln("20:31:56,273", "ReplayLoad", "INFO", "Replay DS", "Replay timing: pre-roll 1119 ms, 841175 entities warmed before 2026-10-27 13:30:00Z.") });
            Check("Ladebestaetigung vor Play: 'warmed before ...Z' gelesen", ex.WarmStartUtc == new DateTime(2026, 10, 27, 13, 30, 0, DateTimeKind.Utc) && !ex.Started);
            Check("Logzeile: Quelle/Stufe korrekt geparst", AtasLogLine.Parse(Ln("20:30:40,311", "2", "INFO", "Market Replay", "x"))?.Source == "Market Replay"
                  && AtasLogLine.Parse("System.Exception: Account is done for day") == null);
        }
        // ---- Dialog-Zeitzone: keine feste Berlin-Annahme, keine +-1h-Heuristik ----
        {
            var tmp = Path.Combine(baseDir, "Platform_test.cnf");
            File.WriteAllText(tmp, "{\"Login\":\"x@y.de\",\"Password\":\"geheim\",\"SavedTimeZoneOffset\":1,\"SavedTimeZoneOffsetMinutes\":0,\"IsAutoUpdateTimeZone\":false}");
            var z = DialogZone.Resolve(tmp);
            Check("Zeitzone: ATAS fester Versatz 0 (z. B. VM in UTC) -> Eingabe = UTC", z.FixedOffsetMinutes == 0 && z.FromUtc(new DateTime(2026, 10, 26, 13, 30, 0, DateTimeKind.Utc)) == new DateTime(2026, 10, 26, 13, 30, 0));
            Check("Zeitzone: Anmeldedaten werden nicht uebernommen", !z.Source.Contains("geheim") && !z.Source.Contains("x@y.de"));
            File.WriteAllText(tmp, "{\"SavedTimeZoneOffsetMinutes\":60,\"IsAutoUpdateTimeZone\":true}");
            var za = DialogZone.Resolve(tmp);
            Check("Zeitzone: automatisch -> Windows-Zeitzone mit Sommerzeitregeln", za.Zone != null && za.FixedOffsetMinutes == null);
            var berlin = new DialogZone("test", TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"), null);
            Check("Zeitzone: Berlin Sommerzeit (Juli) 13:30Z -> 15:30", berlin.FromUtc(new DateTime(2026, 7, 1, 13, 30, 0, DateTimeKind.Utc)) == new DateTime(2026, 7, 1, 15, 30, 0));
            Check("Zeitzone: Berlin Winterzeit (26.10.) 13:30Z -> 14:30", berlin.FromUtc(new DateTime(2026, 10, 26, 13, 30, 0, DateTimeKind.Utc)) == new DateTime(2026, 10, 26, 14, 30, 0));
            File.Delete(tmp);
        }
        // ---- Liste 'Hinzugefuegt': leer vs. nicht lesbar (VM-Befund: 0 UIA-Zeilen bei aktivem Recorder) ----
        Check("Liste: Kopfzahl 1, UIA 0 Zeilen -> NICHT lesbar (kein 'kein Recorder')", ClassifyList(1, 0, true) == ListRead.Unreadable);
        Check("Liste: Kopfzahl 0, 0 Zeilen -> leer", ClassifyList(0, 0, true) == ListRead.Empty);
        Check("Liste: Kopfzahl unbekannt, 0 Zeilen -> NICHT lesbar", ClassifyList(-1, 0, true) == ListRead.Unreadable);
        Check("Liste: Kopfzahl 2, 2 Zeilen mit Text -> lesbar", ClassifyList(2, 2, true) == ListRead.Readable);
        Check("Liste: Zeile ohne Text -> NICHT lesbar", ClassifyList(1, 1, false) == ListRead.Unreadable);
        {
            var lines = GroupOcrLines(new[] { (700, 460, 30, 14, "OF"), (735, 461, 60, 14, "Replay"), (800, 459, 70, 14, "Recorder"), (700, 482, 40, 14, "Big"), (745, 483, 50, 14, "trades") });
            Check("OCR-Zeilen: Woerter je Zeile gruppiert, von oben nach unten", lines.Count == 2 && lines[0].text == "OF Replay Recorder" && lines[1].text == "Big trades" && lines[0].x0 == 700);
        }
        // ---- Diagnose: Konto-/Zugangsdaten maskiert ----
        {
            var m = MaskSensitive("2026-11-16 16:56:59,097 [Process] INFO  ic[LT-R3L4XK96]: State changed Connecting => Connected. user a.b@example.com [MNQZ6@CME, LFF050-D4K01AZ9-PRO022] login=abc");
            Check("Diagnose: E-Mail, Konto-IDs und Login maskiert", !m.Contains("example.com") && !m.Contains("LT-R3L4XK96") && !m.Contains("LFF050") && !m.Contains("login=abc") && m.Contains("MNQZ6@CME"), m);
        }

        // ---- Recorder entfernen: Wirkungspruefung + Symbolsuche im Zeilenbild (VM-Befund: Papierkorb verfehlt) ----
        AddedRow Row(int i, string txt, string ocr = "") => new(i, new System.Windows.Rect(600, 450 + 22 * i, 580, 21), txt, ocr);
        {
            var b0 = new[] { Row(0, "Big trades"), Row(1, "OF Replay Recorder (read-only)"), Row(2, "Volume Profile & TPO") };
            Check("Entfernen: genau Recorder weg, Rest gleich -> bestaetigt", OnlyRecorderRemoved(b0, new[] { Row(0, "Big trades"), Row(1, "Volume Profile & TPO") }, out _));
            Check("Entfernen: falsche Zeile entfernt -> NICHT bestaetigt", !OnlyRecorderRemoved(b0, new[] { Row(0, "OF Replay Recorder (read-only)"), Row(1, "Volume Profile & TPO") }, out _));
            Check("Entfernen: nichts passiert -> NICHT bestaetigt", !OnlyRecorderRemoved(b0, b0, out _));
            Check("Entfernen: Recorder + weiterer Indikator weg -> NICHT bestaetigt", !OnlyRecorderRemoved(b0, new[] { Row(0, "Big trades") }, out _));
            Check("Entfernen: Recorder nur per OCR lesbar -> erkannt und bestaetigt",
                  OnlyRecorderRemoved(new[] { Row(0, "", "Big trades"), Row(1, "", "OF Replay Recorder (read-only)") }, new[] { Row(0, "", "Big trades") }, out _));
            Check("Entfernen: zwei Recorder-Zeilen -> NICHT bestaetigt (nicht eindeutig)",
                  !OnlyRecorderRemoved(new[] { Row(0, "OF Replay Recorder (read-only)"), Row(1, "OF Replay Recorder (read-only)") }, new[] { Row(0, "OF Replay Recorder (read-only)") }, out _));
        }
        {
            using var bmp = new System.Drawing.Bitmap(580, 22);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.Clear(System.Drawing.Color.FromArgb(37, 37, 38));
                g.FillRectangle(System.Drawing.Brushes.White, 10, 6, 180, 10);      // Text
                g.FillRectangle(System.Drawing.Brushes.LightGray, 500, 4, 14, 14);  // Symbol 1
                g.FillRectangle(System.Drawing.Brushes.LightGray, 530, 4, 14, 14);  // Symbol 2
                g.FillRectangle(System.Drawing.Brushes.IndianRed, 556, 4, 14, 14);  // Symbol 3 (Papierkorb)
            }
            var blobs = FindIconBlobs(bmp, new List<(int, int)> { (10, 190) });
            Check("Symbolsuche: drei Symbole rechts, Text ausgeschlossen", blobs.Count == 3 && blobs.All(b => b.cx > 400), string.Join(",", blobs.Select(b => b.cx)));
            Check("Symbolsuche: Mittelpunkte innerhalb der Symbole", blobs.Any(b => Math.Abs(b.cx - 563) <= 2 && Math.Abs(b.cy - 11) <= 2));
            using var empty = new System.Drawing.Bitmap(580, 22);
            using (var g = System.Drawing.Graphics.FromImage(empty)) { g.Clear(System.Drawing.Color.FromArgb(37, 37, 38)); g.FillRectangle(System.Drawing.Brushes.White, 10, 6, 180, 10); }
            Check("Symbolsuche: ohne Symbole -> keine Kandidaten (kein Blindklick)", FindIconBlobs(empty, new List<(int, int)> { (10, 190) }).Count == 0);
        }
        {
            // 1.3.9 (Befund 18.11.): Leiste waehrend 'Module laden…' – Fortschrittsbalken UNTER der Symbolzeile ueber die ganze Breite.
            using var bar = new System.Drawing.Bitmap(760, 70);
            using (var g = System.Drawing.Graphics.FromImage(bar))
            {
                g.Clear(System.Drawing.Color.FromArgb(37, 37, 38));
                g.FillRectangle(System.Drawing.Brushes.White, 40, 14, 120, 12);       // 'Datum auswählen'
                g.FillRectangle(System.Drawing.Brushes.White, 300, 14, 30, 12);       // '100x'
                foreach (int x in new[] { 420, 480, 540, 600, 650 }) g.FillRectangle(System.Drawing.Brushes.LightGray, x, 13, 14, 14); // Transport/Zahnrad/Hilfe
                g.FillRectangle(System.Drawing.Brushes.LightGray, 700, 13, 14, 14);   // X (rechtestes Symbol der Zeile)
                g.FillRectangle(System.Drawing.Brushes.DodgerBlue, 10, 44, 745, 6);   // Fortschrittsbalken 'Module laden…'
            }
            var words = new List<OcrWord>
            {
                new() { Text = "Datum auswählen", X = 40, Y = 14, W = 120, H = 12 },
                new() { Text = "100x", X = 300, Y = 14, W = 30, H = 12 },
                new() { Text = "Module laden...", X = 330, Y = 44, W = 90, H = 6 },
                new() { Text = "x", X = 699, Y = 13, W = 16, H = 14 },   // echtes Bild: das X selbst wird als Buchstabe erkannt
            };
            var spans = words.Where(o => o.Text != "x").Select(o => (o.X, o.X + o.W)).ToList();
            var old = FindIconBlobs(bar, spans).Where(b => b.cx >= bar.Width * 0.85).ToList();
            Check("X-Suche alt: Fortschrittsbalken verschmilzt mit dem X (Befund nachgestellt)", !old.Any(b => Math.Abs(b.cx - 707) <= 3 && Math.Abs(b.cy - 20) <= 3), string.Join(",", old.Select(b => $"{b.cx}/{b.cy}")));
            var neu = BarCloseCandidates(bar, words, out var d1);
            Check("X-Suche: nur Symbolzeile -> rechtestes Symbol ist das X", neu.Count > 0 && Math.Abs(neu[0].cx - 707) <= 3 && Math.Abs(neu[0].cy - 20) <= 3, d1 + " " + string.Join(",", neu.Select(b => $"{b.cx}/{b.cy}")));
            var noRow = BarCloseCandidates(bar, words.Where(o => o.Text.Contains("laden")).ToList(), out var d2);
            Check("X-Suche: 'Module laden' bestimmt NICHT die Zeile", d2.Contains("ganze Hoehe"), d2);
        }

        // ---- Zielchart-Erkennung (VM-Befund 'VulkanChartWpf-Treffer=0'): reine Auswahlfunktion ----
        ChartCand Cc(string name, string cls = "", string aid = "", string type = "Pane", double x = 100, double y = 100, double w = 900, double h = 600, bool off = false, bool min = false)
            => new(name, cls, aid, type, "WPF", x, y, w, h, off, "ATAS - Test", min);
        { var v = SelectChart(new[] { Cc("VulkanChartWpf") }); Check("Chart: genau eine Vulkan-Flaeche -> Unique (bisheriges Verhalten)", v.Kind == ChartVerdictKind.Unique && v.Signal.Contains("VulkanChartWpf")); }
        { var v = SelectChart(new[] { Cc("VulkanChartWpf", type: "Custom") }); Check("Chart: Vulkan-Flaeche als Custom statt Pane -> Unique", v.Kind == ChartVerdictKind.Unique); }
        { var v = SelectChart(new[] { Cc("VulkanChartWpf", x: 100), Cc("VulkanChartWpf", x: 1100) }); Check("Chart: zwei Vulkan-Flaechen -> Multiple (keine Groesste-Flaeche-Wahl)", v.Kind == ChartVerdictKind.Multiple && v.Chosen == null); }
        { var v = SelectChart(new[] { Cc("VulkanChartWpf", x: 100), Cc("VulkanChartWpf", x: 100) }); Check("Chart: dieselbe Flaeche doppelt im UIA-Baum -> Unique", v.Kind == ChartVerdictKind.Unique); }
        { var v = SelectChart(new[] { Cc("", cls: "DxChartWpf") }); Check("Chart: andere Renderflaeche (Klasse mit 'Chart') ohne Vulkan -> Unique ueber Klasse", v.Kind == ChartVerdictKind.Unique && v.Signal.StartsWith("Klasse")); }
        { var v = SelectChart(new[] { Cc("ChartControl", w: 1000, h: 700, x: 90, y: 90), Cc("", aid: "ChartSurface", x: 100, y: 120, w: 900, h: 600) }); Check("Chart: Container + innere Flaeche -> innerste gewaehlt, Unique", v.Kind == ChartVerdictKind.Unique && v.Chosen!.AutomationId == "ChartSurface"); }
        { var v = SelectChart(new[] { Cc("", cls: "DxChartWpf", x: 100), Cc("", cls: "DxChartWpf", x: 1100) }); Check("Chart: zwei andere Renderflaechen -> Multiple", v.Kind == ChartVerdictKind.Multiple); }
        { var v = SelectChart(new[] { Cc("ChartTrader"), Cc("Chart toolbar", w: 900, h: 400), Cc("", cls: "ChartTabItem") }); Check("Chart: nur Chart-Bedienelemente (Trader/Toolbar/Tab) -> NoneFound", v.Kind == ChartVerdictKind.NoneFound); }
        { var v = SelectChart(new[] { Cc("ChartTrader", x: 1100), Cc("VulkanChartWpf") }); Check("Chart: Vulkan-Flaeche + ChartTrader -> Unique (Vulkan)", v.Kind == ChartVerdictKind.Unique && v.Chosen!.Name == "VulkanChartWpf"); }
        { var v = SelectChart(new[] { Cc("VulkanChartWpf", min: true) }); Check("Chart: Fenster minimiert -> OnlyHidden (nicht 'kein Chart')", v.Kind == ChartVerdictKind.OnlyHidden); }
        { var v = SelectChart(new[] { Cc("VulkanChartWpf", off: true) }); Check("Chart: Flaeche offscreen/verdeckt -> OnlyHidden", v.Kind == ChartVerdictKind.OnlyHidden); }
        { var v = SelectChart(new[] { Cc("VulkanChartWpf", w: 250, h: 150) }); Check("Chart: zu kleine Flaeche -> OnlyHidden", v.Kind == ChartVerdictKind.OnlyHidden); }
        { var v = SelectChart(Array.Empty<ChartCand>()); Check("Chart: nichts gefunden -> NoneFound", v.Kind == ChartVerdictKind.NoneFound); }
        { var v = SelectChart(new[] { Cc("VulkanChartWpf", x: 100), Cc("VulkanChartWpf", x: 1100, min: true) }); Check("Chart: ein sichtbarer + ein minimierter Chart -> Unique (sichtbarer)", v.Kind == ChartVerdictKind.Unique && v.Chosen!.X == 100); }
        // ---- 1.3.6: Ersatzweg Instrumenttitel (VM 18.11.: keine ChartView, zwei namenlose WinForms-Flaechen gleicher Lage) ----
        Check("ChartOCR: Titelwoerter MNQ / MNQZ6 / MNQZ26 / MNQ-12-26 / (MNQ) erkannt",
              new[] { "MNQ", "MNQZ6", "MNQZ26", "MNQ-12-26", "(MNQ)", "mnqz6,", "#MNQZ6" }.All(t => IsTargetToken(t, "MNQ")));
        Check("ChartOCR: keine Teilwort-/Fremdtreffer (NQ, MNQX123, XMNQ, MNQUARTER)",
              !new[] { "NQ", "MNQX123", "XMNQ", "MNQUARTER", "MN0", "" }.Any(t => IsTargetToken(t, "MNQ")));
        {
            string LgC(string t, string src, string msg) => $"2026-11-18 {t},100 [2         ] INFO  {src,-15}: {msg}";
            var lines = new[] { LgC("09:00:00", "ES 1m Chart", "ApplyTemplate"), LgC("13:17:20", "MNQ 5m Chart", "ApplyTemplate: template.Panels=1"),
                                LgC("13:21:30", "Chart", "[RenderControl] KeyUp"), LgC("13:22:00", "Replay", "State changed") };
            var cs = ChartSourcesFromLog(lines, new DateTime(2026, 11, 18, 13, 17, 0));
            Check("ChartOCR: Chart-Namen nur seit ATAS-Start aus dem Protokoll (alter ES-Chart und 'Chart' ohne Namen ignoriert)",
                  cs.Count == 1 && cs[0] == "MNQ 5m Chart", string.Join(",", cs));
        }
        {
            System.Windows.Rect R(double x, double y, double w, double h) => new(x, y, w, h);
            var vm = new List<Surface> {
                new("", "WindowsForms10.Window.8.app.0.14a4f1c_r3_ad1", "3998704", "Pane", "WinForm", R(363, 187, 1553, 787), 0x111, false),
                new("", "WindowsForms10.Window.8.app.0.14a4f1c_r3_ad1", "1508644", "Pane", "WinForm", R(363, 187, 1553, 787), 0x222, false),
                new("RootLayout", "ExtendedDockLayoutManager", "RootLayout", "Group", "WPF", R(0, 157, 1920, 843), 0, false) };
            var rg = SurfaceRegions(vm);
            Check("ChartOCR: VM-Fall 18.11. -> Container verworfen, zwei gleiche WinForms-Flaechen = EINE Flaeche", rg.Count == 1 && rg[0].Count == 2);
            vm.Add(new("", "WindowsForms10.Window.8.app.0.14a4f1c_r3_ad1", "777", "Pane", "WinForm", R(363, 600, 1553, 380), 0x333, false));
            Check("ChartOCR: zweite, andere Flaeche -> zwei Flaechen (nicht eindeutig)", SurfaceRegions(vm).Count >= 2);
        }
        {
            var ok = new OcrHit("MNQZ6", 400, 200, true, "gehoert zur Flaeche");
            var logMnq = new[] { "MNQ 5m Chart" };
            var none = Array.Empty<OcrHit>(); var S = RegionRelation.Single; var N = RegionRelation.Nested;
            OcrChartVerdict D(int reg, RegionRelation rel, string? named, IReadOnlyList<OcrHit> h, IReadOnlyList<string> lc) => DecideChartFallback(reg, rel, named, h, lc, "MNQ");
            Check("ChartOCR: eine Flaeche + MNQ-Titel + Zuordnung + Protokoll -> eindeutig", D(1, S, null, new[] { ok }, logMnq).Unique);
            Check("ChartOCR: gleiche Koordinaten reichen nicht - Titel gehoert nicht zur Flaeche -> unklar",
                  !D(1, S, null, new[] { ok, new OcrHit("MNQ", 380, 900, false, "andere hwnd") }, logMnq).Unique);
            Check("ChartOCR: zwei Flaechen -> nicht eindeutig (auch mit Titel)", !D(2, S, null, new[] { ok }, logMnq).Unique);
            Check("ChartOCR: kein Titel und kein Panel-Name -> nicht eindeutig", !D(1, S, null, none, logMnq).Unique);
            Check("ChartOCR: Protokoll nennt nur ES-Chart -> Widerspruch, nicht eindeutig", !D(1, S, null, new[] { ok }, new[] { "ES 1m Chart" }).Unique);
            Check("ChartOCR: Protokoll nennt keinen Chart -> Titel + Zuordnung genuegen", D(1, S, null, new[] { ok }, Array.Empty<string>()).Unique);
            // Korrektur 17.11.: 'MNQ 5m Chart' war der UIA-Name des umgebenden Panels (CustomLayoutPanel), nicht der ChartView.
            Check("ChartOCR: UIA-Name des umgebenden Panels 'MNQ 5m Chart' -> eindeutig (auch ohne Texterkennung)",
                  D(1, S, "MNQ 5m Chart", none, logMnq) is { Unique: true } pv && pv.Signal.Contains("Panel"));
            Check("ChartOCR: Panel-Name + widersprechender Titeltreffer -> unklar", !D(1, S, "MNQ 5m Chart", new[] { new OcrHit("MNQ", 1, 1, false, "fremd") }, logMnq).Unique);
            Check("ChartOCR: Titel in der Titelzeile, per UIA demselben Panel zugeordnet -> eindeutig",
                  D(1, S, null, new[] { new OcrHit("MNQ", 400, 170, true, "UIA: gemeinsamer Vorfahr = dasselbe Chart-Panel") }, logMnq).Unique);
            // Zwei deckungsgleiche Fenster beweisen weder einen noch zwei Charts:
            Check("ChartOCR: deckungsgleiche Fenster VERSCHACHTELT (Host + Zeichenflaeche) + Titel -> eindeutig", D(1, N, null, new[] { ok }, logMnq).Unique);
            Check("ChartOCR: deckungsgleiche Fenster als GESCHWISTER (z. B. Tabs) -> unklar, auch mit Titel", !D(1, RegionRelation.Siblings, "MNQ 5m Chart", new[] { ok }, logMnq).Unique);
            Check("ChartOCR: Verhaeltnis nicht bestimmbar -> unklar", !D(1, RegionRelation.Unknown, null, new[] { ok }, logMnq).Unique);
            Check("ChartOCR: Panel-Name-Erkennung 'MNQ 5m Chart' / 'MNQZ6 - 5 Min' ja, 'NQ 5m Chart' / 'MNQX123' nein",
                  NameHasTarget("MNQ 5m Chart", "MNQ") && NameHasTarget("MNQZ6 - 5 Min", "MNQ") && !NameHasTarget("NQ 5m Chart", "MNQ") && !NameHasTarget("MNQX123 Chart", "MNQ"));
            var surf = new System.Windows.Rect(363, 187, 1553, 787);
            Check("ChartOCR: Panel mit Titelzeile ueber der Flaeche gilt als chartgross; Hauptfenster/Layout nicht",
                  IsPanelOf(new System.Windows.Rect(339, 157, 1577, 840), surf) && !IsPanelOf(new System.Windows.Rect(0, 0, 3840, 2160), surf) && !IsPanelOf(new System.Windows.Rect(400, 187, 1400, 787), surf));
        }

        Check("Bridge-Selbsttest: rc=0 + result ok -> ok", BridgeSelfcheckOk(0, "{\"result\": \"ok\", \"python\": \"3.13.9\"}"));
        Check("Bridge-Selbsttest: ModuleNotFoundError (rc=1, leere Ausgabe) -> NICHT ok", !BridgeSelfcheckOk(1, ""));
        Check("Bridge-Selbsttest: rc=0 ohne result -> NICHT ok", !BridgeSelfcheckOk(0, "irgendwas"));
        string vmRec = @"C:\Daten mit Leerzeichen\Aufnahmen Überprüfung\Recordings";
        Check("Pfad: neue DLL + Konfiguration zeigt auf Lauf-Ordner -> ok", RecorderPathVerdict(vmRec, true, true, vmRec + "\\", out _));
        Check("Pfad: neue DLL + Konfiguration fehlt -> BLOCKER", !RecorderPathVerdict(vmRec, true, true, null, out _));
        Check("Pfad: neue DLL + Konfiguration zeigt woandershin -> BLOCKER", !RecorderPathVerdict(vmRec, true, true, @"D:\Anders\Recordings", out _));
        Check("Pfad: alte DLL (fester F:-Pfad) + anderer Datenordner -> BLOCKER (VM-Fall)", !RecorderPathVerdict(vmRec, true, false, vmRec, out var legacyMsg) && legacyMsg.Contains("aeltere Version"));
        Check("Pfad: alte DLL + Lauf nutzt genau den alten festen Pfad -> ok (bestehender lokaler Betrieb)", RecorderPathVerdict(LegacyRecorderRoot, true, false, null, out _));
        Check("Pfad: keine DLL installiert -> BLOCKER", !RecorderPathVerdict(vmRec, false, false, vmRec, out _));
        Check("Watchdog Boden geschwindigkeitsunabhaengig (>= 200)", PlayWatchdogSeconds(180, 100.0, 200) >= 200);

        // ---- SAMMELLAUF: stabile Job-ID + Wiederanlauf-Auswahl (keine Doppelaufnahme erledigter Auftraege) ----
        Console.WriteLine("# Sammellauf (Job-ID deterministisch; Wiederanlauf ueberspringt committete Auftraege)");
        var jS = new DateTime(2026, 10, 26, 8, 0, 0, DateTimeKind.Utc);
        var jE = new DateTime(2026, 10, 26, 8, 1, 0, DateTimeKind.Utc);
        Check("BuildJobId deterministisch + UTC-Fenster", BuildJobId("London", 2026, jS, jE) == "London_2026_20261026T080000Z_20261026T080100Z");
        Check("BuildJobId gleiche Eingaben -> gleiche ID", BuildJobId("London", 2026, jS, jE) == BuildJobId("London", 2026, jS, jE));
        var allIds = new List<string> { "a", "b", "c" };
        var com = new HashSet<string> { "a" };
        Check("JobsToRun ueberspringt committete, Reihenfolge erhalten", string.Join(",", JobsToRun(allIds, com)) == "b,c");
        Check("JobsToRun: nichts committed -> alle", string.Join(",", JobsToRun(allIds, new HashSet<string>())) == "a,b,c");
        Check("JobsToRun: alle committed -> leer", JobsToRun(allIds, new HashSet<string> { "a", "b", "c" }).Count == 0);
        Check("CollectionComplete true nur wenn alle committed", CollectionComplete(allIds, new HashSet<string> { "a", "b", "c" }));
        Check("CollectionComplete false bei Rest", !CollectionComplete(allIds, com));
        Check("CollectionComplete false bei leerer Jobliste", !CollectionComplete(new List<string>(), com));

        Console.WriteLine(fails == 0 ? "\nREGRESS: ALLE BESTANDEN" : $"\nREGRESS: {fails} FEHLGESCHLAGEN");
        return fails == 0 ? 0 : 1;
    }

    // ===== Pure Entscheidungsfunktionen (offline via 'regress'); autorun nutzt genau diese. =====
    // A1 (Finding 1): Enderreichung ist belegt, wenn der (fenster-gebundene) Marktfortschritt das Fensterende
    // erreicht. KEINE Toleranz mehr (Ende-1s zaehlt NICHT). Halb-offen: Erreichen von winEnd genuegt.
    internal static bool EndReached(DateTime? progressUtc, DateTime windowEndUtc)
        => progressUtc != null && progressUtc.Value.ToUniversalTime() >= windowEndUtc.ToUniversalTime();

    // GESAMT-PASS-Gating des LiveProof (testbar): bestanden NUR, wenn Aufnahme gespeichert UND unabhaengiges
    // Replay-Endsignal belegt UND Cleanup abgeschlossen. Fehlt eines -> KEIN PASS (die Aufnahme bleibt erhalten,
    // der Fehler wird gemeldet; das Verdikt loescht nie eine Aufnahme).
    internal static bool CleanupDone(bool recorderRemoved, bool replayViewLeft) => recorderRemoved && replayViewLeft;
    internal static bool LiveProofPass(bool recordingSaved, bool endProven, bool cleanupDone)
        => recordingSaved && endProven && cleanupDone;

    // A4 (Recheck2): Ein atomarer Token-Gewinner beweist nur EINEN, nicht den RICHTIGEN Gewinner. Daher technisch
    // erzwungener EIN-RECORDER-BETRIEB: Zuordnung ist nur eindeutig, wenn GENAU EINE aktive (gesperrte) MNQ-Instanz
    // existiert (keine weitere irgendwo), sie von DIESEM Lauf gestartet wurde (isNew) UND das Auftragstoken trägt.
    //  - 0 aktive Instanzen -> None; >1 -> Ambiguous (kein Ein-Recorder-Betrieb -> Ablehnung, auch bei fremder Instanz);
    //  - genau 1, aber nicht neu / falsches Token -> None (fremder/unbestätigter Träger abgelehnt).
    internal enum Attrib { None, Unique, Ambiguous }
    internal static Attrib AttributionVerdict(IReadOnlyList<(string runId, bool isNew)> lockedMnqInstances, string expectedToken)
    {
        if (string.IsNullOrWhiteSpace(expectedToken)) return Attrib.None;
        if (lockedMnqInstances == null || lockedMnqInstances.Count == 0) return Attrib.None;
        if (lockedMnqInstances.Count > 1) return Attrib.Ambiguous;   // mehr als ein aktiver Recorder -> nicht eindeutig
        var only = lockedMnqInstances[0];
        if (!only.isNew) return Attrib.None;                         // nicht durch diesen Lauf gestartet
        return string.Equals(only.runId?.Trim(), expectedToken.Trim(), StringComparison.OrdinalIgnoreCase)
            ? Attrib.Unique : Attrib.None;                           // die EINZIGE Instanz muss das Auftragstoken tragen
    }

    // A2 (Finding 2): Ein zugeordneter Ordner ist SAUBER abgeschlossen, wenn Summary vorhanden, Writer beendet
    // (nicht gesperrt), clean==true (A3-Schema) UND die Summary-runId mit dem Auftragstoken uebereinstimmt.
    // Alte/fremde/clean=false-Summary oder verbleibender Writer -> NICHT sauber.
    internal static bool RecorderFinalizedClean(bool hasSummary, bool writerLocked, string summaryJson, string expectedRunId)
        => hasSummary && !writerLocked && SummaryCleanClose(summaryJson)
           && !string.IsNullOrWhiteSpace(expectedRunId)
           && Regex.IsMatch(summaryJson, "\"runId\"\\s*:\\s*\"" + Regex.Escape(expectedRunId.Trim()) + "\"", RegexOptions.IgnoreCase);

    // A3: Eine Summary belegt einen SAUBEREN Abschluss nur, wenn clean==true UND kein writerFault/joinTimeout.
    // Fehlt das Feld 'clean' (alte Summary), gilt der saubere Abschluss als NICHT belegt.
    internal static bool SummaryCleanClose(string summaryJson)
    {
        if (string.IsNullOrWhiteSpace(summaryJson)) return false;
        bool Has(string k, string v) => Regex.IsMatch(summaryJson, "\"" + k + "\"\\s*:\\s*" + v, RegexOptions.IgnoreCase);
        if (!Regex.IsMatch(summaryJson, "\"clean\"\\s*:", RegexOptions.IgnoreCase)) return false; // kein Statusfeld -> nicht belegt
        return Has("clean", "true") && !Has("writerFaulted", "true") && !Has("joinTimedOut", "true");
    }

    // A2: Einheitlicher Cleanup-Pfad (Erfolg wie Abbruch). Zuverlaessige UIA-Teile; Recorder-Entfernen ist UI-fragil
    // (Indikator-Dialog: Custom-WPF-Zeilen, nicht im UIA-Baum) und wird nur als Hinweis/Rest-Instanz-Pruefung behandelt.
    // A2 (Finding 2): einheitlicher, GARANTIERTER Abschlusspfad (aus try/finally der AutoRun aufgerufen, d.h. auch
    // bei frueher Rueckkehr/Exception). Jeder zugeordnete Auftrag (Ordner + Token) wird VERBINDLICH geprueft:
    // Summary + Writer-Ende + clean + runId-Match. Ein nicht sauber abgeschlossener Auftrag ODER ein verbleibender
    // Writer verhindert den Gesamterfolg. (Autonomes Recorder-Entfernen bleibt UI-fragil -> offene Live-Grenze.)
    private static bool CleanupAfterRun(Action<string> W, List<(string folder, string token)> attributed)
    {
        W("\n[A2 cleanup] garantierter Abschlusspfad …");
        // A2 (Recheck): Jeder Schritt ist EINZELN abgesichert — ein Fehler in einem Schritt verhindert nicht die
        // übrigen Abschlussschritte und wirft NIE aus CleanupAfterRun heraus (sonst würde der finally-Pfad den
        // Abbruch-Exitcode durch einen Sammelfehler ersetzen). Jeder Fehlschlag macht den Cleanup NICHT erfolgreich.
        // A2 (Recheck2): Pause und Replay-Verlassen in GETRENNTEN try-Blöcken — ein Pause-Fehler darf das
        // Replay-Verlassen NICHT überspringen. Beide Schritte über CleanupSteps (gleiche Logik wie im Test).
        var (pauseOk, exitAttempted, exitOk) = CleanupSteps(
            pause: () =>
            {
                string t = Safe(() => ReplayWindow()?.Current.Name ?? "");
                if (t.Contains("Historienwiedergabe", StringComparison.OrdinalIgnoreCase) && !t.Contains("angehalten", StringComparison.OrdinalIgnoreCase))
                { var pb = FindIdAny("PlayButton"); if (pb != null) { W("  pausiere Wiedergabe"); Invoke(pb); Thread.Sleep(1200); } }
            },
            exit: () =>
            {
                if (ReplayWindow() != null)
                {
                    var rb = FindIdAny("RibbonReplayButton");
                    if (rb != null) { W("  verlasse Replay"); Invoke(rb); for (int i = 0; i < 8 && ReplayWindow() != null; i++) Thread.Sleep(1000); }
                }
                return ReplayWindow() == null;
            }, W);
        bool replayExited = exitOk;
        W($"  Replay verlassen: {replayExited} (pauseOk={pauseOk}, exitVersucht={exitAttempted})");

        // 3) Finalisierung JEDES zugeordneten Auftrags robust prüfen (clean + runId + Writer-Ende).
        bool allFinalized = AllFinalizedClean(attributed, folder =>
        {
            var dir = Path.Combine(RecordingsDir, folder);
            var sp = Path.Combine(dir, "summary.json");
            bool hasSummary = File.Exists(sp);
            bool locked = IsMetaLocked(Path.Combine(dir, "meta.json"));
            string json = hasSummary ? ReadAllShared(sp) : "";
            return (hasSummary, locked, json);
        }, W);

        // 4) Rest-/Live-Instanz-Scan robust; Fehler -> konservativ NICHT erfolgreich.
        int lockedNow;
        try { lockedNow = Directory.Exists(RecordingsDir) ? Directory.GetDirectories(RecordingsDir).Count(d => IsMetaLocked(Path.Combine(d, "meta.json"))) : 0; }
        catch (Exception ex) { W($"  [A2] Rest-Instanz-Scan FEHLER ({ex.GetType().Name}: {ex.Message}) -> konservativ nicht erfolgreich."); return false; }
        if (lockedNow > 0) W($"  [A2] {lockedNow} aktive Recorder-Instanz(en) nach Replay-Verlassen -> Cleanup NICHT erfolgreich. Recorder-Entfernen erfordert den Indikator-Dialog (Custom-WPF, nicht UIA) -> Live-Grenze.");
        return replayExited && allFinalized && lockedNow == 0;
    }

    // A2 (Recheck2): Pause und Replay-Verlassen als GETRENNTE Schritte. Ein Fehler beim Pausieren überspringt das
    // Replay-Verlassen NICHT; beide Fehlerzustände bleiben erhalten. Genau diese Logik nutzt CleanupAfterRun (und der Test).
    internal static (bool pauseOk, bool exitAttempted, bool exitOk) CleanupSteps(Action pause, Func<bool> exit, Action<string> W)
    {
        bool pauseOk = true;
        try { pause(); }
        catch (Exception ex) { pauseOk = false; W($"  [A2] Pause-Fehler ({ex.GetType().Name}: {ex.Message}) -> Replay-Verlassen wird DENNOCH versucht."); }
        bool exitAttempted = false, exitOk = false;
        try { exitAttempted = true; exitOk = exit(); }
        catch (Exception ex) { exitOk = false; W($"  [A2] Replay-Verlassen-Fehler ({ex.GetType().Name}: {ex.Message})."); }
        return (pauseOk, exitAttempted, exitOk);
    }

    // A2 (Recheck): robuste Finalisierungsprüfung ALLER Aufträge. Jeder Ordner wird einzeln versucht; wirft ein
    // Zugriff (IO/Zugriff), gilt der Auftrag als NICHT finalisiert — die übrigen werden dennoch geprüft, nichts wirft heraus.
    internal static bool AllFinalizedClean(IEnumerable<(string folder, string token)> attributed,
                                           Func<string, (bool hasSummary, bool locked, string summaryJson)> probe,
                                           Action<string> W)
    {
        bool all = true;
        foreach (var (folder, token) in attributed ?? Enumerable.Empty<(string, string)>())
        {
            bool fin;
            try
            {
                var (hasSummary, locked, json) = probe(folder);
                fin = RecorderFinalizedClean(hasSummary, locked, json, token);
                W($"  {folder}: summary={hasSummary} writerBeendet={!locked} clean+runId(A2/A3)={fin} token={token}");
            }
            catch (Exception ex) { fin = false; W($"  {folder}: Finalisierungsprüfung FEHLER ({ex.GetType().Name}: {ex.Message}) -> nicht finalisiert."); }
            if (!fin) all = false;   // übrige Aufträge werden trotzdem geprüft
        }
        return all;
    }

    private sealed record Win(string Label, string DialogStart, string DialogEnd, DateTime UtcStart, DateTime UtcEnd);

    // A1 (Recheck2): obere Grenze der geladenen Spanne aus dem Dialog ableiten (DialogEnd-DialogStart == loadedEnd-UtcStart),
    // statt einer fixen +15min-Annahme. Fehlerhaft/parsебar? -> konservativ UtcEnd (keine Spanne über das Fenster hinaus).
    private static DateTime LoadedEndUtc(Win w)
    {
        try
        {
            var ds = DateTime.ParseExact(w.DialogStart, "dd.MM.yyyy HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            var de = DateTime.ParseExact(w.DialogEnd, "dd.MM.yyyy HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            var span = de - ds;
            return span > TimeSpan.Zero ? w.UtcStart + span : w.UtcEnd;
        }
        catch { return w.UtcEnd; }
    }

    // A2 (Finding 2): Blocker als Exception, damit der Cleanup im finally GARANTIERT laeuft (auch bei frueher Rueckkehr).
    private sealed class AbortRun : Exception { public int Code; public AbortRun(int code, string msg) : base(msg) { Code = code; } }

    // ================= VOLLSTAENDIGER FUNKTIONSNACHWEIS (ein Aufruf, aktuelles Single-Chart-Layout) =================
    // Reuse: FindIdAny/FindBtnAny/Invoke/TypeIntoField/ValueOf/FolderStreams/CloseDateDialogIfOpen/VerifyTargetChartUnique.
    // UIA wo verlaesslich (Replay-Toggle, Transport, Datum-Dialog). Koordinaten NUR fuer den Indikator-Dialog
    // (Custom-WPF-Katalog/Hinzugefuegt/Uebernehmen, nicht im UIA-Baum) -> Ergebnis per DATEISYSTEM verifiziert.
    // Verlaessliche Signale: Replay aktiv == ReplayWindow vorhanden (nicht Titel/Toggle); Instanz == neuer gesperrter
    // Ordner nach LOAD (genau einer, Ein-Recorder-Betrieb); Enderreichung == Transport 'gestoppt' NACH beobachtetem
    // Abspielen UND Recorder-Marktzeit erreicht das geladene Ende; Zeitzone EMPIRISCH aus erstem tMarket vs. Dialog-Start.
    private const int IND_SEARCH_X = 870, IND_SEARCH_Y = 298;     // Indikator-Dialog Suchfeld (1920x1080 maximiert)
    private const int IND_CAT_X = 720, IND_CAT_Y = 327;          // erstes Katalog-Ergebnis
    private const int IND_ROW_X = 770, IND_ROW_Y = 586;          // Hinzugefuegt-Zeile
    private const int IND_TRASH_X = 1074, IND_TRASH_Y = 586;     // Entfernen (Papierkorb)
    private const int IND_APPLY_X = 1402, IND_APPLY_Y = 762;     // Uebernehmen

    private static AutomationElement? MainWin()
        => AtasRoots().FirstOrDefault(w => Safe(() => w.Current.Name).StartsWith("ATAS", StringComparison.OrdinalIgnoreCase));

    private static void ClickMain(int x, int y, bool dbl = false)
    {
        var m = MainWin(); if (m != null) Foreground(m); Thread.Sleep(250);
        SetCursorPos(x, y); Thread.Sleep(200);
        if (dbl) DoubleClickFast(x, y); else ClickScreen(x, y);
        Thread.Sleep(700);
    }

    // Hebt das SCHWEBENDE Replay-Transport-Float zuverlaessig in den Vordergrund (z-Order), damit Screenshot/OCR
    // und Klicks die Leiste treffen und nicht das davorliegende ATAS-Hauptfenster/den Chart.
    // Grund (live belegt): das Haupt-ATAS-Fenster nach vorn zu holen schiebt das Transport-Float DAHINTER ->
    // die Leiste ist unsichtbar fuer OCR und ein Klick landet auf dem Chart. Faellt auf das Hauptfenster zurueck,
    // wenn der Transport angedockt ist (kein separates 'Replay'-Float existiert).
    private static void RaiseReplayFloat()
    {
        var rw = ReplayWindow();
        if (rw != null) { try { ForceForeground(rw); } catch { } Thread.Sleep(150); }
        else { var m = MainWin(); if (m != null) { try { Foreground(m); } catch { } } Thread.Sleep(150); }
    }

    // Klick AUF die Transportleiste: hebt zuerst das schwebende Replay-Float (sonst trifft der Klick das
    // davorliegende Hauptfenster), dann physischer Klick an der Bildschirmposition. Fuer den visuellen Fallback.
    private static void ClickFloat(int x, int y)
    {
        RaiseReplayFloat();
        SetCursorPos(x, y); Thread.Sleep(200);
        ClickScreen(x, y);
        Thread.Sleep(700);
    }

    // Recorder-Zeile entfernen GEOMETRIE-UNABHAENGIG: Klickpunkte relativ zum aktuellen Dialog-Rect
    // (fester Dialog 997x532). Offsets live gegen ATAS verifiziert; ersetzt die bildschirmfesten IND_*-Koordinaten,
    // die nur bei 1920x1080-maximiert trafen. Zeile waehlen -> Papierkorb -> Uebernehmen.
    private static int LockedCount() => Directory.Exists(RecordingsDir)
        ? Directory.GetDirectories(RecordingsDir).Count(d => IsMetaLocked(Path.Combine(d, "meta.json"))) : 0;

    private static HashSet<string> FolderSet() => Directory.Exists(RecordingsDir)
        ? Directory.GetDirectories(RecordingsDir).Select(Path.GetFileName).Where(x => x != null).ToHashSet()!
        : new HashSet<string>();

    // Oeffnet den Indikator-Dialog; falls stattdessen der Datum-Dialog aufgeht (Chart in Replay-Ansicht), schliessen,
    // 'Chart'-Reiter aktivieren und erneut versuchen. true, wenn danach KEIN Datum-Dialog offen ist (Indikator-Dialog an).
    // 'Indikatoren'-Button des Chart-Toolbars. Die BarButtonItemLink-Indizes verschieben sich je nach Indikatorsatz
    // (Link0 ohne / Link2 mit Recorder) -> NICHT auf einen festen Index verlassen: per Name/aid-Enthaltensein finden.
    private static AutomationElement? FindIndicatorsButton()
        => FindInRoots(AtasRoots(), ControlType.Button)
            .Where(b => { var n = Safe(() => b.Current.Name); var id = Safe(() => b.Current.AutomationId); return n == "Indikatoren" || id.Contains("IndicatorsButton", StringComparison.OrdinalIgnoreCase); })
            .Select(b => { System.Windows.Rect r; try { r = b.Current.BoundingRectangle; } catch { r = System.Windows.Rect.Empty; } return (b, r); })
            .Where(t => !t.r.IsEmpty && t.r.Y >= 0).OrderBy(t => t.r.Y).Select(t => t.b).FirstOrDefault();

    // 'Chart'-Reiter des Menuebands per UIA (SelectionItem/Invoke) statt fester Koordinaten.
    private static void SelectChartRibbonTab(Action<string> W)
    {
        var tab = FindInRoots(AtasRoots(), ControlType.TabItem).FirstOrDefault(t => Safe(() => t.Current.Name).Trim().Equals("Chart", StringComparison.OrdinalIgnoreCase));
        if (tab == null) { W("  [ind] 'Chart'-Reiter per UIA nicht gefunden."); return; }
        try { if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var p)) { ((SelectionItemPattern)p).Select(); return; } } catch { }
        if (!Invoke(tab)) ClickElement(tab);
    }

    private static bool OpenIndicatorDialog(Action<string> W)
    {
        if (IndicatorDialog() != null) return true;   // bereits offen
        for (int attempt = 0; attempt < 3; attempt++)
        {
            // Falls der Chart in Replay-Ansicht ist, zuerst 'Chart'-Reiter aktivieren (sonst oeffnet die Toolbar den Datum-Dialog).
            if (attempt > 0) { SelectChartRibbonTab(W); Thread.Sleep(500); }
            var btn = FindIndicatorsButton();
            bool invoked = false;
            if (IsEnabledSafe(btn)) { try { invoked = Invoke(btn!); } catch { invoked = false; } }   // UIA-Invoke (zuverlaessig)
            if (!invoked && btn != null) { ClickElement(btn); invoked = true; }                        // sonst echter Klick am Button
            if (!invoked) W("  [ind] 'Indikatoren'-Schaltflaeche per UIA nicht gefunden (kein Klick auf feste Koordinaten).");
            for (var swD = Stopwatch.StartNew(); swD.ElapsedMilliseconds < 3000 && IndicatorDialog() == null && FindIdAny("StartEdit") == null; Thread.Sleep(100)) { }
            if (FindIdAny("StartEdit") != null)   // versehentlich Datum-Dialog -> Chart-Ansicht noetig
            {
                W("  [ind] Datum-Dialog statt Indikatoren -> schließen + 'Chart'-Reiter aktivieren");
                CloseDateDialogIfOpen(W);
                SelectChartRibbonTab(W);
                Thread.Sleep(600);
                continue;
            }
            if (IndicatorDialog() != null) return true;   // Indikator-Dialog (IndicatorsSettingsView) bestaetigt offen
        }
        return IndicatorDialog() != null;
    }

    // ===== UIA-gestuetzte, FAVORITENUNABHAENGIGE Recorder-Verwaltung im Indikator-Dialog =====
    // Der Dialog ist vollstaendig im UIA-Baum (IndicatorsSettingsView): Kategorieliste 'GroupsLayout',
    // Suchfeld 'SearchText'/'PART_Editor' (Value), Ergebnis-Grid 'AvailableIndicatorsGrid' (TreeItems mit Val=Name),
    // 'Hinzugefügt'-Gruppe 'SelectedIndicatorsLayout' + Liste 'IndicatorsList', Button 'Übernehmen' (Invoke).
    private const string REC_CATALOG_NAME = "OF Replay Recorder (read-only)";

    private static AutomationElement? IndicatorDialog()
    {
        foreach (var top in AtasRoots())
        { try { var e = top.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "IndicatorsSettingsView")); if (e != null) return e; } catch { } }
        return null;
    }
    private static AutomationElement? FindBtnIn(AutomationElement root, string name)
    { try { return root.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button), new PropertyCondition(AutomationElement.NameProperty, name))); } catch { return null; } }

    // 'Hinzugefügt (N)' -> Anzahl aus Gruppennamen + ob ein Replay-Recorder dort gelistet ist.
    private static (int count, bool hasRecorder) AddedState(AutomationElement dlg)
    {
        int count = -1; bool rec = false;
        var grp = FindByAutomationId(dlg, "SelectedIndicatorsLayout");
        if (grp != null) { var m = Regex.Match(Safe(() => grp.Current.Name), @"\((\d+)\)"); if (m.Success) int.TryParse(m.Groups[1].Value, out count); }
        var list = FindByAutomationId(dlg, "IndicatorsList");
        if (list != null)
            try { foreach (AutomationElement it in list.FindAll(TreeScope.Descendants, Condition.TrueCondition)) { var n = Safe(() => it.Current.Name) + " " + ValueOf(it); if (Regex.IsMatch(n, @"Replay\s*Recorder", RegexOptions.IgnoreCase)) rec = true; } } catch { }
        return (count, rec);
    }

    // Kategorie in der linken Liste waehlen (z. B. 'All'), damit die Suche unabhaengig von Favoriten funktioniert.
    // Alle Namen (Val) im Ergebnis-Grid.
    private static List<string> GridValues(AutomationElement dlg)
    {
        var res = new List<string>();
        var grid = FindByAutomationId(dlg, "AvailableIndicatorsGrid");
        if (grid != null) try { foreach (AutomationElement it in grid.FindAll(TreeScope.Descendants, Condition.TrueCondition)) { var v = ValueOf(it); if (!string.IsNullOrEmpty(v)) res.Add(v.Trim()); } } catch { }
        return res;
    }

    // Kategorie 'All' waehlen. Die Kategorie-ListItems sind NICHT im UIA-Baum (DevExpress-Virtualisierung) -> per
    // TASTATUR: Liste fokussieren + Pos1 (erstes Element == 'All'); Fallback Klick auf das oberste Listenelement
    // (aus dem LIVE-Rect der Liste, keine alten festen Koordinaten). VERIFIKATION: Die Suche ist kategorie-gebunden;
    // unter 'All' liefert eine Probe-Suche 'average' Treffer (globaler Katalog enthaelt Moving Averages), unter
    // Favorites(6) nicht. So wird 'All' objektiv bestaetigt.
    private static bool SelectCategoryAll(AutomationElement dlg, Action<string> W)
    {
        var groups = FindByAutomationId(dlg, "GroupsLayout");
        var list = groups != null ? (FindByAutomationId(groups, "PART_Editor") ?? groups) : null;
        if (list == null) { W("  [add] Kategorie-Liste (GroupsLayout) nicht gefunden."); return false; }
        try { list.SetFocus(); Thread.Sleep(150); KeyVk(0x24); Thread.Sleep(300); } catch { }   // Pos1 -> 'All'
        if (CategoryIsAll(dlg)) { W("  [add] Kategorie 'All' via Tastatur (Pos1) bestaetigt."); return true; }
        System.Windows.Rect r; try { r = list.Current.BoundingRectangle; } catch { r = System.Windows.Rect.Empty; }
        if (!r.IsEmpty) { try { ForceForeground(list); } catch { } ClickScreen((int)(r.X + r.Width / 2), (int)(r.Y + 12)); Thread.Sleep(300); }
        if (CategoryIsAll(dlg)) { W("  [add] Kategorie 'All' via Klick (oberstes Listenelement) bestaetigt."); return true; }
        W("  [add] WARN: 'All' nicht objektiv bestaetigt (Probe 'average' ohne Treffer)."); return false;
    }
    // Probe: unter 'All' liefert die Suche 'average' Treffer; andernfalls (z. B. Favorites) nicht.
    private static bool CategoryIsAll(AutomationElement dlg)
    {
        SetSearchText(dlg, "average", _ => { });
        for (int i = 0; i < 6; i++) { Thread.Sleep(300); if (GridValues(dlg).Count > 0) return true; }
        return false;
    }

    // Suchfeld fokussieren (UIA SetFocus), Inhalt vollstaendig ersetzen (Strg+A+Entf) und Text TIPPEN
    // (echte Tastendruecke loesen den Grid-Filter aus; SetValue allein tut das NICHT).
    private static void SetSearchText(AutomationElement dlg, string text, Action<string> W)
    {
        var outer = FindByAutomationId(dlg, "SearchText");
        var edit = (outer != null ? FindByAutomationId(outer, "PART_Editor") : null) ?? outer;
        if (edit == null) { W("  [add] Suchfeld (SearchText) nicht gefunden."); return; }
        try { edit.SetFocus(); Thread.Sleep(180); } catch { }
        KeyVk(0x41, ctrl: true); Thread.Sleep(70); KeyVk(0x2E); Thread.Sleep(120);
        TypeUnicode(text); Thread.Sleep(150);
        W($"  [add] Suche '{text}' -> Feld='{ValueOf(edit)}'");
    }

    // Exakten Treffer im Ergebnis-Grid finden (Val == exactName). KEIN beliebiger erster Treffer.
    private static AutomationElement? FindExactCatalogHit(AutomationElement dlg, string exactName)
    {
        var grid = FindByAutomationId(dlg, "AvailableIndicatorsGrid");
        if (grid == null) return null;
        try { foreach (AutomationElement it in grid.FindAll(TreeScope.Descendants, Condition.TrueCondition)) { if (string.Equals(ValueOf(it)?.Trim(), exactName, StringComparison.OrdinalIgnoreCase)) return it; } } catch { }
        return null;
    }

    // Fuegt den Recorder favoritenunabhaengig per UIA hinzu (ohne Favoriten zu aendern, ohne Namen zu aendern).
    private static bool AddRecorderViaDialog(Action<string> W)
    {
        if (!OpenIndicatorDialog(W)) { W("[add] BLOCKER: Indikator-Dialog nicht zu oeffnen."); return false; }
        var dlg = IndicatorDialog();
        if (dlg == null) { W("[add] BLOCKER: Dialog 'IndicatorsSettingsView' nicht im UIA-Baum."); return false; }
        var st0 = AddedState(dlg);
        if (st0.hasRecorder) { W($"[add] Recorder bereits unter 'Hinzugefügt' (count={st0.count}) -> kein doppeltes Hinzufuegen."); CancelDialog(dlg, W); return true; }
        // Einfach: Suchleiste -> 'replay' -> exakter Treffer -> Doppelklick -> Uebernehmen (kein Kategoriewechsel).
        SetSearchText(dlg, "replay", W);
        AutomationElement? hit = null;
        for (int i = 0; i < 12 && hit == null; i++) { Thread.Sleep(400); hit = FindExactCatalogHit(dlg, REC_CATALOG_NAME); }
        if (hit == null) { W($"[add] BLOCKER: exakter Treffer '{REC_CATALOG_NAME}' nicht in der Ergebnisliste gefunden (keine anderen Indikatoren ausprobiert)."); return false; }
        W($"[add] exakter Treffer: '{ValueOf(hit)}' rect={Rect(hit)}");
        try { if (hit.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp)) ((SelectionItemPattern)sp).Select(); } catch { }
        Thread.Sleep(80);
        System.Windows.Rect hr; try { hr = hit.Current.BoundingRectangle; } catch { hr = System.Windows.Rect.Empty; }
        if (hr.IsEmpty) { W("[add] BLOCKER: Treffer ohne Rect (nicht klickbar)."); return false; }
        DoubleClickFast((int)(hr.X + hr.Width / 2), (int)(hr.Y + hr.Height / 2));   // echter Doppelklick am TATSAECHLICHEN Element
        var dlg2 = dlg; var st1 = st0; bool appeared = false;
        for (var swA = Stopwatch.StartNew(); swA.ElapsedMilliseconds < 4000 && !appeared; Thread.Sleep(100))
        {
            dlg2 = IndicatorDialog() ?? dlg; st1 = AddedState(dlg2);
            appeared = st1.hasRecorder || (st1.count >= 0 && st1.count > (st0.count < 0 ? 0 : st0.count));
        }
        if (!appeared) { W($"[add] BLOCKER: Recorder nicht unter 'Hinzugefügt' erschienen (count {st0.count}->{st1.count}, recorder={st1.hasRecorder})."); return false; }
        W($"[add] unter 'Hinzugefügt' bestaetigt (count {st0.count}->{st1.count}, recorder={st1.hasRecorder}).");
        // Hinweis Portabilitaet: Der Recorder liest 'OutputRoot' nur EINMAL beim Start (Hinzufuegen) und re-initialisiert
        // nicht. Ein nachtraegliches Setzen pro Instanz ist daher wirkungslos. Der Zielordner wird stattdessen EINMALIG
        // als Standardvorlage in ATAS gesetzt ('Als Vorlage speichern'); jeder Add erbt ihn dann beim Start.
        var apply = FindBtnIn(dlg2, "Übernehmen");
        if (apply == null) { W("[add] BLOCKER: 'Übernehmen' nicht gefunden."); return false; }
        Invoke(apply);
        for (var swC = Stopwatch.StartNew(); swC.ElapsedMilliseconds < 5000 && IndicatorDialog() != null; Thread.Sleep(100)) { }
        W("[add] 'Übernehmen' ausgeloest.");
        return true;
    }

    // Eigenständiges, kontrolliertes Entfernen des Recorders am Zielchart (Cleanup). Verifiziert per Dateisystem.
    // Entfernt nach einem fruehen Abbruch den von diesem Auftrag angehaengten Recorder. Nutzt dieselben Bausteine wie der
    // regulaere Abschluss (Replay-Ansicht verlassen -> Indikator-Dialog -> Zeile entfernen -> Uebernehmen) und handelt nur,
    // wenn der Dialog den Recorder unter 'Hinzugefuegt' tatsaechlich zeigt. true = danach kein Recorder mehr angehaengt.
    private static bool RemoveRecorderAfterAbort(Action<string> W)
    {
        W("  [abbruch-cleanup] angehaengten Recorder entfernen …");
        CloseDateDialogIfOpen(W);
        if (ReplayOpen() || !IndicatorsEnabled()) ExitReplayView(W);
        for (int i = 0; i < 10 && !IndicatorsEnabled(); i++) Thread.Sleep(1000);
        var res = RemoveRecorderFromChart(W);
        return res == RemovalOutcome.Removed || (res == RemovalOutcome.NoRecorder && LockedCount() == 0);
    }


    // Chart-gebundener 'Indikatoren'-Button. Im historischen Wiedergabemodus ist er enabled=False
    // ("alle Handelsverbindungen deaktiviert"); nach echtem Verlassen der Replay-Ansicht wieder enabled=True.
    // Dient damit als UNABHAENGIGES, verifizierbares Signal (nicht Fenstertitel, nicht ReplayWindow-Abwesenheit).
    private static AutomationElement? IndicatorsButton()
        => FindIdAny("BarButtonItemLink0IndicatorsButton")
           ?? FindInRoots(AtasRoots(), ControlType.Button).FirstOrDefault(b => Safe(() => b.Current.Name) == "Indikatoren");
    private static bool IndicatorsEnabled() => IsEnabledSafe(IndicatorsButton());

    // Sucht einen Button ueber ALLE ATAS-Fenster anhand seines HelpText (Transport-Icons haben KEINEN Name, nur HelpText).
    private static AutomationElement? FindBtnByHelpAny(string help)
    {
        foreach (var b in FindInRoots(AtasRoots(), ControlType.Button))
        { try { if (string.Equals(Safe(() => b.Current.HelpText), help, StringComparison.OrdinalIgnoreCase)) return b; } catch { } }
        return null;
    }

    // Verlaesst die Replay-ANSICHT zuverlaessig ueber den TRANSPORT-Button 'Zur Echtzeit wechseln'
    // (HelpText='Zur Echtzeit wechseln', rect ~1241,320, pat=[Invoke]) - per UIA gefunden+invoked, KEINE festen
    // Koordinaten. Das blosse Schliessen des Transports / der Ribbon-Toggle beendet die Replay-ANSICHT NICHT
    // (Indikatoren bleibt dann enabled=False). Erfolg wird UNABHAENGIG verifiziert: 'Indikatoren' wird wieder aktiv.
    private static bool ExitReplayView(Action<string> W)
    {
        // Alle UIA-Abfragen begrenzt (waehrend 'Module laden' kann der ATAS-UI-Thread lange nicht antworten).
        bool IE() => Bounded("Indikatoren aktiv?", IndicatorsEnabled, 3000, false, W);
        bool RO() => Bounded("Replay offen?", ReplayOpen, 3000, true, W);
        if (IE() && !RO()) { W("[view] bereits ausserhalb Replay (Indikatoren aktiv, Transport zu) -> nichts zu tun."); return true; }

        // 'Zur Echtzeit wechseln' liegt im Transport. Ist der Transport zu, zuerst wieder oeffnen.
        // Layout-robust ueber den Cluster-Finder (nicht nur Fenstertitel) -> angedockter Transport wird NICHT faelschlich geoeffnet/geschlossen.
        if (!RO())
        {
            W("[view] Transport zu -> oeffne ihn (RibbonReplayButton), um 'Zur Echtzeit wechseln' zu erreichen.");
            var rb = FindIdAny("RibbonReplayButton");
            if (rb != null) { try { Invoke(rb); } catch { } }
            for (int i = 0; i < 8 && !RO(); i++) Thread.Sleep(600);
        }

        // WICHTIG: Am natuerlichen Ende REWINDET ATAS automatisch (Stopped -> LoadingModules -> Initializing -> Play).
        // Waehrend dieses Transients ('Module laden…') ist 'Zur Echtzeit wechseln' DEAKTIVIERT. Daher pollen, bis der
        // Button AKTIV ist (Replay spielt/pausiert wieder), ihn dann invoken; Erfolg = 'Indikatoren' wieder aktiv.
        bool invoked = false, ok = false, toLiveSeen = false;
        var swView = Stopwatch.StartNew();
        for (int i = 0; i < 160 && !ok; i++)   // 250-ms-Schritte, bis ~40s: deckt Rewind (~11s) + Settle ab
        {
            if (IE()) { ok = true; break; }
            var toLive = Bounded("UIA 'Zur Echtzeit wechseln'", () => FindBtnByHelpAny("Zur Echtzeit wechseln"), 3000, null, W);
            if (toLive != null) toLiveSeen = true;
            if (IsEnabledSafe(toLive))
            {
                W($"[view] 'Zur Echtzeit wechseln' aktiv -> Invoke (rect={Rect(toLive!)})");
                try { Invoke(toLive!); invoked = true; } catch (Exception ex) { W($"[view] Invoke-Fehler: {ex.Message}"); }
                for (int k = 0; k < 20 && !ok; k++) { Thread.Sleep(250); if (IE()) ok = true; }
            }
            else { Thread.Sleep(250); if (!toLiveSeen && i >= 48) break; }   // custom-gerendert: Button existiert gar nicht in UIA -> Fallback unten
        }
        // FALLBACK (custom-gerenderter Transport ohne UIA-'Zur Echtzeit wechseln'): Replay-Modus ueber die Ribbon-
        // Umschaltung verlassen. UNABHAENGIG verifiziert daran, dass der chart-gebundene 'Indikatoren'-Button wieder aktiv wird.
        if (!ok)
        {
            W("[view] 'Zur Echtzeit wechseln' nicht ueber UIA erreichbar -> Fallback: RibbonReplayButton (Replay-Modus AUS).");
            var rb = FindIdAny("RibbonReplayButton");
            if (rb != null) { try { Invoke(rb); } catch (Exception ex) { W($"[view] Ribbon-Invoke-Fehler: {ex.Message}"); } }
            for (int k = 0; k < 100 && !ok; k++) { Thread.Sleep(250); if (IE()) ok = true; }
        }
        if (!invoked && !ok) W("[view] BLOCKER: Replay-Ansicht weder ueber 'Zur Echtzeit wechseln' noch ueber die Ribbon-Umschaltung verlassen.");
        bool transportClosed = !RO();
        W($"[view] Ergebnis: Indikatoren aktiv={ok}  Transport {(transportClosed ? "zu" : "offen")}  -> Replay-Ansicht {(ok ? "VERLASSEN (verifiziert)" : "NICHT verlassen")}  ({swView.ElapsedMilliseconds} ms)");
        return ok;
    }

    // Beobachtet EIN kurzes, EXAKTES Replay-Fenster bis zum NATUERLICHEN Ende (kein manuelles Pausieren) und
    // protokolliert die ATAS-Log-Zustandsuebergaenge + Transport-Titel + Button-Zustaende -> Signatur des Replay-Endes.
    private static int WatchEnd(string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); if (outFile != null) { try { File.WriteAllText(outFile, sb.ToString()); } catch { } } }
        W($"# WatchEnd  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        string dStart = "26.10.2026 09:00:00", dEnd = "26.10.2026 09:00:20"; // 20 s Marktzeit, EXAKT (kein Puffer) -> natuerliches Ende
        CloseDateDialogIfOpen(W);   // keinen stale Datum-Dialog aus vorherigem Lauf wiederverwenden
        // SAUBERER Eintritt wie LiveProof: Replay zuerst GANZ aus (Transport zu), dann frisch oeffnen.
        // (Ein 'gestoppt'/realtime-Transport laedt neue Bereiche nicht zuverlaessig -> frisch initialisieren.)
        if (ReplayWindow() != null) { W("[entry] Replay vorhanden -> erst ganz schliessen"); var rbOff = FindIdAny("RibbonReplayButton"); if (rbOff != null) Invoke(rbOff); for (int i = 0; i < 10 && ReplayWindow() != null; i++) Thread.Sleep(800); }
        { var rb = FindIdAny("RibbonReplayButton"); if (rb != null) Invoke(rb); for (int i = 0; i < 15 && ReplayWindow() == null; i++) Thread.Sleep(1000); }
        if (ReplayWindow() == null) { W("BLOCKER: Replay oeffnet nicht."); Flush(sb, outFile); return 4; }
        W($"[entry] frischer Transport: '{Safe(() => ReplayWindow()?.Current.Name)}'");
        { string t = Safe(() => ReplayWindow()?.Current.Name ?? ""); if (t.Contains("Historienwiedergabe", StringComparison.OrdinalIgnoreCase) && !t.Contains("angehalten", StringComparison.OrdinalIgnoreCase) && !t.Contains("gestoppt", StringComparison.OrdinalIgnoreCase)) { var pb = FindIdAny("PlayButton"); if (pb != null) { Invoke(pb); Thread.Sleep(1200); } } }
        if (!EnsureModule(W)) { W("BLOCKER: Modul/Datum-Modus nicht aktivierbar."); Flush(sb, outFile); return 5; }
        var datum = FindBtnAny("Datum auswählen"); if (datum == null) { W("BLOCKER: 'Datum auswählen' fehlt."); Flush(sb, outFile); return 5; }
        Invoke(datum); for (int i = 0; i < 8 && FindIdAny("StartEdit") == null; i++) Thread.Sleep(600);
        if (FindIdAny("StartEdit") == null) { W("BLOCKER: Datum-Dialog oeffnet nicht."); Flush(sb, outFile); return 5; }
        bool okS = false, okE = false;
        for (int a = 0; a < 3 && !(okS && okE); a++) { TypeIntoField(FindIdAny("EndEdit"), dEnd); TypeIntoField(FindIdAny("StartEdit"), dStart); okS = ValueOf(FindIdAny("StartEdit")).StartsWith(dStart.Substring(0, 16), StringComparison.Ordinal); okE = ValueOf(FindIdAny("EndEdit")).StartsWith(dEnd.Substring(0, 16), StringComparison.Ordinal); }
        W($"[setup] Fenster=[{dStart},{dEnd}] okS={okS} okE={okE}");
        if (!(okS && okE)) { W("BLOCKER: Zeitraum nicht setzbar."); Flush(sb, outFile); return 5; }
        int preLoadN = TailLines(AtasLogPath(), 2_000_000).Length;
        var sel = FindBtnAny("Auswählen"); if (sel == null) { W("BLOCKER: 'Auswählen' fehlt."); Flush(sb, outFile); return 5; }
        W("[setup] Laden (Auswählen) …"); Invoke(sel);
        // Bestaetige, dass der Ladevorgang im Log tatsaechlich ausgeloest wurde (Initializing/Loading/Playing from).
        bool loadFired = false;
        for (int i = 0; i < 40 && !loadFired; i++)
        { Thread.Sleep(1000); var ln = TailLines(AtasLogPath(), 2_000_000); for (int k = preLoadN; k < ln.Length; k++) if (ln[k].Contains("Initializing to") || ln[k].Contains("Loading from") || ln[k].Contains("Playing from") || ln[k].Contains("State changed") && ln[k].Contains("Initializing")) { loadFired = true; break; } }
        W($"[setup] Ladevorgang im Log ausgeloest={loadFired}");
        for (int i = 0; i < 30 && !IsEnabledSafe(FindIdAny("PlayButton")); i++) Thread.Sleep(1000);
        if (!IsEnabledSafe(FindIdAny("PlayButton"))) { W("BLOCKER: PlayButton nicht bereit."); Flush(sb, outFile); return 6; }
        var speed = FindSpeedAny(); W($"[setup] Speed='{(speed != null ? Safe(() => speed.Current.Name) : "?")}'");
        var baseLines = TailLines(AtasLogPath(), 2_000_000); int baseN = baseLines.Length;
        var play = FindIdAny("PlayButton"); W("[play] Play (KEIN manuelles Pause - natuerliches Ende abwarten) …"); Invoke(play);
        // Falls die Wiedergabe nicht anlaeuft (Titel nicht 'laeuft'), EINMAL erneut Play.
        Thread.Sleep(2500);
        { string t = Safe(() => ReplayWindow()?.Current.Name ?? ""); if (!(t.Contains("Historienwiedergabe", StringComparison.OrdinalIgnoreCase) && !t.Contains("angehalten", StringComparison.OrdinalIgnoreCase) && !t.Contains("gestoppt", StringComparison.OrdinalIgnoreCase))) { W($"[play] Titel '{t}' -> erneut Play"); var p2 = FindIdAny("PlayButton"); if (p2 != null) Invoke(p2); } }
        var swp = Stopwatch.StartNew(); var printed = new HashSet<string>(); bool ended = false;
        while (swp.Elapsed.TotalSeconds < 90 && !ended)
        {
            Thread.Sleep(2000);
            var now = TailLines(AtasLogPath(), 2_000_000);
            for (int i = Math.Max(0, baseN); i < now.Length; i++)
            {
                var ln = now[i].Trim(); if (ln.Length == 0) continue;
                if (!(ln.Contains("Replay") || ln.Contains("chart state"))) continue;
                if (printed.Add(ln)) W("   LOG| " + ShortLog(ln));
                if (ln.Contains("Started -> Stopped") || ln.Contains("Started -> Paused") || ln.Contains("Play -> Stopped") || ln.Contains("-> Finished") || ln.Contains("Play -> Pausing")) ended = true;
            }
            string title = Safe(() => ReplayWindow()?.Current.Name ?? "");
            W($"   [t+{(int)swp.Elapsed.TotalSeconds}s] title='{title}' play.enabled={IsEnabledSafe(FindIdAny("PlayButton"))} play.help='{Safe(() => FindIdAny("PlayButton")?.Current.HelpText)}' step.enabled={IsEnabledSafe(FindBtnByHelpAny("Schritt vorwärts"))}");
        }
        W($"[ergebnis] ended-signal-beobachtet={ended} nach {(int)swp.Elapsed.TotalSeconds}s");
        Flush(sb, outFile);
        return 0;
    }

    // Diagnose: Indikator-Dialog oeffnen + dessen UIA-Baum dumpen (Suche nach Edit/Suche, Ergebnislisten,
    // 'Hinzugefuegt', 'Uebernehmen'). Findet den Dialog als Top-Level ODER als Descendant-Window/Pane des Hauptfensters.
    private static int IndProbe(string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }
        W($"# IndProbe  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        bool opened = OpenIndicatorDialog(W);
        W($"[open] OpenIndicatorDialog={opened}");
        Thread.Sleep(900);
        var pids = Process.GetProcessesByName("OFT.Platform").Select(p => p.Id).ToHashSet();
        AutomationElement? dlg = TopWindows(pids).FirstOrDefault(w => Safe(() => w.Current.Name).Contains("Indikator", StringComparison.OrdinalIgnoreCase));
        if (dlg != null) W($"[dialog] Top-Level gefunden: '{Safe(() => dlg.Current.Name)}'");
        if (dlg == null)
            foreach (var top in TopWindows(pids))
            {
                foreach (var ct in new[] { ControlType.Window, ControlType.Pane, ControlType.Custom })
                { try { var e = top.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ct), new PropertyCondition(AutomationElement.NameProperty, "Indikatoren"))); if (e != null) { dlg = e; break; } } catch { } }
                if (dlg != null) { W($"[dialog] Descendant gefunden unter '{Safe(() => top.Current.Name)}'"); break; }
            }
        if (dlg != null)
        {
            // Kategorieliste RAW dumpen (virtualisierte Items evtl. nur im RawView): Name/Value/ControlType + Patterns.
            var groups = FindByAutomationId(dlg, "GroupsLayout");
            W("\n-- GroupsLayout (Kategorien) RAW --");
            if (groups != null)
            {
                try
                {
                    var w2 = TreeWalker.RawViewWalker; var c = w2.GetFirstChild(groups); int gi = 0;
                    void Rec(AutomationElement e, int d) { if (e == null || gi > 200) return; gi++; string ct = e.Current.ControlType?.ProgrammaticName?.Replace("ControlType.", "") ?? ""; string nm = Safe(() => e.Current.Name), val = ValueOf(e); if (!string.IsNullOrEmpty(nm) || !string.IsNullOrEmpty(val)) W($"{new string(' ', d * 2)}[{ct}] Name='{Trunc(nm, 30)}' Val='{Trunc(val, 30)}' pat=[{string.Join(",", SupportedPatterns(e))}] rect={Rect(e)}"); var ch = w2.GetFirstChild(e); while (ch != null) { Rec(ch, d + 1); ch = w2.GetNextSibling(ch); } }
                    while (c != null) { Rec(c, 0); c = w2.GetNextSibling(c); }
                }
                catch (Exception ex) { W($"  RAW-Fehler: {ex.Message}"); }
            }
            // Suchfeld: mit REALEN Tastendruecken (SetFocus) testen, damit der Grid-Filter feuert.
            var outer = FindByAutomationId(dlg, "SearchText"); var edit = (outer != null ? FindByAutomationId(outer, "PART_Editor") : null) ?? outer;
            W($"\n-- SearchText edit: {(edit != null ? $"pat=[{string.Join(",", SupportedPatterns(edit))}] val0='{ValueOf(edit)}'" : "nicht gefunden")}");
            if (edit != null)
            {
                try { edit.SetFocus(); Thread.Sleep(200); } catch (Exception ex) { W($"   SetFocus-Fehler: {ex.Message}"); }
                string term = Environment.GetEnvironmentVariable("IND_SEARCH") ?? "replay";
                KeyVk(0x41, ctrl: true); Thread.Sleep(70); KeyVk(0x2E); Thread.Sleep(120);
                TypeUnicode(term); Thread.Sleep(800);
                W($"   nach Tastatur '{term}': val='{ValueOf(edit)}'");
            }
            var grid2 = FindByAutomationId(dlg, "AvailableIndicatorsGrid");
            if (grid2 != null) { W("   Grid-Treffer nach Tastatursuche:"); try { foreach (AutomationElement it in grid2.FindAll(TreeScope.Descendants, Condition.TrueCondition)) { var v = ValueOf(it); if (!string.IsNullOrEmpty(v)) W($"      '{v}'"); } } catch { } }
            int n = 0; DumpDialog(dlg, 0, W, ref n); W($"[info] Knoten={n}");
        }
        else
        {
            W("[dialog] 'Indikatoren' NICHT als UIA-Fenster/Pane gefunden -> dumpe Hauptfenster-Subtree (gefiltert).");
            var main = TopWindows(pids).FirstOrDefault(w => Safe(() => w.Current.Name).StartsWith("ATAS -", StringComparison.OrdinalIgnoreCase));
            if (main != null) { int n = 0; DumpDialog(main, 0, W, ref n); W($"[info] Knoten={n}"); }
        }
        Flush(sb, outFile);
        return 0;
    }

    // Gezielter Add/Remove-Test (UIA-Add favoritenunabhaengig). Add -> per Dateisystem bestaetigen -> Hinzugefügt-Struktur
    // dumpen -> Remove (bestehender Pfad) -> per Dateisystem bestaetigen. Laesst am Ende keinen Recorder angehaengt (bei Erfolg).
    private static int AddRecTest(string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }
        W($"# Add/Remove-Test (UIA)  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        if (Process.GetProcessesByName("OFT.Platform").Length == 0) { W("BLOCKER: ATAS laeuft nicht."); Flush(sb, outFile); return 3; }
        if (ReplayWindow() != null) { W("[prep] Replay aktiv -> verlassen"); var rb = FindIdAny("RibbonReplayButton"); if (rb != null) Invoke(rb); for (int i = 0; i < 8 && ReplayWindow() != null; i++) Thread.Sleep(1000); }
        int before = LockedCount(); var pre = FolderSet();
        W($"[vorher] gesperrte Instanzen={before}");
        // ADD (UIA)
        if (!AddRecorderViaDialog(W)) { W("[test] ADD (UIA) fehlgeschlagen."); Flush(sb, outFile); return 6; }
        bool added = false; string? folder = null;
        for (int i = 0; i < 15 && !added; i++) { Thread.Sleep(1000); var nf = FolderSet().Except(pre).Where(f => IsMetaLocked(Path.Combine(RecordingsDir, f, "meta.json"))).ToList(); if (nf.Count > 0) { added = true; folder = nf[0]; } }
        if (!added) { W("[test] ADD nicht per Dateisystem bestaetigt (kein neuer gesperrter Ordner)."); Flush(sb, outFile); return 6; }
        W($"[test] ADD OK: neuer gesperrter Ordner {folder}  (gesperrt jetzt={LockedCount()})");
        // Hinzugefügt-Struktur dumpen (best effort; fuer robustes Remove)
        var dlg = IndicatorDialog();
        if (dlg != null) { var list = FindByAutomationId(dlg, "IndicatorsList"); if (list != null) { W("-- 'Hinzugefügt'-Liste (UIA) --"); int n = 0; DumpDialog(list, 0, W, ref n); } }
        // REMOVE (bestehender Pfad)
        int rc = RemoveRecorderCommand(null);
        int after = LockedCount();
        W($"[test] REMOVE rc={rc}; gesperrte Instanzen jetzt={after}");
        bool ok = rc == 0 && after <= before;
        W($"[test] ERGEBNIS Add/Remove: {(ok ? "OK" : "FEHLER")} (before={before}, after={after})");
        Flush(sb, outFile);
        return ok ? 0 : 8;
    }

    // NUR Hinzufuegen: Indikatoren-Dialog -> Suche 'replay' -> exakter Treffer -> Doppelklick -> Uebernehmen.
    // Verifiziert per Dateisystem (neuer gesperrter Ordner). Laesst den Recorder angehaengt (kein Entfernen).
    private static int AddOnly(string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }
        W($"# Add-Only (UIA)  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        if (Process.GetProcessesByName("OFT.Platform").Length == 0) { W("BLOCKER: ATAS laeuft nicht."); Flush(sb, outFile); return 3; }
        if (ReplayWindow() != null) { W("[prep] Replay aktiv -> verlassen"); var rb = FindIdAny("RibbonReplayButton"); if (rb != null) Invoke(rb); for (int i = 0; i < 8 && ReplayWindow() != null; i++) Thread.Sleep(1000); }
        int before = LockedCount(); var pre = FolderSet();
        W($"[vorher] gesperrte Instanzen={before}");
        if (!AddRecorderViaDialog(W)) { W("[add] fehlgeschlagen."); Flush(sb, outFile); return 6; }
        bool added = false; string? folder = null;
        for (int i = 0; i < 15 && !added; i++) { Thread.Sleep(1000); var nf = FolderSet().Except(pre).Where(f => IsMetaLocked(Path.Combine(RecordingsDir, f, "meta.json"))).ToList(); if (nf.Count > 0) { added = true; folder = nf[0]; } }
        W($"[ergebnis] hinzugefuegt(Dateisystem)={added} Ordner={folder ?? "-"} gesperrt jetzt={LockedCount()}");
        Flush(sb, outFile);
        return added ? 0 : 6;
    }

    // Standalone-Test fuer ExitReplayView (gibt den verifizierten Zustand aus).
    private static int ExitViewCmd()
    {
        void W(string s) => Console.WriteLine(s);
        W($"# ExitReplayView-Test {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        W($"[vorher] Indikatoren aktiv={IndicatorsEnabled()}  ReplayWindow={(ReplayWindow() == null ? "zu" : "offen")}");
        bool left = ExitReplayView(W);
        W($"[ergebnis] Replay-Ansicht verlassen={left}");
        return left ? 0 : 8;
    }

    // Einzelner LiveProof (ein Fenster) ueber den bewaehrten Ablauf. Duenner Wrapper auf RunRecordingJob.
    private static int LiveProof(string? outFile)
        => RunRecordingJob(outFile, "26.10.2026 14:30:00", "26.10.2026 15:00:00", 1, 1, null, "100x");

    // EIN Aufnahmeauftrag (bewaehrter Ablauf). Zeitraum (dStart/dEnd, Dialog/Berlin) ist Parameter; Zielchart ueber
    // ATAS_TARGET_CHART. Mehrere Auftraege teilen sich sharedSb (ein Report) und outFile. rc==0 == GESAMT-PASS.
    // Altaufrufer (Berlin-Wandzeit): nach UTC umrechnen und den UTC-Auftrag ausfuehren.
    private static int RunRecordingJob(string? outFile, string dStartBerlin, string dEndBerlin, int jobNo, int jobCount, StringBuilder? sharedSb, string speedName = "1x")
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        return RunRecordingJob(outFile, BerlinToUtc(DateTime.ParseExact(dStartBerlin, "dd.MM.yyyy HH:mm:ss", ci)),
            BerlinToUtc(DateTime.ParseExact(dEndBerlin, "dd.MM.yyyy HH:mm:ss", ci)), jobNo, jobCount, sharedSb, speedName);
    }

    private static int RunRecordingJob(string? outFile, DateTime sUtc, DateTime eUtc, int jobNo, int jobCount, StringBuilder? sharedSb, string speedName = "1x")
    {
        var sb = sharedSb ?? new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); if (outFile != null) { try { File.WriteAllText(outFile, sb.ToString()); } catch { } } }
        double tBed = 0, tLoad = 0, tPlay = 0; var swAll = Stopwatch.StartNew();
        string targetChart = Environment.GetEnvironmentVariable("ATAS_TARGET_CHART") ?? "";  // leer = einziger Chart
        // Zeitbezuege GETRENNT fuehren: Soll-Grenzen in UTC (unveraenderlich) -> Eingabe im ATAS-Datumsdialog (dessen
        // Zeitzone) -> von ATAS bestaetigter Ladebereich (Log, UTC) -> aufgezeichneter Datenbereich (Recorder, UTC).
        sUtc = DateTime.SpecifyKind(sUtc, DateTimeKind.Utc); eUtc = DateTime.SpecifyKind(eUtc, DateTimeKind.Utc);
        var zone = DialogZone.Resolve();
        string dStart = zone.FromUtc(sUtc).ToString("dd.MM.yyyy HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        string dEnd = zone.FromUtc(eUtc).ToString("dd.MM.yyyy HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        // Fensterdauer (s) fuer fensterabhaengige Zeitlimits (Laden/Watchdog/Writer-Flush).
        int windowSec = Math.Max(1, (int)(eUtc - sUtc).TotalSeconds);
        W($"\n# ATAS Aufnahmeauftrag {jobNo}/{jobCount}  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  Ausgabe: {RecordingsDir}");
        W($"# Soll (UTC, unveraenderlich): [{sUtc:yyyy-MM-dd HH:mm:ss}Z, {eUtc:yyyy-MM-dd HH:mm:ss}Z)");
        W($"# Dialog-Eingabe: [{dStart}, {dEnd}) in {zone.Describe(sUtc)}; Quelle: {zone.Source}. Annahme - wird gegen den von ATAS bestaetigten Ladebereich geprueft.");
        W($"# Zielchart: {(targetChart == "" ? "(einziger Chart)" : targetChart)}");
        if (!DriveReadyWritable(RecordingsDir, out var rdr)) { W("BLOCKER: " + rdr); return 3; }
        if (Process.GetProcessesByName("OFT.Platform").Length == 0) { W("BLOCKER: ATAS laeuft nicht."); return 3; }

        string? attrFolder = null; string attrRunId = ""; int rc = -1; bool cleanupOk = false;
        var alog = new IncrementalAtasLog(Path.GetDirectoryName(AtasLogPath())!);
        var exec = new ReplayExecution();
        bool? speedVerified = null;
        bool recorderAddedByJob = false;  // dieser Auftrag hat einen Recorder angehaengt -> bei fruehem Abbruch wieder entfernen
        HashSet<string> preJobFolders = new();
        bool provisionalAttr = false;
        string? jobAddedFolder = null;    // in Schritt 3 von DIESEM Auftrag angelegte Instanz     // Instanz nicht beim Laden neu erzeugt -> bestehende (leere) Instanz dieses Auftrags uebernommen
        // GETRENNTE Teilergebnisse (Erfolg nur, wenn ALLE erforderlichen erfuellt sind):
        bool endProven = false;          // unabhaengiges, verdrahtetes Replay-ENDSIGNAL erreicht? (sonst Unproven)
        bool dataWindowRecorded = false; // Daten-KORROBORATION: Fortschritt bis Ende waehrend Abspielen (KEIN Endbeweis)
        bool replayViewLeft = false;     // Chart hat die Replay-ANSICHT verlassen? (offene Funktion, s.u.)
        bool recorderRemoved = false;    // Recorder am Zielchart entfernt (keine aktive Instanz mehr)?
        string endStatus = "Unproven"; string endReason = "n/a"; string span = "-", mpStr = "-"; long ticksBytes = 0; double offMin = 0;
        try
        {
            var swB = Stopwatch.StartNew();
            // ---- Phase 0: offenen Modaldialog kontrolliert behandeln ----
            CloseDateDialogIfOpen(W);
            // ---- Phase 1: Zielchart eindeutig ----
            if (!VerifyTargetChartUnique(W)) throw new AbortRun(5, "BLOCKER: Zielchart nicht eindeutig identifizierbar.");
            // ---- Phase 2: Replay-ANSICHT verlassen ('Zur Echtzeit wechseln') + STABILE Editierbarkeit pruefen ----
            if (ReplayOpen() || !IndicatorsEnabled())
            {
                W("[2] Replay-Ansicht verlassen ('Zur Echtzeit wechseln') + Editierbarkeit pruefen");
                ExitReplayView(W);
            }
            // Auf STABILE Editierbarkeit warten ('Indikatoren' aktiv auf 2 aufeinanderfolgenden Pruefungen; kann waehrend
            // eines Auto-Rewind-Transienten kurz flackern). Bei anhaltend inaktiv: noch einmal gezielt verlassen.
            int editSeen = 0;
            for (int i = 0; i < 18 && editSeen < 2; i++)
            {
                Thread.Sleep(1000);
                editSeen = IndicatorsEnabled() ? editSeen + 1 : 0;
                if (i == 8 && editSeen == 0 && (ReplayOpen() || !IndicatorsEnabled())) ExitReplayView(W);   // zweiter gezielter Versuch
            }
            if (editSeen < 2) throw new AbortRun(4, "BLOCKER: Chart nicht stabil editierbar ('Indikatoren' inaktiv) nach Replay-Verlassen.");
            W("[2] Chart editierbar ('Indikatoren' aktiv, stabil).");
            // Replay-Oeffner VOR dem Hinzufuegen pruefen: fehlt er, wird gar nicht erst ein Recorder angehaengt
            // (VM-Befund: frueher Abbruch in Phase 4 liess einen Recorder am Chart zurueck).
            if (FindIdAny("RibbonReplayButton") == null) throw new AbortRun(4, "BLOCKER: RibbonReplayButton fehlt (vor dem Hinzufuegen geprueft; kein Recorder angehaengt).");
            // ---- Phase 3: Recorder pruefen, bei Bedarf hinzufuegen (Verifikation per Dateisystem) ----
            preJobFolders = FolderSet();   // Ordnerstand VOR diesem Auftrag (fuer die Instanz-Zuordnung in Phase 7)
            if (LockedCount() >= 1) W($"[3] Recorder bereits aktiv ({LockedCount()} gesperrte Instanz) -> kein Hinzufuegen noetig.");
            else
            {
                W("[3] Kein aktiver Recorder -> hinzufuegen (UIA-Dialog, favoritenunabhaengig: Kategorie 'All' + Suche + exakter Treffer)");
                var before = FolderSet();
                if (!AddRecorderViaDialog(W)) throw new AbortRun(6, "BLOCKER: Recorder-Hinzufuegen (UIA) fehlgeschlagen.");
                recorderAddedByJob = true;
                bool added = false;
                for (var swR = Stopwatch.StartNew(); swR.ElapsedMilliseconds < 15000 && !added; ) { Thread.Sleep(200); added = FolderSet().Except(before).Any(f => IsMetaLocked(Path.Combine(RecordingsDir, f, "meta.json"))); }
                if (!added) throw new AbortRun(6, $"BLOCKER: Recorder-Hinzufuegen nicht per Dateisystem bestaetigt (kein neuer gesperrter Ordner unter '{RecordingsDir}'). " +
                    "Der Recorder schreibt nicht in den erwarteten Ordner oder ist beim Start abgebrochen.");
                W("[3] Recorder hinzugefuegt + aktiv (neuer gesperrter Ordner).");
                var addedLocked = FolderSet().Except(before).Where(f => IsMetaLocked(Path.Combine(RecordingsDir, f, "meta.json"))).ToList();
                if (addedLocked.Count == 1) jobAddedFolder = addedLocked[0];
                // Tatsaechlichen Schreibpfad belegen: neuer Ordner liegt unter RecordingsDir; der Recorder nennt zusaetzlich
                // seine Quelle in meta.json (ab proto-2c). Aeltere DLLs schreiben diese Felder nicht.
                foreach (var nf in FolderSet().Except(before))
                {
                    var mp0 = Path.Combine(RecordingsDir, nf, "meta.json");
                    W($"[3] Schreibpfad: {Path.Combine(RecordingsDir, nf)}  recorder='{ReadMetaField(mp0, "recorder")}' outputRoot='{ReadMetaField(mp0, "outputRoot")}' quelle='{ReadMetaField(mp0, "outputRootSource")}'");
                }
            }
            tBed += swB.Elapsed.TotalSeconds;

            // ---- Phase 4: Replay-Transport ERKENNEN (angedockt/schwebend/verschoben), fensteruebergreifend, KEIN Toggle-Thrashing ----
            var swL = Stopwatch.StartNew();
            if (FindIdAny("RibbonReplayButton") == null) throw new AbortRun(4, "BLOCKER: RibbonReplayButton fehlt.");
            Thread.Sleep(800);    // kurzes Settle nach Uebernehmen (Recorder-Start ist bereits per Dateisystem bestaetigt)
            VisualTransport? vt = null; bool visualMode = false;
            var trans = FindReplayTransport(out var tdiag);
            if (trans == null && tdiag.StartsWith("MEHRDEUTIG")) throw new AbortRun(4, "BLOCKER: " + tdiag);
            if (trans != null)
            {
                W($"[4] Transport bereits offen in '{trans.HostName}' (Start-Modus {trans.StartMode}) -> kein Toggle.");
            }
            else if (ReplayWindow() == null)
            {
                // Kein UIA-Transport UND kein eigenes 'Replay'-Fenster -> Transportleiste NICHT sichtbar (zu ODER eingeklappt;
                // 'Zur Echtzeit wechseln' laesst den Wiedergabemodus an, blendet die Leiste aber aus). Praesenz-Check an der
                // Fensterzahl (Nutzer-Hinweis: sichtbare Leiste => eigenes 'Replay'-Fenster NEBEN dem Chart = zwei Fenster).
                // Wieder einblenden: PRIMAER das Replay-Ribbon TOGGELN - ein frischer Replay-Eintritt zeigt die Leiste
                // AUFGEKLAPPT (am 26.10. belegt: Ribbon -> 'Replay'-Fenster, sogar UIA-Transport). Ist der Wiedergabemodus
                // noch an + Leiste eingeklappt, betritt das Toggle (AUS->EIN) ihn frisch und aufgeklappt neu. SEKUNDAER:
                // den Einklapp-Chevron am Chart aufklappen. Eingriff NUR, wenn KEIN 'Replay'-Fenster existiert -> ein
                // funktionierender Transport wird nie geschlossen.
                for (int attempt = 0; attempt < 6 && ReplayWindow() == null; attempt++)
                {
                    var rb = FindIdAny("RibbonReplayButton");
                    if (rb != null) { W($"[4] keine Transportleiste sichtbar -> Replay-Ribbon toggeln (Versuch {attempt + 1})"); Invoke(rb); }
                    for (int i = 0; i < 10 && ReplayWindow() == null; i++) Thread.Sleep(1000);
                    if (ReplayWindow() != null) break;
                    W($"[4] weiterhin kein 'Replay'-Fenster -> Einklapp-Chevron am Chart aufklappen (Versuch {attempt + 1})");
                    if (TryExpandCollapsedTransport(W)) for (int i = 0; i < 8 && ReplayWindow() == null; i++) Thread.Sleep(1000);
                }
                W($"[4] Transportleiste sichtbar? ReplayFenster={(ReplayWindow() != null ? "ja ('" + Safe(() => ReplayWindow()?.Current.Name) + "')" : "nein")}");
            }
            else W($"[4] 'Replay'-Fenster vorhanden ('{Safe(() => ReplayWindow()?.Current.Name)}') -> Transportleiste sichtbar, Geometrie per OCR aufloesen.");
            for (int i = 0; i < 45 && trans == null && vt == null; i++)
            {
                Thread.Sleep(2000); trans = FindReplayTransport(out tdiag);
                if (trans == null && tdiag.StartsWith("MEHRDEUTIG")) throw new AbortRun(4, "BLOCKER: " + tdiag);
                if (trans == null) vt = OcrDetectFresh(out _);   // UIA-Transport fehlt -> VISUELLEN (OCR) Transport pruefen
            }
            if (trans == null && vt == null) throw new AbortRun(4, "BLOCKER: Replay-Transport weder ueber UIA noch visuell (OCR) erkannt (nach Ribbon-/Chevron-Recovery, 90s). " + tdiag);
            if (trans != null)
                W($"[4] Transport zugaenglich (UIA) in '{trans.HostName}' @{(int)trans.Bounds.X},{(int)trans.Bounds.Y} {(int)trans.Bounds.Width}x{(int)trans.Bounds.Height}: " +
                  $"Start-Modus={trans.StartMode}, Play={ElVisibleOperable(trans.PlayBtn)}, Speed={(trans.SpeedBtn != null)}, Settings={(trans.SettingsBtn != null)}.");
            else
            {
                visualMode = true;
                W($"[4] UIA-Transport NICHT verfuegbar (custom-gerendert, 0 UIA-Buttons) -> VISUELLER FALLBACK (OCR) AKTIV: " +
                  $"Start-Modus={vt!.StartMode}, 'Datum'@{vt.StartClick.x},{vt.StartClick.y}, Play@{(vt.PlayClick?.x ?? -1)},{(vt.PlayClick?.y ?? -1)}, Leiste y{vt.BarY}.");
            }
            // ---- Phase 5: In den Datum-Auswahl-Modus bringen ('Bar'/'Zeitraum' != 'Datum' erkennen & gezielt umschalten) ----
            if (visualMode)
            {
                if (vt!.StartMode != "Datum")
                    throw new AbortRun(5, $"BLOCKER: Visueller Transport im Start-Modus '{vt.StartMode}' (nicht 'Datum'); visueller Moduswechsel ist in diesem Lauf nicht freigegeben -> begrenzter Abbruch (Diagnose statt blinder Aktion).");
                W("[5] (visuell) Start-Modus 'Datum' per OCR bestaetigt.");
            }
            else
            {
                if (trans!.StartMode != "Datum")
                {
                    W($"[5] Start-Modus '{trans.StartMode}' erkannt -> gezielt auf 'Datum auswählen' umschalten");
                    if (!SwitchToDatumMode(W)) throw new AbortRun(5, "BLOCKER: Umschalten auf 'Datum auswählen' nicht bestaetigt (Transport vorhanden, aber Modus nicht setzbar).");
                }
                if (!IsEnabledSafe(FindBtnAny("Datum auswählen")))
                {
                    W("[5] 'Datum auswählen' nicht aktiv -> Modul/Modus pruefen");
                    if (!EnsureModule(W)) throw new AbortRun(5, "BLOCKER: Datum-Modus/Modul nicht aktivierbar.");
                }
            }
            // ---- Phase 6: Datum-Dialog oeffnen (UIA ODER visueller Klick), Zeitraum setzen und zuruecklesen ----
            if (visualMode)
            {
                var vtf = OcrDetectFresh(out var vd);   // VOR dem Klick frisch aufloesen (Position NICHT gespeichert)
                if (vtf == null || vtf.StartMode != "Datum") throw new AbortRun(5, "BLOCKER: (visuell) 'Datum auswählen' nicht frisch per OCR aufloesbar: " + vd);
                W($"[6] (visuell) Klick 'Datum auswählen' @{vtf.StartClick.x},{vtf.StartClick.y}");
                ClickFloat(vtf.StartClick.x, vtf.StartClick.y);
                for (int i = 0; i < 12 && FindIdAny("StartEdit") == null; i++) Thread.Sleep(600);
                if (FindIdAny("StartEdit") == null) throw new AbortRun(5, "BLOCKER: (visuell) Datum-Dialog nach Klick NICHT offen (StartEdit fehlt) -> Klickziel/Zustand unsicher, Abbruch.");
                W("[6] (visuell) Datum-Dialog offen (StartEdit UIA bestaetigt).");
            }
            else
            {
                var datumBtn = FindBtnAny("Datum auswählen");
                if (datumBtn == null) throw new AbortRun(5, "BLOCKER: 'Datum auswählen' nicht gefunden.");
                Invoke(datumBtn); for (int i = 0; i < 8 && FindIdAny("StartEdit") == null; i++) Thread.Sleep(600);
                if (FindIdAny("StartEdit") == null) throw new AbortRun(5, "BLOCKER: Datum-Dialog oeffnet nicht.");
            }
            // 1.3.9: Eingabe ohne Leeren, Reihenfolge Start:=Zieltag 00:00 -> Ende -> Start (siehe SetDialogRange in
            // DateEntry.cs; Befund 18.11.: Ende blieb nach 'Strg+A/Entf' + Mitschieben auf 05.11. 00:00 stehen).
            SetDialogRange(dStart, dEnd, W, trace: false);
            {
                string gs = ValueOf(FindIdAny("StartEdit")), ge = ValueOf(FindIdAny("EndEdit"));
                bool okS = gs.StartsWith(dStart.Substring(0, 16), StringComparison.Ordinal);
                bool okE = ge.StartsWith(dEnd.Substring(0, 16), StringComparison.Ordinal);
                W($"[6] Zeitraum zurueckgelesen: Start='{gs}' Ende='{ge}' (okS={okS} okE={okE})");
                if (!(okS && okE)) throw new AbortRun(5, "BLOCKER: Zeitraum nicht verlaesslich setzbar/zurueckgelesen.");
            }
            // ---- Phase 7: LOAD + Instanz-Zuordnung (genau ein neuer gesperrter Ordner) ----
            var beforeLoad = FolderSet(); DateTime loadClick = DateTime.UtcNow.AddSeconds(-5);
            var sel = FindBtnAny("Auswählen"); if (sel == null) throw new AbortRun(5, "BLOCKER: 'Auswählen' nicht gefunden.");
            int logBaseIdx = TailLines(AtasLogPath(), 4_000_000).Length;   // Log-Leseposition VOR dem Laden dieses Auftrags (execution-scope)
            alog.SeekToEnd();                                              // inkrementelles Lesen ab hier: nur Zeilen DIESES Auftrags
            W("[7] Laden (Auswählen) …"); Invoke(sel);
            List<string> NewLocked() => FolderSet().Except(beforeLoad).Where(f =>
            {
                var meta = Path.Combine(RecordingsDir, f, "meta.json");
                if (!IsMetaLocked(meta)) return false;
                var sw = ParseDt(ReadMetaField(meta, "startWallUtc"));
                return sw != null && sw.Value.ToUniversalTime() >= loadClick && ReadMetaField(meta, "instrument").Contains("MNQ", StringComparison.OrdinalIgnoreCase);
            }).ToList();
            bool ready = false; var cands = new List<string>(); string lastLog = "";
            int loadTimeout = Math.Max(180, windowSec / 20);   // session-abhaengig: grosse Fenster brauchen laenger zum Laden (1-Min-Fenster bleibt 180s)
            // 1.3.5 (VM-Befund 17.11.: ATAS meldete 'Initialized', aber KEINE neue Recorder-Instanz -> die Probe wartete bis zu
            // 450 s ohne jede Ausgabe und kam nie zu Play). Jetzt: Statuszeile alle 10 s; Ladeende aus dem ATAS-Protokoll DIESES
            // Ladevorgangs; danach begrenzte Nachfrist, belegte Ersatzwege, sonst sofortiger Abbruch mit Diagnose.
            // 1.3.8 (VM-Befund 18.11., Folgetag 27.10.): Laden + Play bereit, aber keine NEUE Instanz; die Probe sendete wegen
            // 'Indikatoren aktiv' Strg+Alt+R (danach Disconnected, Play nicht mehr bereit) und wartete bis zum Ladelimit.
            // Jetzt: KEIN Kuerzel in Phase 7. 'Indikatoren aktiv' ist nach dem Laden kein Beleg fuer "nicht in Replay" (am 26.10.
            // stand dort 'NEIN (Indikatoren aktiv)' und der Tag lief durch); UIA-Zeitgrenze = "unbekannt". Nach dem Laden:
            // neue Instanz -> wie bisher; sonst die in Schritt 3 angelegte Instanz, wenn eindeutig + aktiv + ohne Trades im
            // Replay-Bereich; sonst nach Nachfrist mit konkretem Grund abbrechen (Aufraeumen im finally). Play nicht bereit
            // nach dem Laden -> begrenzt warten, dann Abbruch statt Ladelimit.
            var swLoad = Stopwatch.StartNew(); long loadedAtMs = -1; bool sawInit = false, readdTried = false;
            long lastBeatMs = -10_000;
            List<string> JobLocked() => FolderSet().Except(preJobFolders).Where(f => IsMetaLocked(Path.Combine(RecordingsDir, f, "meta.json"))
                && ReadMetaField(Path.Combine(RecordingsDir, f, "meta.json"), "instrument").Contains("MNQ", StringComparison.OrdinalIgnoreCase)).ToList();
            string IndState() => Bounded<bool?>("Indikatoren aktiv?", () => IndicatorsEnabled(), 3000, null, W) switch
            { true => "aktiv", false => "inaktiv", null => "unbekannt (UIA-Zeitgrenze)" };
            while (swLoad.ElapsedMilliseconds < loadTimeout * 1000L && !ready)
            {
                Thread.Sleep(1000); cands = NewLocked();
                var newLines = alog.ReadNew(); exec.Feed(newLines);
                foreach (var nl in newLines)
                {
                    if (nl.Contains("Replay") && nl.Contains("-> Initializing")) sawInit = true;
                    if (nl.Contains("Replay") && nl.Contains("Initializing -> Initialized") && sawInit && loadedAtMs < 0)
                    { loadedAtMs = swLoad.ElapsedMilliseconds; W($"    [t+{loadedAtMs / 1000}s] ATAS: Laden abgeschlossen (Initialized)."); }
                    if (nl.Contains("Replay") && nl.Contains("Initialized -> Started"))
                        W($"    [t+{swLoad.ElapsedMilliseconds / 1000}s] HINWEIS: Wiedergabe wurde gestartet, OHNE dass die Steuerung Play gedrueckt hat (manuell?): {ShortLog(nl)}");
                    if (nl.Contains("Replay") && nl.Contains("Disconnected"))
                        W($"    [t+{swLoad.ElapsedMilliseconds / 1000}s] ATAS: {ShortLog(nl)}");
                }
                string lg = LatestReplayState();
                if (lg != lastLog) { lastLog = lg; W($"    [t+{swLoad.ElapsedMilliseconds / 1000}s] neueInstanz={cands.Count} | {lg}"); }
                bool playReady = visualMode ? OcrReadyToPlay(vt!.BarY, out _) : Bounded("PlayButton bereit?", () => IsEnabledSafe(FindIdAny("PlayButton")), 4000, false, W);
                if (cands.Count >= 1 && playReady && swLoad.ElapsedMilliseconds >= 3000) { ready = true; break; }

                if (swLoad.ElapsedMilliseconds - lastBeatMs >= 10_000)
                {
                    lastBeatMs = swLoad.ElapsedMilliseconds;
                    W($"    [t+{swLoad.ElapsedMilliseconds / 1000}s] warte: neueInstanz={cands.Count} Play bereit={playReady} geladen={(loadedAtMs >= 0 ? "ja" : "nein")} Indikatoren={IndState()}");
                }
                if (loadedAtMs < 0) continue;
                long sinceLoaded = swLoad.ElapsedMilliseconds - loadedAtMs;
                if (sinceLoaded < 15_000) continue;   // normal: neue Instanz entsteht mit 'Initialized'; Nachfrist 15 s
                if (!playReady)
                {
                    if (sinceLoaded < 45_000) continue;
                    CaptureLoadState(W, logBaseIdx, cands); ListJobFolders(W, preJobFolders);
                    throw new AbortRun(6, $"BLOCKER: ATAS hat geladen, aber Play ist {sinceLoaded / 1000} s danach nicht bereit (letzter Zustand: {lg}). Kein Play.");
                }

                // Instanz aus Schritt 3 dieses Auftrags pruefen (ATAS hat beim Laden keine neue angelegt).
                var jl = JobLocked();
                string why;
                if (jl.Count != 1) why = $"{jl.Count} aktive MNQ-Instanzen dieses Auftrags ({string.Join(",", jl)})";
                else if (LockedCount() != 1) why = $"insgesamt {LockedCount()} aktive Instanzen (nicht eindeutig)";
                else if (jobAddedFolder == null) why = $"Instanz {jl[0]} nicht in Schritt 3 von diesem Auftrag angelegt";
                else if (jl[0] != jobAddedFolder) why = $"aktive Instanz {jl[0]} ist nicht die in Schritt 3 angelegte {jobAddedFolder}";
                else
                {
                    var pre = PreloadTradesCheck(Path.Combine(RecordingsDir, jl[0]), eUtc);
                    if (pre.ok)
                    {
                        cands = jl; provisionalAttr = true; ready = true;
                        W($"[7] Keine neue Instanz beim Laden; Instanz aus Schritt 3 {jl[0]}: aktiv, einzige, von diesem Auftrag; {pre.why} -> uebernommen (Pruefung nach Play).");
                        break;
                    }
                    why = $"Instanz aus Schritt 3 {jl[0]}: {pre.why}";
                }
                // Lokal belegt (18.11., Folgetag): ATAS stellt beim Laden einen Chart-Stand von VOR Schritt 3 wieder her
                // ('Reload: keeping previously captured chart state ...'); der angehaengte Recorder faellt dabei vom Chart.
                // Dann EINMAL nach dem Laden (Replay am Startpunkt, vor Play) ueber den bewaehrten Dialog anhaengen. Der
                // Dialog fuegt nichts hinzu, wenn der Recorder noch in der Liste steht (kein Doppel). Uebernommen wird nur
                // genau EINE danach neu entstandene, aktive MNQ-Instanz ohne Trades im Replay-Bereich.
                if (!readdTried && jl.Count == 0 && LockedCount() == 0)
                {
                    readdTried = true;
                    W($"[7] Keine aktive Recorder-Instanz nach dem Laden ({why}) -> Recorder jetzt (nach dem Laden, vor Play) EINMAL erneut anhaengen.");
                    var beforeRe = FolderSet();
                    bool addOk = AddRecorderViaDialog(W);
                    List<string> reNew = new();
                    for (var swR = Stopwatch.StartNew(); addOk && swR.ElapsedMilliseconds < 15000 && reNew.Count == 0; Thread.Sleep(300))
                        reNew = FolderSet().Except(beforeRe).Where(f => IsMetaLocked(Path.Combine(RecordingsDir, f, "meta.json"))
                            && ReadMetaField(Path.Combine(RecordingsDir, f, "meta.json"), "instrument").Contains("MNQ", StringComparison.OrdinalIgnoreCase)).ToList();
                    if (reNew.Count == 1 && LockedCount() == 1)
                    {
                        var preRe = PreloadTradesCheck(Path.Combine(RecordingsDir, reNew[0]), eUtc);
                        if (preRe.ok)
                        {
                            cands = reNew; jobAddedFolder = reNew[0]; ready = true;
                            W($"[7] Nach dem Laden erneut angehaengt: neue Instanz {reNew[0]} (aktiv, einzige; {preRe.why}) -> zugeordnet.");
                            break;
                        }
                        why = $"erneut angehaengte Instanz {reNew[0]}: {preRe.why}";
                    }
                    else why = addOk ? $"erneutes Anhaengen ergab {reNew.Count} neue aktive Instanz(en), insgesamt aktiv {LockedCount()}" : "erneutes Anhaengen ueber den Dialog fehlgeschlagen";
                }
                CaptureLoadState(W, logBaseIdx, cands);
                ListJobFolders(W, preJobFolders);
                throw new AbortRun(6, $"BLOCKER: ATAS hat geladen und Play ist bereit, aber nach {sinceLoaded / 1000} s keine eindeutige Recorder-Instanz: {why}. " +
                    "Kein Play ohne zugeordnete Aufnahme; Recorder wird entfernt.");
            }
            if (!ready) { cands = NewLocked(); CaptureLoadState(W, logBaseIdx, cands); ListJobFolders(W, preJobFolders); }   // genauen Zustand sichern (keine Spekulation)
            if (cands.Count == 0) throw new AbortRun(6, $"BLOCKER: keine neue Recorder-Instanz nach LOAD ({swLoad.ElapsedMilliseconds / 1000} s; geladen={(loadedAtMs >= 0 ? "ja" : "nein")}).");
            if (cands.Count > 1) throw new AbortRun(6, $"BLOCKER: mehrere neue Instanzen ({string.Join(",", cands)}) -> nicht eindeutig.");
            attrFolder = cands[0]; string dir = Path.Combine(RecordingsDir, attrFolder);
            attrRunId = ReadMetaField(Path.Combine(dir, "meta.json"), "runId");
            W($"[7] Zugeordnete Instanz: {attrFolder}  runId={attrRunId}  (genau EINE neue gesperrte MNQ-Instanz)");
            if (!ready) throw new AbortRun(6, "BLOCKER: Abspielbereitschaft (PlayButton) nicht erreicht. [Zustand gesichert, siehe 7-DIAG]");
            tLoad += swL.Elapsed.TotalSeconds;
            // Ladebereich VOR Play pruefen, soweit ATAS ihn schon meldet ('... warmed before <Start>Z').
            exec.Feed(alog.ReadNew());
            if (exec.WarmStartUtc is DateTime warm)
            {
                W($"[7] ATAS-Ladebestaetigung Start={warm:yyyy-MM-dd HH:mm:ss}Z (Zeile: {exec.WarmLine})");
                if (warm != sUtc)
                    throw new AbortRun(7, $"BLOCKER: ATAS hat ab {warm:yyyy-MM-dd HH:mm:ss}Z geladen, Soll {sUtc:yyyy-MM-dd HH:mm:ss}Z " +
                        $"(Abweichung {(warm - sUtc).TotalMinutes:+0;-0} min). Dialog-Zeitzone passt nicht ({zone.Source}). Kein Play, keine Veroeffentlichung.");
            }
            else W("[7] ATAS meldet den Startzeitpunkt vor Play nicht im Protokoll -> Pruefung erfolgt sofort bei Play ('Playing from').");
            if (exec.Started)
                throw new AbortRun(7, $"BLOCKER: Wiedergabe lief bereits vor dem Play der Steuerung (manuell gestartet? {exec.RangeLine}). " +
                    "Ein weiterer Play-Klick wuerde pausieren; Zuordnung nicht sauber. Bitte waehrend des Laufs nicht in ATAS klicken.");

            // ---- Phase 8: Geschwindigkeit setzen (konfigurierbar) + zuruecklesen + VERIFIZIEREN, dann Play ----
            // Die Geschwindigkeit wird ueber den vorhandenen Replay-Speed-Selektor gesetzt und zurueckgelesen.
            // Stimmt die Ruecklesung nicht mit der Anforderung ueberein -> Abbruch (KEIN stilles Abspielen mit
            // anderer Geschwindigkeit; 100x wird nicht ungeprueft angenommen).
            var swP = Stopwatch.StartNew();
            DateTime playInvoke;
            if (visualMode)
            {
                // Speed: im custom-gerenderten Transport nicht UIA/OCR-setzbar/-lesbar ('100x' ist fuer Windows-OCR zu
                // kontrastarm). ATAS zeigt als Default sichtbar '100x'. Eine FALSCHE (zu langsame) Geschwindigkeit fiele
                // am Play-Watchdog (Phase 9) zuverlaessig als Timeout auf -> kein stiller Fehlerfolg.
                speedVerified = false;
                W($"[8] (visuell) Geschwindigkeit NICHT verifiziert: weder per UIA noch per OCR lesbar. Angefordert '{speedName}', tatsaechlich unbekannt (Watchdog begrenzt die Laufzeit).");
                // Play-Position: bevorzugt FRISCH per OCR. Nach dem Laden zeigt die Leiste aber oft nicht mehr
                // 'Datum auswählen'/'Modul' (DetectTransport-Muster greift dann nicht). In dem Fall die in DIESEM Lauf
                // bei Phase 4/6 bereits VALIDIERTE Leisten-Geometrie (vt) wiederverwenden (gleiche Position wie der
                // zuvor erfolgreiche 'Datum'-Klick) – KEINE laufuebergreifend gespeicherte Absolutkoordinate.
                // Der Klick bleibt gated durch OcrReadyToPlay (Statuszeile 'Play') und OcrReadyToPlay/RaiseReplayFloat
                // hebt das schwebende Float zuvor nach vorn, damit der Klick die Leiste trifft (nicht den Chart).
                var vtp = OcrDetectFresh(out var vpd);
                (int x, int y)? playPt = vtp?.PlayClick;
                int barY = vtp?.BarY ?? vt!.BarY;
                string playSrc = "frisch(OCR)";
                if (playPt == null)
                {
                    if (vt?.PlayClick == null) throw new AbortRun(8, "BLOCKER: (visuell) Play-Position weder frisch per OCR noch aus der Lauf-Geometrie ableitbar: " + vpd);
                    playPt = vt.PlayClick; barY = vt.BarY; playSrc = "Lauf-Geometrie(Phase4, validiert durch Datum-Klick)";
                    W($"[8] (visuell) frische OCR-Leiste nach Laden nicht erkannt ({vpd}) -> Play aus {playSrc}.");
                }
                if (!OcrReadyToPlay(barY, out var sw0)) throw new AbortRun(8, $"BLOCKER: (visuell) nicht ladebereit vor Play (Statuszeile '{sw0}', erwartet 'Play').");
                // Vor Play: Transportsymbole per Tooltip zuordnen (nur Hover) - 'Zur Echtzeit wechseln' wird spaeter zum Stoppen gebraucht.
                var fwp = ReplayFloatWin32();
                if (_transportMap == null || fwp == null || _transportMap.Hwnd != fwp.Hwnd || _transportMap.WinRect != fwp.Rect) _transportMap = ScanTransportTooltips(W);
                W($"[8] Stop-Bedienelement 'Zur Echtzeit' per Tooltip: {(_transportMap?.Find(RxToRealtime) is { } tr ? $"({tr.x},{tr.y})" : "NICHT gefunden (Stopp dann ueber Ersatzwege, begrenzt)")}");
                W("[8] Auto-Wiederholung: " + EnsureLoopOff(W, visualMode: true));
                playInvoke = DateTime.UtcNow;
                W($"[8] (visuell) Play @{playPt.Value.x},{playPt.Value.y} [{playSrc}] …");
                ClickFloat(playPt.Value.x, playPt.Value.y);
                bool started = false;
                for (int i = 0; i < 10 && !started; i++) { Thread.Sleep(1000); started = ReplayPlayingOrLoadingByTitle(); }
                if (!started)
                {
                    W("[8] (visuell) Titel nicht 'laeuft/laden' -> EINMAL erneut Play.");
                    var vtp2 = OcrDetectFresh(out _);
                    var pt2 = vtp2?.PlayClick ?? vt?.PlayClick;
                    if (pt2 != null) { ClickFloat(pt2.Value.x, pt2.Value.y); playInvoke = DateTime.UtcNow; }
                    for (int i = 0; i < 10 && !started; i++) { Thread.Sleep(1000); started = ReplayPlayingOrLoadingByTitle(); }
                }
                if (!started) throw new AbortRun(8, "BLOCKER: (visuell) Play-Klick wirkungslos (Fenstertitel wechselt nicht auf Wiedergabe) -> Abbruch.");
                W("[8] (visuell) Play bestaetigt (Fenstertitel = Wiedergabe laeuft/laedt)."); Thread.Sleep(2000);
            }
            else
            {
                string sp = SetReplaySpeed(speedName, W);
                if (!SpeedReadbackOk(speedName, sp))
                    throw new AbortRun(8, $"BLOCKER: Replay-Speed '{speedName}' nicht setzbar/zurueckgelesen (gelesen '{sp}').");
                speedVerified = true;
                W($"[8] Speed angefordert='{speedName}' zurueckgelesen='{sp}' (verifiziert)");
                if (!IsEnabledSafe(FindIdAny("PlayButton"))) throw new AbortRun(8, "BLOCKER: PlayButton nicht bereit.");
                W("[8] Auto-Wiederholung: " + EnsureLoopOff(W, visualMode: false));
                // Play per PHYSISCHEM Klick auf die UIA-Button-Position (UIA-Invoke startet die Wiedergabe hier NICHT).
                playInvoke = DateTime.UtcNow; W("[8] Play (physischer Klick auf UIA-Button-Position) …");
                if (!PlayByPhysicalClick(W)) throw new AbortRun(8, "BLOCKER: Play-Klick nicht ausfuehrbar.");
                bool started = false;
                for (int i = 0; i < 12 && !started; i++) { Thread.Sleep(1000); started = ReplayPlayingOrLoadingByTitle(); }
                if (!started)
                {
                    W("[8] Titel nicht 'Module laden/laeuft' -> EINMAL erneut Play (physisch).");
                    playInvoke = DateTime.UtcNow; PlayByPhysicalClick(W);
                    for (int i = 0; i < 12 && !started; i++) { Thread.Sleep(1000); started = ReplayPlayingOrLoadingByTitle(); }
                }
                if (!started) throw new AbortRun(8, "BLOCKER: Play-Klick wirkungslos (Titel wechselt nicht auf 'Module laden'/'Historienwiedergabe').");
                W("[8] Play bestaetigt (Fenstertitel 'Module laden'/'Historienwiedergabe').");
            }
            // Vorlaeufige Zuordnung (Ersatzweg 2 in Phase 7) nach Play bestaetigen: erzeugt ATAS JETZT doch eine neue Instanz,
            // wird genau diese genommen; sonst muss die uebernommene Instanz aktiv bleiben (Daten prueft Phase 9 laufend).
            if (provisionalAttr)
            {
                List<string> after = new();
                for (int k = 0; k < 8 && after.Count == 0; k++) { Thread.Sleep(1000); after = NewLocked().Where(f => f != attrFolder).ToList(); }
                if (after.Count == 1)
                {
                    W($"[8] Nach Play neue Instanz {after[0]} -> Zuordnung von {attrFolder} auf diese umgestellt.");
                    attrFolder = after[0]; dir = Path.Combine(RecordingsDir, attrFolder);
                    attrRunId = ReadMetaField(Path.Combine(dir, "meta.json"), "runId");
                }
                else if (after.Count > 1) throw new AbortRun(8, $"BLOCKER: nach Play mehrere neue Instanzen ({string.Join(",", after)}) -> nicht eindeutig.");
                else if (!IsMetaLocked(Path.Combine(dir, "meta.json"))) throw new AbortRun(8, $"BLOCKER: uebernommene Instanz {attrFolder} nach Play nicht mehr aktiv und keine neue Instanz.");
                else W($"[8] Uebernommene Instanz {attrFolder} bleibt aktiv (keine neue Instanz nach Play) -> Zuordnung bestaetigt.");
            }
            // ---- Phase 9: Wiedergabe bis zum ERSTEN vollstaendigen Durchlauf; Ende aus dem ATAS-Protokoll (Originalzeilen) ----
            // Kurze Abfragen (200 ms) statt 3 s: bei 100x laufen in 3 s rund 5 Minuten Marktzeit weiter. Das Protokoll wird
            // inkrementell gelesen; Recorder-Dateien jede Sekunde, nahe am Fensterende alle 250 ms.
            _verifiedReplayEndUtc = null;
            int playWatchdog = PlayWatchdogSeconds(windowSec, Math.Max(1.0, SpeedToFactor(speedName) * 0.5), loadFlushFloorSeconds: 240);
            var swp = Stopwatch.StartNew();
            DateTime? lastMt = null, maxMp = null, firstMt = null; double stallMs = 0; bool rangeChecked = false, rangeMismatch = false;
            var lastStreams = DateTime.MinValue; var lastReport = DateTime.MinValue; string stopCause = "";
            while (swp.Elapsed.TotalSeconds < playWatchdog)
            {
                exec.Feed(alog.ReadNew());
                if (!rangeChecked && exec.Range is { } lr)
                {
                    rangeChecked = true;
                    W($"[9] Ladebereich laut ATAS: [{lr.startUtc:yyyy-MM-dd HH:mm:ss}Z, {lr.endUtc:yyyy-MM-dd HH:mm:ss}Z]  (Zeile: {exec.RangeLine})");
                    if (lr.startUtc != sUtc || lr.endUtc != eUtc)
                    {
                        rangeMismatch = true;
                        stopCause = $"Ladebereich weicht vom Soll ab: ATAS [{lr.startUtc:HH:mm:ss}Z, {lr.endUtc:HH:mm:ss}Z] statt [{sUtc:HH:mm:ss}Z, {eUtc:HH:mm:ss}Z] " +
                                    $"(Start {(lr.startUtc - sUtc).TotalMinutes:+0;-0} min). Dialog-Zeitzone: {zone.Source}.";
                        break;
                    }
                }
                bool nearEnd = maxMp != null && maxMp.Value >= eUtc.AddMinutes(-3);
                double since = (DateTime.UtcNow - lastStreams).TotalMilliseconds;
                if (since >= (nearEnd ? 250 : 1000))
                {
                    lastStreams = DateTime.UtcNow;
                    var st = FolderStreams(dir);
                    DateTime? mp = null;
                    // 1.3.8: Marktzeiten >= Fensterende+1h sind Live-Vorlauf einer in Schritt 3 (Live-Modus) angehaengten
                    // Instanz, keine Replay-Daten -> zaehlen weder als Fortschritt noch als Fensterende.
                    bool Replayish(DateTime t) => t.ToUniversalTime() < eUtc.AddHours(1);
                    foreach (var k in new[] { "dom.ndjson", "bbo.ndjson", "cumtrades.ndjson", "ticks.ndjson" })
                        if (st.TryGetValue(k, out var v) && v.LastMt is { } lm && Replayish(lm) && (mp == null || lm > mp)) mp = lm;
                    if (st.TryGetValue("ticks.ndjson", out var tv0) && tv0.LastMt is { } lm0 && Replayish(lm0)) firstMt ??= lm0.ToUniversalTime();
                    if (mp != null && lastMt != null && mp == lastMt) stallMs += Math.Min(since, 5000); else stallMs = 0;
                    if (mp != null) { lastMt = mp; if (maxMp == null || mp.Value > maxMp.Value) maxMp = mp; }
                    exec.DataReachedEnd = maxMp != null && maxMp.Value.ToUniversalTime() >= eUtc.AddSeconds(-2);
                    exec.Recheck();
                    if ((DateTime.UtcNow - lastReport).TotalSeconds >= 3)
                    {
                        lastReport = DateTime.UtcNow;
                        W($"    [play t+{swp.Elapsed.TotalSeconds:0.0}s] mp={(mp?.ToString("HH:mm:ss") ?? "-")} max={(maxMp?.ToString("HH:mm:ss") ?? "-")} zustand={exec.State} datenEnde={exec.DataReachedEnd} stillstand={stallMs / 1000:0.0}s hinweise={exec.Hints.Count}");
                    }
                }
                if (exec.State == ReplayEnd.NaturalEnd) { stopCause = "natuerliches Ende"; break; }
                if (StopRequested()) { stopCause = "Kontrollierter Stopp von der Oberflaeche angefordert"; break; }
                if (exec.State is ReplayEnd.Errored or ReplayEnd.OnlineExit or ReplayEnd.OtherRangeRestart or ReplayEnd.StoppedByUs or ReplayEnd.StoppedOther) { stopCause = exec.Rule; break; }
                if (exec.Started && !exec.DataReachedEnd && stallMs >= 30000) { stopCause = $"Stillstand {stallMs / 1000:0}s ohne Ende (letzte Marktzeit {(lastMt?.ToString("HH:mm:ss") ?? "-")})"; break; }
                if (exec.DataReachedEnd && stallMs >= 24000) { stopCause = "Daten am Fensterende, aber kein Abschluss von ATAS innerhalb 24 s (keine Endbestaetigung)"; break; }
                if (firstMt == null && swp.Elapsed.TotalSeconds > 90) { stopCause = "keine Daten nach 90 s"; break; }
                Thread.Sleep(200);
            }
            if (stopCause == "") stopCause = $"Zeitlimit {playWatchdog}s erreicht";
            tPlay += swP.Elapsed.TotalSeconds;

            // SOFORT stoppen (vor jeder weiteren Auswertung): Replay verlassen beendet die Wiedergabe. Ein bereits
            // begonnener Auto-Loop schreibt in eine NEUE Recorder-Instanz (eigener Ordner), nicht in die zugeordnete.
            var swStop = Stopwatch.StartNew();
            exec.MarkOurStop(DateTime.Now);
            W($"[9] Stopp-Grund: {stopCause}  -> Wiedergabe jetzt beenden ({DateTime.Now:HH:mm:ss.fff})");
            // 1.3.7: Replay-Leiste nach vorn, ihr X genau einmal, Wirkung pruefen (kein Kuerzel-Serie; siehe ReplayBarClose.cs).
            var stopRes = CloseReplayBarConfirmed(W, alog, visualMode);
            replayViewLeft = stopRes.Stopped && stopRes.LeftReplayView;
            W($"[9] Ende erkannt -> X der Leiste: Replay-Ansicht verlassen={replayViewLeft} keinNeustart={stopRes.NoFurtherPass} nach {swStop.ElapsedMilliseconds} ms (Weg: {stopRes.How})");
            if (!stopRes.Stopped) throw new AbortRun(9, "BLOCKER: Replay-Ansicht nach dem Ende NICHT nachweislich verlassen (siehe [x]). Keine Veroeffentlichung, kein weiterer Tag.");

            // Entscheidung mit Belegen (Originalzeilen, Regel, Zuordnung zur eigenen Ausfuehrung)
            endProven = exec.State == ReplayEnd.NaturalEnd && !rangeMismatch;
            endStatus = endProven ? "Reached" : "Unproven";
            if (endProven) _verifiedReplayEndUtc = exec.Range?.endUtc;
            dataWindowRecorded = exec.DataReachedEnd;
            endReason = $"{exec.State}: {(exec.Rule == "" ? stopCause : exec.Rule)}";
            W($"[9] Endstatus={endStatus}  Klasse={exec.State}  Regel='{exec.Rule}'  zugeordnet ab eigenem 'Playing from' {(exec.RangeLine != null ? exec.RangeLine.Ts.ToString("HH:mm:ss.fff") : "(keins)")}");
            foreach (var ev in exec.Evidence) W($"      Beleg: {ev}");
            foreach (var h in exec.Hints.Take(8)) W($"      Hinweis (kein Abbruchgrund): {h}");
            if (exec.LoopRestarted) W("      ATAS hat nach dem Abschluss automatisch neu gestartet (Auto-Loop); diese Daten liegen in einer eigenen, NICHT zugeordneten Instanz.");
            if (rangeMismatch) throw new AbortRun(7, "BLOCKER: " + stopCause + " Keine Veroeffentlichung.");

            var fin = FolderStreams(dir); fin.TryGetValue("ticks.ndjson", out var fv);
            span = fv?.FirstMt != null ? $"{fv.FirstMt:yyyy-MM-dd HH:mm:ss}->{fv.LastMt:HH:mm:ss}Z" : "-";
            mpStr = lastMt != null ? lastMt.Value.ToString("HH:mm:ss") + "Z" : "-"; ticksBytes = fv?.Bytes ?? 0;
            W($"[9] Datenbereich der zugeordneten Instanz (Recorder, UTC): {span}; Soll [{sUtc:HH:mm:ss}Z, {eUtc:HH:mm:ss}Z)");
            var measured = MeasuredSpeed(dir);
            double reqF = SpeedToFactor(speedName);
            if (measured is double mf)
            {
                bool speedOk = reqF > 0 && mf >= reqF * 0.7 && mf <= reqF * 1.3;
                if (speedVerified != true) speedVerified = speedOk;
                W($"[9] Gemessene Geschwindigkeit (Recorder tMarket/tRecv): {mf:0.0}x bei angefordert {speedName} -> {(speedOk ? "im Rahmen (+-30 %)" : "ABWEICHEND")}");
            }
            else W("[9] Geschwindigkeit nicht messbar (zu wenige Daten).");
            var loopFolders = FolderSet().Except(beforeLoad).Where(f => f != attrFolder).ToList();
            if (loopFolders.Count > 0) W($"[9] Weitere Instanzen seit dem Laden (Auto-Loop/Echtzeit, werden NICHT angehaengt): {string.Join(", ", loopFolders)}");

            // ---- Phase 10: Replay-Transport verlassen + Replay-ANSICHT des Charts beenden ----
            // Marker: ab hier eigene Cleanup-Aktionen. Deren Logmeldungen (Disconnect durch 'Zur Echtzeit' etc.) gehoeren
            // NICHT mehr zur Enderkennung dieses Auftrags (die Entscheidung in Phase 9 ist bereits gefallen).
            W($"[CLEANUP-START {DateTime.Now:HH:mm:ss}] eigene Cleanup-Aktionen beginnen (nicht Ursache eines etwaigen Abbruchs oben)");
            W("[10] Replay-Transport verlassen");
            // ExitReplayView nutzt den Transport-Button 'Zur Echtzeit wechseln' (Transport muss OFFEN sein) und
            // verifiziert das Verlassen UNABHAENGIG daran, dass der chart-gebundene 'Indikatoren'-Button wieder aktiv wird.
            // 1.3.7: kein zweiter Stopp-Versuch hier (frueher erneute Kuerzel-Serie); [9] ist die einzige Stelle.
            W($"[10] Replay-Ansicht verlassen (bestaetigt in [9]): {replayViewLeft}");
            // ---- Phase 11: Recorder am Zielchart entfernen + Uebernehmen (finalisiert die zugeordnete Instanz) ----
            W("[11] Recorder am Zielchart entfernen");
            RemovalOutcome removal = RemovalOutcome.Failed;
            if (replayViewLeft)
            {
                // Zeile -> Papierkorb (UIA/Screenshot, geprueft) -> Wirkung pruefen -> erst dann Uebernehmen.
                // Writer-Flush beim Dispose: grosse Sessionfenster brauchen laenger bis meta.json entsperrt ist (1-Min bleibt 15s).
                removal = RemoveRecorderFromChart(W, writerWaitSec: Math.Max(15, windowSec / 60));
            }
            else W("  [11] Entfernen uebersprungen: Indikator-Dialog ist waehrend/kurz nach Replay-Ansicht gesperrt (Replay-Ansicht nicht verlassen).");
            // 1.3.8 (lokal belegt): Beim Wechsel zurueck zur Echtzeit stellt ATAS u. U. einen Chart-Stand ohne Recorder wieder
            // her ('keeping previously captured chart state'). Steht dann KEIN Recorder mehr in der Chart-Liste, ist keine
            // Instanz mehr aktiv und ist die zugeordnete Instanz entsperrt (Writer beendet), ist das Ziel erreicht.
            bool attrClosed = attrFolder != null && !IsMetaLocked(Path.Combine(RecordingsDir, attrFolder, "meta.json"));
            if (removal == RemovalOutcome.NoRecorder && LockedCount() == 0 && attrClosed)
                W("[11] Recorder stand nicht mehr in der Chart-Liste (von ATAS beim Wechsel zur Echtzeit entfernt); keine aktive Instanz, zugeordnete Instanz geschlossen -> kein Recorder aktiv (bestaetigt).");
            recorderRemoved = (removal == RemovalOutcome.Removed || (removal == RemovalOutcome.NoRecorder && attrClosed)) && LockedCount() == 0;
        }
        catch (AbortRun ab) { W(ab.Message); rc = ab.Code; }
        catch (Exception ex) { W($"BLOCKER: Exception {ex.GetType().Name}: {ex.Message}"); rc = 2; }
        finally
        {
            // Cleanup: Replay sicher verlassen (layout-robuster Cluster-Finder, nicht nur Fenstertitel); Status protokollieren.
            cleanupOk = !Bounded("Replay offen? (Abschluss)", ReplayOpen, 4000, true, W) || replayViewLeft;
            // Frueher Abbruch NACH dem Anhaengen (VM-Befund 'Recorder bleibt zurueck'): den von DIESEM Auftrag angehaengten
            // Recorder ueber den bewaehrten Dialogweg wieder entfernen. Nur im Abbruchfall, einmal, ohne Wiederholungsschleife.
            if (rc != -1 && recorderAddedByJob && !recorderRemoved)
            {
                try { recorderRemoved = RemoveRecorderAfterAbort(W); }
                catch (Exception ex) { W($"  [abbruch-cleanup] Fehler beim Entfernen: {ex.GetType().Name}: {ex.Message}"); }
                W($"  [abbruch-cleanup] Recorder nach Abbruch entfernt: {recorderRemoved}{(recorderRemoved ? "" : "  -> BITTE in ATAS pruefen und den Indikator 'OF Replay Recorder' von Hand entfernen.")}");
            }
        }

        // ---- Phase 12: Finalisierung der zugeordneten Aufnahme pruefen ----
        bool finalizedClean = false; int tradesInWin = -1; string summaryRunId = "";
        if (attrFolder != null)
        {
            var dir = Path.Combine(RecordingsDir, attrFolder);
            var sp = Path.Combine(dir, "summary.json");
            bool hasSummary = File.Exists(sp); bool locked = IsMetaLocked(Path.Combine(dir, "meta.json"));
            string sjson = hasSummary ? ReadAllShared(sp) : "";
            summaryRunId = hasSummary ? (Regex.Match(sjson, "\"runId\"\\s*:\\s*\"([^\"]*)\"").Groups[1].Value) : "";
            finalizedClean = RecorderFinalizedClean(hasSummary, locked, sjson, attrRunId);
            W($"\n[12] Finalisierung {attrFolder}: summary={hasSummary} writerBeendet={!locked} clean+runId={finalizedClean} (summaryRunId={summaryRunId})");
        }
        // Ergebnis fuer den Sammellauf bereitstellen (welche finalisierte Instanz an die gemeinsame CSV darf).
        _lastJobFolder = attrFolder; _lastJobRunId = attrRunId; _lastJobFinalizedClean = finalizedClean;

        bool recordingSaved = finalizedClean;
        bool cleanupDone = CleanupDone(recorderRemoved, replayViewLeft);
        // GESAMT-PASS nur, wenn ALLE erforderlichen Teilergebnisse erfuellt sind (Aufnahme + unabh. Endsignal + Cleanup).
        bool gesamtPass = (rc == -1) && LiveProofPass(recordingSaved, endProven, cleanupDone);
        if (rc == -1) rc = gesamtPass ? 0 : 8;   // kein frueher Abbruch -> rc aus Verdikt; sonst bleibt der Abbruch-Code
        W($"\n===== ERGEBNIS Auftrag {jobNo}/{jobCount} [{dStart},{dEnd}): {(gesamtPass ? "GESAMT-PASS" : "KEIN GESAMT-PASS")} (rc={rc}) =====");
        W($"  Teilergebnisse (getrennt bewertet):");
        W($"   - Aufnahme gespeichert (Writer beendet + Summary clean + runId-Match): {recordingSaved}");
        W($"   - Replay-ENDE unabhaengig bestaetigt: {endProven} (Status {endStatus}; Daten-Korroboration={dataWindowRecorded} = KEIN Endbeweis)");
        W($"   - Cleanup abgeschlossen (Replay-Ansicht verlassen={replayViewLeft} UND Recorder entfernt={recorderRemoved}): {cleanupDone}");
        W($"  Zielchart: {_chartDesc} | Instanz: {attrFolder ?? "-"} runId={attrRunId}");
        string recMeta = attrFolder != null ? ReadMetaField(Path.Combine(RecordingsDir, attrFolder, "meta.json"), "recorder") : "";
        W($"  Recorder dieser Aufnahme (meta.json): '{(recMeta == "" ? "-" : recMeta)}' | {RecorderIdentity()}");
        W($"  HERKUNFT-GRENZE: ein sichtbarer Chart + neuer gesperrter Ordner ist KEINE vollstaendige Herkunft;");
        W($"   die ausgelieferten Recorder-Varianten binden KEIN Lauf-Token (GUID-runId) -> keine token-gebundene Chart/Instanz-Zuordnung.");
        W($"  aufgezeichnete Marktzeit(ticks): {span}; ticks={ticksBytes}B; Soll [{sUtc:yyyy-MM-dd HH:mm:ss}Z, {eUtc:HH:mm:ss}Z); Dialog {dStart}..{dEnd} ({zone.Source})");
        W($"  Geschwindigkeit: '{speedName}' {(speedVerified == true ? "verifiziert" : speedVerified == false ? "NICHT verifiziert" : "nicht gesetzt")}");
        W($"  Replay-Transport geschlossen (cleanup-Teil): {cleanupOk} | aktive gesperrte Instanzen jetzt: {LockedCount()}");
        LockedInstancesReport(W, attrFolder);
        W($"  [timing] gesamt {swAll.Elapsed.TotalSeconds:F0}s = Bedienung {tBed:F0}s + Laden {tLoad:F0}s + Wiedergabe {tPlay:F0}s");
        if (!gesamtPass) W($"  HINWEIS: Aufnahme bleibt erhalten; Gesamtablauf NICHT bestanden. Fehlend: " +
            $"{(!recordingSaved ? "[Aufnahme] " : "")}{(!endProven ? "[unabh. Endsignal] " : "")}{(!cleanupDone ? "[Cleanup/Remove]" : "")}");
        Flush(sb, outFile);
        return rc;
    }

    // Zwei Aufnahmeauftraege hintereinander in EINEM Aufruf (keine manuellen Zwischenschritte) ueber RunRecordingJob.
    // Zwei nachweislich verfuegbare 1-Minuten-Fenster (26.10.2026, Winterzeit UTC+1):
    //   Auftrag 1: 09:00:00-09:01:00 Berlin == 08:00:00-08:01:00 UTC   (im LiveProof mehrfach belegt)
    //   Auftrag 2: 15:30:00-15:31:00 Berlin == 14:30:00-14:31:00 UTC   (in ATAS-Logs als geladen+gespielt belegt)
    // Scheitert Auftrag 1 oder dessen Cleanup -> KEIN Start von Auftrag 2; Aufnahmen bleiben erhalten, Fehler gemeldet.
    private static int TwoJobs(string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); if (outFile != null) { try { File.WriteAllText(outFile, sb.ToString()); } catch { } } }
        var windows = new (string dStart, string dEnd)[]
        {
            ("26.10.2026 09:00:00", "26.10.2026 09:01:00"),
            ("26.10.2026 15:30:00", "26.10.2026 15:31:00"),
        };
        W($"# ATAS TwoJobs (zwei Auftraege, EIN Aufruf)  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  Ausgabe: {RecordingsDir}");
        string Pf = "dd.MM.yyyy HH:mm:ss";
        for (int i = 0; i < windows.Length; i++)
        {
            var s = DateTime.ParseExact(windows[i].dStart, Pf, System.Globalization.CultureInfo.InvariantCulture);
            var e = DateTime.ParseExact(windows[i].dEnd, Pf, System.Globalization.CultureInfo.InvariantCulture);
            W($"# Auftrag {i + 1} Europe/Berlin [{windows[i].dStart}, {windows[i].dEnd}) == UTC [{BerlinToUtc(s):yyyy-MM-dd HH:mm:ss}, {BerlinToUtc(e):yyyy-MM-dd HH:mm:ss}) (TimeZoneInfo, DST-korrekt)");
        }
        if (!DriveReadyWritable(RecordingsDir, out var rdrTj)) { W("BLOCKER: " + rdrTj); Flush(sb, outFile); return 3; }
        if (Process.GetProcessesByName("OFT.Platform").Length == 0) { W("BLOCKER: ATAS laeuft nicht."); Flush(sb, outFile); return 3; }
        var rcs = new List<int>();
        for (int i = 0; i < windows.Length; i++)
        {
            int rc = RunRecordingJob(outFile, windows[i].dStart, windows[i].dEnd, i + 1, windows.Length, sb);
            rcs.Add(rc);
            if (rc != 0) { W($"\n[ABBRUCH] Auftrag {i + 1} nicht bestanden (rc={rc}) -> KEINE weiteren Auftraege. Aufnahmen bleiben erhalten."); break; }
        }
        bool allPass = rcs.Count == windows.Length && rcs.All(r => r == 0);
        W($"\n===== TWOJOBS-GESAMT: {(allPass ? "BEIDE AUFTRAEGE PASS" : "NICHT VOLLSTAENDIG")} =====");
        for (int i = 0; i < windows.Length; i++)
            W($"  Auftrag {i + 1} [{windows[i].dStart},{windows[i].dEnd}): {(i < rcs.Count ? (rcs[i] == 0 ? "PASS" : $"FAIL(rc={rcs[i]})") : "NICHT GESTARTET")}");
        W($"  aktive gesperrte Recorder jetzt: {LockedCount()} (erwartet 0)");
        Flush(sb, outFile);
        return allPass ? 0 : 8;
    }

    // =============== SAMMELLAUF: mehrere Fenster -> EINE gemeinsame Trade-CSV je Session+Jahr ===============
    // EIN Startbefehl mit JSON-Konfiguration. Wiederverwendung des bewaehrten RunRecordingJob je Fenster
    // (konfigurierbare Geschwindigkeit, auftraggebundenes Endsignal, Cleanup-Pruefungen UNVERAENDERT) PLUS
    // Offline-Anhaengen an die gemeinsame CSV ueber research/data-bridge/atas_collect.py (atomar, wiederanlauffaehig).
    // Wiederanlauf: bereits committete Auftraege werden uebersprungen (kein Doppel-Anhaengen). Bei Fehler: Stopp OHNE
    // Wiederholungsschleife; Aufnahmen bleiben erhalten; neue Zwischenaufnahmen werden NICHT geloescht.
    private static int Collect(string? configPath, string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); if (outFile != null) { try { File.WriteAllText(outFile, sb.ToString()); } catch { } } }
        if (configPath == null || !File.Exists(configPath)) { W($"BLOCKER: Konfig (.json) fehlt/nicht gefunden: {configPath}"); Flush(sb, outFile); return 3; }

        string session; int year; string tz, speed, collDir, py, script; bool keepAttached;
        var windows = new List<(string dStart, string dEnd)>();
        var utcWindows = new List<(DateTime s, DateTime e)>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configPath));
            var r = doc.RootElement;
            session = r.GetProperty("session").GetString()!;
            year = r.GetProperty("year").GetInt32();
            tz = r.TryGetProperty("timezone", out var vtz) ? vtz.GetString()! : "W. Europe Standard Time";
            speed = r.TryGetProperty("speed", out var vsp) ? vsp.GetString()! : "1x";
            collDir = r.TryGetProperty("collectionDir", out var vc) ? vc.GetString()! : Path.Combine(AppContext.BaseDirectory, "ATAS_Data", "Collections", $"{session}_{year}");
            py = r.TryGetProperty("python", out var vp) ? vp.GetString()! : "python";
            script = r.TryGetProperty("collectorScript", out var vs) ? vs.GetString()! : Path.Combine(AppContext.BaseDirectory, "bridge", "atas_collect.py");
            keepAttached = r.TryGetProperty("keepRecorderAttached", out var vk) && vk.GetBoolean();
            foreach (var w in r.GetProperty("windows").EnumerateArray())
            {
                // Bevorzugt UTC-Grenzen (ab 1.3.3); Berlin-Wandzeit bleibt fuer aeltere Konfigurationen lesbar.
                if (w.TryGetProperty("startUtc", out var su) && w.TryGetProperty("endUtc", out var eu))
                    utcWindows.Add((DateTime.Parse(su.GetString()!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal),
                                    DateTime.Parse(eu.GetString()!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal)));
                else
                    windows.Add((w.GetProperty("startBerlin").GetString()!, w.GetProperty("endBerlin").GetString()!));
            }
        }
        catch (Exception ex) { W($"BLOCKER: Konfig nicht lesbar: {ex.GetType().Name}: {ex.Message}"); Flush(sb, outFile); return 3; }

        W($"# ATAS Sammellauf  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        W($"# Programme: Probe {ProgramVersion()} | {RecorderIdentity()}");
        W($"# Session='{session}' Jahr={year} TZ='{tz}' Speed='{speed}' keepAttached={keepAttached}");
        W($"# Gemeinsame CSV: {Path.Combine(collDir, "trades.csv")}");
        W($"# Collector: {py} \"{script}\"");
        if (!DriveReadyWritable(collDir, out var crs)) { W("BLOCKER: " + crs); Flush(sb, outFile); return 3; }
        if (!DriveReadyWritable(RecordingsDir, out var rrs)) { W("BLOCKER: " + rrs); Flush(sb, outFile); return 3; }
        if (!File.Exists(script)) { W($"BLOCKER: Collector-Skript fehlt: {script}"); Flush(sb, outFile); return 3; }
        // ---- Vorpruefungen OHNE jede ATAS-Aktion (VM-Befunde): Bridge startfaehig? Recorder schreibt in DIESEN Ordner? ----
        // Frueher fiel ein nicht importierbares atas_bridge erst NACH der Aufnahme beim Anhaengen auf.
        var (scCode, scOut, scErr) = RunPython(py, new[] { script, "selfcheck" });
        if (!BridgeSelfcheckOk(scCode, scOut))
        {
            W($"BLOCKER: Bridge-Selbsttest fehlgeschlagen (rc={scCode}): {(scErr.Trim() == "" ? scOut.Trim() : scErr.Trim())}");
            W("  -> keine ATAS-Aktion ausgefuehrt. Python-Laufzeit/Bridge pruefen (Setup erneut ausfuehren).");
            Flush(sb, outFile); return 3;
        }
        W($"# Bridge-Selbsttest ok: {scOut.Trim()}");
        var pathVerdict = RecorderPathVerdict(RecordingsDir, File.Exists(SharedDataRoot.InstalledRecorderDll),
            SharedDataRoot.DllSupportsSharedRoot(SharedDataRoot.InstalledRecorderDll), SharedDataRoot.Read(), out var pathMsg);
        W((pathVerdict ? "# Recorder-Ausgabeordner ok: " : "BLOCKER: ") + pathMsg);
        if (!pathVerdict) { W("  -> keine ATAS-Aktion ausgefuehrt."); Flush(sb, outFile); return 3; }
        if (Process.GetProcessesByName("OFT.Platform").Length == 0) { W("BLOCKER: ATAS laeuft nicht."); Flush(sb, outFile); return 3; }
        if (keepAttached) W("# HINWEIS: keepRecorderAttached=true ist eine noch nicht live-verifizierte Variante. Dieser Lauf nutzt weiterhin den bewaehrten add/remove-Pfad je Fenster (saubere, finalisierte Ordnertrennung; Voraussetzung fuer das sichere Anhaengen).");

        string Pf = "dd.MM.yyyy HH:mm:ss";
        var jobs = new List<(string dStart, string dEnd, DateTime sUtc, DateTime eUtc, string jobId)>();
        try
        {
            foreach (var (ds, de) in windows)
            {
                var s = DateTime.ParseExact(ds, Pf, System.Globalization.CultureInfo.InvariantCulture);
                var e = DateTime.ParseExact(de, Pf, System.Globalization.CultureInfo.InvariantCulture);
                var sU = BerlinToUtc(s); var eU = BerlinToUtc(e);
                jobs.Add((ds, de, sU, eU, BuildJobId(session, year, sU, eU)));
            }
            foreach (var (sU, eU) in utcWindows)
            {
                if (!(sU < eU)) throw new ArgumentException($"Ende {eU:o} liegt nicht nach Start {sU:o}");
                jobs.Add(($"{sU:yyyy-MM-ddTHH:mm:ssZ}", $"{eU:yyyy-MM-ddTHH:mm:ssZ}", sU, eU, BuildJobId(session, year, sU, eU)));
            }
        }
        catch (Exception ex) { W($"BLOCKER: Fenster nicht parsebar ({ex.Message})."); Flush(sb, outFile); return 3; }
        foreach (var j in jobs)
            W($"#  Fenster UTC [{j.sUtc:yyyy-MM-ddTHH:mm:ssZ}, {j.eUtc:yyyy-MM-ddTHH:mm:ssZ})  job_id={j.jobId}");

        var committed = ReadCommittedJobs(py, script, collDir, W);
        W($"# bereits committed: {(committed.Count == 0 ? "(keine)" : string.Join(", ", committed))}");
        var todo = JobsToRun(jobs.Select(j => j.jobId).ToList(), committed);
        if (todo.Count == 0) W("# Alle Fenster bereits committed -> nichts aufzunehmen (Wiederanlauf ohne Doppeldaten).");

        int done = 0, skipped = 0; string stopReason = "";
        for (int i = 0; i < jobs.Count; i++)
        {
            var j = jobs[i];
            if (committed.Contains(j.jobId)) { skipped++; W($"\n[SKIP] {j.jobId} bereits committed -> nicht erneut aufnehmen/anhaengen."); continue; }
            if (StopRequested()) { stopReason = "Kontrolliert gestoppt (Anforderung der Oberflaeche) vor dem naechsten Fenster"; break; }
            W($"\n===== Fenster-Auftrag {i + 1}/{jobs.Count}: {j.jobId} =====");
            int rc = RunRecordingJob(outFile, j.sUtc, j.eUtc, i + 1, jobs.Count, sb, speed);
            if (rc != 0) { stopReason = $"Aufnahme-Auftrag {j.jobId} nicht bestanden (rc={rc})"; break; }
            if (_lastJobFolder == null || !_lastJobFinalizedClean) { stopReason = $"Instanz fuer {j.jobId} nicht finalisiert (folder={_lastJobFolder ?? "-"})"; break; }
            string recDir = Path.Combine(RecordingsDir, _lastJobFolder);
            var (code, outp, err) = RunPython(py, new[] {
                script, "append", "--collection", collDir, "--recording", recDir, "--job-id", j.jobId,
                "--start", $"{j.sUtc:yyyy-MM-ddTHH:mm:ssZ}", "--end", $"{j.eUtc:yyyy-MM-ddTHH:mm:ssZ}",
                "--session", session, "--year", year.ToString(), "--timezone", tz });
            W($"[collect] append rc={code} {outp.Trim()}{(err.Trim() == "" ? "" : "  stderr=" + err.Trim())}");
            string result = ExtractJsonField(outp, "result");
            if (code != 0 || (result != "committed" && result != "skipped" && result != "empty"))
            { stopReason = $"Anhaengen fuer {j.jobId} fehlgeschlagen (result='{result}', rc={code})"; break; }
            done++;
        }

        var finalCommitted = ReadCommittedJobs(py, script, collDir, W);
        bool allDone = CollectionComplete(jobs.Select(j => j.jobId).ToList(), finalCommitted);
        W($"\n===== SAMMELLAUF-GESAMT: {(stopReason == "" && allDone ? "ALLE FENSTER IN GEMEINSAMER CSV" : "NICHT VOLLSTAENDIG")} =====");
        if (stopReason != "") W($"  [STOPP] {stopReason} -> keine weiteren Fenster, keine Wiederholungsschleife. Aufnahmen bleiben erhalten.");
        foreach (var j in jobs) W($"  {j.jobId}: {(finalCommitted.Contains(j.jobId) ? "committed" : "offen")}");
        var (_, statusOut, _) = RunPython(py, new[] { script, "status", "--collection", collDir });
        W($"  [status] {statusOut.Trim()}");
        W($"  aktive gesperrte Recorder jetzt: {LockedCount()} (erwartet 0)");
        Flush(sb, outFile);
        return (stopReason == "" && allDone) ? 0 : 8;
    }

    internal static bool BridgeSelfcheckOk(int code, string stdout) => code == 0 && ExtractJsonField(stdout, "result") == "ok";

    // Alter fest eingebauter Standardpfad der Recorder-DLL 'proto-2-bufferfix' (nur noch zur Erkennung, kein Schreibziel).
    internal static readonly string LegacyRecorderRoot = Path.Combine("F:" + Path.DirectorySeparatorChar, "ATAS_Data", "Recordings");

    // Schreibt der in ATAS installierte Recorder in den Ordner, in dem dieser Lauf die Aufnahme sucht?
    //  - DLL mit gemeinsamer Konfiguration: die Konfiguration muss GENAU auf den Recordings-Ordner dieses Laufs zeigen.
    //  - aeltere DLL (fester Pfad): nur zulaessig, wenn der Lauf genau diesen festen Pfad benutzt.
    internal static bool RecorderPathVerdict(string recordingsDir, bool dllInstalled, bool dllSupportsSharedRoot, string? sharedConfig, out string msg)
    {
        if (!dllInstalled) { msg = $"Recorder-DLL nicht installiert ({SharedDataRoot.InstalledRecorderDll})."; return false; }
        if (dllSupportsSharedRoot)
        {
            if (SharedDataRoot.SamePath(sharedConfig, recordingsDir)) { msg = $"{recordingsDir} (gemeinsame Konfiguration stimmt)"; return true; }
            msg = $"gemeinsame Recorder-Konfiguration zeigt auf '{sharedConfig ?? "(nicht gesetzt)"}', dieser Lauf erwartet '{recordingsDir}'. " +
                  "Datenordner in der Oberflaeche neu waehlen oder Setup erneut ausfuehren.";
            return false;
        }
        if (SharedDataRoot.SamePath(LegacyRecorderRoot, recordingsDir)) { msg = $"{recordingsDir} (aeltere Recorder-DLL mit festem Pfad, passt)"; return true; }
        msg = $"installierte Recorder-DLL ist eine aeltere Version mit festem Ausgabepfad '{LegacyRecorderRoot}' und wuerde NICHT nach " +
              $"'{recordingsDir}' schreiben. Setup bei geschlossenem ATAS erneut ausfuehren.";
        return false;
    }

    // Python-Collector ausfuehren -> (ExitCode, stdout, stderr). Keine Shell; Argumente einzeln (kein Quoting-Risiko).
    private static (int code, string stdout, string stderr) RunPython(string py, string[] args)
    {
        var psi = new ProcessStartInfo { FileName = py, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };   // Collector schreibt UTF-8 (Pfade mit Umlauten)
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd(); string e = p.StandardError.ReadToEnd();
            p.WaitForExit(120000);
            return (p.HasExited ? p.ExitCode : -1, o, e);
        }
        catch (Exception ex) { return (-2, "", ex.GetType().Name + ": " + ex.Message); }
    }

    // Committete job_ids aus dem Collection-Manifest (ueber Collector 'status'); robust bei fehlender/neuer Collection.
    private static HashSet<string> ReadCommittedJobs(string py, string script, string collDir, Action<string> W)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var (code, outp, err) = RunPython(py, new[] { script, "status", "--collection", collDir });
        if (code != 0) { W($"  [resume] status rc={code} (Collection evtl. neu){(err.Trim() == "" ? "" : " " + err.Trim())}"); return set; }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(outp);
            if (doc.RootElement.TryGetProperty("jobs", out var js) && js.ValueKind == System.Text.Json.JsonValueKind.Object)
                foreach (var jp in js.EnumerateObject())
                    if (jp.Value.GetString() == "committed") set.Add(jp.Name);
        }
        catch { /* leeres/neues Manifest -> keine committeten Auftraege */ }
        return set;
    }

    private static string ExtractJsonField(string json, string field)
    {
        try { using var doc = System.Text.Json.JsonDocument.Parse(json); return doc.RootElement.TryGetProperty(field, out var v) ? (v.GetString() ?? "") : ""; }
        catch { var m = Regex.Match(json, "\"" + Regex.Escape(field) + "\"\\s*:\\s*\"([^\"]*)\""); return m.Success ? m.Groups[1].Value : ""; }
    }

    // ================= AUTONOMER DURCHLAUF (ein Startbefehl, keine LLM-/UI-Eingriffe zwischen den Schritten) =================
    // Zustaende mit ueberprüfbaren Uebergaengen; fensteruebergreifende UIA-Suche; Laufzeit-abgeleitete Zielpositionen;
    // hoechstens 1 gezielter Retry je Schritt; Fehler -> Exitcode != 0. Finalisiert zwei kurze Fenster auf F:.
    private static int AutoRun(string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); if (outFile != null) { try { File.WriteAllText(outFile, sb.ToString()); } catch { } } }
        double tBedienungS = 0, tLadenS = 0, tWiedergabeS = 0;
        var swAll = Stopwatch.StartNew();
        W($"# ATAS AutoRun  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  Ausgabe: {RecordingsDir}");
        if (!DriveReadyWritable(RecordingsDir, out var rdrAr)) { W("BLOCKER: " + rdrAr); return 3; }
        string cRecDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "ATAS_OF_Recordings");
        var cBefore = Directory.Exists(cRecDir) ? Directory.GetDirectories(cRecDir).Select(Path.GetFileName).ToHashSet() : new HashSet<string?>();

        // DialogEnd mit Puffer (Fensterende + ~3 min) laden, damit das Replay beim Pausieren (am echten Fensterende UtcEnd)
        // NICHT das Ende des geladenen Bereichs erreicht -> 'Datum auswählen' bleibt fuer das naechste Fenster aktiv.
        var wins = new[]
        {
            new Win("26.10.2026 09:00-09:01 Berlin", "26.10.2026 09:00:00", "26.10.2026 09:04:00", new DateTime(2026,10,26,8,0,0,DateTimeKind.Utc), new DateTime(2026,10,26,8,1,0,DateTimeKind.Utc)),
            new Win("26.10.2026 15:30-15:31 Berlin", "26.10.2026 15:30:00", "26.10.2026 15:34:00", new DateTime(2026,10,26,14,30,0,DateTimeKind.Utc), new DateTime(2026,10,26,14,31,0,DateTimeKind.Utc)),
        };

        var results = new List<string>();
        var attributed = new List<(string folder, string token)>();   // A2/A4: jeder zugeordnete Auftrag (Ordner + Token)
        string? folderPrev = null;
        string tokenFile = Path.Combine(RecordingsDir, ".of_run_token");
        string? lastToken = null;   // A4: fuer owner-gesicherten Rueckzug im finally
        int rc = -1;                        // -1 = Ergebnis aus den Fenstern berechnen; sonst Abbruch-Code
        bool cleanupOk = false, cClean = true;

        try
        {
            // ---- Schritt 1: Replay sicherstellen (offen) ----
            var swB = Stopwatch.StartNew();
            if (Process.GetProcessesByName("OFT.Platform").Length == 0) throw new AbortRun(3, "BLOCKER: ATAS laeuft nicht.");
            if (ReplayWindow() == null)
            {
                var rb = FindIdAny("RibbonReplayButton");
                if (rb == null) throw new AbortRun(4, "BLOCKER: RibbonReplayButton nicht gefunden.");
                W("[1] Replay oeffnen (RibbonReplayButton) …"); Invoke(rb);
                for (int i = 0; i < 12 && ReplayWindow() == null; i++) Thread.Sleep(1000);
            }
            if (ReplayWindow() == null) throw new AbortRun(4, "BLOCKER: Replay-Fenster nicht offen.");
            W($"[1] Replay offen: '{Safe(() => ReplayWindow()!.Current.Name)}'");
            // Falls das Replay noch spielt, pausieren (sonst ist die Datumsauswahl gesperrt).
            {
                string t = Safe(() => ReplayWindow()?.Current.Name ?? "");
                if (t.Contains("Historienwiedergabe", StringComparison.OrdinalIgnoreCase) && !t.Contains("angehalten", StringComparison.OrdinalIgnoreCase))
                {
                    var pb = FindIdAny("PlayButton"); if (pb != null) { W("[1] Replay spielt -> pausieren."); Invoke(pb); Thread.Sleep(1500); }
                }
            }

            // ---- Schritt 2: Modul sicherstellen (Zielchart als Replay-Modul) ----
            if (!EnsureModule(W)) { tBedienungS += swB.Elapsed.TotalSeconds; throw new AbortRun(5, "BLOCKER: Modulauswahl fehlgeschlagen (Zielchart nicht eindeutig)."); }
            tBedienungS += swB.Elapsed.TotalSeconds;

            for (int wi = 0; wi < wins.Length; wi++)
            {
                var win = wins[wi];
                W($"\n===== Fenster {wi + 1}/{wins.Length}: {win.Label}  (erwartet UTC {win.UtcStart:yyyy-MM-dd HH:mm}->{win.UtcEnd:HH:mm}) =====");

                // ---- Schritt 4: Datum setzen+zuruecklesen (bis zu 1 Retry) ----
                var swB2 = Stopwatch.StartNew();
                bool dateOk = false;
                for (int attempt = 0; attempt < 2 && !dateOk; attempt++)
                {
                    var dlg = OpenDateDialog(W);
                    if (dlg == null) { W($"  [4.{attempt}] Datumsdialog nicht offen."); continue; }
                    bool SetF(string id, string want)
                    { for (int a = 0; a < 4; a++) { TypeIntoField(FindByAutomationId(dlg, id) ?? FindIdAny(id), want); var g = ValueOf(FindByAutomationId(dlg, id) ?? FindIdAny(id)); if (g.StartsWith(want.Substring(0, 19), StringComparison.Ordinal)) return true; } return false; }
                    bool okE = SetF("EndEdit", win.DialogEnd), okS = SetF("StartEdit", win.DialogStart);
                    string gs = ValueOf(FindIdAny("StartEdit")), ge = ValueOf(FindIdAny("EndEdit"));
                    W($"  [4] Datum Start='{gs}' Ende='{ge}' (okS={okS} okE={okE})");
                    dateOk = okS && okE;
                }
                if (!dateOk) { W("BLOCKER: Datum nicht setzbar."); results.Add($"F{wi + 1}: ABBRUCH (Datum)"); tBedienungS += swB2.Elapsed.TotalSeconds; break; }
                tBedienungS += swB2.Elapsed.TotalSeconds;

                // ---- Schritt 4b: A4-Token VOR dem Laden schreiben; Laden + Bereitschaft; Schritt 5: token-eindeutige Instanz ----
                var swL = Stopwatch.StartNew();
                // A4 (Finding 4): eindeutiges Auftragstoken VOR dem Laden; der Zielrecorder liest es und schreibt es in meta/summary.
                string jobToken = Guid.NewGuid().ToString("N"); lastToken = jobToken;
                // A4 (Recheck): Token ATOMAR bereitstellen (temp->rename). Der Zielrecorder konsumiert es EXKLUSIV.
                try { OrderFlowTools.TokenHandshake.Publish(RecordingsDir, jobToken); } catch (Exception ex) { W($"  [4b] WARNUNG: Token nicht schreibbar: {ex.Message}"); }
                var before = Directory.Exists(RecordingsDir) ? Directory.GetDirectories(RecordingsDir).Select(Path.GetFileName).Where(x => x != null).ToHashSet()! : new HashSet<string>();
                var sel = FindBtnAny("Auswählen");
                if (sel == null) { W("BLOCKER: 'Auswählen' nicht gefunden."); results.Add($"F{wi + 1}: ABBRUCH (Auswählen)"); tLadenS += swL.Elapsed.TotalSeconds; break; }
                DateTime loadClickUtc = DateTime.UtcNow.AddSeconds(-10);
                W($"  [4b] Laden (Auswählen), Auftragstoken={jobToken} …"); Invoke(sel);
                // A4 (Recheck2): ALLE aktiven (gesperrten) MNQ-Instanzen global erfassen (neu + alt) -> Ein-Recorder-Betrieb erzwingen.
                List<(string folder, string runId, bool isNew)> LockedMnq()
                {
                    var res2 = new List<(string folder, string runId, bool isNew)>();
                    var dirs = Directory.Exists(RecordingsDir) ? Directory.GetDirectories(RecordingsDir).Select(Path.GetFileName).Where(x => x != null)! : Enumerable.Empty<string>();
                    foreach (var f in dirs)
                    {
                        var meta = Path.Combine(RecordingsDir, f!, "meta.json");
                        if (!IsMetaLocked(meta)) continue;
                        var inst = ReadMetaField(meta, "instrument");
                        if (!inst.Contains("MNQ", StringComparison.OrdinalIgnoreCase)) continue;
                        var sw = ParseDt(ReadMetaField(meta, "startWallUtc"));
                        bool isNew = !before.Contains(f!) && sw != null && sw.Value.ToUniversalTime() >= loadClickUtc;
                        res2.Add((f!, ReadMetaField(meta, "runId"), isNew));
                    }
                    return res2;
                }
                bool ready = false; string lastLog = ""; bool sawLaden = false; var locked = new List<(string folder, string runId, bool isNew)>();
                for (int i = 0; i < 180 && !ready; i++)
                {
                    Thread.Sleep(1000);
                    string title = Safe(() => ReplayWindow()?.Current.Name ?? ""); string lg = LatestReplayState();
                    if (title.Contains("laden", StringComparison.OrdinalIgnoreCase)) sawLaden = true;
                    locked = LockedMnq();
                    if (lg != lastLog) { lastLog = lg; W($"    [t+{i + 1}s] laden={sawLaden} aktiveInstanzen={locked.Count} (neu {locked.Count(x => x.isNew)}) | {lg}"); }
                    if (sawLaden && locked.Count(x => x.isNew) >= 1 && title.Contains("gestoppt", StringComparison.OrdinalIgnoreCase) && i >= 3) ready = true;
                }
                Thread.Sleep(1200); locked = LockedMnq();
                var verdict = AttributionVerdict(locked.Select(x => (x.runId, x.isNew)).ToList(), jobToken);
                string? folder = null;
                if (ready && verdict == Attrib.Unique) folder = locked.First(x => x.isNew).folder;
                else if (verdict == Attrib.Ambiguous) { W($"BLOCKER (A4): kein Ein-Recorder-Betrieb — {locked.Count} aktive MNQ-Instanzen: {string.Join(", ", locked.Select(x => x.folder))} -> Abbruch (fremde/mehrdeutige Instanz)."); results.Add($"F{wi + 1}: ABBRUCH (A4 mehrdeutig)"); tLadenS += swL.Elapsed.TotalSeconds; break; }
                if (!ready || folder == null)
                {
                    W($"BLOCKER (A4): keine eindeutige, token-tragende Ein-Recorder-Instanz (aktiv={locked.Count}, neu={locked.Count(x => x.isNew)}, sawLaden={sawLaden}). {(locked.Count == 0 ? "Recorder evtl. nicht angehaengt." : "Einzige Instanz trägt nicht das Auftragstoken / ist nicht neu -> abgelehnt.")}");
                    foreach (var l in TailReplayLog(6)) W("   " + l);
                    results.Add($"F{wi + 1}: ABBRUCH ({(locked.Count == 0 ? "keine Recorder-Instanz" : "keine eindeutige Auftragszuordnung")})"); tLadenS += swL.Elapsed.TotalSeconds; break;
                }
                string dir = Path.Combine(RecordingsDir, folder);
                string runId = ReadMetaField(Path.Combine(dir, "meta.json"), "runId");
                attributed.Add((folder, jobToken));   // A2: fuer den garantierten Cleanup-Abschluss registrieren
                W($"  [5] Instanz (F:): {folder}  runId={runId}  (einzige aktive MNQ-Instanz == Auftragstoken)");
                tLadenS += swL.Elapsed.TotalSeconds;

                // ---- Schritt 6: 1x setzen+zuruecklesen, Play bis Marktzeit-Ende ----
                var swW = Stopwatch.StartNew();
                var speed = FindSpeedAny(); string sp = speed != null ? Safe(() => speed.Current.Name) : "?";
                if (speed != null && !Regex.IsMatch(sp, @"^\s*1x\s*$", RegexOptions.IgnoreCase))
                { Invoke(speed); Thread.Sleep(800); var it = FindMenuItemByName(new Regex(@"^\s*1x\s*$", RegexOptions.IgnoreCase)); if (it != null) { Invoke(it); Thread.Sleep(500); } speed = FindSpeedAny(); sp = speed != null ? Safe(() => speed.Current.Name) : "?"; }
                bool is1x = Regex.IsMatch(sp, @"^\s*1x\s*$", RegexOptions.IgnoreCase);
                W($"  [6] Speed='{sp}' {(is1x ? "OK" : "!! nicht 1x")}");
                var play = FindIdAny("PlayButton");
                if (!IsEnabledSafe(play)) { Thread.Sleep(2000); play = FindIdAny("PlayButton"); }
                if (!IsEnabledSafe(play)) { W("BLOCKER: PlayButton nicht bereit."); results.Add($"F{wi + 1}: ABBRUCH (Play)"); tWiedergabeS += swW.Elapsed.TotalSeconds; break; }
                // A1 (Recheck2): Play-Zeitpunkt für die Daten-Querprüfung; obere Grenze aus dem TATSÄCHLICH geladenen Fenster.
                DateTime playInvokeUtc = DateTime.UtcNow;
                DateTime loadedEndUtc = LoadedEndUtc(win);   // aus DialogStart/DialogEnd abgeleitet, NICHT fix +15min
                W("  [6] Play …"); Invoke(play); Thread.Sleep(2500);
                var swp = Stopwatch.StartNew(); DateTime? mp = null, lastProg = null; bool reachedEnd = false; int stall = 0; string endReason = "end_unproven_no_verified_replay_signal"; bool playingObserved = false;
                while (swp.Elapsed.TotalSeconds < 220)
                {
                    Thread.Sleep(3000);
                    var st = FolderStreams(dir);
                    mp = ReplayProgress(st, win.UtcStart, win.UtcEnd);
                    if (mp != null && lastProg != null && mp == lastProg) stall++; else stall = 0;
                    if (mp != null) lastProg = mp;
                    string title = Safe(() => ReplayWindow()?.Current.Name ?? "");
                    if (title.Contains("Historienwiedergabe", StringComparison.OrdinalIgnoreCase) && !title.Contains("angehalten", StringComparison.OrdinalIgnoreCase)) playingObserved = true;
                    // A1 (Recheck2): Erfolg NUR aus verifiziertem, auftraggebundenem Replay-Signal (AssessEnd); Daten nur Protokoll.
                    var endVerdict = DecideEnd(dir, playInvokeUtc, win.UtcEnd, loadedEndUtc, TryVerifiedReplayPositionUtc(), playingObserved, W);
                    if (endVerdict == EndVerdict.Reached) { reachedEnd = true; endReason = "verified_replay_end"; break; }
                    if (endVerdict == EndVerdict.StoppedBeforeEnd) { endReason = "replay_stopped_before_end"; break; }
                    if (title.Contains("angehalten", StringComparison.OrdinalIgnoreCase) || title.Contains("gestoppt", StringComparison.OrdinalIgnoreCase)) { endReason = "replay_stopped_before_end"; break; }
                    if (stall >= 12) { endReason = "stall_no_market_progress"; break; }
                }
                // ---- Schritt 7: Pause (immer, auch bei Fehler) ----
                var p2 = FindIdAny("PlayButton"); string tp = Safe(() => ReplayWindow()?.Current.Name ?? "");
                if (!tp.Contains("angehalten", StringComparison.OrdinalIgnoreCase) && !tp.Contains("gestoppt", StringComparison.OrdinalIgnoreCase) && p2 != null) { Invoke(p2); Thread.Sleep(1200); }
                var fin = FolderStreams(dir); fin.TryGetValue("ticks.ndjson", out var fv);
                var mpFin = ReplayProgress(fin, win.UtcStart, win.UtcEnd);
                string span = fv?.FirstMt != null ? $"{fv.FirstMt:yyyy-MM-dd HH:mm:ss}->{fv.LastMt:HH:mm:ss}Z" : "-";
                string mpStr = mpFin != null ? mpFin.Value.ToString("HH:mm:ss") + "Z" : "-";
                double dev = fv?.FirstMt is { } fm ? Math.Abs((fm.ToUniversalTime() - win.UtcStart).TotalMinutes) : 9999;
                W($"  [6/7] Marktende={mpStr} Trade-Abdeckung={span} (erwartet {win.UtcStart:HH:mm}->{win.UtcEnd:HH:mm}Z) reachedEnd={reachedEnd}({endReason}) tz-dev={dev:F1}min ticks={fv?.Bytes ?? 0}B");
                tWiedergabeS += swW.Elapsed.TotalSeconds;
                // A1: Timeout/Stillstand/vorzeitiger Stopp ist KEIN Erfolg.
                if (!reachedEnd) { W($"BLOCKER (A1): angefordertes Ende nicht belegt erreicht (reason={endReason})."); results.Add($"F{wi + 1}: ABBRUCH (Ende nicht erreicht: {endReason})"); break; }
                if (dev > 15) { W("BLOCKER: Marktzeit weicht grob ab (Zeitzone/Zuordnung)."); results.Add($"F{wi + 1}: ABBRUCH (tz {span})"); break; }
                if ((fv?.Bytes ?? 0) == 0) { W("BLOCKER: keine Ticks aufgezeichnet."); results.Add($"F{wi + 1}: ABBRUCH (0 Ticks)"); break; }
                results.Add($"F{wi + 1} {win.Label}: Ordner={folder} Marktzeit={span} ticks={fv?.Bytes ?? 0}B reachedEnd={reachedEnd}");
                folderPrev = folder;
                try { OrderFlowTools.TokenHandshake.RetractIfOwner(RecordingsDir, jobToken); } catch { }
            }

            // ---- Finalisierung des letzten Fensters via Dispose-Load ----
            if (folderPrev != null && results.Any() && !results.Last().Contains("ABBRUCH"))
            {
                var swF = Stopwatch.StartNew();
                W($"\n[final] letztes Fenster {folderPrev} finalisieren (Dispose-Load) …");
                var dlg = OpenDateDialog(W);
                if (dlg != null)
                {
                    for (int a = 0; a < 4; a++) { TypeIntoField(FindByAutomationId(dlg, "EndEdit") ?? FindIdAny("EndEdit"), wins[0].DialogEnd); if (ValueOf(FindIdAny("EndEdit")).StartsWith(wins[0].DialogEnd.Substring(0, 19))) break; }
                    for (int a = 0; a < 4; a++) { TypeIntoField(FindByAutomationId(dlg, "StartEdit") ?? FindIdAny("StartEdit"), wins[0].DialogStart); if (ValueOf(FindIdAny("StartEdit")).StartsWith(wins[0].DialogStart.Substring(0, 19))) break; }
                    var sel = FindBtnAny("Auswählen"); if (sel != null) { Invoke(sel); for (int i = 0; i < 40; i++) { Thread.Sleep(1000); if (File.Exists(Path.Combine(RecordingsDir, folderPrev, "summary.json"))) break; } }
                }
                W($"[final] {folderPrev} summary.json={(File.Exists(Path.Combine(RecordingsDir, folderPrev, "summary.json")) ? "JA" : "NEIN")}");
                tLadenS += swF.Elapsed.TotalSeconds;
            }
        }
        catch (AbortRun ab) { W(ab.Message); rc = ab.Code; }
        catch (Exception ex) { W($"BLOCKER: unerwartete Exception {ex.GetType().Name}: {ex.Message}"); rc = 2; }
        finally
        {
            // A2 (Recheck): der finally-Pfad darf NIE eine Exception werfen (sonst würde der Abbruch-Exitcode durch
            // einen Sammelfehler ersetzt und die Abschlussausgabe übersprungen). Jeder Schritt einzeln gekapselt.
            try { if (lastToken != null) OrderFlowTools.TokenHandshake.RetractIfOwner(RecordingsDir, lastToken); } catch { }
            try { cleanupOk = CleanupAfterRun(W, attributed); }
            catch (Exception ex) { cleanupOk = false; W($"[A2] Cleanup-Ausnahme ({ex.GetType().Name}: {ex.Message}) -> Cleanup NICHT erfolgreich."); }
            try
            {
                var cAfter = Directory.Exists(cRecDir) ? Directory.GetDirectories(cRecDir).Select(Path.GetFileName).ToHashSet() : new HashSet<string?>();
                var cNew = cAfter.Except(cBefore).ToList();
                cClean = cNew.Count == 0;
                W($"\n[C:-check] neue Aufnahmeordner auf C: waehrend des Laufs: {(cClean ? "KEINE" : "WARNUNG: " + string.Join(", ", cNew))}  (Alt-Leftover ignoriert)");
            }
            catch (Exception ex) { cClean = false; W($"[C:-check] FEHLER ({ex.GetType().Name}: {ex.Message}) -> konservativ nicht sauber."); }
        }

        bool allWindowsOk = results.Count == wins.Length && results.All(r => !r.Contains("ABBRUCH"));
        bool ok = rc == -1 && allWindowsOk && cClean && cleanupOk;
        if (rc == -1) rc = ok ? 0 : 8;
        W($"\n===== ERGEBNIS ({(ok ? "BESTANDEN" : "NICHT bestanden")}) =====  (rc={rc} cleanup={(cleanupOk ? "ok" : "FEHLER")})");
        foreach (var r in results) W("  " + r);
        W($"[timing] gesamt {swAll.Elapsed.TotalSeconds:F0}s = Bedienung {tBedienungS:F0}s + Laden {tLadenS:F0}s + Wiedergabe {tWiedergabeS:F0}s");
        Flush(sb, outFile);
        return rc;
    }

    // Oeffnet den Datumsdialog (ReplayDateSelectionDialog) per echtem Klick auf 'Datum auswählen' (Transport: Invoke genuegt nicht).
    private static AutomationElement? OpenDateDialog(Action<string> W)
    {
        var dlg = FindIdAny("ReplayDateSelectionDialog");
        if (dlg != null) return dlg;
        // 1) UIA-Invoke (wie im erfolgreichen series-Lauf).
        for (int o = 0; o < 2 && dlg == null; o++)
        {
            var d = FindBtnAny("Datum auswählen"); if (d == null) break;
            if (!IsEnabledSafe(d)) { W($"  [datum] 'Datum auswählen' disabled (Replay-Zustand) – warte …"); Thread.Sleep(1500); continue; }
            Invoke(d); Thread.Sleep(1500); dlg = FindIdAny("ReplayDateSelectionDialog");
        }
        if (dlg != null) return dlg;
        // 2) Fallback: echter Klick (Hauptfenster-Vordergrund).
        var mainWin = AtasRoots().FirstOrDefault(w => Safe(() => w.Current.Name).StartsWith("ATAS -", StringComparison.OrdinalIgnoreCase));
        for (int o = 0; o < 4 && dlg == null; o++)
        {
            var d = FindBtnAny("Datum auswählen"); if (d == null) break;
            System.Windows.Rect r; try { r = d.Current.BoundingRectangle; } catch { r = System.Windows.Rect.Empty; }
            if (r.IsEmpty) break;
            if (mainWin != null) { ForceForeground(mainWin); Thread.Sleep(150); }
            ClickScreen((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2)); Thread.Sleep(900);
            dlg = FindIdAny("ReplayDateSelectionDialog");
        }
        return dlg;
    }

    // Klickt ein UIA-Element per echtem Mausklick in seiner Rect-Mitte (Transport braucht echten Klick, kein Invoke).
    private static void RealClick(AutomationElement el, int settleMs = 1000)
    {
        System.Windows.Rect r; try { r = el.Current.BoundingRectangle; } catch { return; }
        if (r.IsEmpty) return;
        ClickScreen((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2)); Thread.Sleep(settleMs);
    }

    // Stellt Replay auf DATUM-basiert + Zielchart als Modul, sodass 'Datum auswählen' aktiv ist.
    // Transportklicks erfordern aktives Fenster (erster Klick wird sonst verschluckt) -> ForceForeground des Transports.
    private static bool EnsureModule(Action<string> W)
    {
        // A4 (Recheck): Zielchart IMMER zuerst eindeutig verifizieren — auch im bereits vorbereiteten Zustand
        // (sonst würde ein aktiver Datumsbutton die Chart-Prüfung überspringen). Nicht eindeutig -> Abbruch.
        if (!VerifyTargetChartUnique(W)) return false;
        if (IsEnabledSafe(FindBtnAny("Datum auswählen"))) { W("[2/3] Datum-basiertes Replay bereits aktiv (Zielchart verifiziert)."); return true; }

        var mainWin = AtasRoots().FirstOrDefault(w => Safe(() => w.Current.Name).StartsWith("ATAS -", StringComparison.OrdinalIgnoreCase));
        void FgClick(System.Windows.Rect r) { if (mainWin != null) { ForceForeground(mainWin); Thread.Sleep(150); } ClickScreen((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2)); Thread.Sleep(700); }
        for (int attempt = 0; attempt < 2; attempt++)
        {
            // (a) Modul (Zielchart) waehlen: 'Modul auswählen' scharfschalten, bis Prompt 'Bar/Zeitraum/Datum auswählen'-Button erscheint.
            var modul = FindBtnAny("Modul auswählen");
            if (modul != null && !IsEnabledSafe(FindBtnAny("Bar auswählen")) && !IsEnabledSafe(FindBtnAny("Datum auswählen")))
            {
                System.Windows.Rect mr0; try { mr0 = modul.Current.BoundingRectangle; } catch { mr0 = System.Windows.Rect.Empty; }
                W("[3] 'Modul auswählen' scharfschalten …");
                for (int k = 0; k < 3; k++) { if (!mr0.IsEmpty) FgClick(mr0); var m2 = FindBtnAny("Modul auswählen"); try { if (m2 != null) { var tg = m2.GetCurrentPropertyValue(AutomationElement.ControlTypeProperty); } } catch { } if (FindBtnAny("Bar auswählen") != null || FindBtnAny("Datum auswählen") != null) break; }
                // A4 (Finding 4): KEIN Größte-Fläche-Fallback (ein fremder Chart könnte der größte sein). Stattdessen
                // den AUSDRÜCKLICH benannten Zielchart über seinen Header identifizieren und bei Mehrdeutigkeit abbrechen.
                // Zielchart-Flaeche zum Anklicken: mit ATAS_TARGET_CHART ueber dessen Header (wie bisher), sonst dieselbe
                // eindeutige Erkennung wie in Phase 1 (kein fester Chartname, kein fester Renderer-Name, keine Groesste-Flaeche-Wahl).
                System.Windows.Rect pr;
                string targetName = Environment.GetEnvironmentVariable("ATAS_TARGET_CHART") ?? "";
                if (!string.IsNullOrWhiteSpace(targetName))
                {
                    var roots = AtasRoots();
                    var headers = FindInRoots(roots, ControlType.Button)
                        .Where(b => Safe(() => b.Current.Name).StartsWith(targetName, StringComparison.OrdinalIgnoreCase))
                        .Select(b => { System.Windows.Rect r; try { r = b.Current.BoundingRectangle; } catch { r = System.Windows.Rect.Empty; } return r; })
                        .Where(r => !r.IsEmpty).ToList();
                    if (headers.Count != 1) { W($"[3] Zielchart '{targetName}' nicht eindeutig (Header-Treffer={headers.Count}) -> Abbruch (keine Größte-Fläche-Auswahl)."); return false; }
                    var hx = headers[0].X;
                    var cands = ScanChartCandidates().Candidates.Where(c => IsUsable(c) && c.Y > 0 && Math.Abs(c.X - hx) < 60).ToList();
                    var v3 = SelectChart(cands);
                    if (v3.Kind != ChartVerdictKind.Unique) { W($"[3] Zielchart-Flaeche zu '{targetName}' nicht eindeutig ({v3.Reason}) -> Abbruch."); return false; }
                    pr = new System.Windows.Rect(v3.Chosen!.X, v3.Chosen.Y, v3.Chosen.W, v3.Chosen.H);
                }
                else
                {
                    var v3 = SelectChart(ScanChartCandidates().Candidates);
                    if (v3.Kind != ChartVerdictKind.Unique) { W($"[3] Zielchart-Flaeche nicht eindeutig ({v3.Reason}) -> Abbruch."); return false; }
                    pr = new System.Windows.Rect(v3.Chosen!.X, v3.Chosen.Y, v3.Chosen.W, v3.Chosen.H);
                    targetName = v3.Signal;
                }
                W($"[3] Zielchart '{targetName}' rect={(int)pr.X},{(int)pr.Y},{(int)pr.Width}x{(int)pr.Height} -> klicken.");
                FgClick(pr); Thread.Sleep(900);
            }

            // (b) Modus auf 'Datum auswählen' stellen, falls er auf 'Bar/Zeitraum auswählen' steht.
            // Menueintraege sind CheckBoxen (BarCheckItemLink*, nicht MenuItem). Transport vor dem Menue-Klick aktivieren.
            if (!IsEnabledSafe(FindBtnAny("Datum auswählen")))
            {
                var mainW = AtasRoots().FirstOrDefault(w => Safe(() => w.Current.Name).StartsWith("ATAS -", StringComparison.OrdinalIgnoreCase));
                var mode = FindIdAny("ModeMenuButton");
                if (mode != null)
                {
                    W("[3] Modus-Menü öffnen -> 'Datum auswählen' …");
                    // Klick-und-prüfe (Muster wie erfolgreicher Handtest: Hauptfenster-Vordergrund + echter Klick, Parität unsicher).
                    AutomationElement? item = null;
                    for (int c = 0; c < 5 && item == null; c++)
                    {
                        if (mainW != null) { ForceForeground(mainW); Thread.Sleep(150); }
                        var mf = FindIdAny("ModeMenuButton") ?? mode;
                        System.Windows.Rect mr; try { mr = mf.Current.BoundingRectangle; } catch { mr = System.Windows.Rect.Empty; }
                        if (!mr.IsEmpty) { ClickScreen((int)(mr.X + mr.Width / 2), (int)(mr.Y + mr.Height / 2)); Thread.Sleep(700); }
                        item = FindCheckAny("Datum auswählen");
                    }
                    if (item != null) { Invoke(item); Thread.Sleep(1200); W("[3] Modus 'Datum auswählen' gesetzt."); }
                    else { W("[3] Mode-CheckBox 'Datum auswählen' nach mehreren Klicks nicht gefunden."); }
                }
            }

            if (IsEnabledSafe(FindBtnAny("Datum auswählen"))) { W("[3] Datum-basiertes Replay aktiv."); return true; }
            W($"[3] Versuch {attempt + 1} nicht erfolgreich (Datum noch nicht aktiv).");
        }
        return false;
    }

    // EIN Startbefehl: nimmt die vier Phase-1-Kurzfenster nacheinander auf. Pro Fenster: Datum setzen+zurücklesen,
    // laden, Bereitschaft prüfen, eindeutige Recorder-Instanz, 1x, Play bis Marktzeitende, Pause. Getrennte Ordner.
    // Auto-Finalisierung: der Folge-Load disponiert die vorherige Instanz (summary.json); ein finaler Dispose-Load
    // schliesst das letzte Fenster. Abbruch (kein blindes Wiederholen) bei: Datum nicht setzbar, nicht ladebereit,
    // unklare Zuordnung, Timeout, Marktzeit weicht grob vom erwarteten UTC-Fenster ab.
    private static int Series(string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); if (outFile != null) { try { File.WriteAllText(outFile, sb.ToString()); } catch { } } }
        W($"# ATAS Series (Phase 1, Europe/Berlin -> dialog=UTC+1)  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        // Berlin 26./27.10.2026 sind Normalzeit (UTC+1). Dialog-Zeitbasis = UTC+1 (Juni-Kalibrierung); fuer Okt == Berlin-Wanduhr.
        var wins = new[]
        {
            new Win("26.10.2026 09:00-09:01 Berlin", "26.10.2026 09:00:00", "26.10.2026 09:01:00", new DateTime(2026,10,26,8,0,0,DateTimeKind.Utc), new DateTime(2026,10,26,8,1,0,DateTimeKind.Utc)),
            new Win("26.10.2026 15:30-15:31 Berlin", "26.10.2026 15:30:00", "26.10.2026 15:31:00", new DateTime(2026,10,26,14,30,0,DateTimeKind.Utc), new DateTime(2026,10,26,14,31,0,DateTimeKind.Utc)),
            new Win("27.10.2026 09:00-09:01 Berlin", "27.10.2026 09:00:00", "27.10.2026 09:01:00", new DateTime(2026,10,27,8,0,0,DateTimeKind.Utc), new DateTime(2026,10,27,8,1,0,DateTimeKind.Utc)),
            new Win("27.10.2026 15:30-15:31 Berlin", "27.10.2026 15:30:00", "27.10.2026 15:31:00", new DateTime(2026,10,27,14,30,0,DateTimeKind.Utc), new DateTime(2026,10,27,14,31,0,DateTimeKind.Utc)),
        };

        var rw = ReplayWindow();
        if (rw == null) { W("BLOCKER: Replay-Fenster nicht offen (open-replay zuerst)."); return 4; }

        var results = new List<string>();
        string? folderPrev = null;
        for (int wi = 0; wi < wins.Length; wi++)
        {
            var win = wins[wi];
            W($"\n==================== Fenster {wi + 1}/4 : {win.Label} ====================");
            W($"[erwartet] UTC {win.UtcStart:yyyy-MM-dd HH:mm:ss} -> {win.UtcEnd:HH:mm:ss}   Dialog-Eingabe: '{win.DialogStart}'..'{win.DialogEnd}'");

            // --- Datum setzen + zuruecklesen ---
            var dlg = FindByAutomationId(rw, "ReplayDateSelectionDialog");
            if (dlg == null) { var d = FindButtonByName(rw, "Datum auswählen"); if (d != null) { Invoke(d); Thread.Sleep(1600); } dlg = FindByAutomationId(rw, "ReplayDateSelectionDialog"); }
            if (dlg == null) { W("BLOCKER: Datumsdialog nicht gefunden -> ABBRUCH."); results.Add($"F{wi + 1}: ABBRUCH (kein Datumsdialog)"); break; }
            bool SetField(string id, string want)
            {
                for (int a = 0; a < 4; a++) { TypeIntoField(FindByAutomationId(dlg, id), want); string got = ValueOf(FindByAutomationId(dlg, id)); if (got.StartsWith(want.Substring(0, 19), StringComparison.Ordinal)) return true; }
                return false;
            }
            bool okEnd = SetField("EndEdit", win.DialogEnd);
            bool okStart = SetField("StartEdit", win.DialogStart);
            string gs = ValueOf(FindByAutomationId(dlg, "StartEdit")), ge = ValueOf(FindByAutomationId(dlg, "EndEdit"));
            W($"[datum] gesetzt Start='{gs}' Ende='{ge}'  (okStart={okStart} okEnd={okEnd})");
            if (!(okStart && okEnd)) { W("BLOCKER: Datum nicht zuverlaessig setzbar -> ABBRUCH."); results.Add($"F{wi + 1}: ABBRUCH (Datum nicht setzbar)"); break; }

            // --- Laden ---
            var before = Directory.Exists(RecordingsDir) ? Directory.GetDirectories(RecordingsDir).Select(Path.GetFileName).Where(x => x != null).ToHashSet()! : new HashSet<string>();
            var select = FindButtonByName(dlg, "Auswählen") ?? FindButtonByName(rw, "Auswählen");
            if (select == null) { W("BLOCKER: 'Auswählen' nicht gefunden -> ABBRUCH."); results.Add($"F{wi + 1}: ABBRUCH (kein Auswählen)"); break; }
            W("[load] Invoke 'Auswählen' …"); Invoke(select);
            // Bereitschaft NUR, wenn (a) eine NEUE gesperrte Recorder-Instanz entstand (Load hat real re-initialisiert
            // und die vorherige disponiert) UND (b) Titel nach sichtbarer Ladephase auf 'gestoppt' steht.
            // Das vermeidet stale Vor-Lade-Signale und liefert zugleich die eindeutige Ordnerzuordnung.
            bool ready = false; string lastLog = ""; bool sawLaden = false; string? folder = null;
            for (int i = 0; i < 180 && !ready; i++)
            {
                Thread.Sleep(1000);
                string title = Safe(() => rw.Current.Name), lg = LatestReplayState();
                if (title.Contains("laden", StringComparison.OrdinalIgnoreCase)) sawLaden = true;
                var nowFolders = Directory.Exists(RecordingsDir) ? Directory.GetDirectories(RecordingsDir).Select(Path.GetFileName).Where(x => x != null).ToHashSet()! : new HashSet<string>();
                var newLocked = nowFolders.Except(before).Where(f => IsMetaLocked(Path.Combine(RecordingsDir, f!, "meta.json"))).OrderByDescending(f => f).ToList();
                if (lg != lastLog) { lastLog = lg; W($"  [t+{i + 1}s] sawLaden={sawLaden} neueInstanz={newLocked.Count} Titel='{title}' | {lg}"); }
                bool titleReady = sawLaden && title.Contains("gestoppt", StringComparison.OrdinalIgnoreCase);
                if (newLocked.Count >= 1 && titleReady && i >= 3) { ready = true; folder = newLocked[0]; }
            }
            Thread.Sleep(1500); // settlen
            if (!ready || folder == null)
            {
                // Unterscheiden: keine neue Instanz (Load/Dispose nicht erfolgt) vs. nicht ladebereit (Daten?).
                var nf = Directory.Exists(RecordingsDir) ? Directory.GetDirectories(RecordingsDir).Select(Path.GetFileName).Where(x => x != null).ToHashSet()! : new HashSet<string>();
                int nl = nf.Except(before).Count(f => IsMetaLocked(Path.Combine(RecordingsDir, f!, "meta.json")));
                W($"BLOCKER: nicht bereit in 180s (neueInstanz={nl}, sawLaden={sawLaden}) -> {(nl == 0 ? "Load/Dispose nicht erfolgt" : "Daten evtl. nicht verfuegbar")} -> ABBRUCH.");
                W("[log]"); foreach (var l in TailReplayLog(6)) W("   " + l);
                results.Add($"F{wi + 1}: {(nl == 0 ? "ABBRUCH (keine neue Instanz)" : "NICHT VERFUEGBAR")}"); break;
            }
            string dir = Path.Combine(RecordingsDir, folder);
            W($"[recorder] aktive Instanz: {folder}");
            if (folderPrev != null) { var sp = Path.Combine(RecordingsDir, folderPrev, "summary.json"); W($"[finalize vorheriges] {folderPrev} summary.json={(File.Exists(sp) ? "JA" : "NEIN")} locked={IsMetaLocked(Path.Combine(RecordingsDir, folderPrev, "meta.json"))}"); }

            // --- 1x bestaetigen ---
            var speed = FindButtonByNameRegex(rw, new Regex(@"^\s*\d+(\.\d+)?x\s*$", RegexOptions.IgnoreCase));
            string spName = speed != null ? Safe(() => speed.Current.Name) : "?";
            bool is1x = Regex.IsMatch(spName, @"^\s*1x\s*$", RegexOptions.IgnoreCase);
            W($"[speed] '{spName}' {(is1x ? "OK" : "!! nicht 1x")}");

            // --- Play bis Marktzeitende ---
            var play = FindByAutomationId(rw, "PlayButton"); // frisch
            if (play == null) { W("BLOCKER: PlayButton nicht gefunden -> ABBRUCH."); results.Add($"F{wi + 1}: ABBRUCH (kein PlayButton)"); break; }
            string t0 = Safe(() => rw.Current.Name);
            W("[play] Invoke PlayButton …"); Invoke(play); Thread.Sleep(2500);
            W($"[play] Titel '{t0}' -> '{Safe(() => rw.Current.Name)}'");
            var swp = Stopwatch.StartNew(); DateTime? firstMt = null, lastMt = null; bool reachedEnd = false;
            while (swp.Elapsed.TotalSeconds < 180)
            {
                Thread.Sleep(3000);
                var st = FolderStreams(dir);
                st.TryGetValue("ticks.ndjson", out var tv); if (tv?.FirstMt != null) { firstMt = tv.FirstMt; lastMt = tv.LastMt; }
                string title = Safe(() => rw.Current.Name);
                W($"  [t+{(int)swp.Elapsed.TotalSeconds,3}s] ticks={(tv?.Bytes ?? 0)} erster={(firstMt?.ToString("HH:mm:ss") ?? "-")} letzter={(lastMt?.ToString("HH:mm:ss") ?? "-")} Titel='{title}'");
                if (lastMt != null && lastMt.Value.ToUniversalTime() >= win.UtcEnd.AddSeconds(-2)) { reachedEnd = true; break; }
                if (title.Contains("angehalten", StringComparison.OrdinalIgnoreCase) || title.Contains("gestoppt", StringComparison.OrdinalIgnoreCase) || title.Contains("Echtzeit", StringComparison.OrdinalIgnoreCase)) { W("  [info] Replay nicht mehr spielend (Ende/Stop)."); break; }
            }
            // --- Pause ---
            string tP = Safe(() => rw.Current.Name);
            if (!tP.Contains("angehalten", StringComparison.OrdinalIgnoreCase) && !tP.Contains("gestoppt", StringComparison.OrdinalIgnoreCase))
            { W("[pause] Invoke PlayButton …"); Invoke(play); Thread.Sleep(1500); }
            W($"[pause] Titel '{Safe(() => rw.Current.Name)}'");

            // --- Timezone-Verifikation (beobachtet vs erwartet UTC) ---
            var fin = FolderStreams(dir);
            fin.TryGetValue("ticks.ndjson", out var fv);
            string span = fv?.FirstMt != null ? $"{fv.FirstMt:yyyy-MM-dd HH:mm:ss}->{fv.LastMt:HH:mm:ss}Z" : "-";
            W($"[marktzeit] beobachtet (UTC) {span}  | erwartet {win.UtcStart:HH:mm}->{win.UtcEnd:HH:mm}Z  reachedEnd={reachedEnd}");
            if (fv?.FirstMt is { } fm)
            {
                double devMin = Math.Abs((fm.ToUniversalTime() - win.UtcStart).TotalMinutes);
                W($"[tz-check] Abweichung erster Tick vs erwartetem UTC-Start: {devMin:F1} min");
                if (devMin > 15) { W("BLOCKER: Marktzeit weicht grob vom erwarteten UTC-Fenster ab (Zeitzone?) -> ABBRUCH."); results.Add($"F{wi + 1}: ABBRUCH (Zeitzone/Zuordnung: {span})"); break; }
            }
            else { W("WARNUNG: keine Ticks aufgezeichnet in diesem Fenster."); }
            results.Add($"F{wi + 1} {win.Label}: Ordner={folder}  Marktzeit={span}  ticks={fv?.Bytes ?? 0}B");
            folderPrev = folder;
        }

        // --- finaler Dispose-Load, um das letzte Fenster zu finalisieren (summary.json) ---
        if (folderPrev != null)
        {
            W($"\n[finalisiere letztes Fenster {folderPrev} via Dispose-Load] …");
            var dlg = FindByAutomationId(rw, "ReplayDateSelectionDialog");
            if (dlg == null) { var d2 = FindButtonByName(rw, "Datum auswählen"); if (d2 != null) { Invoke(d2); Thread.Sleep(1600); } dlg = FindByAutomationId(rw, "ReplayDateSelectionDialog"); }
            if (dlg != null)
            {
                for (int a = 0; a < 4; a++) { TypeIntoField(FindByAutomationId(dlg, "EndEdit"), wins[0].DialogEnd); if (ValueOf(FindByAutomationId(dlg, "EndEdit")).StartsWith(wins[0].DialogEnd.Substring(0,19))) break; }
                for (int a = 0; a < 4; a++) { TypeIntoField(FindByAutomationId(dlg, "StartEdit"), wins[0].DialogStart); if (ValueOf(FindByAutomationId(dlg, "StartEdit")).StartsWith(wins[0].DialogStart.Substring(0,19))) break; }
                var sel = FindButtonByName(dlg, "Auswählen") ?? FindButtonByName(rw, "Auswählen");
                if (sel != null) { Invoke(sel); for (int i = 0; i < 30; i++) { Thread.Sleep(1000); if (File.Exists(Path.Combine(RecordingsDir, folderPrev, "summary.json"))) break; } }
            }
            bool sj = File.Exists(Path.Combine(RecordingsDir, folderPrev, "summary.json"));
            W($"[finalize] {folderPrev} summary.json={(sj ? "JA" : "NEIN")}");
        }

        W("\n==================== ERGEBNIS ====================");
        foreach (var r in results) W("  " + r);
        Flush(sb, outFile);
        return results.Count == wins.Length && results.All(r => !r.Contains("ABBRUCH") && !r.Contains("NICHT VERFUEGBAR")) ? 0 : 8;
    }

    private static int DumpDate(string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }
        var rw = ReplayWindow();
        if (rw == null) { W("Replay-Fenster nicht offen."); Flush(sb, outFile); return 4; }
        var dlg = FindByAutomationId(rw, "ReplayDateSelectionDialog");
        if (dlg == null) { var d = FindButtonByName(rw, "Datum auswählen"); if (d != null) { Invoke(d); Thread.Sleep(1500); } dlg = FindByAutomationId(rw, "ReplayDateSelectionDialog"); }
        if (dlg == null) { W("ReplayDateSelectionDialog nicht gefunden."); Flush(sb, outFile); return 4; }
        W($"# DumpDate  {DateTime.Now:HH:mm:ss}  '{Safe(() => dlg.Current.Name)}' rect={Rect(dlg)}");
        int n = 0; DumpDialog(dlg, 0, W, ref n);
        W($"[knoten={n}]");
        Flush(sb, outFile);
        return 0;
    }

    // Maskiertes Datetime-Feld (dd.MM.yyyy HH:mm:ss): fokussieren -> in Feldmitte klicken -> Home ->
    // NUR Ziffern tippen (die Maske setzt '.'/':'/Leer selbst) -> Tab (Commit). Keine Trennzeichen, kein Strg+A.
    private static bool TypeIntoField(AutomationElement? el, string value, Action<string>? step = null, bool clear = true)
    {
        if (el == null) return false;
        // Editierbare Maske ist 'dd.MM.yyyy HH:mm' = 12 Ziffern (KEINE Sekunden). Nur diese tippen (Sekunden-00 wuerden ueberlaufen).
        string digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length > 12) digits = digits.Substring(0, 12);
        try
        {
            ForceForeground(el); Thread.Sleep(120); // ATAS-Dialog sicher nach vorne (sonst trifft der Klick ein ueberlappendes Fenster)
            System.Windows.Rect r; try { r = el.Current.BoundingRectangle; } catch { return false; }
            if (r.IsEmpty) return false;
            ClickScreen((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2)); Thread.Sleep(280); step?.Invoke("Klick");
            if (clear)
            {
                KeyVk(0x41, ctrl: true);   // Strg+A (alles markieren -> Ersetzmodus)
                Thread.Sleep(90); step?.Invoke("Strg+A");
                KeyVk(0x2E);               // Entf (Feld leeren -> Maske rebaut sauber)
                Thread.Sleep(90); step?.Invoke("Entf");
            }
            KeyVk(0x24);               // Home -> an Maskenanfang
            Thread.Sleep(120); step?.Invoke("Pos1");
            // In 2er-Gruppen tippen (dd|MM|yyyy|HH|mm|ss) mit Pause an Trenner-Uebergaengen -> Maske verschluckt keine Ziffer.
            int[] grp = { 2, 2, 4, 2, 2 }; int idx = 0;   // dd|MM|yyyy|HH|mm (12 Ziffern)
            foreach (var g in grp)
            {
                for (int k = 0; k < g && idx < digits.Length; k++, idx++) { SendUnicode(digits[idx]); Thread.Sleep(90); step?.Invoke($"Ziffer {idx + 1} '{digits[idx]}'"); }
                Thread.Sleep(220); // Trenner-Uebergang settlen lassen
            }
            Thread.Sleep(220);
            PressTab();                // Commit/Validierung
            Thread.Sleep(220); step?.Invoke("Tab (Feld verlassen)");
            return true;
        }
        catch { return false; }
    }

    // Setzt Start/Ende im Datumsdialog PROGRAMMATISCH (ValuePattern.SetValue) und liest zurueck. Optional --load.
    //   setdate "<dd.MM.yyyy HH:mm:ss>" "<dd.MM.yyyy HH:mm:ss>" [--load] [out.txt]
    private static int SetDate(string[] args)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }
        string? outFile = args.FirstOrDefault(a => a.EndsWith(".txt"));
        bool load = args.Contains("--load");
        var dt = args.Skip(1).Where(a => Regex.IsMatch(a, @"^\d{2}\.\d{2}\.\d{4} \d{2}:\d{2}")).ToList();
        if (dt.Count < 2) { W("setdate \"dd.MM.yyyy HH:mm:ss\" \"dd.MM.yyyy HH:mm:ss\" [--load] [out.txt]"); Flush(sb, outFile); return 2; }
        string wantStart = dt[0], wantEnd = dt[1];
        W($"# ATAS SetDate  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  Start='{wantStart}' Ende='{wantEnd}' load={load}");

        var rw = ReplayWindow();
        if (rw == null) { W("BLOCKER: Replay-Fenster nicht offen (zuerst open-replay)."); Flush(sb, outFile); return 4; }
        var dlg = FindByAutomationId(rw, "ReplayDateSelectionDialog");
        if (dlg == null)
        {
            var datum = FindButtonByName(rw, "Datum auswählen");
            if (datum == null) { W("BLOCKER: 'Datum auswählen' nicht gefunden."); Flush(sb, outFile); return 4; }
            Invoke(datum); Thread.Sleep(1600);
            dlg = FindByAutomationId(rw, "ReplayDateSelectionDialog");
        }
        if (dlg == null) { W("BLOCKER: ReplayDateSelectionDialog nicht gefunden."); Flush(sb, outFile); return 4; }

        try { var tl = TopLevelOf(dlg); if (tl != null) Foreground(tl); } catch { }
        W($"[rect] StartEdit={Rect(FindByAutomationId(dlg, "StartEdit"))}  EndEdit={Rect(FindByAutomationId(dlg, "EndEdit"))}");
        W($"[vorher]  Start='{ValueOf(FindByAutomationId(dlg, "StartEdit"))}'  Ende='{ValueOf(FindByAutomationId(dlg, "EndEdit"))}'");
        // Der Dialog verwirft Start>Ende; zudem 'committed' nur das zuletzt gesetzte Feld. Reihenfolge Ende->Start,
        // Start zuletzt; bis zu 3 Durchgaenge mit frisch gefetchten Elementen + Rueckleseverifikation je Feld.
        // Pro Feld einzeln setzen + verifizieren (Ende zuerst, damit Start<=Ende gilt). Je bis zu 4 Versuche.
        bool SetField(string id, string want)
        {
            for (int a = 0; a < 4; a++)
            {
                TypeIntoField(FindByAutomationId(dlg, id), want);
                string got = ValueOf(FindByAutomationId(dlg, id));
                W($"    [{id} v{a + 1}] '{got}'");
                if (got.StartsWith(want.Substring(0, 19), StringComparison.Ordinal)) return true;
            }
            return false;
        }
        bool okEnd = SetField("EndEdit", wantEnd);
        bool okStart = SetField("StartEdit", wantStart);
        string gotStart = ValueOf(FindByAutomationId(dlg, "StartEdit")), gotEnd = ValueOf(FindByAutomationId(dlg, "EndEdit"));
        W($"[readback] Start='{gotStart}'  Ende='{gotEnd}'  (okStart={okStart} okEnd={okEnd})");
        bool match = okStart && okEnd;
        W($"[vergleich] gesetzt==zurueckgelesen: {(match ? "JA" : "NEIN")}");
        if (!match) { W("BLOCKER: Datum liess sich nicht zuverlaessig setzen (SetValue nicht uebernommen)."); Flush(sb, outFile); return 6; }

        if (!load) { W("\n[ok] Datum gesetzt+zurueckgelesen. (ohne --load nicht geladen.)"); Flush(sb, outFile); return 0; }

        var before = Directory.Exists(RecordingsDir) ? Directory.GetDirectories(RecordingsDir).Select(Path.GetFileName).Where(x => x != null).ToHashSet()! : new HashSet<string>();
        var select = FindButtonByName(dlg, "Auswählen") ?? FindButtonByName(rw, "Auswählen") ?? FindButtonByName(dlg, "Bestätigen");
        if (select == null) { W("BLOCKER: 'Auswählen' nicht gefunden."); Flush(sb, outFile); return 5; }
        W($"[load] Invoke '{Safe(() => select.Current.Name)}' …"); Invoke(select);
        var play = FindByAutomationId(rw, "PlayButton");
        bool ready = false; string lastLog = "";
        for (int i = 0; i < 90 && !ready; i++)
        {
            Thread.Sleep(1000);
            bool pe; try { pe = play != null && play.Current.IsEnabled; } catch { pe = false; }
            string status = ReplayStatusText(rw), title = Safe(() => rw.Current.Name), lg = LatestReplayState();
            if (lg != lastLog) { lastLog = lg; W($"  [t+{i + 1}s] PlayEnabled={pe} Titel='{title}' | LOG: {lg}"); }
            else if (i % 10 == 0 || pe) W($"  [t+{i + 1}s] PlayEnabled={pe} Status='{Trunc(status, 40)}' Titel='{title}'");
            if (pe && !status.Contains("Datum", StringComparison.OrdinalIgnoreCase)) ready = true;
        }
        W(ready ? "[bereit] JA (Play aktiv)." : "[bereit] NEIN innerhalb 90s -> evtl. keine Daten fuer diesen Abschnitt (Verfuegbarkeit?).");
        W("[log] letzte Replay-Zustaende:"); foreach (var l in TailReplayLog(6)) W("   " + l);
        var after = Directory.Exists(RecordingsDir) ? Directory.GetDirectories(RecordingsDir).Select(Path.GetFileName).Where(x => x != null).ToHashSet()! : new HashSet<string>();
        var created = after.Except(before).ToList();
        W($"[ordner] Neue Ordner: {created.Count} [{string.Join(", ", created)}]");
        Flush(sb, outFile);
        return ready ? 0 : 8;
    }

    private static AutomationElement? ReplayWindow()
    {
        var pids = Process.GetProcessesByName("OFT.Platform").Select(p => p.Id).ToHashSet();
        foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
        {
            try { if (pids.Contains(w.Current.ProcessId) && (w.Current.Name ?? "").StartsWith("Replay", StringComparison.OrdinalIgnoreCase)) return w; }
            catch { }
        }
        return null;
    }

    // ======== Robuster Replay-Transport-Finder (angedockt / schwebend / verschoben), fensteruebergreifend ========
    // Erkennt den Transport an seinem CONTROL-CLUSTER (PlayButton + Start-Auswahl 'Datum/Bar/Zeitraum auswählen'),
    // NICHT an einem separaten 'Replay'-Fenstertitel. Durchsucht ALLE Top-Level-Fenster des ATAS-Prozesses.
    // Bevorzugt sichtbare, bedienbare Controls; leere/unsichtbare Fenster werden ignoriert (auch wenn ihr Titel
    // 'Replay' enthaelt). Bei mehreren bedienbaren Treffern -> Diagnose statt blindem Klick (gibt null + diag).
    internal sealed class ReplayTransport
    {
        public AutomationElement Host = null!;
        public string HostName = "";
        public AutomationElement? ModuleBtn;    // 'Modul auswählen'
        public AutomationElement? ModuleMenu;   // aid ModuleMenuButton
        public AutomationElement? StartBtn;     // 'Datum/Bar/Zeitraum auswählen'
        public string StartMode = "?";          // Datum | Bar | Zeitraum | ?
        public AutomationElement? ModeMenu;     // aid ModeMenuButton
        public AutomationElement? PlayBtn;      // aid PlayButton
        public AutomationElement? SpeedBtn;     // 'NNx'
        public AutomationElement? SettingsBtn;  // aid SettingsButton
        public AutomationElement? ToRealtimeBtn;// HelpText 'Zur Echtzeit wechseln'
        public System.Windows.Rect Bounds;
    }

    private static System.Windows.Rect SafeRect(AutomationElement? e)
    {
        if (e == null) return System.Windows.Rect.Empty;
        try { return e.Current.BoundingRectangle; } catch { return System.Windows.Rect.Empty; }
    }

    private static bool ElVisibleOperable(AutomationElement? e)
    {
        if (e == null) return false;
        try { var r = e.Current.BoundingRectangle; if (r.IsEmpty || r.Width < 1 || r.Height < 1) return false; } catch { return false; }
        try { if (e.Current.IsOffscreen) return false; } catch { }
        return true;
    }

    private static string StartModeOf(string? name)
    {
        var n = name ?? "";
        if (n.IndexOf("Datum", StringComparison.OrdinalIgnoreCase) >= 0) return "Datum";
        if (n.IndexOf("Bar", StringComparison.OrdinalIgnoreCase) >= 0) return "Bar";
        if (n.IndexOf("Zeitraum", StringComparison.OrdinalIgnoreCase) >= 0) return "Zeitraum";
        return "?";
    }

    private static System.Windows.Rect UnionRect(IEnumerable<AutomationElement?> els)
    {
        var acc = System.Windows.Rect.Empty;
        foreach (var e in els) { if (e == null) continue; try { var r = e.Current.BoundingRectangle; if (!r.IsEmpty) acc = acc.IsEmpty ? r : System.Windows.Rect.Union(acc, r); } catch { } }
        return acc;
    }

    private static AutomationElement? FindBtnByHelpIn(AutomationElement root, string help)
    {
        try { foreach (AutomationElement b in root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
            if (string.Equals(Safe(() => b.Current.HelpText), help, StringComparison.OrdinalIgnoreCase)) return b; } catch { }
        return null;
    }

    private static AutomationElement? FindSpeedIn(AutomationElement root)
    {
        var rx = new Regex(@"^\s*\d+(\.\d+)?x\s*$", RegexOptions.IgnoreCase);
        try { foreach (AutomationElement b in root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
            if (rx.IsMatch(Safe(() => b.Current.Name) ?? "")) return b; } catch { }
        return null;
    }

    /// <summary>Findet genau EINEN bedienbaren Replay-Transport. Bei 0/Mehrdeutigkeit -> null + Diagnose in diag.</summary>
    private static ReplayTransport? FindReplayTransport(out string diag)
    {
        diag = "";
        var hits = new List<ReplayTransport>();
        var notes = new List<string>();
        foreach (var root in AtasRoots())
        {
            AutomationElement? play = null;
            try { play = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "PlayButton")); } catch { }
            AutomationElement? startBtn = null; string startName = "";
            foreach (var nm in new[] { "Datum auswählen", "Bar auswählen", "Zeitraum auswählen" })
            {
                AutomationElement? b = null;
                try { b = root.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button), new PropertyCondition(AutomationElement.NameProperty, nm))); } catch { }
                if (b != null) { startBtn = b; startName = nm; break; }
            }
            if (play == null && startBtn == null) continue;   // dieses Fenster enthaelt keinen Transport-Cluster
            string hn = Safe(() => root.Current.Name) ?? "";
            if (!(ElVisibleOperable(play) || ElVisibleOperable(startBtn)))
            { notes.Add($"'{hn}': Transport-Controls vorhanden, aber unsichtbar/leer -> ignoriert"); continue; }
            var t = new ReplayTransport { Host = root, HostName = hn, PlayBtn = play, StartBtn = startBtn, StartMode = StartModeOf(startName) };
            try { t.ModuleBtn = root.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button), new PropertyCondition(AutomationElement.NameProperty, "Modul auswählen"))); } catch { }
            try { t.ModuleMenu = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "ModuleMenuButton")); } catch { }
            try { t.ModeMenu = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "ModeMenuButton")); } catch { }
            try { t.SettingsBtn = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "SettingsButton")); } catch { }
            t.SpeedBtn = FindSpeedIn(root);
            t.ToRealtimeBtn = FindBtnByHelpIn(root, "Zur Echtzeit wechseln");
            t.Bounds = UnionRect(new[] { t.ModuleBtn, t.StartBtn, t.PlayBtn, t.SpeedBtn, t.SettingsBtn });
            hits.Add(t);
        }
        if (hits.Count == 0) { diag = "kein bedienbarer Transport. " + string.Join(" | ", notes); return null; }
        if (hits.Count > 1)
        { diag = "MEHRDEUTIG (" + hits.Count + " bedienbare Treffer): " + string.Join(" ; ", hits.Select(h => $"'{h.HostName}' mode={h.StartMode} @{(int)h.Bounds.X},{(int)h.Bounds.Y} {(int)h.Bounds.Width}x{(int)h.Bounds.Height}")); return null; }
        if (notes.Count > 0) diag = string.Join(" | ", notes);
        return hits[0];
    }

    private static bool ReplayTransportPresent() { return FindReplayTransport(out _) != null; }

    // Diagnose: Transport VISUELL (Screenshot + Windows-OCR) erkennen und protokollieren, OHNE Klick.
    private static int OcrFind(string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); if (outFile != null) { try { File.WriteAllText(outFile, sb.ToString()); } catch { } } }
        W($"# OcrFind  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        try
        {
            var bmp = ScreenOcr.CapturePrimary(out int ox, out int oy);
            List<OcrWord> words;
            using (bmp) words = ScreenOcr.Recognize(bmp, ox, oy);
            W($"# OCR-Woerter gesamt: {words.Count}");
            var rel = words.Where(x => x.Text.IndexOf("ausw", StringComparison.OrdinalIgnoreCase) >= 0
                || x.Text.IndexOf("Modul", StringComparison.OrdinalIgnoreCase) >= 0
                || x.Text.IndexOf("Datum", StringComparison.OrdinalIgnoreCase) >= 0
                || x.Text.IndexOf("Bar", StringComparison.OrdinalIgnoreCase) >= 0
                || x.Text.IndexOf("Zeitraum", StringComparison.OrdinalIgnoreCase) >= 0
                || System.Text.RegularExpressions.Regex.IsMatch(x.Text, @"^\d+(\.\d+)?x$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToList();
            W("# Transport-relevante Woerter:");
            foreach (var r in rel) W("   " + r);
            var t = ScreenOcr.DetectTransport(words, out var diag);
            if (t == null) { W("\nVISUELL: Transport NICHT eindeutig erkannt."); W("  Diagnose: " + diag); Flush(sb, outFile); return 8; }
            W($"\nVISUELL ERKANNT: Start-Modus={t.StartMode}");
            W($"  Leisten-Bereich ~ {(int)t.BarBounds.X},{(int)t.BarBounds.Y} {(int)t.BarBounds.Width}x{(int)t.BarBounds.Height} (y-Mitte {t.BarY})");
            W($"  Modul   : {(t.ModulWord?.ToString() ?? "-")}");
            W($"  Start   : {t.StartWord} -> Klick {t.StartClick.x},{t.StartClick.y}");
            W($"  Speed   : {(t.SpeedWord?.ToString() ?? "-")}" + (t.SpeedWord != null ? $" -> Klick {t.SpeedClick.x},{t.SpeedClick.y}" : ""));
            W($"  Play    : {(t.PlayClick.HasValue ? t.PlayClick.Value.x + "," + t.PlayClick.Value.y + " (aus Layout abgeleitet)" : "- (kein Speed-Anker)")}");
            W($"  ZurEchtzeit: {(t.ToRealtimeClick.HasValue ? t.ToRealtimeClick.Value.x + "," + t.ToRealtimeClick.Value.y + " (abgeleitet)" : "-")}");
            W("\nErgebnis: Transport visuell eindeutig erkannt (keine Aktion ausgefuehrt).");
            Flush(sb, outFile);
            return 0;
        }
        catch (Exception ex) { W("OCR-FEHLER: " + ex.GetType().Name + ": " + ex.Message); Flush(sb, outFile); return 2; }
    }

    // ===================== VISUELLER FALLBACK (OCR + Klick), wenn Transport-Controls NICHT ueber UIA erreichbar =====================
    // ATAS rendert die Replay-Transportleiste in bestimmten Sitzungszustaenden vollstaendig selbst (kein UIA-Peer:
    // RawView 0 Buttons; live belegt). Erkennung dann per Screenshot+Windows-OCR (ScreenOcr.DetectTransport). NUR zwei
    // Aktionen brauchen einen visuellen Klick: 'Datum auswählen' (oeffnet den UIA-Datum-Dialog) und 'Play'. Alles andere
    // bleibt UIA (Datum-Dialog StartEdit/EndEdit/Auswählen, Recorder-Dialog), UIA-LESBAR (Fenstertitel = Spielzustand)
    // oder laeuft ueber die Ribbon-Umschaltung (Replay verlassen). Positionen werden VOR JEDEM Klick FRISCH per OCR
    // aufgeloest (keine gespeicherten Absolutkoordinaten); Klickpunkte relativ zu OCR-erkannten Beschriftungen. Nach
    // jedem Klick wird die Wirkung UNABHAENGIG verifiziert (StartEdit-UIA bzw. Fenstertitel). DPI: physische Pixel.

    // Transport frisch per OCR erkennen (ATAS zuvor in den Vordergrund holen, damit die angedockte Leiste sichtbar ist).
    private static VisualTransport? OcrDetectFresh(out string diag)
    {
        diag = "";
        RaiseReplayFloat();   // das schwebende Transport-Float nach vorn (NICHT das Hauptfenster, das es sonst verdeckt)
        try
        {
            var bmp = ScreenOcr.CapturePrimary(out int ox, out int oy);
            List<OcrWord> words; using (bmp) words = ScreenOcr.Recognize(bmp, ox, oy);
            return ScreenOcr.DetectTransport(words, out diag);
        }
        catch (Exception ex) { diag = "OCR-Fehler: " + ex.GetType().Name + ": " + ex.Message; return null; }
    }

    // Klappt eine EINGEKLAPPTE Replay-Transportleiste wieder auf. Im eingeklappten Zustand bleibt nur ein kleiner
    // Chevron '^' am Chart (oben links, direkt UNTER dem Recorder-Label 'OF Replay Recorder …') uebrig; ein Klick
    // darauf blendet das 'Replay'-Transportfenster wieder ein. Der Klickpunkt wird per OCR an diesem Label VERANKERT
    // (kein fester Bildschirmpunkt): Chevron = links unter dem Wortanfang 'OF'. Wirkung wird vom Aufrufer unabhaengig
    // an der Fensterpraesenz (ReplayWindow() != null) geprueft.
    private static bool TryExpandCollapsedTransport(Action<string> W)
    {
        try
        {
            try { var m = MainWin(); if (m != null) { Foreground(m); Thread.Sleep(300); } } catch { }   // Chart muss sichtbar sein, sonst sieht OCR das Label nicht
            var bmp = ScreenOcr.CapturePrimary(out int ox, out int oy);
            List<OcrWord> words; using (bmp) words = ScreenOcr.Recognize(bmp, ox, oy);
            var rec = words.FirstOrDefault(w => w.Text.IndexOf("Recorder", StringComparison.OrdinalIgnoreCase) >= 0);
            if (rec == null) { W("  [expand] Recorder-Label nicht per OCR gefunden -> kein Chevron-Anker (Ribbon-Toggle bleibt primaer)."); return false; }
            // Linken Anker der Label-Zeile bestimmen (Wort 'OF' davor, sonst das 'Recorder'-Wort selbst).
            var left = words.Where(w => Math.Abs(w.CY - rec.CY) <= 8).OrderBy(w => w.X).FirstOrDefault() ?? rec;
            int cx = left.X + 17, cy = rec.Y + 34;   // Chevron sitzt knapp unter dem Label, am linken Rand
            W($"  [expand] Einklapp-Chevron (unter '{left.Text}…') @{cx},{cy} klicken");
            ClickMain(cx, cy);
            return true;
        }
        catch (Exception ex) { W("  [expand] Fehler: " + ex.Message); return false; }
    }

    // Statuszeile unter der Leiste per OCR: 'Play' => ladebereit ('Drücken Sie Play…'); 'Datum' => noch nicht geladen
    // ('Drücken Sie Datum auswählen…'). Ersetzt die UIA-Abfrage PlayButton.IsEnabled im custom-gerenderten Zustand.
    // Band relativ zur erkannten Leisten-y-Mitte (barY+12..+46).
    private static bool OcrReadyToPlay(int barY, out string statusWord)
    {
        statusWord = "?";
        RaiseReplayFloat();   // Statuszeile liegt am Float -> Float nach vorn, sonst verdeckt das Hauptfenster sie
        try
        {
            var bmp = ScreenOcr.CapturePrimary(out int ox, out int oy);
            List<OcrWord> words; using (bmp) words = ScreenOcr.Recognize(bmp, ox, oy);
            var band = words.Where(w => w.CY >= barY + 12 && w.CY <= barY + 46).ToList();
            bool play = band.Any(w => w.Text.IndexOf("Play", StringComparison.OrdinalIgnoreCase) >= 0);
            bool datum = band.Any(w => w.Text.IndexOf("Datum", StringComparison.OrdinalIgnoreCase) >= 0);
            statusWord = play ? "Play" : (datum ? "Datum" : "?");
            return play;
        }
        catch { return false; }
    }

    // Spielt das Replay gerade? UIA-LESBAR am Fenstertitel (die Buttons sind es nicht): Transport 'Historienwiedergabe…'
    // (nicht 'gestoppt'/'angehalten') ODER Hauptfenster 'Wiedergabe gestartet'.
    private static bool ReplayPlayingByTitle()
    {
        string t = Safe(() => ReplayWindow()?.Current.Name ?? "");
        string m = Safe(() => MainWin()?.Current.Name ?? "");
        bool byReplay = t.IndexOf("Historienwiedergabe", StringComparison.OrdinalIgnoreCase) >= 0
            && t.IndexOf("gestoppt", StringComparison.OrdinalIgnoreCase) < 0
            && t.IndexOf("angehalten", StringComparison.OrdinalIgnoreCase) < 0;
        bool byMain = m.IndexOf("Wiedergabe gestartet", StringComparison.OrdinalIgnoreCase) >= 0;
        return byReplay || byMain;
    }

    // Spielt ODER laedt das Replay gerade (Titel 'Module laden...' ist der Uebergang direkt nach Play). Dient als
    // robuster 'Play-ist-angelaufen'-Nachweis, der auch den kurzen Lade-Transienten erfasst.
    private static bool ReplayPlayingOrLoadingByTitle()
    {
        string t = Safe(() => ReplayWindow()?.Current.Name ?? "");
        if (t.IndexOf("laden", StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return ReplayPlayingByTitle();
    }

    // Startet die Wiedergabe per PHYSISCHEM Klick auf die per UIA ermittelte PlayButton-Position.
    // Grund (live belegt): UIA-Invoke(PlayButton) loest die Wiedergabe in diesem ATAS-Zustand NICHT aus
    // (Titel bleibt 'gestoppt', kein Log); ein physischer Klick auf dieselbe Stelle startet sie zuverlaessig
    // (Titel -> 'Module laden' -> 'Historienwiedergabe'). KEINE festen Koordinaten: Punkt = aktuelles UIA-Rect.
    // Das Transport-Fenster wird vorher fokussiert (schwebende Float braucht Vordergrund, sonst trifft der Klick dahinter).
    private static bool PlayByPhysicalClick(Action<string> W)
    {
        var pb = FindIdAny("PlayButton");
        if (!IsEnabledSafe(pb)) { W("  [play] PlayButton nicht bereit (nicht aktiv)."); return false; }
        System.Windows.Rect r; try { r = pb!.Current.BoundingRectangle; } catch { W("  [play] PlayButton ohne Rect."); return false; }
        if (r.Width <= 0 || double.IsInfinity(r.X)) { W("  [play] PlayButton-Rect ungueltig."); return false; }
        int cx = (int)(r.X + r.Width / 2), cy = (int)(r.Y + r.Height / 2);
        var rw = ReplayWindow(); if (rw != null) { try { Foreground(rw); } catch { } }
        Thread.Sleep(350);
        SetCursorPos(cx, cy); Thread.Sleep(150);
        ClickScreen(cx, cy);
        Thread.Sleep(400);
        return true;
    }

    // "Transport offen?" layout-robust: Cluster-Finder ODER (altes) Replay-Titelfenster. So wird ein ANGEDOCKTER
    // Transport (kein separates 'Replay'-Fenster) korrekt als offen erkannt -> kein faelschliches Toggeln/Schliessen.
    private static bool ReplayOpen() { return ReplayTransportPresent() || ReplayWindow() != null; }

    // Diagnose-Kommando: aktuellen Transport ERKENNEN und protokollieren, OHNE Aufnahme/Umschalten.
    private static int ReplayFind(string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); if (outFile != null) { try { File.WriteAllText(outFile, sb.ToString()); } catch { } } }
        W($"# ReplayFind  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        var pids = Process.GetProcessesByName("OFT.Platform").Select(p => p.Id).ToHashSet();
        W($"# ATAS-Prozesse: {pids.Count}  Top-Level-Fenster:");
        foreach (var w in AtasRoots()) { var r = SafeRect(w); W($"   '{Safe(() => w.Current.Name)}' [{Safe(() => w.Current.ClassName)}] {(int)r.X},{(int)r.Y} {(int)r.Width}x{(int)r.Height}"); }
        var t = FindReplayTransport(out var diag);
        if (t == null) { W($"\nTransport: NICHT eindeutig erkannt.\n  Diagnose: {diag}"); Flush(sb, outFile); return 8; }
        W($"\nTransport ERKANNT in Fenster: '{t.HostName}'");
        W($"  Cluster-Bereich: {(int)t.Bounds.X},{(int)t.Bounds.Y}  {(int)t.Bounds.Width}x{(int)t.Bounds.Height}");
        W($"  Start-Modus: {t.StartMode}  (Start-Button Name='{Safe(() => t.StartBtn?.Current.Name)}')");
        void L(string n, AutomationElement? e) { if (e == null) { W($"   - {n,-16}: fehlt"); return; } var r = SafeRect(e); W($"   - {n,-16}: '{Safe(() => e.Current.Name)}' aid='{Safe(() => e.Current.AutomationId)}' enabled={IsEnabledSafe(e)} sichtbar={ElVisibleOperable(e)} @{(int)r.X},{(int)r.Y}"); }
        L("Modul auswählen", t.ModuleBtn); L("ModuleMenu", t.ModuleMenu); L("Start-Auswahl", t.StartBtn); L("ModeMenu", t.ModeMenu);
        L("Play", t.PlayBtn); L("Speed", t.SpeedBtn); L("Settings", t.SettingsBtn); L("ZurEchtzeit", t.ToRealtimeBtn);
        if (diag != "") W($"  Hinweise: {diag}");
        W("\nErgebnis: Transport eindeutig bedienbar erkannt (keine Aufnahme gestartet).");
        Flush(sb, outFile);
        return 0;
    }

    // Schaltet den Start-Auswahl-Modus des Transports gezielt auf 'Datum' (aus 'Bar'/'Zeitraum') ueber den
    // ModeMenuButton. Controls werden nach jedem Schritt FRISCH aufgeloest; Erfolg wird am Start-Button-Namen
    // bestaetigt. UIA-Invoke/ExpandCollapse bevorzugt; kein blindes Klicken bei Mehrdeutigkeit.
    private static bool SwitchToDatumMode(Action<string> W)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            var t = FindReplayTransport(out var d);
            if (t == null) { W("  [mode] Transport nicht eindeutig aufloesbar: " + d); return false; }
            if (t.StartMode == "Datum") { W("  [mode] bereits 'Datum auswählen'."); return true; }
            if (t.ModeMenu == null) { W("  [mode] ModeMenuButton nicht gefunden -> kein gezielter Moduswechsel moeglich."); return false; }
            W($"  [mode] oeffne Modus-Menue (aktuell '{t.StartMode}') …");
            bool opened = false;
            try { if (t.ModeMenu.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ep)) { ((ExpandCollapsePattern)ep).Expand(); opened = true; } } catch { }
            if (!opened) { try { if (t.ModeMenu.TryGetCurrentPattern(InvokePattern.Pattern, out var ip)) { ((InvokePattern)ip).Invoke(); opened = true; } } catch { } }
            Thread.Sleep(700);
            var item = FindMenuItemContains("Datum");
            if (item == null) { W("  [mode] kein sichtbarer 'Datum'-Menueeintrag -> Abbruch des Moduswechsels (keine blinde Aktion)."); return false; }
            W($"  [mode] waehle Menueeintrag '{Safe(() => item.Current.Name)}'");
            try { if (item.TryGetCurrentPattern(InvokePattern.Pattern, out var ii)) ((InvokePattern)ii).Invoke(); else ClickElement(item); } catch { ClickElement(item); }
            Thread.Sleep(900);
            var t2 = FindReplayTransport(out _);
            if (t2 != null && t2.StartMode == "Datum") { W("  [mode] bestaetigt: Start-Modus ist jetzt 'Datum auswählen'."); return true; }
            W($"  [mode] noch nicht 'Datum' (jetzt '{t2?.StartMode}') -> erneuter Versuch");
        }
        return false;
    }

    // Sichtbaren Menueeintrag finden, dessen Name den Teilstring enthaelt (MenuItem; Fallback ListItem/Button mit 'auswählen').
    private static AutomationElement? FindMenuItemContains(string sub)
    {
        var pids = Process.GetProcessesByName("OFT.Platform").Select(p => p.Id).ToHashSet();
        var roots = TopWindows(pids);
        foreach (var root in roots)
        {
            try { foreach (AutomationElement it in root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem)))
                if ((Safe(() => it.Current.Name) ?? "").IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0 && ElVisibleOperable(it)) return it; } catch { }
        }
        foreach (var root in roots)
            foreach (var ct in new[] { ControlType.ListItem, ControlType.Button })
                try { foreach (AutomationElement it in root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ct)))
                { var nm = Safe(() => it.Current.Name) ?? ""; if (nm.IndexOf(sub, StringComparison.OrdinalIgnoreCase) >= 0 && nm.IndexOf("auswählen", StringComparison.OrdinalIgnoreCase) >= 0 && ElVisibleOperable(it)) return it; } } catch { }
        return null;
    }

    // Oeffnet das Dropdown-Menue des Ribbon-'Replay'-Buttons per UIA ExpandCollapse (KEIN Toggle -> kein Moduswechsel),
    // listet die Menueeintraege (z. B. 'Zur Echtzeit wechseln'/'Zurueck zum Hauptfenster') und schliesst wieder.
    private static int ReplayMenu(string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }
        W($"# ReplayMenu {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        var btn = FindIdAny("RibbonReplayButton");
        if (btn == null) { W("RibbonReplayButton nicht gefunden."); Flush(sb, outFile); return 4; }
        string tgl = "?"; try { if (btn.TryGetCurrentPattern(TogglePattern.Pattern, out var tp)) tgl = ((TogglePattern)tp).Current.ToggleState.ToString(); } catch { }
        W($"[vorher] RibbonReplayButton ToggleState={tgl}  pat=[{string.Join(",", SupportedPatterns(btn))}]");

        var pids = Process.GetProcessesByName("OFT.Platform").Select(p => p.Id).ToHashSet();
        var before = TopWindows(pids);
        bool expanded = false;
        try { if (btn.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ep)) { ((ExpandCollapsePattern)ep).Expand(); expanded = true; } }
        catch (Exception ex) { W($"Expand-Fehler: {ex.Message}"); }
        W($"[expand] {(expanded ? "ok" : "fehlgeschlagen")}"); Thread.Sleep(1000);

        var newWins = TopWindows(pids).Where(a => !before.Any(b => SameRuntimeId(a, b))).ToList();
        var roots = new List<AutomationElement>(newWins); roots.Add(btn);
        W($"Neue Popups: {newWins.Count} [{string.Join(", ", newWins.Select(w => $"'{Trunc(Safe(() => w.Current.Name), 24)}'"))}]");
        int count = 0;
        foreach (var ct in new[] { ControlType.MenuItem, ControlType.ListItem, ControlType.Button })
            foreach (var mi in FindInRoots(roots, ct))
            {
                var n = Safe(() => mi.Current.Name);
                if (string.IsNullOrEmpty(n) || n.Length > 60) continue;
                W($"  [{ct.ProgrammaticName.Replace("ControlType.", "")}] '{n}' Id='{Safe(() => mi.Current.AutomationId)}' enabled={IsEnabledSafe(mi)} pat=[{string.Join(",", SupportedPatterns(mi))}] rect={Rect(mi)}");
                count++;
            }
        foreach (var d in newWins) { W($"\n[neues Fenster] '{Safe(() => d.Current.Name)}'"); int n2 = 0; DumpDialog(d, 0, W, ref n2); }
        W($"\n[info] Eintraege={count}.");
        // sauber schliessen OHNE Auswahl
        try { if (btn.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ep2)) ((ExpandCollapsePattern)ep2).Collapse(); } catch { }
        string tgl2 = "?"; try { var b2 = FindIdAny("RibbonReplayButton"); if (b2 != null && b2.TryGetCurrentPattern(TogglePattern.Pattern, out var tp2)) tgl2 = ((TogglePattern)tp2).Current.ToggleState.ToString(); } catch { }
        W($"[nachher] ToggleState={tgl2} (unveraendert erwartet)");
        Flush(sb, outFile);
        return 0;
    }

    // Gezielte Diagnose fuer ExitReplayView + TryVerifiedReplayPositionUtc:
    //  (1) Transport-Fenster komplett (Player-Subtree, evtl. Position/Scrubber/Datetime, Exit/Stop-Buttons)
    //  (2) Header-Reiter 'Chart'/'Replay' mit Patterns + Parent-Kette (ist ein echtes TabItem dahinter?)
    //  (3) explizite RangeValue-Elemente im Transport (Position als Bruch interpretierbar?)
    private static int PlayerProbe(string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }
        W($"# PlayerProbe {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        var rw = ReplayWindow();
        W($"\n== (1) Transport-Fenster == {(rw != null ? $"'{Safe(() => rw.Current.Name)}'" : "NICHT OFFEN")}");
        if (rw != null) { int n = 0; DumpDialog(rw, 0, W, ref n); }

        W("\n== (2) Header-Reiter Chart/Replay ==");
        foreach (var name in new[] { "Chart", "Replay" })
        {
            AutomationElement? t = null;
            foreach (var r in AtasRoots())
            {
                try
                {
                    t = r.FindFirst(TreeScope.Descendants, new AndCondition(
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                        new PropertyCondition(AutomationElement.NameProperty, name)));
                }
                catch { }
                if (t != null) break;
            }
            if (t == null) { W($"  '{name}': nicht gefunden"); continue; }
            W($"  '{name}': enabled={IsEnabledSafe(t)} pat=[{string.Join(",", SupportedPatterns(t))}] rect={Rect(t)}");
            try
            {
                var p = TreeWalker.ControlViewWalker.GetParent(t);
                for (int i = 0; i < 5 && p != null; i++)
                {
                    string ct = p.Current.ControlType?.ProgrammaticName?.Replace("ControlType.", "") ?? "";
                    W($"      parent[{i}] [{ct}] Name='{Trunc(Safe(() => p.Current.Name), 30)}' Id='{Safe(() => p.Current.AutomationId)}' enabled={IsEnabledSafe(p)} pat=[{string.Join(",", SupportedPatterns(p))}]");
                    p = TreeWalker.ControlViewWalker.GetParent(p);
                }
            }
            catch { }
        }

        W("\n== (3) RangeValue-Elemente im Transport ==");
        if (rw != null)
        {
            try
            {
                foreach (AutomationElement e in rw.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                {
                    try
                    {
                        if (e.TryGetCurrentPattern(RangeValuePattern.Pattern, out var rp))
                        {
                            var c = ((RangeValuePattern)rp).Current;
                            string ct = e.Current.ControlType?.ProgrammaticName?.Replace("ControlType.", "") ?? "";
                            W($"  [{ct}] Name='{Trunc(Safe(() => e.Current.Name), 24)}' Id='{Safe(() => e.Current.AutomationId)}' val={c.Value} min={c.Minimum} max={c.Maximum} rect={Rect(e)}");
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
        else W("  (Transport zu)");

        Flush(sb, outFile);
        return 0;
    }

    // Gezielte Diagnose des Replay-Speed-Selektors: oeffnet den '1x'-Button per ExpandCollapse (nicht nur Invoke)
    // und listet NUR innerhalb der ATAS-Prozessfenster alle Menu-/List-/Text-Eintraege, die wie eine Geschwindigkeit
    // aussehen ('Nx', 'xN', reine Zahl) bzw. editierbare Felder. Antwortet die Frage: ist 100x ueberhaupt waehlbar/lesbar?
    private static int SpeedProbe(string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); if (outFile != null) { try { File.WriteAllText(outFile, sb.ToString()); } catch { } } }
        W($"# SpeedProbe {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        var rw = ReplayWindow();
        if (rw == null) { W("BLOCKER: Replay-Transport nicht offen (zuerst open-replay)."); Flush(sb, outFile); return 4; }
        var speed = FindSpeedAny();
        if (speed == null) { W("BLOCKER: Speed-Button (Nx) nicht gefunden."); Flush(sb, outFile); return 4; }
        W($"[speed] Name='{Safe(() => speed.Current.Name)}' rect={Rect(speed)} pat=[{string.Join(",", SupportedPatterns(speed))}]");
        var pids = Process.GetProcessesByName("OFT.Platform").Select(p => p.Id).ToHashSet();
        var before = TopWindows(pids);

        bool opened = false;
        try { if (speed.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ep)) { ((ExpandCollapsePattern)ep).Expand(); opened = true; } } catch (Exception ex) { W($"[expand] Fehler: {ex.Message}"); }
        if (!opened) { try { Invoke(speed); opened = true; W("[open] via Invoke (kein ExpandCollapse)"); } catch { } } else W("[open] via ExpandCollapse.Expand");
        Thread.Sleep(1000);

        var rxSpeed = new Regex(@"^\s*(x\s*)?\d+(\.\d+)?\s*x?\s*$", RegexOptions.IgnoreCase);
        var newWins = TopWindows(pids).Where(a => !before.Any(b => SameRuntimeId(a, b))).ToList();
        W($"[popups] neue ATAS-Fenster: {newWins.Count} [{string.Join(", ", newWins.Select(w => $"'{Trunc(Safe(() => w.Current.Name), 30)}'"))}]");

        // NUR ATAS-Prozessfenster durchsuchen (kein Desktop-Rauschen): Menu/List/Text/Edit mit speed-aehnlichem Namen.
        int hits = 0;
        var roots = new List<AutomationElement>(newWins) { rw };
        foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
        { try { if (pids.Contains(w.Current.ProcessId)) roots.Add(w); } catch { } }
        foreach (var root in roots.Distinct())
            foreach (var ct in new[] { ControlType.MenuItem, ControlType.ListItem, ControlType.Text, ControlType.Edit, ControlType.Button, ControlType.Slider })
            {
                AutomationElementCollection coll;
                try { coll = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ct)); } catch { continue; }
                foreach (AutomationElement mi in coll)
                {
                    string n = Safe(() => mi.Current.Name); string val = "";
                    try { if (mi.TryGetCurrentPattern(ValuePattern.Pattern, out var vp)) val = ((ValuePattern)vp).Current.Value ?? ""; } catch { }
                    try { if (string.IsNullOrEmpty(val) && mi.TryGetCurrentPattern(RangeValuePattern.Pattern, out var rp)) { var r = (RangeValuePattern)rp; val = $"{r.Current.Value} [{r.Current.Minimum}..{r.Current.Maximum}]"; } } catch { }
                    bool speedish = (!string.IsNullOrEmpty(n) && n.Length <= 10 && rxSpeed.IsMatch(n)) || ct == ControlType.Slider || (!string.IsNullOrEmpty(val) && rxSpeed.IsMatch(val));
                    if (speedish) { W($"  [{ct.ProgrammaticName.Replace("ControlType.", "")}] Name='{Trunc(n, 16)}' Val='{Trunc(val, 24)}' Id='{Safe(() => mi.Current.AutomationId)}' enabled={IsEnabledSafe(mi)} pat=[{string.Join(",", SupportedPatterns(mi))}] rect={Rect(mi)}"); hits++; }
                }
            }
        W($"[info] speed-aehnliche Eintraege: {hits}");
        try { if (speed.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ep2)) ((ExpandCollapsePattern)ep2).Collapse(); } catch { }
        Flush(sb, outFile);
        return 0;
    }

    // Toolbar-Buttons inkl. HelpText auflisten + Speed-Menue oeffnen, um '1x' und Stop sicher zu identifizieren.
    private static int Inspect2()
    {
        var rw = ReplayWindow();
        if (rw == null) { Console.WriteLine("Replay-Fenster nicht gefunden."); return 4; }
        Console.WriteLine($"Replay-Fenster: '{Safe(() => rw.Current.Name)}'");
        Console.WriteLine("\n-- Buttons im Replay-Fenster (Name | AutoId | HelpText | rect) --");
        foreach (AutomationElement b in rw.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
        {
            string nm = Safe(() => b.Current.Name), id = Safe(() => b.Current.AutomationId), ht = Safe(() => b.Current.HelpText);
            if (string.IsNullOrEmpty(nm) && string.IsNullOrEmpty(id) && string.IsNullOrEmpty(ht)) { /* icon ohne alles */ }
            Console.WriteLine($"  Name='{Trunc(nm, 28)}' Id='{Trunc(id, 24)}' Help='{Trunc(ht, 40)}'  rect={Rect(b)}  pat=[{string.Join(",", SupportedPatterns(b))}]");
        }

        var speed = FindButtonByNameRegex(rw, new Regex(@"^\s*\d+(\.\d+)?x\s*$", RegexOptions.IgnoreCase));
        Console.WriteLine($"\nSpeed-Button: {(speed != null ? $"'{Safe(() => speed.Current.Name)}'" : "nicht gefunden")}");
        if (speed != null)
        {
            Invoke(speed); Thread.Sleep(800);
            Console.WriteLine("-- Menue-/Listeneintraege nach Speed-Klick --");
            foreach (var ctype in new[] { ControlType.MenuItem, ControlType.ListItem })
                foreach (AutomationElement mi in AutomationElement.RootElement.FindAll(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ctype)))
                {
                    var n = Safe(() => mi.Current.Name);
                    if (!string.IsNullOrEmpty(n) && n.Length < 20)
                        Console.WriteLine($"  [{ctype.ProgrammaticName.Replace("ControlType.", "")}] '{n}' Id='{Safe(() => mi.Current.AutomationId)}'");
                }
            Invoke(speed); // Menue wieder schliessen
        }
        return 0;
    }

    // Oeffnet "Datum auswählen" und inventarisiert den Dialog vollstaendig (Felder/Zeitzone/Instrument/Modus).
    // Reine Inspektion: der Dialog bleibt offen; es wird NICHTS gesetzt/geladen.
    private static int InspectDate(string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }
        W($"# ATAS InspectDate  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        var rw = ReplayWindow();
        if (rw == null) { W("Replay-Fenster nicht gefunden."); return 4; }

        var pids = Process.GetProcessesByName("OFT.Platform").Select(p => p.Id).ToHashSet();
        var before = TopWindows(pids);
        var datum = FindButtonByName(rw, "Datum auswählen");
        if (datum == null) { W("'Datum auswählen' nicht gefunden."); Flush(sb, outFile); return 4; }
        W("[open] Invoke 'Datum auswählen' …");
        Invoke(datum); Thread.Sleep(1800);

        var after = TopWindows(pids);
        var dialogs = after.Where(a => !before.Any(b => SameRuntimeId(a, b))).ToList();
        W($"Neue Top-Level-Fenster: {dialogs.Count}");
        AutomationElement target = dialogs.FirstOrDefault() ?? rw; // Fallback: Inline-Popup im Replay-Fenster
        W($"Inventarisiere: '{Safe(() => target.Current.Name)}'\n");
        int n = 0;
        DumpDialog(target, 0, W, ref n);
        W($"\n[hinweis] Dialog bleibt GEOEFFNET fuer den Bedienschritt. Besuchte Knoten={n}.");
        Flush(sb, outFile);
        return 0;
    }

    // Oeffnet ein Dropdown (per Button-AutomationId) und listet die Eintraege inkl. Auswahlzustand.
    private static int InspectMenu(string buttonId, string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }
        W($"# ATAS InspectMenu '{buttonId}'  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        var rw = ReplayWindow();
        if (rw == null) { W("Replay-Fenster nicht gefunden."); Flush(sb, outFile); return 4; }
        var btn = FindByAutomationId(rw, buttonId) ?? FindButtonByName(rw, buttonId);
        if (btn == null) { W($"Button '{buttonId}' nicht gefunden."); Flush(sb, outFile); return 4; }
        var pids = Process.GetProcessesByName("OFT.Platform").Select(p => p.Id).ToHashSet();
        var before = TopWindows(pids);
        W($"[open] Invoke '{buttonId}' …");
        Invoke(btn); Thread.Sleep(1200);

        // STRIKT ATAS-prozess-skopiert: nur neue ATAS-Popups + das Replay-Fenster durchsuchen (kein Desktop/Chat).
        var newWins = TopWindows(pids).Where(a => !before.Any(b => SameRuntimeId(a, b))).ToList();
        var roots = new List<AutomationElement>(newWins) { rw };
        W($"Neue ATAS-Popups/Fenster: {newWins.Count}  [{string.Join(", ", newWins.Select(w => $"'{Trunc(Safe(() => w.Current.Name), 24)}'"))}]");
        int count = 0;
        foreach (var ct in new[] { ControlType.MenuItem, ControlType.ListItem, ControlType.Button })
            foreach (var mi in FindInRoots(roots, ct))
            {
                var n = Safe(() => mi.Current.Name);
                if (string.IsNullOrEmpty(n) || n.Length > 60) continue;
                string sel = "";
                try { if (mi.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp) && ((SelectionItemPattern)sp).Current.IsSelected) sel = " <SELECTED>"; } catch { }
                try { if (mi.TryGetCurrentPattern(TogglePattern.Pattern, out var tp) && ((TogglePattern)tp).Current.ToggleState == ToggleState.On) sel += " <ON>"; } catch { }
                W($"  [{ct.ProgrammaticName.Replace("ControlType.", "")}] '{n}'{sel} Id='{Safe(() => mi.Current.AutomationId)}' rect={Rect(mi)}");
                count++;
            }
        foreach (var d in newWins)
        {
            W($"\n[neues Fenster] '{Safe(() => d.Current.Name)}' Id='{Safe(() => d.Current.AutomationId)}'");
            int n2 = 0; DumpDialog(d, 0, W, ref n2);
        }
        W($"\n[info] Eintraege={count}, neue Fenster={newWins.Count}. (Menue bleibt ggf. offen.)");
        Flush(sb, outFile);
        return 0;
    }

    private static string ValueOf(AutomationElement? el)
    {
        if (el == null) return "";
        try { if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vp)) return ((ValuePattern)vp).Current.Value ?? ""; } catch { }
        return "";
    }

    // Laedt den (bereits vorausgefuellten) June-23-Abschnitt ueber "Auswählen" und wartet auf echte Bereitschaft.
    // Setzt KEINE Zeiten (keine Zeitzonen-Annahme); nutzt die zurueckgelesene Auswahl. Meldet aktiven Ordner-Kandidaten.
    private static int Load(string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }
        W($"# ATAS Load  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        var rw = ReplayWindow();
        if (rw == null) { W("BLOCKER: Replay-Fenster nicht gefunden."); Flush(sb, outFile); return 4; }

        var dlg = FindByAutomationId(rw, "ReplayDateSelectionDialog");
        if (dlg == null)
        {
            var datum = FindButtonByName(rw, "Datum auswählen");
            if (datum == null) { W("BLOCKER: 'Datum auswählen' nicht gefunden."); Flush(sb, outFile); return 4; }
            Invoke(datum); Thread.Sleep(1800);
            dlg = FindByAutomationId(rw, "ReplayDateSelectionDialog");
        }
        if (dlg == null) { W("BLOCKER: Datumsdialog (ReplayDateSelectionDialog) nicht gefunden."); Flush(sb, outFile); return 4; }

        string sVal = ValueOf(FindByAutomationId(dlg, "StartEdit"));
        string eVal = ValueOf(FindByAutomationId(dlg, "EndEdit"));
        W($"[auswahl-readback] Start='{sVal}'  Ende='{eVal}'");
        if (!sVal.Contains("23.06.2026")) W("[WARN] Start ist NICHT 23.06.2026 — bitte pruefen (ich tippe nichts ein).");

        var before = Directory.Exists(RecordingsDir)
            ? Directory.GetDirectories(RecordingsDir).Select(Path.GetFileName).Where(x => x != null).ToHashSet()!
            : new HashSet<string>();

        var select = FindButtonByName(dlg, "Auswählen");
        if (select == null) { W("BLOCKER: 'Auswählen'-Button nicht gefunden."); Flush(sb, outFile); return 5; }
        W("[load] Invoke 'Auswählen' …");
        Invoke(select);

        var play = FindByAutomationId(rw, "PlayButton");
        bool ready = false; string status = ""; bool pe = false; string lastLog = "";
        for (int i = 0; i < 70 && !ready; i++)
        {
            Thread.Sleep(1000);
            try { pe = play != null && play.Current.IsEnabled; } catch { pe = false; }
            status = ReplayStatusText(rw);
            string title = Safe(() => rw.Current.Name);
            string lg = LatestReplayState();
            if (lg != lastLog) { lastLog = lg; W($"  [t+{i + 1}s] PlayEnabled={pe} Titel='{title}' | LOG: {lg}"); }
            else if (i % 5 == 0 || pe) W($"  [t+{i + 1}s] PlayEnabled={pe}  Status='{Trunc(status, 44)}'  Titel='{title}'");
            if (pe && !status.Contains("Datum", StringComparison.OrdinalIgnoreCase)) ready = true;
        }
        W(ready ? "[bereit] JA: PlayButton aktiv, kein 'Datum auswählen'-Hinweis mehr." :
                  "[bereit] NEIN: innerhalb 70 s nicht abspielbereit (Play bleibt disabled / Ladehinweis).");
        W("[log] letzte Replay-Zustaende:"); foreach (var l in TailReplayLog(5)) W("   " + l);

        var after = Directory.Exists(RecordingsDir)
            ? Directory.GetDirectories(RecordingsDir).Select(Path.GetFileName).Where(x => x != null).ToHashSet()!
            : new HashSet<string>();
        var created = after.Except(before).ToList();
        W($"[ordner] Neue Ordner seit Laden: {created.Count} [{string.Join(", ", created)}]");
        // Aktiver Kandidat: genau ein neuer, gesperrter (=aktive Instanz). Sonst: gesperrten vorhandenen suchen.
        var locked = (created.Count > 0 ? created : after.ToList())
            .Where(f => IsMetaLocked(Path.Combine(RecordingsDir, f!, "meta.json"))).ToList();
        W($"[aktiv] gesperrte meta.json (laufende Instanz): {locked.Count} [{string.Join(", ", locked)}]");
        if (locked.Count == 1) W($"[aktiv] eindeutiger aktiver Ordner: {locked[0]}  -> capture --folder {locked[0]}");
        else W("[aktiv] NICHT eindeutig -> vor capture manuell bestaetigen (keine blinde Umhaengung).");
        Flush(sb, outFile);
        return ready ? 0 : 8;
    }

    private static bool IsMetaLocked(string metaPath)
    {
        if (!File.Exists(metaPath)) return false;
        try { using var fs = new FileStream(metaPath, FileMode.Open, FileAccess.Read, FileShare.None); return false; }
        catch (IOException) { return true; }
        catch { return false; }
    }

    private const uint MEF_RIGHTDOWN = 0x08, MEF_RIGHTUP = 0x10;
    private static void RightClickScreen(int x, int y)
    { SetCursorPos(x, y); Thread.Sleep(180); mouse_event(MEF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(80); mouse_event(MEF_RIGHTUP, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(250); }

    // Rechtsklick auf die MNQ-4RXV-Chartflaeche -> Kontextmenue (Indikatoren) + Screenshot + Menue-Dump.
    private static int RClickChart(string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }
        W($"# ATAS RClickChart  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        var pids = Process.GetProcessesByName("OFT.Platform").Select(p => p.Id).ToHashSet();
        var roots = TopWindows(pids);
        var header = FindInRoots(roots, ControlType.Button).FirstOrDefault(b => Safe(() => b.Current.Name).StartsWith("MNQ 4RXV Chart", StringComparison.OrdinalIgnoreCase));
        if (header == null) { W("4RXV-Header nicht gefunden."); Flush(sb, outFile); return 4; }
        System.Windows.Rect hr; try { hr = header.Current.BoundingRectangle; } catch { hr = System.Windows.Rect.Empty; }
        var pane = FindInRoots(roots, ControlType.Pane).Where(p => Safe(() => p.Current.Name) == "VulkanChartWpf")
            .Select(p => { System.Windows.Rect r; try { r = p.Current.BoundingRectangle; } catch { r = System.Windows.Rect.Empty; } return (p, r); })
            .Where(t => !t.r.IsEmpty && t.r.Y > 0 && Math.Abs(t.r.X - hr.X) < 60).OrderByDescending(t => t.r.Width * t.r.Height).Select(t => t.p).FirstOrDefault();
        if (pane == null) { W("4RXV-Chartflaeche nicht gefunden."); Flush(sb, outFile); return 4; }
        System.Windows.Rect pr = pane.Current.BoundingRectangle;
        int cx = (int)(pr.X + pr.Width * 0.5), cy = (int)(pr.Y + pr.Height * 0.72); // unterer Bereich, nicht die Replay-Leiste
        W($"[rclick] Foreground 4RXV + Rechtsklick ({cx},{cy}) …");
        Foreground(pane); Thread.Sleep(300); RightClickScreen(cx, cy); Thread.Sleep(1000);
        if (outFile != null) { var png = outFile.Replace(".txt", ".png"); int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1); Shot(0, 0, sw, sh, png); W($"[shot] {png}"); }
        // Kontextmenue-Eintraege (scoped)
        var before = roots;
        var newW = TopWindows(pids).Where(a => !before.Any(b => SameRuntimeId(a, b))).ToList();
        var mroots = new List<AutomationElement>(newW); mroots.AddRange(roots);
        W("[menue] Eintraege mit Indikator/Recorder/Einstell:");
        foreach (var e in FindInRoots(mroots, ControlType.MenuItem))
        {
            var nm = Safe(() => e.Current.Name);
            if (!string.IsNullOrEmpty(nm) && nm.Length < 50 && Regex.IsMatch(nm, "indik|recorder|orderflow|einstell|study|studie", RegexOptions.IgnoreCase))
                W($"   [MenuItem] '{nm}' Id='{Safe(() => e.Current.AutomationId)}' rect={Rect(e)}");
        }
        Flush(sb, outFile);
        return 0;
    }

    // Schliesst ein ATAS-Fenster (Name enthaelt Substring) via WindowPattern.Close oder dessen Schliessen-Button.
    private static int CloseWindow(string nameSub, string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }
        W($"# ATAS CloseWindow '{nameSub}'  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        if (string.IsNullOrWhiteSpace(nameSub)) { W("Kein Name angegeben."); Flush(sb, outFile); return 2; }
        var pids = Process.GetProcessesByName("OFT.Platform").Select(p => p.Id).ToHashSet();
        var win = TopWindows(pids).FirstOrDefault(w => Safe(() => w.Current.Name).Contains(nameSub, StringComparison.OrdinalIgnoreCase));
        if (win == null)
        {
            // als Descendant-Window suchen
            foreach (var top in TopWindows(pids))
                foreach (var e in FindInRoots(new[] { top }, ControlType.Window))
                    if (Safe(() => e.Current.Name).Contains(nameSub, StringComparison.OrdinalIgnoreCase)) { win = e; break; }
        }
        if (win == null) { W($"Fenster mit '{nameSub}' nicht gefunden (evtl. schon zu)."); Flush(sb, outFile); return 0; }
        W($"[close] '{Safe(() => win.Current.Name)}' …");
        bool done = false;
        try { if (win.TryGetCurrentPattern(WindowPattern.Pattern, out var wp)) { ((WindowPattern)wp).Close(); done = true; } } catch { }
        if (!done)
        {
            var cb = FindInRoots(new[] { win }, ControlType.Button).FirstOrDefault(b => { var h = Safe(() => b.Current.HelpText); var id = Safe(() => b.Current.AutomationId); return h.Contains("Schlie", StringComparison.OrdinalIgnoreCase) || id.Contains("Close", StringComparison.OrdinalIgnoreCase); });
            if (cb != null) { Invoke(cb); done = true; }
        }
        Thread.Sleep(800);
        var still = TopWindows(pids).Any(w => Safe(() => w.Current.Name).Contains(nameSub, StringComparison.OrdinalIgnoreCase));
        W(done ? $"[close] ausgeloest; Fenster noch offen: {still}" : "[close] kein Schliessmechanismus gefunden.");
        Flush(sb, outFile);
        return 0;
    }

    // Oeffnet den Indikatoren-Dialog des 4RXV-Charts und listet vorhandene Indikatoren (Recorder vorhanden?).
    private static int Indicators(string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }
        W($"# ATAS Indicators  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        var pids = Process.GetProcessesByName("OFT.Platform").Select(p => p.Id).ToHashSet();
        // Indikatoren-Button des OBEREN (aktiven = MNQ 4RXV) Chart-Toolbars (kleinstes Y) per ECHTEM Klick.
        var btn = FindInRoots(TopWindows(pids), ControlType.Button)
            .Where(b => { var n = Safe(() => b.Current.Name); var id = Safe(() => b.Current.AutomationId); return n == "Indikatoren" || id.Contains("IndicatorsButton", StringComparison.OrdinalIgnoreCase); })
            .Select(b => { System.Windows.Rect r; try { r = b.Current.BoundingRectangle; } catch { r = System.Windows.Rect.Empty; } return (b, r); })
            .Where(t => !t.r.IsEmpty && t.r.Y >= 0)
            .OrderBy(t => t.r.Y)
            .Select(t => t.b).FirstOrDefault();
        if (btn == null) { W("BLOCKER: 'Indikatoren'-Button (on-screen) nicht gefunden."); Flush(sb, outFile); return 4; }
        System.Windows.Rect br; try { br = btn.Current.BoundingRectangle; } catch { br = System.Windows.Rect.Empty; }
        W($"[open] 'Indikatoren' @ {(int)br.X},{(int)br.Y} -> ECHTER Klick (Foreground) …");
        var before = TopWindows(pids);
        ClickElement(btn); Thread.Sleep(1600);
        // Screenshot zur Sichtkontrolle
        if (outFile != null) { var png = outFile.Replace(".txt", ".png"); int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1); Shot(0, 0, sw, sh, png); W($"[shot] {png}"); }
        var dlgs = TopWindows(pids).Where(a => !before.Any(b => SameRuntimeId(a, b))).ToList();
        W($"Neue Fenster: {dlgs.Count} [{string.Join(", ", dlgs.Select(d => $"'{Trunc(Safe(() => d.Current.Name), 30)}'"))}]");
        var roots = dlgs.Count > 0 ? dlgs : TopWindows(pids);

        // Struktur dumpen (Listen/Baum/Edit/Buttons) + Recorder-Suche
        W("\n-- Dialoginhalt (Listen/Baum/Edit/Buttons) --");
        bool recPresent = false;
        foreach (var ct in new[] { ControlType.ListItem, ControlType.TreeItem, ControlType.DataItem, ControlType.Text, ControlType.Edit, ControlType.Button, ControlType.TabItem })
            foreach (var e in FindInRoots(roots, ct))
            {
                var nm = Safe(() => e.Current.Name);
                if (string.IsNullOrEmpty(nm) || nm.Length > 60) continue;
                bool isRec = Regex.IsMatch(nm, "OrderFlowReplayRecorder|OrderFlow.*Recorder|Replay.*Recorder|Recorder", RegexOptions.IgnoreCase);
                if (isRec) recPresent = true;
                if (isRec || ct == ControlType.ListItem || ct == ControlType.TreeItem || ct == ControlType.DataItem || ct == ControlType.Edit || ct == ControlType.TabItem
                    || (ct == ControlType.Button && !string.IsNullOrEmpty(nm)))
                    W($"  {(isRec ? "*** " : "")}[{ct.ProgrammaticName.Replace("ControlType.", "")}] '{nm}' Id='{Safe(() => e.Current.AutomationId)}'");
            }
        W($"\n[ergebnis] Recorder im Dialog sichtbar: {(recPresent ? "JA" : "NEIN/unklar")}");
        W("[hinweis] Dialog bleibt offen fuer den naechsten Schritt (hinzufuegen/pruefen).");
        Flush(sb, outFile);
        return 0;
    }

    // Dumpt alle Buttons der ON-SCREEN-Chart-Fenster (Indikator-Dialog-Oeffner finden). Markiert Indikator-/Settings-Kandidaten.
    private static int ChartTools(string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }
        W($"# ATAS ChartTools  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        var pids = Process.GetProcessesByName("OFT.Platform").Select(p => p.Id).ToHashSet();
        // On-Screen-Chart-Fenster (Y>=0, grosse Flaeche)
        var onscreen = TopWindows(pids).Where(w => { try { var r = w.Current.BoundingRectangle; return !r.IsEmpty && r.Y >= 0 && r.Width > 400 && r.Height > 300; } catch { return false; } }).ToList();
        W($"On-Screen-Chart-Fenster: {onscreen.Count}");
        var rx = new Regex("indik|indicator|studie|study|settings|einstell|kolben|flask|werkzeug|tool|add|hinzuf", RegexOptions.IgnoreCase);
        int n = 0, hit = 0;
        foreach (var win in onscreen)
        {
            foreach (var b in FindInRoots(new[] { win }, ControlType.Button))
            {
                n++;
                string nm = Safe(() => b.Current.Name), id = Safe(() => b.Current.AutomationId), ht = Safe(() => b.Current.HelpText);
                bool cand = rx.IsMatch(nm) || rx.IsMatch(id) || rx.IsMatch(ht);
                // Toolbar-Buttons (kleine, mit HelpText oder Id) oben im Chart zeigen
                System.Windows.Rect r; try { r = b.Current.BoundingRectangle; } catch { r = System.Windows.Rect.Empty; }
                if (cand || (!string.IsNullOrEmpty(ht)) || (!string.IsNullOrEmpty(id) && id != "PART_Close" && id != "PART_Grouping"))
                {
                    if (cand) hit++;
                    W($"  {(cand ? "*" : " ")}[Button] Name='{Trunc(nm, 26)}' Id='{Trunc(id, 26)}' Help='{Trunc(ht, 34)}' rect={(int)r.X},{(int)r.Y},{(int)r.Width}x{(int)r.Height}");
                }
            }
        }
        W($"\n[info] Buttons gesamt={n}, Indikator-/Tool-Kandidaten(*)={hit}");
        Flush(sb, outFile);
        return 0;
    }

    // Kontrollierter, GRACEFULER ATAS-Schluss (kein Prozess-Kill): WindowPattern.Close am Hauptfenster.
    // Erkennt Bestaetigungs-/Speichern-Dialoge und meldet sie, statt blind zu klicken.
    private static int CloseAtas(string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }
        W($"# ATAS CloseAtas (graceful)  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        var procs = Process.GetProcessesByName("OFT.Platform");
        if (procs.Length == 0) { W("ATAS laeuft nicht."); Flush(sb, outFile); return 0; }
        var pids = procs.Select(p => p.Id).ToHashSet();

        // Aktive Aufnahme? (irgendein Recorder-Ordner waechst gerade?)
        W("[check] aktive Aufnahme? (Dateigroessen 2x messen)");
        long s1 = RecDirBytes(), _ = 0; Thread.Sleep(2500); long s2 = RecDirBytes();
        W($"  Recordings-Bytes {s1} -> {s2} (delta {s2 - s1}) => {(s2 > s1 ? "AKTIV! -> NICHT schliessen" : "keine laufende Aufnahme")}");
        if (s2 > s1) { W("BLOCKER: laufende Aufnahme erkannt -> Abbruch (kein Schliessen bei unklarem Zustand)."); Flush(sb, outFile); return 9; }

        var main = TopWindows(pids).FirstOrDefault(w => Safe(() => w.Current.AutomationId) == "AtasMainWindow")
                   ?? TopWindows(pids).FirstOrDefault(w => Safe(() => w.Current.Name).StartsWith("ATAS -", StringComparison.OrdinalIgnoreCase));
        if (main == null) { W("BLOCKER: ATAS-Hauptfenster nicht gefunden."); Flush(sb, outFile); return 4; }
        W($"[close] WindowPattern.Close auf '{Safe(() => main.Current.Name)}' …");
        try { if (main.TryGetCurrentPattern(WindowPattern.Pattern, out var wp)) ((WindowPattern)wp).Close(); else W("  kein WindowPattern!"); }
        catch (Exception ex) { W($"  Close-Fehler: {ex.Message}"); }

        // Auf Exit ODER Dialog warten
        for (int i = 0; i < 15; i++)
        {
            Thread.Sleep(1000);
            var alive = Process.GetProcessesByName("OFT.Platform");
            if (alive.Length == 0) { W($"[exit] ATAS beendet nach {i + 1}s."); Flush(sb, outFile); return 0; }
            // neue Dialoge?
            var dlgs = TopWindows(alive.Select(p => p.Id).ToHashSet()).Where(w => { var n = Safe(() => w.Current.Name); return !string.IsNullOrEmpty(n) && !n.StartsWith("ATAS -") && !n.StartsWith("Replay") && !n.StartsWith("LayoutItem"); }).ToList();
            if (dlgs.Count > 0)
            {
                W($"[dialog] Bestaetigungs-/Speichern-Dialog(e) erschienen ({dlgs.Count}):");
                foreach (var d in dlgs)
                {
                    W($"   '{Safe(() => d.Current.Name)}'");
                    foreach (var b in FindInRoots(new[] { d }, ControlType.Button))
                    { var bn = Safe(() => b.Current.Name); if (!string.IsNullOrEmpty(bn)) W($"      [Button] '{bn}'"); }
                }
                W("[dialog] -> nicht blind geklickt. Bitte Dialog-Entscheidung festlegen (z. B. Workspace speichern).");
                Flush(sb, outFile); return 7;
            }
        }
        W("[exit] ATAS nach 15s noch aktiv (kein Dialog, kein Exit) -> kein Kill. Abbruch.");
        Flush(sb, outFile);
        return 8;
    }

    private static long RecDirBytes()
    {
        try { return Directory.Exists(RecordingsDir) ? Directory.GetFiles(RecordingsDir, "*", SearchOption.AllDirectories).Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } }) : 0; }
        catch { return 0; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    private static void Shot(int x, int y, int w, int h, string path)
    {
        try
        {
            w = Math.Max(1, w); h = Math.Max(1, h);
            using var bmp = new System.Drawing.Bitmap(w, h);
            using var g = System.Drawing.Graphics.FromImage(bmp);
            g.CopyFromScreen(x, y, 0, 0, bmp.Size);
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception ex) { Console.Error.WriteLine($"Shot-Fehler: {ex.Message}"); }
    }

    // Gezielte Diagnose: ALLE PlayButton-UIA-Treffer (frisch) mit vollen Eigenschaften + Screenshot + Sitzungskontext.
    private static int Diag(string[] args)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }
        string? outFile = args.FirstOrDefault(a => a.EndsWith(".txt"));
        string? pngBase = args.FirstOrDefault(a => a.EndsWith(".png"));
        W($"# ATAS Diag  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        var procs = Process.GetProcessesByName("OFT.Platform");
        if (procs.Length == 0) { W("ATAS laeuft nicht."); Flush(sb, outFile); return 3; }
        var pids = procs.Select(p => p.Id).ToHashSet();
        // Sitzungs-/Rechte-/Vordergrund-Kontext
        var self = Process.GetCurrentProcess();
        bool elevated = false;
        try { using var id = System.Security.Principal.WindowsIdentity.GetCurrent(); elevated = new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator); } catch { }
        var fg = GetForegroundWindow(); uint fgPid = 0; try { GetWindowThreadProcessId(fg, out fgPid); } catch { }
        W($"[sitzung] Tool pid={self.Id} session={self.SessionId} elevated={elevated}");
        foreach (var p in procs) W($"[sitzung] ATAS pid={p.Id} session={p.SessionId}");
        W($"[vordergrund] hwnd={fg} pid={fgPid} ({(pids.Contains((int)fgPid) ? "ATAS" : fgPid == self.Id ? "TOOL" : "andere App")})");

        // ALLE PlayButton-Treffer frisch
        W("\n-- Alle 'PlayButton'-Treffer im ATAS-Prozess (frisch) --");
        int idx = 0; var playRects = new List<System.Windows.Rect>();
        foreach (var e in FindInRoots(TopWindows(pids), ControlType.Button))
        {
            if (Safe(() => e.Current.AutomationId) != "PlayButton") continue;
            idx++;
            var top = TopLevelOf(e);
            string rid = ""; try { rid = string.Join(".", e.GetRuntimeId()); } catch { }
            bool en = false, off = true; System.Windows.Rect r = System.Windows.Rect.Empty;
            try { en = e.Current.IsEnabled; } catch { }
            try { off = e.Current.IsOffscreen; } catch { }
            try { r = e.Current.BoundingRectangle; } catch { }
            int hwnd = 0; try { hwnd = (top ?? e).Current.NativeWindowHandle; } catch { }
            W($"  #{idx} win='{Trunc(Safe(() => (top ?? e).Current.Name), 36)}' hwnd={hwnd} RuntimeId={rid}");
            W($"      Name='{Safe(() => e.Current.Name)}' Help='{Safe(() => e.Current.HelpText)}' IsEnabled={en} IsOffscreen={off} rect={(int)r.X},{(int)r.Y},{(int)r.Width}x{(int)r.Height}");
            if (!r.IsEmpty && !off) playRects.Add(r);
        }
        W($"[info] PlayButton-Treffer: {idx}");

        // Screenshots: Replay-Fenster-Region + Primaerschirm
        if (pngBase != null)
        {
            int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
            var rw = ReplayWindow();
            if (rw != null)
            {
                try
                {
                    var rr = rw.Current.BoundingRectangle;
                    int x = (int)rr.X - 10, y = (int)rr.Y - 10, w = (int)rr.Width + 20, h = (int)rr.Height + 20;
                    var p1 = pngBase.Replace(".png", "-replay.png");
                    Shot(Math.Max(0, x), Math.Max(0, y), w, h, p1);
                    W($"[shot] Replay-Fenster -> {p1}  (Region {x},{y},{w}x{h})");
                }
                catch (Exception ex) { W($"[shot] Replay-Fehler: {ex.Message}"); }
            }
            var p2 = pngBase.Replace(".png", "-screen.png");
            Shot(0, 0, sw, sh, p2);
            W($"[shot] Primaerschirm {sw}x{sh} -> {p2}");
        }
        Flush(sb, outFile);
        return 0;
    }

    // Liest den zuletzt geloggten replayModule-Zustand des MNQ-4RXV-Charts (True/False/unbekannt).
    private static bool? Rxv4ReplayModule()
    {
        var line = TailLines(AtasLogPath(), 262144).Where(l => l.Contains("MNQ 4RXV Chart") && l.Contains("replayModule=")).LastOrDefault();
        if (line == null) return null;
        var m = Regex.Match(line, "replayModule=(True|False)");
        return m.Success ? m.Groups[1].Value == "True" : (bool?)null;
    }

    // Stellt sicher, dass der MNQ-4RXV-Chart replayModule=True ist (verifiziert, hoechstens EINE Korrektur).
    private static int ToggleHeader(string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }
        W($"# ATAS Ensure4RXV  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        var pids = Process.GetProcessesByName("OFT.Platform").Select(p => p.Id).ToHashSet();
        var rw = ReplayWindow();
        if (rw == null) { W("Replay-Fenster nicht gefunden."); Flush(sb, outFile); return 4; }

        // Frischen Zustand erzeugen (Load loggt chart state) und lesen
        LoadAndWait(rw, W, 35);
        var st = Rxv4ReplayModule();
        W($"[4rxv] replayModule aktuell: {(st == null ? "unbekannt" : st.ToString())}");
        if (st == true) { W("[4rxv] bereits True -> keine Korrektur noetig."); Flush(sb, outFile); return 0; }

        var header = FindInRoots(TopWindows(pids), ControlType.Button).FirstOrDefault(b => Safe(() => b.Current.Name).StartsWith("MNQ 4RXV Chart", StringComparison.OrdinalIgnoreCase));
        if (header == null) { W("[4rxv] Header nicht gefunden."); Flush(sb, outFile); return 4; }
        W("[4rxv] EINE Korrektur: Invoke Header, dann Reload + Verifikation …");
        Invoke(header); Thread.Sleep(1000);
        LoadAndWait(rw, W, 35);
        var st2 = Rxv4ReplayModule();
        W($"[4rxv] replayModule nach Korrektur: {(st2 == null ? "unbekannt" : st2.ToString())}");
        W("[log] 4RXV-Zustand (letzte 2):");
        foreach (var l in TailLines(AtasLogPath(), 262144).Where(l => l.Contains("MNQ 4RXV Chart") && l.Contains("chart state")).Select(ShortLog).TakeLast(2)) W("   " + l);
        Flush(sb, outFile);
        return st2 == true ? 0 : 8;
    }

    // End-to-End-Vorbereitung ueber UIA (kein Vordergrund noetig): 4RXV-Chart aktivieren -> Datum laden ->
    // auf playContract=True + Play-aktiv pruefen (ATAS-Log). Mehrere UIA-invokable Kandidaten werden probiert.
    private static int Prepare(string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }
        W($"# ATAS Prepare  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        var pids = Process.GetProcessesByName("OFT.Platform").Select(p => p.Id).ToHashSet();
        var rw = ReplayWindow();
        if (rw == null) { W("BLOCKER: Replay-Fenster nicht gefunden."); Flush(sb, outFile); return 4; }
        var roots = TopWindows(pids);

        // UIA-invokable Kandidaten rund um den 4RXV-Chart
        var header = FindInRoots(roots, ControlType.Button).FirstOrDefault(b => Safe(() => b.Current.Name).StartsWith("MNQ 4RXV Chart", StringComparison.OrdinalIgnoreCase));
        AutomationElement? instrBtn = null;
        if (header != null)
        {
            // ChangeInstrumentButton in derselben Chart-Pane (gleiche X-Naehe)
            System.Windows.Rect hr; try { hr = header.Current.BoundingRectangle; } catch { hr = System.Windows.Rect.Empty; }
            instrBtn = FindInRoots(roots, ControlType.Button)
                .Where(b => Safe(() => b.Current.AutomationId) == "ChangeInstrumentButton")
                .Select(b => { System.Windows.Rect r; try { r = b.Current.BoundingRectangle; } catch { r = System.Windows.Rect.Empty; } return (b, r); })
                .Where(t => !t.r.IsEmpty && Math.Abs(t.r.X - hr.X) < 120 && t.r.Y > 0)
                .Select(t => t.b).FirstOrDefault();
        }
        W($"[kandidaten] 4RXV-Header={(header != null ? "ja" : "nein")}  ChangeInstrumentButton={(instrBtn != null ? "ja" : "nein")}");

        foreach (var (name, el) in new[] { ("4RXV-Header", header), ("ChangeInstrument#MNQZ6", instrBtn) })
        {
            if (el == null) continue;
            if (!SupportedPatterns(el).Contains("Invoke")) { W($"[{name}] kein Invoke-Pattern -> uebersprungen"); continue; }
            W($"[aktivieren] UIA-Invoke {name} …"); Invoke(el); Thread.Sleep(1200);
            W($"[aktivieren] LOG: {LatestReplayState()}");
            // Datum laden
            bool ready = LoadAndWait(rw, W, 60);
            if (ready) { W($"\n[OK] Nach '{name}': Play AKTIV. playContract gesetzt. -> capture."); Flush(sb, outFile); return 0; }
            W($"[{name}] danach Play weiterhin disabled.");
        }
        W("\n[offen] Keiner der UIA-Kandidaten aktivierte den Play-Contract.");
        W("[log] letzte Replay-Zustaende:"); foreach (var l in TailReplayLog(6)) W("   " + l);
        Flush(sb, outFile);
        return 8;
    }

    // Datum-Dialog (vorbelegt) per 'Auswählen' laden und auf Play-aktiv warten. Gibt true bei Bereitschaft.
    private static bool LoadAndWait(AutomationElement rw, Action<string> W, int maxSec)
    {
        var dlg = FindByAutomationId(rw, "ReplayDateSelectionDialog");
        if (dlg == null)
        {
            var datum = FindButtonByName(rw, "Datum auswählen");
            if (datum != null) { Invoke(datum); Thread.Sleep(1500); dlg = FindByAutomationId(rw, "ReplayDateSelectionDialog"); }
        }
        if (dlg == null) { W("  [load] Datumsdialog nicht gefunden."); return false; }
        W($"  [load] Auswahl: Start='{ValueOf(FindByAutomationId(dlg, "StartEdit"))}' Ende='{ValueOf(FindByAutomationId(dlg, "EndEdit"))}'");
        var sel = FindButtonByName(dlg, "Auswählen");
        if (sel == null) { W("  [load] 'Auswählen' nicht gefunden."); return false; }
        Invoke(sel);
        var play = FindByAutomationId(rw, "PlayButton");
        for (int i = 0; i < maxSec; i++)
        {
            Thread.Sleep(1000);
            bool pe = false; try { pe = play != null && play.Current.IsEnabled; } catch { }
            if (i % 5 == 0 || pe) W($"  [load t+{i + 1}s] PlayEnabled={pe} LOG: {LatestReplayState()}");
            if (pe) return true;
        }
        return false;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    private const uint MEF_LEFTDOWN = 0x02, MEF_LEFTUP = 0x04;
    private static void ClickScreen(int x, int y)
    { SetCursorPos(x, y); Thread.Sleep(180); mouse_event(MEF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(80); mouse_event(MEF_LEFTUP, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(200); }
    private static void DoubleClickFast(int x, int y)
    { SetCursorPos(x, y); Thread.Sleep(60);
      mouse_event(MEF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(30); mouse_event(MEF_LEFTUP, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(60);
      mouse_event(MEF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero); Thread.Sleep(30); mouse_event(MEF_LEFTUP, 0, 0, 0, UIntPtr.Zero); }

    // Bringt das Top-Level-Fenster eines Elements in den Vordergrund (sonst landen Klicks auf anderen Apps).
    private static AutomationElement? TopLevelOf(AutomationElement el)
    {
        try
        {
            var cur = el; var root = AutomationElement.RootElement;
            while (cur != null)
            {
                var parent = TreeWalker.ControlViewWalker.GetParent(cur);
                if (parent == null || Automation.Compare(parent, root)) return cur;
                cur = parent;
            }
        }
        catch { }
        return null;
    }
    private static bool Foreground(AutomationElement el)
    {
        try
        {
            var top = TopLevelOf(el) ?? el;
            int h = 0; try { h = top.Current.NativeWindowHandle; } catch { }
            if (h == 0) { try { h = el.Current.NativeWindowHandle; } catch { } }
            if (h != 0) { SetForegroundWindow(new IntPtr(h)); Thread.Sleep(350); return true; }
        }
        catch { }
        return false;
    }
    private static void ClickElement(AutomationElement el)
    {
        Foreground(el);
        System.Windows.Rect r; try { r = el.Current.BoundingRectangle; } catch { return; }
        ClickScreen((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
    }

    // Findet die On-Screen-Chart-Flaeche des MNQ-4RXV-Charts (Recorder-Chart) und waehlt sie als Replay-Modul:
    // "Modul auswählen" scharfschalten -> verifizierter Klick auf die Chart-Flaeche -> Wirkung aus ATAS-Log zuruecklesen.
    private static int SelectModule(string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }
        W($"# ATAS SelectModule  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        var pids = Process.GetProcessesByName("OFT.Platform").Select(p => p.Id).ToHashSet();
        var rw = ReplayWindow();
        if (rw == null) { W("BLOCKER: Replay-Fenster nicht gefunden."); Flush(sb, outFile); return 4; }

        // 4RXV-Header-Button finden -> dessen X lokalisiert die zugehoerige Chart-Pane.
        var roots = TopWindows(pids);
        AutomationElement? header = FindInRoots(roots, ControlType.Button)
            .FirstOrDefault(b => Safe(() => b.Current.Name).StartsWith("MNQ 4RXV Chart", StringComparison.OrdinalIgnoreCase));
        if (header == null) { W("BLOCKER: 'MNQ 4RXV Chart'-Header nicht gefunden (Chart sichtbar?)."); Flush(sb, outFile); return 4; }
        System.Windows.Rect hr; try { hr = header.Current.BoundingRectangle; } catch { hr = System.Windows.Rect.Empty; }
        W($"[ziel] Header 'MNQ 4RXV Chart' @ x={(int)hr.X},y={(int)hr.Y}");

        // zugehoerige VulkanChartWpf-Pane: gleiche X-Naehe, on-screen (Y>0), grosse Flaeche.
        AutomationElement? pane = FindInRoots(roots, ControlType.Pane)
            .Where(p => Safe(() => p.Current.Name) == "VulkanChartWpf")
            .Select(p => { System.Windows.Rect r; try { r = p.Current.BoundingRectangle; } catch { r = System.Windows.Rect.Empty; } return (p, r); })
            .Where(t => !t.r.IsEmpty && t.r.Y > 0 && t.r.Width > 300 && Math.Abs(t.r.X - hr.X) < 60)
            .OrderByDescending(t => t.r.Width * t.r.Height)
            .Select(t => t.p).FirstOrDefault();
        if (pane == null) { W("BLOCKER: On-Screen-Chartflaeche (VulkanChartWpf) des 4RXV nicht gefunden."); Flush(sb, outFile); return 4; }
        System.Windows.Rect pr = pane.Current.BoundingRectangle;
        int cx = (int)(pr.X + pr.Width / 2), cy = (int)(pr.Y + pr.Height / 2);
        W($"[ziel] 4RXV-Chartflaeche rect={(int)pr.X},{(int)pr.Y},{(int)pr.Width}x{(int)pr.Height} -> Klickpunkt ({cx},{cy})");
        if (cy < 0 || cx < 0) { W("BLOCKER: Chartflaeche off-screen -> kein sicherer Klick."); Flush(sb, outFile); return 4; }

        var modul = FindButtonByName(rw, "Modul auswählen");
        if (modul == null) { W("BLOCKER: 'Modul auswählen' nicht gefunden."); Flush(sb, outFile); return 4; }
        W($"[fg] Vordergrund-Fenster vorher: {GetForegroundWindow()}");
        var winsBefore = TopWindows(pids);
        // (A) Vordergrund Replay-Fenster + ECHTER Klick auf 'Modul auswählen'
        W("[A] Foreground Replay + Klick 'Modul auswählen' …"); ClickElement(modul); Thread.Sleep(1200);
        W($"[A] Panel-Status: '{Trunc(ReplayStatusText(rw), 60)}'  LOG: {LatestReplayState()}");
        var newW = TopWindows(pids).Where(a => !winsBefore.Any(b => SameRuntimeId(a, b))).ToList();
        W($"[A] neue ATAS-Fenster: {newW.Count} [{string.Join(", ", newW.Select(w => $"'{Trunc(Safe(() => w.Current.Name), 26)}'"))}]");
        var pickRoots = new List<AutomationElement>(newW) { rw };
        foreach (var ct in new[] { ControlType.ListItem, ControlType.MenuItem, ControlType.Button, ControlType.DataItem, ControlType.TreeItem })
            foreach (var e in FindInRoots(pickRoots, ct))
            {
                var nm = Safe(() => e.Current.Name);
                if (!string.IsNullOrEmpty(nm) && nm.Length < 50 && Regex.IsMatch(nm, "MNQ|MES|\\bES\\b|RXV|5m|1m", RegexOptions.IgnoreCase))
                    W($"   [{ct.ProgrammaticName.Replace("ControlType.", "")}] '{nm}' Id='{Safe(() => e.Current.AutomationId)}' rect={Rect(e)} pat=[{string.Join(",", SupportedPatterns(e))}]");
            }
        // (B) Falls Play noch disabled: Vordergrund 4RXV-Chart + Klick auf dessen Flaeche (Aktivierung/Play-Contract)
        bool pe0 = false; try { pe0 = FindByAutomationId(rw, "PlayButton")?.Current.IsEnabled ?? false; } catch { }
        if (!pe0)
        {
            W($"[B] Foreground 4RXV-Chartfenster + Klick Chartflaeche ({cx},{cy}) …");
            Foreground(pane); Thread.Sleep(300); ClickScreen(cx, cy); Thread.Sleep(1500);
            W($"[B] LOG: {LatestReplayState()}");
        }

        // Wirkung zuruecklesen: Play aktiv? Panel-Status? Log?
        var play = FindByAutomationId(rw, "PlayButton");
        bool pe = false; try { pe = play != null && play.Current.IsEnabled; } catch { }
        W($"[readback] PlayButton IsEnabled={pe}  Panel='{Trunc(ReplayStatusText(rw), 50)}'  Titel='{Safe(() => rw.Current.Name)}'");
        W("[readback] Log (neu):"); foreach (var l in TailReplayLog(6)) W("   " + l);
        W(pe ? "\n[ok] Play ist aktiv -> Modul gewaehlt. Weiter mit load + capture."
             : "\n[offen] Play noch nicht aktiv -> ggf. Datum neu laden; Mechanismus pruefen.");
        Flush(sb, outFile);
        return pe ? 0 : 8;
    }

    // Zustand des ATAS-Hauptfensters (Charts). Minimiert = Charts nicht anklickbar -> Modulwahl unmoeglich.
    private static int MainWin(bool restore, string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }
        W($"# ATAS MainWin  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        var pids = Process.GetProcessesByName("OFT.Platform").Select(p => p.Id).ToHashSet();
        W("-- Alle ATAS-Top-Level-Fenster --");
        foreach (var w in TopWindows(pids))
        {
            string vs0 = "?"; try { if (w.TryGetCurrentPattern(WindowPattern.Pattern, out var wp)) vs0 = ((WindowPattern)wp).Current.WindowVisualState.ToString(); } catch { }
            W($"  '{Trunc(Safe(() => w.Current.Name), 44)}' Id='{Trunc(Safe(() => w.Current.AutomationId), 26)}' State={vs0} rect={Rect(w)}");
        }
        W("");
        AutomationElement? main = null;
        foreach (var w in TopWindows(pids))
        {
            var id = Safe(() => w.Current.AutomationId);
            if (id == "AtasMainWindow") { main = w; break; }
        }
        if (main == null)
        {
            // Fallback: groesstes Fenster des Prozesses
            main = TopWindows(pids).OrderByDescending(w => { try { var r = w.Current.BoundingRectangle; return r.Width * r.Height; } catch { return 0; } }).FirstOrDefault();
        }
        if (main == null) { W("BLOCKER: ATAS-Hauptfenster nicht gefunden."); Flush(sb, outFile); return 4; }

        string vs = "?";
        try { if (main.TryGetCurrentPattern(WindowPattern.Pattern, out var wp)) vs = ((WindowPattern)wp).Current.WindowVisualState.ToString(); } catch { }
        W($"Hauptfenster: '{Safe(() => main.Current.Name)}' Id='{Safe(() => main.Current.AutomationId)}' VisualState={vs} rect={Rect(main)}");

        if (restore && vs == "Minimized")
        {
            try { if (main.TryGetCurrentPattern(WindowPattern.Pattern, out var wp)) { ((WindowPattern)wp).SetWindowVisualState(WindowVisualState.Normal); W("[restore] -> Normal gesetzt."); Thread.Sleep(1500); } }
            catch (Exception ex) { W($"[restore] Fehler: {ex.Message}"); }
            try { if (main.TryGetCurrentPattern(WindowPattern.Pattern, out var wp2)) vs = ((WindowPattern)wp2).Current.WindowVisualState.ToString(); } catch { }
            W($"[restore] neuer VisualState={vs} rect={Rect(main)}");
        }

        // Charts/Tabs mit "4RXV"/"RXV"/"MNQ"/"Chart" suchen - in ALLEN ATAS-Top-Level-Fenstern (LayoutItems).
        W("\n-- Chart-bezogene Elemente (Name enthaelt RXV/MNQ/Chart) --");
        int shown = 0;
        var allRoots = TopWindows(pids);
        foreach (var ct in new[] { ControlType.TabItem, ControlType.Tab, ControlType.Custom, ControlType.Pane, ControlType.Window, ControlType.Document, ControlType.Button })
        {
            foreach (var e in FindInRoots(allRoots, ct))
            {
                var n = Safe(() => e.Current.Name);
                if (string.IsNullOrEmpty(n)) continue;
                if (n.Contains("RXV", StringComparison.OrdinalIgnoreCase) || n.Contains("MNQ", StringComparison.OrdinalIgnoreCase)
                    || (n.Contains("Chart", StringComparison.OrdinalIgnoreCase) && n.Length < 40))
                {
                    W($"  [{ct.ProgrammaticName.Replace("ControlType.", "")}] '{Trunc(n, 46)}' Id='{Trunc(Safe(() => e.Current.AutomationId), 28)}' rect={Rect(e)} pat=[{string.Join(",", SupportedPatterns(e))}]");
                    if (++shown > 40) break;
                }
            }
            if (shown > 40) break;
        }
        W($"\n[info] {shown} Chart-bezogene Elemente.");
        Flush(sb, outFile);
        return 0;
    }

    private static string AtasLogPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ATAS", "Logs", $"app_{DateTime.Now:yyyyMMdd}.log");

    private static string[] TailLines(string path, int maxBytes)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            long start = Math.Max(0, fs.Length - maxBytes);
            fs.Seek(start, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            return sr.ReadToEnd().Split('\n');
        }
        catch { return Array.Empty<string>(); }
    }

    private static string ShortLog(string l)
    { var i = l.IndexOf("INFO"); return (i > 0 && l.Length > 19) ? l[..19] + " " + l[(i + 4)..].Trim() : l.Trim(); }

    private static string LatestReplayState()
    {
        var lines = TailLines(AtasLogPath(), 65536);
        for (int i = lines.Length - 1; i >= 0; i--)
            if (lines[i].Contains("Replay") && lines[i].Contains("State changed")) return ShortLog(lines[i]);
        return "";
    }

    private static List<string> TailReplayLog(int n)
    {
        var lines = TailLines(AtasLogPath(), 131072);
        var res = lines.Where(l => l.Contains("Replay") && (l.Contains("State changed") || l.Contains("chart state")
                    || l.Contains("Initializing to") || l.Contains("historical data"))).Select(ShortLog).ToList();
        return res.Skip(Math.Max(0, res.Count - n)).ToList();
    }

    private static IEnumerable<AutomationElement> FindInRoots(IEnumerable<AutomationElement> roots, ControlType ct)
    {
        foreach (var r in roots)
        {
            AutomationElementCollection? col = null;
            try { col = r.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ct)); } catch { }
            if (col != null) foreach (AutomationElement e in col) yield return e;
        }
    }

    private static List<AutomationElement> TopWindows(HashSet<int> pids)
    {
        var list = new List<AutomationElement>();
        try
        {
            foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
                try { if (pids.Contains(w.Current.ProcessId)) list.Add(w); } catch { }
        }
        catch { }
        return list;
    }

    // ---- Fensteruebergreifende UIA-Suche (robust gegen Docking/Floating des Replay-Transports) ----
    private static List<AutomationElement> AtasRoots() => TopWindows(Process.GetProcessesByName("OFT.Platform").Select(p => p.Id).ToHashSet());

    private static AutomationElement? FindIdAny(string id)
    {
        foreach (var r in AtasRoots())
        { try { var e = r.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id)); if (e != null) return e; } catch { } }
        return null;
    }
    private static AutomationElement? FindBtnAny(string name)
    {
        foreach (var r in AtasRoots())
        { try { var e = r.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button), new PropertyCondition(AutomationElement.NameProperty, name))); if (e != null) return e; } catch { } }
        return null;
    }
    private static AutomationElement? FindCheckAny(string name)
    {
        foreach (var r in AtasRoots())
        { try { var e = r.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox), new PropertyCondition(AutomationElement.NameProperty, name))); if (e != null) return e; } catch { } }
        return null;
    }
    private static AutomationElement? FindSpeedAny()
    {
        var rx = new Regex(@"^\s*\d+(\.\d+)?x\s*$", RegexOptions.IgnoreCase);
        foreach (var b in FindInRoots(AtasRoots(), ControlType.Button))
        { var n = Safe(() => b.Current.Name); if (!string.IsNullOrEmpty(n) && rx.IsMatch(n)) return b; }
        return null;
    }

    // Setzt die Replay-Geschwindigkeit ueber den vorhandenen Speed-Selektor auf 'requested' (z.B. '100x') und
    // liefert den ZURUECKGELESENEN Namen. Oeffnet das Menue, waehlt den exakten Eintrag und schliesst sonst
    // wieder (keine Aenderung, wenn die Option fehlt -> Ruecklesung bleibt ungleich -> Aufrufer bricht ab).
    private static string SetReplaySpeed(string requested, Action<string> W)
    {
        var rxReq = new Regex(@"^\s*" + Regex.Escape(requested.Trim()) + @"\s*$", RegexOptions.IgnoreCase);
        var speed = FindSpeedAny(); string sp = speed != null ? Safe(() => speed.Current.Name) : "?";
        for (int attempt = 0; attempt < 3 && speed != null && !rxReq.IsMatch(sp); attempt++)
        {
            // Dropdown OEFFNEN: die Optionen (0.1x,0.3x,0.5x,1x,5x,10x,100x,1000x) sind BUTTONS, keine MenuItems.
            // Der Selektor-Button unterstuetzt ExpandCollapse -> bevorzugt expandieren (Invoke oeffnet nicht zuverlaessig).
            bool opened = false;
            try { if (speed.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ep)) { ((ExpandCollapsePattern)ep).Expand(); opened = true; } } catch { }
            if (!opened) Invoke(speed);
            Thread.Sleep(700);
            var opt = FindButtonByNameRegexAny(rxReq);     // exakte Option als Button (favoritenunabhaengig)
            if (opt != null) { Invoke(opt); Thread.Sleep(700); }
            else { try { if (speed.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ep2)) ((ExpandCollapsePattern)ep2).Collapse(); } catch { } Thread.Sleep(200); }
            speed = FindSpeedAny(); sp = speed != null ? Safe(() => speed.Current.Name) : "?";
        }
        return sp;
    }

    // Button mit exaktem Namen (Regex) ueber ALLE ATAS-Prozessfenster suchen (auch Dropdown-Popups).
    private static AutomationElement? FindButtonByNameRegexAny(Regex rx)
    {
        var pids = Process.GetProcessesByName("OFT.Platform").Select(p => p.Id).ToHashSet();
        foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
        {
            try { if (!pids.Contains(w.Current.ProcessId)) continue; } catch { continue; }
            AutomationElementCollection coll;
            try { coll = w.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)); } catch { continue; }
            foreach (AutomationElement b in coll) { var n = Safe(() => b.Current.Name); if (!string.IsNullOrEmpty(n) && rx.IsMatch(n)) return b; }
        }
        return null;
    }
    private static bool IsEnabledSafe(AutomationElement? e) { try { return e != null && e.Current.IsEnabled; } catch { return false; } }

    private static void DumpDialog(AutomationElement el, int depth, Action<string> W, ref int n)
    {
        if (el == null || depth > 28 || n > 4000) return;
        n++;
        string ct = ""; try { ct = el.Current.ControlType?.ProgrammaticName?.Replace("ControlType.", "") ?? ""; } catch { }
        string nm = Safe(() => el.Current.Name), id = Safe(() => el.Current.AutomationId);
        var pats = SupportedPatterns(el);
        string val = "";
        try { if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vp)) val = ((ValuePattern)vp).Current.Value ?? ""; } catch { }
        try { if (string.IsNullOrEmpty(val) && el.TryGetCurrentPattern(RangeValuePattern.Pattern, out var rp)) val = ((RangeValuePattern)rp).Current.Value.ToString(); } catch { }
        bool interesting = ct is "Edit" or "ComboBox" or "Calendar" or "Spinner" or "Button" or "CheckBox" or "RadioButton"
                           or "List" or "ListItem" or "DataItem" or "Tab" or "TabItem" or "Hyperlink"
                           || !string.IsNullOrEmpty(val)
                           || (ct == "Text" && !string.IsNullOrEmpty(nm) && nm.Length < 60)
                           || (!string.IsNullOrEmpty(id));
        if (interesting && (!string.IsNullOrEmpty(nm) || !string.IsNullOrEmpty(id) || !string.IsNullOrEmpty(val)))
            W($"{new string(' ', Math.Min(depth, 10) * 2)}[{ct}] Name='{Trunc(nm, 40)}' Id='{Trunc(id, 30)}' Val='{Trunc(val, 30)}' pat=[{string.Join(",", pats)}] rect={Rect(el)}");
        AutomationElement? c = null;
        try { c = TreeWalker.ControlViewWalker.GetFirstChild(el); } catch { }
        while (c != null) { DumpDialog(c, depth + 1, W, ref n); try { c = TreeWalker.ControlViewWalker.GetNextSibling(c); } catch { break; } }
    }

    private static readonly string[] Streams =
        { "ticks.csv", "ticks.ndjson", "cumtrades.ndjson", "bbo.ndjson", "dom.ndjson", "bars.ndjson" };

    // Strikt zugeordneter Aufnahmenachweis. AUSSCHLIESSLICH der per --folder uebergebene Ordner wird gemessen
    // (vorher per meta.json bestaetigt). KEIN Rueckfall auf aeltere Ordner, KEIN altes summary.json als Beleg.
    // Erfolg nur aus Vorher/Nachher-Dateistaenden + neuen Ereignissen je Stream + deren Marktzeitstempeln.
    // Legt ATAS eine neue Recorder-Instanz an (neuer Ordner), wird die Zuordnung umgehaengt und ausgewiesen.
    // Finalisieren NICHT ueber "Zur Echtzeit wechseln" (kein versehentlicher Live-Recorder). --manual: kein
    // UIA-Play, sondern auf manuelles Play warten (gleicher Beobachter) -> trennt Bedienung von Replay/Recorder.
    private static int Capture(string[] args)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }
        string? outFile = args.FirstOrDefault(a => a.EndsWith(".txt"));
        bool manual = args.Contains("--manual");
        int fi = Array.IndexOf(args, "--folder");
        string? folder = (fi >= 0 && fi + 1 < args.Length) ? args[fi + 1] : null;

        W($"# ATAS Capture (strikt)  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        if (string.IsNullOrWhiteSpace(folder))
        {
            W("BLOCKER: --folder <Ordnername> ist PFLICHT (kein Auto-Rueckfall). Vorhandene (neueste zuerst):");
            foreach (var r in SnapshotRecordings().OrderByDescending(x => x.Meta).Take(6))
                W($"  {r.Name}  meta={r.Meta:HH:mm:ss}  startWall={r.StartWall}");
            Flush(sb, outFile); return 2;
        }
        string dir = Path.Combine(RecordingsDir, folder);
        var metaPath = Path.Combine(dir, "meta.json");
        if (!File.Exists(metaPath)) { W($"BLOCKER: meta.json fehlt in '{folder}' -> kein gueltiger Aufnahmeordner."); Flush(sb, outFile); return 3; }
        W($"[ordner]  {folder}");
        W($"[meta]    {Trunc(ReadAllShared(metaPath).Replace("\n", " "), 420)}  (meta.json gesperrt = Recorder-Instanz aktiv)");

        var rw = ReplayWindow();
        if (rw == null) { W("BLOCKER: Replay-Fenster nicht gefunden."); Flush(sb, outFile); return 4; }
        var play = FindByAutomationId(rw, "PlayButton");
        var speed = FindButtonByNameRegex(rw, new Regex(@"^\s*\d+(\.\d+)?x\s*$", RegexOptions.IgnoreCase));
        if (play == null) { W("BLOCKER: PlayButton nicht gefunden."); Flush(sb, outFile); return 5; }

        // 1x bestaetigen/setzen
        string spName = speed != null ? Safe(() => speed.Current.Name) : "?";
        if (speed != null && !Regex.IsMatch(spName, @"^\s*1x\s*$", RegexOptions.IgnoreCase))
        {
            Invoke(speed); Thread.Sleep(900);
            var it = FindMenuItemByName(new Regex(@"^\s*1x\s*$", RegexOptions.IgnoreCase));
            if (it != null) { Invoke(it); Thread.Sleep(600); } else { Invoke(speed); Thread.Sleep(300); }
            speed = FindButtonByNameRegex(rw, new Regex(@"^\s*\d+(\.\d+)?x\s*$", RegexOptions.IgnoreCase));
            spName = speed != null ? Safe(() => speed.Current.Name) : "?";
        }
        bool is1x = Regex.IsMatch(spName, @"^\s*1x\s*$", RegexOptions.IgnoreCase);
        W($"[speed]   '{spName}' {(is1x ? "(=1x OK)" : "(NICHT 1x!)")}");

        // Vorher-Stand NUR dieses Ordners + Ordner-Set zur Erkennung neuer Instanzen
        var before = FolderStreams(dir);
        var knownFolders = Directory.Exists(RecordingsDir)
            ? Directory.GetDirectories(RecordingsDir).Select(Path.GetFileName).Where(n => n != null).ToHashSet()!
            : new HashSet<string>();
        string t0 = Safe(() => rw.Current.Name);
        W($"[titel t0] '{t0}'");
        W("[vorher]  " + StreamsLine(before));

        // Play (UIA) oder manuell
        string activeFolder = folder; string activeDir = dir; bool uiaPlayAckd = manual;
        bool playEnabled = false; try { playEnabled = play.Current.IsEnabled; } catch { }
        string statusText = ReplayStatusText(rw);
        W($"[play-btn] IsEnabled={playEnabled}  PanelStatus='{Trunc(statusText, 80)}'");
        if (manual)
        {
            W("[manuell] Bitte JETZT im ATAS-Replay EINMAL Play druecken (1x). Beobachtung laeuft …");
        }
        else if (!playEnabled)
        {
            W("BLOCKER (Bedienung/Bereitschaft): PlayButton ist DISABLED -> Replay in diesem Zustand NICHT abspielbereit.");
            W("  Weder UIA- noch manuelles Play moeglich, bis ein abspielbereiter Abschnitt geladen ist ('Datum auswählen').");
            Flush(sb, outFile); return 9;
        }
        else
        {
            W("[play]    Invoke PlayButton …");
            if (!Invoke(play)) { W("BLOCKER: Invoke(Play) fehlgeschlagen trotz IsEnabled -> bitte --manual nutzen."); Flush(sb, outFile); return 7; }
            Thread.Sleep(2500);
            string t1 = Safe(() => rw.Current.Name);
            uiaPlayAckd = !string.Equals(t1, t0, StringComparison.Ordinal);
            W(uiaPlayAckd ? $"[play]    Transport reagierte: '{t0}' -> '{t1}'"
                          : "[play]    WARNUNG: Titel unveraendert -> UIA-Play evtl. wirkungslos (Fallback: --manual).");
        }

        // Beobachten bis ~120 s oder klarer Tick-Fortschritt; bei unklarem Zustand pausieren
        var swWall = Stopwatch.StartNew();
        string lastTitle = ""; bool reattached = false;
        while (swWall.Elapsed.TotalSeconds < 120)
        {
            Thread.Sleep(3000);
            var nowFolders = Directory.GetDirectories(RecordingsDir).Select(Path.GetFileName).Where(n => n != null).ToHashSet()!;
            var created = nowFolders.Except(knownFolders).ToList();
            if (created.Count > 0 && !reattached)
            {
                activeFolder = created.OrderByDescending(n => n).First()!;
                activeDir = Path.Combine(RecordingsDir, activeFolder);
                reattached = true;
                before = FolderStreams(activeDir);
                W($"[attribution] NEUE Recorder-Instanz '{activeFolder}' -> messe ab jetzt EINDEUTIG diese (nicht '{folder}').");
            }
            var cur = FolderStreams(activeDir);
            string t = Safe(() => rw.Current.Name);
            string note = t != lastTitle ? $"  Titel='{t}'" : "";
            lastTitle = t;
            W($"  [t+{(int)swWall.Elapsed.TotalSeconds,3}s] {StreamsLine(cur)}{note}");
            if (Grew(before, cur, "ticks.csv") && MtSpan(cur, "ticks.csv") >= 60) { W("  -> klarer Tick-Marktzeit-Fortschritt (>=60s)."); break; }
        }

        // Pause
        W("[pause]   Invoke PlayButton …");
        Invoke(play); Thread.Sleep(1500);
        W($"[titel t2] '{Safe(() => rw.Current.Name)}'");
        W("[finalize] KEIN 'Zur Echtzeit wechseln'. Recorder bleibt am Chart (pausiert); summary.json nur durch " +
          "manuelles Entfernen AUSSCHLIESSLICH dieser Indikator-Instanz. Kein Live-Recorder gestartet.");

        // Getrennter Streambericht NUR aus dem aktiven Ordner
        var after = FolderStreams(activeDir);
        W($"\n--- Ergebnis (gemessener Ordner: {activeFolder}{(reattached ? " [umgehaengt]" : "")}) ---");
        W($"1x gesetzt : {(is1x ? "JA" : "NEIN")} | UIA-Play quittiert: {(uiaPlayAckd ? "JA" : "NEIN")}");
        bool tradeGrew = false, tradeIsReplay = false, anyGrew = false;
        foreach (var s in Streams)
        {
            before.TryGetValue(s, out var b); after.TryGetValue(s, out var a);
            long db = (a?.Bytes ?? 0) - (b?.Bytes ?? 0);
            if (db > 0) anyGrew = true;
            string span = a?.FirstMt != null ? $"{a.FirstMt:yyyy-MM-dd HH:mm:ss}->{a.LastMt:HH:mm:ss} (~{MtSpan(after, s):F0}s)" : "-";
            W($"  {s,-16} Bytes {(b?.Bytes ?? 0),9} -> {(a?.Bytes ?? 0),9}  (+{db})   Marktzeit {span}");
            if ((s == "ticks.csv" || s == "ticks.ndjson") && db > 0)
            {
                tradeGrew = true;
                if (a?.FirstMt is { } fmt && fmt.Year == 2026 && fmt.Month == 6) tradeIsReplay = true;
            }
        }
        W(tradeGrew
            ? $"\nERGEBNIS: Tick-Aufnahme in '{activeFolder}' gewachsen." + (tradeIsReplay ? " Marktzeit = Replay-Datum (Juni) -> echter 1x-Nachweis." : " ACHTUNG: Tick-Marktzeit NICHT Replay-Datum (Live?).")
            : anyGrew
              ? $"\nRESTBLOCKER: Nur Nicht-Tick-Streams wuchsen (DOM/Cumtrades), KEINE ticks.* -> OnNewTrade liefert in dieser Replay-Konfiguration keinen Tick-Stream."
              : $"\nRESTBLOCKER: KEIN Stream in '{activeFolder}' gewachsen -> Replay lieferte nichts an diese Recorder-Instanz.");
        Flush(sb, outFile);
        return tradeGrew ? 0 : 8;
    }

    private sealed record Sstat(long Bytes, DateTime? FirstMt, DateTime? LastMt);

    private static Dictionary<string, Sstat> FolderStreams(string dir)
    {
        var d = new Dictionary<string, Sstat>();
        foreach (var s in Streams)
        {
            var p = Path.Combine(dir, s);
            if (!File.Exists(p)) { d[s] = new Sstat(0, null, null); continue; }
            long bytes = 0; try { bytes = new FileInfo(p).Length; } catch { }
            DateTime? fm, lm;
            if (s.EndsWith(".csv")) { fm = ParseDt(FirstField(FirstCsvData(p))); lm = ParseDt(FirstField(ReadLastLine(p) ?? "")); }
            else { fm = ParseDt(JsonTMarket(FirstLine(p) ?? "")); lm = ParseDt(JsonTMarket(ReadLastLine(p) ?? "")); }
            d[s] = new Sstat(bytes, fm, lm);
        }
        return d;
    }

    private static bool Grew(Dictionary<string, Sstat> b, Dictionary<string, Sstat> a, string s)
        => (a.TryGetValue(s, out var av) ? av.Bytes : 0) > (b.TryGetValue(s, out var bv) ? bv.Bytes : 0);
    private static double MtSpan(Dictionary<string, Sstat> m, string s)
        => m.TryGetValue(s, out var v) && v.FirstMt != null && v.LastMt != null ? (v.LastMt.Value - v.FirstMt.Value).TotalSeconds : 0;
    private static string StreamsLine(Dictionary<string, Sstat> m)
        => string.Join("  ", Streams.Select(s => $"{s.Split('.')[0]}:{(m.TryGetValue(s, out var v) ? v.Bytes : 0)}"));
    private static string FirstField(string line) { var i = line.IndexOf(','); return i >= 0 ? line[..i].Trim().Trim('"') : line.Trim(); }
    private static string JsonTMarket(string line) => Match(line, "\"tMarket\"\\s*:\\s*\"([^\"]+)\"");
    private static string JsonTRecv(string line) => Match(line, "\"tRecv\"\\s*:\\s*\"([^\"]+)\"");

    // Zeilenweises, gemeinsames Lesen (Reader bleibt offen, waehrend der Recorder schreibt).
    private static IEnumerable<string> ReadLinesShared(string path)
    {
        StreamReader? sr = null;
        try { sr = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)); }
        catch { yield break; }
        using (sr)
        {
            string? ln;
            while ((ln = sr.ReadLine()) != null) yield return ln;
        }
    }

    // A1 (Recheck): belegte, ABGESPIELTE Enderreichung. Es muss ein Event existieren, dessen Marktzeit das Fensterende
    // erreicht (innerhalb der geladenen Spanne [Ende, Ende+loadMargin]) UND dessen Empfangszeit tRecv NACH Play liegt.
    // Damit zaehlt weder Preload/Historie (tRecv vor Play) noch ein Live-Event (tMarket weit jenseits der Spanne).
    // Es werden ALLE Zeilen gescannt, damit ein einmal belegter Grenzuebertritt nicht durch spaetere Polls/letzte-Zeile
    // verloren geht. Kein Trade exakt an der Grenze noetig (dom/bbo/cumtrades genuegen).
    // A1 (Recheck2): Enderkennungs-Verdikt. tMarket/tRecv aus den Recorder-Dateien können abgespielte Strecke und
    // verspäteten Preload GRUNDSÄTZLICH NICHT unterscheiden (tRecv ist Empfangszeit, keine Replay-Herkunft).
    // Daher ist Erfolg NUR aus einem VERIFIZIERTEN, auftrag-/instanzgebundenen Replay-Fortschrittssignal ableitbar.
    // Fehlt dieses Signal -> Unproven (kein Erfolg). Das schliesst falschen Erfolg aus; es ist KEINE fertige Enderkennung.
    internal enum EndVerdict { Unproven, Reached, StoppedBeforeEnd }

    // 'verifiedReplayPosUtc' ist die vom Replay-Transport selbst gemeldete, dem Auftrag zugeordnete Wiedergabeposition
    // (Live-Signal). NULL = kein verifiziertes Signal verfügbar (z.B. offline / nicht verdrahtet) -> Unproven.
    internal static EndVerdict AssessEnd(DateTime? verifiedReplayPosUtc, bool replayPlayingObserved, DateTime winEndUtc)
    {
        if (verifiedReplayPosUtc == null || !replayPlayingObserved) return EndVerdict.Unproven;
        return verifiedReplayPosUtc.Value.ToUniversalTime() >= winEndUtc.ToUniversalTime()
            ? EndVerdict.Reached : EndVerdict.StoppedBeforeEnd;
    }

    // Reine DATEN-Querprüfung (nur Protokoll, NIE Erfolgsgrund): gibt es ein Grenzevent im TATSÄCHLICH geladenen
    // Bereich [Ende, loadedEnd] mit tRecv>=Play? Bewusst KEIN Herkunftsbeweis (Preload kann dasselbe erfüllen).
    internal static bool PlayedBoundaryDataHint(string dir, DateTime playInvokeUtc, DateTime winEndUtc, DateTime loadedEndUtc)
    {
        var lo = winEndUtc.ToUniversalTime(); var hi = loadedEndUtc.ToUniversalTime(); var pi = playInvokeUtc.ToUniversalTime();
        foreach (var s in new[] { "dom.ndjson", "bbo.ndjson", "cumtrades.ndjson", "ticks.ndjson" })
        {
            var p = Path.Combine(dir, s);
            if (!File.Exists(p)) continue;
            foreach (var line in ReadLinesShared(p))
            {
                var tm = ParseDt(JsonTMarket(line)); var tr = ParseDt(JsonTRecv(line));
                if (tm == null || tr == null) continue;
                var tmu = tm.Value.ToUniversalTime();
                if (tmu < lo || tmu > hi) continue;
                if (tr.Value.ToUniversalTime() < pi) continue;
                return true;
            }
        }
        return false;
    }

    // Produktionsentscheidung, die AutoRun nutzt: der DATEN-Hinweis wird nur geloggt; das Verdikt kommt AUSSCHLIESSLICH
    // aus dem verifizierten Replay-Signal (AssessEnd). Fehlt es -> Unproven -> AutoRun leitet KEINEN Erfolg ab.
    internal static EndVerdict DecideEnd(string dir, DateTime playInvokeUtc, DateTime winEndUtc, DateTime loadedEndUtc,
                                         DateTime? verifiedReplayPosUtc, bool replayPlayingObserved, Action<string> log)
    {
        bool dataHint = PlayedBoundaryDataHint(dir, playInvokeUtc, winEndUtc, loadedEndUtc);
        var verdict = AssessEnd(verifiedReplayPosUtc, replayPlayingObserved, winEndUtc);
        log($"    [A1] dataHint(nur Protokoll)={dataHint} verifiedReplayPos={(verifiedReplayPosUtc?.ToString("HH:mm:ss") ?? "null")} playing={replayPlayingObserved} -> {verdict}");
        return verdict;
    }

    // Verifizierte, auftraggebundene Replay-END-Position. Der Replay-TRANSPORT exponiert KEINE Position via UIA
    // (Player ist Custom-gerendert, kein RangeValue, kein Zeit-Text). Deshalb wird das ENDE AUTHORITATIV aus dem
    // ATAS-Log abgeleitet: der engine-emittierte Abschluss-Zustandsuebergang NACH dem Play-Invoke (ohne von uns
    // ausgeloesten Stopp) setzt _verifiedReplayEndUtc = geladenes Fensterende (UTC, ebenfalls aus dem Log). Das ist
    // KEIN Tick-Stall, sondern ATAS' eigener Zustand, auftraggebunden ueber Log-Zeit(>Play) + geladenen Bereich.
    private static DateTime? _verifiedReplayEndUtc = null;
    private static DateTime? TryVerifiedReplayPositionUtc() => _verifiedReplayEndUtc;

    // Parst aus dem ATAS-Log den zuletzt GELADENEN Replay-Bereich (Log-Zeile 'Replay DS : Playing from <s> to <e>'),
    // mit Log-Zeitstempel (LOKAL) >= afterUtc. Die Bereichszeiten <s>,<e> sind bereits UTC (Marktzeit).
    private static (DateTime startUtc, DateTime endUtc)? ReplayLoadedRangeUtcFromLog(DateTime afterUtc)
    {
        var afterLocal = afterUtc.ToLocalTime();
        var rx = new System.Text.RegularExpressions.Regex(
            @"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}),\d+.*Playing from (?<s>\d{2}\.\d{2}\.\d{4} \d{2}:\d{2}:\d{2}) to (?<e>\d{2}\.\d{2}\.\d{4} \d{2}:\d{2}:\d{2})");
        var lines = TailLines(AtasLogPath(), 2_000_000);
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var m = rx.Match(lines[i].Trim());
            if (!m.Success) continue;
            if (!DateTime.TryParseExact(m.Groups["ts"].Value, "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var ts)) continue;
            if (ts < afterLocal.AddSeconds(-2)) continue;
            if (DateTime.TryParseExact(m.Groups["s"].Value, "dd.MM.yyyy HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var s)
             && DateTime.TryParseExact(m.Groups["e"].Value, "dd.MM.yyyy HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var e))
                return (DateTime.SpecifyKind(s, DateTimeKind.Utc), DateTime.SpecifyKind(e, DateTimeKind.Utc));
        }
        return null;
    }

    // Engine-emittierter Replay-ABSCHLUSS im Log NACH afterUtc (Log-Zeit lokal). Wird im Play-Loop geprueft, BEVOR wir
    // selbst pausieren/verlassen -> ein Treffer ist der AUTONOME (natuerliche) Abschluss, kein von uns ausgeloester Stopp.
    private static bool ReplayEngineFinishedAfter(DateTime afterUtc)
    {
        var afterLocal = afterUtc.ToLocalTime();
        var rxTs = new System.Text.RegularExpressions.Regex(@"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}),\d+");
        foreach (var raw in TailLines(AtasLogPath(), 2_000_000))
        {
            var line = raw.Trim();
            var m = rxTs.Match(line);
            if (!m.Success) continue;
            if (!DateTime.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var ts)) continue;
            if (ts < afterLocal.AddSeconds(-1)) continue;
            if (line.Contains("Data processing finished")
                || line.Contains("State changed Started -> Stopped")
                || line.Contains("Playing historical data stopped")
                || line.Contains("Play -> Stopped") || line.Contains("-> Finished"))
                return true;
        }
        return false;
    }

    // Klassifikation des Replay-Endes EINER Ausfuehrung. Erwartet die Log-Zeilen AB der eigenen 'Playing from'-
    // Startzeile dieses Auftrags (execution-scoped) + ob die Recorder-Marktzeit das geladene Ende erreichte.
    //
    // ERFOLG (NaturalEnd) braucht eine BELEGTE ABSCHLUSSSEQUENZ der aktuellen Ausfuehrung:
    //   completion = '-> Stopped' (Started->Stopped ODER Play->Stopped) UND 'Data processing finished'.
    // Zusaetzlich muss das Ende tatsaechlich erreicht sein (Enderreichung), belegt durch:
    //   - Auto-Loop auf DENSELBEN geladenen Bereich (2. 'Playing from' mit identischem [s,e]) — ATAS rewinded nur am Ende; ODER
    //   - UNTERSTUETZEND die Recorder-Marktzeit am geladenen Ende (dataReachedLoadedEnd) — NIE allein ausreichend.
    // KEIN natuerliches Ende bei: Fehler (Errored), Verbindungsabbruch (Disconnected), manuellem Wechsel zu Realtime
    // ('to online state' -> StoppedEarly), Reset/Restart auf einen ANDEREN Bereich (2. 'Playing from' != Startbereich),
    // sowie bei Auto-Rewind/2. 'Playing from' OHNE Abschlusssequenz (dann NotFinished).
    internal enum ReplayEndClass { NoStart, NotFinished, Disconnected, Errored, StoppedEarly, NaturalEnd }
    internal static ReplayEndClass ClassifyExecutionEnd(IReadOnlyList<string> executionLines, bool dataReachedLoadedEnd)
    {
        if (executionLines == null || executionLines.Count == 0) return ReplayEndClass.NoStart;
        var rxPF = new System.Text.RegularExpressions.Regex(@"Playing from (?<s>\d{2}\.\d{2}\.\d{4} \d{2}:\d{2}:\d{2}) to (?<e>\d{2}\.\d{2}\.\d{4} \d{2}:\d{2}:\d{2})");
        bool hasStart = false, sawStop = false, sawFinished = false, disconnect = false, errored = false, onlineExit = false;
        bool sameRangeRewind = false, otherRangeRestart = false; string? firstRange = null;
        foreach (var raw in executionLines)
        {
            var l = raw ?? "";
            var m = rxPF.Match(l);
            if (m.Success)
            {
                var range = m.Groups["s"].Value + "|" + m.Groups["e"].Value;
                if (!hasStart) { hasStart = true; firstRange = range; }
                else if (range == firstRange) sameRangeRewind = true;   // Auto-Loop auf DENSELBEN Bereich (ATAS rewinded nur am Ende)
                else otherRangeRestart = true;                          // Reload auf ANDEREN Bereich -> Reset/Restart, kein natuerliches Ende
            }
            if (l.Contains("to online state")) onlineExit = true;       // manueller 'Zur Echtzeit wechseln'
            if (l.Contains("Disconnect")) disconnect = true;            // Verbindungsabbruch
            if ((l.Contains("ERROR") && l.Contains("Replay")) || l.Contains("faulted") || l.Contains("Exception")) errored = true;
            if (l.Contains("State changed Started -> Stopped") || l.Contains("Play -> Stopped")) sawStop = true;
            if (l.Contains("Data processing finished")) sawFinished = true;
        }
        if (!hasStart) return ReplayEndClass.NoStart;
        if (errored) return ReplayEndClass.Errored;
        bool completion = sawStop && sawFinished;                       // BELEGTE Abschlusssequenz der aktuellen Ausfuehrung
        bool reachedEnd = sameRangeRewind || dataReachedLoadedEnd;      // Auto-Loop (gleicher Bereich) ODER unterstuetzend Recorder-Ende
        // NATUERLICHES ENDE hat Vorrang: Abschlusssequenz + Enderreichung (+ ggf. Auto-Rewind) == erreichtes Ende.
        // Ein Feed-/Cache-Disconnect im End-/Rewind-Zyklus disqualifiziert dann NICHT (er gehoert zum Rewind).
        if (completion && reachedEnd && !onlineExit && !(otherRangeRestart && !sameRangeRewind))
            return ReplayEndClass.NaturalEnd;
        // Sonst gilt: ein Disconnect OHNE erreichtes Ende ist ein echter Mid-Play-Abbruch (Fehlerpruefung bleibt scharf).
        if (disconnect) return ReplayEndClass.Disconnected;
        if (!completion) return ReplayEndClass.NotFinished;             // Rewind/2. 'Playing from' ODER Daten-Ende ALLEIN reichen NICHT
        if (onlineExit) return ReplayEndClass.StoppedEarly;             // zu Realtime gewechselt
        return ReplayEndClass.StoppedEarly;                             // Stop ohne Enderreichung / Reset auf anderen Bereich
    }

    // ===== Reine Helfer fuer Geschwindigkeit + Sammellauf (offline via 'regress' getestet) =====

    // Ruecklesung der gesetzten Geschwindigkeit akzeptieren: normalisiert '100', '100x', ' 100X ' gleich.
    internal static bool SpeedReadbackOk(string requested, string? readback)
    {
        if (string.IsNullOrWhiteSpace(readback) || string.IsNullOrWhiteSpace(requested)) return false;
        static string N(string s) { s = s.Trim().ToLowerInvariant(); return s.EndsWith("x") ? s : s + "x"; }
        return N(requested) == N(readback);
    }

    // '100x' -> 100.0 (nur der Zahlwert; fuer die Watchdog-Berechnung). Ohne Zahl -> 1.0.
    internal static double SpeedToFactor(string? speedName)
    {
        var m = Regex.Match(speedName ?? "", @"(\d+(\.\d+)?)");
        return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0 ? d : 1.0;
    }

    // Wiedergabe-Watchdog (Wandzeit): geschwindigkeits-UNABHAENGIGER Boden (Laden/Flush/Abschluss sind I/O-gebunden)
    // PLUS Marktanteil/Geschwindigkeit. NIEMALS bloss Fensterdauer/Geschwindigkeit (sonst zu knapp fuer Flush bei 100x).
    internal static int PlayWatchdogSeconds(int windowSeconds, double speed, int loadFlushFloorSeconds = 200)
    {
        double s = speed <= 0 ? 1.0 : speed;
        if (windowSeconds < 0) windowSeconds = 0;
        return loadFlushFloorSeconds + (int)Math.Ceiling(windowSeconds / s);
    }

    // Stabile, deterministische Auftrags-ID aus Session+Jahr+UTC-Fenster -> Wiederanlauf-Dedup matcht exakt.
    internal static string BuildJobId(string session, int year, DateTime startUtc, DateTime endUtc)
        => $"{session}_{year}_{startUtc:yyyyMMddTHHmmssZ}_{endUtc:yyyyMMddTHHmmssZ}";

    // Noch offene Auftraege (Reihenfolge erhalten): alle, die nicht bereits committed sind.
    internal static List<string> JobsToRun(IReadOnlyList<string> allJobIds, ISet<string> committedJobIds)
        => allJobIds.Where(j => !committedJobIds.Contains(j)).ToList();

    internal static bool CollectionComplete(IReadOnlyList<string> allJobIds, ISet<string> committedJobIds)
        => allJobIds.Count > 0 && allJobIds.All(committedJobIds.Contains);

    // Europe/Berlin Wandzeit -> UTC via TimeZoneInfo (korrekte DST-Behandlung; KEIN fest verdrahteter +1h-Offset).
    private static DateTime BerlinToUtc(DateTime berlinWall)
    {
        foreach (var id in new[] { "W. Europe Standard Time", "Europe/Berlin" })
        {
            try { var tz = TimeZoneInfo.FindSystemTimeZoneById(id); return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(berlinWall, DateTimeKind.Unspecified), tz); }
            catch { }
        }
        return berlinWall;   // Fallback: keine Umrechnung moeglich (wird im Header als '=UTC?' kenntlich)
    }

    // Sichert den EXAKTEN Zustand eines fehlgeschlagenen Ladeversuchs (ausgewaehltes Modul, frisch aufgeloester
    // PlayButton samt IsEnabled/HelpText, sichtbarer Panel-Status, nur die Logzeilen DIESES Ladeversuchs). Keine Spekulation.
    private static void CaptureLoadState(Action<string> W, int logBaseIdx, List<string> cands)
    {
        var pb = FindIdAny("PlayButton");
        var rw = ReplayWindow();
        W("[7-DIAG] Zustand dieses Ladeversuchs:");
        W($"   Modul gewaehlt (='Datum auswählen' aktiv): {IsEnabledSafe(FindBtnAny("Datum auswählen"))}");
        W($"   PlayButton: gefunden={pb != null} IsEnabled={IsEnabledSafe(pb)} Help='{Safe(() => pb?.Current.HelpText)}' rect={Rect(pb)}");
        W($"   Panel-Titel='{Safe(() => rw?.Current.Name ?? "(kein Transport)")}'  Panel-Status='{(rw != null ? ReplayStatusText(rw) : "-")}'");
        W($"   neue gesperrte MNQ-Instanz(en): {cands.Count} [{string.Join(",", cands)}]");
        if (rw != null)
        {
            W("   Transport-Buttons (frisch):");
            foreach (var b in FindInRoots(new[] { rw }, ControlType.Button))
            { var n = Safe(() => b.Current.Name); var h = Safe(() => b.Current.HelpText); if (!string.IsNullOrEmpty(n) || !string.IsNullOrEmpty(h)) W($"      Name='{Trunc(n, 24)}' Help='{Trunc(h, 28)}' enabled={IsEnabledSafe(b)}"); }
        }
        W("   Logzeilen dieses Ladeversuchs (execution-scoped):");
        var sl = ExecutionLogSlice(logBaseIdx);
        if (sl.Count == 0) foreach (var ln in TailLines(AtasLogPath(), 4_000_000).Skip(Math.Max(0, logBaseIdx)).Where(l => l.Contains("Replay") || l.Contains("chart state")).Take(40)) W("      " + ln.Trim());
        else foreach (var ln in sl.Take(50)) W("      " + ln.Trim());
    }

    // TIMING: Der End-Disconnect trifft VOR den Abschlusszeilen ein (beobachtet: Disconnect t, Abschluss t+2s, Rewind t+4s),
    // und die Wiedergabe laeuft nach einem Disconnect oft WEITER bis zum geplanten Ende. Deshalb ist ein Disconnect NUR
    // ein Hinweis - KEINE feste Abbruch-Uhr. Die Entscheidung je Poll:
    //  - NaturalEnd (auftraggebundenes Endsignal: Abschluss + Enderreichung)       -> Success
    //  - Errored (echter Fehler)                                                   -> Abort
    //  - Ende erreicht, aber kein Abschluss innerhalb settleLimit-Stillstandspolls -> Abort (KORROBORATION, KEIN Erfolg)
    //  - Ende NICHT erreicht und echter Stillstand (stall >= stallFailLimit)       -> Abort (begrenzt fehlschlagen)
    //  - sonst (Fortschritt laeuft, auch bei Disconnect-Hinweis)                   -> Continue (bis Ende + Puffer)
    // Erfolg NUR ueber das Endsignal; Tickfortschritt/-zahl allein loesen nie Erfolg aus. Fehler/manueller Stopp -> nie Success.
    internal enum LoopAction { Continue, Success, Abort }
    internal static LoopAction PlayLoopStep(ReplayEndClass endClass, bool dataReachedEnd, int stall, int stallFailLimit, int settleLimit)
    {
        if (endClass == ReplayEndClass.NaturalEnd) return LoopAction.Success;
        if (endClass == ReplayEndClass.Errored) return LoopAction.Abort;
        if (dataReachedEnd && stall >= settleLimit) return LoopAction.Abort;
        if (!dataReachedEnd && stall >= stallFailLimit) return LoopAction.Abort;
        return LoopAction.Continue;
    }

    // Log-Zeilen AB der eigenen 'Playing from'-Startzeile dieses Auftrags (erste 'Playing from' ab baseLineIdx).
    private static List<string> ExecutionLogSlice(int baseLineIdx)
    {
        var all = TailLines(AtasLogPath(), 4_000_000);
        int start = -1;
        for (int i = Math.Max(0, baseLineIdx); i < all.Length; i++) if (all[i].Contains("Playing from")) { start = i; break; }
        var res = new List<string>();
        if (start < 0) return res;
        for (int i = start; i < all.Length; i++) res.Add(all[i]);
        return res;
    }
    private static string ReplayStatusText(AutomationElement rw)
    {
        try
        {
            foreach (AutomationElement t in rw.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)))
            {
                var n = Safe(() => t.Current.Name);
                if (!string.IsNullOrEmpty(n) && (n.Contains("Play", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("Wiedergabe", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("laden", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("Drücken", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("Datum", StringComparison.OrdinalIgnoreCase)))
                    return n;
            }
        }
        catch { }
        return "";
    }
    private static string ReadAllShared(string path) { try { using var sr = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)); return sr.ReadToEnd(); } catch { return "(nicht lesbar)"; } }
    private static string? FirstLine(string path) { try { using var sr = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)); return sr.ReadLine(); } catch { return null; } }
    private static string FirstCsvData(string path) { try { using var sr = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)); sr.ReadLine(); return sr.ReadLine() ?? ""; } catch { return ""; } }

    private static AutomationElement? FindMenuItemByName(Regex rx)
    {
        foreach (var ct in new[] { ControlType.MenuItem, ControlType.ListItem })
            try
            {
                foreach (AutomationElement mi in AutomationElement.RootElement.FindAll(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ct)))
                {
                    var n = Safe(() => mi.Current.Name);
                    if (!string.IsNullOrEmpty(n) && rx.IsMatch(n)) return mi;
                }
            }
            catch { }
        return null;
    }


    private static long FileLen(string dir, string f)
    { try { var p = Path.Combine(dir, f); return File.Exists(p) ? new FileInfo(p).Length : 0; } catch { return 0; } }

    // Erste/letzte Marktzeit aus ticks.csv (Spalte tMarket). Tolerant gegenueber Spaltenlage.
    private static (DateTime? first, DateTime? last) MarketTimeSpan(string ticksCsv)
    {
        try
        {
            if (!File.Exists(ticksCsv)) return (null, null);
            using var fs = new FileStream(ticksCsv, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs);
            string? header = sr.ReadLine();
            if (header == null) return (null, null);
            var cols = header.Split(',');
            int idx = Array.FindIndex(cols, c => c.Trim().Equals("tMarket", StringComparison.OrdinalIgnoreCase));
            if (idx < 0) idx = Array.FindIndex(cols, c => c.Trim().ToLowerInvariant().Contains("market"));
            if (idx < 0) return (null, null);
            string? firstLine = sr.ReadLine();
            if (firstLine == null) return (null, null);
            DateTime? first = ParseDt(Field(firstLine, idx));
            // letzte Zeile effizient vom Ende lesen
            string? last = ReadLastLine(ticksCsv);
            DateTime? lastDt = last != null ? ParseDt(Field(last, idx)) : null;
            return (first, lastDt ?? first);
        }
        catch { return (null, null); }
    }

    private static string Field(string line, int idx)
    { var p = line.Split(','); return idx < p.Length ? p[idx].Trim().Trim('"') : ""; }

    private static DateTime? ParseDt(string s)
    { return DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : (long.TryParse(s, out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : (DateTime?)null); }

    private static string? ReadLastLine(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length == 0) return null;
            long pos = fs.Length - 1; int toRead = (int)Math.Min(8192, fs.Length);
            fs.Seek(-toRead, SeekOrigin.End);
            var buf = new byte[toRead];
            int n = fs.Read(buf, 0, toRead);
            var text = Encoding.UTF8.GetString(buf, 0, n).TrimEnd('\r', '\n');
            int nl = text.LastIndexOf('\n');
            return nl >= 0 ? text[(nl + 1)..] : text;
        }
        catch { return null; }
    }

    private sealed record RecInfo(string Name, DateTime Meta, long TicksCsv, string Instrument, string StartWall);

    private static List<RecInfo> SnapshotRecordings()
    {
        var list = new List<RecInfo>();
        if (!Directory.Exists(RecordingsDir)) return list;
        foreach (var d in Directory.GetDirectories(RecordingsDir))
        {
            try
            {
                var meta = Path.Combine(d, "meta.json");
                var ticks = Path.Combine(d, "ticks.csv");
                DateTime mt = File.Exists(meta) ? File.GetLastWriteTime(meta) : Directory.GetLastWriteTime(d);
                long tb = File.Exists(ticks) ? new FileInfo(ticks).Length : 0;
                string instr = "", sw = "";
                if (File.Exists(meta))
                {
                    var txt = File.ReadAllText(meta);
                    instr = Match(txt, "\"instrument\"\\s*:\\s*\"([^\"]*)\"");
                    sw = Match(txt, "\"startWallUtc\"\\s*:\\s*\"([^\"]*)\"");
                }
                list.Add(new RecInfo(Path.GetFileName(d), mt, tb, instr, sw));
            }
            catch { }
        }
        return list;
    }

    private static string Match(string s, string pat)
    { var m = Regex.Match(s, pat); return m.Success ? m.Groups[1].Value : ""; }

    private static AutomationElement? FindByAutomationId(AutomationElement root, string id)
    {
        try { return root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id)); }
        catch { return null; }
    }

    private static AutomationElement? FindButtonByName(AutomationElement root, string name)
    {
        try
        {
            return root.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, name)));
        }
        catch { return null; }
    }

    private static AutomationElement? FindButtonByNameRegex(AutomationElement root, Regex rx)
    {
        try
        {
            foreach (AutomationElement b in root.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
            {
                var n = Safe(() => b.Current.Name);
                if (!string.IsNullOrEmpty(n) && rx.IsMatch(n)) return b;
            }
        }
        catch { }
        return null;
    }

    private static bool Invoke(AutomationElement el)
    {
        try
        {
            if (el.TryGetCurrentPattern(InvokePattern.Pattern, out var p)) { ((InvokePattern)p).Invoke(); return true; }
            if (el.TryGetCurrentPattern(TogglePattern.Pattern, out var tp)) { ((TogglePattern)tp).Toggle(); return true; }
        }
        catch (Exception ex) { Console.Error.WriteLine($"Invoke-Fehler: {ex.Message}"); }
        return false;
    }

    private static string PatInfo(AutomationElement el) => "pat=[" + string.Join(",", SupportedPatterns(el)) + "]";
    private static string Safe(Func<string?> f) { try { return f() ?? ""; } catch { return ""; } }
    private static void Flush(StringBuilder sb, string? outFile)
    { if (outFile != null) { try { File.WriteAllText(outFile, sb.ToString()); Console.WriteLine($"[geschrieben: {outFile}]"); } catch { } } }

    private static List<string> SupportedPatterns(AutomationElement el)
    {
        var res = new List<string>();
        try
        {
            foreach (var p in el.GetSupportedPatterns())
            {
                var n = p.ProgrammaticName; // z.B. "InvokePatternIdentifiers.Pattern"
                var shortName = n.Replace("PatternIdentifiers.Pattern", "").Replace("Identifiers.Pattern", "");
                res.Add(shortName);
            }
        }
        catch { }
        return res;
    }

    private static string Rect(AutomationElement el)
    {
        try
        {
            var r = el.Current.BoundingRectangle;
            if (r.IsEmpty) return "empty";
            return $"{(int)r.X},{(int)r.Y},{(int)r.Width}x{(int)r.Height}";
        }
        catch { return "?"; }
    }

    private static bool SameRuntimeId(AutomationElement a, AutomationElement b)
    {
        try { return Automation.Compare(a, b); } catch { return false; }
    }

    private static string Trunc(string s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");
}
