using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Risk;

/// <summary>
/// Sicherheits-Gatekeeper. Prüft jede Order-Absicht gegen alle Risk-Regeln und gibt
/// eine <see cref="RiskDecision"/> zurück. Hat KEINE Referenz auf Execution/OrderManager –
/// kann technisch keine Order senden. Fail-closed: jede Unsicherheit führt zur Ablehnung.
///
/// Konventionen:
/// - Limitwert 0 bei MaxTradesPerDay/MaxOrdersPerMinute/MaxConsecutiveLosses bedeutet "unbegrenzt".
/// - MaxDailyLoss/MaxContracts/MaxOpenPositions sind laut Validierung &gt; 0.
/// </summary>
public sealed class RiskManager : IRiskManager
{
    private readonly IKillSwitchService _killSwitch;
    private readonly ISafetyMonitor _safety;
    private readonly IClock _clock;

    public RiskManager(IKillSwitchService killSwitch, ISafetyMonitor safety, IClock clock)
    {
        _killSwitch = killSwitch ?? throw new ArgumentNullException(nameof(killSwitch));
        _safety = safety ?? throw new ArgumentNullException(nameof(safety));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public RiskDecision Evaluate(RiskEvaluationRequest request)
    {
        // --- Guards: echte Aufrufer-Fehler werfen laut (keine stillen Fehler) ---
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Signal);
        ArgumentNullException.ThrowIfNull(request.DailyState);
        if (request.RequestedContracts <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), "RequestedContracts muss > 0 sein.");
        if (request.Signal.Direction == SignalDirection.Flat)
            throw new ArgumentException("Signal muss Long oder Short sein (Flat ist keine Order-Absicht).", nameof(request));

        var m = ComputeMetrics(request);
        int requested = request.RequestedContracts;

        // ===================== 1) Harte technische Stops =====================
        if (_killSwitch.IsActive)
            return Reject(RiskRejectionReason.KillSwitchActive, "Kill Switch ist aktiv.", m, requested);

        if (!_safety.IsBrokerConnected)
            return Reject(RiskRejectionReason.BrokerDisconnected, "Broker ist nicht verbunden.", m, requested);

        if (!_safety.IsMarketDataConnected)
            return Reject(RiskRejectionReason.MarketDataDisconnected, "Datenfeed ist nicht verbunden.", m, requested);

        if (request.PositionReconciled == false || _safety.HasPositionMismatch)
            return Reject(RiskRejectionReason.PositionMismatch, "Lokale Position weicht von Broker-Position ab.", m, requested);

        if (_safety.Status == SafetyStatus.Halted)
            return Reject(RiskRejectionReason.TechnicalError, "SafetyMonitor meldet HALTED.", m, requested);

        // ===================== 2) Pflicht-Profile vorhanden =====================
        if (request.Risk is null)
            return Reject(RiskRejectionReason.MissingRiskConfig, "Kein RiskConfig vorhanden.", m, requested);
        if (request.Instrument is null)
            return Reject(RiskRejectionReason.MissingInstrumentProfile, "Kein InstrumentProfile vorhanden.", m, requested);
        if (request.Fee is null)
            return Reject(RiskRejectionReason.MissingFeeProfile, "Kein FeeProfile vorhanden.", m, requested);
        if (request.Broker is null)
            return Reject(RiskRejectionReason.MissingBrokerProfile, "Kein BrokerProfile vorhanden.", m, requested);

        var risk = request.Risk;
        var instrument = request.Instrument;
        var d = request.DailyState;

        // ===================== 3) Symbol & Session =====================
        if (!string.Equals(request.Signal.Symbol, instrument.Symbol, StringComparison.OrdinalIgnoreCase))
            return Reject(RiskRejectionReason.SymbolNotAllowed,
                $"Signal-Symbol '{request.Signal.Symbol}' passt nicht zum InstrumentProfile '{instrument.Symbol}'.", m, requested);

