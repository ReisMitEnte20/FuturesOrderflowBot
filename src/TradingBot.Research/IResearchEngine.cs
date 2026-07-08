namespace TradingBot.Research;

/// <summary>
/// Vergleicht mehrere Strategie-Kandidaten über Backtests, Monte Carlo und Robustheitsprüfung
/// und rankt sie. Nutzt ausschließlich die bestehende BacktestEngine (über den Runner),
/// tradet niemals live und sendet keine Orders.
/// </summary>
public interface IResearchEngine
{
    Task<ResearchResult> RunAsync(ResearchRequest request, CancellationToken cancellationToken = default);
}
