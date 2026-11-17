using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AtasReplayProbe;

// ATAS-Protokoll auswerten: Zeilen strukturiert lesen (Zeit, Thread, Stufe, Quelle, Text), inkrementell nachlesen und
// das Ende EINER Replay-Ausfuehrung klassifizieren. Jede Entscheidung traegt die ausloesende Originalzeile.
//
// Befund (1.3.2, VM-Protokoll): 'Errored' wurde gesetzt, waehrend die Marktzeit weiterlief. Die alte Regel wertete JEDE
// Zeile mit "Exception"/"faulted" aus dem GESAMTEN ATAS-Log als Replay-Fehler (z. B. Konto-, Websocket- oder
// Stacktrace-Zeilen anderer Module). Jetzt zaehlen nur Fehler der Replay-Quellen.
internal sealed record AtasLogLine(DateTime Ts, string Thread, string Level, string Source, string Message, string Raw)
{
    private static readonly Regex Rx = new(@"^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}),(?<ms>\d{1,3}) \[(?<th>[^\]]*)\] (?<lvl>[A-Z]+)\s+(?<src>[^:]{1,40}?)\s*:\s?(?<msg>.*)$", RegexOptions.Compiled);

    public static AtasLogLine? Parse(string raw)
    {
        var m = Rx.Match(raw ?? "");
        if (!m.Success) return null;
        if (!DateTime.TryParseExact(m.Groups["ts"].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts)) return null;
        ts = ts.AddMilliseconds(int.Parse(m.Groups["ms"].Value.PadRight(3, '0')));
        return new AtasLogLine(ts, m.Groups["th"].Value.Trim(), m.Groups["lvl"].Value, m.Groups["src"].Value.Trim(), m.Groups["msg"].Value.Trim(), raw.Trim());
    }

    public bool IsReplaySource =>
        Source is "Replay" or "Replay DS" or "Replay MD" or "Market Replay" or "Replay AUC" || Source.StartsWith("Replay", StringComparison.Ordinal);

    public override string ToString() => Raw.Length > 240 ? Raw[..240] + "…" : Raw;
}

/// <summary>Liest neue Zeilen des ATAS-Tagesprotokolls ab einer gemerkten Position (kein erneutes Lesen der ganzen Datei).</summary>
internal sealed class IncrementalAtasLog
{
    private readonly string _dir;
    private string? _file;
    private long _pos;
    private string _partial = "";

    public IncrementalAtasLog(string logDir) { _dir = logDir; }

    private static string FileFor(string dir, DateTime d) => Path.Combine(dir, $"app_{d:yyyyMMdd}.log");

    /// <summary>Position auf das aktuelle Dateiende setzen (alles Bisherige gehoert nicht zu diesem Auftrag).</summary>
    public void SeekToEnd()
    {
        _file = FileFor(_dir, DateTime.Now);
        _pos = File.Exists(_file) ? new FileInfo(_file).Length : 0;
        _partial = "";
    }

    public List<string> ReadNew()
    {
        var res = new List<string>();
        var today = FileFor(_dir, DateTime.Now);
        if (_file != null && !string.Equals(_file, today, StringComparison.OrdinalIgnoreCase))
        {
            ReadFrom(_file, res);                       // Rest der alten Datei (Mitternacht)
            _file = today; _pos = 0; _partial = "";
        }
        _file ??= today;
        ReadFrom(_file, res);
        return res;
    }

    private void ReadFrom(string path, List<string> res)
    {
        try
        {
            if (!File.Exists(path)) return;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < _pos) _pos = 0;            // Datei neu begonnen
            if (fs.Length == _pos) return;
            fs.Seek(_pos, SeekOrigin.Begin);
            var buf = new byte[fs.Length - _pos];
            int n = 0; while (n < buf.Length) { int r = fs.Read(buf, n, buf.Length - n); if (r <= 0) break; n += r; }
            _pos += n;
            var text = _partial + Encoding.UTF8.GetString(buf, 0, n);
            var parts = text.Split('\n');
            _partial = parts[^1];                         // unvollstaendige letzte Zeile aufheben
            for (int i = 0; i < parts.Length - 1; i++) res.Add(parts[i].TrimEnd('\r'));
        }
        catch { }
    }
}

internal enum ReplayEnd { NoStart, Running, NaturalEnd, Errored, StoppedByUs, StoppedOther, OnlineExit, OtherRangeRestart }

