using System;
using System.IO;
using System.Reflection;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Text.Json;
class StubBase { protected virtual void OnDispose() {} }
class WriterHarness : StubBase {
 public string OutputRoot;
 const string DefaultOutputRoot = "UNUSED";
 public int QueueCapacity = 1000;
 private string SafeInstrument() => "SYNTHETIC";
        private sealed record Line(string Stream, string Json, int Bytes);

        private BlockingCollection<Line> _queue;
        private Thread _writer;
        private readonly Dictionary<string, StreamWriter> _writers = new();
        private string _dir;
        private volatile bool _running;
        private volatile bool _writerFaulted;   // Writer-Thread hatte eine Exception (A3: kein sauberer Abschluss)
        private volatile bool _joinTimedOut;     // Writer-Join lief in den Timeout (Drain nicht bestaetigt)
        private string _runId;                   // Lauf-Korrelation (aus Env ATAS_OF_RUN_ID, sonst GUID) - A4

        private long _seq;                      // monoton, lokal
        private readonly ConcurrentDictionary<string, long> _count = new();   // Events je Stream
        private readonly ConcurrentDictionary<string, long> _drops = new();   // verworfen je Stream (Overflow)
        private readonly ConcurrentDictionary<string, long> _bytes = new();
        private long _queueHighWater;
        private DateTime _startWall;
        private void StartIfNeeded()
        {
            if (_running) return;
            lock (_writers)
            {
                if (_running) return;
                _startWall = DateTime.UtcNow;
                // A4: Lauf-Korrelation. Der Steuerungslauf kann ATAS_OF_RUN_ID setzen; sonst eindeutige GUID.
                try { _runId = Environment.GetEnvironmentVariable("ATAS_OF_RUN_ID"); } catch { }
                if (string.IsNullOrWhiteSpace(_runId)) _runId = Guid.NewGuid().ToString("N");
                var root = string.IsNullOrWhiteSpace(OutputRoot) ? DefaultOutputRoot : OutputRoot;
                // Kein stiller Fallback nach C:: fehlt das Ziellaufwerk, klar abbrechen statt auf C: zu schreiben.
                var driveRoot = Path.GetPathRoot(root);
                if (string.IsNullOrEmpty(driveRoot) || !Directory.Exists(driveRoot))
                    throw new DirectoryNotFoundException(
                        $"OF Replay Recorder: Ziellaufwerk '{driveRoot}' fuer OutputRoot '{root}' nicht verfuegbar - KEIN Fallback nach C:.");
                var sym = SafeInstrument();
                _dir = Path.Combine(root, $"{sym}_{_startWall:yyyyMMdd_HHmmss}Z");
                Directory.CreateDirectory(_dir);

                _queue = new BlockingCollection<Line>(Math.Max(1000, QueueCapacity));
                _running = true;
                _writer = new Thread(WriterLoop) { IsBackground = true, Name = "OFRecorderWriter" };
                _writer.Start();
            }
        }

