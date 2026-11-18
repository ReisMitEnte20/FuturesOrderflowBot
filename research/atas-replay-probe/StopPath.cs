using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace AtasReplayProbe;

// Stop-/Exit-Pfad NACH erkanntem Replay-Ende (VM-Befund 1.3.3: Ende erkannt, danach ATAS im Auto-Rewind-Loop, kein
// weiteres Protokoll). Ursachen im alten Pfad (ExitReplayView):
//  - jede Pruefung war eine UIA-Suche ueber ALLE ATAS-Fenster (FindAll Descendants). Waehrend 'Module laden' ist der
//    ATAS-UI-Thread beschaeftigt; solche Aufrufe koennen lange blockieren und hatten keine Zeitgrenze.
//  - 'Zur Echtzeit wechseln' wurde NUR per UIA gesucht. Beim custom-gerenderten Transport (VM) existiert er in UIA
//    nicht; danach folgte die Ribbon-Umschaltung, die die Replay-Ansicht nachweislich nicht verlaesst.
// Neu: Zustand ueber den Win32-Fenstertitel des Transports (blockiert nicht) und das ATAS-Protokoll; UIA nur mit
// Zeitgrenze; Bedienelement 'Zur Echtzeit wechseln' frisch per Tooltip bestaetigt; Erfolg NUR, wenn ATAS den Wechsel
// protokolliert UND danach kein weiterer Durchlauf beginnt. Gesamtdauer begrenzt.
internal static partial class Program
{
    // ------------------------------------------------------------------ begrenzte Aufrufe

    /// <summary>Fuehrt einen moeglicherweise blockierenden (UIA-)Aufruf mit Zeitgrenze aus und protokolliert Dauer/Ergebnis.</summary>
    internal static T Bounded<T>(string what, Func<T> f, int ms, T fallback, Action<string>? W = null)
    {
        var sw = Stopwatch.StartNew();
        var t = Task.Run(() => { try { return (ok: true, v: f()); } catch { return (ok: false, v: fallback); } });
        if (!t.Wait(ms))
        {
            W?.Invoke($"      [zeit] {what}: BLOCKIERT > {ms} ms -> abgebrochen (Ersatzwert)");
            return fallback;
        }
        if (sw.ElapsedMilliseconds > 500) W?.Invoke($"      [zeit] {what}: {sw.ElapsedMilliseconds} ms");
        return t.Result.ok ? t.Result.v : fallback;
    }

    // ------------------------------------------------------------------ Win32: Transportfenster (blockiert nicht)

    internal sealed record FloatWin(IntPtr Hwnd, string Title, System.Windows.Rect Rect);

