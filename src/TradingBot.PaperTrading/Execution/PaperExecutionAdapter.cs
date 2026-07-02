using TradingBot.Application.Simulation;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.PaperTrading.Execution;

/// <summary>
/// SIMULIERTER Execution-Adapter für Paper Trading. Rein in-memory:
/// KEINE Netzwerkverbindung, KEINE Broker-SDKs, KEINE echten Orders, keine API-Keys.
///
/// Nutzt dasselbe <see cref="FillModel"/> wie der Backtest (gemeinsame Fill-Semantik):
/// Orders werden bei Submit als "pending" angenommen (Status New) und erst vom NÄCHSTEN
/// per <see cref="ProcessTick"/> gelieferten Tick gefüllt (kein Lookahead). Limit/Stop
/// füllen nur bei Preisberührung. Fills gehen als <see cref="Filled"/>-Event an den OrderManager.
///
/// Rejected Orders sind über ein deterministisches Prädikat simulierbar; Cancel/Replace
/// werden unterstützt; Teilfüllungen sind vorbereitet (FillEvent.IsPartial), Standard: volle Fills.
/// </summary>
public sealed class PaperExecutionAdapter : IBrokerExecutionAdapter
{
    private readonly FillModel _fillModel;
    private readonly InstrumentProfile? _instrument;
    private readonly decimal _slippageTicks;
    private readonly Func<OrderRequest, bool>? _rejectPredicate;
    private readonly List<OrderRequest> _pending = new();
    private readonly object _sync = new();

    public PaperExecutionAdapter(
        FillModel fillModel, InstrumentProfile? instrument, decimal slippageTicks,
        Func<OrderRequest, bool>? rejectPredicate = null)
    {
        _fillModel = fillModel ?? throw new ArgumentNullException(nameof(fillModel));
        _instrument = instrument;
        _slippageTicks = slippageTicks < 0m ? 0m : slippageTicks;
        _rejectPredicate = rejectPredicate;
    }

    public bool IsConnected { get; private set; }
    public decimal TotalSlippageCost { get; private set; }
    public int FilledCount { get; private set; }
    public int RejectedCount { get; private set; }
    public int CancelledCount { get; private set; }

    public int PendingCount
    {
        get { lock (_sync) return _pending.Count; }
    }

    public event EventHandler<FillEvent>? Filled;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task<OrderResult> SubmitOrderAsync(OrderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Fail-closed: ohne InstrumentProfile kann kein Fill simuliert werden -> ablehnen.
        if (_instrument is null)
        {
            RejectedCount++;
            return Task.FromResult(Rejected(request, "Kein InstrumentProfile – Paper-Fill nicht möglich."));
        }

        if (_rejectPredicate?.Invoke(request) == true)
        {
            RejectedCount++;
            return Task.FromResult(Rejected(request, "Simulierte Ablehnung (Paper)."));
        }

        lock (_sync) _pending.Add(request);
        return Task.FromResult(new OrderResult
        {
            OrderId = request.OrderId,
            Status = OrderStatus.New, // angenommen/ruhend; Fill folgt über Filled-Event
            Timestamp = request.CreatedAt
        });
    }

    public Task<OrderResult> CancelOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            int removed = _pending.RemoveAll(o => o.OrderId == orderId);
            if (removed > 0) CancelledCount++;
        }
        return Task.FromResult(new OrderResult { OrderId = orderId, Status = OrderStatus.Cancelled });
    }

    public Task<OrderResult> ReplaceOrderAsync(OrderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_sync)
        {
            var idx = _pending.FindIndex(o => o.OrderId == request.OrderId);
            if (idx >= 0) _pending[idx] = request;
        }
        return Task.FromResult(new OrderResult { OrderId = request.OrderId, Status = OrderStatus.New });
    }

    /// <summary>Paper hält die Position lokal (PositionManager) – kein externer Broker-Abruf.</summary>
    public Task<Position?> GetBrokerPositionAsync(string symbol, CancellationToken cancellationToken = default)
        => Task.FromResult<Position?>(null);

    /// <summary>
    /// Wird von der Session für jeden Tick aufgerufen. Prüft alle offenen Orders gegen den Tick
    /// und löst Fills aus. Wird VOR der Strategie aufgerufen → Market-Fills am Folge-Tick.
    /// </summary>
    public void ProcessTick(MarketTick tick)
    {
        if (_instrument is null) return;

        List<OrderRequest> snapshot;
        lock (_sync)
        {
            if (_pending.Count == 0) return;
            snapshot = _pending.ToList();
        }

        foreach (var order in snapshot)
        {
            var fill = _fillModel.TryFill(order, tick, _instrument, _slippageTicks);
            if (fill is null) continue;

            bool removed;
            lock (_sync) removed = _pending.Remove(order);
            if (!removed) continue; // z. B. zwischenzeitlich storniert

            TotalSlippageCost += fill.SlippageCost;
            FilledCount++;
            Filled?.Invoke(this, fill.Event);
        }
    }

    private static OrderResult Rejected(OrderRequest request, string message) => new()
    {
        OrderId = request.OrderId,
        Status = OrderStatus.Rejected,
        Message = message,
        Timestamp = request.CreatedAt
    };
}
