using TradingBot.Domain.Models;
using TradingBot.Infrastructure.Config;

namespace TradingBot.DevDashboard.Services;

/// <summary>
/// Lädt die JSON-Profile READ-ONLY zur reinen Anzeige (Broker/Instrument/Fee/Risk).
/// Bearbeitet NICHTS. Liest ausschließlich den config/-Ordner – keine Secrets, keine .env.
/// </summary>
public sealed class ConfigOverviewService
{
    private readonly string? _root;

    public ConfigOverviewService(string? root) => _root = root;

    public async Task<ConfigOverview> LoadAsync()
    {
        if (_root is null)
            return new ConfigOverview { Error = "Repo-Wurzel (TradingBot.sln) nicht gefunden." };

        var configDir = Path.Combine(_root, "config");
        if (!Directory.Exists(configDir))
            return new ConfigOverview { ConfigPath = configDir, Error = "config-Verzeichnis nicht gefunden." };

        try
        {
            var cfg = new JsonConfigService();

            var brokers = await LoadDir(Path.Combine(configDir, "brokers"),
                d => new BrokerProfileProvider(cfg, d).GetAllAsync());
            var instruments = await LoadDir(Path.Combine(configDir, "instruments"),
                d => new InstrumentProfileProvider(cfg, d).GetAllAsync());
            var fees = await LoadDir(Path.Combine(configDir, "fees"),
                d => new FeeProfileProvider(cfg, d).GetAllAsync());

            var risks = new List<RiskConfig>();
            var riskDir = Path.Combine(configDir, "risk");
            if (Directory.Exists(riskDir))
                foreach (var file in Directory.EnumerateFiles(riskDir, "*.json"))
                    risks.Add(await cfg.LoadAsync<RiskConfig>(file));

            return new ConfigOverview
            {
                ConfigPath = configDir,
                Brokers = brokers.ToList(),
                Instruments = instruments.ToList(),
                Fees = fees.ToList(),
                Risks = risks
            };
        }
        catch (Exception ex)
        {
            return new ConfigOverview { ConfigPath = configDir, Error = ex.Message };
        }
    }

    private static async Task<IReadOnlyCollection<T>> LoadDir<T>(
        string dir, Func<string, Task<IReadOnlyCollection<T>>> load)
        => Directory.Exists(dir) ? await load(dir) : Array.Empty<T>();
}

public sealed record ConfigOverview
{
    public string ConfigPath { get; init; } = string.Empty;
    public string? Error { get; init; }
    public IReadOnlyList<BrokerProfile> Brokers { get; init; } = Array.Empty<BrokerProfile>();
    public IReadOnlyList<InstrumentProfile> Instruments { get; init; } = Array.Empty<InstrumentProfile>();
    public IReadOnlyList<FeeProfile> Fees { get; init; } = Array.Empty<FeeProfile>();
    public IReadOnlyList<RiskConfig> Risks { get; init; } = Array.Empty<RiskConfig>();
}
