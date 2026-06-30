using TradingBot.Core.Interfaces;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.Config;

/// <summary>
/// Lädt alle FeeProfile aus einem Verzeichnis (*.json), validiert sie und indiziert
/// sie nach Broker + ExecutionProvider + Instrument. Lazy + thread-safe geladen.
/// </summary>
public sealed class FeeProfileProvider : IFeeProfileProvider
{
    private readonly IConfigService _config;
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, FeeProfile>? _cache;

    public FeeProfileProvider(IConfigService config, string directory)
    {
        _config = config;
        _directory = directory;
    }

    private static string Key(string broker, string provider, string instrument)
        => $"{broker}|{provider}|{instrument}";

    public async Task<FeeProfile?> GetAsync(
        string brokerName, string executionProvider, string instrument,
        CancellationToken cancellationToken = default)
    {
        var cache = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return cache.GetValueOrDefault(Key(brokerName, executionProvider, instrument));
    }

    public async Task<IReadOnlyCollection<FeeProfile>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var cache = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return cache.Values.ToList();
    }

    private async Task<Dictionary<string, FeeProfile>> EnsureLoadedAsync(CancellationToken ct)
    {
        if (_cache is not null) return _cache;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache is not null) return _cache;
            if (!Directory.Exists(_directory))
                throw new ConfigFileNotFoundException(_directory);

            var cache = new Dictionary<string, FeeProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
            {
                var profile = await _config.LoadAsync<FeeProfile>(file, ct).ConfigureAwait(false);
                ProfileValidator.Validate(profile);
                cache[Key(profile.BrokerName, profile.ExecutionProvider, profile.Instrument)] = profile;
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
