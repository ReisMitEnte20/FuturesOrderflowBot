using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;

namespace AtasReplayProbe;

// Diagnose Phase 6 (Befund 18.11.2026, lokal): Zieltag 05.01.2026 15:30–18:00, Dialog stand vorher auf November.
// StartEdit stimmte, EndEdit lieferte dreimal '05.11.2026 00:00:00'. Dieser Befehl protokolliert am ECHTEN Dialog beide
// Felder vor der Eingabe, nach jedem einzelnen Tastendruck und nach dem Verlassen des Felds. Er laedt NICHTS
// (kein 'Auswählen'), schliesst den Dialog am Ende per 'Abbrechen' und startet keine Aufnahme.
internal static partial class Program
{
    //   datediag [--legacy] "dd.MM.yyyy HH:mm" "dd.MM.yyyy HH:mm" ["dd.MM.yyyy HH:mm" "dd.MM.yyyy HH:mm" ...] [out.txt]
    //   Paare = (Start, Ende) nacheinander im SELBEN Dialog. --legacy = Eingabe exakt wie 1.3.8 (Ende, dann Start).
    private static int DateDiag(string[] args)
    {
        var sb = new StringBuilder();
        void W(string s = "") { sb.AppendLine(s); Console.WriteLine(s); }
        string? outFile = args.FirstOrDefault(a => a.EndsWith(".txt"));
        bool legacy = args.Contains("--legacy");
        var dts = args.Skip(1).Where(a => Regex.IsMatch(a, @"^\d{2}\.\d{2}\.\d{4} \d{2}:\d{2}")).ToList();
        if (dts.Count < 2 || dts.Count % 2 != 0) { W("datediag [--legacy] \"dd.MM.yyyy HH:mm\" \"dd.MM.yyyy HH:mm\" [...] [out.txt]"); Flush(sb, outFile); return 2; }
        W($"# datediag {DateTime.Now:yyyy-MM-dd HH:mm:ss}  Modus={(legacy ? "1.3.8 (Ende, dann Start)" : "1.3.9 (Start:=Tag 00:00, Ende, Start)")}  Ziele={dts.Count / 2}");
        W($"[vorab] aktive Recorder-Instanzen: {LockedCount()}  Replay-Fenster: {(ReplayWindow() != null ? "offen" : "zu")}");
        // Wie Phase 4: ohne 'Replay'-Fenster das Replay-Ribbon toggeln (begrenzt, protokolliert). Laedt nichts.
        for (int attempt = 0; attempt < 3 && ReplayWindow() == null && FindIdAny("StartEdit") == null; attempt++)
        {
            var rb = FindIdAny("RibbonReplayButton");
            if (rb == null) break;
            W($"[vorab] kein 'Replay'-Fenster -> Replay-Ribbon toggeln (Versuch {attempt + 1})"); Invoke(rb);
            for (int i = 0; i < 10 && ReplayWindow() == null; i++) Thread.Sleep(1000);
        }
        if (ReplayWindow() == null && FindIdAny("StartEdit") == null) { W("BLOCKER: Replay-Fenster nicht offen."); Flush(sb, outFile); return 4; }
        Thread.Sleep(2000);

        if (FindIdAny("StartEdit") == null)
        {
            var datumBtn = FindBtnAny("Datum auswählen");
            if (datumBtn != null) { W("[oeffnen] 'Datum auswählen' per UIA"); Invoke(datumBtn); }
            else
            {
                // Wie Phase 6 im visuellen Modus: Transport custom-gerendert -> 'Datum' frisch per OCR aufloesen und klicken.
                var vtf = OcrDetectFresh(out var vd);
                if (vtf == null || vtf.StartMode != "Datum") { W("BLOCKER: 'Datum auswählen' weder per UIA noch per OCR auffindbar: " + vd); Flush(sb, outFile); return 4; }
                W($"[oeffnen] (visuell) Klick 'Datum auswählen' @{vtf.StartClick.x},{vtf.StartClick.y}");
                ClickFloat(vtf.StartClick.x, vtf.StartClick.y);
            }
            for (int i = 0; i < 12 && FindIdAny("StartEdit") == null; i++) Thread.Sleep(600);
            if (FindIdAny("StartEdit") == null) { W("BLOCKER: Datum-Dialog oeffnet nicht."); Flush(sb, outFile); return 5; }
        }
        string Both() => $"Start='{ValueOf(FindIdAny("StartEdit"))}' Ende='{ValueOf(FindIdAny("EndEdit"))}'";
        W($"[vor Eingabe] {Both()}");

        int rc = 0;
        try
        {
            for (int p = 0; p < dts.Count; p += 2)
            {
                string ws = dts[p], we = dts[p + 1];
                W($"\n=== Ziel {p / 2 + 1}: Start '{ws}'  Ende '{we}' ===");
                bool ok = legacy ? LegacyEntry(ws, we, W, trace: true) : SetDialogRange(ws, we, W, trace: true);
                W($"[Ergebnis Ziel {p / 2 + 1}] {Both()} -> {(ok ? "OK" : "FEHLER")}");
                if (!ok) rc = 6;
            }
        }
        finally
        {
            CloseDateDialogIfOpen(W);   // 'Abbrechen' -> nichts wird geladen
            W($"[nachher] aktive Recorder-Instanzen: {LockedCount()}");
            Flush(sb, outFile);
        }
        return rc;
    }