    /// <summary>Schwebendes Replay-Transportfenster ('Replay - ...') ueber Win32 (Titel/Rechteck ohne UIA).</summary>
    internal static FloatWin? ReplayFloatWin32()
    {
        var pids = Process.GetProcessesByName("OFT.Platform").Select(p => (uint)p.Id).ToHashSet();
        FloatWin? hit = null;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (!pids.Contains(pid) || !IsWindowVisible(h)) return true;
            var t = TitleOf(h);
            if (t.StartsWith("Replay", StringComparison.OrdinalIgnoreCase))
            {
                GetWindowRect(h, out var rc);
                hit = new FloatWin(h, t, new System.Windows.Rect(rc.L, rc.T, rc.R - rc.L, rc.B - rc.T));
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return hit;
    }

    internal static bool TitleLoading(string t) => t.Contains("laden", StringComparison.OrdinalIgnoreCase) || t.Contains("Initializ", StringComparison.OrdinalIgnoreCase);
    internal static bool TitlePlaying(string t) => t.Contains("Historienwiedergabe", StringComparison.OrdinalIgnoreCase) && !t.Contains("angehalten", StringComparison.OrdinalIgnoreCase) && !t.Contains("gestoppt", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ Transportsymbole per Tooltip (nur Hover, kein Klick)

    internal sealed class TransportMap
    {
        public IntPtr Hwnd; public System.Windows.Rect WinRect;
        public List<(int x, int y, string tip)> Icons { get; } = new();
        public (int x, int y)? Find(Regex rx) { var i = Icons.FirstOrDefault(c => rx.IsMatch(c.tip)); return i.tip == null ? null : (i.x, i.y); }
    }

    private static TransportMap? _transportMap;
    internal static readonly Regex RxToRealtime = new(@"(?i)(echtzeit|real\s*-?time|live|online)", RegexOptions.Compiled);
    internal static readonly Regex RxLoop = new(@"(?i)(wiederhol|schleife|loop|repeat|endlos)", RegexOptions.Compiled);

    /// <summary>Symbole der Transportleiste finden (frischer Screenshot) und je Symbol den Tooltip lesen. Kein Klick.</summary>
    internal static TransportMap? ScanTransportTooltips(Action<string> W, int budgetMs = 20000)
    {
        var fw = ReplayFloatWin32();
        if (fw == null) { W("      [transport] kein 'Replay'-Fenster (Win32) -> keine Symbolsuche."); return null; }
        var map = new TransportMap { Hwnd = fw.Hwnd, WinRect = fw.Rect };
        var sw = Stopwatch.StartNew();
        try
        {
            ForceForegroundHwnd(fw.Hwnd);
            using var bmp = CaptureRect(fw.Rect);
            List<(int x0, int x1)> spans;
            try { spans = ScreenOcr.Recognize(bmp, 0, 0).Select(o => (o.X, o.X + o.W)).ToList(); } catch { spans = new(); }
            var blobs = FindIconBlobs(bmp, spans);
            foreach (var b in blobs)
            {
                if (sw.ElapsedMilliseconds > budgetMs) { W($"      [transport] Zeitbudget {budgetMs} ms erreicht - Suche beendet."); break; }
                int sx = (int)Math.Round(fw.Rect.X) + b.cx, sy = (int)Math.Round(fw.Rect.Y) + b.cy;
                SetCursorPos(sx, sy); Thread.Sleep(900);
                var tip = Bounded("Tooltip lesen", () => CurrentToolTip(), 1500, null) ?? "";
                map.Icons.Add((sx, sy, tip));
            }
            SetCursorPos((int)(fw.Rect.X + fw.Rect.Width / 2), (int)(fw.Rect.Y + fw.Rect.Height + 40));   // Zeiger aus der Leiste
        }
        catch (Exception ex) { W($"      [transport] Symbolsuche fehlgeschlagen: {ex.Message}"); }
        W($"      [transport] {map.Icons.Count} Symbole in {sw.ElapsedMilliseconds} ms: " +
          string.Join(" | ", map.Icons.Select(i => $"({i.x},{i.y}) '{(i.tip == "" ? "-" : Trunc(i.tip, 40))}'")));
        return map;
    }

    private static void ForceForegroundHwnd(IntPtr h) { try { ShowWindow(h, 5); BringWindowToTop(h); SetForegroundWindow(h); } catch { } }

    // ------------------------------------------------------------------ Schleifen-Option (nur mit lesbarem Zustand)

    /// <summary>Auto-Wiederholung pruefen und - nur wenn der Zustand per UIA LESBAR ist - vor Play ausschalten und zuruecklesen.</summary>
    internal static string EnsureLoopOff(Action<string> W, bool visualMode)
    {
        if (!visualMode)
        {
            var btn = Bounded("Loop-Schalter suchen", () => FindInRoots(AtasRoots(), ControlType.Button).Concat(FindInRoots(AtasRoots(), ControlType.CheckBox))
                .FirstOrDefault(b => RxLoop.IsMatch(Safe(() => b.Current.Name) + " " + Safe(() => b.Current.HelpText) + " " + Safe(() => b.Current.AutomationId))), 4000, null, W);
            if (btn == null) return "Schleifen-Schalter per UIA nicht gefunden - Zustand unbekannt, nicht veraendert";
            string Read() { try { return btn.TryGetCurrentPattern(TogglePattern.Pattern, out var p) ? ((TogglePattern)p).Current.ToggleState.ToString() : "nicht lesbar"; } catch { return "nicht lesbar"; } }
            var before = Bounded("Loop-Zustand lesen", Read, 2000, "nicht lesbar", W);
            if (before == "On")
            {
                Bounded("Loop ausschalten", () => { ((TogglePattern)btn.GetCurrentPattern(TogglePattern.Pattern)).Toggle(); return true; }, 2000, false, W);
                var after = Bounded("Loop-Zustand zuruecklesen", Read, 2000, "nicht lesbar", W);
                return after == "Off" ? "Auto-Wiederholung war AN -> ausgeschaltet und zurueckgelesen (Off)" : $"Ausschalten nicht bestaetigt (zurueckgelesen '{after}')";
            }
            return before == "Off" ? "Auto-Wiederholung ist AUS (zurueckgelesen)" : $"Schleifen-Schalter gefunden, Zustand '{before}' - nicht veraendert";
        }
        var tip = _transportMap?.Icons.FirstOrDefault(i => RxLoop.IsMatch(i.tip)).tip;
        return tip != null ? $"Schleifen-Symbol gefunden (Tooltip '{tip}'), Zustand visuell nicht sicher lesbar - NICHT veraendert"
                           : "kein Schleifen-Symbol mit Tooltip gefunden - Zustand unbekannt, nicht veraendert";
    }

    // ------------------------------------------------------------------ ATAS-Tastenkuerzel (aus der ATAS-Konfiguration gelesen)

    /// <summary>Tastenkuerzel aus %APPDATA%\ATAS\HotKeys\GlobalHotKeys.cnf, z. B. StopMarketReplay = LeftCtrl+LeftAlt+D.</summary>
    internal static List<string>? ReadAtasHotkey(string name, string? path = null)
    {
        path ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ATAS", "HotKeys", "GlobalHotKeys.cnf");
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty(name, out var arr) || arr.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
            var keys = arr.EnumerateArray().Select(e => e.GetString() ?? "").Where(k => k != "").ToList();
            return keys.Count > 0 ? keys : null;
        }
        catch { return null; }
    }

    /// <summary>Tastennamen (WPF Key) -> virtuelle Tastencodes; null, wenn eine Taste nicht sicher abbildbar ist.</summary>
    internal static List<byte>? HotkeyToVk(IReadOnlyList<string> keys)
    {
        var res = new List<byte>();
        foreach (var k in keys)
        {
            byte vk = k switch
            {
                "LeftCtrl" or "RightCtrl" or "Ctrl" => 0x11,
                "LeftAlt" or "RightAlt" or "Alt" => 0x12,
                "LeftShift" or "RightShift" or "Shift" => 0x10,
                "Up" => 0x26, "Down" => 0x28, "Left" => 0x25, "Right" => 0x27, "Space" => 0x20, "Pause" => 0x13,
                _ when k.Length == 1 && char.IsLetterOrDigit(k[0]) => (byte)char.ToUpperInvariant(k[0]),
                _ when k.Length == 2 && k[0] == 'D' && char.IsDigit(k[1]) => (byte)k[1],
                _ when k.Length >= 2 && k[0] == 'F' && int.TryParse(k[1..], out var fn) && fn >= 1 && fn <= 12 => (byte)(0x70 + fn - 1),
                _ => 0,
            };
            if (vk == 0) return null;
            res.Add(vk);
        }
        return res;
    }

    private static IntPtr AtasMainHwnd()
    {
        var pids = Process.GetProcessesByName("OFT.Platform").Select(p => (uint)p.Id).ToHashSet();
        IntPtr hit = IntPtr.Zero;
        EnumWindows((h, _) => { GetWindowThreadProcessId(h, out uint pid); if (pids.Contains(pid) && IsWindowVisible(h) && TitleOf(h).StartsWith("ATAS", StringComparison.OrdinalIgnoreCase)) { hit = h; return false; } return true; }, IntPtr.Zero);
        return hit;
    }

    /// <summary>Kuerzel an ATAS senden (Hauptfenster vorher in den Vordergrund). true = Tasten gesendet (KEIN Wirkungsnachweis).</summary>
    private static bool SendAtasHotkey(string name, Action<string> W)
    {
        var keys = ReadAtasHotkey(name);
        if (keys == null) { W($"      [taste] '{name}' in ATAS nicht belegt."); return false; }
        var vks = HotkeyToVk(keys);
        if (vks == null) { W($"      [taste] '{name}' = {string.Join("+", keys)} nicht sicher sendbar."); return false; }
        var main = AtasMainHwnd();
        if (main == IntPtr.Zero) { W("      [taste] ATAS-Hauptfenster (Win32) nicht gefunden."); return false; }
        ForceForegroundHwnd(main); Thread.Sleep(150);
        foreach (var vk in vks) { keybd_event(vk, 0, 0, UIntPtr.Zero); Thread.Sleep(25); }
        for (int i = vks.Count - 1; i >= 0; i--) { keybd_event(vks[i], 0, 2, UIntPtr.Zero); Thread.Sleep(15); }
        W($"      [taste] '{name}' = {string.Join("+", keys)} gesendet.");
        return true;
    }

    // ------------------------------------------------------------------ Stoppen mit Bestaetigung

    internal sealed record StopResult(bool Stopped, bool NoFurtherPass, string How, long Ms, List<string> StartLinesAfter) { public bool LeftReplayView { get; init; } }


    // 1.3.7: Der fruehere Stop-Pfad (StopReplayConfirmed: 'StopMarketReplay' alle 2,5 s bis zu 60 s) ist entfernt. VM-Befund
    // 18.11.: nach dem ERSTEN Kuerzel zeigte der Titel 'gestoppt'; die Bestaetigung kannte nur Replay-Quellen -> 21 Kuerzel.
    // Ersatz: CloseReplayBarConfirmed (ReplayBarClose.cs).

    private static void FailShot(Action<string> W, string tag)
    {
        try
        {
            var fail = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(RecordingsDir)) ?? AppContext.BaseDirectory, "Logs", $"{tag}_fehlgeschlagen_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            Directory.CreateDirectory(Path.GetDirectoryName(fail)!);
            using var b = ScreenOcr.CapturePrimary(out _, out _); b.Save(fail, System.Drawing.Imaging.ImageFormat.Png);
            W($"[stop] Bildschirmfoto: {fail}");
        }
        catch { }
    }

