using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace AtasReplayProbe;

// Ein OCR-erkanntes Wort mit Koordinaten im BILDSCHIRM-Pixelraum (= Capture-Origin bereits eingerechnet).
internal sealed class OcrWord
{
    public string Text = "";
    public int X, Y, W, H;
    public int CX => X + W / 2;
    public int CY => Y + H / 2;
    public override string ToString() => $"'{Text}' @{X},{Y} {W}x{H}";
}

// Visueller Fallback: findet den Replay-Transport per aktuellem Screenshot + Windows-OCR, wenn UIA keine Controls liefert.
// Liefert anklickbare BILDSCHIRM-Punkte fuer die Transportaktionen RELATIV zu erkannten Beschriftungen (keine festen
// Bildschirmkoordinaten). Ausfuehrbarer Code (laeuft ohne Claude). DPI: Prozess ist DPI-aware (siehe Main), Capture
// und SetCursorPos arbeiten im selben physischen Pixelraum; das Capture-Origin wird auf die Wortkoordinaten addiert.
internal sealed class VisualTransport
{
    public int BarY;                 // y-Mitte der Transportleiste (Bildschirm)
    public OcrWord? ModulWord;       // 'Modul' (auswählen)
    public OcrWord? StartWord;       // 'Datum'/'Bar'/'Zeitraum' (auswählen)
    public string StartMode = "?";   // Datum | Bar | Zeitraum
    public OcrWord? SpeedWord;       // z. B. '100x'
    public (int x, int y) StartClick;      // 'Datum/Bar/Zeitraum auswählen'
    public (int x, int y) SpeedClick;      // Geschwindigkeits-Anzeige
    public (int x, int y)? PlayClick;      // ▶ (aus Layout abgeleitet)
    public (int x, int y)? ToRealtimeClick;// 'Zur Echtzeit wechseln' (aus Layout abgeleitet)
    public string Diag = "";
    public System.Windows.Rect BarBounds;
}

