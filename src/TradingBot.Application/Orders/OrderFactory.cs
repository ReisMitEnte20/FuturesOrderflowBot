using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Orders;

/// <summary>Ergebnis einer Bracket-Vorbereitung: Entry + Stop-Loss + Take-Profit (OCO-verknüpft).</summary>
public sealed record BracketOrders(OrderRequest Entry, OrderRequest StopLoss, OrderRequest TakeProfit);

/// <summary>
/// Reine Order-Bauwerkstatt: erzeugt <see cref="OrderRequest"/>-Objekte für Entry, Stop-Loss,
/// Take-Profit, Bracket/OCO, Break-Even und Trailing-Stop. KEINE Ausführung, KEINE Seiteneffekte –
/// daher vollständig deterministisch testbar. Tick-Werte kommen ausschließlich aus dem InstrumentProfile.
/// </summary>
public static class OrderFactory
{
    /// <summary>Baut eine Entry-Order aus einem Signal. Long → Buy, Short → Sell.</summary>
    public static OrderRequest BuildEntry(
        TradeSignal signal, InstrumentProfile instrument, string accountId, int contracts,
        OrderType orderType, string idempotencyKey, DateTimeOffset now,
        decimal? limitPrice = null, decimal? stopPrice = null)
    {
        ArgumentNullException.ThrowIfNull(signal);
        ArgumentNullException.ThrowIfNull(instrument);
        if (contracts <= 0) throw new ArgumentOutOfRangeException(nameof(contracts));
        if (signal.Direction == SignalDirection.Flat)
            throw new ArgumentException("Entry benötigt Long oder Short.", nameof(signal));

        var side = signal.Direction == SignalDirection.Long ? OrderSide.Buy : OrderSide.Sell;

        return new OrderRequest
        {
            IdempotencyKey = idempotencyKey,
            SourceSignalId = signal.SignalId,
            AccountId = accountId,
            Symbol = signal.Symbol,
            BrokerSymbol = instrument.BrokerSymbol,
            Side = side,
            OrderType = orderType,
            Quantity = contracts,
            TimeInForce = TimeInForce.Day,
            LimitPrice = limitPrice,
            StopPrice = stopPrice,
            CreatedAt = now
        };
    }

    /// <summary>Stop-Loss als Exit-Stop. Long-Position → Sell-Stop unter Entry, Short → Buy-Stop über Entry.</summary>
    public static OrderRequest BuildStopLoss(
        PositionSide positionSide, InstrumentProfile instrument, string accountId, string symbol,
        decimal entryPrice, int stopLossTicks, int contracts, string idempotencyKey, DateTimeOffset now,
        Guid? bracketGroupId = null)
    {
        RequireDirectional(positionSide);
        decimal stopPrice = positionSide == PositionSide.Long
            ? entryPrice - stopLossTicks * instrument.TickSize
            : entryPrice + stopLossTicks * instrument.TickSize;

        return new OrderRequest
        {
            IdempotencyKey = idempotencyKey,
            AccountId = accountId,
            Symbol = symbol,
            BrokerSymbol = instrument.BrokerSymbol,
            Side = positionSide == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy,
            OrderType = OrderType.Stop,
            Quantity = contracts,
            StopPrice = stopPrice,
            BracketGroupId = bracketGroupId,
            CreatedAt = now
        };
    }

    /// <summary>Take-Profit als Exit-Limit. Long → Sell-Limit über Entry, Short → Buy-Limit unter Entry.</summary>
    public static OrderRequest BuildTakeProfit(
        PositionSide positionSide, InstrumentProfile instrument, string accountId, string symbol,
        decimal entryPrice, int takeProfitTicks, int contracts, string idempotencyKey, DateTimeOffset now,
        Guid? bracketGroupId = null)
    {
        RequireDirectional(positionSide);
        decimal limitPrice = positionSide == PositionSide.Long
            ? entryPrice + takeProfitTicks * instrument.TickSize
            : entryPrice - takeProfitTicks * instrument.TickSize;

        return new OrderRequest
        {
            IdempotencyKey = idempotencyKey,
            AccountId = accountId,
            Symbol = symbol,
            BrokerSymbol = instrument.BrokerSymbol,
            Side = positionSide == PositionSide.Long ? OrderSide.Sell : OrderSide.Buy,
            OrderType = OrderType.Limit,
            Quantity = contracts,
            LimitPrice = limitPrice,
            BracketGroupId = bracketGroupId,
            CreatedAt = now
        };
    }

