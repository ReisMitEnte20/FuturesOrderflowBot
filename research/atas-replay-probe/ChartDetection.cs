using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using OrderFlowTools;

namespace AtasReplayProbe;

// Zielchart-Erkennung und rein lesende Chart-Diagnose.
//
// Vorher (bis 1.3.0-pre1): genau EIN UIA-Pane mit Name == "VulkanChartWpf" (> 300x200) unter den Top-Level-Fenstern von
// OFT.Platform. Gebunden an (a) den Namen der Vulkan-Renderflaeche, (b) ControlType Pane, (c) UIA-Sichtbarkeit aller
// ATAS-Fenster fuer diesen Prozess (gleiche Sitzung, gleiche Integritaetsstufe), (d) still verschluckte UIA-Fehler, die
// als "0 Treffer" erschienen. Jede dieser Annahmen kann auf einem anderen Rechner falsch sein, ohne dass die Meldung
// zeigt, welche.
//
// Jetzt: Kandidaten werden mit allen Merkmalen erfasst (Name, Klasse, AutomationId, Typ, Sichtbarkeit, Fehler) und von
// einer reinen, getesteten Funktion bewertet: zuerst exakt "VulkanChartWpf" (bisheriges Verhalten), sonst Flaechen,
// deren Name/Klasse/AutomationId auf eine Chart-Renderflaeche hinweist. Nur GENAU EINE sichtbare Flaeche gilt als
// bestaetigt; keine Auswahl nach Groesse oder Reihenfolge. Jeder Fehlschlag nennt die Ursache und schreibt die
// Diagnose ins Protokoll.
internal static partial class Program
{
    internal const string VulkanSurfaceName = "VulkanChartWpf";
    private const int MinChartW = 300, MinChartH = 200;

    // Was ein Chart-Kandidat ist (aus UIA gelesen; in Tests synthetisch).
    internal sealed record ChartCand(
        string Name, string ClassName, string AutomationId, string ControlType, string FrameworkId,
        double X, double Y, double W, double H, bool Offscreen, string Window, bool WindowMinimized);

    internal enum ChartVerdictKind { Unique, NoneFound, Multiple, OnlyHidden }

    internal sealed record ChartVerdict(ChartVerdictKind Kind, ChartCand? Chosen, string Signal, IReadOnlyList<ChartCand> Considered, string Reason);

    // Typische Teile der Oberflaeche, die "Chart" im Namen tragen, aber keine Renderflaeche sind.
    private static readonly Regex NonSurface = new(@"(?i)(trader|toolbar|tool|tab|menu|list|header|button|scroll|legend|template|settings|panel(?!e)|ribbon|dom\b)", RegexOptions.Compiled);
    private static readonly Regex ChartWord = new(@"(?i)chart", RegexOptions.Compiled);
    private static readonly HashSet<string> SurfaceTypes = new(StringComparer.OrdinalIgnoreCase)
        { "Pane", "Custom", "Window", "Image", "Group", "Document" };

    internal static bool IsVulkanSurface(ChartCand c) => c.Name == VulkanSurfaceName;

    internal static bool IsChartLikeSurface(ChartCand c)
    {
        if (!SurfaceTypes.Contains(c.ControlType)) return false;
        foreach (var s in new[] { c.Name, c.ClassName, c.AutomationId })
            if (!string.IsNullOrEmpty(s) && ChartWord.IsMatch(s) && !NonSurface.IsMatch(s)) return true;
        return false;
    }

    internal static bool IsUsable(ChartCand c) =>
        !c.Offscreen && !c.WindowMinimized && c.W > MinChartW && c.H > MinChartH && c.Y > -10000 && c.X > -10000;

    private static bool Contains(ChartCand outer, ChartCand inner) =>
        !ReferenceEquals(outer, inner)
        && inner.X >= outer.X - 1 && inner.Y >= outer.Y - 1
        && inner.X + inner.W <= outer.X + outer.W + 1 && inner.Y + inner.H <= outer.Y + outer.H + 1
        && (inner.W < outer.W - 1 || inner.H < outer.H - 1);

