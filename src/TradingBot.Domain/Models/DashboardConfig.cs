namespace TradingBot.Domain.Models;

/// <summary>Einstellungen für das (spätere) Dashboard.</summary>
public sealed record DashboardConfig
{
    public bool Enabled { get; init; }
    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 5005;
    public int RefreshIntervalMs { get; init; } = 1000;
    public bool ReadOnly { get; init; } = true;
    public bool EnableTickChart { get; init; } = true;
    public int MaxTickPoints { get; init; } = 2000;
    public string ExternalProjectRepository { get; init; } = "https://github.com/HKUDS/Vibe-Trading";
}
