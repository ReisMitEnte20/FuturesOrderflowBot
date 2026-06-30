using TradingBot.Core.Interfaces;
using TradingBot.Domain.Models;

namespace TradingBot.Infrastructure.MarketData;

/// <summary>
/// Bequemer <see cref="IMarketDataProvider"/>, der Ticks aus einer CSV lädt
/// (<see cref="CsvTickReader"/>) und über einen <see cref="ReplayMarketDataProvider"/> abspielt.
/// Keine Live-Anbindung, keine Order-Ausführung.
/// </summary>
public sealed class CsvMarketDataProvider : IMarketDataProvider
{
    private readonly ReplayMarketDataProvider _replay;

    public CsvMarketDataProvider(string csvPath, ReplayOptions? options = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var ticks = CsvTickReader.ReadFile(csvPath);
        _replay = new ReplayMarketDataProvider(ticks, options, delay);
    }

    public bool IsConnected => _replay.IsConnected;
    public Task ConnectAsync(CancellationToken cancellationToken = default) => _replay.ConnectAsync(cancellationToken);
    public Task DisconnectAsync(CancellationToken cancellationToken = default) => _replay.DisconnectAsync(cancellationToken);
    public IAsyncEnumerable<MarketTick> SubscribeTicksAsync(string symbol, CancellationToken cancellationToken = default)
        => _replay.SubscribeTicksAsync(symbol, cancellationToken);

    public void Pause() => _replay.Pause();
    public void Resume() => _replay.Resume();
}
