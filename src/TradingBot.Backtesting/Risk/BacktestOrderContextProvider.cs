using TradingBot.Core.Interfaces;
using TradingBot.Domain.Models;

namespace TradingBot.Backtesting.Risk;

/// <summary>
/// Baut den RiskEvaluationRequest im Backtest aus den fixen Profilen + aktuellem Positionsstand.
/// Profile werden immer mitgegeben (nie null), damit der RiskManager sie regulär prüft.
/// </summary>
public sealed class BacktestOrderContextProvider : IOrderContextProvider
{
    private readonly InstrumentProfile _instrument;
    private readonly FeeProfile _fee;
    private readonly BrokerProfile _broker;
    private readonly RiskConfig _risk;
    private readonly IDailyRiskStateProvider _dailyState;
    private readonly IPositionManager _positions;
    private readonly IClock _clock;

    public BacktestOrderContextProvider(
        InstrumentProfile instrument, FeeProfile fee, BrokerProfile broker, RiskConfig risk,
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