internal static class ScreenOcr
{
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int n);
    private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;

    // Primaerbildschirm (0,0 .. CX,CY) aufnehmen. Fuer dieses Setup (ein 1920x1080-Monitor) deckt das die Leiste ab.
    public static System.Drawing.Bitmap CapturePrimary(out int originX, out int originY)
    {
        originX = 0; originY = 0;
        int w = GetSystemMetrics(SM_CXSCREEN), h = GetSystemMetrics(SM_CYSCREEN);
        if (w <= 0) w = 1920; if (h <= 0) h = 1080;
        var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = System.Drawing.Graphics.FromImage(bmp)) g.CopyFromScreen(0, 0, 0, 0, new System.Drawing.Size(w, h));
        return bmp;
    }

    public static List<OcrWord> Recognize(System.Drawing.Bitmap bmp, int originX, int originY)
        => RecognizeAsync(bmp, originX, originY).GetAwaiter().GetResult();

    private static async Task<List<OcrWord>> RecognizeAsync(System.Drawing.Bitmap bmp, int originX, int originY)
    {
        var outp = new List<OcrWord>();
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
        ms.Position = 0;
        var decoder = await BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
        using var sb = await decoder.GetSoftwareBitmapAsync();
        OcrEngine? eng = OcrEngine.TryCreateFromUserProfileLanguages();
        if (eng == null) { try { eng = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("de-DE")); } catch { } }
        if (eng == null) { try { eng = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US")); } catch { } }
        if (eng == null) throw new InvalidOperationException("Windows-OCR: keine Spracherkennung verfuegbar.");
        var r = await eng.RecognizeAsync(sb);
        foreach (var ln in r.Lines)
            foreach (var w in ln.Words)
                outp.Add(new OcrWord { Text = w.Text, X = originX + (int)Math.Round(w.BoundingRect.X), Y = originY + (int)Math.Round(w.BoundingRect.Y), W = (int)Math.Round(w.BoundingRect.Width), H = (int)Math.Round(w.BoundingRect.Height) });
        return outp;
    }

    // Erkennt die Transportleiste aus OCR-Woertern. 'auswählen'-Woerter + linkes Modus-/Modul-Wort auf gleicher Zeile.
    // Ableitung der Symbol-Buttons (Play, Zur Echtzeit) als PIXEL-Offsets relativ zur Geschwindigkeitsanzeige
    // (Leiste hat feste Groesse, nur die Position variiert -> Offsets sind positionsunabhaengig stabil).
    public static VisualTransport? DetectTransport(List<OcrWord> words, out string diag)
    {
        diag = "";
        bool IsAusw(string t) => t.IndexOf("ausw", StringComparison.OrdinalIgnoreCase) >= 0; // robust ggü. ä-OCR
        string ModeOf(string t)
        {
            if (t.IndexOf("Datum", StringComparison.OrdinalIgnoreCase) >= 0) return "Datum";
            if (t.IndexOf("Bar", StringComparison.OrdinalIgnoreCase) >= 0) return "Bar";
            if (t.IndexOf("Zeitraum", StringComparison.OrdinalIgnoreCase) >= 0) return "Zeitraum";
            if (t.IndexOf("Modul", StringComparison.OrdinalIgnoreCase) >= 0) return "Modul";
            return "";
        }
        var ausw = words.Where(w => IsAusw(w.Text)).ToList();
        var modeWords = words.Where(w => ModeOf(w.Text) is "Datum" or "Bar" or "Zeitraum").ToList();
        var modulWords = words.Where(w => ModeOf(w.Text) == "Modul").ToList();
        var rx = new System.Text.RegularExpressions.Regex(@"^\s*\d+(\.\d+)?x\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var speedWords = words.Where(w => rx.IsMatch(w.Text)).ToList();

        // Kandidaten-Zeilen: ein Modus-Wort, in dessen Zeile (±8px) auch 'ausw' + 'Modul' vorkommen -> Transportleiste.
        var cands = new List<VisualTransport>();
        foreach (var m in modeWords)
        {
            bool auswNear = ausw.Any(a => Math.Abs(a.CY - m.CY) <= 10 && a.X >= m.X - 4 && a.X <= m.X + 220);
            bool modulNear = modulWords.Any(u => Math.Abs(u.CY - m.CY) <= 10 && u.X < m.X);
            if (!auswNear && !modulNear) continue;
            var vt = new VisualTransport { StartWord = m, StartMode = ModeOf(m.Text), BarY = m.CY };
            vt.ModulWord = modulWords.Where(u => Math.Abs(u.CY - m.CY) <= 10 && u.X < m.X).OrderByDescending(u => u.X).FirstOrDefault();
            vt.SpeedWord = speedWords.Where(s => Math.Abs(s.CY - m.CY) <= 12 && s.X > m.X).OrderBy(s => s.X).FirstOrDefault();
            cands.Add(vt);
        }
        if (cands.Count == 0) { diag = "OCR: keine Transportleiste erkannt (kein 'Datum/Bar/Zeitraum auswählen' mit benachbartem 'Modul')."; return null; }
        if (cands.Count > 1) { diag = "OCR MEHRDEUTIG: " + cands.Count + " Leisten-Kandidaten @ y=" + string.Join(",", cands.Select(c => c.BarY)); return null; }

        var t = cands[0];
        // Startbutton: Mitte der Modus-Beschriftung ('Datum' usw.). Live verifiziert: Klick hierauf oeffnet den Datum-Dialog.
        t.StartClick = (t.StartWord!.CX, t.StartWord!.CY);
        // Speed ('100x') und die Symbol-Buttons (Play, X) liest Windows-OCR NICHT (zu kontrastarm/als Icon gerendert).
        // Die native Replay-Toolbar hat jedoch FESTE Breiten; bei erzwungenem 'Datum'-Modus ('Datum auswählen') sind die
        // Offsets von der LINKEN Kante des Wortes 'Datum' zu Speed/Play/X konstant (an diesem Bildschirm aus dem Balken
        // gemessen UND gegen funktionierende Klicks verifiziert: StartWort.X+301 == Play, Datum-Mitte == Datum-Dialog).
        // Das sind KEINE gespeicherten Absolutkoordinaten, sondern Offsets relativ zur bei jedem Lauf frisch OCR-erkannten
        // Beschriftung. Fuer 'Bar'/'Zeitraum' (andere Button-Breite) werden KEINE abgeleiteten Punkte gesetzt.
        int xD = t.StartWord!.X;
        if (t.SpeedWord != null) t.SpeedClick = (t.SpeedWord.CX, t.SpeedWord.CY);
        else t.SpeedClick = (xD + 173, t.BarY);
        if (t.StartMode == "Datum")
        {
            t.PlayClick = (xD + 301, t.BarY);        // Play ▶  (verifiziert)
            t.ToRealtimeClick = (xD + 521, t.BarY);  // X schliessen (Reserve; Exit erfolgt bevorzugt ueber Ribbon-Toggle)
        }
        t.BarBounds = new System.Windows.Rect(
            (t.ModulWord?.X ?? t.StartWord!.X) - 4, t.BarY - 14,
            ((t.SpeedWord?.X ?? t.StartWord!.X) + 300) - ((t.ModulWord?.X ?? t.StartWord!.X) - 4), 28);
        return t;
    }
}
