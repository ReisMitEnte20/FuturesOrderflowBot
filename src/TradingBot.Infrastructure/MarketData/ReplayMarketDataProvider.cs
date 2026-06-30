using System.Runtime.CompilerServices;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.MarketData;

/// <summary>
/// Spielt historische Ticks sequenziell ab (Replay/Backtest/Paper). Sortiert defensiv
/// chronologisch. Geschwindigkeit über <see cref="ReplayOptions"/> (AsFastAsPossible/RealTime/
/// FasterThanRealtime). Sendet NIEMALS Orders – reiner Datenlieferant. Stop über
/// <see cref="DisconnectAsync"/> oder das CancellationToken; Pause/Resume vorbereitet.
/// Die Verzögerung ist injizierbar (testbar ohne reales Warten).
/// </summary>
public sealed class ReplayMarketDataProvider : IMarketDataProvider
{
    private readonly IReadOnlyList<MarketTick> _ticks;
    private readonly ReplayOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    private CancellationTokenSource? _stopCts;
    private volatile bool _connected;
    private volatile TaskCompletionSource? _pauseTcs;

    public ReplayMarketDataProvider(
        IEnumerable<MarketTick> ticks, ReplayOptions? options = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(ticks);
        _ticks = ticks.OrderBy(t => t.Timestamp).ToList(); // stabil chronologisch
        _options = options ?? ReplayOptions.Fast;
        _delay = delay ?? ((ts, ct) => Task.Delay(ts, ct));
    }

    public bool IsConnected => _connected;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _stopCts?.Dispose();
        _stopCts = new CancellationTokenSource();
        _connected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _connected = false;
        _stopCts?.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>Pausiert den Stream vor dem nächsten Tick (vorbereitet).</summary>
    public void Pause() =>
        Interlocked.CompareExchange(
            ref _pauseTcs, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), null);

    /// <summary>Setzt einen pausierten Stream fort.</summary>
    public void Resume() => Interlocked.Exchange(ref _pauseTcs, null)?.TrySetResult();

    public async IAsyncEnumerable<MarketTick> SubscribeTicksAsync(
        string symbol, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!_connected)
            throw new InvalidOperationException("Feed nicht verbunden – zuerst ConnectAsync aufrufen.");

        var stopToken = _stopCts?.Token ?? CancellationToken.None;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopToken);
        var token = linked.Token;

        DateTimeOffset? prev = null;
        foreach (var tick in _ticks)
        {
            if (!string.IsNullOrEmpty(symbol) &&
                !string.Equals(tick.Symbol, symbol, StringComparison.OrdinalIgnoreCase))
                continue;

            if (token.IsCancellationRequested) yield break;

            // Pause-Gate (vorbereitet).
            var gate = _pauseTcs;
            if (gate is not null)
            {
                try { await gate.Task.WaitAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) { yield break; }
            }

            // Geschwindigkeit: Verzögerung zwischen aufeinanderfolgenden Ticks.
            if (prev is not null && _options.Mode != ReplayMode.AsFastAsPossible)
            {
                var wait = _options.DelayFor(tick.Timestamp - prev.Value);
                try { await _delay(wait, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { yield break; }
            }

            if (token.IsCancellationRequested) yield break;

            prev = tick.Timestamp;
            yield return tick;
        }
    }
}
