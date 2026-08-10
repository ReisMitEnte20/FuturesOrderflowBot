using System.Text.Json;
using TradingBot.Core.Interfaces;

namespace TradingBot.Infrastructure.ExternalApis;

/// <summary>
/// HTTP-based Hindsight API client.
/// Communicates with Hindsight API server for trading analysis and insights.
/// </summary>
public class HindsightClient : IHindsightClient
{
    private readonly HttpClient _httpClient;
    private readonly string _apiBaseUrl;
    private readonly string _apiKey;
    private readonly ILogger<HindsightClient> _logger;
    private readonly JsonSerializerOptions _jsonOptions;

    public HindsightClient(
        HttpClient httpClient,
        string apiBaseUrl,
        string apiKey,
        ILogger<HindsightClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiBaseUrl = apiBaseUrl?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(apiBaseUrl));
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        // Set default headers
        _httpClient.DefaultRequestHeaders.Add("X-API-Key", _apiKey);
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "TradingBot/1.0");
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<HindsightAnalysis?> GetHistoricalAnalysisAsync(
        string instrumentCode,
        DateTimeOffset from,
        DateTimeOffset to,
        string analysisType = "default",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instrumentCode);

        try
        {
            var url = $"{_apiBaseUrl}/analysis/historical?instrument={instrumentCode}&from={from:O}&to={to:O}&type={analysisType}";
            
            _logger.LogInformation("Fetching historical analysis from Hindsight: {Instrument}", instrumentCode);

            using var response = await _httpClient.GetAsync(url, cancellationToken);
            
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning("No analysis found for {Instrument}", instrumentCode);
                return null;
            }

            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<HindsightAnalysis>(content, _jsonOptions);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to fetch historical analysis from Hindsight");
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to deserialize Hindsight response");
            return null;
        }
    }

    public async Task<HindsightSignal?> GetCurrentSignalAsync(
        string instrumentCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instrumentCode);

        try
        {
            var url = $"{_apiBaseUrl}/signal/current?instrument={instrumentCode}";
            
            _logger.LogInformation("Fetching current signal from Hindsight: {Instrument}", instrumentCode);

            using var response = await _httpClient.GetAsync(url, cancellationToken);
            
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning("No signal available for {Instrument}", instrumentCode);
                return null;
            }

            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<HindsightSignal>(content, _jsonOptions);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to fetch signal from Hindsight");
            return null;
        }
    }

    public async Task<HindsightPattern?> RecognizePatternAsync(
        string instrumentCode,
        decimal[] prices,
        long[] volumes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instrumentCode);
        ArgumentNullException.ThrowIfNull(prices);
        ArgumentNullException.ThrowIfNull(volumes);

        if (prices.Length == 0 || volumes.Length == 0 || prices.Length != volumes.Length)
            throw new ArgumentException("Prices and volumes arrays must have matching non-zero length");

        try
        {
            var url = $"{_apiBaseUrl}/pattern/recognize";
            
            var payload = new
            {
                instrument = instrumentCode,
                prices = prices,
                volumes = volumes
            };

            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

            _logger.LogInformation("Sending pattern recognition request to Hindsight: {Instrument}", instrumentCode);

            using var response = await _httpClient.PostAsync(url, content, cancellationToken);
            response.EnsureSuccessStatusCode();

            var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<HindsightPattern>(responseContent, _jsonOptions);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to recognize pattern with Hindsight");
            return null;
        }
    }

    public async Task<HindsightBacktestComparison?> GetBacktestComparisonAsync(
        string instrumentCode,
        string strategyName,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instrumentCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyName);

        try
        {
            var url = $"{_apiBaseUrl}/backtest/compare?instrument={instrumentCode}&strategy={strategyName}&from={from:O}&to={to:O}";
            
            _logger.LogInformation("Fetching backtest comparison from Hindsight: {Instrument} - {Strategy}", 
                instrumentCode, strategyName);

            using var response = await _httpClient.GetAsync(url, cancellationToken);
            
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning("No backtest comparison found for {Instrument} - {Strategy}", 
                    instrumentCode, strategyName);
                return null;
            }

            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<HindsightBacktestComparison>(content, _jsonOptions);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to fetch backtest comparison from Hindsight");
            return null;
        }
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{_apiBaseUrl}/health";
            
            _logger.LogInformation("Testing connection to Hindsight API");

            using var response = await _httpClient.GetAsync(url, cancellationToken);
            var isSuccess = response.IsSuccessStatusCode;
            
            if (isSuccess)
            {
                _logger.LogInformation("Hindsight API connection successful");
            }
            else
            {
                _logger.LogWarning("Hindsight API returned status {StatusCode}", response.StatusCode);
            }

            return isSuccess;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to test connection to Hindsight API");
            return false;
        }
    }

    public async Task<HindsightStatus?> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"{_apiBaseUrl}/status";
            
            _logger.LogInformation("Fetching Hindsight API status");

            using var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonSerializer.Deserialize<HindsightStatus>(content, _jsonOptions);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to fetch Hindsight API status");
            return null;
        }
    }
}
