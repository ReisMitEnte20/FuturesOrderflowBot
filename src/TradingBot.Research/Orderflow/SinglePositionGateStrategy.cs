using TradingBot.Application.Strategies.OrderFlow;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Research.Orderflow;

/// <summary>
/// LOKALE Lauf-Anpassung (keine Aenderung der Strategieregeln, kein globaler OrderManager-Eingriff):
/// begrenzt eine innere OrderFlow-Strategie auf HOECHSTENS EINEN Kontrakt und unterdrueckt das Aufstocken
/// (Add) bei wiederholten gleichgerichteten Signalen — wie der urspruengliche Harness.
///
/// Die Entscheidung stuetzt sich auf den TATSAECHLICHEN Positionszustand der Engine (read-only ueber
/// <see cref="IPositionManager.GetPosition"/>, gebunden via <see cref="IPositionManagerAware"/>), NICHT auf
/// ein signalbasiertes Modell. Ein gesendetes Signal ist kein Fill: wird eine Order abgelehnt oder bleibt
/// sie ungefuellt, bleibt die gelesene Position flach — es entsteht keine Phantomposition und ein spaeteres
/// gueltiges Signal wird nicht faelschlich unterdrueckt.
///
/// Logik (gegen die tatsaechliche Position):
///  - gleiche Richtung bereits offen -> KEIN Signal (halten, kein Add)
///  - Flat -> nur schliessen, wenn tatsaechlich eine Position offen ist
///  - Gegenrichtung -> Signal durchreichen (die Engine schliesst konservativ; Reopen per Folgesignal)
/// Ist (noch) kein PositionManager gebunden, reicht der Gate das Signal unveraendert durch (die harte
/// Obergrenze MaxContracts begrenzt dann die Position); im Backtest bindet die Engine den PositionManager.
/// </summary>
public sealed class SinglePositionGateStrategy : IOrderFlowStrategy, IPositionManagerAware
{
    private readonly IStrategy _inner;
    private IPositionManager? _positions;

    public SinglePositionGateStrategy(IStrategy inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public string Name => _inner.Name;
    public StrategyDataRequirements DataRequirements => new() { NeedsOrderFlowBars = true };

    public void Initialize(StrategyExecutionContext context) => _inner.Initialize(context);
    public void Reset() => _inner.Reset();

    public void UsePositionManager(IPositionManager positions)
    {
        _positions = positions;
        if (_inner is IPositionManagerAware aware) aware.UsePositionManager(positions);
    }

    public TradeSignal? OnOrderFlowBar(OrderFlowBar bar)
    {
        var sig = _inner.OnOrderFlowBar(bar);
        if (sig is null) return null; // z. B. Doji -> halten, keine Positionsaenderung

        // Tatsaechlicher Positionszustand (Fills bis einschliesslich dieses Ticks). Ohne Bindung: durchreichen.
        var pos = _positions?.GetPosition(bar.Symbol);
        var side = pos is null || pos.Quantity == 0 ? PositionSide.Flat : pos.Side;

        return sig.Direction switch
        {
            SignalDirection.Long => side == PositionSide.Long ? null : sig,   // schon long -> halten (kein Add)
            SignalDirection.Short => side == PositionSide.Short ? null : sig, // schon short -> halten (kein Add)
            _ => side == PositionSide.Flat ? null : sig                       // Flat -> nur schliessen, wenn offen
        };
    }
}
