using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace AtasReplayProbe;

// 1.3.6: Zielchart, wenn die UIA-Kontrollansicht keine Chart-Flaeche liefert.
//
// Befunde:
//  - 17.11.2026 (erfolgreich): UIA-Kontrollansicht enthielt das Panel 'CustomLayoutPanel' mit dem NAMEN 'MNQ 5m Chart'
//    und darin die namenlose Zeichenflaeche 'ChartView' (aid='Root', Custom, WPF). Gewaehlt wurde die innerste
//    chart-artige Flaeche (ChartView); der Instrumentbezug steckte im Namen des UMGEBENDEN Panels. Der sichtbare Titel
//    kann im Panel OBERHALB der Zeichenflaeche liegen.
//  - 18.11.2026 (nach ATAS-Neustart, Direct2D wie am 17.11.): weder Panel-Name noch ChartView in der Kontrollansicht;
//    nur zwei namenlose WinForms-Panes mit identischem Rechteck. Zwei deckungsgleiche Fenster beweisen weder einen noch
//    zwei Charts.
//
// Ersatzweg (liest nur, klickt nichts):
//  1) Rohansicht (RawView) zusaetzlich zur Kontrollansicht: Elemente mit IsControlElement=false fehlen der
//     Kontrollansicht. Chart-artige Treffer dort -> bisherige Regeln (genau eine sichtbare Flaeche).
//  2) Genau EINE grosse Flaeche (deckungsgleiche Fenster nur, wenn VERSCHACHTELT = Host + Zeichenflaeche; nebeneinander
//     liegende deckungsgleiche Fenster, z. B. Charts in Tabs -> unklar).
//  3) Instrumentbezug, in dieser Reihenfolge:
//     a) UIA-Name eines VORFAHREN der Flaeche (z. B. Panel 'MNQ 5m Chart'), Panel chartgross (nicht das Hauptfenster);
//     b) Texterkennung in der Flaeche UND in der Titelzeile darueber. Zuordnung je Treffer: in der Flaeche per Win32-
//        Treffertest (Fenster unter dem Titel = Flaeche oder ihr Kind, ATAS-Prozess); in der Titelzeile per UIA: das
//        Element unter dem Titel und die Flaeche haben einen gemeinsamen, chartgrossen Vorfahren (dasselbe Panel).
//        Gleiche Koordinaten allein reichen nie.
//  4) Chart-Namen im ATAS-Protokoll seit dem ATAS-Start duerfen nicht widersprechen.
internal static partial class Program
{
    internal sealed record Surface(string Name, string Cls, string Aid, string Type, string Fw, System.Windows.Rect R, int Hwnd, bool RawOnly, AutomationElement? El = null);

    internal sealed record OcrHit(string Text, int X, int Y, bool BelongsToSurface, string HitInfo);

    internal sealed record OcrChartVerdict(bool Unique, string Reason, string Signal);

    /// <summary>Verhaeltnis mehrerer UIA-Knoten einer Flaeche (gleiches Rechteck).</summary>
    internal enum RegionRelation { Single, Nested, Siblings, Unknown }

    private const double PanelMaxAreaFactor = 2.0;   // "chartgross": hoechstens doppelte Flaeche der Zeichenflaeche
    private const int TitleBandPx = 80;              // Titelzeile oberhalb der Zeichenflaeche

    internal static string TargetInstrument =>
        (Environment.GetEnvironmentVariable("ATAS_TARGET_INSTRUMENT") ?? "MNQ").Trim().ToUpperInvariant() is { Length: > 0 } s ? s : "MNQ";

    /// <summary>Wort ist ein Instrumenttitel des Ziels (MNQ, MNQZ6, MNQZ26, MNQ-12-26 …), nicht nur ein Teilwort.</summary>
    internal static bool IsTargetToken(string word, string target)
    {
        var w = (word ?? "").Trim().Trim('(', ')', '[', ']', ',', ';', ':', '.', '"', '\'', '#', '•', '*');   // lokal gelesen: '#MNQZ6'
        return Regex.IsMatch(w, "^" + Regex.Escape(target) + @"([A-Z]\d{1,2}|[-_ ]?\d{1,2}([-_.]\d{2,4})?)?$", RegexOptions.IgnoreCase);
    }

