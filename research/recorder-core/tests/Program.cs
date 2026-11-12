using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Text.Json;
using OrderFlowTools;

// Offline-Regressionstests für den ECHTEN Recorder-Lebenszyklus (Finding 3).
// Jeder Test kodiert ein Codex-Gegenbeispiel und läuft gegen RecorderCore (Produktionscode).
internal static class Program
{
    private static int _fail;
    private static void Check(string name, bool ok, string detail = "")
    {
        Console.WriteLine($"  [{(ok ? "OK  " : "FAIL")}] {name}{(detail == "" ? "" : "  -> " + detail)}");
        if (!ok) _fail++;
    }

    // --- Test-Writer (Nahtstellen) ---
    private sealed class FailOnceFlushWriter : StreamWriter
    {
        public bool FlushFailed;
        public FailOnceFlushWriter() : base(new MemoryStream()) { }
        public override void Flush()
        {
            if (!FlushFailed) { FlushFailed = true; throw new IOException("synthetic transient flush failure"); }
            base.Flush();
        }
    }

    private sealed class AlwaysFailFlushWriter : StreamWriter
    {
        public bool Disposed;
        public AlwaysFailFlushWriter() : base(new MemoryStream()) { }
        public override void Flush() => throw new IOException("synthetic permanent flush failure");
        protected override void Dispose(bool disposing) { Disposed = true; /* Handle schließen, Flush NICHT aufrufen */ }
    }

    private sealed class BlockingWriteWriter : StreamWriter
    {
        public readonly ManualResetEventSlim Gate = new(false);
        public BlockingWriteWriter() : base(new MemoryStream()) { }
        public override void WriteLine(string? value) { Gate.Wait(10000); base.WriteLine(value); }
    }

    private static RecorderCore NewCore(string root) =>
        new RecorderCore(root, root, 1000, () => "SYNTHETIC", () => null);

