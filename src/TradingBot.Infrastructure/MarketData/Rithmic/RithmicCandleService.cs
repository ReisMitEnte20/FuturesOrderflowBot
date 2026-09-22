using TradingBot.Infrastructure.MarketData.Rithmic.Models;

namespace TradingBot.Infrastructure.MarketData.Rithmic;

public sealed class RithmicCandleService
{
    private readonly RithmicApiClient _apiClient;

    public RithmicCandleService(RithmicConfig config)
    {
        _apiClient = new RithmicApiClient(config);
    }

    public async Task<List<RithmicCandle>> GetCandlesAsync(
        string symbol, string interval, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        var response = await _apiClient.GetCandlesAsync(symbol, interval, from, to, cancellationToken);
        if (!response.Success)
            throw new RithmicException(response.Error ?? "Failed to fetch candles");
        return response.Data;
    }

    public async Task<List<RithmicTick>> GetTicksAsync(
        string symbol, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        return await _apiClient.GetTicksAsync(symbol, from, to, cancellationToken);
    }
}