/// <summary>
/// Zustandsautomat EINER Replay-Ausfuehrung (ab der eigenen 'Playing from'-Zeile). Rein, offline testbar.
/// Natuerliches Ende = 'Playing historical data stopped' bzw. 'Started -> Stopped' UND 'Data processing finished' der
/// Replay-Quellen, NICHT von uns ausgeloest, gefolgt von Auto-Rewind ('start path = rewind') oder Neustart auf DENSELBEN
/// Bereich - ODER (nur unterstuetzend) Recorder-Marktzeit am Fensterende. Einmal erkannt bleibt es gesetzt (Latch);
/// ein anschliessender Auto-Rewind/Loop loescht es nicht.
/// </summary>
internal sealed class ReplayExecution
{
    private static readonly Regex RxPlaying = new(@"Playing from (?<s>\d{2}\.\d{2}\.\d{4} \d{2}:\d{2}:\d{2}) to (?<e>\d{2}\.\d{2}\.\d{4} \d{2}:\d{2}:\d{2})", RegexOptions.Compiled);
    private static readonly Regex RxWarm = new(@"warmed before (?<s>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})Z", RegexOptions.Compiled);

    public (DateTime startUtc, DateTime endUtc)? Range { get; private set; }
    public AtasLogLine? RangeLine { get; private set; }
    public DateTime? WarmStartUtc { get; private set; }
    public AtasLogLine? WarmLine { get; private set; }
    public bool Started => Range != null;
    public ReplayEnd State { get; private set; } = ReplayEnd.NoStart;
    public List<AtasLogLine> Evidence { get; } = new();
    public string Rule { get; private set; } = "";
    public List<AtasLogLine> Hints { get; } = new();
    public bool DataReachedEnd { get; set; }
    public bool LoopRestarted { get; private set; }

    private DateTime? _ourStopAt;
    private AtasLogLine? _stop, _finished;
    private bool _latched;

    /// <summary>Ab diesem Zeitpunkt (lokale Logzeit) ausgeloeste Stopps gehen auf unsere Aktion zurueck.</summary>
    public void MarkOurStop(DateTime localNow) { _ourStopAt ??= localNow.AddMilliseconds(-300); }

    public void Feed(IEnumerable<string> rawLines)
    {
        AtasLogLine? last = null;
        foreach (var raw in rawLines)
        {
            var l = AtasLogLine.Parse(raw);
            if (l == null) continue;                    // Fortsetzungszeilen (Stacktraces) zaehlen nie allein
            last = l;
            Feed(l);
        }
        _ = last;
    }

    public void Feed(AtasLogLine l)
    {
        var msg = l.Message;
        var w = RxWarm.Match(msg);
        if (w.Success && l.IsReplaySource && !Started)
        {
            if (DateTime.TryParseExact(w.Groups["s"].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var ws))
            { WarmStartUtc = DateTime.SpecifyKind(ws, DateTimeKind.Utc); WarmLine = l; }
        }
        var m = RxPlaying.Match(msg);
        if (m.Success && l.IsReplaySource)
        {
            var r = (Utc(m.Groups["s"].Value), Utc(m.Groups["e"].Value));
            if (!Started) { Range = r; RangeLine = l; State = ReplayEnd.Running; return; }
            if (r == Range) { LoopRestarted = true; TryLatchNatural(l, "Neustart auf denselben Bereich nach Abschluss (Auto-Loop)"); }
            else if (!_latched) Set(ReplayEnd.OtherRangeRestart, "zweites 'Playing from' mit ANDEREM Bereich", l);
            return;
        }
        if (!Started || _latched) return;

        bool ours = _ourStopAt != null && l.Ts >= _ourStopAt.Value;
        if (l.IsReplaySource && (l.Level is "ERROR" or "FATAL"
                                 || msg.Contains("faulted", StringComparison.OrdinalIgnoreCase)
                                 || msg.Contains("Exception", StringComparison.Ordinal)))
        { Set(ReplayEnd.Errored, $"Fehler der Replay-Quelle '{l.Source}' (Stufe {l.Level})", l); return; }

        if (msg.Contains("to online state", StringComparison.OrdinalIgnoreCase))
        { Set(ours ? ReplayEnd.StoppedByUs : ReplayEnd.OnlineExit, ours ? "Wechsel zu Echtzeit durch uns" : "Wechsel zu Echtzeit (nicht durch uns)", l); return; }

        // Hinweise (nie allein ein Abbruchgrund): Verbindungsmeldungen und Fehler ANDERER Module.
        if (Hints.Count < 30 && (msg.Contains("Disconnect", StringComparison.OrdinalIgnoreCase) || l.Level is "ERROR" or "FATAL")) Hints.Add(l);

        if (l.IsReplaySource && (msg.Contains("Playing historical data stopped") || msg.Contains("State changed Started -> Stopped") || msg.Contains("Play -> Stopped")))
        {
            if (ours) { Set(ReplayEnd.StoppedByUs, "Stopp nach eigener Aktion", l); return; }
            _stop ??= l;
        }
        if (l.IsReplaySource && msg.Contains("Data processing finished") && !ours) _finished ??= l;
        if (l.IsReplaySource && msg.Contains("start path = rewind")) TryLatchNatural(l, "Abschluss + Auto-Rewind");
        if (_stop != null && _finished != null && DataReachedEnd) TryLatchNatural(null, "Abschluss + Recorder-Marktzeit am Fensterende (unterstuetzend)");
    }