    public static int Main(string[] args)
    {
        // Standard-Ausgabe neben die Assembly (auf A:), NICHT nach C:-Temp (Projektvorgabe).
        var root = args.Length > 0 ? args[0]
                                   : Path.Combine(AppContext.BaseDirectory, "rc_tests_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(root);
        Console.WriteLine($"# RecorderCore Regression (Finding 3)  root={root}");

        // 1) Später Callback nach Dispose darf den Writer NICHT neu starten (terminaler Zustand).
        {
            var c = NewCore(Path.Combine(root, "restart")); Directory.CreateDirectory(Path.Combine(root, "restart"));
            c.Start(); c.Enqueue("ticks.ndjson", "{}"); c.Finish();
            var stateAfter = c.State;
            c.Start();                       // exakt der Aufruf, der jeden Produktions-Callback beginnt
            Check("later_callback_after_dispose_does_not_restart",
                  c.State == RecorderCore.LifeState.Disposed && !c.Running,
                  $"state={c.State} running={c.Running} (stateAfterFinish={stateAfter})");
        }

        // 2) Transienter Flush-Fehler darf NICHT clean werden.
        {
            var dir = Path.Combine(root, "transient"); Directory.CreateDirectory(dir);
            var c = NewCore(dir);
            var fault = new FailOnceFlushWriter();
            c.WriterFactory = (_, stream) => stream == "ticks.ndjson" ? fault : new StreamWriter(new MemoryStream());
            c.Start(); c.Enqueue("ticks.ndjson", "{}"); c.Finish();
            var s = ReadSummary(c.Dir);
            Check("transient_flush_failure_not_clean",
                  fault.FlushFailed && s.GetProperty("clean").GetBoolean() == false
                                    && s.GetProperty("writerFaulted").GetBoolean() == true,
                  $"flushFailed={fault.FlushFailed} clean={s.GetProperty("clean")} faulted={s.GetProperty("writerFaulted")}");
        }

        // 3) Finaler Flush-Fehler: Handles werden trotzdem geschlossen; nicht clean.
        {
            var dir = Path.Combine(root, "final"); Directory.CreateDirectory(dir);
            var c = NewCore(dir);
            var fault = new AlwaysFailFlushWriter();
            c.WriterFactory = (_, stream) => stream == "ticks.ndjson" ? fault : new StreamWriter(new MemoryStream());
            c.Start(); c.Enqueue("ticks.ndjson", "{}"); c.Finish();
            var s = ReadSummary(c.Dir);
            Check("final_flush_failure_still_closes_handles_not_clean",
                  fault.Disposed && s.GetProperty("clean").GetBoolean() == false,
                  $"disposed={fault.Disposed} clean={s.GetProperty("clean")} faulted={s.GetProperty("writerFaulted")}");
        }

        // 4) Join-Timeout: nicht clean, joinTimedOut=true (Writer blockiert, kleiner Timeout).
        {
            var dir = Path.Combine(root, "jointimeout"); Directory.CreateDirectory(dir);
            var c = NewCore(dir);
            var blk = new BlockingWriteWriter();
            c.WriterFactory = (_, stream) => stream == "ticks.ndjson" ? blk : new StreamWriter(new MemoryStream());
            c.WriterJoinMs = 800; c.BarrierMs = 300;
            c.Start(); c.Enqueue("ticks.ndjson", "{}");
            Thread.Sleep(300);               // Writer hängt in WriteLine
            c.Finish();
            var s = ReadSummary(c.Dir);
            Check("join_timeout_not_clean",
                  s.GetProperty("joinTimedOut").GetBoolean() == true && s.GetProperty("clean").GetBoolean() == false,
                  $"joinTimedOut={s.GetProperty("joinTimedOut")} clean={s.GetProperty("clean")}");
            blk.Gate.Set();                  // Writer-Thread freigeben (kein Leak)
        }

        // 5) Mehrfaches Finish ist sicher (keine Exception, Summary bleibt gültig).
        {
            var dir = Path.Combine(root, "double"); Directory.CreateDirectory(dir);
            var c = NewCore(dir);
            c.Start(); c.Enqueue("ticks.ndjson", "{}");
            bool ok = true;
            try { c.Finish(); c.Finish(); } catch { ok = false; }
            var s = ReadSummary(c.Dir);
            Check("repeated_finish_is_safe",
                  ok && s.GetProperty("clean").GetBoolean() == true, $"ok={ok}");
        }

        // 6) Drain bestätigt: alle akzeptierten Events stehen auf Platte, Zähler == Dateizeilen, clean.
        {
            var dir = Path.Combine(root, "counts"); Directory.CreateDirectory(dir);
            var c = NewCore(dir);
            c.Start();
            int n = 500;
            for (int i = 0; i < n; i++) c.Enqueue("ticks.ndjson", "{\"i\":" + i + "}");
            c.Finish();
            var s = ReadSummary(c.Dir);
            long fileLines = File.Exists(Path.Combine(c.Dir, "ticks.ndjson"))
                ? File.ReadAllLines(Path.Combine(c.Dir, "ticks.ndjson")).Length : -1;
            long summaryEvents = s.GetProperty("perStream").GetProperty("ticks.ndjson").GetProperty("events").GetInt64();
            Check("drain_confirmed_counts_match_file",
                  fileLines == n && summaryEvents == n && s.GetProperty("clean").GetBoolean() == true
                                 && s.GetProperty("writerFaulted").GetBoolean() == false,
                  $"fileLines={fileLines} summaryEvents={summaryEvents} clean={s.GetProperty("clean")}");
        }

        // 7) Events nach Annahmestopp werden bilanziert (kein stilles Verwerfen) und werfen nicht.
        {
            var dir = Path.Combine(root, "rejected"); Directory.CreateDirectory(dir);
            var c = NewCore(dir);
            c.Start(); c.Enqueue("ticks.ndjson", "{}"); c.Finish();
            bool threw = false;
            try { for (int i = 0; i < 5; i++) c.Enqueue("ticks.ndjson", "{\"late\":true}"); } catch { threw = true; }
            Check("events_after_stop_counted_not_silently_dropped",
                  !threw && c.Rejected >= 5, $"threw={threw} rejected={c.Rejected}");
        }

        // 8) Recheck: Barriere-Timeout mit verbleibendem Producer -> NICHT clean.
        {
            var dir = Path.Combine(root, "barrier"); Directory.CreateDirectory(dir);
            var c = NewCore(dir);
            c.Start(); c.Enqueue("ticks.ndjson", "{}");
            c.TestSetInFlight(1);          // Producer zwischen TryAdd und Decrement angehalten
            c.BarrierMs = 30;
            c.Finish();
            var s = ReadSummary(c.Dir);
            Check("barrier_timeout_pending_producer_not_clean",
                  s.GetProperty("barrierTimedOut").GetBoolean() == true && s.GetProperty("clean").GetBoolean() == false,
                  $"barrierTimedOut={s.GetProperty("barrierTimedOut")} clean={s.GetProperty("clean")}");
            c.TestSetInFlight(0);
        }

        // 9) Recheck: Indicator-Gate ist vor erstem Callback terminal (Dispose vor GetOrCreate -> kein neuer Kern).
        {
            var dir = Path.Combine(root, "early_dispose"); Directory.CreateDirectory(dir);
            var gate = new CoreGate();
            gate.Dispose();                                 // Dispose VOR jeder Core-Erzeugung
            var later = gate.GetOrCreateAndStart(() => NewCore(dir));
            Check("gate_dispose_before_first_callback_no_restart",
                  later == null && gate.IsDisposed && gate.CoreOrNull == null,
                  $"later={(later == null ? "null" : "running=" + later.Running)}");
            // Auch: normal erzeugt, dann Dispose, dann erneut -> kein neuer Kern.
            var dir2 = Path.Combine(root, "early_dispose2"); Directory.CreateDirectory(dir2);
            var g2 = new CoreGate();
            var c1 = g2.GetOrCreateAndStart(() => NewCore(dir2));
            g2.Dispose();
            var c2 = g2.GetOrCreateAndStart(() => NewCore(dir2));
            Check("gate_after_dispose_returns_null", c1 != null && c2 == null);
        }

        // 10) Recheck: periodischer (nicht nur finaler) transienter Flush-Fehler -> NICHT clean.
        {
            var dir = Path.Combine(root, "periodic"); Directory.CreateDirectory(dir);
            var c = NewCore(dir);
            var once = new FailOnceFlushWriter();
            c.WriterFactory = (_, stream) => stream == "ticks.ndjson" ? once : new StreamWriter(new MemoryStream());
            c.Start(); c.Enqueue("ticks.ndjson", "{}");
            // Auf den PERIODISCHEN Flush warten (nicht sofort Finish), damit der Fehler im Loop passiert.
            if (!SpinUntil(() => once.FlushFailed, 4000)) { Check("periodic_flush_reached", false, "Flush nicht erreicht"); }
            c.Enqueue("ticks.ndjson", "{}");    // Writer läuft weiter; folgender Flush gelingt wieder
            c.Finish();
            var s = ReadSummary(c.Dir);
            Check("periodic_transient_flush_not_clean",
                  once.FlushFailed && s.GetProperty("clean").GetBoolean() == false && s.GetProperty("writerFaulted").GetBoolean() == true,
                  $"clean={s.GetProperty("clean")} faulted={s.GetProperty("writerFaulted")}");
        }

        // 11) Recheck: Summary-Schreibfehler wird ausdrücklich gemeldet (Property + Marker).
        {
            var dir = Path.Combine(root, "summaryfail"); Directory.CreateDirectory(dir);
            var c = NewCore(dir);
            c.Start(); c.Enqueue("ticks.ndjson", "{}");
            // summary.json als VERZEICHNIS anlegen -> File.WriteAllText scheitert.
            Directory.CreateDirectory(Path.Combine(c.Dir, "summary.json"));
            c.Finish();
            Check("summary_write_failure_reported",
                  c.SummaryWriteFailed && File.Exists(Path.Combine(c.Dir, "summary_error.txt")),
                  $"summaryWriteFailed={c.SummaryWriteFailed}");
        }

        // 12) Recheck (Finding 4): Token wird EXKLUSIV konsumiert; zwei Konsumenten -> genau einer gewinnt.
        {
            var dir = Path.Combine(root, "token_excl"); Directory.CreateDirectory(dir);
            TokenHandshake.Publish(dir, "job-ABC");
            var a = TokenHandshake.Consume(dir, "claimA");
            var b = TokenHandshake.Consume(dir, "claimB");
            Check("token_exclusive_consume_one_winner",
                  ((a == "job-ABC") ^ (b == "job-ABC")) && (a == null || b == null),
                  $"a={a ?? "null"} b={b ?? "null"}");
            Check("token_consumed_file_gone", !File.Exists(TokenHandshake.TokenPath(dir)));
        }

        // 13) Recheck (Finding 4): RetractIfOwner löscht nur das eigene Token, nie ein fremdes.
        {
            var dir = Path.Combine(root, "token_retract"); Directory.CreateDirectory(dir);
            TokenHandshake.Publish(dir, "foreign-token");
            TokenHandshake.RetractIfOwner(dir, "my-token");    // fremder Inhalt -> nicht löschen
            bool foreignKept = File.Exists(TokenHandshake.TokenPath(dir));
            TokenHandshake.RetractIfOwner(dir, "foreign-token"); // eigener Inhalt -> löschen
            bool ownDeleted = !File.Exists(TokenHandshake.TokenPath(dir));
            Check("retract_only_owns_token", foreignKept && ownDeleted, $"foreignKept={foreignKept} ownDeleted={ownDeleted}");
        }

        // 14) Recheck (Finding 4): zwei Kerne mit DEMSELBEN runId -> verschiedene Ordner (echte Instanz-ID).
        {
            var dir = Path.Combine(root, "folder_uniq"); Directory.CreateDirectory(dir);
            var c1 = new RecorderCore(dir, dir, 1000, () => "SYNTHETIC", () => "same-run");
            var c2 = new RecorderCore(dir, dir, 1000, () => "SYNTHETIC", () => "same-run");
            c1.Start(); c2.Start();
            Check("same_runId_distinct_directories",
                  c1.RunId == "same-run" && c2.RunId == "same-run" && c1.Dir != c2.Dir && c1.InstanceId != c2.InstanceId,
                  $"dir1={Path.GetFileName(c1.Dir)} dir2={Path.GetFileName(c2.Dir)}");
            c1.Finish(); c2.Finish();
        }

        Console.WriteLine(_fail == 0 ? "\nRECORDERCORE: ALLE BESTANDEN" : $"\nRECORDERCORE: {_fail} FEHLGESCHLAGEN");
        return _fail == 0 ? 0 : 1;
    }

    private static bool SpinUntil(Func<bool> cond, int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; Thread.Sleep(20); }
        return cond();
    }

    private static JsonElement ReadSummary(string dir)
    {
        var path = Path.Combine(dir, "summary.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.Clone();
    }
}