        if (risk.EnforceSession)
        {
            if (!TryIsWithinSession(instrument, _clock.UtcNow, out bool inside))
                return Reject(RiskRejectionReason.TechnicalError,
                    $"TradingTimezone '{instrument.TradingTimezone}' konnte nicht aufgelöst werden.", m, requested);
            if (!inside)
                return Reject(RiskRejectionReason.OutsideTradingSession,
                    "Außerhalb der erlaubten Handelssession.", m, requested);
        }

        // ===================== 4) Tagesverlust / Profit-Schutz =====================
        if (d.IsDailyLossLimitHit || d.NetPnL <= -risk.MaxDailyLoss)
            return Reject(RiskRejectionReason.MaxDailyLossReached,
                $"Max Daily Loss erreicht: NetPnL {d.NetPnL} ≤ -{risk.MaxDailyLoss}.", m, requested);

        if (risk.TrailingDrawdownEnabled && risk.TrailingDrawdownAmount is decimal tdd
            && d.NetPnL <= d.PeakNetPnL - tdd)
            return Reject(RiskRejectionReason.TrailingDrawdownHit,
                $"Trailing Drawdown erreicht: NetPnL {d.NetPnL} ≤ Peak {d.PeakNetPnL} - {tdd}.", m, requested);

        if (risk.ProfitLockEnabled && d.IsProfitLockActive)
            return Reject(RiskRejectionReason.ProfitLockActive, "Profit Lock ist aktiv.", m, requested);

        // ===================== 5) Frequenz-/Streak-Limits =====================
        if (risk.MaxTradesPerDay > 0 && d.TradesTaken >= risk.MaxTradesPerDay)
            return Reject(RiskRejectionReason.MaxTradesPerDayReached,
                $"Max Trades pro Tag erreicht: {d.TradesTaken}/{risk.MaxTradesPerDay}.", m, requested);

        if (risk.MaxConsecutiveLosses > 0 && d.ConsecutiveLosses >= risk.MaxConsecutiveLosses)
            return Reject(RiskRejectionReason.ConsecutiveLossLimitReached,
                $"Verlustserie erreicht: {d.ConsecutiveLosses}/{risk.MaxConsecutiveLosses}.", m, requested);

        if (risk.MaxOrdersPerMinute > 0 && d.OrdersThisMinute >= risk.MaxOrdersPerMinute)
            return Reject(RiskRejectionReason.MaxOrdersPerMinuteExceeded,
                $"Max Orders pro Minute erreicht: {d.OrdersThisMinute}/{risk.MaxOrdersPerMinute}.", m, requested);

        if (request.OpenPositionsCount >= risk.MaxOpenPositions)
            return Reject(RiskRejectionReason.MaxOpenPositionsReached,
                $"Max offene Positionen erreicht: {request.OpenPositionsCount}/{risk.MaxOpenPositions}.", m, requested);

        // ===================== 6) Contracts: hartes Limit + Sizing =====================
        int effectiveMax = Math.Min(risk.MaxContracts, instrument.MaxContracts);

        // Anfrage über absolutem Ceiling -> harte Ablehnung (kein stilles Verkleinern).
        if (requested > effectiveMax)
            return Reject(RiskRejectionReason.MaxContractsExceeded,
                $"Angefragte Kontrakte {requested} > Limit {effectiveMax}.", m, requested);

        int room = effectiveMax - request.CurrentOpenContracts;
        if (room <= 0)
            return Reject(RiskRejectionReason.MaxContractsExceeded,
                $"Kein Contract-Spielraum: offen {request.CurrentOpenContracts}, Limit {effectiveMax}.", m, requested);

        int approved = Math.Min(requested, room);

        // Max Loss per Trade (nur wenn Stop bekannt): begrenzt Kontrakte oder lehnt ab.
        if (request.Signal.SuggestedStopLossTicks is int stopTicks && stopTicks > 0 && risk.MaxLossPerTrade > 0)
        {
            decimal lossPerContract = stopTicks * instrument.TickValue;
            if (lossPerContract > risk.MaxLossPerTrade)
                return Reject(RiskRejectionReason.MaxLossPerTradeExceeded,
                    $"Verlust pro Kontrakt {lossPerContract} > Max Loss/Trade {risk.MaxLossPerTrade}.", m, requested);

            int maxByLoss = (int)Math.Floor(risk.MaxLossPerTrade / lossPerContract);
            approved = Math.Min(approved, maxByLoss);
        }

