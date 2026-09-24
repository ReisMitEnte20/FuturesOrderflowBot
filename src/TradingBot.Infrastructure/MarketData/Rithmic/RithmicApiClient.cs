using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TradingBot.Infrastructure.MarketData.Rithmic.Models;

namespace TradingBot.Infrastructure.MarketData.Rithmic;

public sealed class RithmicApiClient
{
    private readonly HttpClient _http;
    private readonly RithmicConfig _config;

    public RithmicApiClient(RithmicConfig config)
    {
        _config = config;
        _http = new HttpClient { BaseAddress = new Uri(config.BaseUrl), Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds) };
    }

    public async Task<RithmicCandleResponse> GetCandlesAsync(
        string symbol, string interval, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/bars?symbol={Uri.EscapeDataString(symbol)}&interval={Uri.EscapeDataString(interval)}"
            + $"&from={toUnixMs(from)}&to={toUnixMs(to)}");

        SetAuthHeader(request);

        var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new RithmicException($"Rithmic API error: {response.StatusCode}", (int)response.StatusCode);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<RithmicCandleResponse>(json, options) ?? new RithmicCandleResponse();
    }

    public async Task<List<RithmicTick>> GetTicksAsync(
        string symbol, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/v1/ticks?symbol={Uri.EscapeDataString(symbol)}"
            + $"&from={toUnixMs(from)}&to={toUnixMs(to)}");

        SetAuthHeader(request);

        var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new RithmicException($"Rithmic API error: {response.StatusCode}", (int)response.StatusCode);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return (JsonSerializer.Deserialize<RithmicTick[]>(json, options) ?? []).ToList();
    }

    private void SetAuthHeader(HttpRequestMessage request)
    {
        var credentials = Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes($"{_config.Username}:{_config.Password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
    }

    private static long toUnixMs(DateTimeOffset dt) => dt.ToUnixTimeMilliseconds();
}