    /// <summary>Name enthaelt das Ziel als eigenes Wort (z. B. 'MNQ 5m Chart', 'MNQZ6 - 5 Min').</summary>
    internal static bool NameHasTarget(string name, string target) =>
        (name ?? "").Split(new[] { ' ', '\t', '|', '/' }, StringSplitOptions.RemoveEmptyEntries).Any(t => IsTargetToken(t, target));

    /// <summary>Chart-Namen aus Quellen des ATAS-Protokolls (z. B. 'MNQ 5m Chart'), nur Zeilen seit dem ATAS-Start.</summary>
    internal static List<string> ChartSourcesFromLog(IEnumerable<string> lines, DateTime since)
    {
        var set = new List<string>();
        foreach (var raw in lines)
        {
            var l = AtasLogLine.Parse(raw);
            if (l == null || l.Ts < since) continue;
            if (l.Source.EndsWith(" Chart", StringComparison.Ordinal) && l.Source.Length > 6 && !set.Contains(l.Source)) set.Add(l.Source);
        }
        return set;
    }

    /// <summary>Panel ist "chartgross": umschliesst die Flaeche und ist hoechstens PanelMaxAreaFactor-mal so gross.</summary>
    internal static bool IsPanelOf(System.Windows.Rect panel, System.Windows.Rect surface) =>
        !panel.IsEmpty && panel.X <= surface.X + 1 && panel.Y <= surface.Y + 1 && panel.Right >= surface.Right - 1 && panel.Bottom >= surface.Bottom - 1
        && panel.Width * panel.Height <= surface.Width * surface.Height * PanelMaxAreaFactor;

    /// <summary>Reine Entscheidung (getestet).</summary>
    internal static OcrChartVerdict DecideChartFallback(int regions, RegionRelation rel, string? namedAncestor, IReadOnlyList<OcrHit> targetHits,
                                                        IReadOnlyList<string> logCharts, string target)
    {
        if (regions == 0) return new(false, "keine grosse sichtbare Flaeche im ATAS-Fenster", "");
        if (regions > 1) return new(false, $"{regions} verschiedene grosse Flaechen -> nicht eindeutig (es wird nichts angeklickt)", "");
        if (rel == RegionRelation.Siblings) return new(false, "deckungsgleiche, NICHT verschachtelte Fenster (z. B. Charts in Tabs) -> ein oder mehrere Charts? unklar", "");
        if (rel == RegionRelation.Unknown) return new(false, "Verhaeltnis der deckungsgleichen Fenster nicht bestimmbar -> unklar", "");
        if (logCharts.Count > 0 && !logCharts.Any(c => NameHasTarget(c, target)))
            return new(false, $"ATAS-Protokoll nennt nur andere Charts ({string.Join(", ", logCharts)}) -> Widerspruch", "");
        var foreign = targetHits.Where(h => !h.BelongsToSurface).ToList();
        if (foreign.Count > 0) return new(false, $"'{target}'-Text gefunden, gehoert aber nicht zu dieser Flaeche ({foreign[0].HitInfo}) -> unklar", "");
        string relTxt = rel == RegionRelation.Nested ? "deckungsgleiche Fenster verschachtelt (Host + Zeichenflaeche)" : "eine Flaeche";
        string logPart = logCharts.Count > 0 ? $"; ATAS-Protokoll: {string.Join(", ", logCharts)}" : "; ATAS-Protokoll nennt keinen Chart";
        if (namedAncestor != null)
            return new(true, $"{relTxt}; umgebendes Panel heisst per UIA '{namedAncestor}'{(targetHits.Count > 0 ? $"; Titel auch per Texterkennung '{targetHits[0].Text}'" : "")}{logPart}",
                       $"UIA-Name des umgebenden Panels '{namedAncestor}'");
        if (targetHits.Count == 0) return new(false, $"kein '{target}'-Titel: weder UIA-Name eines umgebenden Panels noch Texterkennung (Flaeche + Titelzeile)", "");
        var h0 = targetHits[0];
        return new(true, $"{relTxt}; Titel '{h0.Text}' per Texterkennung @{h0.X},{h0.Y}; Zuordnung: {h0.HitInfo}{logPart}", $"Titel '{h0.Text}' per Texterkennung + Zuordnung");
    }

    // ------------------------------------------------------------------ UIA: grosse Flaechen (Kontroll- + Rohansicht)

