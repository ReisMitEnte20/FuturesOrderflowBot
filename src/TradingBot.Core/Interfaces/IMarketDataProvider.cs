using TradingBot.Domain.Models;

namespace TradingBot.Core.Interfaces;

/// <summary>
/// Liefert Marktdaten (Replay/Historie/später Live). Implementierungen müssen einen
/// Verbindungsabbruch erkennbar machen, damit der RiskManager bei Feed-Verlust blockt.
/// </summary>
public interface IMarketDataProvider
{
    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Asynchroner Tick-Stream für ein Symbol.</summary>
    IAsyncEnumerable<MarketTick> SubscribeTicksAsync(string symbol, CancellationToken cancellationToken = default);
}
