using TradingBot.Application.Fees;
using TradingBot.Application.MarketData;
using TradingBot.Application.Orders;
using TradingBot.Application.Pnl;
using TradingBot.Application.Positions;
using TradingBot.Application.Risk;
using TradingBot.Application.Simulation;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.PaperTrading.Execution;
using TradingBot.PaperTrading.Journal;
using TradingBot.PaperTrading.Positions;
using TradingBot.PaperTrading.Risk;

namespace TradingBot.PaperTrading;

/// <summary>
/// Eine laufende Paper-Trading-Session. Verdrahtet die reale Pipeline (Strategy → RiskManager →
/// OrderManager → PositionManager) mit dem simulierten <see cref="PaperExecutionAdapter"/> und
/// einem Marktdaten-Feed (Replay/CSV). Sendet niemals echte Orders.
///
/// Ablauf pro Tick (Reihenfolge ist sicherheitsrelevant, identisch zum Backtest):
///   1. Uhr auf Tick-Zeit  2. Feed-Heartbeat  3. offene Orders füllen (VOR der Strategie →
///   Market füllt am Folge-Tick, kein Lookahead)  4. Strategie auswerten (außer pausiert)
///   5. Signal über OrderManager (Risk-Prüfung) einreichen.
///
/// Pause: stoppt NEUE Signale; offene Orders können weiterhin füllen (wie in der Realität).
/// Stop/CancellationToken: beendet die Session sauber (fail-closed, Ergebnis wird berechnet).
/// Uhrzeit ist Tick-gesteuert → mit Replay-Daten vollständig deterministisch.
/// </summary>
public sealed class PaperTradingSession
{
    private readonly PaperTradingRequest _request;
    private readonly CancellationTokenSource _stopCts = new();
    private readonly TaskCompletionSource<PaperTradingResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Pipeline-Komponenten (in Start() verdrahtet).
    private readonly SimulatedClock _clock = new(DateTimeOffset.MinValue);
    private readonly ClosedTradeTracker _positions;
    private readonly PaperDailyStateProvider _dailyState;
    private readonly IKillSwitchService _killSwitch;
    private readonly ISafetyMonitor _safety;
    private readonly PaperExecutionAdapter _adapter;
    private readonly OrderManager _orderManager;
    private readonly FeedHealthMonitor _feedHealth;
    private readonly InMemoryTradeJournal _journal = new();
    private readonly object _dashboardSync = new();
    private readonly List<DashboardTickPoint> _dashboardTicks = [];
    private readonly List<DashboardPositionEvent> _dashboardPositionEvents = [];
    private readonly Dictionary<Guid, (decimal? StopLossPrice, decimal? TakeProfitPrice)> _signalMarkers = [];
    private readonly int _dashboardTickBufferSize;

    private volatile bool _paused;
    private volatile bool _stopRequested;
    private int _started; // 0/1 via Interlocked
    private int _ticks, _signals, _submitted, _riskRejected, _brokerRejected;
    private DateTimeOffset? _startedAt, _stoppedAt;
    private PaperTradingRunStatus _status = PaperTradingRunStatus.NotStarted;

    public PaperTradingSession(PaperTradingRequest request)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _dashboardTickBufferSize = Math.Max(100, request.Config.DashboardTickBufferSize);

        var feeCalc = new FeeCalculator();
        var pnlCalc = new PnLCalculator(feeCalc);
        _positions = new ClosedTradeTracker(new PositionManager(feeCalc, pnlCalc));

        _dailyState = new PaperDailyStateProvider(request.Risk?.MaxDailyLoss ?? decimal.MaxValue);
        _positions.TradeClosed = OnTradeClosed;

        _killSwitch = request.KillSwitch ?? new PaperKillSwitch();
        _safety = request.Safety ?? new PaperSafetyMonitor();
        var riskManager = new RiskManager(_killSwitch, _safety, _clock);

        decimal slippageTicks = request.Config.SlippageTicksOverride
            ?? request.Fee?.EstimatedSlippageTicks ?? 0m;
        _adapter = new PaperExecutionAdapter(new FillModel(), request.Instrument, slippageTicks, request.RejectOrder);

        var context = new PaperOrderContextProvider(
            request.Instrument, request.Fee, request.Broker, request.Risk,
            _dailyState, _positions, _clock);

        _feedHealth = new FeedHealthMonitor(_clock, request.Config.FeedStaleTimeout);

