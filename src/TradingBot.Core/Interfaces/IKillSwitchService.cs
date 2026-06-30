namespace TradingBot.Core.Interfaces;

/// <summary>
/// Harter Notaus. Bei aktivem Kill Switch lehnt der RiskManager jede neue Order ab.
/// <see cref="EmergencyFlattenAsync"/> schließt alle offenen Positionen sofort.
/// </summary>
public interface IKillSwitchService
{
    bool IsActive { get; }

    /// <summary>Aktiviert den Kill Switch mit Begründung (z. B. "Max Daily Loss").</summary>
    Task ActivateAsync(string reason, CancellationToken cancellationToken = default);

    Task DeactivateAsync(CancellationToken cancellationToken = default);

    /// <summary>Schließt alle offenen Positionen sofort (Emergency Flatten).</summary>
    Task EmergencyFlattenAsync(CancellationToken cancellationToken = default);

    /// <summary>Wird bei Aktivierung ausgelöst (mit Begründung).</summary>
    event EventHandler<string>? Activated;
}
