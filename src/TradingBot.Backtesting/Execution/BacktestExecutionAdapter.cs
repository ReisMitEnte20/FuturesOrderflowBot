using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Backtesting.Execution;

/// <summary>
/// SIMULIERTER Execution-Adapter für Backtests. Rein in-memory: keine Netzwerkverbindung,
/// keine Live-API, keine Broker-SDKs. Kann niemals echte Orders senden.
///
/// Orders werden bei <see cref="SubmitOrderAsync"/> als "pending" akzeptiert (Status New).
/// Erst der nächste per <see cref="ProcessTick"/> gelieferte Tick kann sie füllen – dadurch
/// füllt eine Market-Order frühestens am Folge-Tick (kein Lookahead). Fills werden als
/// <see cref="Filled"/>-Event an den OrderManager gemeldet.
/// </summary>
public sealed class BacktestExecutionAdapter : IBrokerExecutionAdapter
{
    private readonly FillModel _fillModel;
    private readonly InstrumentProfile _instrument;
    private readonly decimal _slippageTicks;
    private readonly Func<OrderRequest, bool>? _rejectPredicate;
    private readonly List<OrderRequest> _pending = new();

    public BacktestExecutionAdapter(
        FillModel fillModel, InstrumentProfile instrument, decimal slippageTicks,
        Func<OrderRequest, bool>? rejectPredicate = null)
    {
        _fillModel = fillModel ?? throw new ArgumentNullException(nameof(fillModel));
        _instrument = instrument ?? throw new ArgumentNullException(nameof(instrument));
        _slippageTicks = slippageTicks < 0m ? 0m : slippageTicks;
        _rejectPredicate = rejectPredicate;
    }

    public bool IsConnected { get; private set; }
    public decimal TotalSlippageCost { get; private set; }
    public int PendingCount => _pending.Count;

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

        if (_rejectPredicate?.Invoke(request) == true)
            return Task.FromResult(new OrderResult
            {
                OrderId = request.OrderId,
                Status = OrderStatus.Rejected,
                Message = "Simulierte Ablehnung (Backtest).",
                Timestamp = request.CreatedAt
            });

        _pending.Add(request);
        return Task.FromResult(new OrderResult
        {
            OrderId = request.OrderId,
            Status = OrderStatus.New, // akzeptiert/ruhend; Fill folgt über Filled-Event
            Timestamp = request.CreatedAt
        });
    }

    public Task<OrderResult> CancelOrderAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        _pending.RemoveAll(o => o.OrderId == orderId);
        return Task.FromResult(new OrderResult { OrderId = orderId, Status = OrderStatus.Cancelled });
    }

    public Task<OrderResult> ReplaceOrderAsync(OrderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var idx = _pending.FindIndex(o => o.OrderId == request.OrderId);
        if (idx >= 0) _pending[idx] = request;
        return Task.FromResult(new OrderResult { OrderId = request.OrderId, Status = OrderStatus.New });
    }

    /// <summary>Backtest hält die Position lokal (PositionManager) – kein externer Abruf.</summary>
    public Task<Position?> GetBrokerPositionAsync(string symbol, CancellationToken cancellationToken = default)
        => Task.FromResult<Position?>(null);

    /// <summary>
    /// Wird von der Engine für jeden Tick aufgerufen. Prüft alle offenen Orders gegen den Tick
    /// und löst Fills aus. Wird VOR der Strategie aufgerufen → Market-Fills am Folge-Tick.
    /// </summary>
    public void ProcessTick(MarketTick tick)
    {
        if (_pending.Count == 0) return;

        foreach (var order in _pending.ToList())
        {
            var fill = _fillModel.TryFill(order, tick, _instrument, _slippageTicks);
            if (fill is null) continue;

            _pending.Remove(order);
            TotalSlippageCost += fill.SlippageCost;
            Filled?.Invoke(this, fill.Event);
        }
    }
}
