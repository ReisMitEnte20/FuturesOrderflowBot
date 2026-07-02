using TradingBot.Core.Interfaces;
using TradingBot.Domain.Models;

namespace TradingBot.PaperTrading.Risk;

/// <summary>
/// Baut den RiskEvaluationRequest der Paper-Session aus Profilen + aktuellem Positionsstand.
/// Profile dürfen NULL sein – der RiskManager lehnt dann fail-closed ab (Missing*Profile).
/// PositionReconciled = true, weil im Paper Mode die lokale Position die einzige Wahrheit ist
/// (kein externer Broker); echte Reconciliation kommt erst mit einem Live-Adapter.
/// </summary>
public sealed class PaperOrderContextProvider : IOrderContextProvider
{
    private readonly InstrumentProfile? _instrument;
    private readonly FeeProfile? _fee;
    private readonly BrokerProfile? _broker;
    private readonly RiskConfig? _risk;
    private readonly IDailyRiskStateProvider _dailyState;
    private readonly IPositionManager _positions;
    private readonly IClock _clock;

    public PaperOrderContextProvider(
        InstrumentProfile? instrument, FeeProfile? fee, BrokerProfile? broker, RiskConfig? risk,
        IDailyRiskStateProvider dailyState, IPositionManager positions, IClock clock)
    {
        _instrument = instrument;
        _fee = fee;
        _broker = broker;
        _risk = risk;
        _dailyState = dailyState;
        _positions = positions;
        _clock = clock;
    }

    public Task<RiskEvaluationRequest> BuildAsync(
        TradeSignal signal, int requestedContracts, CancellationToken cancellationToken = default)
    {
        var date = DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime);
        int currentContracts = _positions.GetPosition(signal.Symbol)?.Quantity ?? 0;

        var request = new RiskEvaluationRequest
        {
            Signal = signal,
            RequestedContracts = requestedContracts,
            DailyState = _dailyState.GetCurrent(date),
            Instrument = _instrument,
            Fee = _fee,
            Broker = _broker,
            Risk = _risk,
            OpenPositionsCount = _positions.OpenPositions.Count,
            CurrentOpenContracts = currentContracts,
            PositionReconciled = true
        };
        return Task.FromResult(request);
    }
}
