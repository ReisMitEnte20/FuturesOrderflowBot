using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Strategies.OrderFlow;

/// <summary>
/// Professionelles Orderflow-Strategie-TEMPLATE. KEINE Profit-Strategie – die Checks sind
/// dokumentierte Platzhalter-Proxys, die später durch die echten Setup-Regeln des Traders
/// ersetzt/parametrisiert werden.
///
/// Garantien:
/// - Arbeitet NUR auf echten OrderFlowBars (Bid/Ask/Aggressor); ohne Klassifikation kein Signal.
/// - Erzeugt ausschließlich <see cref="TradeSignal"/> – niemals Orders (keine Execution-Referenz).
/// - Vollständig über StrategyConfig.Parameters konfigurierbar (keine Magic Numbers).
/// - Deterministisch: gleiche Bars + gleiche Config → gleiche Signale.
///
/// Signal-Logik: Basis-Filter (MinDelta/MinVolume) und aktivierte Filter (VWAP-Distanz,
/// Session-High/Low-Nähe) müssen bestehen; von den Confirmations (Delta-Divergenz, Absorption,
/// Liquidity Sweep, CVD, Volume Spike, Reversal, Breakout, Bar-Imbalance) müssen mindestens
/// <c>RequiredConfirmations</c> erfüllt sein. Qualifizieren BEIDE Richtungen → kein Signal.
/// </summary>
public sealed class OrderFlowSetupTemplateStrategy : IStrategy
{
    private OrderFlowTemplateParameters _p = new();
    private OrderFlowConditionEvaluator _evaluator;
    private InstrumentProfile? _instrument;
    private int _barsSinceLastSignal = int.MaxValue;

    public OrderFlowSetupTemplateStrategy()
        => _evaluator = new OrderFlowConditionEvaluator(_p, null);

    public string Name => "OrderFlowSetupTemplateStrategy";

    public StrategyDataRequirements DataRequirements => new()
    {
        NeedsOrderFlowBars = true,
        NeedsBidAskVolume = true,
        NeedsDelta = true,
        NeedsCumulativeDelta = true,
        NeedsVwap = true // optional genutzt (bar-basiert), wenn UseVwapFilter aktiv
    };

    public void Initialize(StrategyExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _p = OrderFlowTemplateParameters.From(context.Config);
        _instrument = context.Instrument;
        _evaluator = new OrderFlowConditionEvaluator(_p, _instrument);
        _barsSinceLastSignal = int.MaxValue;
    }

    public TradeSignal? OnOrderFlowBar(OrderFlowBar bar)
    {
        // Fail-closed: ohne echte Bid/Ask-Klassifikation KEIN Signal (Doppel-Absicherung
        // zusätzlich zur StrategyEngine, die solche Bars gar nicht erst verteilt).
        if (bar.TotalVolume > 0m && bar.BidVolume + bar.AskVolume <= 0m)
            return null;

        _evaluator.Add(bar);
        if (_barsSinceLastSignal < int.MaxValue) _barsSinceLastSignal++;

        if (_evaluator.BarCount < 2) return null;                       // Mindest-Historie
        if (_p.CooldownBars > 0 && _barsSinceLastSignal <= _p.CooldownBars) return null;

        var longEval = Evaluate(SignalDirection.Long);
        var shortEval = Evaluate(SignalDirection.Short);

        bool longOk = longEval.Qualifies(_p.RequiredConfirmations);
        bool shortOk = shortEval.Qualifies(_p.RequiredConfirmations);

        if (longOk == shortOk) return null; // keins oder beide (ambivalent) -> kein Signal

        var (direction, eval) = longOk ? (SignalDirection.Long, longEval) : (SignalDirection.Short, shortEval);
        _barsSinceLastSignal = 0;

        int enabledConfirmations = eval.Confirmations.Count;
        var triggered = eval.Confirmations.Where(c => c.IsMet).Select(c => c.Condition).ToList();
        var failed = eval.Confirmations.Where(c => c.Status == ConditionStatus.NotMet)
            .Concat(eval.Filters.Where(f => f.Status == ConditionStatus.NotMet))
            .Select(c => c.Condition).ToList();
        var insufficient = eval.Confirmations.Concat(eval.Filters)
            .Where(c => c.Status == ConditionStatus.InsufficientData)
            .Select(c => $"{c.Condition}: {c.Detail}").ToList();

        return new TradeSignal
        {
            StrategyName = Name,
            Symbol = bar.Symbol,
            Direction = direction,
            Timestamp = bar.CloseTime,
            ReferencePrice = bar.Close,
            Confidence = enabledConfirmations > 0 ? (decimal)triggered.Count / enabledConfirmations : 0m,
            Reason = $"{direction} signal: {string.Join(" + ", triggered)} " +
                     $"({triggered.Count}/{enabledConfirmations} Confirmations, Filter bestanden)",
            TriggeredConditions = triggered,
            FailedConditions = failed,
            DebugNotes = insufficient.Count > 0 ? string.Join("; ", insufficient) : null
        };
    }

    public void Reset()
    {
        _evaluator.Reset();
        _barsSinceLastSignal = int.MaxValue;
    }

    // ---- intern --------------------------------------------------------------

    private DirectionEvaluation Evaluate(SignalDirection direction)
    {
        var filters = new List<ConditionResult> { _evaluator.BaseFilters() };
        if (_p.UseVwapFilter) filters.Add(_evaluator.VwapDistance());
        if (_p.UseSessionHighLowFilter) filters.Add(_evaluator.SessionHighLowProximity(direction));

        var confirmations = new List<ConditionResult>
        {
            _evaluator.DeltaDivergence(direction),
            _evaluator.Absorption(direction),
            _evaluator.LiquiditySweep(direction),
            _evaluator.VolumeSpike(direction),
            _evaluator.ReversalConfirmation(direction),
            _evaluator.BreakoutConfirmation(direction),
            _evaluator.BarImbalance(direction)
        };
        if (_p.UseCvdConfirmation) confirmations.Add(_evaluator.CvdConfirmation(direction));

        return new DirectionEvaluation(filters, confirmations);
    }

    private sealed record DirectionEvaluation(
        IReadOnlyList<ConditionResult> Filters, IReadOnlyList<ConditionResult> Confirmations)
    {
        /// <summary>Alle aktivierten Filter erfüllt (InsufficientData zählt fail-closed als nicht bestanden)
        /// UND genügend Confirmations erfüllt.</summary>
        public bool Qualifies(int requiredConfirmations)
            => Filters.All(f => f.IsMet)
               && Confirmations.Count(c => c.IsMet) >= requiredConfirmations;
    }
}
