using TradingBot.Core.Interfaces;
using TradingBot.Domain.Models;

namespace TradingBot.Application.Strategies;

/// <summary>
/// Thread-safe Registry für Strategien. Erzwingt eindeutige Namen und verwaltet den
/// Aktiv-Status. Enthält KEINE Trading-Logik und keinerlei Order-/Broker-Referenzen.
/// </summary>
public sealed class StrategyRegistry : IStrategyRegistry
{
    private readonly object _sync = new();
    private readonly List<StrategyDescriptor> _ordered = new();
    private readonly Dictionary<string, StrategyDescriptor> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _enabled = new(StringComparer.OrdinalIgnoreCase);

    public void Register(IStrategy strategy, StrategyConfig config)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(config);
        if (string.IsNullOrWhiteSpace(config.Name))
            throw new ArgumentException("StrategyConfig.Name darf nicht leer sein.", nameof(config));

        lock (_sync)
        {
            if (_byName.ContainsKey(config.Name))
                throw new InvalidOperationException($"Strategie '{config.Name}' ist bereits registriert.");

            var descriptor = new StrategyDescriptor { Strategy = strategy, Config = config };
            _ordered.Add(descriptor);
            _byName[config.Name] = descriptor;
            _enabled[config.Name] = config.Enabled;
        }
    }

    public bool Enable(string name) => SetEnabled(name, true);
    public bool Disable(string name) => SetEnabled(name, false);

    public bool IsEnabled(string name)
    {
        lock (_sync) return _enabled.TryGetValue(name, out var on) && on;
    }

    public StrategyDescriptor? Get(string name)
    {
        lock (_sync) return _byName.GetValueOrDefault(name);
    }

    public IReadOnlyList<StrategyDescriptor> All
    {
        get { lock (_sync) return _ordered.ToList(); }
    }

    private bool SetEnabled(string name, bool value)
    {
        lock (_sync)
        {
            if (!_byName.ContainsKey(name)) return false;
            _enabled[name] = value;
            return true;
        }
    }
}
