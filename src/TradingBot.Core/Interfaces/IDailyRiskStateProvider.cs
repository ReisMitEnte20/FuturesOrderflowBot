using TradingBot.Domain.Models;

namespace TradingBot.Core.Interfaces;

/// <summary>Liefert den aktuellen Tages-Risikozustand. Wird vom OrderContextProvider genutzt.</summary>
public interface IDailyRiskStateProvider
{
    DailyRiskState GetCurrent(DateOnly date);
}