    /// <summary>Bereitet eine Bracket vor: Entry + SL + TP, gemeinsam per BracketGroupId (OCO) verknüpft.</summary>
    public static BracketOrders BuildBracket(
        TradeSignal signal, InstrumentProfile instrument, string accountId, int contracts,
        decimal entryPrice, int stopLossTicks, int takeProfitTicks,
        string idempotencyKeyBase, DateTimeOffset now)
    {
        var positionSide = signal.Direction == SignalDirection.Long ? PositionSide.Long : PositionSide.Short;
        var group = Guid.NewGuid();

        var entry = BuildEntry(signal, instrument, accountId, contracts, OrderType.Limit,
            idempotencyKeyBase + ":entry", now, limitPrice: entryPrice) with
        { BracketGroupId = group };

        var sl = BuildStopLoss(positionSide, instrument, accountId, signal.Symbol, entryPrice,
            stopLossTicks, contracts, idempotencyKeyBase + ":sl", now, group);

        var tp = BuildTakeProfit(positionSide, instrument, accountId, signal.Symbol, entryPrice,
            takeProfitTicks, contracts, idempotencyKeyBase + ":tp", now, group);

        return new BracketOrders(entry, sl, tp);
    }

    /// <summary>Verschiebt einen bestehenden Stop-Loss auf Break-Even (Entry ± Offset-Ticks).</summary>
    public static OrderRequest BuildBreakEven(
        OrderRequest existingStop, PositionSide positionSide, InstrumentProfile instrument,
        decimal entryPrice, int offsetTicks = 0)
    {
        RequireDirectional(positionSide);
        decimal newStop = positionSide == PositionSide.Long
            ? entryPrice + offsetTicks * instrument.TickSize
            : entryPrice - offsetTicks * instrument.TickSize;

        return existingStop with { StopPrice = newStop };
    }

    /// <summary>
    /// Berechnet den neuen Trailing-Stop-Preis. Der Stop bewegt sich NUR in günstige Richtung
    /// (Long: nur nach oben, Short: nur nach unten) und lockert nie.
    /// </summary>
    public static decimal ComputeTrailingStop(
        PositionSide positionSide, decimal existingStopPrice, decimal currentPrice,
        int trailTicks, InstrumentProfile instrument)
    {
        RequireDirectional(positionSide);
        decimal candidate = positionSide == PositionSide.Long
            ? currentPrice - trailTicks * instrument.TickSize
            : currentPrice + trailTicks * instrument.TickSize;

        return positionSide == PositionSide.Long
            ? Math.Max(existingStopPrice, candidate)
            : Math.Min(existingStopPrice, candidate);
    }

    /// <summary>Wie <see cref="ComputeTrailingStop"/>, liefert aber direkt die aktualisierte Stop-Order.</summary>
    public static OrderRequest BuildTrailingStop(
        OrderRequest existingStop, PositionSide positionSide, decimal currentPrice,
        int trailTicks, InstrumentProfile instrument)
    {
        decimal newStop = ComputeTrailingStop(
            positionSide, existingStop.StopPrice ?? currentPrice, currentPrice, trailTicks, instrument);
        return existingStop with { StopPrice = newStop };
    }

    private static void RequireDirectional(PositionSide side)
    {
        if (side == PositionSide.Flat)
            throw new ArgumentException("PositionSide muss Long oder Short sein.", nameof(side));
    }
}
