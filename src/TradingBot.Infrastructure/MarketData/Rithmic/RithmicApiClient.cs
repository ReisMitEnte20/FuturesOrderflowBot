using System.Net.Http.Json;
using System.Text.Json;
using TradingBot.Infrastructure.MarketData.Rithmic.Models;

namespace TradingBot.Infrastructure.MarketData.Rithmic;

public sealed class RithmicApiClient
{
    private readonly HttpClient _http;
    private readonly RithmicConfig _config;
    private string? _accessToken;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;

    public RithmicApiClient(RithmicConfig config)
    {
        _config = config;
        _http = new HttpClient { BaseAddress = new Uri(config.BaseUrl), Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds) };
    }

    public async Task<RithmicCandleResponse> GetCandlesAsync(
        string symbol, string interval, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var url = $"/api/v1/bars?symbol={Uri.EscapeDataString(symbol)}&interval={Uri.EscapeDataString(interval)}"
                + $"&from={toUnixMs(from)}&to={toUnixMs(to)}";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken!);

        var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new RithmicException($"Rithmic API error: {response.StatusCode}", (int)response.StatusCode);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var result = JsonSerializer.Deserialize<RithmicCandleResponse>(json, options);
        return result ?? new RithmicCandleResponse();
    }

    public async Task<List<RithmicTick>> GetTicksAsync(
        string symbol, DateTimeOffset from, DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);

        var url = $"/api/v1/ticks?symbol={Uri.EscapeDataString(symbol)}"
                + $"&from={toUnixMs(from)}&to={toUnixMs(to)}";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken!);

        var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new RithmicException($"Rithmic API error: {response.StatusCode}", (int)response.StatusCode);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var result = JsonSerializer.Deserialize<RithmicTick[]>(json, options);
        return (result ?? []).ToList();
    }

    private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_accessToken) && DateTimeOffset.UtcNow < _tokenExpiresAt)
            return;

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/token");
        request.Content = JsonContent.Create(new
        {
            apiKey = _config.ApiKey,
            apiSecret = _config.ApiSecret,
        });

        var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new RithmicException($"Rithmic auth failed: {response.StatusCode}", (int)response.StatusCode);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var tokenResponse = JsonSerializer.Deserialize<JsonElement>(json, options);
        _accessToken = tokenResponse.GetProperty("accessToken").GetString();
        var expiresIn = tokenResponse.TryGetProperty("expiresIn", out var exp) ? exp.GetInt32() : 3600;
        _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60);
    }

    private static long toUnixMs(DateTimeOffset dt) => dt.ToUnixTimeMilliseconds();
}