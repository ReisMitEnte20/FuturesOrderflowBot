using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Orders;

/// <summary>
/// Orchestriert Entry-Orders: Kontext bauen → RiskManager prüfen → bei Freigabe Order mit
/// <c>ApprovedContracts</c> über den <see cref="IBrokerExecutionAdapter"/> senden →
/// Lifecycle tracken → Fills an den PositionManager weiterreichen.
///
/// Sicherheit:
/// - Sendet NUR bei <see cref="RiskDecision.Approved"/> == true.
/// - Idempotency-Key wird erst bei der Freigabe reserviert (abgelehnte Signale bleiben retry-fähig);
///   Re-Check nach dem await verhindert Doppel-Submit bei Races.
/// - Fills (FillEvents) sind die einzige Quelle für gefüllte Menge/Position; das Submit-Ergebnis
///   liefert nur Accepted/Rejected. Verhindert Doppelzählung.
/// - Exceptions/Unknown beim Submit → Lifecycle Failed (fail-closed), keine Position.
/// </summary>
public sealed class OrderManager : IOrderManager, IDisposable
{
    private readonly IOrderContextProvider _context;
    private readonly IRiskManager _riskManager;
    private readonly IBrokerExecutionAdapter _adapter;
    private readonly IPositionManager _positionManager;
    private readonly IClock _clock;
    private readonly ILogger _logger;
    private readonly TradingAccount _account;

    private readonly object _sync = new();
    private readonly Dictionary<Guid, ManagedOrder> _orders = new();
    private readonly Dictionary<string, Guid> _keyToOrder = new(StringComparer.Ordinal);
    private bool _disposed;

    public OrderManager(
        IOrderContextProvider context, IRiskManager riskManager, IBrokerExecutionAdapter adapter,
        IPositionManager positionManager, IClock clock, ILogger logger, TradingAccount account)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _riskManager = riskManager ?? throw new ArgumentNullException(nameof(riskManager));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _positionManager = positionManager ?? throw new ArgumentNullException(nameof(positionManager));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _account = account ?? throw new ArgumentNullException(nameof(account));