    private static bool IsPassStart(AtasLogLine l) =>
        l.IsReplaySource && (l.Message.Contains("Playing from", StringComparison.Ordinal) || l.Message.Contains("Initialized -> Started", StringComparison.Ordinal));

    /// <summary>Wartet begrenzt, bis der Chart wieder editierbar ist ('Indikatoren' aktiv), mit begrenzten UIA-Aufrufen.</summary>
    internal static bool WaitChartEditable(Action<string> W, int seconds)
    {
        var sw = Stopwatch.StartNew(); int seen = 0;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            seen = Bounded("Indikatoren aktiv?", IndicatorsEnabled, 3000, false, W) ? seen + 1 : 0;
            if (seen >= 2) { W($"[stop] Chart wieder editierbar nach {sw.ElapsedMilliseconds} ms."); return true; }
            Thread.Sleep(400);
        }
        W($"[stop] Chart nach {seconds}s nicht editierbar ('Indikatoren' inaktiv).");
        return false;
    }

    // ------------------------------------------------------------------ Gemessene Geschwindigkeit (Recorder-Zeitstempel)

    /// <summary>Tatsaechliche Wiedergabegeschwindigkeit aus der Aufnahme: Marktzeit-Spanne / Empfangszeit-Spanne (tMarket/tRecv).</summary>
    internal static double? MeasuredSpeed(string recordingDir)
    {
        var t = Path.Combine(recordingDir, "ticks.ndjson");
        if (!File.Exists(t)) return null;
        var first = FirstLine(t); var last = ReadLastLine(t);
        if (first == null || last == null) return null;
        var m0 = ParseDt(JsonTMarket(first)); var m1 = ParseDt(JsonTMarket(last));
        var r0 = ParseDt(JsonTRecv(first)); var r1 = ParseDt(JsonTRecv(last));
        if (m0 == null || m1 == null || r0 == null || r1 == null) return null;
        double dm = (m1.Value - m0.Value).TotalSeconds, dr = (r1.Value - r0.Value).TotalSeconds;
        if (dr <= 0.05 || dm <= 0) return null;
        return dm / dr;
    }

    // ------------------------------------------------------------------ Kontrollierter Stopp von der Oberflaeche

    internal static string? StopFile => Environment.GetEnvironmentVariable("ATAS_COLLECTOR_STOPFILE");
    internal static bool StopRequested() { var f = StopFile; return !string.IsNullOrEmpty(f) && File.Exists(f); }

    // ------------------------------------------------------------------ Einzelbefehl: Stop-Pfad am vorhandenen Zustand

    /// <summary>'stoptest': 10 s beobachten (nur lesen), dann die Wiedergabe ueber den Stop-Pfad beenden, bestaetigen und
    /// pruefen, dass kein weiterer Durchlauf beginnt. Keine Aufnahme, kein Laden, kein Play.</summary>
    private static int StopTest(string? outFile)
    {
        outFile ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"ATAS-Stoptest-{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); try { File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(true)); } catch { } }
        W($"# ATAS Stoptest  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  Probe {ProgramVersion()}");
        if (Process.GetProcessesByName("OFT.Platform").Length == 0) { W("BLOCKER: ATAS laeuft nicht."); return 3; }
        var alog = new IncrementalAtasLog(Path.GetDirectoryName(AtasLogPath())!);
        alog.SeekToEnd();

        W("[beobachten] 10 s nur lesen: Transporttitel (Win32), ATAS-Protokoll, Antwortzeit von UIA-Aufrufen");
        var obs = Stopwatch.StartNew(); string lastTitle = "\u0000"; int starts = 0;
        while (obs.Elapsed.TotalSeconds < 10)
        {
            var t = ReplayFloatWin32()?.Title ?? "(kein Replay-Fenster)";
            if (t != lastTitle) { W($"   t+{obs.ElapsedMilliseconds,5} ms Titel='{t}'"); lastTitle = t; }
            foreach (var raw in alog.ReadNew())
            {
                var l = AtasLogLine.Parse(raw);
                if (l == null || !(l.IsReplaySource || l.Source.Contains("Chart", StringComparison.OrdinalIgnoreCase))) continue;
                if (IsPassStart(l)) starts++;
                W("   log " + MaskSensitive(l.ToString()));
            }
            Thread.Sleep(200);
        }
        W($"[beobachten] Durchlauf-Starts in 10 s: {starts} ({(starts > 0 ? "ATAS wiederholt die Wiedergabe" : "kein neuer Durchlauf")})");
        foreach (var (name, f) in new (string, Func<object?>)[] {
            ("FindIdAny(RibbonReplayButton)", () => FindIdAny("RibbonReplayButton")),
            ("FindBtnByHelpAny(Zur Echtzeit wechseln)", () => FindBtnByHelpAny("Zur Echtzeit wechseln")),
            ("IndicatorsEnabled", () => IndicatorsEnabled()),
            ("FindReplayTransport", () => FindReplayTransport(out _)) })
        {
            var swc = Stopwatch.StartNew();
            var r = Bounded(name, f, 8000, "(blockiert)", W);
            W($"   UIA {name}: {swc.ElapsedMilliseconds} ms -> {(r == null ? "null" : r is AutomationElement ? "gefunden" : r.ToString())}");
        }
        var fw = ReplayFloatWin32();
        if (fw != null)
        {
            var png = Path.ChangeExtension(outFile, ".transport.png");
            Shot((int)fw.Rect.X, (int)fw.Rect.Y, (int)fw.Rect.Width, (int)fw.Rect.Height, png);
            W($"[transport] Fenster '{fw.Title}' rect={(int)fw.Rect.X},{(int)fw.Rect.Y},{(int)fw.Rect.Width}x{(int)fw.Rect.Height} Bild: {png}");
        }

        bool uiaTransport = Bounded("FindReplayTransport", () => FindReplayTransport(out _) != null, 5000, false, W);
        W($"[stoppen] Transport per UIA bedienbar: {uiaTransport}");
        var res = CloseReplayBarConfirmed(W, alog, visualMode: !uiaTransport);
        bool editable = res.LeftReplayView;
        W($"\n===== ERGEBNIS Stoptest: gestoppt={res.Stopped} keinWeitererDurchlauf={res.NoFurtherPass} Chart editierbar={editable} Weg='{res.How}' Dauer={res.Ms} ms =====");
        W($"Protokoll: {outFile}");
        return res.Stopped && res.NoFurtherPass && editable ? 0 : 8;
    }
}