    // Verschachtelte Treffer (Container um die eigentliche Flaeche) auf die innersten reduzieren; identische Rechtecke
    // (dieselbe Flaeche ueber zwei UIA-Knoten) zusammenfassen.
    private static List<ChartCand> Innermost(List<ChartCand> l)
    {
        var inner = l.Where(c => !l.Any(d => Contains(c, d))).ToList();
        return inner.GroupBy(c => (Math.Round(c.X), Math.Round(c.Y), Math.Round(c.W), Math.Round(c.H))).Select(g => g.First()).ToList();
    }

    /// <summary>Reine Entscheidung: genau eine sichtbare Renderflaeche => Unique. Keine Auswahl nach Groesse/Reihenfolge.</summary>
    internal static ChartVerdict SelectChart(IReadOnlyList<ChartCand> all)
    {
        var vulkan = all.Where(IsVulkanSurface).ToList();
        var vUse = Innermost(vulkan.Where(IsUsable).ToList());
        if (vUse.Count == 1) return new(ChartVerdictKind.Unique, vUse[0], "Name=" + VulkanSurfaceName, vulkan, "genau eine sichtbare Vulkan-Chartflaeche");
        if (vUse.Count > 1) return new(ChartVerdictKind.Multiple, null, "Name=" + VulkanSurfaceName, vUse, $"{vUse.Count} sichtbare Vulkan-Chartflaechen");

        var like = all.Where(c => !IsVulkanSurface(c) && IsChartLikeSurface(c)).ToList();
        var lUse = Innermost(like.Where(IsUsable).ToList());
        if (lUse.Count == 1)
        {
            var c = lUse[0];
            string sig = ChartWord.IsMatch(c.Name) ? $"Name='{c.Name}'" : ChartWord.IsMatch(c.ClassName) ? $"Klasse='{c.ClassName}'" : $"AutomationId='{c.AutomationId}'";
            return new(ChartVerdictKind.Unique, c, sig, like, "keine Vulkan-Flaeche, aber genau eine sichtbare Chart-Renderflaeche (" + sig + ")");
        }
        if (lUse.Count > 1) return new(ChartVerdictKind.Multiple, null, "Chart-Name/Klasse/AutomationId", lUse, $"{lUse.Count} sichtbare Chart-Renderflaechen");

        var hidden = vulkan.Concat(like).ToList();
        if (hidden.Count > 0)
            return new(ChartVerdictKind.OnlyHidden, null, "", hidden,
                $"{hidden.Count} Chart-Flaeche(n) vorhanden, aber nicht nutzbar sichtbar (minimiert, verdeckt/ausserhalb des Bildschirms oder kleiner als {MinChartW}x{MinChartH})");
        return new(ChartVerdictKind.NoneFound, null, "", Array.Empty<ChartCand>(), "keine Chart-Renderflaeche im UIA-Baum der ATAS-Fenster");
    }

    // ------------------------------------------------------------------ UIA-Erfassung (liest nur)

    internal sealed class ChartScan
    {
        public List<ChartCand> Candidates { get; } = new();
        public List<string> Errors { get; } = new();
        public int Roots;
        public int AtasProcesses;
        public List<string> RootNames { get; } = new();
    }