        _adapter.Filled += OnFilled;
    }

    public RiskDecision? LastDecision { get; private set; }

    public OrderState? GetOrder(Guid orderId)
    {
        lock (_sync) return _orders.TryGetValue(orderId, out var mo) ? mo.State : null;
    }

    public IReadOnlyCollection<OrderState> Orders
    {
        get { lock (_sync) return _orders.Values.Select(o => o.State).ToList(); }
    }

    public async Task<OrderState?> ProcessSignalAsync(
        TradeSignal signal, OrderType entryType = OrderType.Market,
        decimal? limitPrice = null, decimal? stopPrice = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signal);

        int requested = signal.SuggestedQuantity is int q && q > 0 ? q : 1;
        string key = BuildIdempotencyKey(signal);

        // Frühe Duplikat-Erkennung (genehmigte Signale sind bereits reserviert).
        lock (_sync)
        {
            if (_keyToOrder.TryGetValue(key, out var existingId))
            {
                _logger.Warning($"Doppeltes Signal ignoriert (key={key}).");
                return _orders[existingId].State;
            }
        }

        // Validierung der Order-Parameter (fail-closed, kein malformter Submit).
        if (entryType == OrderType.Limit && limitPrice is null)
        {
            _logger.Warning("Limit-Entry ohne LimitPrice – keine Order.");
            return null;
        }
        if (entryType is OrderType.Stop or OrderType.StopLimit && stopPrice is null)
        {
            _logger.Warning("Stop-Entry ohne StopPrice – keine Order.");
            return null;
        }

        // Kontext + Risk (außerhalb des Locks; reine/IO-Operationen).
        RiskEvaluationRequest request = await _context.BuildAsync(signal, requested, cancellationToken)
            .ConfigureAwait(false);
        RiskDecision decision = _riskManager.Evaluate(request);
        LastDecision = decision;

        if (!decision.Approved)
        {
            _logger.Warning($"Signal abgelehnt ({decision.RejectionReason}): {decision.Message}");
            return null; // Key NICHT reserviert -> retry-fähig.
        }

        if (request.Instrument is null || request.Fee is null)
        {
            // Sollte bei Approved nie passieren (RiskManager erzwingt Profile), aber fail-closed.
            _logger.Error("Freigabe ohne Instrument/Fee-Profil – Order verworfen.");
            return null;
        }

        var now = _clock.UtcNow;
        var order = OrderFactory.BuildEntry(
            signal, request.Instrument, _account.AccountId, decision.ApprovedContracts,
            entryType, key, now, limitPrice, stopPrice);

        ManagedOrder mo;
        lock (_sync)
        {
            // Re-Check nach await: verhindert Doppel-Submit bei nebenläufigen Duplikaten.
            if (_keyToOrder.TryGetValue(key, out var existingId))
            {
                _logger.Warning($"Doppeltes Signal nach Risk-Check ignoriert (key={key}).");
                return _orders[existingId].State;
            }

            mo = new ManagedOrder
            {
                Request = order,
                Instrument = request.Instrument,
                Fee = request.Fee,
                State = new OrderState
                {
                    OrderId = order.OrderId,
                    IdempotencyKey = key,
                    SourceSignalId = signal.SignalId,
                    Symbol = order.Symbol,
                    Side = order.Side,
                    OrderType = order.OrderType,
                    RequestedQuantity = order.Quantity,
                    Lifecycle = OrderLifecycleState.Created,
                    CreatedAt = now,
                    UpdatedAt = now
                }
            };
            _orders[order.OrderId] = mo;
            _keyToOrder[key] = order.OrderId; // Reservierung VOR dem Submit.
        }

        // Submit außerhalb des Locks.
        Transition(mo.State.OrderId, s => s with { Lifecycle = OrderLifecycleState.Submitted, UpdatedAt = _clock.UtcNow });
        try
        {
            OrderResult result = await _adapter.SubmitOrderAsync(order, cancellationToken).ConfigureAwait(false);
            ApplySubmitResult(mo.State.OrderId, result);
        }
        catch (Exception ex)
        {
            _logger.Error($"Submit fehlgeschlagen für Order {order.OrderId}.", ex);
            Transition(mo.State.OrderId, s => s with
            {
                Lifecycle = OrderLifecycleState.Failed,
                Message = "Submit-Exception: " + ex.Message,
                UpdatedAt = _clock.UtcNow
            });
        }

        return GetOrder(mo.State.OrderId);
    }

    public async Task<OrderState?> CancelAsync(Guid orderId, CancellationToken cancellationToken = default)
    {
        ManagedOrder? mo;
        lock (_sync) _orders.TryGetValue(orderId, out mo);
        if (mo is null) { _logger.Warning($"Cancel: Order {orderId} unbekannt."); return null; }
        if (mo.State.IsTerminal) return mo.State;

        try
        {
            var result = await _adapter.CancelOrderAsync(orderId, cancellationToken).ConfigureAwait(false);
            if (result.Status is OrderStatus.Cancelled)
                Transition(orderId, s => s with { Lifecycle = OrderLifecycleState.Cancelled, UpdatedAt = _clock.UtcNow });
            else
                Transition(orderId, s => s with { Message = "Cancel-Antwort: " + result.Status, UpdatedAt = _clock.UtcNow });
        }
        catch (Exception ex)
        {
            _logger.Error($"Cancel fehlgeschlagen für Order {orderId}.", ex);
            Transition(orderId, s => s with { Lifecycle = OrderLifecycleState.Failed, UpdatedAt = _clock.UtcNow });
        }

        return GetOrder(orderId);
    }

    public async Task<OrderState?> ReplaceAsync(
        Guid orderId, decimal? newLimitPrice = null, decimal? newStopPrice = null,
        int? newQuantity = null, CancellationToken cancellationToken = default)
    {
        ManagedOrder? mo;
        lock (_sync) _orders.TryGetValue(orderId, out mo);
        if (mo is null) { _logger.Warning($"Replace: Order {orderId} unbekannt."); return null; }
        if (mo.State.IsTerminal) { _logger.Warning($"Replace: Order {orderId} bereits terminal."); return mo.State; }

        // Gleiche OrderId beibehalten -> keine zweite offene Order.
        var replacement = mo.Request with
        {
            LimitPrice = newLimitPrice ?? mo.Request.LimitPrice,
            StopPrice = newStopPrice ?? mo.Request.StopPrice,
            Quantity = newQuantity ?? mo.Request.Quantity
        };

        try
        {
            var result = await _adapter.ReplaceOrderAsync(replacement, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                mo.Request = replacement;
                mo.State = mo.State with
                {
                    RequestedQuantity = replacement.Quantity,
                    Message = "Replaced: " + result.Status,
                    UpdatedAt = _clock.UtcNow
                };
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"Replace fehlgeschlagen für Order {orderId}.", ex);
            Transition(orderId, s => s with { Lifecycle = OrderLifecycleState.Failed, UpdatedAt = _clock.UtcNow });
        }

        return GetOrder(orderId);
    }

    // ---- intern --------------------------------------------------------------

    private static string BuildIdempotencyKey(TradeSignal signal) => signal.SignalId.ToString("N");

    private void ApplySubmitResult(Guid orderId, OrderResult result)
    {
        var lifecycle = result.Status switch
        {
            OrderStatus.Rejected => OrderLifecycleState.Rejected,
            OrderStatus.Cancelled => OrderLifecycleState.Cancelled,
            OrderStatus.Expired => OrderLifecycleState.Cancelled,
            OrderStatus.PendingNew or OrderStatus.New or OrderStatus.Working
                or OrderStatus.PartiallyFilled or OrderStatus.Filled => OrderLifecycleState.Accepted,
            _ => OrderLifecycleState.Failed // Unbekannt -> fail-closed.
        };

        Transition(orderId, s => s with
        {
            Lifecycle = s.Lifecycle is OrderLifecycleState.PartiallyFilled or OrderLifecycleState.Filled
                ? s.Lifecycle            // bereits durch Fill weiter -> nicht zurückstufen
                : lifecycle,
            BrokerOrderId = result.BrokerOrderId ?? s.BrokerOrderId,
            Message = result.Message ?? s.Message,
            UpdatedAt = _clock.UtcNow
        });
    }

    private void OnFilled(object? sender, FillEvent fill)
    {
        ManagedOrder? mo;
        InstrumentProfile? instrument;
        FeeProfile? fee;

        lock (_sync)
        {
            if (!_orders.TryGetValue(fill.OrderId, out mo))
            {
                _logger.Warning($"Fill für unbekannte Order {fill.OrderId} ignoriert.");
                return;
            }

            int newFilled = mo.State.FilledQuantity + fill.Quantity;
            decimal avg = WeightedAverage(
                mo.State.AverageFillPrice, mo.State.FilledQuantity, fill.FillPrice, fill.Quantity);

            var lifecycle = newFilled >= mo.State.RequestedQuantity
                ? OrderLifecycleState.Filled
                : OrderLifecycleState.PartiallyFilled;

            mo.State = mo.State with
            {
                FilledQuantity = newFilled,
                AverageFillPrice = avg,
                Lifecycle = lifecycle,
                BrokerOrderId = fill.BrokerOrderId ?? mo.State.BrokerOrderId,
                UpdatedAt = _clock.UtcNow
            };
            instrument = mo.Instrument;
            fee = mo.Fee;
        }

        // Position außerhalb des Locks aktualisieren (PositionManager hat eigenen Lock).
        if (instrument is not null && fee is not null)
            _positionManager.ApplyFill(fill, instrument, fee);
    }

    private static decimal WeightedAverage(decimal? oldAvg, int oldQty, decimal newPrice, int newQty)
    {
        if (oldAvg is null || oldQty <= 0) return newPrice;
        return (oldAvg.Value * oldQty + newPrice * newQty) / (oldQty + newQty);
    }

    private void Transition(Guid orderId, Func<OrderState, OrderState> mutate)
    {
        lock (_sync)
            if (_orders.TryGetValue(orderId, out var mo))
                mo.State = mutate(mo.State);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _adapter.Filled -= OnFilled;
        _disposed = true;
    }

    private sealed class ManagedOrder
    {
        public required OrderState State { get; set; }
        public required OrderRequest Request { get; set; }
        public required InstrumentProfile Instrument { get; init; }
        public required FeeProfile Fee { get; init; }
    }
}
