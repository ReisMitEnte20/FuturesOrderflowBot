using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Core.Interfaces;

/// <summary>
/// Übersetzt FREIGEGEBENE Signale in Orders und verwaltet deren Lebenszyklus.
/// Nur der OrderManager erzeugt <see cref="OrderRequest"/>-Objekte und sendet sie über
/// den <see cref="IBrokerExecutionAdapter"/>. Er fragt zuvor IMMER den RiskManager und
/// schützt per Idempotency-Key vor Doppel-Orders.
/// </summary>
public interface IOrderManager
{
    /// <summary>
    /// Verarbeitet ein Signal als Entry-Order: Kontext bauen, Risk prüfen, bei
    /// Freigabe Order mit <c>ApprovedContracts</c> senden. Gibt den erzeugten
    /// <see cref="OrderState"/> zurück; null, wenn der RiskManager ablehnt.
    /// Bei doppeltem Signal wird der bereits existierende OrderState zurückgegeben.
    /// </summary>
    Task<OrderState?> ProcessSignalAsync(
        TradeSignal signal,
        OrderType entryType = OrderType.Market,
        decimal? limitPrice = null,
        decimal? stopPrice = null,
        CancellationToken cancellationToken = default);

    Task<OrderState?> CancelAsync(Guid orderId, CancellationToken cancellationToken = default);

    /// <summary>Ändert Preis/Menge einer bestehenden Order, ohne eine zweite offene Order zu erzeugen.</summary>
    Task<OrderState?> ReplaceAsync(
        Guid orderId, decimal? newLimitPrice = null, decimal? newStopPrice = null,
        int? newQuantity = null, CancellationToken cancellationToken = default);

    OrderState? GetOrder(Guid orderId);
    IReadOnlyCollection<OrderState> Orders { get; }

    /// <summary>Letzte Risk-Entscheidung (Diagnose/Dashboard).</summary>
    RiskDecision? LastDecision { get; }
}