        _orderManager = new OrderManager(
            context, riskManager, _adapter, _positions, _clock, SilentLogger.Instance, request.Account);
    }

    public Guid SessionId { get; } = Guid.NewGuid();
    public PaperTradingRunStatus Status => _paused && _status == PaperTradingRunStatus.Running
        ? PaperTradingRunStatus.Paused
        : _status;
    public bool IsRunning => _status == PaperTradingRunStatus.Running;
    public bool IsPaused => _paused;

    /// <summary>Wird nach jedem verarbeiteten Tick ausgelöst (Observability/Tests).</summary>
    public event Action<MarketTick>? TickProcessed;

    /// <summary>Endergebnis; abgeschlossen sobald die Session endet.</summary>
    public Task<PaperTradingResult> Completion => _completion.Task;

    /// <summary>Pausiert NEUE Signale; offene Orders können weiter füllen.</summary>
    public void Pause() => _paused = true;

    public void Resume() => _paused = false;

    /// <summary>Stoppt die Session sauber und liefert das Endergebnis.</summary>
    public async Task<PaperTradingResult> StopAsync()
    {
        _stopRequested = true;
        _stopCts.Cancel();
        return await Completion.ConfigureAwait(false);
    }

    /// <summary>Startet den Hintergrund-Lauf. Nur einmal aufrufbar (von der Engine).</summary>
    internal void Start(CancellationToken externalToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("Session wurde bereits gestartet.");

        _ = Task.Run(() => RunAsync(externalToken));
    }

    private async Task RunAsync(CancellationToken externalToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(externalToken, _stopCts.Token);
        var token = linked.Token;
        string? failure = null;

        _startedAt = DateTimeOffset.UtcNow;
        _status = PaperTradingRunStatus.Running;

        try
        {
            await _adapter.ConnectAsync(token).ConfigureAwait(false);
            _feedHealth.SetConnected(true);
            await _request.MarketData.ConnectAsync(token).ConfigureAwait(false);

            await foreach (var tick in _request.MarketData
                .SubscribeTicksAsync(_request.Symbol, token).ConfigureAwait(false))
            {
                if (token.IsCancellationRequested) break;

                var positionBefore = _positions.GetPosition(_request.Symbol);
                _clock.Set(tick.Timestamp);
                _feedHealth.RecordTick(tick);
                _adapter.ProcessTick(tick);          // Fills offener Orders VOR der Strategie

                if (!_paused)
                {
                    var signal = _request.Strategy.OnTick(tick);
                    if (signal is not null)
                    {
                        TrackSignalMarkers(signal);
                        Interlocked.Increment(ref _signals);
                        var state = await _orderManager.ProcessSignalAsync(signal, cancellationToken: token)
                            .ConfigureAwait(false);

                        if (state is null)
                        {
                            if (_orderManager.LastDecision is { Approved: false })
                                Interlocked.Increment(ref _riskRejected);
                        }
                        else if (state.Lifecycle == OrderLifecycleState.Rejected)
                        {
                            Interlocked.Increment(ref _brokerRejected);
                        }
                        else
                        {
                            Interlocked.Increment(ref _submitted);
                        }
                    }
                }

                var positionAfter = _positions.GetPosition(_request.Symbol);
                AppendDashboardTick(tick, positionAfter);
                AppendDashboardPositionEvent(tick, positionBefore, positionAfter);

                // Zähler erst NACH vollständiger Tick-Verarbeitung (inkl. Strategie) erhöhen,
                // damit "TicksProcessed >= n" bedeutet: Tick n ist fertig verarbeitet.
                Interlocked.Increment(ref _ticks);
                TickProcessed?.Invoke(tick);
            }
        }
        catch (OperationCanceledException)
        {
            // sauberer Abbruch über Stop/Token
        }
        catch (Exception ex)
        {
            failure = ex.Message;
        }
        finally
        {
            _feedHealth.SetConnected(false);
            try { await _request.MarketData.DisconnectAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* best effort */ }
            try { await _adapter.DisconnectAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* best effort */ }
            _orderManager.Dispose();

            _stoppedAt = DateTimeOffset.UtcNow;
            _status = failure is not null
                ? PaperTradingRunStatus.Failed
                : _stopRequested
                    ? PaperTradingRunStatus.Stopped
                    : externalToken.IsCancellationRequested
                        ? PaperTradingRunStatus.Cancelled
                        : PaperTradingRunStatus.Completed;

            _completion.TrySetResult(BuildResult(failure));
        }
    }

    private void OnTradeClosed(PaperClosedTrade trade)
    {
        _dailyState.ApplyTrade(trade);

        // Journal-Eintrag über das bestehende ITradeJournal (in-memory, synchron abgeschlossen).
        var entry = new TradeJournalEntry
        {
            Timestamp = trade.ExitTime,
            Mode = TradingMode.Paper,
            Trade = new Trade
            {
                AccountId = _request.Account.AccountId,
                Symbol = trade.Symbol,
                Side = trade.Side,
                Quantity = trade.Quantity,
                EntryPrice = trade.EntryPrice,
                ExitPrice = trade.ExitPrice,
                EntryTime = trade.EntryTime,
                ExitTime = trade.ExitTime,
                GrossPnL = trade.GrossPnL,
                NetPnL = trade.NetPnL,
                Fees = trade.Fees
            }
        };
        _journal.RecordAsync(entry).GetAwaiter().GetResult();
    }

    /// <summary>Aktueller Zustands-Snapshot (read-only, jederzeit abrufbar).</summary>
    public PaperTradingSessionState GetState()
    {
        var position = _positions.GetPosition(_request.Symbol);
        var orders = _orderManager.Orders;
        var feed = _feedHealth.State;
        var today = _dailyState.GetCurrent(DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime));

        return new PaperTradingSessionState
        {
            SessionId = SessionId,
            StartedAt = _startedAt,
            StoppedAt = _stoppedAt,
            CurrentSymbol = _request.Symbol,
            TradingMode = TradingMode.Paper,
            Status = Status,
            IsRunning = IsRunning,
            IsPaused = _paused,
            CurrentPosition = position,
            TicksProcessed = _ticks,
            OpenOrders = orders.Count(o => !o.IsTerminal),
            FilledOrders = orders.Count(o => o.Lifecycle == OrderLifecycleState.Filled),
            RejectedOrders = _riskRejected + _brokerRejected,
            TradesToday = today.TradesTaken,
            GrossPnL = position?.RealizedGrossPnL ?? 0m,
            NetPnL = position?.RealizedNetPnL ?? 0m,
            TotalFees = position?.Fees.TotalFees ?? 0m,
            TotalSlippage = _adapter.TotalSlippageCost,
            UnrealizedGrossPnL = position?.UnrealizedGrossPnL ?? 0m,
            LastTickTime = feed.LastTickTimestamp,
            FeedHealthStatus = feed.Status,
            RiskStatus = _orderManager.LastDecision,
            KillSwitchActive = _killSwitch.IsActive
        };
    }

    /// <summary>
    /// Read-only Snapshot für externe Dashboards. Enthält Tick-Serie + Positions-Overlay-Events
    /// und hat bewusst keine Steuer- oder Orderfunktionen.
    /// </summary>
    public DashboardSnapshot GetDashboardSnapshot(int maxTicks = 1000)
    {
        if (maxTicks <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxTicks), "maxTicks muss > 0 sein.");

        var state = GetState();
        List<DashboardTickPoint> ticks;
        List<DashboardPositionEvent> events;
        lock (_dashboardSync)
        {
            int skip = Math.Max(0, _dashboardTicks.Count - maxTicks);
            ticks = _dashboardTicks.Skip(skip).ToList();
            events = _dashboardPositionEvents.ToList();
        }

        return new DashboardSnapshot
        {
            SessionId = state.SessionId,
            Symbol = state.CurrentSymbol,
            TradingMode = state.TradingMode,
            SessionStatus = state.Status.ToString(),
            CapturedAt = _clock.UtcNow,
            IsReadOnly = true,
            IsExecutionControlEnabled = false,
            TicksProcessed = state.TicksProcessed,
            GrossPnL = state.GrossPnL,
            NetPnL = state.NetPnL,
            TotalFees = state.TotalFees,
            TotalSlippage = state.TotalSlippage,
            UnrealizedGrossPnL = state.UnrealizedGrossPnL,
            FeedHealthStatus = state.FeedHealthStatus,
            RiskStatus = state.RiskStatus,
            KillSwitchActive = state.KillSwitchActive,
            TickSeries = ticks,
            PositionEvents = events
        };
    }

    /// <summary>Journal der Session (Trades mit Kontext, Mode = Paper).</summary>
    public IReadOnlyList<TradeJournalEntry> JournalEntries => _journal.Entries;

    /// <summary>Bisher abgeschlossene (simulierte) Trades – auch während des Laufs abfragbar.</summary>
    public IReadOnlyList<PaperClosedTrade> ClosedTrades => _positions.Trades;

    private PaperTradingResult BuildResult(string? failure) => new()
    {
        Status = _status,
        Message = failure,
        FinalState = GetState(),
        ClosedTrades = _positions.Trades,
        TicksProcessed = _ticks,
        SignalsGenerated = _signals,
        OrdersSubmitted = _submitted,
        OrdersRejectedByRisk = _riskRejected,
        OrdersRejectedByBroker = _brokerRejected,
        OrdersUnfilledAtEnd = _adapter.PendingCount
    };

    /// <summary>Stiller ILogger für die Paper-Session.</summary>
    private sealed class SilentLogger : ILogger
    {
        public static readonly SilentLogger Instance = new();
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }

    private void AppendDashboardTick(MarketTick tick, Position? position)
    {
        var point = new DashboardTickPoint
        {
            Symbol = tick.Symbol,
            Timestamp = tick.Timestamp,
            Price = tick.Price,
            Bid = tick.Bid,
            Ask = tick.Ask,
            Volume = tick.Volume,
            PositionSide = position?.Side ?? PositionSide.Flat,
            PositionQuantity = position?.Quantity ?? 0,
            UnrealizedGrossPnL = position?.UnrealizedGrossPnL ?? 0m
        };

        lock (_dashboardSync)
        {
            _dashboardTicks.Add(point);
            if (_dashboardTicks.Count > _dashboardTickBufferSize)
                _dashboardTicks.RemoveRange(0, _dashboardTicks.Count - _dashboardTickBufferSize);
        }
    }

    private void AppendDashboardPositionEvent(MarketTick tick, Position? before, Position? after)
    {
        var eventType = ClassifyPositionEvent(before, after);
        if (eventType is null)
            return;

        var marker = FindLatestMarker(tick.Symbol);

        var evt = new DashboardPositionEvent
        {
            EventType = eventType.Value,
            Symbol = tick.Symbol,
            Timestamp = tick.Timestamp,
            PositionSide = after?.Side ?? PositionSide.Flat,
            PositionQuantity = after?.Quantity ?? 0,
            Price = tick.Price,
            RealizedGrossPnL = after?.RealizedGrossPnL ?? before?.RealizedGrossPnL ?? 0m,
            RealizedNetPnL = after?.RealizedNetPnL ?? before?.RealizedNetPnL ?? 0m,
            StopLossPrice = marker.StopLossPrice,
            TakeProfitPrice = marker.TakeProfitPrice,
            SourceOrderId = marker.OrderId
        };

        lock (_dashboardSync)
            _dashboardPositionEvents.Add(evt);
    }

    private static DashboardPositionEventType? ClassifyPositionEvent(Position? before, Position? after)
    {
        int beforeQty = before?.Quantity ?? 0;
        int afterQty = after?.Quantity ?? 0;
        var beforeSide = before?.Side ?? PositionSide.Flat;
        var afterSide = after?.Side ?? PositionSide.Flat;

        bool wasFlat = beforeSide == PositionSide.Flat || beforeQty == 0;
        bool isFlat = afterSide == PositionSide.Flat || afterQty == 0;

        if (wasFlat && !isFlat)
            return DashboardPositionEventType.Entry;
        if (!wasFlat && isFlat)
            return DashboardPositionEventType.Exit;
        if (!wasFlat && !isFlat && beforeSide != afterSide)
            return DashboardPositionEventType.Flip;
        if (afterQty > beforeQty)
            return DashboardPositionEventType.Add;
        if (afterQty < beforeQty)
            return DashboardPositionEventType.Reduce;

        return null;
    }

    private void TrackSignalMarkers(TradeSignal signal)
    {
        if (_request.Instrument is null)
            return;

        decimal? stop = null;
        decimal? take = null;
        decimal tickSize = _request.Instrument.TickSize;

        if (signal.SuggestedStopLossTicks is int slTicks && slTicks > 0)
        {
            stop = signal.Direction == SignalDirection.Long
                ? signal.ReferencePrice - slTicks * tickSize
                : signal.ReferencePrice + slTicks * tickSize;
        }

        if (signal.SuggestedTakeProfitTicks is int tpTicks && tpTicks > 0)
        {
            take = signal.Direction == SignalDirection.Long
                ? signal.ReferencePrice + tpTicks * tickSize
                : signal.ReferencePrice - tpTicks * tickSize;
        }

        if (stop is null && take is null)
            return;

        lock (_dashboardSync)
            _signalMarkers[signal.SignalId] = (stop, take);
    }

    private (Guid? OrderId, decimal? StopLossPrice, decimal? TakeProfitPrice) FindLatestMarker(string symbol)
    {
        foreach (var order in _orderManager.Orders
                     .Where(o => o.Symbol == symbol)
                     .OrderByDescending(o => o.UpdatedAt))
        {
            decimal? stop = order.StopLossPrice;
            decimal? take = order.TakeProfitPrice;

            if ((stop is null && take is null) && order.SourceSignalId is Guid signalId)
            {
                lock (_dashboardSync)
                {
                    if (_signalMarkers.TryGetValue(signalId, out var tracked))
                    {
                        stop = tracked.StopLossPrice;
                        take = tracked.TakeProfitPrice;
                    }
                }
            }

            if (stop is not null || take is not null)
                return (order.OrderId, stop, take);
        }

        return (null, null, null);
    }
}
