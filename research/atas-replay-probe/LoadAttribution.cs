using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AtasReplayProbe;

// 1.3.5: Instanz-Zuordnung nach dem Laden (VM-Befund 17.11.2026: ATAS meldete 'Initialized', aber es entstand keine neue
// Recorder-Instanz; die Probe wartete stumm bis zum Ladelimit und kam nie zu Play).
internal static partial class Program
{
    /// <summary>Summe der Datenstroeme (*.ndjson) einer Recorder-Instanz. 0 = noch keine Daten geschrieben.</summary>
    internal static long DataBytes(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.ndjson").Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } }) : 0; }
        catch { return 0; }
    }

    /// <summary>Alle Recorder-Ordner, die seit Beginn dieses Auftrags entstanden sind, mit Zustand (nur lesen).</summary>
    private static void ListJobFolders(Action<string> W, HashSet<string> preJob)
    {
        var nf = FolderSet().Except(preJob).OrderBy(f => f, StringComparer.Ordinal).ToList();
        W($"   Recorder-Ordner seit Auftragsbeginn: {nf.Count}  (aktive Instanzen gesamt: {LockedCount()})");
        foreach (var f in nf)
        {
            var d = Path.Combine(RecordingsDir, f); var m = Path.Combine(d, "meta.json");
            W($"      {f}  aktiv={IsMetaLocked(m)}  start='{ReadMetaField(m, "startWallUtc")}'  instrument='{ReadMetaField(m, "instrument")}'  daten={DataBytes(d)} B");
        }
    }

    /// <summary>Reine Bewertung (getestet): Trades, die eine in Schritt 3 (Live-Modus) angelegte Instanz VOR dem Laden
    /// geschrieben hat. Erlaubt: keine Trades, oder nur Trades mindestens 1 h NACH dem Fensterende (Live-Vorlauf; die
    /// Bridge schliesst sie als solchen aus). Jeder Trade im/vor dem Replay-Bereich -> nicht verwendbar.</summary>
    internal static (bool ok, string why) ClassifyPreload(IReadOnlyList<DateTime> tradeTimesUtc, DateTime windowEndUtc, bool truncated)
    {
        if (truncated) return (false, "Datenstrom zu gross fuer die Pruefung vor dem Laden -> nicht verwendbar");
        if (tradeTimesUtc.Count == 0) return (true, "noch keine Trades");
        var bad = tradeTimesUtc.Count(t => t < windowEndUtc.AddHours(1));
        if (bad > 0) return (false, $"{bad} von {tradeTimesUtc.Count} Trades liegen im/vor dem Replay-Bereich (Marktzeit < Fensterende+1h) -> wuerde Daten mischen");
        return (true, $"{tradeTimesUtc.Count} Live-Trades vor dem Laden (Marktzeit {tradeTimesUtc.Min():yyyy-MM-dd HH:mm}Z..{tradeTimesUtc.Max():HH:mm}Z, weit nach dem Fenster) - Bridge schliesst sie als Live-Vorlauf aus");
    }

    private static readonly System.Text.RegularExpressions.Regex RxTradeTm = new(@"""dataType""\s*:\s*""Trade"".*?""tMarket""\s*:\s*""([^""]+)""|""tMarket""\s*:\s*""([^""]+)"".*?""dataType""\s*:\s*""Trade""",System.Text.RegularExpressions.RegexOptions.Compiled);

    internal static (bool ok, string why) PreloadTradesCheck(string dir, DateTime windowEndUtc)
    {
        var p = Path.Combine(dir, "ticks.ndjson");
        var times = new List<DateTime>(); bool truncated = false;
        try
        {
            if (File.Exists(p))
            {
                if (new FileInfo(p).Length > 50_000_000) truncated = true;
                else
                {
                    using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var sr = new StreamReader(fs);
                    string? line;
                    while ((line = sr.ReadLine()) != null)
                    {
                        var m = RxTradeTm.Match(line);
                        if (!m.Success) continue;
                        var v = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                        if (DateTime.TryParse(v, System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var t)) times.Add(t);
                    }
                }
            }
        }
        catch (Exception ex) { return (false, $"ticks.ndjson nicht lesbar ({ex.Message})"); }
        return ClassifyPreload(times, windowEndUtc, truncated);
    }
}
