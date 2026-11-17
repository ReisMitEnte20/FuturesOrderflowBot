using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace AtasReplayProbe;

// Gezieltes Entfernen des Recorders aus dem Indikator-Dialog (ersetzt RemoveSelectedRecorderRelative).
//
// Vorher: drei Klicks auf FESTE Versaetze relativ zur Dialog-Ecke (Zeile x+300/y+331, Papierkorb x+613/y+331,
// Uebernehmen x+941/y+508). Die Liste 'Hinzugefuegt' aendert aber Breite und Zeilenposition mit Dialoggroesse, Layout,
// Anzahl Indikatoren und DPI -> der Papierkorb wurde verfehlt, und "Uebernehmen" lief trotzdem.
//
// Jetzt, jeder Schritt geprueft:
//  1. Zeilen unter 'Hinzugefuegt' per UIA lesen; Recorder-Zeile ueber UIA-Text, sonst OCR eines FRISCHEN Screenshots
//     genau dieser Zeile erkennen. Genau eine Recorder-Zeile, sonst Abbruch.
//  2. Zeile per UIA auswaehlen (SelectionItem), Mauszeiger auf die Zeile -> Aktionssymbole werden sichtbar.
//  3. Papierkorb INNERHALB der Zeile suchen: (a) UIA-Schaltflaeche mit Loeschen-/Entfernen-Bezeichnung, sonst
//     (b) Symbole im frischen Zeilen-Screenshot finden und per Tooltip pruefen. Ohne bestaetigtes Ziel kein Klick.
//  4. Nach dem Klick: genau die Recorder-Zeile weg, alle anderen Zeilen unveraendert. Sonst 'Abbrechen'
//     (verwirft alle Aenderungen im Dialog) + Screenshot + Klickziel ins Protokoll.
//  5. Erst dann 'Uebernehmen'; danach Writer-Abschluss und keine verbliebene Recorder-Zeile/Instanz pruefen.
internal static partial class Program
{
    private static readonly Regex RecorderRowText = new(@"(?i)replay\s*rec", RegexOptions.Compiled);
    private static readonly Regex DeleteWords = new(@"(?i)(l[oö]sch|entfern|delete|remove|trash|papierkorb|\bbin\b)", RegexOptions.Compiled);

    internal sealed record AddedRow(int Index, System.Windows.Rect Rect, string UiaText, string OcrText, AutomationElement? Element = null)
    {
        public string Identity => (UiaText.Length > 0 ? UiaText : OcrText).Trim();
        public bool IsRecorder => RecorderRowText.IsMatch(UiaText) || RecorderRowText.IsMatch(OcrText);
    }

    internal enum RemovalOutcome { Removed, NoRecorder, Failed }

    // ------------------------------------------------------------------ reine, getestete Teile

    internal enum ListRead { Readable, Empty, Unreadable }

    /// <summary>Ist die Liste 'Hinzugefuegt' wirklich leer, lesbar oder NICHT lesbar? Eine leere UIA-Abfrage ist kein Beweis
    /// fuer eine leere Liste: massgeblich ist die Kopfzahl 'Hinzugefuegt (N)'; fehlt sie, entscheidet die Texterkennung.</summary>
    internal static ListRead ClassifyList(int headerCount, int rowsFound, bool allRowsHaveText)
    {
        if (headerCount == 0 && rowsFound == 0) return ListRead.Empty;
        if (headerCount > 0 && rowsFound == headerCount && allRowsHaveText) return ListRead.Readable;
        if (headerCount < 0 && rowsFound > 0 && allRowsHaveText) return ListRead.Readable;
        return ListRead.Unreadable;
    }