    private static readonly Condition SurfaceCondition = new OrCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Pane),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Custom),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Image),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group),
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));

    internal static ChartScan ScanChartCandidates()
    {
        var scan = new ChartScan();
        var procs = Process.GetProcessesByName("OFT.Platform");
        scan.AtasProcesses = procs.Length;
        var roots = AtasRoots();
        scan.Roots = roots.Count;
        foreach (var r in roots)
        {
            string wn = Safe(() => r.Current.Name);
            bool minimized = false;
            try { if (r.TryGetCurrentPattern(WindowPattern.Pattern, out var wp)) minimized = ((WindowPattern)wp).Current.WindowVisualState == WindowVisualState.Minimized; }
            catch (Exception ex) { scan.Errors.Add($"Fensterzustand '{wn}': {ex.GetType().Name}: {ex.Message}"); }
            scan.RootNames.Add(wn + (minimized ? " [minimiert]" : ""));
            AutomationElementCollection? col = null;
            try { col = r.FindAll(TreeScope.Descendants, SurfaceCondition); }
            catch (Exception ex) { scan.Errors.Add($"UIA-Suche in '{wn}': {ex.GetType().Name}: {ex.Message}"); }
            if (col == null) continue;
            foreach (AutomationElement e in col)
            {
                try
                {
                    var cur = e.Current;
                    string nm = cur.Name ?? "", cls = cur.ClassName ?? "", aid = cur.AutomationId ?? "";
                    var cand = new ChartCand(nm, cls, aid, cur.ControlType.ProgrammaticName.Replace("ControlType.", ""), cur.FrameworkId ?? "",
                        cur.BoundingRectangle.IsEmpty ? -1e9 : cur.BoundingRectangle.X, cur.BoundingRectangle.IsEmpty ? -1e9 : cur.BoundingRectangle.Y,
                        cur.BoundingRectangle.IsEmpty ? 0 : cur.BoundingRectangle.Width, cur.BoundingRectangle.IsEmpty ? 0 : cur.BoundingRectangle.Height,
                        cur.IsOffscreen, wn, minimized);
                    if (IsVulkanSurface(cand) || IsChartLikeSurface(cand)) scan.Candidates.Add(cand);
                }
                catch (Exception ex) { scan.Errors.Add($"UIA-Element in '{wn}': {ex.GetType().Name}: {ex.Message}"); if (scan.Errors.Count > 40) break; }
            }
        }
        return scan;
    }

    private static string Describe(ChartCand c) =>
        $"name='{c.Name}' klasse='{c.ClassName}' aid='{c.AutomationId}' typ={c.ControlType} fw={c.FrameworkId} " +
        $"rect={(int)c.X},{(int)c.Y},{(int)c.W}x{(int)c.H} offscreen={c.Offscreen} fenster='{c.Window}'{(c.WindowMinimized ? " [minimiert]" : "")}";

    // Beschreibung des bestaetigten Zielcharts fuer das Ergebnisprotokoll (statt fest "VulkanChartWpf-Pane").
    private static string _chartDesc = "(nicht geprueft)";

    // ------------------------------------------------------------------ Zielchart pruefen (Phase 1 jedes Auftrags)

    private static bool VerifyTargetChartUnique(Action<string> W)
    {
        string targetName = Environment.GetEnvironmentVariable("ATAS_TARGET_CHART") ?? "";
        if (!string.IsNullOrWhiteSpace(targetName))
        {
            var headers = FindInRoots(AtasRoots(), ControlType.Button)
                .Where(b => Safe(() => b.Current.Name).StartsWith(targetName, StringComparison.OrdinalIgnoreCase))
                .Select(b => { System.Windows.Rect r; try { r = b.Current.BoundingRectangle; } catch { r = System.Windows.Rect.Empty; } return r; })
                .Where(r => !r.IsEmpty).ToList();
            if (headers.Count != 1) { W($"[1] Zielchart '{targetName}' nicht eindeutig (Header-Treffer={headers.Count}) -> Abbruch."); return false; }
            _chartDesc = $"Header '{targetName}' (ATAS_TARGET_CHART)";
            W($"[1] Zielchart '{targetName}' eindeutig."); return true;
        }

        var scan = ScanChartCandidates();
        var v = SelectChart(scan.Candidates);
        if (v.Kind == ChartVerdictKind.Unique)
        {
            _chartDesc = $"{v.Signal} @{(int)v.Chosen!.X},{(int)v.Chosen.Y} {(int)v.Chosen.W}x{(int)v.Chosen.H} in '{v.Chosen.Window}'";
            W($"[1] Zielchart eindeutig bestaetigt: {v.Reason}: {Describe(v.Chosen)}");
            return true;
        }

        string why = v.Kind switch
        {
            ChartVerdictKind.Multiple => "MEHRERE Charts",
            ChartVerdictKind.OnlyHidden => "Chart vorhanden, aber nicht sichtbar",
            _ => scan.AtasProcesses == 0 ? "ATAS-Prozess nicht gefunden"
               : scan.Roots == 0 ? "ATAS laeuft, aber KEIN ATAS-Fenster per UI Automation sichtbar"
               : scan.Errors.Count > 0 ? "KEIN Chart gefunden (UIA-Zugriffsfehler, siehe unten)"
               : "KEIN Chart gefunden",
        };
        W($"[1] Zielchart nicht bestaetigt: {why} - {v.Reason}.");
        foreach (var c in v.Considered.Take(12)) W("    Kandidat: " + Describe(c));
        // Rein lesende Diagnose direkt ins Protokoll, damit die Ursache ohne weiteren Aufnahmeversuch sichtbar ist.
        try { WriteChartDiagnosis(W, scan); } catch (Exception ex) { W($"    [diag] Diagnose fehlgeschlagen: {ex.GetType().Name}: {ex.Message}"); }
        return false;
    }

    // ------------------------------------------------------------------ Diagnose (liest nur, klickt nichts)

    private static int ChartDiag(string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); if (outFile != null) { try { File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(true)); } catch { } } }
        W($"# ATAS Chart-Diagnose (nur lesen)  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  Probe {typeof(Program).Assembly.GetName().Version}");
        var scan = ScanChartCandidates();
        var v = SelectChart(scan.Candidates);
        W($"ERGEBNIS: {v.Kind}  ({v.Reason}){(v.Chosen != null ? " -> " + Describe(v.Chosen) : "")}");
        WriteChartDiagnosis(W, scan);
        Flush(sb, outFile);
        return v.Kind == ChartVerdictKind.Unique ? 0 : 5;
    }

    private static void WriteChartDiagnosis(Action<string> W, ChartScan scan)
    {
        W("  --- Umgebung");
        var me = Process.GetCurrentProcess();
        W($"  Probe: pid={me.Id} sitzung={me.SessionId} erhoeht={IsElevated(me.Handle, out _)} benutzer={Environment.UserName} os={Environment.OSVersion.VersionString}");
        W($"  Vulkan-Laufzeit (System32\\vulkan-1.dll): {(File.Exists(Path.Combine(Environment.SystemDirectory, "vulkan-1.dll")) ? "vorhanden" : "FEHLT")}");
        foreach (var d in DisplayAdapters()) W("  Grafikadapter: " + d);
        W($"  Bildschirm: {System.Windows.SystemParameters.PrimaryScreenWidth}x{System.Windows.SystemParameters.PrimaryScreenHeight} (logisch), Remotesitzung={GetSystemMetrics(0x1000) != 0}");

        W("  --- ATAS-Prozesse");
        var atas = Process.GetProcesses().Where(p =>
        {
            try
            {
                var n = p.ProcessName;
                return (n.StartsWith("OFT", StringComparison.OrdinalIgnoreCase) || n.Contains("ATAS", StringComparison.OrdinalIgnoreCase))
                    && !n.StartsWith("AtasReplayProbe", StringComparison.OrdinalIgnoreCase) && !n.StartsWith("AtasSessionCollector", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }).ToList();
        if (atas.Count == 0) W("  keiner (OFT.Platform laeuft nicht)");
        foreach (var p in atas)
        {
            string path; try { path = p.MainModule?.FileName ?? "?"; } catch (Exception ex) { path = $"(nicht lesbar: {ex.GetType().Name}: {ex.Message})"; }
            string elev = "?";
            try { var h = OpenProcess(0x1000, false, p.Id); if (h != IntPtr.Zero) { elev = IsElevated(h, out var err) ? "ja" : (err != "" ? "unbekannt (" + err + ")" : "nein"); CloseHandle(h); } else elev = "unbekannt (OpenProcess " + new Win32Exception(Marshal.GetLastWin32Error()).Message + ")"; } catch (Exception ex) { elev = "unbekannt (" + ex.Message + ")"; }
            W($"  {p.ProcessName} pid={p.Id} sitzung={SafeInt(() => p.SessionId)} erhoeht={elev} hauptfenster='{Safe(() => p.MainWindowTitle)}' pfad={path}");
        }

        W("  --- Top-Level-Fenster der ATAS-Prozesse (Win32)");
        var pids = Process.GetProcessesByName("OFT.Platform").Select(p => (uint)p.Id).ToHashSet();
        int visibleTop = 0;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (!pids.Contains(pid)) return true;
            bool vis = IsWindowVisible(h);
            if (vis) visibleTop++;
            GetWindowRect(h, out var rc);
            var kids = new Dictionary<string, int>();
            EnumChildWindows(h, (c, _) => { var k = ClassOf(c); kids[k] = kids.TryGetValue(k, out var n) ? n + 1 : 1; return true; }, IntPtr.Zero);
            if (vis || kids.Count > 0)
                W($"  hwnd=0x{h.ToInt64():X} klasse='{ClassOf(h)}' titel='{TitleOf(h)}' sichtbar={vis} minimiert={IsIconic(h)} rect={rc.L},{rc.T},{rc.R - rc.L}x{rc.B - rc.T} " +
                  $"kindfenster=[{string.Join(", ", kids.OrderByDescending(k => k.Value).Take(8).Select(k => $"{k.Key}x{k.Value}"))}]");
            return true;
        }, IntPtr.Zero);

        W("  --- UI Automation");
        W($"  ATAS-Prozesse={scan.AtasProcesses}  UIA-Fenster={scan.Roots}  (sichtbare Win32-Fenster={visibleTop})");
        foreach (var n in scan.RootNames) W($"  UIA-Fenster: '{n}'");
        if (scan.AtasProcesses > 0 && scan.Roots == 0 && visibleTop > 0)
            W("  HINWEIS: Fenster existieren, sind fuer UI Automation aber unsichtbar -> typischerweise laeuft ATAS mit Administratorrechten (erhoeht) " +
              "und der Collector nicht, oder beide laufen in verschiedenen Windows-Sitzungen.");
        foreach (var e in scan.Errors.Take(20)) W("  UIA-Fehler: " + e);
        W($"  Chart-Kandidaten ({scan.Candidates.Count}):");
        foreach (var c in scan.Candidates.Take(30)) W($"    {(IsVulkanSurface(c) ? "[vulkan]" : "[chart] ")}{(IsUsable(c) ? "[nutzbar]" : "[verdeckt/klein]")} " + Describe(c));

        // Bildflaechen ohne Chart-Namen: zeigt eine Renderflaeche unter unerwartetem Namen (nur grosse, sichtbare).
        W("  Grosse sichtbare Flaechen ohne Chart-Namen (Hinweis auf andere Renderflaeche):");
        int shown = 0;
        foreach (var r in AtasRoots())
        {
            AutomationElementCollection? col = null;
            try { col = r.FindAll(TreeScope.Descendants, SurfaceCondition); } catch { }
            if (col == null) continue;
            foreach (AutomationElement e in col)
            {
                try
                {
                    var cur = e.Current; var b = cur.BoundingRectangle;
                    if (b.IsEmpty || cur.IsOffscreen || b.Width < 400 || b.Height < 250) continue;
                    if (ChartWord.IsMatch((cur.Name ?? "") + (cur.ClassName ?? "") + (cur.AutomationId ?? ""))) continue;
                    W($"    name='{cur.Name}' klasse='{cur.ClassName}' aid='{cur.AutomationId}' typ={cur.ControlType.ProgrammaticName.Replace("ControlType.", "")} fw={cur.FrameworkId} rect={(int)b.X},{(int)b.Y},{(int)b.Width}x{(int)b.Height}");
                    if (++shown >= 25) break;
                }
                catch { }
            }
            if (shown >= 25) break;
        }
        if (shown == 0) W("    (keine)");

        W("  --- Recorder");
        var dll = SharedDataRoot.InstalledRecorderDll;
        W($"  {RecorderIdentity()}");
        W($"  gemeinsame Konfiguration: {SharedDataRoot.ConfigPath} -> '{SharedDataRoot.Read() ?? "(nicht gesetzt)"}'");

        W("  --- ATAS-Protokoll (Renderer/Grafik, letzte Eintraege)");
        try
        {
            var dir = Path.GetDirectoryName(AtasLogPath())!;
            var log = Directory.Exists(dir) ? Directory.GetFiles(dir, "app_*.log").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;
            if (log == null) W("  kein ATAS-Protokoll gefunden: " + dir);
            else
            {
                var lines = TailLines(log, 4 << 20);
                var gfx = lines.Where(l => Regex.IsMatch(l, @"(?i)vulkan|render|directx|opengl|gpu|graphic|swapchain")).ToList();
                // Haeufige Swapchain-Meldungen ausblenden; Initialisierung, Rueckfall und Fehler zeigen.
                var key = gfx.Where(l => !l.Contains("Swapchain recreated", StringComparison.OrdinalIgnoreCase)).ToList();
                var errs = lines.Where(l => Regex.IsMatch(l, @"(?i)\b(ERROR|FATAL)\b") && Regex.IsMatch(l, @"(?i)chart|render|vulkan|gpu|automation")).ToList();
                W($"  {Path.GetFileName(log)}: {gfx.Count} Grafikzeilen (davon {gfx.Count - key.Count} 'Swapchain recreated'), {errs.Count} passende Fehlerzeilen");
                // Welche Render-Engine ATAS fuer Charts gewaehlt hat (z. B. "Chart:Vulkan"); bestimmt den UIA-Namen der Flaeche.
                var engine = lines.LastOrDefault(l => l.Contains("Rendering engine", StringComparison.OrdinalIgnoreCase));
                W("  Render-Engine laut ATAS: " + (engine == null ? "(im Protokoll nicht gefunden)" : Trunc(engine.Trim(), 220)));
                foreach (var l in key.Skip(Math.Max(0, key.Count - 12))) W("    " + Trunc(l.Trim(), 220));
                foreach (var l in errs.Skip(Math.Max(0, errs.Count - 8))) W("    " + Trunc(l.Trim(), 220));
            }
        }
        catch (Exception ex) { W($"  Protokoll nicht lesbar: {ex.Message}"); }
    }

    // Tatsaechlich installierte Recorder-DLL statt eines fest eingebauten Versionstexts.
    internal static string RecorderIdentity()
    {
        var dll = SharedDataRoot.InstalledRecorderDll;
        if (!File.Exists(dll)) return $"Recorder-DLL nicht installiert ({dll})";
        try
        {
            string hash;
            using (var fs = File.OpenRead(dll)) hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs));
            var data = File.ReadAllBytes(dll);
            var text = Encoding.Unicode.GetString(data);
            var m = Regex.Match(text, @"OrderFlowReplayRecorder/(proto-[A-Za-z0-9\-]+)");
            string kennung = m.Success ? m.Groups[1].Value : "unbekannt";
            bool shared = SharedDataRoot.DllSupportsSharedRoot(dll);
            return $"installierte Recorder-DLL: Kennung '{kennung}', SHA-256 {hash}, {data.Length:N0} B, " +
                   (shared ? "liest gemeinsamen Datenordner" : "fester Ausgabepfad (aeltere Version)") + $", {dll}";
        }
        catch (Exception ex) { return $"Recorder-DLL nicht lesbar ({dll}): {ex.Message}"; }
    }

    // ------------------------------------------------------------------ Win32-Helfer (nur lesend)

    private delegate bool EnumWndProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWndProc cb, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWndProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] private struct CdRect { public int L, T, R, B; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out CdRect r);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int cls, out int info, int len, out int retLen);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice { public int cb; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string String; public int Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string? dev, int i, ref DisplayDevice d, int flags);

    private static string ClassOf(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, s.Capacity); return s.ToString(); }
    private static string TitleOf(IntPtr h) { var s = new StringBuilder(256); GetWindowText(h, s, s.Capacity); return s.ToString(); }
    private static int SafeInt(Func<int> f) { try { return f(); } catch { return -1; } }

    private static bool IsElevated(IntPtr proc, out string error)
    {
        error = "";
        if (!OpenProcessToken(proc, 0x0008 /*TOKEN_QUERY*/, out var tok)) { error = new Win32Exception(Marshal.GetLastWin32Error()).Message; return false; }
        try { return GetTokenInformation(tok, 20 /*TokenElevation*/, out int e, 4, out _) && e != 0; }
        finally { CloseHandle(tok); }
    }

    private static IEnumerable<string> DisplayAdapters()
    {
        var seen = new HashSet<string>();
        for (int i = 0; i < 16; i++)
        {
            var d = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, i, ref d, 0)) break;
            if (seen.Add(d.String)) yield return $"{d.String}{((d.Flags & 4) != 0 ? " (primaer)" : "")}";
        }
    }
}
