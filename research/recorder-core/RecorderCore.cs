using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace OrderFlowTools
{
    /// <summary>
    /// Lebenszyklus-/Writer-KERN des OF Replay Recorders — bewusst OHNE ATAS-Abhängigkeit, damit der
    /// TATSÄCHLICHE Produktionsablauf offline geprüft werden kann (RecorderCoreTests kompiliert genau
    /// diese Datei; kein driftender Copy-Harness). Der ATAS-Indicator (OrderFlowReplayRecorder.cs)
    /// delegiert Start/Enqueue/Finish an diese Klasse.
    ///
    /// Fixes Finding 3 (Codex):
    ///  - terminaler Lifecycle-Zustand (Idle->Running->Disposing->Disposed); KEIN Neustart nach Dispose,
    ///  - synchronisierter Annahme-/Zählabschluss: laufende Producer werden vor Summary abgewartet,
    ///  - jeder Flush-/Schreibfehler wird dauerhaft gemerkt (nicht verschluckt),
    ///  - Handles werden unabhängig vom Flush-Erfolg geschlossen (Flush/Dispose getrennt gekapselt),
    ///  - Summary-Schreibfehler werden ausdrücklich gemeldet (Retry + Marker, kein toter Enqueue-Fallback),
    ///  - nach Annahmestopp abgewiesene Events werden gezählt (kein stilles Verwerfen).
    /// </summary>
    public sealed class RecorderCore
    {
        public enum LifeState { Idle, Running, Disposing, Disposed }

        private sealed record Line(string Stream, string Json, int Bytes);

        private readonly string _outputRoot;
        private readonly string _defaultRoot;
        private readonly int _queueCapacity;
        private readonly Func<string> _instrument;
        private readonly Func<string?>? _runIdProvider;

        private BlockingCollection<Line>? _queue;
        private Thread? _writer;
        private readonly Dictionary<string, TextWriter> _writers = new();
        private string _dir = "";
        private volatile LifeState _state = LifeState.Idle;
        private volatile bool _writerFaulted;
        private volatile bool _joinTimedOut;
        private volatile bool _barrierTimedOut;   // Finding 3 (Recheck): Producer-Barriere lief in den Timeout -> kein sauberer Abschluss
        private volatile bool _summaryWriteFailed;
        private string? _runId;

        private long _seq;
        private long _inFlight;                 // laufende Enqueue-Producer (Barriere vor Abschluss)
        private long _rejected;                 // nach Annahmestopp verworfene Events (bilanziert)
        private readonly ConcurrentDictionary<string, long> _count = new();
        private readonly ConcurrentDictionary<string, long> _drops = new();
        private readonly ConcurrentDictionary<string, long> _bytes = new();
        private long _queueHighWater;
        private DateTime _startWall;

        // Test-Nahtstellen (Default = Produktionsverhalten): Writer-Fabrik + Join-Timeout.
        internal Func<string, string, TextWriter> WriterFactory = DefaultWriterFactory;
        internal int WriterJoinMs = 30000;
        internal int BarrierMs = 5000;

        public RecorderCore(string outputRoot, string defaultRoot, int queueCapacity,
                            Func<string> instrument, Func<string?>? runIdProvider = null)
        {
            _outputRoot = outputRoot;
            _defaultRoot = defaultRoot;
            _queueCapacity = queueCapacity;
            _instrument = instrument ?? (() => "UNKNOWN");
            _runIdProvider = runIdProvider;
        }

        // Finding 4 (Recheck): echte, token-UNABHÄNGIGE Instanz-ID -> eindeutiger Ordner auch bei gleichem runId/Sekunde.
        private readonly string _instanceId = Guid.NewGuid().ToString("N");

        public LifeState State => _state;
        public bool Running => _state == LifeState.Running;
        public string Dir => _dir;
        public string RunId => _runId ?? "";
        public string InstanceId => _instanceId;
        public DateTime StartWallUtc => _startWall;
        public bool WriterFaulted => _writerFaulted;
        public bool JoinTimedOut => _joinTimedOut;
        public bool BarrierTimedOut => _barrierTimedOut;
        public bool SummaryWriteFailed => _summaryWriteFailed;
        public long TotalDrops => _drops.Values.Sum();
        public long Rejected => Interlocked.Read(ref _rejected);
        public long NextSeq() => Interlocked.Increment(ref _seq);
        // Test-Nahtstelle: modelliert einen zwischen TryAdd und Decrement angehaltenen Producer (Finding 3 Barriere).
        internal void TestSetInFlight(long n) => Interlocked.Exchange(ref _inFlight, n);

        private static TextWriter DefaultWriterFactory(string dir, string stream)
        {
            var path = Path.Combine(dir, stream);
            var sw = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
                                      new UTF8Encoding(false)) { AutoFlush = false };
            if (stream == "ticks.csv" && new FileInfo(path).Length == 0)
                sw.WriteLine("Time,Instrument,Price,Volume,AggressorSide");
            return sw;
        }

        /// <summary>Startet Writer-Thread + Ordner, falls Idle. Terminaler Zustand: nach Dispose KEIN Neustart.</summary>
        public void Start()
        {
            if (_state != LifeState.Idle) return;        // Finding 3: kein Restart nach Disposing/Disposed
            lock (_writers)
            {
                if (_state != LifeState.Idle) return;
                _startWall = DateTime.UtcNow;
                try { _runId = _runIdProvider?.Invoke(); } catch { _runId = null; }
                if (string.IsNullOrWhiteSpace(_runId))
                {
                    try { _runId = Environment.GetEnvironmentVariable("ATAS_OF_RUN_ID"); } catch { }
                }
                if (string.IsNullOrWhiteSpace(_runId)) _runId = Guid.NewGuid().ToString("N");
                var root = string.IsNullOrWhiteSpace(_outputRoot) ? _defaultRoot : _outputRoot;
                var driveRoot = Path.GetPathRoot(root);
                if (string.IsNullOrEmpty(driveRoot) || !Directory.Exists(driveRoot))
                    throw new DirectoryNotFoundException(
                        $"OF Replay Recorder: Ziellaufwerk '{driveRoot}' fuer OutputRoot '{root}' nicht verfuegbar - KEIN Fallback nach C:.");
                var sym = SafeSym();
                // Eindeutiger Ordner: Sekunde + echte Instanz-ID (NICHT das ggf. geteilte Token) -> keine Kollision
                // zweier Recorder in derselben Sekunde, auch bei gleichem runId (Finding 4 Recheck).
                _dir = Path.Combine(root, $"{sym}_{_startWall:yyyyMMdd_HHmmss}Z_{_instanceId.Substring(0, 8)}");
                Directory.CreateDirectory(_dir);
                _queue = new BlockingCollection<Line>(Math.Max(1000, _queueCapacity));
                _state = LifeState.Running;
                _writer = new Thread(WriterLoop) { IsBackground = true, Name = "OFRecorderWriter" };
                _writer.Start();
            }
        }

        private string SafeSym()
        {
            try { var s = _instrument(); return string.IsNullOrEmpty(s) ? "UNKNOWN" : s; }
            catch { return "UNKNOWN"; }
        }

        private void WriterLoop()
        {
            try
            {
                var sw = Stopwatch.StartNew();
                long sinceFlush = 0;
                while (!_queue!.IsCompleted)
                {
                    if (_queue.TryTake(out var ln, 200))
                    {
                        var w = GetWriter(ln.Stream);
                        w.WriteLine(ln.Json);
                        _bytes.AddOrUpdate(ln.Stream, ln.Bytes, (_, b) => b + ln.Bytes);
                        sinceFlush++;
                    }
                    if (sinceFlush > 0 && (sinceFlush >= 200 || sw.ElapsedMilliseconds >= 500))
                    {
                        // Finding 3: periodischer Flush-Fehler wird dauerhaft gemerkt (nicht verschluckt).
                        foreach (var wr in _writers.Values)
                            try { wr.Flush(); } catch { _writerFaulted = true; }
                        sinceFlush = 0; sw.Restart();
                    }
                }
            }
            catch (Exception) { _writerFaulted = true; }
            finally
            {
                // Finding 3: Handles unabhängig vom Flush-Erfolg schließen -> Flush und Dispose GETRENNT kapseln.
                foreach (var w in _writers.Values)
                {
                    try { w.Flush(); } catch { _writerFaulted = true; }
                    try { w.Dispose(); } catch { _writerFaulted = true; }
                }
            }
        }

        private TextWriter GetWriter(string stream)
        {
            if (_writers.TryGetValue(stream, out var w)) return w;
            var sw = WriterFactory(_dir, stream);
            _writers[stream] = sw;
            return sw;
        }

        /// <summary>Nicht-blockierend einreihen. Barriere-gezählt; nach Annahmestopp als 'rejected' bilanziert.</summary>
        public void Enqueue(string stream, string json)
        {
            var s = _state;
            if (s != LifeState.Running)
            {
                if (s == LifeState.Disposing || s == LifeState.Disposed)
                    Interlocked.Increment(ref _rejected);   // kein stilles Verwerfen
                return;
            }
            Interlocked.Increment(ref _inFlight);
            try
            {
                if (_state != LifeState.Running) { Interlocked.Increment(ref _rejected); return; }
                var q = _queue;
                if (q == null || q.IsAddingCompleted) { Interlocked.Increment(ref _rejected); return; }
                var line = new Line(stream, json, Encoding.UTF8.GetByteCount(json) + 1);
                bool added;
                try { added = q.TryAdd(line); }
                catch (InvalidOperationException) { Interlocked.Increment(ref _rejected); return; }
                if (added)
                {
                    _count.AddOrUpdate(stream, 1, (_, c) => c + 1);
                    var n = q.Count;
                    if (n > Interlocked.Read(ref _queueHighWater)) Interlocked.Exchange(ref _queueHighWater, n);
                }
                else
                {
                    var d = _drops.AddOrUpdate(stream, 1, (_, c) => c + 1);
                    var gap = J(("type", "queue_overflow"), ("stream", stream), ("afterSeq", Interlocked.Read(ref _seq)),
                                ("dropCountStream", d), ("tRecv", Iso(DateTime.UtcNow)));
                    try { q.TryAdd(new Line("gaps.ndjson", gap, 0)); } catch { }
                }
            }
            finally { Interlocked.Decrement(ref _inFlight); }
        }

        /// <summary>
        /// Kontrollierter Abschluss (Finding 3), feste Reihenfolge:
        ///  1) terminaler Zustand -> Annahme stoppt; laufende Producer abwarten (Barriere)
        ///  2) CompleteAdding; Writer draint die akzeptierten Events; Join + Prüfung
        ///  3) ERST DANACH Summary mit clean-Status; Schreibfehler ausdrücklich gemeldet.
        /// Mehrfaches Finish ist sicher (idempotent).
        /// </summary>
        public void Finish(Action<string, string>? summarySink = null)
        {
            lock (_writers)
            {
                if (_state == LifeState.Idle) { _state = LifeState.Disposed; WriteSummarySafe(false, summarySink); return; }
                if (_state == LifeState.Disposing || _state == LifeState.Disposed) return;  // mehrfach-Dispose
                _state = LifeState.Disposing;                                               // 1) Annahme stoppen
            }
            // Barriere: keine laufenden Enqueue-Producer mehr, bevor gezählt/geschlossen/summiert wird.
            var spin = new SpinWait();
            var bsw = Stopwatch.StartNew();
            while (Interlocked.Read(ref _inFlight) > 0 && bsw.ElapsedMilliseconds < BarrierMs) spin.SpinOnce();
            // Finding 3 (Recheck): Läuft die Barriere in den Timeout, sind angenommene Producer NICHT abgeschlossen
            // -> das ist ein fehlgeschlagener Abschluss, niemals clean (nicht nur Join/WriterFault entscheiden).
            if (Interlocked.Read(ref _inFlight) > 0) _barrierTimedOut = true;

            bool joined = true;
            try
            {
                _queue?.CompleteAdding();                                   // 2) keine Adds mehr; Writer draint
                if (_writer != null) joined = _writer.Join(WriterJoinMs);   //    warten + prüfen
            }
            catch (Exception) { _writerFaulted = true; }
            _joinTimedOut = !joined;
            bool clean = joined && !_writerFaulted && !_barrierTimedOut;
            WriteSummarySafe(clean, summarySink);                          // 3) Summary nach bestätigtem Drain
            _state = LifeState.Disposed;
        }

        private void WriteSummarySafe(bool clean, Action<string, string>? sink)
        {
            string json;
            try { json = BuildSummaryJson(clean); }
            catch { _summaryWriteFailed = true; return; }
            if (sink != null) { try { sink("summary.json", json); } catch { _summaryWriteFailed = true; } return; }
            // Direkt schreiben (kein toter Enqueue-Fallback nach Annahmestopp). Ein Retry, dann Marker + Flag.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try { File.WriteAllText(Path.Combine(_dir, "summary.json"), json, new UTF8Encoding(false)); return; }
                catch { if (attempt == 0) Thread.Sleep(50); }
            }
            _summaryWriteFailed = true;
            try { File.WriteAllText(Path.Combine(_dir, "summary_error.txt"),
                                    "OF Replay Recorder: summary.json konnte nicht geschrieben werden (" + Iso(DateTime.UtcNow) + ")",
                                    new UTF8Encoding(false)); } catch { }
        }

        private string BuildSummaryJson(bool clean)
        {
            var dur = (DateTime.UtcNow - _startWall).TotalSeconds;
            long totalEvents = _count.Values.Sum();
            long totalBytes = _bytes.Values.Sum();
            long totalDrops = _drops.Values.Sum();
            var sb = new StringBuilder();
            sb.Append('{');
            Kv(sb, "schemaVersion", "ofrec-summary-2"); sb.Append(',');
            Kv(sb, "runId", _runId ?? ""); sb.Append(',');
            Kv(sb, "clean", clean); sb.Append(',');
            Kv(sb, "writerFaulted", _writerFaulted); sb.Append(',');
            Kv(sb, "joinTimedOut", _joinTimedOut); sb.Append(',');
            Kv(sb, "barrierTimedOut", _barrierTimedOut); sb.Append(',');
            Kv(sb, "summaryWriteFailed", _summaryWriteFailed); sb.Append(',');
            Kv(sb, "totalDrops", totalDrops); sb.Append(',');
            Kv(sb, "rejectedAfterStop", Interlocked.Read(ref _rejected)); sb.Append(',');
            Kv(sb, "durationSec", (decimal)dur); sb.Append(',');
            Kv(sb, "totalEvents", totalEvents); sb.Append(',');
            Kv(sb, "totalBytes", totalBytes); sb.Append(',');
            Kv(sb, "eventsPerSec", (decimal)(dur > 0 ? totalEvents / dur : 0)); sb.Append(',');
            Kv(sb, "bytesPerSec", (decimal)(dur > 0 ? totalBytes / dur : 0)); sb.Append(',');
            Kv(sb, "queueHighWater", Interlocked.Read(ref _queueHighWater)); sb.Append(',');
            sb.Append("\"perStream\":{");
            bool first = true;
            foreach (var strm in _count.Keys.Concat(_drops.Keys).Distinct())
            {
                if (!first) sb.Append(',');
                first = false;
                _count.TryGetValue(strm, out var ce); _drops.TryGetValue(strm, out var de); _bytes.TryGetValue(strm, out var be);
                sb.Append('"').Append(strm).Append("\":{");
                Kv(sb, "events", ce); sb.Append(',');
                Kv(sb, "drops", de); sb.Append(',');
                Kv(sb, "bytes", be);
                sb.Append('}');
            }
            sb.Append("}}");
            return sb.ToString();
        }

        // ================= Helfer (identisch zum Indicator; JSON ohne Fremd-Abhängigkeit) =================
        public static string Iso(DateTime t) =>
            (t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t.ToUniversalTime())
            .ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

        public static string Num(decimal d) => d.ToString(CultureInfo.InvariantCulture);

        public static void Kv(StringBuilder sb, string k, decimal v) { sb.Append('"').Append(k).Append("\":").Append(Num(v)); }
        public static void Kv(StringBuilder sb, string k, long v) { sb.Append('"').Append(k).Append("\":").Append(v.ToString(CultureInfo.InvariantCulture)); }
        public static void Kv(StringBuilder sb, string k, int v) { sb.Append('"').Append(k).Append("\":").Append(v.ToString(CultureInfo.InvariantCulture)); }
        public static void Kv(StringBuilder sb, string k, string v) { sb.Append('"').Append(k).Append("\":\"").Append(v).Append('"'); }
        public static void Kv(StringBuilder sb, string k, bool v) { sb.Append('"').Append(k).Append("\":").Append(v ? "true" : "false"); }

        public static string J(params (string k, object? v)[] kv)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            for (int i = 0; i < kv.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(kv[i].k).Append("\":");
                var v = kv[i].v;
                switch (v)
                {
                    case null: sb.Append("null"); break;
                    case bool b: sb.Append(b ? "true" : "false"); break;
                    case decimal d: sb.Append(Num(d)); break;
                    case int n: sb.Append(n.ToString(CultureInfo.InvariantCulture)); break;
                    case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
                    default: sb.Append('"').Append(v.ToString()).Append('"'); break;
                }
            }
            sb.Append('}');
            return sb.ToString();
        }
    }
}
