using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using TradingBot.Application.Sessions;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.MarketData.Import;

namespace TradingBot.DevDashboard.Services;

/// <summary>Eine Fassung eines Handelstags in einer Collection (fuer die Quellenwahl bei mehrfach vorhandenen Tagen).</summary>
public sealed record TickReplayCandidate(
    string Id, string Collection, string RecordingId, long Rows, decimal TotalVolume, long? RecorderDrops, bool? SummaryClean, bool Usable);

/// <summary>Eine abspielbare Session (ein committeter, vollstaendiger Collection-Tag).
/// <c>Id</c> adressiert die Session in /meta und /ticks: gleich der Job-ID, ausser bei ungeloesten Konflikten
/// (dann <c>JobId@Collection</c>, eine Zeile je Fassung). <c>Selection</c>: single | resolved | conflict.</summary>
public sealed record TickReplaySession(
    string JobId, string Day, string Label, string Timezone, string WindowStartUtc, string WindowEndUtc,
    long Rows, decimal TotalVolume, string Collection = "", string Id = "", long? RecorderDrops = null,
    string Selection = "single", string? SelectionNote = null, IReadOnlyList<TickReplayCandidate>? Candidates = null,
    string Session = "NewYork", string SessionLabel = "New York");

/// <summary>Gepruefte Sicht auf einen aufgeloesten Collection-Job (fuer den Sessionplan „London + New York“).</summary>
public sealed record TickReplayJobInfo(
    string Id, string JobId, string Session, DateOnly Day, DateTimeOffset StartUtc, DateTimeOffset EndUtc, long Rows, decimal TotalVolume,
    string Collection, string CollectionsRoot, string RecordingId, string RunId, long? RecorderDrops, bool? SummaryClean, long? ManifestTickDrops,
    string Selection, string? SelectionNote, bool Usable);

/// <summary>Sessions der konfigurierten Collection(s) (oder Grund, warum keine verfuegbar sind). Im Ordner-Modus ist
/// <c>CollectionDir</c> der Collections-Ordner und <c>ManifestPath</c> leer (je Session steht die Collection im Eintrag).
/// <c>Conflicts</c>: Handelstage mit mehreren drop-freien, verschiedenen Aufnahmen – nichts wird still gewaehlt.</summary>
public sealed record TickReplaySessionList(
    bool Available, string? Reason, string CollectionDir, string ManifestPath, IReadOnlyList<TickReplaySession> Sessions,
    IReadOnlyList<string>? Conflicts = null);

/// <summary>Metadaten einer vorbereiteten Session. Zeiten der Trades sind 100-ns-Einheiten relativ zu AnchorUtc
/// (AnchorUtc liegt auf einer 5-Minuten-Grenze, daher sind Kerzenintervalle [Start, Ende) exakt ganzzahlig).</summary>
public sealed record TickReplaySessionMeta(
    string JobId, string Day, string Label, string Timezone,
    string WindowStartUtc, string WindowEndUtc, string AnchorUtc, long AnchorUnixMs,
    long WindowStartUnits, long WindowEndUnits, long UnitsPerMs, long BucketUnits,
    long Count, decimal TotalVolume, decimal BuyVolume, decimal SellVolume,
    int RowBytes, string CsvPath, string CsvSha256, string ManifestPath, string Cache, double PrepareSeconds,
    string TimePrecision, string Id = "", string Collection = "", string SliceSha256 = "", string SliceBytes = "",
    long? RecorderDrops = null, string Selection = "single", string? SelectionNote = null,
    string Session = "NewYork", string Contract = "", string RecordingId = "");

/// <summary>
/// Read-only Tick-Replay-Quelle fuer das React-Research-Dashboard. Je Session wird EINMALIG ein kompakter Binaer-Cache
/// ausserhalb der Originaldaten angelegt. Aufbau ueber <see cref="AtasCollectionImporter.ImportJobSlice"/>: nur die im
/// Manifest festgeschriebenen Bytes des Tages werden gelesen und fail-closed geprueft (keine Volldurchlaeufe der
/// Jahres-CSV). Der Cache-Kopf ist der persistente Tagesindex: Abschnitt, SHA-256 des Abschnitts und Dateistand. Waechst
/// die CSV (neue Tage angehaengt), wird nur der Abschnitt erneut gehasht – stimmt er, bleibt der Cache gueltig.
/// Trades werden portionsweise (Seek, O(1)) ausgeliefert. Originaldateien werden nur gelesen. Keine Order-/Execution-Referenz.
/// </summary>
public sealed class TickReplayService
{
    /// <summary>5 Minuten in 100-ns-Einheiten.</summary>
    public const long BucketUnits = 3_000_000_000L;
    public const long UnitsPerMs = TimeSpan.TicksPerMillisecond;
    /// <summary>Zeilenformat im Cache und im Abschnitt: Float64 Zeit, Float64 Preis, Int32 Volumen, Int8 Seite.</summary>
    public const int RowBytes = 21;
    public const int MaxChunkRows = 250_000;
    private const string Magic = "TRPL-3";
    private const string ManifestName = "collection_manifest.json";
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>Standard ohne Sessionplan: wie bisher nur New York 09:30–12:00 America/New_York.</summary>
    public static readonly IReadOnlyList<SessionDefinition> NewYorkOnly = new[]
    {
        new SessionDefinition("NewYork", "New York", "America/New_York", new TimeOnly(9, 30), new TimeOnly(12, 0))
    };

