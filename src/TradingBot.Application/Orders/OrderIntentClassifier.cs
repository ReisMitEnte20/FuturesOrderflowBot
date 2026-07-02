using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Orders;

/// <summary>
/// Bestimmt aus aktueller Position + Signalrichtung, ob eine Order Risiko erhöht oder reduziert,
/// und die dafür sinnvolle Kontraktanzahl. Rein und deterministisch.
///
/// - Flat → <see cref="OrderIntent.Entry"/>
/// - gleiche Richtung → <see cref="OrderIntent.Add"/> (risiko-erhöhend)
/// - Gegenrichtung, weniger als offen → <see cref="OrderIntent.Reduce"/>
/// - Gegenrichtung, genau die offene Menge → <see cref="OrderIntent.Close"/>
/// - Gegenrichtung, MEHR als offen (würde flippen) → konservativ <see cref="OrderIntent.Close"/>
///   und auf die offene Menge begrenzt; das Eröffnen der Gegenposition erfordert ein Folgesignal
///   (das dann als Entry voll geprüft wird). So wird nie ungeprüftes Neurisiko über einen Flip geöffnet.
/// </summary>
public static class OrderIntentClassifier
{
    public static (OrderIntent Intent, int Contracts) Classify(
        Position? current, SignalDirection direction, int requestedContracts)
    {
        if (requestedContracts < 0) requestedContracts = 0;
        var side = direction == SignalDirection.Long ? OrderSide.Buy : OrderSide.Sell;

        if (current is null || current.Side == PositionSide.Flat || current.Quantity == 0)
            return (OrderIntent.Entry, requestedContracts);

        bool increasesPosition =
            (current.Side == PositionSide.Long && side == OrderSide.Buy) ||
            (current.Side == PositionSide.Short && side == OrderSide.Sell);

        if (increasesPosition)
            return (OrderIntent.Add, requestedContracts);

        // Gegenrichtung -> reduziert die bestehende Position.
        if (requestedContracts < current.Quantity)
            return (OrderIntent.Reduce, requestedContracts);
        if (requestedContracts == current.Quantity)
            return (OrderIntent.Close, requestedContracts);

        // Würde flippen -> konservativ nur schließen (auf offene Menge begrenzt).
        return (OrderIntent.Close, current.Quantity);
    }
}
