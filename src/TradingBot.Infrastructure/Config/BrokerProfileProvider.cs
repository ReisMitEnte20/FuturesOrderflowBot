using TradingBot.Core.Interfaces;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.Config;

/// <summary>
/// Lädt alle BrokerProfile aus einem Verzeichnis (*.json), validiert sie und
/// indiziert sie nach BrokerName. Lazy + thread-safe geladen.
/// </summary>
public sealed class BrokerProfileProvider : IBrokerProfileProvider
{
    private readonly IConfigService _config;
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, BrokerProfile>? _cache;

    public BrokerProfileProvider(IConfigService config, string directory)
    {
        _config = config;
        _directory = directory;
    }

    public async Task<BrokerProfile?> GetAsync(string brokerName, CancellationToken cancellationToken = default)
    {
        var cache = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return cache.GetValueOrDefault(brokerName);
    }

    public async Task<IReadOnlyCollection<BrokerProfile>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var cache = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return cache.Values.ToList();
    }

    private async Task<Dictionary<string, BrokerProfile>> EnsureLoadedAsync(CancellationToken ct)
    {
        if (_cache is not null) return _cache;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache is not null) return _cache;
            if (!Directory.Exists(_directory))
                throw new ConfigFileNotFoundException(_directory);

            var cache = new Dictionary<string, BrokerProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
            {
                var profile = await _config.LoadAsync<BrokerProfile>(file, ct).ConfigureAwait(false);
                ProfileValidator.Validate(profile);
                cache[profile.BrokerName] = profile;
            }

            _cache = cache;
            return _cache;
        }
        finally
        {
            _gate.Release();
        }
    }
}