    /// <summary>OCR-Woerter zu Zeilen gruppieren (gleiche Hoehe +-tol Pixel). Liefert (y0, y1, x0, Text) je Zeile, von oben nach unten.</summary>
    internal static List<(int y0, int y1, int x0, string text)> GroupOcrLines(IEnumerable<(int x, int y, int w, int h, string t)> words, int tol = 6)
    {
        var lines = new List<(int cy, List<(int x, int y, int w, int h, string t)> ws)>();
        foreach (var wd in words.OrderBy(w => w.y + w.h / 2).ThenBy(w => w.x))
        {
            int cy = wd.y + wd.h / 2;
            int idx = lines.FindIndex(l => Math.Abs(l.cy - cy) <= tol);
            if (idx < 0) lines.Add((cy, new List<(int, int, int, int, string)> { wd }));
            else lines[idx].ws.Add(wd);
        }
        return lines.OrderBy(l => l.cy).Select(l => (l.ws.Min(w => w.y), l.ws.Max(w => w.y + w.h), l.ws.Min(w => w.x),
            string.Join(" ", l.ws.OrderBy(w => w.x).Select(w => w.t)))).ToList();
    }

    /// <summary>Genau die Recorder-Zeile ist weg, alle anderen Zeilen (Text/Reihenfolge) unveraendert?</summary>
    internal static bool OnlyRecorderRemoved(IReadOnlyList<AddedRow> before, IReadOnlyList<AddedRow> after, out string why)
    {
        var recBefore = before.Where(r => r.IsRecorder).ToList();
        if (recBefore.Count != 1) { why = $"vorher {recBefore.Count} Recorder-Zeilen"; return false; }
        if (after.Count != before.Count - 1) { why = $"Zeilenzahl {before.Count} -> {after.Count} (erwartet {before.Count - 1})"; return false; }
        if (after.Any(r => r.IsRecorder)) { why = "Recorder-Zeile noch vorhanden"; return false; }
        var others = before.Where(r => !r.IsRecorder).Select(r => Norm(r.Identity)).ToList();
        var now = after.Select(r => Norm(r.Identity)).ToList();
        if (!others.SequenceEqual(now)) { why = $"andere Zeilen veraendert: vorher [{string.Join(" | ", others)}] nachher [{string.Join(" | ", now)}]"; return false; }
        why = "";
        return true;
    }

    private static string Norm(string s) => Regex.Replace(s ?? "", @"\s+", " ").Trim().ToLowerInvariant();

    /// <summary>Symbol-Kandidaten in einem Zeilenbild: zusammenhaengende Spalten, die sich vom Zeilenhintergrund abheben
    /// und nicht im Textbereich liegen. Liefert Mittelpunkte im Bildkoordinatensystem.</summary>
    internal static List<(int cx, int cy, int w)> FindIconBlobs(Bitmap bmp, IReadOnlyList<(int x0, int x1)> textSpans)
    {
        int w = bmp.Width, h = bmp.Height;
        var counts = new Dictionary<int, int>();
        for (int y = 0; y < h; y += 2) for (int x = 0; x < w; x += 2) { int c = bmp.GetPixel(x, y).ToArgb(); counts[c] = counts.TryGetValue(c, out var n) ? n + 1 : 1; }
        var bg = Color.FromArgb(counts.OrderByDescending(k => k.Value).First().Key);
        bool Diff(Color c) => Math.Abs(c.R - bg.R) + Math.Abs(c.G - bg.G) + Math.Abs(c.B - bg.B) > 60;
        var col = new bool[w]; var top = new int[w]; var bot = new int[w];
        for (int x = 0; x < w; x++)
        {
            if (textSpans.Any(s => x >= s.x0 - 3 && x <= s.x1 + 3)) continue;
            int t = -1, b = -1;
            for (int y = 1; y < h - 1; y++) if (Diff(bmp.GetPixel(x, y))) { if (t < 0) t = y; b = y; }
            if (t >= 0 && b - t >= 3) { col[x] = true; top[x] = t; bot[x] = b; }
        }
        var blobs = new List<(int cx, int cy, int w)>();
        int i = 0;
        while (i < w)
        {
            if (!col[i]) { i++; continue; }
            int s = i, e = i, gap = 0;
            while (i + 1 < w && (col[i + 1] || gap < 2)) { i++; if (col[i]) { e = i; gap = 0; } else gap++; }
            int bw = e - s + 1;
            if (bw >= 6 && bw <= 40)
            {
                int t = Enumerable.Range(s, bw).Where(x => col[x]).Min(x => top[x]);
                int b = Enumerable.Range(s, bw).Where(x => col[x]).Max(x => bot[x]);
                blobs.Add(((s + e) / 2, (t + b) / 2, bw));
            }
            i = e + 1;
        }
        return blobs;
    }