    /// <summary>Nach einem Datenupdate erneut pruefen (Recorder-Marktzeit am Ende ist nur Bestaetigung, nie allein).</summary>
    public void Recheck() { if (!_latched && _stop != null && _finished != null && DataReachedEnd) TryLatchNatural(null, "Abschluss + Recorder-Marktzeit am Fensterende (unterstuetzend)"); }

    private void TryLatchNatural(AtasLogLine? confirm, string rule)
    {
        if (_latched || _stop == null || _finished == null) return;
        _latched = true;
        State = ReplayEnd.NaturalEnd; Rule = rule;
        Evidence.Clear(); Evidence.Add(_stop); Evidence.Add(_finished); if (confirm != null) Evidence.Add(confirm);
    }

    private void Set(ReplayEnd s, string rule, AtasLogLine l)
    {
        if (_latched) return;
        State = s; Rule = rule; Evidence.Clear(); Evidence.Add(l);
        if (s is ReplayEnd.Errored or ReplayEnd.OnlineExit or ReplayEnd.OtherRangeRestart or ReplayEnd.StoppedByUs) _latched = true;
    }

    private static DateTime Utc(string s) =>
        DateTime.SpecifyKind(DateTime.ParseExact(s, "dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture), DateTimeKind.Utc);
}

/// <summary>In welcher Zeitzone der ATAS-Datumsdialog Eingaben erwartet. Wird nach dem Laden GEPRUEFT, nie still korrigiert.</summary>
internal sealed record DialogZone(string Source, TimeZoneInfo? Zone, int? FixedOffsetMinutes)
{
    public DateTime FromUtc(DateTime utc) =>
        FixedOffsetMinutes is int off ? DateTime.SpecifyKind(utc.AddMinutes(off), DateTimeKind.Unspecified)
                                      : TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone ?? TimeZoneInfo.Local);

    public string Describe(DateTime utc) =>
        FixedOffsetMinutes is int off ? $"fester Versatz UTC{(off >= 0 ? "+" : "")}{off / 60.0:0.##} h"
                                      : $"{(Zone ?? TimeZoneInfo.Local).Id} (UTC{FormatOffset((Zone ?? TimeZoneInfo.Local).GetUtcOffset(utc))} an diesem Datum)";

    private static string FormatOffset(TimeSpan t) => (t < TimeSpan.Zero ? "-" : "+") + t.Duration().ToString(@"hh\:mm");

    /// <summary>Liest NUR die beiden Zeitzonen-Schluessel aus Platform.cnf (die Datei enthaelt auch Anmeldedaten; diese werden
    /// weder gelesen noch protokolliert). Automatisch -> Windows-Zeitzone; sonst fester Versatz.</summary>
    public static DialogZone Resolve(string? platformCnfPath = null)
    {
        string path = platformCnfPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ATAS", "Platform.cnf");
        bool? auto = null; int? minutes = null;
        try
        {
            if (File.Exists(path))
            {
                var t = File.ReadAllText(path);
                var a = Regex.Match(t, "\"IsAutoUpdateTimeZone\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
                var o = Regex.Match(t, "\"SavedTimeZoneOffsetMinutes\"\\s*:\\s*(-?\\d+)");
                if (a.Success) auto = a.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase);
                if (o.Success) minutes = int.Parse(o.Groups[1].Value, CultureInfo.InvariantCulture);
            }
        }
        catch { }
        if (auto == false && minutes != null)
            return new DialogZone($"ATAS-Einstellung fester Versatz ({minutes} min, automatisch=aus)", null, minutes);
        return new DialogZone($"Windows-Zeitzone {TimeZoneInfo.Local.Id} (ATAS-Zeitzone automatisch={(auto?.ToString() ?? "unbekannt")}" +
                              $"{(minutes != null ? $", gespeicherter Versatz {minutes} min" : "")})", TimeZoneInfo.Local, null);
    }
}