    /// <summary>Exakt die Eingabe aus 1.3.8 Phase 6 (Ende, dann Start), optional mit Protokoll je Tastendruck.</summary>
    private static bool LegacyEntry(string wantStart, string wantEnd, Action<string> W, bool trace)
    {
        Action<string>? Step(string id) => trace ? s => W($"   [{id}] {s,-22} -> Start='{ValueOf(FindIdAny("StartEdit"))}' Ende='{ValueOf(FindIdAny("EndEdit"))}'") : null;
        TypeIntoField(FindIdAny("EndEdit"), wantEnd, Step("EndEdit"));
        TypeIntoField(FindIdAny("StartEdit"), wantStart, Step("StartEdit"));
        return DialogMatches(wantStart, wantEnd);
    }

    /// <summary>Stimmen beide Felder (bis auf die Minute) mit dem Ziel ueberein?</summary>
    private static bool DialogMatches(string wantStart, string wantEnd) =>
        ValueOf(FindIdAny("StartEdit")).StartsWith(wantStart.Substring(0, 16), StringComparison.Ordinal)
        && ValueOf(FindIdAny("EndEdit")).StartsWith(wantEnd.Substring(0, 16), StringComparison.Ordinal);

    /// <summary>Tagesanfang des Ziel-Starts ('dd.MM.yyyy 00:00') – fruehester Punkt des Zieltags.</summary>
    internal static string DayAnchor(string wantStart) => wantStart.Substring(0, 10) + " 00:00";

    /// <summary>
    /// 1.3.9: Zeitraum im ATAS-Datumsdialog setzen (Befund am echten Dialog, 18.11.2026):
    ///  - ATAS uebernimmt jeden Maskenabschnitt (Tag, Monat, Jahr, Stunde, Minute) SOFORT und haelt Start &lt; Ende ein,
    ///    indem es das ANDERE Feld mitschiebt (z. B. Ende 05.11. 14:50 -> Start 15:30 wird 14:20).
    ///  - Ein per Strg+A/Entf geleertes Feld wird nach den ersten zwei Ziffern mit 'Tag.aktueller Monat.Jahr 00:00'
    ///    vorbelegt; dadurch schob der Start (05.11. 00:00) das bereits richtige Ende (05.01. 18:00) auf 05.11. 00:00.
    /// Daher: NICHT leeren, ab Pos1 ueberschreiben, Reihenfolge Start := Zieltag 00:00 -> Ende := Ziel -> Start := Ziel.
    /// Ein Mitschieben trifft so nur ein Feld, das danach noch geschrieben wird. Hat Runde 1 beide Felder auf den Zieltag
    /// gebracht, aendert Runde 2 nur noch Uhrzeiten innerhalb des Tages. Nach JEDEM Schreibvorgang werden beide Felder
    /// protokolliert; Erfolg nur, wenn beide nach dem Fokuswechsel exakt stimmen (Rueckleseprüfung bleibt).
    /// </summary>
    private static bool SetDialogRange(string wantStart, string wantEnd, Action<string> W, bool trace)
    {
        string Both() => $"Start='{ValueOf(FindIdAny("StartEdit"))}' Ende='{ValueOf(FindIdAny("EndEdit"))}'";
        Action<string>? Step(string id) => trace ? s => W($"   [{id}] {s,-22} -> {Both()}") : null;
        W($"[6] vor Eingabe: {Both()}");
        for (int round = 1; round <= 2; round++)
        {
            (string id, string v, string what)[] seq =
            {
                ("StartEdit", DayAnchor(wantStart), "Start := Zieltag 00:00"),
                ("EndEdit", wantEnd, "Ende := Ziel"),
                ("StartEdit", wantStart, "Start := Ziel"),
            };
            foreach (var (id, v, what) in seq)
            {
                if (!TypeIntoField(FindIdAny(id), v, Step(id), clear: false)) W($"[6] Runde {round}: {what} -> Feld nicht bedienbar");
                W($"[6] Runde {round}: {what,-24} '{v}' -> {Both()}");
            }
            Thread.Sleep(250);
            bool ok = DialogMatches(wantStart, wantEnd);
            W($"[6] Runde {round} nach Fokuswechsel: {Both()} -> {(ok ? "stimmt" : "weicht ab")}");
            if (ok) return true;
        }
        return false;
    }
}
