using TradingBot.Application.Strategies.OrderFlow;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Research.Orderflow;

/// <summary>Ein von der (gegateten) Strategie tatsaechlich ausgegebenes Signal samt der Bar, auf der es entstand,
/// und dem Positionszustand der Engine UNMITTELBAR davor (nur gelesen).</summary>
public sealed record RecordedSignal(
    int Seq, OrderFlowBar Bar, TradeSignal Signal, PositionSide PositionBefore, int PositionQtyBefore, bool PositionKnown);

/// <summary>
/// Reiner MITSCHNITT (Trade-Nachvollzug im Dashboard): reicht jeden Aufruf unveraendert an die innere Strategie
/// durch und protokolliert nur die ausgegebenen Signale. Aendert weder Signale noch Reihenfolge noch Parameter;
/// der Positionszustand wird ausschliesslich LESEND ueber <see cref="IPositionManager.GetPosition"/> abgefragt.
/// Keine Order-/Execution-Faehigkeit.
/// </summary>
public sealed class SignalRecordingStrategy : IOrderFlowStrategy, IPositionManagerAware
{
    private readonly IStrategy _inner;
    private readonly List<RecordedSignal> _log;
    private IPositionManager? _positions;

    public SignalRecordingStrategy(IStrategy inner, List<RecordedSignal> log)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public string Name => _inner.Name;
    public StrategyDataRequirements DataRequirements => _inner.DataRequirements;

    public void Initialize(StrategyExecutionContext context) => _inner.Initialize(context);

    public void Reset()
    {
        _inner.Reset();
        _log.Clear();
    }

    public void UsePositionManager(IPositionManager positions)
    {
        _positions = positions;
        if (_inner is IPositionManagerAware aware) aware.UsePositionManager(positions);
    }

    public TradeSignal? OnTick(MarketTick tick) => _inner.OnTick(tick);
    public TradeSignal? OnCandle(Candle candle) => _inner.OnCandle(candle);

    public TradeSignal? OnOrderFlowBar(OrderFlowBar bar)
    {
        var sig = _inner.OnOrderFlowBar(bar);
        if (sig is null) return null;
        var pos = _positions?.GetPosition(bar.Symbol);
        var side = pos is null || pos.Quantity == 0 ? PositionSide.Flat : pos.Side;
        _log.Add(new RecordedSignal(_log.Count, bar, sig, side, pos?.Quantity ?? 0, _positions is not null));
        return sig;
    }
}
