using TradingBot.Core.Interfaces;

namespace TradingBot.Backtesting.Risk;

/// <summary>
/// Kill Switch für Backtests. Standard: inaktiv. <see cref="EmergencyFlattenAsync"/> ist ein No-op –
/// im Backtest gibt es keine echten Positionen zum Glattstellen über eine Live-Verbindung.
/// </summary>
public sealed class BacktestKillSwitch : IKillSwitchService
{
    public BacktestKillSwitch(bool active = false) => IsActive = active;

    public bool IsActive { get; private set; }
    public event EventHandler<string>? Activated;

    public Task ActivateAsync(string reason, CancellationToken cancellationToken = default)
    {
        IsActive = true;
        Activated?.Invoke(this, reason);
        return Task.CompletedTask;
    }

    public Task DeactivateAsync(CancellationToken cancellationToken = default)
    {
        IsActive = false;
        return Task.CompletedTask;
    }

    public Task EmergencyFlattenAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}