        // approved ist hier garantiert >= 1.
        return new RiskDecision
        {
            Approved = true,
            RejectionReason = RiskRejectionReason.None,
            Message = "Genehmigt.",
            RequestedContracts = requested,
            ApprovedContracts = approved,
            RemainingDailyLoss = m.RemainingDailyLoss,
            RemainingTrades = m.RemainingTrades,
            CurrentDailyPnL = m.CurrentDailyPnL,
            CurrentConsecutiveLosses = m.CurrentConsecutiveLosses,
            RiskUtilizationPercent = m.Utilization
        };
    }

    // ---- Hilfsmethoden -------------------------------------------------------

    private static RiskDecision Reject(RiskRejectionReason reason, string message, in Metrics m, int requested) => new()
    {
        Approved = false,
        RejectionReason = reason,
        Message = message,
        RequestedContracts = requested,
        ApprovedContracts = 0,
        RemainingDailyLoss = m.RemainingDailyLoss,
        RemainingTrades = m.RemainingTrades,
        CurrentDailyPnL = m.CurrentDailyPnL,
        CurrentConsecutiveLosses = m.CurrentConsecutiveLosses,
        RiskUtilizationPercent = m.Utilization
    };

    private static Metrics ComputeMetrics(RiskEvaluationRequest request)
    {
        var d = request.DailyState;
        var risk = request.Risk;

        if (risk is null)
            return new Metrics(0m, 0, d.NetPnL, d.ConsecutiveLosses, 0m);

        decimal lossSoFar = d.NetPnL < 0m ? -d.NetPnL : 0m;
        decimal remainingLoss = Math.Max(0m, risk.MaxDailyLoss - lossSoFar);
        int remainingTrades = risk.MaxTradesPerDay > 0
            ? Math.Max(0, risk.MaxTradesPerDay - d.TradesTaken)
            : int.MaxValue;

        decimal lossUtil = risk.MaxDailyLoss > 0m ? lossSoFar / risk.MaxDailyLoss : 0m;
        decimal tradesUtil = risk.MaxTradesPerDay > 0 ? (decimal)d.TradesTaken / risk.MaxTradesPerDay : 0m;
        decimal consUtil = risk.MaxConsecutiveLosses > 0 ? (decimal)d.ConsecutiveLosses / risk.MaxConsecutiveLosses : 0m;

        decimal util = Math.Max(lossUtil, Math.Max(tradesUtil, consUtil)) * 100m;
        util = Math.Clamp(util, 0m, 100m);

        return new Metrics(remainingLoss, remainingTrades, d.NetPnL, d.ConsecutiveLosses, util);
    }

    private static bool TryIsWithinSession(InstrumentProfile instrument, DateTimeOffset utcNow, out bool inside)
    {
        inside = false;
        try
        {
            var tz = ResolveTimeZone(instrument.TradingTimezone);
            var local = TimeZoneInfo.ConvertTime(utcNow, tz);
            var t = TimeOnly.FromTimeSpan(local.TimeOfDay);
            inside = t >= instrument.SessionStart && t < instrument.SessionEnd;
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false; // Fail-closed: aufrufende Logik lehnt ab.
        }
    }

    private static TimeZoneInfo ResolveTimeZone(string id)
        => string.IsNullOrWhiteSpace(id) || id.Equals("UTC", StringComparison.OrdinalIgnoreCase)
            ? TimeZoneInfo.Utc
            : TimeZoneInfo.FindSystemTimeZoneById(id);

    private readonly record struct Metrics(
        decimal RemainingDailyLoss,
        int RemainingTrades,
        decimal CurrentDailyPnL,
        int CurrentConsecutiveLosses,
        decimal Utilization);
}