    private readonly string _collectionDir;
    private readonly IReadOnlyList<string> _roots;
    private readonly IReadOnlyList<SessionDefinition> _sessions;
    private readonly bool _isRoot;
    private readonly string _cacheDir;
    private readonly ConcurrentDictionary<string, Lazy<Task<TickReplaySessionMeta>>> _builds = new();
    private readonly ConcurrentDictionary<string, string> _sliceHashMemo = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _manifestLock = new();
    private readonly Dictionary<string, (long Length, long WriteTicks, List<Job> Jobs)> _manifestCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Genau eine Collection (Ordner mit collection_manifest.json + trades.csv).</summary>
    public TickReplayService(string collectionDir, string cacheDir, IReadOnlyList<SessionDefinition>? sessions = null)
        : this(new[] { collectionDir }, cacheDir, isRoot: false, sessions) { }

    private TickReplayService(IReadOnlyList<string> dirs, string cacheDir, bool isRoot, IReadOnlyList<SessionDefinition>? sessions)
    {
        _collectionDir = dirs[0];
        _roots = dirs;
        _cacheDir = cacheDir;
        _isRoot = isRoot;
        _sessions = sessions ?? NewYorkOnly;
    }

    /// <summary>Alle Collections direkt unter <paramref name="collectionsRoot"/> (z. B. "ATAS_Data Collection\Collections").
    /// Ordner ohne Manifest oder mit unlesbarem Manifest werden uebersprungen. Kommt derselbe Handelstag in mehreren
    /// Collections vor, entscheidet der Drop-Status der Aufnahme (siehe <see cref="Resolve"/>); bleibt mehr als eine
    /// drop-freie, verschiedene Aufnahme uebrig, wird der Tag als Konflikt mit allen Fassungen gemeldet.</summary>
    public static TickReplayService ForCollectionsRoot(string collectionsRoot, string cacheDir) => new(new[] { collectionsRoot }, cacheDir, isRoot: true, null);

    /// <summary>Mehrere Collections-Ordner (z. B. London und New York in getrennten Datenordnern), Sessions laut Plan.
    /// Die Ordner werden nur gelesen; keine CSV wird kopiert oder zusammengefuehrt.</summary>
    public static TickReplayService ForCollectionsRoots(IReadOnlyList<string> collectionsRoots, string cacheDir, IReadOnlyList<SessionDefinition> sessions)
    {
        if (collectionsRoots.Count == 0) throw new ArgumentException("Mindestens ein Collections-Ordner.", nameof(collectionsRoots));
        return new(collectionsRoots, cacheDir, isRoot: true, sessions);
    }

    /// <summary>Konfigurierte Collections-Ordner (Ordner-Modus) bzw. die eine Collection.</summary>
    public IReadOnlyList<string> Roots => _roots;
    public IReadOnlyList<SessionDefinition> SessionDefinitions => _sessions;
    /// <summary>Manifest im Einzel-Modus; im Ordner-Modus leer.</summary>
    public string ManifestPath => _isRoot ? "" : Path.Combine(_collectionDir, ManifestName);
    private static string ManifestOf(Job job) => Path.Combine(job.CollectionDir, ManifestName);
    private static string CollectionName(string dir) => Path.GetFileName(dir.TrimEnd('\\', '/'));
    private string CacheFile(Job job) =>
        Path.Combine(_cacheDir, SafeName(CollectionName(job.CollectionDir)), SafeName(job.JobId) + ".bin");

