using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Automation;

namespace AtasReplayProbe;

// 1.4.0: Papierkorb der Recorder-Zeile gezielt bestimmen.
// Befund 1.3.9 (Lauf 19.11.2026 00:11, echter Dialog): In der Recorder-Zeile liegen rechts vier Aktionssymbole. 1.3.9 fuhr die
// Kandidaten von rechts nach links mit der Maus an; das rechte Symbol (Favoritenstern, kein Tooltip) kam zuerst, der Papierkorb
// ('Löschen') erst als zweites. Jetzt: Symbole nach Form bewerten (Papierkorb = Deckel-Linie oben + senkrechter Korpus;
// Stern = Spitze oben, breiteste Stelle in der Mitte), den besten Kandidaten ZUERST anfahren und per Tooltip bestaetigen.
// Keine festen Koordinaten: alles relativ zur frisch gemessenen Zeile. Ohne Tooltip-Bestaetigung kein Klick.
internal static partial class Program
{
    /// <summary>Formmerkmale eines Symbols (Vordergrund = deutlich vom Zeilenhintergrund verschiedene Pixel).</summary>
    internal sealed record IconShape(int W, int H, double TopRowFill, double MaxRowFillPos, double Verticality, double Symmetry, double Fill, double TrashScore);

    /// <summary>Formmerkmale im Bildausschnitt um (cx, cy) des Zeilenbilds. bg = Zeilenhintergrund.</summary>
    internal static IconShape? MeasureIcon(Bitmap row, int cx, int cy, int halfW = 10, int halfH = 10)
    {
        int w = row.Width, h = row.Height;
        var counts = new Dictionary<int, int>();
        for (int y = 0; y < h; y += 2) for (int x = 0; x < w; x += 2) { int c = row.GetPixel(x, y).ToArgb(); counts[c] = counts.TryGetValue(c, out var n) ? n + 1 : 1; }
        var bg = Color.FromArgb(counts.OrderByDescending(k => k.Value).First().Key);
        bool Fg(int x, int y) { var c = row.GetPixel(x, y); return Math.Abs(c.R - bg.R) + Math.Abs(c.G - bg.G) + Math.Abs(c.B - bg.B) > 60; }
        int x0 = Math.Max(0, cx - halfW), x1 = Math.Min(w - 1, cx + halfW), y0 = Math.Max(0, cy - halfH), y1 = Math.Min(h - 1, cy + halfH);
        // Bounding-Box des Vordergrunds im Fenster
        int bx0 = int.MaxValue, bx1 = -1, by0 = int.MaxValue, by1 = -1;
        for (int y = y0; y <= y1; y++) for (int x = x0; x <= x1; x++) if (Fg(x, y)) { bx0 = Math.Min(bx0, x); bx1 = Math.Max(bx1, x); by0 = Math.Min(by0, y); by1 = Math.Max(by1, y); }
        if (bx1 < 0) return null;
        int bw = bx1 - bx0 + 1, bh = by1 - by0 + 1;
        if (bw < 4 || bh < 4) return null;
        var rowFill = new double[bh]; int fgTotal = 0;
        for (int y = 0; y < bh; y++) { int n = 0; for (int x = 0; x < bw; x++) if (Fg(bx0 + x, by0 + y)) n++; rowFill[y] = (double)n / bw; fgTotal += n; }
        var colFill = new double[bw];
        for (int x = 0; x < bw; x++) { int n = 0; for (int y = 0; y < bh; y++) if (Fg(bx0 + x, by0 + y)) n++; colFill[x] = (double)n / bh; }
        // Deckel: maximale Zeilenfuellung im oberen Viertel (Papierkorb ~ volle Breite; Stern: nur Spitze).
        int topBand = Math.Max(1, bh / 4);
        double topRow = rowFill.Take(topBand).Max();
        // Position der breitesten Zeile (0 = oben, 1 = unten).
        int argMax = Array.IndexOf(rowFill, rowFill.Max());
        double maxPos = bh > 1 ? (double)argMax / (bh - 1) : 0.5;
        // Senkrechte: Anteil Spalten mit Fuellung >= 60 % (Korpus-Kanten/Rillen des Papierkorbs).
        double vert = colFill.Count(f => f >= 0.6) / (double)bw;
        // Links-rechts-Symmetrie
        int same = 0, tot = 0;
        for (int y = 0; y < bh; y++) for (int x = 0; x < bw / 2; x++) { tot++; if (Fg(bx0 + x, by0 + y) == Fg(bx1 - x, by0 + y)) same++; }
        double sym = tot > 0 ? (double)same / tot : 0;
        double fill = (double)fgTotal / (bw * bh);
        // Papierkorb-Wert: Deckel oben breit, breiteste Zeile im oberen Drittel, senkrechte Kanten, hochformatig/quadratisch.
        double score = 0;
        score += topRow >= 0.7 ? 2 : topRow >= 0.5 ? 1 : 0;
        score += maxPos <= 0.34 ? 1 : 0;
        score += vert >= 0.25 ? 1 : 0;
        score += bh >= bw * 0.9 ? 0.5 : 0;
        score += sym >= 0.85 ? 1 : 0;   // Papierkorb spiegelsymmetrisch; 'Duplizieren' (versetzte Quadrate) nicht (echt: 1,0 vs 0,58)
        return new IconShape(bw, bh, Math.Round(topRow, 2), Math.Round(maxPos, 2), Math.Round(vert, 2), Math.Round(sym, 2), Math.Round(fill, 2), score);
    }

