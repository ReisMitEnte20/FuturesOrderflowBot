using TradingBot.Core.Interfaces;
using TradingBot.Domain.Models;

namespace TradingBot.Backtesting.Risk;

/// <summary>
/// Führt den DailyRiskState während des Backtests aus abgeschlossenen Trades fort (pro Handelstag).
/// Dadurch kann der RiskManager dynamische Regeln wie Max Daily Loss / Max Trades / Verlustserie
/// AUCH im Backtest durchsetzen – realistischer als ein statischer Nullzustand.
///
/// Hinweis: OrdersThisMinute wird hier nicht getrackt (Order-pro-Minute-Limit im Backtest nicht dynamisch).
/// </summary>
public sealed class BacktestDailyStateProvider : IDailyRiskStateProvider
{
    private readonly decimal _maxDailyLoss;
    private readonly Dictionary<DateOnly, DailyRiskState> _byDate = new();

    public BacktestDailyStateProvider(decimal maxDailyLoss) => _maxDailyLoss = maxDailyLoss;

    public DailyRiskState GetCurrent(DateOnly date)
        => _byDate.TryGetValue(date, out var s) ? s : DailyRiskState.Start(date);

    /// <summary>Verbucht einen abgeschlossenen Trade auf seinem Exit-Handelstag.</summary>
    public void ApplyTrade(BacktestTrade trade)
    {
        var date = DateOnly.FromDateTime(trade.ExitTime.UtcDateTime);
        var s = GetCurrent(date);

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
