using System.Diagnostics;
using System.IO;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace AtasReplayProbe;

// 1.3.7: Ablauf nach dem erkannten Replay-Ende - Replay-Leiste schliessen (ihr eigenes X), Wirkung pruefen.
//
// VM-Befund 18.11.2026 14:25 (1.3.6): Nach 'natuerliches Ende' sendete der Stop-Pfad 21-mal 'StopMarketReplay'. Das
// Nach dem ERSTEN Kuerzel zeigte der Fenstertitel 'Replay - Wiedergabe gestoppt' (vorher 'Replay - Module laden...').
// Bestaetigt wurde nur ueber eine '-> Stopped'-Zeile einer REPLAY-Quelle; der Titel zaehlte nicht als Beleg. Ergebnis:
// Serie von Kuerzeln, Replay-Ansicht nie verlassen. (Der hochgeladene VM-Lauf enthaelt KEINE ATAS-Logzeilen zum Stopp.
// Dass ATAS einen Stopp waehrend des Ladens auch unter der CHART-Quelle protokolliert - 'MNQ 5m Chart: Replay chart
// state: LoadingModules -> Stopped' - stammt aus separaten, lokalen ATAS-Logs (Okt./12.11.), nicht von der VM.)
//
// Neu (keine Serie von Kuerzeln, jeder Schritt mit Zeit und Beleg):
//  0) Laeuft/laedt die Wiedergabe laut Titel noch (Auto-Rewind), genau EIN 'StopMarketReplay'; Wirkung per Titel
//     ('gestoppt') und Protokoll (Replay- ODER Chart-Quelle). Kein zweites Kuerzel.
//  1) Replay-Leiste finden: schwebend -> ihr EIGENES Fenster nach vorn (kein Klick in den Chart); angedockt -> die
//     sichtbare Leiste im Hauptfenster.
//  2) Ihr X FRISCH ermitteln (UIA-Schliessen-Schaltflaeche der Leiste; sonst Win32-Treffertest HTCLOSE am eigenen Fenster)
//     und genau EINMAL klicken. Unmittelbar vor dem Klick: unter dem Mauszeiger liegt wirklich dieses Schliessen-Symbol
//     dieser Leiste (UIA-Element bzw. HTCLOSE + Fenster). Verdeckt/unklar -> kein Klick, Diagnosebild.
//  3) Wirkung: Leiste weg, KEIN neuer Durchlauf, Chart wieder editierbar. Blendet X nur die Leiste aus -> genau das melden.
internal static partial class Program
{
    private static readonly Regex RxCloseName = new(@"(?i)^\s*(schlie(ß|ss)en|close|x|×)\s*$", RegexOptions.Compiled);
    private static readonly Regex RxCloseId = new(@"(?i)(^|_)(close(button)?|PART_Close\w*|closebtn)$", RegexOptions.Compiled);
    private const int MaxBarContainerHeight = 200;   // Leiste/Leistenfenster; Chart-Panel und Hauptfenster sind groesser

    internal static bool IsCloseControl(string name, string aid, string help) =>
        RxCloseName.IsMatch(name ?? "") || RxCloseId.IsMatch(aid ?? "") || RxCloseName.IsMatch(help ?? "")
        || Regex.IsMatch(help ?? "", @"(?i)^\s*(schlie(ß|ss)en|close)\b");

    /// <summary>Stopp-Beleg im ATAS-Protokoll: Replay-Quelle ODER Chart-Quelle ('Replay chart state: … -> Stopped').</summary>
    internal static bool IsStopEvidence(AtasLogLine l) =>
        (l.IsReplaySource && (Regex.IsMatch(l.Message, @"State changed \w+ -> Stopped") || l.Message.Contains("Playing historical data stopped")))
        || (l.Source.EndsWith(" Chart", StringComparison.Ordinal) && Regex.IsMatch(l.Message, @"Replay chart state: \w+ -> Stopped"));

