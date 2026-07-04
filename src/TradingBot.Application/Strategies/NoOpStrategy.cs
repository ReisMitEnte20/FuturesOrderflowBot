using TradingBot.Core.Interfaces;

namespace TradingBot.Application.Strategies;

/// <summary>
/// Strategie, die NIEMALS ein Signal erzeugt (alle Handler = Interface-Defaults).
/// Dient als Platzhalter und für Infrastruktur-Tests.
/// </summary>
public sealed class NoOpStrategy : IStrategy
{
    public string Name => "NoOpStrategy";
}
