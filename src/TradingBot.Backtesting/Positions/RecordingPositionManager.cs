using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;

namespace TradingBot.Backtesting.Positions;

/// <summary>
/// Dekoriert einen echten <see cref="IPositionManager"/> und rekonstruiert daraus abgeschlossene
/// Round-Turn-Trades (flat → flat bzw. Flip). Nutzt AUSSCHLIESSLICH die kumulativen Realized-Werte
/// des inneren PositionManagers (Deltas) – KEINE eigene Netting-Mathematik, daher keine
/// Doppelzählung. Es gilt garantiert: Σ Trade.Fees = TotalFees und Trade.NetPnL = Trade.GrossPnL − Fees.
/// </summary>
public sealed class RecordingPositionManager : IPositionManager
{
    private readonly IPositionManager _inner;
    private readonly Dictionary<string, OpenState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<BacktestTrade> _trades = new();

    public RecordingPositionManager(IPositionManager inner)
        => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>Wird bei jedem abgeschlossenen Trade aufgerufen (z. B. für DailyRiskState-Feedback).</summary>
    public Action<BacktestTrade>? TradeClosed { get; set; }

    public IReadOnlyList<BacktestTrade> Trades => _trades;

    public Position? GetPosition(string symbol) => _inner.GetPosition(symbol);
    public IReadOnlyCollection<Position> OpenPositions => _inner.OpenPositions;
    public Position MarkToMarket(string symbol, decimal lastPrice, InstrumentProfile instrument)
        => _inner.MarkToMarket(symbol, lastPrice, instrument);
    public bool Reconcile(string symbol, Position? brokerPosition) => _inner.Reconcile(symbol, brokerPosition);

    public Position ApplyFill(FillEvent fill, InstrumentProfile instrument, FeeProfile feeProfile)
    {
        var before = _inner.GetPosition(fill.Symbol);
        var after = _inner.ApplyFill(fill, instrument, feeProfile);
        RecordTransition(fill, before, after);
        return after;
    }

    private void RecordTransition(FillEvent fill, Position? before, Position after)
    {
        var beforeSide = before?.Side ?? PositionSide.Flat;
        var state = _states.TryGetValue(fill.Symbol, out var s) ? s : (_states[fill.Symbol] = new OpenState());

        bool wasOpen = beforeSide != PositionSide.Flat && state.IsOpen;

        if (!wasOpen && after.Side != PositionSide.Flat)
        {
            OpenTrade(state, fill, after, baselineFrom: before);
        }
        else if (wasOpen && after.Side == PositionSide.Flat)
        {
            CloseTrade(state, fill, after);
        }
        else if (wasOpen && after.Side != PositionSide.Flat && after.Side != state.EntrySide)
        {
            // Flip: alten Trade schließen, neuen im selben Fill eröffnen.
            CloseTrade(state, fill, after);
            OpenTrade(state, fill, after, baselineFrom: after);
        }
        // sonst: Aufbau/Teilreduktion in gleicher Richtung -> keine Trade-Grenze.
    }

    private static void OpenTrade(OpenState state, FillEvent fill, Position after, Position? baselineFrom)
    {
        state.IsOpen = true;
        state.EntrySide = after.Side;
        state.EntryTime = fill.Timestamp;
        state.EntryPrice = fill.FillPrice;
        state.EntryQuantity = after.Quantity;
        state.BaselineGross = baselineFrom?.RealizedGrossPnL ?? 0m;
        state.BaselineNet = baselineFrom?.RealizedNetPnL ?? 0m;
        state.BaselineFees = baselineFrom?.Fees.TotalFees ?? 0m;
    }

    private void CloseTrade(OpenState state, FillEvent fill, Position after)
    {
        var trade = new BacktestTrade
        {
            Symbol = fill.Symbol,
            Side = state.EntrySide,
            Quantity = state.EntryQuantity,
            EntryTime = state.EntryTime,
            EntryPrice = state.EntryPrice,
            ExitTime = fill.Timestamp,
            ExitPrice = fill.FillPrice,
            GrossPnL = after.RealizedGrossPnL - state.BaselineGross,
            Fees = after.Fees.TotalFees - state.BaselineFees,
            NetPnL = after.RealizedNetPnL - state.BaselineNet
        };
        state.IsOpen = false;
        _trades.Add(trade);
        TradeClosed?.Invoke(trade);
    }

    private sealed class OpenState
    {
        public bool IsOpen;
        public PositionSide EntrySide;
        public DateTimeOffset EntryTime;
        public decimal EntryPrice;
        public int EntryQuantity;
        public decimal BaselineGross;
        public decimal BaselineNet;
        public decimal BaselineFees;
    }
}
