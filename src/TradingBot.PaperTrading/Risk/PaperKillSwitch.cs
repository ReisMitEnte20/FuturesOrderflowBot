using TradingBot.Core.Interfaces;

namespace TradingBot.PaperTrading.Risk;

/// <summary>
/// Kill Switch für Paper-Sessions. Standard: inaktiv. <see cref="EmergencyFlattenAsync"/> ist
/// derzeit ein No-op – das echte Emergency Flatten (inkl. Live) kommt in einer späteren Phase.
/// </summary>
public sealed class PaperKillSwitch : IKillSwitchService
{
    public PaperKillSwitch(bool active = false) => IsActive = active;

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
