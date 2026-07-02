using TradingBot.Core.Interfaces;
using TradingBot.Domain.Models;
using TradingBot.PaperTrading.Positions;

namespace TradingBot.PaperTrading.Risk;

/// <summary>
/// Führt den DailyRiskState während der Paper-Session aus abgeschlossenen Trades fort
/// (pro Handelstag). Dadurch greifen Max Daily Loss / Max Trades / Verlustserie AUCH im
/// Paper Mode – Paper darf nicht "lockerer" sein als später Live.
/// Hinweis: OrdersThisMinute wird (wie im Backtest) nicht dynamisch getrackt.
/// </summary>
public sealed class PaperDailyStateProvider : IDailyRiskStateProvider
{
    private readonly decimal _maxDailyLoss;
    private readonly Dictionary<DateOnly, DailyRiskState> _byDate = new();
    private readonly object _sync = new();

    public PaperDailyStateProvider(decimal maxDailyLoss) => _maxDailyLoss = maxDailyLoss;

    public DailyRiskState GetCurrent(DateOnly date)
    {
        lock (_sync)
            return _byDate.TryGetValue(date, out var s) ? s : DailyRiskState.Start(date);
    }

    /// <summary>Verbucht einen abgeschlossenen Trade auf seinem Exit-Handelstag.</summary>
    public void ApplyTrade(PaperClosedTrade trade)
    {
        lock (_sync)
        {
            var date = DateOnly.FromDateTime(trade.ExitTime.UtcDateTime);
            var s = _byDate.TryGetValue(date, out var existing) ? existing : DailyRiskState.Start(date);

            decimal net = s.NetPnL + trade.NetPnL;
            decimal peak = Math.Max(s.PeakNetPnL, net);
            int consec = trade.IsLoser ? s.ConsecutiveLosses + 1 : (trade.IsWinner ? 0 : s.ConsecutiveLosses);

            _byDate[date] = s with
            {
                GrossPnL = s.GrossPnL + trade.GrossPnL,
                NetPnL = net,
                RealizedPnL = net,
                TradesTaken = s.TradesTaken + 1,
                WinningTrades = s.WinningTrades + (trade.IsWinner ? 1 : 0),
                LosingTrades = s.LosingTrades + (trade.IsLoser ? 1 : 0),
                ConsecutiveLosses = consec,
                PeakNetPnL = peak,
                IsDailyLossLimitHit = net <= -_maxDailyLoss
            };
        }
    }
}
