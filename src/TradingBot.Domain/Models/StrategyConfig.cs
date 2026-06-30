namespace TradingBot.Domain.Models;

/// <summary>
/// Konfiguration einer Strategie-Instanz. Parameter bleiben generisch (Key/Value),
/// damit neue Orderflow-Strategien ohne Modelländerung konfigurierbar sind.
/// </summary>
public sealed record StrategyConfig
{
    public required string Name { get; init; }
    public required string Symbol { get; init; }
    public bool Enabled { get; init; }

    /// <summary>Strategie-spezifische Parameter (z. B. "MinDelta" -&gt; "500").</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; }
        = new Dictionary<string, string>();
}