    internal static (List<Surface> big, List<ChartCand> rawChartLike, string info) ScanSurfaces(Action<string>? W = null)
    {
        var big = new List<Surface>(); var rawLike = new List<ChartCand>();
        int ctl = 0, raw = 0; var errs = new List<string>();
        foreach (var r in AtasRoots())
        {
            string wn = Safe(() => r.Current.Name);
            foreach (var rawView in new[] { false, true })
            {
                AutomationElementCollection? col = null;
                try
                {
                    col = Bounded($"UIA-{(rawView ? "Rohansicht" : "Kontrollansicht")} '{wn}'", () =>
                    {
                        // Eigenschaften im Cache (ein Aufruf statt einer Prozessgrenze je Eigenschaft und Element).
                        var cr = new CacheRequest { TreeFilter = rawView ? Automation.RawViewCondition : Automation.ControlViewCondition, AutomationElementMode = AutomationElementMode.Full };
                        foreach (var p in new AutomationProperty[] { AutomationElement.NameProperty, AutomationElement.ClassNameProperty, AutomationElement.AutomationIdProperty,
                                 AutomationElement.ControlTypeProperty, AutomationElement.FrameworkIdProperty, AutomationElement.BoundingRectangleProperty,
                                 AutomationElement.IsOffscreenProperty, AutomationElement.NativeWindowHandleProperty, AutomationElement.IsControlElementProperty })
                            cr.Add(p);
                        using (cr.Activate()) return r.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                    }, 20000, null, W);
                }
                catch (Exception ex) { errs.Add($"{(rawView ? "Roh" : "Kontroll")}ansicht '{wn}': {ex.GetType().Name}: {ex.Message}"); }
                if (col == null) { errs.Add($"{(rawView ? "Roh" : "Kontroll")}ansicht '{wn}': keine Antwort"); continue; }
                if (rawView) raw += col.Count; else ctl += col.Count;
                foreach (AutomationElement e in col)
                {
                    try
                    {
                        var cur = e.Cached; var b = cur.BoundingRectangle;
                        string nm = cur.Name ?? "", cls = cur.ClassName ?? "", aid = cur.AutomationId ?? "";
                        string type = cur.ControlType.ProgrammaticName.Replace("ControlType.", "");
                        bool isNew = !big.Any(s => s.Hwnd == cur.NativeWindowHandle && s.Cls == cls && s.R == b && s.Aid == aid);
                        if (rawView && !cur.IsControlElement)
                        {
                            var cand = new ChartCand(nm, cls, aid, type, cur.FrameworkId ?? "", b.IsEmpty ? -1e9 : b.X, b.IsEmpty ? -1e9 : b.Y,
                                b.IsEmpty ? 0 : b.Width, b.IsEmpty ? 0 : b.Height, cur.IsOffscreen, wn, false);
                            if (IsVulkanSurface(cand) || IsChartLikeSurface(cand)) rawLike.Add(cand);
                        }
                        if (b.IsEmpty || cur.IsOffscreen || b.Width < 400 || b.Height < 250 || !isNew || !SurfaceTypes.Contains(type)) continue;
                        big.Add(new Surface(nm, cls, aid, type, cur.FrameworkId ?? "", b, cur.NativeWindowHandle, rawView, e));
                    }
                    catch (Exception ex) { if (errs.Count < 20) errs.Add($"Element: {ex.GetType().Name}: {ex.Message}"); }
                }
            }
        }
        string info = $"Kontrollansicht {ctl} Elemente, Rohansicht {raw} Elemente; nur in der Rohansicht chart-artig: {rawLike.Count}" +
                      (errs.Count > 0 ? "; Fehler: " + string.Join(" | ", errs.Take(5)) : "; keine UIA-Fehler");
        return (big, rawLike, info);
    }

    /// <summary>Innerste grosse Flaechen; identische Rechtecke als EINE Kandidaten-Flaeche gruppiert (Verhaeltnis wird
    /// danach eigens geprueft - deckungsgleich allein beweist nichts).</summary>
    internal static List<List<Surface>> SurfaceRegions(List<Surface> big)
    {
        bool Contains(Surface o, Surface i) => !ReferenceEquals(o, i) && i.R.X >= o.R.X - 1 && i.R.Y >= o.R.Y - 1
            && i.R.Right <= o.R.Right + 1 && i.R.Bottom <= o.R.Bottom + 1 && (i.R.Width < o.R.Width - 1 || i.R.Height < o.R.Height - 1);
        var inner = big.Where(s => !big.Any(d => Contains(s, d))).ToList();
        return inner.GroupBy(s => (Math.Round(s.R.X), Math.Round(s.R.Y), Math.Round(s.R.Width), Math.Round(s.R.Height))).Select(g => g.ToList()).ToList();
    }