    internal static bool TitleStopped(string t) => t.Contains("gestoppt", StringComparison.OrdinalIgnoreCase) || t.Contains("stopped", StringComparison.OrdinalIgnoreCase);

    internal enum AfterCloseVerdict { Left, OnlyHidden, LoopContinues, BarStillVisible, NotEditable }

    /// <summary>Reine Bewertung (getestet) der Wirkung nach dem X.</summary>
    internal static (AfterCloseVerdict v, string why) JudgeAfterClose(bool barGone, bool editable, int passStartsAfter, bool stopAfterLastPass)
    {
        if (passStartsAfter > 0 && !stopAfterLastPass) return (AfterCloseVerdict.LoopContinues, $"nach dem X {passStartsAfter} neuer Durchlauf ohne folgenden Stopp -> Replay-Loop laeuft weiter");
        if (!barGone) return (AfterCloseVerdict.BarStillVisible, "Leiste nach dem X weiterhin sichtbar");
        if (!editable) return (AfterCloseVerdict.OnlyHidden, "X hat nur die Leiste ausgeblendet: Chart NICHT wieder editierbar (Replay-Ansicht aktiv)");
        return (AfterCloseVerdict.Left, "Leiste geschlossen, kein neuer Durchlauf, Chart editierbar");
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    private const uint WM_NCHITTEST = 0x0084; private const int HTCLOSE = 20;

    private static int NcHit(IntPtr h, int x, int y)
    {
        var lp = new IntPtr(((y & 0xFFFF) << 16) | (x & 0xFFFF));
        return SendMessageTimeout(h, WM_NCHITTEST, IntPtr.Zero, lp, 0x0002 /*ABORTIFHUNG*/, 1000, out var r) == IntPtr.Zero ? -1 : r.ToInt32();
    }

    private static bool BelongsTo(IntPtr bar, IntPtr hit) => hit != IntPtr.Zero && (hit == bar || IsChild(bar, hit));

    private sealed record CloseTarget(int X, int Y, string How, AutomationElement? El, IntPtr BarHwnd) { public bool Visual { get; init; } }

    private static readonly Regex RxCloseTip = new(@"(?i)(schlie(ß|ss)en|close)", RegexOptions.Compiled);

    /// <summary>Tooltip nahe am Zeiger: UIA, sonst Texterkennung des Bereichs unter dem Zeiger (Tooltip wird gezeichnet).</summary>
    private static string ReadTooltipNear(int x, int y)
    {
        var tip = Bounded("Tooltip lesen", () => CurrentToolTip(), 1500, null) ?? "";
        if (tip != "") return tip;
        try
        {
            var r = new System.Windows.Rect(Math.Max(0, x - 200), y + 4, 420, 60);
            using var bmp = CaptureRect(r);
            return string.Join(" ", ScreenOcr.Recognize(bmp, (int)r.X, (int)r.Y).Select(w => w.Text));
        }
        catch { return ""; }
    }

    // Beschriftungen der Symbolzeile (Start-Modus-Auswahl, Geschwindigkeit). NICHT 'Module laden…' – das steht im
    // Fortschrittsbalken UNTER der Symbolzeile.
    private static readonly Regex RxBarRowLabel = new(@"(?i)(ausw(ä|a)hlen|^\d{1,4}\s?x$)", RegexOptions.Compiled);

    /// <summary>1.3.9 (Befund 18.11., Ende nach 'Module laden…'): Kandidaten fuer das X nur in der SYMBOLZEILE der Leiste.
    /// Die Zeile wird aus den OCR-Beschriftungen ('… auswählen', '100x') bestimmt; der Fortschrittsbalken darunter
    /// verschmolz sonst spaltenweise mit allen Symbolen, und sein rechtes Ende galt als 'rechtestes Symbol'.
    /// Ohne erkennbare Zeile: bisheriges Verfahren ueber die ganze Hoehe (die Tooltip-Pruefung entscheidet weiterhin).
    /// Rein (Bild + OCR-Woerter rein, Kandidaten raus) -> im Regressionstest pruefbar.</summary>
    internal static List<(int cx, int cy, int w)> BarCloseCandidates(System.Drawing.Bitmap bar, IReadOnlyList<OcrWord> words, out string diag)
    {
        // Einzelzeichen-Treffer sind Symbole, kein Text (echtes Bild 18.11.: das X wurde als 'x', '?' als 'o' erkannt und
        // seine Spalten sonst als Text ausgeblendet).
        var spans = words.Where(o => o.Text.Trim().Length >= 2).Select(o => (o.X, o.X + o.W)).ToList();
        var labels = words.Where(o => RxBarRowLabel.IsMatch(o.Text.Trim()) && !o.Text.Contains("laden", StringComparison.OrdinalIgnoreCase)).ToList();
        List<(int cx, int cy, int w)> blobs; string where;
        if (labels.Count > 0)
        {
            var cys = labels.Select(o => o.Y + o.H / 2).OrderBy(v => v).ToList();
            int rowCy = cys[cys.Count / 2];
            int half = Math.Max(10, labels.Max(o => o.H));
            int y0 = Math.Max(0, rowCy - half), y1 = Math.Min(bar.Height, rowCy + half + 1);
            using var band = bar.Clone(new System.Drawing.Rectangle(0, y0, bar.Width, Math.Max(1, y1 - y0)), bar.PixelFormat);
            blobs = FindIconBlobs(band, spans).Select(b => (b.cx, b.cy + y0, b.w)).ToList();
            where = $"Symbolzeile y{y0}–{y1} (aus {labels.Count} Beschriftung(en))";
        }
        else { blobs = FindIconBlobs(bar, spans); where = "ganze Hoehe (keine Zeilenbeschriftung erkannt)"; }
        var right = blobs.Where(b => b.cx >= bar.Width * 0.85).OrderByDescending(b => b.cx).ToList();
        diag = $"{blobs.Count} Symbole in {where}, im rechten Rand {right.Count}";
        return right;
    }

    /// <summary>Offline-Diagnose (kein ATAS, kein Klick): X-Kandidaten in einem gespeicherten Bild, Ausschnitt x y w h.
    ///   xblob &lt;bild.png&gt; x y w h</summary>
    internal static int XBlob(string[] args)
    {
        if (args.Length < 6) { Console.WriteLine("xblob <bild.png> x y w h"); return 2; }
        using var img = new System.Drawing.Bitmap(args[1]);
        var rc = new System.Drawing.Rectangle(int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]), int.Parse(args[5]));
        using var bar = img.Clone(rc, img.PixelFormat);
        var words = ScreenOcr.Recognize(bar, 0, 0);
        Console.WriteLine("OCR: " + string.Join(" | ", words.Select(o => $"'{o.Text}'@{o.X},{o.Y} {o.W}x{o.H}")));
        var old = FindIconBlobs(bar, words.Select(o => (o.X, o.X + o.W)).ToList()).Where(b => b.cx >= bar.Width * 0.85).OrderByDescending(b => b.cx).ToList();
        Console.WriteLine($"alt (ganze Hoehe): rechtestes {(old.Count > 0 ? $"@({rc.X + old[0].cx},{rc.Y + old[0].cy}) Breite {old[0].w}" : "keins")}");
        var neu = BarCloseCandidates(bar, words, out var d);
        Console.WriteLine($"neu: {d}; rechtestes {(neu.Count > 0 ? $"@({rc.X + neu[0].cx},{rc.Y + neu[0].cy}) Breite {neu[0].w}" : "keins")}");
        return 0;
    }

    /// <summary>X der Leiste frisch ermitteln. Nur Elemente im Leistencontainer (Hoehe <= 200 px), nie Chart/Hauptfenster.</summary>
    private static CloseTarget? FindBarClose(Action<string> W, FloatWin? fw, bool allowHover = true)
    {
        // Container: schwebend = eigenes Fenster der Leiste; angedockt = kleinster Vorfahr der Transport-Schaltflaechen.
        AutomationElement? container = null; string cdesc = "";
        if (fw != null)
        {
            container = Bounded("UIA-Fenster der Leiste", () => AutomationElement.FromHandle(fw.Hwnd), 3000, null, W);
            cdesc = $"schwebendes Fenster '{fw.Title}' hwnd=0x{fw.Hwnd.ToInt64():X} {(int)fw.Rect.Width}x{(int)fw.Rect.Height}";
        }
        else
        {
            var tr = Bounded("angedockte Leiste (UIA)", () => FindReplayTransport(out _), 8000, null, W);
            var seed = tr?.PlayBtn ?? tr?.StartBtn;
            if (seed != null)
            {
                foreach (var a in Bounded("Vorfahren der Leiste", () => Ancestors(seed, 10), 4000, new List<AutomationElement>(), W))
                {
                    var r = SafeRect(a);
                    if (r.IsEmpty || r.Height > MaxBarContainerHeight) break;
                    container = a; cdesc = $"angedockte Leiste '{Safe(() => a.Current.ClassName)}' {(int)r.Width}x{(int)r.Height}";
                }
            }
        }
        W($"[x] Leiste: {(container != null || fw != null ? cdesc : "NICHT gefunden")}");

        // a) UIA-Schliessen-Schaltflaeche im Container
        if (container != null)
        {
            var cr = SafeRect(container);
            var btns = Bounded("Schliessen-Schaltflaeche suchen", () =>
            {
                var res = new List<AutomationElement>();
                foreach (AutomationElement b in container.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
                {
                    var c = b.Current;
                    if (!IsCloseControl(c.Name, c.AutomationId, c.HelpText)) continue;
                    var r = c.BoundingRectangle;
                    if (r.IsEmpty || c.IsOffscreen) continue;
                    if (r.X < cr.X - 1 || r.Right > cr.Right + 1 || r.Y < cr.Y - 1 || r.Bottom > cr.Bottom + 1) continue;
                    res.Add(b);
                }
                return res;
            }, 5000, new List<AutomationElement>(), W);
            foreach (var b in btns) W($"[x]   Kandidat UIA: name='{Safe(() => b.Current.Name)}' aid='{Safe(() => b.Current.AutomationId)}' rect={Rect(b)}");
            if (btns.Count == 1)
            {
                var r = SafeRect(btns[0]);
                return new CloseTarget((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2), $"UIA-Schaltflaeche '{Safe(() => btns[0].Current.Name)}' der Leiste", btns[0], fw?.Hwnd ?? IntPtr.Zero);
            }
            if (btns.Count > 1) { W($"[x] {btns.Count} Schliessen-Schaltflaechen in der Leiste -> unklar, kein Klick."); return null; }
        }

        // b) Schwebend: Win32-Treffertest HTCLOSE am eigenen Fenster (oberer rechter Bereich, frisch abgetastet)
        if (fw != null)
        {
            var r = fw.Rect; var found = new List<(int x, int y)>();
            for (int y = (int)r.Y + 2; y < (int)r.Y + Math.Min(40, (int)r.Height); y += 3)
                for (int x = (int)r.Right - 3; x > (int)r.Right - Math.Min(90, (int)r.Width); x -= 3)
                    if (NcHit(fw.Hwnd, x, y) == HTCLOSE) found.Add((x, y));
            if (found.Count > 0)
            {
                int cx = (int)found.Average(p => p.x), cy = (int)found.Average(p => p.y);
                return new CloseTarget(cx, cy, $"Win32-Schliessen-Feld (HTCLOSE) des Leistenfensters, {found.Count} Punkte", null, fw.Hwnd);
            }
            W("[x] Kein Schliessen-Feld per Win32-Treffertest am Leistenfenster.");

            // c) Custom gezeichnete Leiste (lokal belegt, 1.3.8): das X ist das RECHTESTE Symbol im eigenen Leistenfenster,
            //    Tooltip 'Schließen'. Nur wenn die Leiste sichtbar vorn liegt (Fenster unter ihrer Mitte = Leistenfenster).
            var mid = WindowFromPoint(new Point((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2)));
            if (!BelongsTo(fw.Hwnd, mid)) { W($"[x] Leiste verdeckt (unter ihrer Mitte liegt 0x{mid.ToInt64():X} '{ClassOf(mid)}') -> keine Symbolsuche."); return null; }
            List<(int cx, int cy, int w)> right;
            try
            {
                using var bmp = CaptureRect(r);
                List<OcrWord> words;
                try { words = ScreenOcr.Recognize(bmp, 0, 0); } catch { words = new(); }
                right = BarCloseCandidates(bmp, words, out var diag);
                W($"[x] Symbolsuche im Leistenfenster: {diag}" +
                  (right.Count > 0 ? $"; rechtestes @({(int)r.X + right[0].cx},{(int)r.Y + right[0].cy}) Breite {right[0].w}" : ""));
            }
            catch (Exception ex) { W($"[x] Symbolsuche fehlgeschlagen: {ex.Message}"); return null; }
            if (right.Count == 0) return null;
            int sx = (int)r.X + right[0].cx, sy = (int)r.Y + right[0].cy;
            if (!allowHover)
                return new CloseTarget(sx, sy, "rechtestes Symbol der Leiste (Tooltip-Pruefung nur im Lauf)", null, fw.Hwnd) { Visual = true };
            SetCursorPos(sx, sy); Thread.Sleep(1300);
            var tip = ReadTooltipNear(sx, sy);
            W($"[x] Tooltip am rechtesten Symbol: '{Trunc(tip, 60)}'");
            if (!RxCloseTip.IsMatch(tip)) { W("[x] Tooltip ist nicht 'Schließen' -> kein Ziel."); return null; }
            return new CloseTarget(sx, sy, "rechtestes Symbol der Leiste mit Tooltip 'Schließen'", null, fw.Hwnd) { Visual = true };
        }
        return null;
    }

    /// <summary>Unmittelbar vor dem Klick: unter dem Mauszeiger liegt wirklich das Schliessen-Symbol dieser Leiste.</summary>
    private static (bool ok, string why) VerifyUnderCursor(CloseTarget t, bool moveCursor = true)
    {
        if (moveCursor) { SetCursorPos(t.X, t.Y); Thread.Sleep(250); }
        var hw = WindowFromPoint(new Point(t.X, t.Y));
        if (t.BarHwnd != IntPtr.Zero && !BelongsTo(t.BarHwnd, hw))
            return (false, $"Fenster unter dem Zeiger 0x{hw.ToInt64():X} '{ClassOf(hw)}' ist NICHT die Leiste (verdeckt)");
        if (t.El != null)
        {
            var at = Bounded("UIA-Element unter dem Zeiger", () => AutomationElement.FromPoint(new System.Windows.Point(t.X, t.Y)), 3000, null);
            if (at == null) return (false, "UIA: kein Element unter dem Zeiger");
            bool same = Automation.Compare(at, t.El) || Ancestors(at, 4).Any(a => Automation.Compare(a, t.El));
            return same ? (true, $"UIA: unter dem Zeiger liegt die Schliessen-Schaltflaeche (Fenster 0x{hw.ToInt64():X})")
                        : (false, $"UIA: unter dem Zeiger liegt '{Safe(() => at.Current.Name)}' ({Safe(() => at.Current.ClassName)}), nicht das Schliessen-Symbol");
        }
        if (t.Visual)
        {
            if (!moveCursor) return (true, $"Fenster am Ziel 0x{hw.ToInt64():X} = Leiste (Tooltip-Pruefung erst im Lauf)");
            var tip = ReadTooltipNear(t.X, t.Y);
            return RxCloseTip.IsMatch(tip) ? (true, $"unter dem Zeiger: Leistenfenster 0x{hw.ToInt64():X}, Tooltip '{Trunc(tip, 40)}'")
                                           : (false, $"Tooltip unter dem Zeiger ist '{Trunc(tip, 40)}', nicht 'Schließen'");
        }
        int ht = NcHit(t.BarHwnd, t.X, t.Y);
        return ht == HTCLOSE ? (true, $"Win32: HTCLOSE am Zeiger, Fenster 0x{hw.ToInt64():X} = Leiste") : (false, $"Win32: Treffertest am Zeiger = {ht}, nicht HTCLOSE");
    }

    private static bool BringBarToFront(Action<string> W, FloatWin fw)
    {
        ForceForegroundHwnd(fw.Hwnd); Thread.Sleep(250);
        if (GetForegroundWindow() == fw.Hwnd) return true;
        // Windows verweigert SetForegroundWindow ohne Eingabe: kurzer Alt-Tastendruck (kein Klick), dann erneut.
        keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero);
        ForceForegroundHwnd(fw.Hwnd); Thread.Sleep(250);
        bool ok = GetForegroundWindow() == fw.Hwnd;
        W($"[x] Leistenfenster im Vordergrund: {ok}");
        return ok;
    }

    /// <summary>Nach dem erkannten Ende: (0) hoechstens EIN Stopp-Kuerzel, (1) Leiste nach vorn, (2) ihr X einmal,
    /// (3) Wirkung pruefen. Liefert Stopped=true nur, wenn die Replay-Ansicht nachweislich verlassen ist.</summary>
    internal static StopResult CloseReplayBarConfirmed(Action<string> W, IncrementalAtasLog alog, bool visualMode, int verifySec = 30)
    {
        var sw = Stopwatch.StartNew();
        var all = new List<AtasLogLine>();
        List<AtasLogLine> Pull() { var n = new List<AtasLogLine>(); foreach (var raw in alog.ReadNew()) { var l = AtasLogLine.Parse(raw); if (l != null) { n.Add(l); all.Add(l); } } return n; }
        string T() => ReplayFloatWin32()?.Title ?? "";
        W($"[x] Beginn {DateTime.Now:HH:mm:ss.fff}  Leiste='{(T() == "" ? "(kein schwebendes Fenster)" : T())}'  Modus={(visualMode ? "visuell" : "UIA")}");
        Pull();

        // ---- 0) Loop/Laden laut Titel? Dann genau EIN Stopp-Kuerzel, Wirkung per Titel + Protokoll ----
        string t0 = T(); bool hotkeyUsed = false;
        if (t0 != "" && (TitleLoading(t0) || TitlePlaying(t0)))
        {
            var actAt = DateTime.Now.AddMilliseconds(-300);
            hotkeyUsed = SendAtasHotkey("StopMarketReplay", W);
            W($"[x] 0) Wiedergabe laeuft/laedt ('{t0}') -> EINMAL 'StopMarketReplay' gesendet={hotkeyUsed} (t+{sw.ElapsedMilliseconds} ms)");
            var s0 = Stopwatch.StartNew(); bool titleStop = false; AtasLogLine? logStop = null; long stableSince = -1;
            while (s0.ElapsedMilliseconds < 12000)
            {
                foreach (var l in Pull()) if (l.Ts >= actAt && IsStopEvidence(l)) logStop ??= l;
                bool ts = TitleStopped(T());
                if (ts && stableSince < 0) stableSince = s0.ElapsedMilliseconds; if (!ts) stableSince = -1;
                titleStop = ts && s0.ElapsedMilliseconds - stableSince >= 1500;
                if (titleStop) break;
                Thread.Sleep(200);
            }
            W($"[x] 0) Wirkung nach {s0.ElapsedMilliseconds} ms: Titel '{T()}' (gestoppt stabil={titleStop}); Protokoll: {(logStop != null ? logStop.ToString() : "keine Stopp-Zeile")}");
            if (!titleStop && logStop == null) W("[x] 0) Stopp nicht belegt - KEIN weiteres Kuerzel; weiter mit dem X der Leiste.");
        }
        else W($"[x] 0) Titel '{t0}' -> kein Stopp-Kuerzel noetig.");

        // ---- 1) Leiste finden + nach vorn ----
        var fw = ReplayFloatWin32();
        if (fw != null && !BringBarToFront(W, fw)) W("[x] 1) Leistenfenster nicht im Vordergrund - X wird trotzdem nur nach Pruefung unter dem Zeiger geklickt.");
        fw = ReplayFloatWin32();   // frisch (Lage kann sich geaendert haben)

        // ---- 2) X frisch ermitteln, unter dem Zeiger pruefen, EINMAL klicken ----
        var target = FindBarClose(W, fw);
        if (target == null)
        {
            W("[x] 2) X der Leiste nicht eindeutig ermittelbar -> KEIN Klick.");
            FailShot(W, "x_nicht_gefunden");
            return new StopResult(false, false, "X nicht gefunden", sw.ElapsedMilliseconds, new List<string>());
        }
        var (okUnder, whyUnder) = VerifyUnderCursor(target);
        W($"[x] 2) Ziel: {target.How} @({target.X},{target.Y}); Pruefung unter dem Zeiger: {whyUnder}");
        if (!okUnder)
        {
            W("[x] 2) Ziel verdeckt/unklar -> KEIN Klick.");
            FailShot(W, "x_unklar");
            return new StopResult(false, false, "X unklar", sw.ElapsedMilliseconds, new List<string>());
        }
        var clickAt = DateTime.Now.AddMilliseconds(-200);
        ClickScreen(target.X, target.Y);
        W($"[x] 2) X EINMAL geklickt @({target.X},{target.Y}) t+{sw.ElapsedMilliseconds} ms");
        SetCursorPos(target.X, target.Y + 120);   // Zeiger von der Leiste weg (keine Tooltips/Hover-Effekte)

        // ---- 3) Wirkung pruefen (ohne weitere Aktionen) ----
        var sv = Stopwatch.StartNew(); int passAfter = 0; DateTime? lastPass = null, lastStop = null, online = null;
        int editSeen = 0; bool barGone = false; long quietSince = 0;
        while (sv.Elapsed.TotalSeconds < verifySec)
        {
            foreach (var l in Pull())
            {
                if (l.Ts < clickAt) continue;
                if (IsPassStart(l)) { passAfter++; lastPass = l.Ts; quietSince = sv.ElapsedMilliseconds; W($"[x] 3) ATAS: neuer Durchlauf nach dem X: {l}"); }
                if (IsStopEvidence(l)) { lastStop = l.Ts; W($"[x] 3) ATAS: {l}"); }
                if (l.Message.Contains("to online state", StringComparison.OrdinalIgnoreCase)) { online = l.Ts; W($"[x] 3) ATAS: {l}"); }
            }
            barGone = fw != null ? ReplayFloatWin32() == null : Bounded("Leiste noch da? (UIA)", () => FindReplayTransport(out _) == null, 6000, false, W);
            editSeen = Bounded("Indikatoren aktiv?", IndicatorsEnabled, 6000, false, W) ? editSeen + 1 : 0;
            bool stopAfterPass = lastPass == null || (lastStop ?? online) > lastPass;
            if (barGone && editSeen >= 2 && stopAfterPass && sv.ElapsedMilliseconds - quietSince >= 8000) break;
            Thread.Sleep(500);
        }
        bool sap = lastPass == null || (lastStop ?? online) > lastPass;
        var (verdict, why) = JudgeAfterClose(barGone, editSeen >= 2, passAfter, sap);
        W($"[x] 3) Ergebnis nach {sv.ElapsedMilliseconds} ms: {verdict} - {why}. (Leiste weg={barGone}, editierbar={editSeen >= 2}, Durchlaeufe nach X={passAfter}, online-Zeile={(online != null)})");
        if (verdict != AfterCloseVerdict.Left) FailShot(W, "x_wirkung");
        W($"[x] Ende {DateTime.Now:HH:mm:ss.fff} ({sw.ElapsedMilliseconds} ms); Stopp-Kuerzel gesendet: {(hotkeyUsed ? "1x" : "0x")}");
        bool left = verdict == AfterCloseVerdict.Left;
        return new StopResult(left, passAfter == 0 || sap, $"X der Leiste ({target.How})", sw.ElapsedMilliseconds,
                              all.Where(IsPassStart).Select(l => l.ToString()).ToList()) { LeftReplayView = left };
    }

    /// <summary>Nur lesen: Replay-Leiste und ihr X finden, Pruefung am Zielpunkt ohne Mausbewegung, Bild mit Markierung.
    /// Kein Klick, keine Taste, kein Stopp.</summary>
    internal static int XDiag(string? outFile)
    {
        outFile ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"ATAS-xdiag-{DateTime.Now:yyyyMMdd_HHmmss}.txt");
        var sb = new System.Text.StringBuilder();
        void W(string s) { sb.AppendLine(s); Console.WriteLine(s); try { File.WriteAllText(outFile, sb.ToString(), new System.Text.UTF8Encoding(true)); } catch { } }
        W($"# ATAS X-Diagnose (nur lesen, kein Klick)  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  Probe {ProgramVersion()}");
        if (Process.GetProcessesByName("OFT.Platform").Length == 0) { W("ATAS laeuft nicht."); return 3; }
        var fw = ReplayFloatWin32();
        if (fw != null) { ForceForegroundHwnd(fw.Hwnd); Thread.Sleep(300); }   // nur nach vorn holen (kein Klick, keine Mausbewegung)
        W($"Leiste schwebend (Win32): {(fw != null ? $"'{fw.Title}' hwnd=0x{fw.Hwnd.ToInt64():X} rect={(int)fw.Rect.X},{(int)fw.Rect.Y},{(int)fw.Rect.Width}x{(int)fw.Rect.Height} Vordergrund={GetForegroundWindow() == fw.Hwnd}" : "nein")}");
        var t = FindBarClose(W, fw, allowHover: false);
        if (t == null) { W("ERGEBNIS: X der Leiste NICHT eindeutig gefunden - im Lauf wuerde NICHT geklickt."); }
        else
        {
            var (ok, why) = VerifyUnderCursor(t, moveCursor: false);
            W($"Ziel: {t.How} @({t.X},{t.Y})");
            W($"Pruefung am Zielpunkt (ohne Mausbewegung): {why}");
            W(ok ? "ERGEBNIS: X der Leiste eindeutig; im Lauf wuerde genau hier EINMAL geklickt."
                 : "ERGEBNIS: Ziel verdeckt/unklar - im Lauf wuerde NICHT geklickt (Leiste ggf. hinter dem Chart; der Lauf holt sie vorher nach vorn).");
        }
        try
        {
            var r = fw?.Rect ?? (t != null ? new System.Windows.Rect(t.X - 300, t.Y - 60, 600, 120) : System.Windows.Rect.Empty);
            if (!r.IsEmpty)
            {
                var rr = new System.Windows.Rect(Math.Max(0, r.X - 20), Math.Max(0, r.Y - 20), r.Width + 40, r.Height + 40);
                using var bmp = new Bitmap((int)rr.Width, (int)rr.Height);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen((int)rr.X, (int)rr.Y, 0, 0, bmp.Size);
                    if (t != null) using (var pen = new Pen(Color.Red, 2)) g.DrawEllipse(pen, t.X - (int)rr.X - 9, t.Y - (int)rr.Y - 9, 18, 18);
                }
                var png = Path.ChangeExtension(outFile, ".png"); bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png); W($"Bild (rotes Ziel): {png}");
            }
        }
        catch (Exception ex) { W($"Bild nicht gespeichert: {ex.Message}"); }
        W($"Protokoll: {outFile}");
        return t != null ? 0 : 5;
    }
}