    /// <summary>Reihenfolge, in der Symbol-Kandidaten per Tooltip geprueft werden: hoechster Papierkorb-Wert zuerst,
    /// bei Gleichstand von rechts nach links (bisheriges Verhalten).</summary>
    internal static List<(int cx, int cy, int w, IconShape? shape)> RankTrashCandidates(Bitmap row, IReadOnlyList<(int cx, int cy, int w)> blobs, double minXFrac = 0.6)
    {
        return blobs.Where(b => b.cx >= row.Width * minXFrac)
                    .Select(b => (b.cx, b.cy, b.w, shape: MeasureIcon(row, b.cx, b.cy)))
                    .OrderByDescending(b => b.shape?.TrashScore ?? -1).ThenByDescending(b => b.cx).ToList();
    }

    //   indtool list <suche> | add "<exakter Katalogname>" | remove "<Zeilentext>"  [out.txt]
    //   Testhilfe fuer die 1.4.0-Abnahme: einen ZUSAETZLICHEN Indikator an den Chart haengen bzw. wieder entfernen, damit
    //   geprueft werden kann, dass das Recorder-Entfernen andere Indikatoren unveraendert laesst. Jeder Schritt geprueft.
    private static int IndTool(string[] args)
    {
        var sb = new StringBuilder();
        string? outFile = args.FirstOrDefault(a => a.EndsWith(".txt"));
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }
        var rest = args.Skip(1).Where(a => !a.EndsWith(".txt")).ToList();
        if (rest.Count < 2) { W("indtool list <suche> | add \"<Katalogname>\" | remove \"<Zeilentext>\" [out.txt]"); Flush(sb, outFile); return 2; }
        string mode = rest[0], name = rest[1];
        W($"# indtool {mode} '{name}'  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        if (!OpenIndicatorDialog(W) || IndicatorDialog() is not { } dlg) { W("BLOCKER: Indikator-Dialog nicht zu oeffnen."); Flush(sb, outFile); return 4; }
        ForceForeground(dlg); Thread.Sleep(400);
        bool applied = false;
        try
        {
            var (rows0, h0, s0, src0) = ReadAddedRows(dlg, W, ocr: true);
            W($"[ind] vorher: Kopfzahl={h0} ({src0}) {s0}: {RowsLine(rows0)}");
            if (mode == "list")
            {
                SetSearchText(dlg, name, W); Thread.Sleep(1200);
                W("[ind] Katalogtreffer: " + string.Join(" | ", GridValues(IndicatorDialog() ?? dlg).Distinct().Take(40)));
                return 0;
            }
            if (mode == "add")
            {
                SetSearchText(dlg, name, W);
                AutomationElement? hit = null;
                for (int i = 0; i < 12 && hit == null; i++) { Thread.Sleep(400); hit = FindExactCatalogHit(dlg, name); }
                if (hit == null) { W($"BLOCKER: exakter Katalogtreffer '{name}' fehlt."); return 5; }
                try { if (hit.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp)) ((SelectionItemPattern)sp).Select(); } catch { }
                ForceForeground(dlg); Thread.Sleep(200);
                var hr = hit.Current.BoundingRectangle;
                DoubleClickFast((int)(hr.X + hr.Width / 2), (int)(hr.Y + hr.Height / 2));
                int h1 = h0; for (int i = 0; i < 30 && h1 <= h0; i++) { Thread.Sleep(200); h1 = HeaderCount(IndicatorDialog() ?? dlg); }
                if (h1 != h0 + 1) { W($"BLOCKER: Kopfzahl {h0} -> {h1} (erwartet +1)."); return 6; }
                var apply = FindBtnIn(IndicatorDialog() ?? dlg, "Übernehmen");
                if (apply == null || !Invoke(apply)) { W("BLOCKER: 'Übernehmen' nicht ausloesbar."); return 7; }
                applied = true; W($"[ind] '{name}' hinzugefuegt (Kopfzahl {h0} -> {h1}) und uebernommen.");
                return 0;
            }
            if (mode == "remove")
            {
                var hits = rows0.Where(r => r.Identity.Contains(name, StringComparison.OrdinalIgnoreCase) && !r.IsRecorder).ToList();
                if (hits.Count != 1) { W($"BLOCKER: {hits.Count} Zeilen mit '{name}' (erwartet genau 1)."); return 5; }
                var row = hits[0]; var rr = row.Rect;
                if (row.Element != null) { try { var er = row.Element.Current.BoundingRectangle; if (!er.IsEmpty && er.Width > 10) rr = er; } catch { } }
                SetCursorPos((int)(rr.X + Math.Min(60, rr.Width / 5)), (int)(rr.Y + rr.Height / 2)); Thread.Sleep(700);
                using var bmp = CaptureRect(rr);
                List<(int x0, int x1)> spans; try { spans = ScreenOcr.Recognize(bmp, 0, 0).Where(o => o.Text.Trim().Length >= 2).Select(o => (o.X, o.X + o.W)).ToList(); } catch { spans = new(); }
                (int x, int y)? target = null;
                foreach (var b in RankTrashCandidates(bmp, FindIconBlobs(bmp, spans)))
                {
                    int sx = (int)Math.Round(rr.X) + b.cx, sy = (int)Math.Round(rr.Y) + b.cy;
                    SetCursorPos(sx, sy); Thread.Sleep(1300);
                    var tip = CurrentToolTip(); W($"   Symbol x+{b.cx} Formwert={b.shape?.TrashScore} Tooltip='{tip ?? "(keiner)"}'");
                    if (tip != null && DeleteWords.IsMatch(tip)) { target = (sx, sy); break; }
                }
                if (target == null) { W("BLOCKER: Papierkorb nicht bestaetigt -> kein Klick."); return 6; }
                ForceForeground(dlg); Thread.Sleep(150); ClickScreen(target.Value.x, target.Value.y); Thread.Sleep(1200);
                var (rows1, h1, s1, src1) = ReadAddedRows(IndicatorDialog() ?? dlg, W, ocr: true);
                W($"[ind] nachher: Kopfzahl={h1} ({src1}) {s1}: {RowsLine(rows1)}");
                var expect = rows0.Where(r => r != row).Select(r => Norm(r.Identity)).ToList();
                if (h1 != h0 - 1 || !expect.SequenceEqual(rows1.Select(r => Norm(r.Identity)))) { W("BLOCKER: nicht genau diese Zeile entfernt -> Abbrechen."); return 7; }
                var apply = FindBtnIn(IndicatorDialog() ?? dlg, "Übernehmen");
                if (apply == null || !Invoke(apply)) { W("BLOCKER: 'Übernehmen' nicht ausloesbar."); return 8; }
                applied = true; W($"[ind] '{name}' entfernt und uebernommen.");
                return 0;
            }
            W("unbekannter Modus"); return 2;
        }
        finally
        {
            if (!applied && IndicatorDialog() is { } d) CancelDialog(d, W);
            Flush(sb, outFile);
        }
    }

    //   trashdiag [out.txt]   — echter Indikator-Dialog: Recorder-Zeile (ggf. NUR im Dialog hinzufuegen), Symbole messen,
    //   nur ANFAHREN (kein Klick auf Symbole), Tooltips lesen, Bilder speichern, dann 'Abbrechen' (nichts uebernommen).
    private static int TrashDiag(string? outFile)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }
        W($"# trashdiag {DateTime.Now:yyyy-MM-dd HH:mm:ss}  aktive Recorder vorher: {LockedCount()}");
        if (System.Diagnostics.Process.GetProcessesByName("OFT.Platform").Length == 0) { W("BLOCKER: ATAS laeuft nicht."); Flush(sb, outFile); return 3; }
        if (!OpenIndicatorDialog(W)) { W("BLOCKER: Indikator-Dialog nicht zu oeffnen."); Flush(sb, outFile); return 4; }
        var dlg = IndicatorDialog();
        if (dlg == null) { W("BLOCKER: Dialog nicht gefunden."); Flush(sb, outFile); return 4; }
        ForceForeground(dlg); Thread.Sleep(400);
        try
        {
            var st0 = AddedState(dlg);
            if (!st0.hasRecorder)
            {
                W("[diag] keine Recorder-Zeile -> NUR im Dialog hinzufuegen (wird per 'Abbrechen' verworfen).");
                SetSearchText(dlg, "replay", W);
                AutomationElement? hit = null;
                for (int i = 0; i < 12 && hit == null; i++) { Thread.Sleep(400); hit = FindExactCatalogHit(dlg, REC_CATALOG_NAME); }
                if (hit == null) { W("BLOCKER: Katalogtreffer fehlt."); return 5; }
                try { if (hit.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp)) ((SelectionItemPattern)sp).Select(); } catch { }
                ForceForeground(dlg); Thread.Sleep(200);
                var hr = hit.Current.BoundingRectangle;
                W($"[diag] Doppelklick auf Katalogtreffer rect={Rect(hit)}");
                DoubleClickFast((int)(hr.X + hr.Width / 2), (int)(hr.Y + hr.Height / 2));
                for (int i = 0; i < 30 && !AddedState(IndicatorDialog() ?? dlg).hasRecorder; i++) Thread.Sleep(200);
                dlg = IndicatorDialog() ?? dlg;
                W($"[diag] nach Doppelklick: {AddedState(dlg)}");
                Thread.Sleep(600);
            }
            var (rows, header, state, src) = ReadAddedRows(dlg, W, ocr: true);
            W($"[diag] Hinzugefuegt: Kopfzahl={header} gelesen={rows.Count} ({src}) {state}: {RowsLine(rows)}");
            var rec = rows.FirstOrDefault(r => r.IsRecorder);
            if (rec == null) { W("BLOCKER: keine Recorder-Zeile."); return 6; }
            var rr = rec.Rect; if (rec.Element != null) { try { var er = rec.Element.Current.BoundingRectangle; if (!er.IsEmpty && er.Width > 10) rr = er; } catch { } }
            var logs = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(RecordingsDir)) ?? ".", "Logs");
            Directory.CreateDirectory(logs);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            using (var b0 = CaptureRect(rr)) b0.Save(Path.Combine(logs, $"trashdiag_{stamp}_ohnehover.png"));
            int hx = (int)(rr.X + Math.Min(60, rr.Width / 5)), hy = (int)(rr.Y + rr.Height / 2);
            SetCursorPos(hx, hy); Thread.Sleep(700);
            using var bmp = CaptureRect(rr);
            bmp.Save(Path.Combine(logs, $"trashdiag_{stamp}_hover.png"));
            List<(int x0, int x1)> spans;
            try { spans = ScreenOcr.Recognize(bmp, 0, 0).Where(o => o.Text.Trim().Length >= 2).Select(o => (o.X, o.X + o.W)).ToList(); } catch { spans = new(); }
            var blobs = FindIconBlobs(bmp, spans);
            W($"[diag] Zeile rect={(int)rr.X},{(int)rr.Y},{(int)rr.Width}x{(int)rr.Height}  Blobs: {string.Join(", ", blobs.Select(b => $"x+{b.cx}/y+{b.cy} b={b.w}"))}");
            var ranked = RankTrashCandidates(bmp, blobs);
            foreach (var c in ranked) W($"   Kandidat x+{c.cx}/y+{c.cy} b={c.w}  {c.shape}");
            // Nur anfahren (kein Klick) und Tooltip lesen, von rechts nach links.
            foreach (var b in blobs.Where(b => b.cx >= bmp.Width * 0.6).OrderByDescending(b => b.cx))
            {
                int sx = (int)Math.Round(rr.X) + b.cx, sy = (int)Math.Round(rr.Y) + b.cy;
                SetCursorPos(sx, sy); Thread.Sleep(1300);
                W($"   anfahren x+{b.cx} @({sx},{sy}) Tooltip='{CurrentToolTip() ?? "(keiner)"}'");
                using var crop = CaptureRect(new System.Windows.Rect(sx - 12, sy - 12, 24, 24));
                crop.Save(Path.Combine(logs, $"trashdiag_{stamp}_x{b.cx}.png"));
            }
            SetCursorPos(hx, hy); Thread.Sleep(300);
            return 0;
        }
        finally
        {
            var d = IndicatorDialog(); if (d != null) CancelDialog(d, W);
            W($"[diag] aktive Recorder nachher: {LockedCount()}");
            Flush(sb, outFile);
        }
    }
}
