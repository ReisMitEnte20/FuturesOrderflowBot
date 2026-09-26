using System.Collections.Concurrent;

namespace TradingBot.DevDashboard.Services.Quant;

public enum QuantJobStatus { Running = 0, Completed = 1, Failed = 2, Cancelled = 3 }

/// <summary>Zustand einer laufenden oder abgeschlossenen Berechnung (für Fortschritt und Abbruch).</summary>
public sealed record QuantJobState
{
    public required string Id { get; init; }
    public required string Kind { get; init; }
    public QuantJobStatus Status { get; init; }
    /// <summary>Fortschritt 0..1. Bei Verfahren ohne messbaren Fortschritt bleibt er bei 0 bis zum Abschluss.</summary>
    public double Progress { get; init; }
    public DateTimeOffset StartedUtc { get; init; }
    public DateTimeOffset? FinishedUtc { get; init; }
    public long ElapsedMs { get; init; }
    public string? Error { get; init; }
    public object? Result { get; init; }
}

/// <summary>
/// Führt Quant-Berechnungen im Hintergrund aus und macht sie abbrechbar.
///
/// Zweck: Walk-forward, Monte Carlo und CSCV können Minuten dauern. Ein blockierender
/// HTTP-Aufruf wäre weder abbrechbar noch beobachtbar. Jeder Job hat eine harte Laufzeitgrenze;
/// nach Ablauf wird abgebrochen statt unbegrenzt weiterzurechnen.
/// </summary>
public sealed class QuantJobManager : IDisposable
{
    private sealed class Entry
    {
        public required QuantJobState State;
        public required CancellationTokenSource Cts;
    }

    private readonly ConcurrentDictionary<string, Entry> _jobs = new();

    /// <summary>Harte Obergrenze der Rechenzeit je Job.</summary>
    public TimeSpan DefaultTimeLimit { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Abgeschlossene Jobs werden nach dieser Zeit aufgeräumt.</summary>
    public TimeSpan RetentionTime { get; init; } = TimeSpan.FromHours(2);

    public string Start(string kind, Func<IProgress<double>, CancellationToken, Task<object>> work, TimeSpan? timeLimit = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        Cleanup();

        string id = Guid.NewGuid().ToString("N")[..12];
        var cts = new CancellationTokenSource(timeLimit ?? DefaultTimeLimit);
        var started = DateTimeOffset.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var entry = new Entry
        {
            State = new QuantJobState { Id = id, Kind = kind, Status = QuantJobStatus.Running, StartedUtc = started },
            Cts = cts
        };
        _jobs[id] = entry;

        var progress = new Progress<double>(p =>
        {
            if (_jobs.TryGetValue(id, out var e) && e.State.Status == QuantJobStatus.Running)
                e.State = e.State with { Progress = Math.Clamp(p, 0, 1), ElapsedMs = sw.ElapsedMilliseconds };
        });

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await work(progress, cts.Token);
                Set(id, s => s with
                {
                    Status = QuantJobStatus.Completed, Progress = 1, Result = result,
                    FinishedUtc = DateTimeOffset.UtcNow, ElapsedMs = sw.ElapsedMilliseconds
                });
            }
            catch (OperationCanceledException)
            {
                Set(id, s => s with
                {
                    Status = QuantJobStatus.Cancelled,
                    Error = cts.IsCancellationRequested && sw.Elapsed >= (timeLimit ?? DefaultTimeLimit)
                        ? $"Laufzeitgrenze von {(timeLimit ?? DefaultTimeLimit).TotalMinutes:0.#} Minuten erreicht — abgebrochen."
                        : "Vom Benutzer abgebrochen.",
                    FinishedUtc = DateTimeOffset.UtcNow, ElapsedMs = sw.ElapsedMilliseconds
                });
            }
            catch (Exception ex)
            {
                Set(id, s => s with
                {
                    Status = QuantJobStatus.Failed, Error = ex.Message,
                    FinishedUtc = DateTimeOffset.UtcNow, ElapsedMs = sw.ElapsedMilliseconds
                });
            }
            finally { cts.Dispose(); }
        }, CancellationToken.None);

        return id;
    }

    public QuantJobState? Get(string id) => _jobs.TryGetValue(id, out var e) ? e.State : null;

    public IReadOnlyList<QuantJobState> List() =>
        _jobs.Values.Select(e => e.State with { Result = null })
            .OrderByDescending(s => s.StartedUtc).ToList();

    public bool Cancel(string id)
    {
        if (!_jobs.TryGetValue(id, out var e) || e.State.Status != QuantJobStatus.Running) return false;
        try { e.Cts.Cancel(); } catch (ObjectDisposedException) { return false; }
        return true;
    }

    private void Set(string id, Func<QuantJobState, QuantJobState> update)
    {
        if (_jobs.TryGetValue(id, out var e)) e.State = update(e.State);
    }

    private void Cleanup()
    {
        var cutoff = DateTimeOffset.UtcNow - RetentionTime;
        foreach (var kv in _jobs)
            if (kv.Value.State.FinishedUtc is { } f && f < cutoff)
                _jobs.TryRemove(kv.Key, out _);
    }

    public void Dispose()
    {
        foreach (var e in _jobs.Values)
            try { e.Cts.Cancel(); e.Cts.Dispose(); } catch (ObjectDisposedException) { /* bereits freigegeben */ }
        _jobs.Clear();
    }
}
