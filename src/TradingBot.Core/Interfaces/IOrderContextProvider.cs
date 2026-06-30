using TradingBot.Domain.Models;

namespace TradingBot.Core.Interfaces;

/// <summary>
/// Stellt den vollständigen Risk-Kontext für ein Signal zusammen (Profile, Tageszustand,
/// offene Positionen). Trennt das Auflösen von Zustand von der OrderManager-Orchestrierung
/// und macht den OrderManager voll testbar.
/// </summary>
public interface IOrderContextProvider
{
    Task<RiskEvaluationRequest> BuildAsync(
        TradeSignal signal, int requestedContracts, CancellationToken cancellationToken = default);
}
