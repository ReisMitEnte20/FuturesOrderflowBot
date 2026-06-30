namespace TradingBot.Domain.Models;

/// <summary>Einstellungen für das (spätere) Dashboard.</summary>
public sealed record DashboardConfig
{
    public bool Enabled { get; init; }
    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 5005;
    public int RefreshIntervalMs { get; init; } = 1000;
}