    // ------------------------------------------------------------------ Win32/UIA-Zuordnung (liest nur)

    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point p);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);

    private static List<AutomationElement> Ancestors(AutomationElement el, int max = 40)
    {
        var res = new List<AutomationElement>();
        var w = TreeWalker.RawViewWalker; var cur = el;
        for (int i = 0; i < max; i++)
        {
            AutomationElement? p; try { p = w.GetParent(cur); } catch { break; }
            if (p == null || Automation.Compare(p, AutomationElement.RootElement)) break;
            res.Add(p); cur = p;
        }
        return res;
    }

    private static bool IsAncestor(AutomationElement anc, AutomationElement el) => Ancestors(el).Any(a => Automation.Compare(a, anc));

    /// <summary>Verhaeltnis der Knoten einer Flaeche und der innerste Knoten (Zeichenflaeche).</summary>
    private static (RegionRelation rel, Surface inner, string info) Relation(List<Surface> g)
    {
        if (g.Count == 1) return (RegionRelation.Single, g[0], "ein Knoten");
        var notes = new List<string>(); bool siblings = false, unknown = false;
        Surface inner = g[0];
        for (int i = 0; i < g.Count; i++)
            for (int j = i + 1; j < g.Count; j++)
            {
                Surface a = g[i], b = g[j];
                if (a.Hwnd != 0 && a.Hwnd == b.Hwnd) { notes.Add($"0x{a.Hwnd:X} zweimal (gleiches Fenster)"); continue; }
                bool aOverB, bOverA;
                if (a.Hwnd != 0 && b.Hwnd != 0) { aOverB = IsChild(new IntPtr(a.Hwnd), new IntPtr(b.Hwnd)); bOverA = IsChild(new IntPtr(b.Hwnd), new IntPtr(a.Hwnd)); }
                else if (a.El != null && b.El != null) { aOverB = IsAncestor(a.El, b.El); bOverA = IsAncestor(b.El, a.El); }
                else { unknown = true; notes.Add("Verhaeltnis nicht bestimmbar"); continue; }
                if (aOverB) { inner = b; notes.Add($"0x{b.Hwnd:X} ist Kind von 0x{a.Hwnd:X}"); }
                else if (bOverA) { inner = a; notes.Add($"0x{a.Hwnd:X} ist Kind von 0x{b.Hwnd:X}"); }
                else { siblings = true; notes.Add($"0x{a.Hwnd:X} und 0x{b.Hwnd:X} NICHT verschachtelt (Geschwister)"); }
            }
        // Win32-Sichtbarkeit zusaetzlich protokollieren (verdeckte Tabs)
        foreach (var s in g.Where(s => s.Hwnd != 0)) notes.Add($"0x{s.Hwnd:X} sichtbar={IsWindowVisible(new IntPtr(s.Hwnd))}");
        var rel = siblings ? RegionRelation.Siblings : unknown ? RegionRelation.Unknown : RegionRelation.Nested;
        return (rel, inner, string.Join("; ", notes));
    }

    private static List<OcrWord> OcrRegion(System.Windows.Rect r, string? savePng)
    {
        int w = Math.Max(1, (int)Math.Round(r.Width)), h = Math.Max(1, (int)Math.Round(r.Height));
        using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen((int)Math.Round(r.X), (int)Math.Round(r.Y), 0, 0, new Size(w, h));
        if (savePng != null) { try { bmp.Save(savePng, System.Drawing.Imaging.ImageFormat.Png); } catch { } }
        // 2x vergroessert: Chart-Beschriftungen sind klein; Koordinaten danach zurueckgerechnet.
        using var big = new Bitmap(w * 2, h * 2);
        using (var g = Graphics.FromImage(big)) { g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic; g.DrawImage(bmp, 0, 0, w * 2, h * 2); }
        return ScreenOcr.Recognize(big, 0, 0).Select(o => new OcrWord
        { Text = o.Text, X = (int)Math.Round(r.X) + o.X / 2, Y = (int)Math.Round(r.Y) + o.Y / 2, W = Math.Max(1, o.W / 2), H = Math.Max(1, o.H / 2) }).ToList();
    }

    /// <summary>Treffer IN der Zeichenflaeche: Fenster unter dem Punkt = Flaeche oder ihr Kind, im ATAS-Prozess.</summary>
    private static (bool belongs, string info) HitInSurface(int x, int y, List<Surface> region)
    {
        var hit = WindowFromPoint(new Point(x, y));
        if (hit == IntPtr.Zero) return (false, "kein Fenster am Punkt");
        GetWindowThreadProcessId(hit, out uint pid);
        bool atas = Process.GetProcessesByName("OFT.Platform").Any(p => p.Id == pid);
        var mine = region.Where(s => s.Hwnd != 0).Select(s => new IntPtr(s.Hwnd)).Distinct().ToList();
        bool belongs = atas && mine.Any(m => m == hit || IsChild(m, hit));
        return (belongs, $"Win32-Treffertest: Fenster unter dem Titel 0x{hit.ToInt64():X} '{ClassOf(hit)}' ATAS={atas} -> {(belongs ? "gehoert zur Flaeche" : "NICHT die Flaeche")}");
    }

    /// <summary>Treffer in der TITELZEILE ueber der Flaeche: UIA-Element unter dem Titel und die Flaeche haben einen
    /// gemeinsamen, chartgrossen Vorfahren (dasselbe Panel).</summary>
    private static (bool belongs, string info) HitInTitleBand(int x, int y, Surface inner, List<AutomationElement> surfAnc)
    {
        var at = Bounded("UIA-Element unter dem Titel", () => AutomationElement.FromPoint(new System.Windows.Point(x, y)), 4000, null);
        if (at == null) return (false, "UIA: kein Element unter dem Titel");
        var chain = new List<AutomationElement> { at }; chain.AddRange(Ancestors(at));
        foreach (var a in chain)
        {
            var hitAnc = surfAnc.FirstOrDefault(s => Automation.Compare(s, a));
            if (hitAnc == null) continue;
            var pr = SafeRect(hitAnc);
            string pn = Safe(() => hitAnc.Current.Name), pc = Safe(() => hitAnc.Current.ClassName);
            bool panel = IsPanelOf(pr, inner.R);
            return (panel, $"UIA: gemeinsamer Vorfahr von Titel und Flaeche = '{pn}' ({pc}) {(int)pr.Width}x{(int)pr.Height} -> {(panel ? "dasselbe Chart-Panel" : "zu gross (Hauptfenster/Layout), keine Zuordnung")}");
        }
        return (false, "UIA: Titel und Flaeche haben keinen gemeinsamen Vorfahren");
    }

    /// <summary>Ersatzweg fuer Phase 1/chartdiag. Liest und erkennt nur; klickt nichts.</summary>
    internal static (bool ok, string desc) ResolveChartFallback(Action<string> W, string? pngDir)
    {
        string target = TargetInstrument;
        var (big, rawLike, info) = ScanSurfaces(W);
        W($"  --- Ersatzweg: Rohansicht + Panel-Name + Texterkennung (Ziel '{target}')");
        W($"  UIA: {info}");
        foreach (var c in rawLike.Take(10)) W("    nur Rohansicht: " + Describe(c));
        if (rawLike.Count > 0)
        {
            var rv = SelectChart(rawLike);
            W($"  Rohansicht-Entscheidung: {rv.Kind} ({rv.Reason})");
            if (rv.Kind == ChartVerdictKind.Unique)
                return (true, $"{rv.Signal} (nur in der UIA-Rohansicht, IsControlElement=false) @{(int)rv.Chosen!.X},{(int)rv.Chosen.Y} {(int)rv.Chosen.W}x{(int)rv.Chosen.H}");
            if (rv.Kind == ChartVerdictKind.Multiple) return (false, "Rohansicht: " + rv.Reason);
        }

        var regions = SurfaceRegions(big);
        W($"  grosse sichtbare Flaechen (innerste; gleiche Rechtecke gruppiert, Verhaeltnis wird geprueft): {regions.Count}");
        foreach (var g in regions)
            W($"    Flaeche @{(int)g[0].R.X},{(int)g[0].R.Y} {(int)g[0].R.Width}x{(int)g[0].R.Height}: " +
              string.Join(" | ", g.Select(s => $"name='{s.Name}' klasse='{s.Cls}' aid='{s.Aid}' typ={s.Type} fw={s.Fw} hwnd=0x{s.Hwnd:X}{(s.RawOnly ? " [nur Rohansicht]" : "")}")));

        var logCharts = new List<string>();
        try
        {
            var start = Process.GetProcessesByName("OFT.Platform").Select(p => { try { return p.StartTime; } catch { return DateTime.MinValue; } }).DefaultIfEmpty(DateTime.MinValue).Min();
            logCharts = ChartSourcesFromLog(TailLines(AtasLogPath(), 8_000_000), start);
            W($"  ATAS-Protokoll seit ATAS-Start {start:HH:mm:ss}: Charts {(logCharts.Count > 0 ? string.Join(", ", logCharts) : "(keine genannt)")}");
        }
        catch (Exception ex) { W($"  ATAS-Protokoll nicht lesbar: {ex.Message}"); }

        var rel = RegionRelation.Unknown; string? named = null; var hits = new List<OcrHit>();
        if (regions.Count == 1)
        {
            var reg = regions[0];
            var (r0, inner, relInfo) = Relation(reg); rel = r0;
            W($"  Verhaeltnis der Knoten: {rel} ({relInfo}); Zeichenflaeche = hwnd 0x{inner.Hwnd:X} klasse='{inner.Cls}'");

            // a) UIA-Name eines chartgrossen Vorfahren (z. B. CustomLayoutPanel 'MNQ 5m Chart' am 17.11.)
            var anc = inner.El != null ? Bounded("UIA-Vorfahren der Flaeche", () => Ancestors(inner.El), 8000, new List<AutomationElement>(), W) : new List<AutomationElement>();
            foreach (var a in anc.Take(12))
            {
                string an = Safe(() => a.Current.Name), ac = Safe(() => a.Current.ClassName);
                var ar = SafeRect(a);
                bool panel = IsPanelOf(ar, inner.R);
                W($"    Vorfahr: name='{Trunc(an, 50)}' klasse='{ac}' {(int)ar.Width}x{(int)ar.Height}{(panel ? " [chartgross]" : "")}");
                if (named == null && panel && NameHasTarget(an, target)) named = an;
            }

            // b) Texterkennung: Flaeche + Titelzeile darueber
            var r = inner.R; double bandTop = Math.Max(0, r.Y - TitleBandPx);
            var ocrRect = new System.Windows.Rect(r.X, bandTop, r.Width, r.Bottom - bandTop);
            string? png = pngDir != null ? Path.Combine(pngDir, $"chart-flaeche-{DateTime.Now:HHmmss}.png") : null;
            List<OcrWord> words;
            try { words = OcrRegion(ocrRect, png); }
            catch (Exception ex) { W($"  Texterkennung fehlgeschlagen: {ex.GetType().Name}: {ex.Message}"); words = new(); }
            if (png != null) W($"  Bild (Titelzeile + Flaeche): {png}");
            W($"  Texterkennung: {words.Count} Woerter; Titelzeile: {string.Join(" ", words.Where(o => o.CY < r.Y).Take(20).Select(o => o.Text))}" +
              $" | Flaeche oben: {string.Join(" ", words.Where(o => o.CY >= r.Y && o.Y < r.Y + Math.Min(140, r.Height / 4)).Take(20).Select(o => o.Text))}");
            foreach (var o in words.Where(o => IsTargetToken(o.Text, target)))
            {
                bool inSurface = o.CY >= r.Y;
                var (belongs, hi) = inSurface ? HitInSurface(o.CX, o.CY, reg)
                                              : inner.El != null ? HitInTitleBand(o.CX, o.CY, inner, anc) : (false, "UIA-Element der Flaeche fehlt");
                W($"    Titel-Treffer '{o.Text}' @{o.CX},{o.CY} ({(inSurface ? "in der Flaeche" : "Titelzeile")}): {hi}");
                // Titelzeile ohne belegbares gemeinsames Panel: KEIN Beleg, aber auch kein Gegenbeweis (z. B. fehlen die
                // WPF-Ebenen im UIA-Baum). In der Flaeche dagegen zeigt ein fremdes Fenster einen Widerspruch.
                if (!inSurface && !belongs) { W("      -> nicht zuordenbar, zaehlt nicht (weder Beleg noch Widerspruch)"); continue; }
                hits.Add(new OcrHit(o.Text, o.CX, o.CY, belongs, hi));
            }
        }
        var v = DecideChartFallback(regions.Count, rel, named, hits, logCharts, target);
        W($"  Ersatzweg-Ergebnis: {(v.Unique ? "EINDEUTIG" : "NICHT eindeutig")} - {v.Reason}");
        return (v.Unique, v.Unique ? $"{v.Signal}: {v.Reason}" : v.Reason);
    }
}
