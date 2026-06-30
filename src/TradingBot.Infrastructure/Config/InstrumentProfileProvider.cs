using TradingBot.Core.Interfaces;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.Config;

/// <summary>
/// Lädt alle InstrumentProfile aus einem Verzeichnis (*.json), validiert sie und
/// indiziert sie nach Symbol. Lazy + thread-safe geladen.
/// </summary>
public sealed class InstrumentProfileProvider : IInstrumentProvider
{
    private readonly IConfigService _config;
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, InstrumentProfile>? _cache;

    public InstrumentProfileProvider(IConfigService config, string directory)
    {
        _config = config;
        _directory = directory;
    }

    public async Task<InstrumentProfile?> GetAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var cache = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return cache.GetValueOrDefault(symbol);
    }

    public async Task<IReadOnlyCollection<InstrumentProfile>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var cache = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return cache.Values.ToList();
    }

    private async Task<Dictionary<string, InstrumentProfile>> EnsureLoadedAsync(CancellationToken ct)
    {
        if (_cache is not null) return _cache;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache is not null) return _cache;
            if (!Directory.Exists(_directory))
                throw new ConfigFileNotFoundException(_directory);

            var cache = new Dictionary<string, InstrumentProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
            {
                var profile = await _config.LoadAsync<InstrumentProfile>(file, ct).ConfigureAwait(false);
                ProfileValidator.Validate(profile);
                cache[profile.Symbol] = profile;
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
