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
}
