using TradingBot.Core.Interfaces;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Orders;

/// <summary>
/// Standard-Implementierung von <see cref="IOrderContextProvider"/>. Stellt den Risk-Kontext
/// aus Profil-Providern, Tageszustand und PositionManager zusammen. Fehlende Profile bleiben
/// null – der RiskManager erzwingt sie fail-closed.
/// </summary>
public sealed class OrderContextProvider : IOrderContextProvider
{
    private readonly IInstrumentProvider _instruments;
    private readonly IFeeProfileProvider _fees;
    private readonly BrokerProfile _broker;
    private readonly RiskConfig _risk;
    private readonly IDailyRiskStateProvider _dailyState;
    private readonly IPositionManager _positions;
    private readonly IClock _clock;

    public OrderContextProvider(
        IInstrumentProvider instruments, IFeeProfileProvider fees, BrokerProfile broker,
        RiskConfig risk, IDailyRiskStateProvider dailyState, IPositionManager positions, IClock clock)
    {
        _instruments = instruments;
        _fees = fees;
        _broker = broker;
        _risk = risk;
        _dailyState = dailyState;
        _positions = positions;
        _clock = clock;
    }

    public async Task<RiskEvaluationRequest> BuildAsync(
        TradeSignal signal, int requestedContracts, CancellationToken cancellationToken = default)
    {
        var instrument = await _instruments.GetAsync(signal.Symbol, cancellationToken).ConfigureAwait(false);
        var fee = await _fees.GetAsync(_broker.BrokerName, _broker.ExecutionProvider, signal.Symbol, cancellationToken)
            .ConfigureAwait(false);

        var date = DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime);
        var daily = _dailyState.GetCurrent(date);

        int currentContracts = _positions.GetPosition(signal.Symbol)?.Quantity ?? 0;

        return new RiskEvaluationRequest
        {
            Signal = signal,
            RequestedContracts = requestedContracts,
            DailyState = daily,
            Instrument = instrument,
            Fee = fee,
            Broker = _broker,
            Risk = _risk,
            OpenPositionsCount = _positions.OpenPositions.Count,
            CurrentOpenContracts = currentContracts,
            PositionReconciled = null // Reconciliation prüft der SafetyMonitor separat.
        };
    }
}

/// <summary>
/// Einfacher In-Memory-Tageszustand. Platzhalter, bis ein echter State-Updater (spätere Phase)
/// den Zustand aus Trades fortschreibt.
/// </summary>
public sealed class InMemoryDailyRiskStateProvider : IDailyRiskStateProvider
{
    private DailyRiskState _state;

    public InMemoryDailyRiskStateProvider(DailyRiskState? initial = null)
        => _state = initial ?? DailyRiskState.Start(DateOnly.FromDateTime(DateTime.UtcNow));

    public DailyRiskState GetCurrent(DateOnly date)
        => _state.TradingDate == date ? _state : DailyRiskState.Start(date);

    public void Update(DailyRiskState state) => _state = state;
}