    // ------------------------------------------------------------------ UIA/Screenshot-Teile

    private static AutomationElement? AddedList(AutomationElement dlg)
    {
        var outer = FindByAutomationId(dlg, "IndicatorsList");
        if (outer == null) return null;
        try { return outer.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "PART_Editor")) ?? outer; } catch { return outer; }
    }

    private static string SubtreeText(AutomationElement el)
    {
        var parts = new List<string>();
        void Add(string? s) { if (!string.IsNullOrWhiteSpace(s) && !s.StartsWith("OFT.Platform.", StringComparison.Ordinal)) parts.Add(s.Trim()); }
        try
        {
            var walker = TreeWalker.RawViewWalker;
            var stack = new Stack<(AutomationElement e, int d)>(); stack.Push((el, 0));
            int n = 0;
            while (stack.Count > 0 && n++ < 200)
            {
                var (e, d) = stack.Pop();
                Add(Safe(() => e.Current.Name)); Add(Safe(() => e.Current.HelpText)); Add(ValueOf(e));
                if (d >= 6) continue;
                var c = walker.GetFirstChild(e);
                while (c != null) { stack.Push((c, d + 1)); c = walker.GetNextSibling(c); }
            }
        }
        catch { }
        return string.Join(" ", parts.Distinct());
    }

    private static int HeaderCount(AutomationElement dlg) { try { return AddedState(dlg).count; } catch { return -1; } }

    /// <summary>Zeilen unter 'Hinzugefuegt' lesen: zuerst UIA (ListItem, sonst zeilenfoermige Elemente im Rohbaum), dann - wenn
    /// UIA weniger Zeilen liefert als die Kopfzahl - Texterkennung eines frischen Screenshots der Liste.</summary>
    private static (List<AddedRow> rows, int header, ListRead state, string source) ReadAddedRows(AutomationElement dlg, Action<string> W, bool ocr)
    {
        var rows = new List<AddedRow>();
        int header = HeaderCount(dlg);
        var list = AddedList(dlg);
        if (list == null) { W("  [rm] Liste 'Hinzugefuegt' (IndicatorsList) nicht gefunden."); return (rows, header, ListRead.Unreadable, "keine Liste"); }
        System.Windows.Rect lr; try { lr = list.Current.BoundingRectangle; } catch { lr = System.Windows.Rect.Empty; }

        // (a) UIA: ListItems, sonst zeilenfoermige Elemente (Hoehe 12..60, Breite >= 40 % der Liste) im Rohbaum.
        var elems = new List<AutomationElement>();
        try { foreach (AutomationElement it in list.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem))) elems.Add(it); } catch { }
        if (elems.Count == 0 && !lr.IsEmpty)
        {
            try
            {
                var walker = TreeWalker.RawViewWalker; var st = new Stack<(AutomationElement e, int d)>(); st.Push((list, 0)); int n = 0;
                var seenY = new HashSet<int>();
                while (st.Count > 0 && n++ < 600)
                {
                    var (e, d) = st.Pop();
                    System.Windows.Rect r; try { r = e.Current.BoundingRectangle; } catch { r = System.Windows.Rect.Empty; }
                    if (d > 0 && !r.IsEmpty && r.Height >= 12 && r.Height <= 60 && r.Width >= lr.Width * 0.4 && seenY.Add((int)Math.Round(r.Y / 4)))
                    { elems.Add(e); continue; }
                    if (d >= 8) continue;
                    var c = walker.GetFirstChild(e); while (c != null) { st.Push((c, d + 1)); c = walker.GetNextSibling(c); }
                }
            }
            catch { }
        }
        int i = 0;
        foreach (var it in elems.OrderBy(e => { try { return e.Current.BoundingRectangle.Y; } catch { return 0.0; } }))
        {
            System.Windows.Rect r; try { r = it.Current.BoundingRectangle; } catch { r = System.Windows.Rect.Empty; }
            string ut = SubtreeText(it), ot = "";
            if (ocr && !r.IsEmpty && r.Width > 10 && r.Height > 5)
            {
                try { using var bmp = CaptureRect(r); ot = string.Join(" ", ScreenOcr.Recognize(bmp, (int)r.X, (int)r.Y).Select(x => x.Text)); }
                catch (Exception ex) { ot = "(OCR-Fehler: " + ex.Message + ")"; }
            }
            rows.Add(new AddedRow(i++, r, ut, ot, it));
        }
        var state = ClassifyList(header, rows.Count, rows.All(r => r.Identity.Length > 0));
        if (state != ListRead.Unreadable || !ocr || lr.IsEmpty) return (rows, header, state, "UIA");

        // (b) Texterkennung der ganzen Liste (frischer Screenshot): Zeilen = Textzeilen.
        try
        {
            using var bmp = CaptureRect(lr);
            var words = ScreenOcr.Recognize(bmp, (int)Math.Round(lr.X), (int)Math.Round(lr.Y)).Select(w => (w.X, w.Y, w.W, w.H, w.Text));
            var lines = GroupOcrLines(words);
            var orows = lines.Select((l, k) => new AddedRow(k, new System.Windows.Rect(l.x0, l.y0 - 4, Math.Max(10, lr.X + lr.Width - l.x0), (l.y1 - l.y0) + 8), "", l.text)).ToList();
            var ostate = ClassifyList(header, orows.Count, orows.All(r => r.Identity.Length > 0));
            W($"  [rm] UIA lieferte {rows.Count} Zeile(n) bei Kopfzahl {header} -> Texterkennung der Liste: {orows.Count} Zeile(n), Zustand {ostate}.");
            return (orows, header, ostate, "OCR");
        }
        catch (Exception ex) { W($"  [rm] Texterkennung der Liste fehlgeschlagen: {ex.Message}"); return (rows, header, ListRead.Unreadable, "UIA (OCR fehlgeschlagen)"); }
    }

    private static Bitmap CaptureRect(System.Windows.Rect r)
    {
        int w = Math.Max(1, (int)Math.Round(r.Width)), h = Math.Max(1, (int)Math.Round(r.Height));
        var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen((int)Math.Round(r.X), (int)Math.Round(r.Y), 0, 0, new System.Drawing.Size(w, h));
        return bmp;
    }

    private static string RowsLine(IEnumerable<AddedRow> rows) =>
        string.Join(" | ", rows.Select(r => $"#{r.Index}{(r.IsRecorder ? "*" : "")} '{Trunc(r.Identity, 50)}' @{(int)r.Rect.X},{(int)r.Rect.Y} {(int)r.Rect.Width}x{(int)r.Rect.Height}"));

    private static string? CurrentToolTip()
    {
        try
        {
            var tips = AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ToolTip));
            foreach (AutomationElement t in tips) { var n = Safe(() => t.Current.Name); if (!string.IsNullOrWhiteSpace(n)) return n; var s = SubtreeText(t); if (s != "") return s; }
            foreach (var root in AtasRoots())
            {
                var t2 = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ToolTip));
                if (t2 != null) { var s = SubtreeText(t2); if (s != "") return s; }
            }
        }
        catch { }
        return null;
    }

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

    private static string FailArtifacts(Action<string> W, AutomationElement? dlg, string tag, string clickInfo)
    {
        try
        {
            var logs = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(RecordingsDir)) ?? AppContext.BaseDirectory, "Logs");
            Directory.CreateDirectory(logs);
            var png = Path.Combine(logs, $"recorder_remove_{tag}_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            System.Windows.Rect r = System.Windows.Rect.Empty; uint dpi = 0;
            if (dlg != null) { try { r = dlg.Current.BoundingRectangle; dpi = GetDpiForWindow(new IntPtr(dlg.Current.NativeWindowHandle)); } catch { } }
            if (!r.IsEmpty) Shot((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height, png);
            else { using var b = ScreenOcr.CapturePrimary(out _, out _); b.Save(png, System.Drawing.Imaging.ImageFormat.Png); }
            W($"  [rm] Screenshot: {png}  dialog={(int)r.X},{(int)r.Y},{(int)r.Width}x{(int)r.Height} dpi={dpi} klickziel={clickInfo}");
            return png;
        }
        catch (Exception ex) { W($"  [rm] Screenshot nicht moeglich: {ex.Message}  klickziel={clickInfo}"); return ""; }
    }

    private static void CancelDialog(AutomationElement dlg, Action<string> W)
    {
        var cancel = FindBtnIn(dlg, "Abbrechen");
        if (cancel != null && Invoke(cancel)) { W("  [rm] 'Abbrechen' ausgeloest -> keine Aenderung am Chart uebernommen."); Thread.Sleep(800); return; }
        var close = FindByAutomationId(dlg, "PART_CloseButton");
        if (close != null && Invoke(close)) { W("  [rm] Dialog ohne Uebernehmen geschlossen."); Thread.Sleep(800); return; }
        W("  [rm] WARNUNG: Dialog konnte nicht ohne Uebernehmen geschlossen werden - bitte in ATAS 'Abbrechen' druecken.");
    }

    // ------------------------------------------------------------------ Ablauf

    /// <summary>Entfernt genau den Recorder vom Zielchart. Jeder Schritt verifiziert; bei Fehler Abbrechen + Screenshot.</summary>
    private static RemovalOutcome RemoveRecorderFromChart(Action<string> W, int writerWaitSec = 60)
    {
        var swRm = Stopwatch.StartNew();
        W($"  [rm] Beginn {DateTime.Now:HH:mm:ss.fff}");
        try { return RemoveRecorderFromChartCore(W, writerWaitSec); }
        finally { W($"  [rm] Ende {DateTime.Now:HH:mm:ss.fff} ({swRm.ElapsedMilliseconds} ms)"); }
    }

    private static RemovalOutcome RemoveRecorderFromChartCore(Action<string> W, int writerWaitSec)
    {
        var lockedBefore = Directory.Exists(RecordingsDir)
            ? Directory.GetDirectories(RecordingsDir).Where(d => IsMetaLocked(Path.Combine(d, "meta.json"))).ToList() : new List<string>();

        if (!OpenIndicatorDialog(W)) { W("  [rm] BLOCKER: Indikator-Dialog nicht zugaenglich."); FailArtifacts(W, null, "dialog", "-"); return RemovalOutcome.Failed; }
        var dlg = IndicatorDialog();
        if (dlg == null) { W("  [rm] BLOCKER: Indikator-Dialog nicht gefunden."); FailArtifacts(W, null, "dialog", "-"); return RemovalOutcome.Failed; }
        ForceForeground(dlg); Thread.Sleep(400);

        // 1) Recorder-Zeile eindeutig bestimmen (UIA-Text, sonst OCR des frischen Zeilenbilds).
        var (rows0, header0, state0, src0) = ReadAddedRows(dlg, W, ocr: true);
        W($"  [rm] Hinzugefuegt: Kopfzahl={header0} gelesen={rows0.Count} ({src0}) Zustand={state0}: {RowsLine(rows0)}");
        if (state0 == ListRead.Unreadable)
        {
            W("  [rm] BLOCKER: Liste 'Hinzugefuegt' nicht zuverlaessig lesbar (eine leere UIA-Abfrage beweist keine leere Liste) -> kein Klick.");
            FailArtifacts(W, dlg, "liste", "-"); CancelDialog(dlg, W); return RemovalOutcome.Failed;
        }
        var recRows = rows0.Where(r => r.IsRecorder).ToList();
        if (recRows.Count == 0)
        {
            W(state0 == ListRead.Empty ? "  [rm] Liste 'Hinzugefuegt' ist leer (Kopfzahl 0) -> kein Recorder am Chart."
                                       : "  [rm] Liste gelesen, keine Recorder-Zeile darin.");
            CancelDialog(dlg, W); return RemovalOutcome.NoRecorder;
        }
        if (recRows.Count > 1) { W($"  [rm] BLOCKER: {recRows.Count} Recorder-Zeilen - nicht eindeutig."); FailArtifacts(W, dlg, "zeile", "-"); CancelDialog(dlg, W); return RemovalOutcome.Failed; }
        var rec = recRows[0];

        // 2) Zeile auswaehlen (UIA) + Mauszeiger auf die Zeile (Aktionssymbole erscheinen).
        var item = rec.Element;
        bool selected = false;
        if (item != null)
        {
            try { if (item.TryGetCurrentPattern(ScrollItemPattern.Pattern, out var sp)) ((ScrollItemPattern)sp).ScrollIntoView(); } catch { }
            try { if (item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sel)) { ((SelectionItemPattern)sel).Select(); Thread.Sleep(250); selected = ((SelectionItemPattern)sel).Current.IsSelected; } } catch { }
        }
        System.Windows.Rect rr = rec.Rect; if (item != null) { try { rr = item.Current.BoundingRectangle; } catch { rr = rec.Rect; } }
        if (rr.IsEmpty) { W("  [rm] BLOCKER: Recorder-Zeile ohne Bildschirmposition."); FailArtifacts(W, dlg, "zeile", "-"); CancelDialog(dlg, W); return RemovalOutcome.Failed; }
        ForceForeground(dlg); Thread.Sleep(150);
        // Hover/Klick auf den TEXT der Zeile (erstes erkanntes Wort) - waehlt hoechstens die Zeile, loest kein Symbol aus.
        int hx = (int)(rr.X + Math.Min(60, rr.Width / 5)), hy = (int)(rr.Y + rr.Height / 2);
        try
        {
            using var rb = CaptureRect(rr);
            var fw = ScreenOcr.Recognize(rb, (int)Math.Round(rr.X), (int)Math.Round(rr.Y)).OrderBy(w => w.X).FirstOrDefault();
            if (fw != null) { hx = fw.CX; hy = fw.CY; }
        }
        catch { }
        SetCursorPos(hx, hy); Thread.Sleep(700);
        if (!selected) { ClickScreen(hx, hy); Thread.Sleep(400); }
        W($"  [rm] Recorder-Zeile #{rec.Index} ausgewaehlt={selected} rect={(int)rr.X},{(int)rr.Y},{(int)rr.Width}x{(int)rr.Height} hover=({hx},{hy}) dpi={SafeDpi(dlg)}");

        // 3) Papierkorb in DIESER Zeile: (a) UIA-Schaltflaeche, (b) Symbol im frischen Screenshot + Tooltip-Pruefung.
        (int x, int y)? target = null; string how = "";
        AutomationElement? uiaBtn = null;
        try
        {
            var btns = new List<AutomationElement>();
            var walker = TreeWalker.RawViewWalker; var st = new Stack<AutomationElement>(); if (item != null) st.Push(item); int n = 0;
            while (st.Count > 0 && n++ < 300) { var e = st.Pop(); if (Safe(() => e.Current.ControlType.ProgrammaticName) == "ControlType.Button") btns.Add(e); var c = walker.GetFirstChild(e); while (c != null) { st.Push(c); c = walker.GetNextSibling(c); } }
            foreach (var b in btns)
                W($"    UIA-Schaltflaeche in Zeile: name='{Safe(() => b.Current.Name)}' aid='{Safe(() => b.Current.AutomationId)}' help='{Safe(() => b.Current.HelpText)}' rect={Rect(b)}");
            var del = btns.Where(b => DeleteWords.IsMatch(Safe(() => b.Current.Name) + " " + Safe(() => b.Current.AutomationId) + " " + Safe(() => b.Current.HelpText))).ToList();
            if (del.Count == 1) { uiaBtn = del[0]; how = "UIA-Schaltflaeche '" + Safe(() => uiaBtn.Current.Name) + Safe(() => uiaBtn.Current.AutomationId) + "'"; }
            else if (del.Count > 1) W($"  [rm] {del.Count} Loeschen-Schaltflaechen in der Zeile -> nicht eindeutig, visuelle Pruefung.");
        }
        catch (Exception ex) { W($"  [rm] UIA-Suche in der Zeile: {ex.GetType().Name}: {ex.Message}"); }

        if (uiaBtn == null)
        {
            using var bmp = CaptureRect(rr);
            List<(int x0, int x1)> spans;
            try { spans = ScreenOcr.Recognize(bmp, 0, 0).Select(o => (o.X, o.X + o.W)).ToList(); } catch { spans = new(); }
            var blobs = FindIconBlobs(bmp, spans);
            W($"  [rm] Symbole im Zeilenbild (frischer Screenshot): {string.Join(", ", blobs.Select(b => $"x+{b.cx}/y+{b.cy} b={b.w}"))}{(blobs.Count == 0 ? "(keine)" : "")}");
            foreach (var b in blobs.OrderByDescending(b => b.cx))
            {
                int sx = (int)Math.Round(rr.X) + b.cx, sy = (int)Math.Round(rr.Y) + b.cy;
                SetCursorPos(sx, sy); Thread.Sleep(1300);
                var tip = CurrentToolTip();
                W($"    Symbol @({sx},{sy}) Tooltip='{tip ?? "(keiner)"}'");
                if (tip != null && DeleteWords.IsMatch(tip)) { target = (sx, sy); how = $"Symbol mit Tooltip '{tip}'"; break; }
            }
            if (target == null)
            {
                W("  [rm] BLOCKER: Papierkorb in der Recorder-Zeile nicht bestaetigt (keine UIA-Schaltflaeche, kein Symbol mit Loeschen-Tooltip) -> KEIN Klick.");
                FailArtifacts(W, dlg, "papierkorb", "keins");
                CancelDialog(dlg, W);
                return RemovalOutcome.Failed;
            }
        }

        // Klick bzw. Invoke
        string clickInfo;
        if (uiaBtn != null)
        {
            System.Windows.Rect br; try { br = uiaBtn.Current.BoundingRectangle; } catch { br = System.Windows.Rect.Empty; }
            clickInfo = $"{how} rect={(int)br.X},{(int)br.Y},{(int)br.Width}x{(int)br.Height}";
            bool ok = false; try { ok = Invoke(uiaBtn); } catch { }
            if (!ok && !br.IsEmpty) { ClickScreen((int)(br.X + br.Width / 2), (int)(br.Y + br.Height / 2)); clickInfo += " (Klick, Invoke nicht verfuegbar)"; }
        }
        else
        {
            clickInfo = $"{how} @({target!.Value.x},{target.Value.y})";
            ForceForeground(dlg); Thread.Sleep(150);
            ClickScreen(target.Value.x, target.Value.y);
        }
        W($"  [rm] Papierkorb ausgeloest: {clickInfo}");
        Thread.Sleep(1200);

        // 4) Wirkung pruefen: genau die Recorder-Zeile weg, andere Zeilen unveraendert. Sonst Abbrechen.
        var dlg2 = IndicatorDialog() ?? dlg;
        var (rows1, header1, state1, src1) = ReadAddedRows(dlg2, W, ocr: true);
        W($"  [rm] Hinzugefuegt nachher: Kopfzahl={header1} gelesen={rows1.Count} ({src1}) Zustand={state1}: {RowsLine(rows1)}");
        string why;
        bool headerOk = header0 < 0 || header1 == header0 - 1;
        if (state1 == ListRead.Unreadable || !headerOk || !OnlyRecorderRemoved(rows0, rows1, out why))
        {
            why = state1 == ListRead.Unreadable ? "Liste nachher nicht lesbar" : !headerOk ? $"Kopfzahl {header0} -> {header1}" : (OnlyRecorderRemoved(rows0, rows1, out var w2) ? "" : w2);
        }
        else why = "";
        if (why != "")
        {
            W($"  [rm] BLOCKER: Entfernen nicht bestaetigt ({why}).");
            FailArtifacts(W, dlg2, "pruefung", clickInfo);
            CancelDialog(dlg2, W);
            return RemovalOutcome.Failed;
        }
        W("  [rm] bestaetigt: genau die Recorder-Zeile entfernt, andere Indikatoren unveraendert.");

        // 5) Erst jetzt Uebernehmen.
        var apply = FindBtnIn(dlg2, "Übernehmen");
        if (apply == null || !Invoke(apply)) { W("  [rm] BLOCKER: 'Uebernehmen' nicht ausloesbar."); FailArtifacts(W, dlg2, "uebernehmen", clickInfo); CancelDialog(dlg2, W); return RemovalOutcome.Failed; }
        W("  [rm] 'Uebernehmen' ausgeloest.");
        for (int i = 0; i < 10 && IndicatorDialog() != null; i++) Thread.Sleep(500);

        // Writer-Abschluss der zuvor aktiven Instanz(en) + keine verbliebene eigene Instanz.
        int wait = Math.Max(15, writerWaitSec);
        for (int i = 0; i < wait && lockedBefore.Any(d => IsMetaLocked(Path.Combine(d, "meta.json"))); i++) Thread.Sleep(1000);
        bool writersDone = true;
        foreach (var d in lockedBefore)
        {
            var sp = Path.Combine(d, "summary.json");
            bool locked = IsMetaLocked(Path.Combine(d, "meta.json")), has = File.Exists(sp);
            bool clean = has && SummaryCleanClose(ReadAllShared(sp));
            W($"  [rm] Writer {Path.GetFileName(d)}: beendet={!locked} summary={has} sauber={clean}");
            writersDone &= !locked && has && clean;
        }
        if (lockedBefore.Count == 0) W($"  [rm] vorher keine aktive (gesperrte) Recorder-Instanz unter {RecordingsDir}.");
        int lockedNow = LockedCount();

        // Gegenprobe im Dialog: keine Recorder-Zeile mehr; Dialog ohne Aenderung schliessen.
        bool stillListed = false;
        if (OpenIndicatorDialog(W) && IndicatorDialog() is { } dlg3)
        {
            var (rows2, header2, state2, src2) = ReadAddedRows(dlg3, W, ocr: true);
            stillListed = state2 == ListRead.Unreadable || rows2.Any(r => r.IsRecorder);
            W($"  [rm] Gegenprobe Hinzugefuegt: Kopfzahl={header2} gelesen={rows2.Count} ({src2}) Zustand={state2}: {RowsLine(rows2)}");
            CancelDialog(dlg3, W);
        }
        bool ok2 = writersDone && lockedNow == 0 && !stillListed;
        W($"  [rm] ERGEBNIS: Recorder entfernt={!stillListed}, Writer abgeschlossen={writersDone}, aktive Instanzen jetzt={lockedNow} -> {(ok2 ? "BESTAETIGT" : "NICHT BESTAETIGT")}");
        if (!ok2) FailArtifacts(W, null, "abschluss", clickInfo);
        return ok2 ? RemovalOutcome.Removed : RemovalOutcome.Failed;
    }

    private static string SafeDpi(AutomationElement el) { try { return GetDpiForWindow(new IntPtr(el.Current.NativeWindowHandle)).ToString(); } catch { return "?"; } }

    // ------------------------------------------------------------------ Einzelbefehl: nur den vorhandenen Recorder entfernen

    private static int RemoveRecorderCommand(string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); if (outFile != null) { try { File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(true)); } catch { } } }
        W($"# Recorder gezielt entfernen  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  Probe {typeof(Program).Assembly.GetName().Version}");
        W($"# Aufnahmeordner: {RecordingsDir} | {RecorderIdentity()}");
        if (Process.GetProcessesByName("OFT.Platform").Length == 0) { W("BLOCKER: ATAS laeuft nicht."); Flush(sb, outFile); return 3; }
        CloseDateDialogIfOpen(W);
        if (ReplayOpen() || !IndicatorsEnabled()) { W("[rmrec] Replay-Ansicht verlassen"); ExitReplayView(W); for (int i = 0; i < 15 && !IndicatorsEnabled(); i++) Thread.Sleep(1000); }
        var res = RemoveRecorderFromChart(W);
        W($"\n===== {res} =====");
        Flush(sb, outFile);
        return res switch { RemovalOutcome.Removed => 0, RemovalOutcome.NoRecorder => 0, _ => 8 };
    }
}