    /// <summary>Alle vom Importer unterstuetzten Sessions (NewYork 09:30–12:00 America/New_York, committed, complete).</summary>
    public TickReplaySessionList ListSessions()
    {
        if (_isRoot && !_roots.Any(Directory.Exists))
            return new(false, "Collections-Ordner nicht gefunden: " + string.Join(" | ", _roots), _collectionDir, ManifestPath, Array.Empty<TickReplaySession>());
        if (!_isRoot && !File.Exists(ManifestPath))
            return new(false, "Collection-Manifest nicht gefunden: " + ManifestPath, _collectionDir, ManifestPath, Array.Empty<TickReplaySession>());
        var list = new List<TickReplaySession>();
        foreach (var job in ReadJobs()) list.Add(new(job.JobId, job.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), job.Label,
            job.Def.TimeZoneId, job.StartIso, job.EndIso, job.Rows, job.TotalVolume, CollectionName(job.CollectionDir), job.Id,
            job.RecorderDrops, job.Selection, job.SelectionNote, job.Candidates, job.Def.Key, job.Def.Label));
        list.Sort((a, b) => a.WindowStartUtc != b.WindowStartUtc ? string.CompareOrdinal(a.WindowStartUtc, b.WindowStartUtc) : string.CompareOrdinal(a.Collection, b.Collection));
        var conflicts = list.Where(s => s.Selection == "conflict").Select(s => s.Day).Distinct().ToList();
        string what = string.Join(" / ", _sessions.Select(d => $"{d.Key} {d.Start:HH\\:mm}–{d.End:HH\\:mm}"));
        return new(list.Count > 0, list.Count > 0 ? null
                : _isRoot ? $"Keine vollstaendigen Sessions ({what}) in den Collections unter {string.Join(" | ", _roots)}."
                : $"Keine vollstaendigen Sessions ({what}) im Manifest.",
            _collectionDir, ManifestPath, list, conflicts);
    }

    /// <summary>Aufgeloeste Jobs (nach Quellenwahl) mit Drop-/Abschluss-Status – Grundlage des Sessionplans.</summary>
    public IReadOnlyList<TickReplayJobInfo> ResolvedJobs()
    {
        var res = new List<TickReplayJobInfo>();
        foreach (var j in ReadJobs())
            res.Add(new TickReplayJobInfo(j.Id, j.JobId, j.Def.Key, j.Day, FromUnixNs(j.StartNs), FromUnixNs(j.EndNs), j.Rows, j.TotalVolume,
                CollectionName(j.CollectionDir), Path.GetDirectoryName(j.CollectionDir.TrimEnd('\\', '/')) ?? "", j.RecordingId, j.RunId,
                j.RecorderDrops, j.SummaryClean, j.ManifestTickDrops, j.Selection, j.SelectionNote, j.Usable));
        return res;
    }

    /// <summary>Alle Trades einer Session als MarketTicks aus dem geprueften Cache (bei Bedarf einmalig aufgebaut).
    /// Zeiten auf 100 ns (Engine-Aufloesung), Preise exakt aus dem Cache, Aggressor gemessen.</summary>
    public async Task<(TickReplaySessionMeta Meta, List<MarketTick> Ticks)> LoadMarketTicksAsync(string id, CancellationToken ct)
    {
        var meta = await PrepareAsync(id, ct);
        var job = FindJob(id);
        var ticks = new List<MarketTick>((int)meta.Count);
        long anchor = DateTime.UnixEpoch.Ticks + meta.AnchorUnixMs * TimeSpan.TicksPerMillisecond;
        using var fs = new FileStream(CacheFile(job), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        int headerLen = ReadHeaderLength(fs);
        fs.Position = 4 + headerLen;
        using var br = new BinaryReader(fs);
        for (long i = 0; i < meta.Count; i++)
        {
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            long off = (long)br.ReadDouble();
            decimal price = (decimal)br.ReadDouble();
            int vol = br.ReadInt32();
            sbyte side = br.ReadSByte();
            ticks.Add(new MarketTick
            {
                Symbol = "MNQ", Timestamp = new DateTimeOffset(anchor + off, TimeSpan.Zero), Price = price, Volume = vol,
                Aggressor = side > 0 ? AggressorSide.Buy : side < 0 ? AggressorSide.Sell : throw new InvalidDataException("InsufficientData: Aggressor fehlt")
            });
        }
        return (meta, ticks);
    }

    /// <summary>Stellt sicher, dass der Cache der Session gueltig ist (sonst einmaliger Aufbau ueber den Importer).</summary>
    public async Task<TickReplaySessionMeta> PrepareAsync(string id, CancellationToken ct)
    {
        var job = FindJob(id);
        var cached = TryReadValidMeta(job);
        if (cached is not null) return cached;
        // Aufbau laeuft unabhaengig von der Anfrage weiter (ein Abbruch durch Datumswechsel verwirft das Ergebnis
        // nur auf Client-Seite); parallele Anfragen derselben Session teilen sich einen Aufbau.
        var lazy = _builds.GetOrAdd(job.Id, _ => new Lazy<Task<TickReplaySessionMeta>>(() => Task.Run(() => Build(job))));
        try { return await lazy.Value.WaitAsync(ct); }
        finally { if (lazy.IsValueCreated && lazy.Value.IsCompleted) _builds.TryRemove(new KeyValuePair<string, Lazy<Task<TickReplaySessionMeta>>>(job.Id, lazy)); }
    }

    /// <summary>Liest Trades [offset, offset+count) als Spaltenblock [t f64 x n][Preis f64 x n][Vol i32 x n][Seite i8 x n].</summary>
    public byte[] ReadTicks(string id, long offset, int count)
    {
        var job = FindJob(id);
        var meta = TryReadValidMeta(job) ?? throw new InvalidOperationException("Session ist nicht vorbereitet (zuerst /meta abrufen).");
        if (offset < 0 || offset > meta.Count) throw new ArgumentOutOfRangeException(nameof(offset));
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
        int n = (int)Math.Min(Math.Min(count, MaxChunkRows), meta.Count - offset);
        var rows = new byte[n * RowBytes];
        using (var fs = new FileStream(CacheFile(job), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            int headerLen = ReadHeaderLength(fs);
            fs.Position = 4 + headerLen + offset * RowBytes;
            fs.ReadExactly(rows);
        }
        var cols = new byte[n * RowBytes];
        for (int i = 0; i < n; i++)
        {
            int src = i * RowBytes;
            Buffer.BlockCopy(rows, src, cols, i * 8, 8);
            Buffer.BlockCopy(rows, src + 8, cols, 8 * n + i * 8, 8);
            Buffer.BlockCopy(rows, src + 16, cols, 16 * n + i * 4, 4);
            cols[20 * n + i] = rows[src + 20];
        }
        return cols;
    }

    // ------------------------------------------------------------------------------------------------------------

    private sealed record Job(string Id, string JobId, DateOnly Day, string Label, string StartIso, string EndIso,
        long StartNs, long EndNs, long Rows, decimal TotalVolume, string CollectionDir,
        string RecordingId, string RunId, long SliceStart, long SliceEnd, long? RecorderDrops, bool? SummaryClean,
        SessionDefinition Def, long? ManifestTickDrops,
        string Selection = "single", string? SelectionNote = null, IReadOnlyList<TickReplayCandidate>? Candidates = null)
    {
        public bool Usable => RecorderDrops == 0 && SummaryClean == true && ManifestTickDrops is null or 0;
        public string Key => $"{RecordingId}|{RunId}|{Rows}|{TotalVolume.ToString(CultureInfo.InvariantCulture)}|{SliceStart}|{SliceEnd}";
    }

    private Job FindJob(string id)
    {
        var jobs = ReadJobs();
        var job = jobs.FirstOrDefault(j => j.Id == id);
        if (job is not null) return job;
        if (jobs.Any(j => j.JobId == id && j.Selection == "conflict"))
            throw new KeyNotFoundException("Konflikt: Dieser Handelstag liegt in mehreren Collections als verschiedene drop-freie Aufnahmen vor – bitte eine Fassung waehlen (" +
                string.Join(", ", jobs.Where(j => j.JobId == id).Select(j => j.Id)) + ").");
        throw new KeyNotFoundException("Unbekannte oder nicht unterstuetzte Session: " + id);
    }

    private List<Job> ReadJobs()
    {
        var raw = new List<Job>();
        if (!_isRoot)
        {
            if (!File.Exists(ManifestPath)) throw new FileNotFoundException("Collection-Manifest nicht gefunden.", ManifestPath);
            raw.AddRange(ReadManifestCached(_collectionDir));
        }
        else
        {
            foreach (var dir in _roots.Where(Directory.Exists)
                         .SelectMany(r => Directory.EnumerateDirectories(r).OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal)))
            {
                if (!File.Exists(Path.Combine(dir, ManifestName))) continue;
                try { raw.AddRange(ReadManifestCached(dir)); }
                catch (Exception e) when (e is JsonException or IOException or InvalidOperationException or KeyNotFoundException
                                           or FormatException or InvalidDataException or UnauthorizedAccessException)
                {
                    // unlesbares/fremdes Manifest -> diese Collection nicht anbieten (fail-closed, kein Raten)
                }
            }
        }
        return Resolve(raw);
    }

    /// <summary>
    /// Quellenwahl je Handelstag (Job-ID). Eine Fassung: unveraendert. Mehrere: nutzbar ist nur eine Fassung mit
    /// 0 Recorder-Drops UND sauberem Writer-Abschluss (summary clean). Bleibt genau eine (bzw. nur eine Aufnahme-ID)
    /// uebrig, wird sie mit Begruendung gewaehlt. Sonst Konflikt: jede Fassung wird einzeln angeboten (Id JobId@Collection),
    /// eine Fassung OHNE Angabe der Collection gibt es dann nicht.
    /// </summary>
    private static List<Job> Resolve(List<Job> raw)
    {
        var outList = new List<Job>();
        foreach (var g in raw.GroupBy(j => j.JobId, StringComparer.Ordinal))
        {
            var all = g.ToList(); // bereits nach Ordnername sortiert
            if (all.Count == 1) { outList.Add(all[0]); continue; }
            var cands = all.Select(j => new TickReplayCandidate(j.JobId + "@" + CollectionName(j.CollectionDir), CollectionName(j.CollectionDir),
                j.RecordingId, j.Rows, j.TotalVolume, j.RecorderDrops, j.SummaryClean, j.Usable)).ToList();
            var usable = all.Where(j => j.Usable).ToList();
            string Describe(Job j) => $"{CollectionName(j.CollectionDir)} ({(j.RecorderDrops is { } d ? $"{d} Recorder-Drops" : "Drop-Status unbekannt")}{(j.SummaryClean == false ? ", Writer nicht sauber" : "")})";
            if (usable.Count == 1 || (usable.Count > 1 && usable.Select(j => j.RecordingId).Distinct(StringComparer.Ordinal).Count() == 1))
            {
                var pick = usable[0];
                var rejected = all.Where(j => !j.Usable).ToList();
                string note = usable.Count == 1
                    ? $"Gewaehlt: {CollectionName(pick.CollectionDir)} (0 Recorder-Drops). Verworfen: {string.Join("; ", rejected.Select(Describe))}."
                    : $"Gewaehlt: {CollectionName(pick.CollectionDir)} – dieselbe Aufnahme {pick.RecordingId} liegt in {usable.Count} Collections.";
                outList.Add(pick with { Selection = "resolved", SelectionNote = note, Candidates = cands });
                continue;
            }
            string why = usable.Count == 0
                ? "Konflikt: keine Fassung ist drop-frei mit sauberem Abschluss – bitte bewusst waehlen."
                : $"Konflikt: {usable.Count} drop-freie, verschiedene Aufnahmen ({string.Join(", ", usable.Select(j => j.RecordingId))}) – nicht still entschieden, bitte Fassung waehlen.";
            foreach (var j in usable.Count == 0 ? all : usable)
                outList.Add(j with { Id = j.JobId + "@" + CollectionName(j.CollectionDir), Selection = "conflict", SelectionNote = why, Candidates = cands });
        }
        return outList;
    }

    /// <summary>Liest ein Manifest; das Ergebnis wird bis zur naechsten Aenderung (Laenge/Schreibzeit) wiederverwendet.</summary>
    private List<Job> ReadManifestCached(string collectionDir)
    {
        string path = Path.Combine(collectionDir, ManifestName);
        var info = new FileInfo(path);
        lock (_manifestLock)
            if (_manifestCache.TryGetValue(path, out var hit) && hit.Length == info.Length && hit.WriteTicks == info.LastWriteTimeUtc.Ticks)
                return hit.Jobs;
        var jobs = ReadManifest(collectionDir, _sessions);
        lock (_manifestLock) _manifestCache[path] = (info.Length, info.LastWriteTimeUtc.Ticks, jobs);
        return jobs;
    }

    private static List<Job> ReadManifest(string collectionDir, IReadOnlyList<SessionDefinition> sessions)
    {
        using var fs = new FileStream(Path.Combine(collectionDir, ManifestName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var doc = JsonDocument.Parse(fs);
        var root = doc.RootElement;
        var jobs = new List<Job>();
        var def = sessions.FirstOrDefault(d => d.Key == root.GetProperty("session").GetString());
        if (root.GetProperty("schemaVersion").GetString() != "atas-collection-trades-1" || def is null)
            return jobs;
        var tz = def.TimeZone;
        foreach (var j in root.GetProperty("jobs").EnumerateObject())
        {
            var v = j.Value;
            if (v.GetProperty("status").GetString() != "committed" || v.GetProperty("coverageStatus").GetString() != "complete") continue;
            var w = v.GetProperty("window");
            if (w.GetProperty("interval").GetString() != "[start, end)") continue;
            string startIso = w.GetProperty("start").GetString()!, endIso = w.GetProperty("end").GetString()!;
            long startNs = w.TryGetProperty("startNs", out var sn) ? sn.GetInt64() : ParseNs(startIso);
            long endNs = w.TryGetProperty("endNs", out var en) ? en.GetInt64() : ParseNs(endIso);
            var startLocal = TimeZoneInfo.ConvertTimeFromUtc(FromUnixNs(startNs).UtcDateTime, tz);
            var endLocal = TimeZoneInfo.ConvertTimeFromUtc(FromUnixNs(endNs).UtcDateTime, tz);
            // Nur Fenster laut Sessionplan (z. B. 09:30–12:00 America/New_York, 08:00–11:00 Europe/London) desselben lokalen Tages.
            if (TimeOnly.FromTimeSpan(startLocal.TimeOfDay) != def.Start || TimeOnly.FromTimeSpan(endLocal.TimeOfDay) != def.End || startLocal.Date != endLocal.Date) continue;
            var day = DateOnly.FromDateTime(startLocal);
            var trades = v.GetProperty("trades");
            string label = day.ToString("ddd dd.MM.yyyy", German) + $" · {def.Label} {def.Start:HH\\:mm}–{def.End:HH\\:mm} (" + TzAbbrev(def, startLocal) + ")";
            long? manifestDrops = v.TryGetProperty("reconciliation", out var rc0) && rc0.TryGetProperty("tick_drops", out var td)
                && td.TryGetProperty("total", out var tdt) && tdt.ValueKind == JsonValueKind.Number ? tdt.GetInt64() : null;
            long sliceStart = v.TryGetProperty("byteOffsetBefore", out var ob) && ob.ValueKind == JsonValueKind.Number ? ob.GetInt64() : -1;
            long sliceEnd = v.TryGetProperty("byteOffsetAfter", out var oa) && oa.ValueKind == JsonValueKind.Number ? oa.GetInt64() : -1;
            if (sliceStart < 0 || sliceEnd <= sliceStart) { sliceStart = -1; sliceEnd = -1; }
            bool? clean = v.TryGetProperty("reconciliation", out var rec) && rec.TryGetProperty("summary_clean", out var sc)
                && sc.ValueKind is JsonValueKind.True or JsonValueKind.False ? sc.GetBoolean() : null;
            string recordingDir = v.TryGetProperty("recordingDir", out var rd) ? rd.GetString() ?? "" : "";
            jobs.Add(new Job(j.Name, j.Name, day, label, startIso, endIso, startNs, endNs,
                trades.GetProperty("rows").GetInt64(), trades.GetProperty("totalVolume").GetDecimal(), collectionDir,
                v.TryGetProperty("recordingId", out var ri) ? ri.GetString() ?? "" : "", v.TryGetProperty("runId", out var ru) ? ru.GetString() ?? "" : "",
                sliceStart, sliceEnd, ReadRecorderDrops(recordingDir), clean, def, manifestDrops));
        }
        return jobs;
    }

    private static string TzAbbrev(SessionDefinition def, DateTime local)
    {
        bool dst = def.TimeZone.IsDaylightSavingTime(local);
        return def.TimeZoneId switch
        {
            "America/New_York" => dst ? "EDT" : "EST",
            "Europe/London" => dst ? "BST" : "GMT",
            _ => dst ? "Sommerzeit" : "Normalzeit"
        };
    }

    /// <summary>Recorder-Drops laut summary.json der Rohaufnahme (nie komprimiert); null = nicht feststellbar.</summary>
    private static long? ReadRecorderDrops(string recordingDir)
    {
        if (string.IsNullOrEmpty(recordingDir)) return null;
        try
        {
            string p = Path.Combine(recordingDir, "summary.json");
            if (!File.Exists(p)) return null;
            using var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            return doc.RootElement.TryGetProperty("totalDrops", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt64() : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    private TickReplaySessionMeta Build(Job job)
    {
        var sw = Stopwatch.StartNew();
        string manifestPath = ManifestOf(job);
        IReadOnlyList<TradingBot.Domain.Models.MarketTick> ticks;
        string csvPath, csvSha = "", sliceSha = "", contract = "";
        if (job.SliceStart >= 0)
        {
            var slice = new AtasCollectionImporter().ImportJobSlice(manifestPath, job.JobId);
            if (slice.SliceStart != job.SliceStart || slice.SliceEnd != job.SliceEnd || slice.RecordingId != job.RecordingId)
                throw new InvalidDataException("Manifest hat sich waehrend des Imports geaendert.");
            ticks = slice.Data.Ticks; csvPath = slice.CsvPath; sliceSha = slice.SliceSha256;
            contract = slice.Evidence.GetValueOrDefault("contract") ?? "";
        }
        else
        {
            // Aeltere Manifeste ohne Byte-Abschnitte: bisheriger Volldurchlauf ueber den Importer.
            var result = new AtasCollectionImporter().ImportDay(manifestPath, job.Day);
            if (result.Evidence.GetValueOrDefault("collection.jobId") != job.JobId)
                throw new InvalidDataException("Importer lieferte eine andere Session als angefordert.");
            ticks = result.Data.Ticks; csvPath = result.CsvPath; csvSha = result.CsvSha256;
        }
        if (ticks.Count != job.Rows) throw new InvalidDataException("Zeilenzahl weicht vom Manifest ab.");

        long windowStartTicks = FromUnixNs(job.StartNs).UtcTicks, windowEndTicks = FromUnixNs(job.EndNs).UtcTicks;
        long anchorTicks = windowStartTicks - (windowStartTicks % BucketUnits); // 5-Minuten-Grenze (UTC-Raster, gilt fuer New York und London)
        decimal buy = 0, sell = 0;
        string cache = CacheFile(job);
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        string tmp = cache + ".tmp-" + Guid.NewGuid().ToString("N");
        var csvInfo = new FileInfo(csvPath);
        var meta = new MetaHeader(job.JobId, Path.GetFullPath(csvPath), job.SliceStart, job.SliceEnd, sliceSha, csvInfo.Length, csvInfo.LastWriteTimeUtc.Ticks,
            job.Key, csvSha, anchorTicks, windowStartTicks, windowEndTicks, ticks.Count, 0, 0, 0, contract);
        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var bw = new BinaryWriter(fs))
        {
            // Volumen-Summen stehen erst nach dem Durchlauf fest -> Header am Ende mit fester Laenge neu schreiben.
            byte[] placeholder = SerializeHeader(meta);
            bw.Write(placeholder.Length);
            bw.Write(placeholder);
            long prev = long.MinValue;
            foreach (var t in ticks)
            {
                long off = t.Timestamp.UtcTicks - anchorTicks;
                if (off < 0 || off >= (1L << 53)) throw new InvalidDataException("Zeit ausserhalb des darstellbaren Bereichs.");
                if (off < prev) throw new InvalidDataException("Nicht chronologische Trades.");
                prev = off;
                if (t.Volume != decimal.Truncate(t.Volume) || t.Volume <= 0 || t.Volume > int.MaxValue)
                    throw new InvalidDataException("Volumen ist keine positive Ganzzahl: " + t.Volume);
                sbyte side = t.Aggressor switch { AggressorSide.Buy => (sbyte)1, AggressorSide.Sell => (sbyte)-1, _ => throw new InvalidDataException("InsufficientData: Aggressor fehlt") };
                if (side > 0) buy += t.Volume; else sell += t.Volume;
                bw.Write((double)off);
                bw.Write((double)t.Price);
                bw.Write((int)t.Volume);
                bw.Write(side);
            }
            meta = meta with { TotalVolume = buy + sell, BuyVolume = buy, SellVolume = sell };
            byte[] header = SerializeHeader(meta);
            if (header.Length != placeholder.Length) throw new InvalidOperationException("Header-Laenge geaendert.");
            fs.Position = 4;
            bw.Write(header);
        }
        File.Move(tmp, cache, overwrite: true);
        sw.Stop();
        return ToMeta(job, meta, "aufgebaut", sw.Elapsed.TotalSeconds);
    }

    /// <summary>Gueltiger Cache? Gleicher Job (Aufnahme, Lauf, Zeilen, Volumen, Abschnitt). Unveraenderte CSV -> sofort
    /// gueltig. Veraenderte CSV (z. B. neue Tage angehaengt) -> nur den Abschnitt neu hashen; stimmt er, wird der neue
    /// Dateistand im Cache-Kopf vermerkt (persistent, beim naechsten Start kein erneutes Hashen).</summary>
    private TickReplaySessionMeta? TryReadValidMeta(Job job)
    {
        string cache = CacheFile(job);
        if (!File.Exists(cache)) return null;
        try
        {
            MetaHeader h;
            using (var fs = new FileStream(cache, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int len = ReadHeaderLength(fs);
                var buf = new byte[len];
                fs.ReadExactly(buf);
                h = DeserializeHeader(buf);
                if (fs.Length != 4 + len + h.Count * RowBytes) return null;
            }
            if (h.JobId != job.JobId || h.Count != job.Rows || h.TotalVolume != job.TotalVolume || h.JobKey != job.Key
                || h.SliceStart != job.SliceStart || h.SliceEnd != job.SliceEnd) return null;
            var csv = new FileInfo(h.CsvPath);
            if (!csv.Exists) return null;
            if (csv.Length == h.CsvLength && csv.LastWriteTimeUtc.Ticks == h.CsvWriteTicks) return ToMeta(job, h, "wiederverwendet", 0);
            if (job.SliceStart < 0 || csv.Length < h.SliceEnd) return null; // ohne Abschnitt: jede Dateiaenderung invalidiert
            string memoKey = $"{h.CsvPath}|{csv.Length}|{csv.LastWriteTimeUtc.Ticks}|{h.SliceStart}|{h.SliceEnd}";
            string sha = _sliceHashMemo.GetOrAdd(memoKey, _ => AtasCollectionImporter.HashSlice(h.CsvPath, h.SliceStart, h.SliceEnd));
            if (!string.Equals(sha, h.SliceSha256, StringComparison.OrdinalIgnoreCase)) return null;
            var updated = h with { CsvLength = csv.Length, CsvWriteTicks = csv.LastWriteTimeUtc.Ticks };
            TryPersistHeader(cache, h, updated);
            return ToMeta(job, updated, "wiederverwendet (Abschnitt nach Dateiaenderung neu geprueft)", 0);
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException or IOException or FormatException or UnauthorizedAccessException)
        {
            return null; // defekter/fremder Cache oder geaenderte Quelle -> neu aufbauen
        }
    }

    private static void TryPersistHeader(string cache, MetaHeader old, MetaHeader updated)
    {
        try
        {
            byte[] a = SerializeHeader(old), b = SerializeHeader(updated);
            if (a.Length != b.Length) return;
            using var fs = new FileStream(cache, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (ReadHeaderLength(fs) != b.Length) return;
            fs.Position = 4;
            fs.Write(b);
        }
        catch (IOException) { /* nur Optimierung: beim naechsten Mal erneut pruefen */ }
        catch (UnauthorizedAccessException) { }
    }

    private TickReplaySessionMeta ToMeta(Job job, MetaHeader h, string cache, double seconds) => new(
        job.JobId, job.Day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), job.Label, job.Def.TimeZoneId,
        job.StartIso, job.EndIso, new DateTime(h.AnchorTicks, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
        (h.AnchorTicks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerMillisecond,
        h.WindowStartTicks - h.AnchorTicks, h.WindowEndTicks - h.AnchorTicks, UnitsPerMs, BucketUnits,
        h.Count, h.TotalVolume, h.BuyVolume, h.SellVolume, RowBytes, h.CsvPath, h.CsvSha256, Path.GetFullPath(ManifestOf(job)),
        cache, Math.Round(seconds, 2),
        "Quelle in Nanosekunden; Replay-Zeit auf 100 ns abgerundet (kann keine 5-Min-Grenze ueberschreiten); Originalreihenfolge erhalten.",
        job.Id, CollectionName(job.CollectionDir), h.SliceSha256, h.SliceStart >= 0 ? $"[{h.SliceStart}, {h.SliceEnd})" : "",
        job.RecorderDrops, job.Selection, job.SelectionNote, job.Def.Key, h.Contract, job.RecordingId);

    // --- Cache-Header ---------------------------------------------------------------------------------------------

    private sealed record MetaHeader(string JobId, string CsvPath, long SliceStart, long SliceEnd, string SliceSha256,
        long CsvLength, long CsvWriteTicks, string JobKey, string CsvSha256, long AnchorTicks, long WindowStartTicks, long WindowEndTicks,
        long Count, decimal TotalVolume, decimal BuyVolume, decimal SellVolume, string Contract = "");

    private static byte[] SerializeHeader(MetaHeader h)
    {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write(Magic); bw.Write(h.JobId); bw.Write(h.CsvPath); bw.Write(h.SliceStart); bw.Write(h.SliceEnd); bw.Write(h.SliceSha256);
            bw.Write(h.CsvLength); bw.Write(h.CsvWriteTicks); bw.Write(h.JobKey); bw.Write(h.CsvSha256); bw.Write(h.AnchorTicks);
            bw.Write(h.WindowStartTicks); bw.Write(h.WindowEndTicks); bw.Write(h.Count); bw.Write(h.TotalVolume); bw.Write(h.BuyVolume); bw.Write(h.SellVolume);
            bw.Write(h.Contract);
        }
        return ms.ToArray();
    }

    private static MetaHeader DeserializeHeader(byte[] buf)
    {
        using var br = new BinaryReader(new MemoryStream(buf));
        if (br.ReadString() != Magic) throw new InvalidDataException("Unbekanntes Cache-Format.");
        return new MetaHeader(br.ReadString(), br.ReadString(), br.ReadInt64(), br.ReadInt64(), br.ReadString(),
            br.ReadInt64(), br.ReadInt64(), br.ReadString(), br.ReadString(), br.ReadInt64(),
            br.ReadInt64(), br.ReadInt64(), br.ReadInt64(), br.ReadDecimal(), br.ReadDecimal(), br.ReadDecimal(), br.ReadString());
    }

    private static int ReadHeaderLength(Stream fs)
    {
        Span<byte> b = stackalloc byte[4];
        fs.Position = 0;
        fs.ReadExactly(b);
        int len = BitConverter.ToInt32(b);
        if (len <= 0 || len > 1 << 20) throw new InvalidDataException("Ungueltige Header-Laenge.");
        return len;
    }

    private static DateTimeOffset FromUnixNs(long ns) => new(DateTime.UnixEpoch.Ticks + ns / 100, TimeSpan.Zero);

    private static long ParseNs(string iso)
    {
        if (iso.Length != 30 || iso[19] != '.' || iso[29] != 'Z') throw new InvalidDataException("Erwartet UTC-Nanosekunden: " + iso);
        var whole = DateTime.ParseExact(iso[..19], "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        return (whole.Ticks - DateTime.UnixEpoch.Ticks) * 100 + long.Parse(iso.AsSpan(20, 9), CultureInfo.InvariantCulture);
    }

    private static string SafeName(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
}