        private void WriterLoop()
        {
            try
            {
                var sw = Stopwatch.StartNew();
                long sinceFlush = 0;
                // Timeout-Take: auch im Leerlauf (pausiertes Replay) regelmaessig flushen, damit
                // meta.json & Co. sofort auf Platte stehen und bei Absturz nichts im Puffer verloren geht.
                while (!_queue.IsCompleted)
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
                        foreach (var wr in _writers.Values) { try { wr.Flush(); } catch { } }
                        sinceFlush = 0; sw.Restart();
                    }
                }
            }
            catch (Exception) { _writerFaulted = true; /* Writer darf Callbacks nie stoeren, aber Fehler wird vermerkt (A3) */ }
            finally
            {
                foreach (var w in _writers.Values) { try { w.Flush(); w.Dispose(); } catch { _writerFaulted = true; } }
            }
        }

        private StreamWriter GetWriter(string stream)
        {
            if (_writers.TryGetValue(stream, out var w)) return w;
            var path = Path.Combine(_dir, stream);
            var sw = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read),
                                      new UTF8Encoding(false)) { AutoFlush = false };
            // CSV-Header einmalig.
            if (stream == "ticks.csv" && new FileInfo(path).Length == 0)
                sw.WriteLine("Time,Instrument,Price,Volume,AggressorSide");
            _writers[stream] = sw;
            return sw;
        }

        /// <summary>Nicht-blockierend einreihen; bei vollem Puffer: Drop zaehlen + Luecke markieren (kein stilles Verwerfen).</summary>
        private void Enqueue(string stream, string json)
        {
            if (!_running) return;
            var q = _queue;
            if (q == null || q.IsAddingCompleted) return;   // A3: nach CompleteAdding nichts mehr annehmen (kein Throw/Race)
            var line = new Line(stream, json, Encoding.UTF8.GetByteCount(json) + 1);
            bool added;
            try { added = q.TryAdd(line); }
            catch (InvalidOperationException) { return; }    // Dispose lief gerade an -> nicht mehr annehmen
            if (added)
            {
                _count.AddOrUpdate(stream, 1, (_, c) => c + 1);
                var n = q.Count;
                if (n > Interlocked.Read(ref _queueHighWater)) Interlocked.Exchange(ref _queueHighWater, n);
            }
            else
            {
                var d = _drops.AddOrUpdate(stream, 1, (_, c) => c + 1);
                // Luecke dokumentieren (best effort, selbst ueber die Queue, sonst verworfen).
                var gap = J(("type", "queue_overflow"), ("stream", stream), ("afterSeq", _seq), ("dropCountStream", d),
                            ("tRecv", Iso(DateTime.UtcNow)));
                try { q.TryAdd(new Line("gaps.ndjson", gap, 0)); } catch { }
            }
        }
        private const int WriterJoinMs = 30000;
        protected override void OnDispose()
        {
            if (!_running) { base.OnDispose(); return; }
            _running = false;                                  // 1) Annahme stoppen
            bool joined = true;
            try
            {
                _queue?.CompleteAdding();                       // 2) keine Adds mehr; Writer draint Restqueue
                if (_writer != null) joined = _writer.Join(WriterJoinMs);  // 3) auf Drain/Flush warten + pruefen
            }
            catch (Exception) { _writerFaulted = true; }
            _joinTimedOut = !joined;
            bool clean = joined && !_writerFaulted;
            try { WriteSummary(clean); } catch { }             // 4) Summary mit Status erst nach bestaetigtem Drain
            base.OnDispose();
        }

        private void WriteSummary(bool clean)
        {
            var dur = (DateTime.UtcNow - _startWall).TotalSeconds;
            long totalEvents = _count.Values.Sum();
            long totalBytes = _bytes.Values.Sum();
            long totalDrops = _drops.Values.Sum();
            var sb = new StringBuilder();
            sb.Append('{');
            Kv(sb, "schemaVersion", "ofrec-summary-2"); sb.Append(',');   // A3: Schema-/Statusversion
            Kv(sb, "runId", _runId ?? ""); sb.Append(',');                // A4: Lauf-Korrelation
            Kv(sb, "clean", clean); sb.Append(',');                        // true nur bei bestaetigtem Drain ohne Writer-Fault
            Kv(sb, "writerFaulted", _writerFaulted); sb.Append(',');
            Kv(sb, "joinTimedOut", _joinTimedOut); sb.Append(',');
            Kv(sb, "totalDrops", totalDrops); sb.Append(',');
            Kv(sb, "durationSec", (decimal)dur); sb.Append(',');
            Kv(sb, "totalEvents", totalEvents); sb.Append(',');
            Kv(sb, "totalBytes", totalBytes); sb.Append(',');
            Kv(sb, "eventsPerSec", (decimal)(dur > 0 ? totalEvents / dur : 0)); sb.Append(',');
            Kv(sb, "bytesPerSec", (decimal)(dur > 0 ? totalBytes / dur : 0)); sb.Append(',');
            Kv(sb, "queueHighWater", Interlocked.Read(ref _queueHighWater)); sb.Append(',');
            sb.Append("\"perStream\":{");
            bool first = true;
            foreach (var s in _count.Keys.Concat(_drops.Keys).Distinct())
            {
                if (!first) sb.Append(',');
                first = false;
                _count.TryGetValue(s, out var ce); _drops.TryGetValue(s, out var de); _bytes.TryGetValue(s, out var be);
                sb.Append('"').Append(s).Append("\":{");
                Kv(sb, "events", ce); sb.Append(',');
                Kv(sb, "drops", de); sb.Append(',');
                Kv(sb, "bytes", be);
                sb.Append('}');
            }
            sb.Append("}}");
            // Direkt schreiben (Writer laeuft evtl. noch); notfalls ueber Queue.
            try
            {
                File.WriteAllText(Path.Combine(_dir, "summary.json"), sb.ToString(), new UTF8Encoding(false));
            }
            catch { Enqueue("summary.json", sb.ToString()); }
        }
        private static string Iso(DateTime t) =>
            (t.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : t.ToUniversalTime())
            .ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

        private static string Num(decimal d) => d.ToString(CultureInfo.InvariantCulture);

        private static void Kv(StringBuilder sb, string k, decimal v) { sb.Append('"').Append(k).Append("\":").Append(Num(v)); }
        private static void Kv(StringBuilder sb, string k, long v) { sb.Append('"').Append(k).Append("\":").Append(v.ToString(CultureInfo.InvariantCulture)); }
        private static void Kv(StringBuilder sb, string k, int v) { sb.Append('"').Append(k).Append("\":").Append(v.ToString(CultureInfo.InvariantCulture)); }
        private static void Kv(StringBuilder sb, string k, string v) { sb.Append('"').Append(k).Append("\":\"").Append(v).Append('"'); }
        private static void Kv(StringBuilder sb, string k, bool v) { sb.Append('"').Append(k).Append("\":").Append(v ? "true" : "false"); }

        private static string J(params (string k, object v)[] kv)
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
 sealed class FailOnceWriter : StreamWriter {
  public readonly ManualResetEventSlim Failed = new(false);
  public FailOnceWriter() : base(new MemoryStream()) {}
  public override void Flush() { if (!Failed.IsSet) { Failed.Set(); throw new IOException("synthetic transient flush failure"); } base.Flush(); }
 }
 public static void Main(string[] args) {
  var root=args[0];
  var h=new WriterHarness { OutputRoot=Path.Combine(root,"writer_restart") };
  h.StartIfNeeded(); h.Enqueue("ticks.ndjson","{}"); h.OnDispose();
  var previous=h._writer;
  // Exact same call that begins each production callback, after OnDispose.
  h.StartIfNeeded();
  Console.WriteLine("callback_after_dispose_restarts_writer="+(h._running && h._writer!=previous));
  h.OnDispose();
  var f=new WriterHarness { OutputRoot=Path.Combine(root,"writer_flush") };
  var fault=new FailOnceWriter(); f._writers["ticks.ndjson"]=fault;
  f.StartIfNeeded(); f.Enqueue("ticks.ndjson","{}");
  if(!fault.Failed.Wait(4000)) throw new Exception("flush injection not reached");
  f.OnDispose();
  using var j=JsonDocument.Parse(File.ReadAllText(Path.Combine(f._dir,"summary.json")));
  Console.WriteLine("transient_flush_failed="+fault.Failed.IsSet+" summary_clean="+j.RootElement.GetProperty("clean")+" writerFaulted="+j.RootElement.GetProperty("writerFaulted"));
 }
}
