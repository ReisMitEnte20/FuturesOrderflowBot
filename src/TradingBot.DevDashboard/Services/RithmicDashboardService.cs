using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TradingBot.Infrastructure.MarketData.Rithmic;
using TradingBot.Infrastructure.MarketData.Rithmic.Models;

namespace TradingBot.DevDashboard.Services;

public sealed class RithmicDashboardService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<RithmicDashboardService>? _logger;
    private RithmicConfig? _currentConfig;
    private bool _isConnected;
    private string? _lastError;

    public RithmicDashboardService(ILogger<RithmicDashboardService>? logger = null)
    {
        _httpClient = new HttpClient();
        _logger = logger;
        _isConnected = false;
    }

    public bool IsConnected => _isConnected;
    public string? LastError => _lastError;
    public RithmicConfig? CurrentConfig => _currentConfig;

    public async Task<RithmicConnectResult> ConnectAsync(RithmicConnectRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            _lastError = null;

            var config = new RithmicConfig
            {
                Username = request.UserId,
                Password = request.Password,
                BaseUrl = GetGatewayBaseUrl(request.System, request.Gateway),
                TimeoutSeconds = 30,
            };

            _currentConfig = config;

            using var client = new HttpClient
            {
                BaseAddress = new Uri(config.BaseUrl),
                Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
            };

            var testRequest = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health");
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.Username}:{config.Password}"));
            testRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

            var response = await client.SendAsync(testRequest, cancellationToken);

            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _isConnected = true;
                _logger?.LogInformation("Rithmic connected to {Gateway} ({System})", request.Gateway, request.System);
                return new RithmicConnectResult(true, "Connected successfully", config.BaseUrl);
            }

            _isConnected = false;
            var error = $"Connection failed: {response.StatusCode}";
            _lastError = error;
            _logger?.LogWarning("Rithmic connection failed: {Error}", error);
            return new RithmicConnectResult(false, error, config.BaseUrl);
        }
        catch (Exception ex)
        {
            _isConnected = false;
            _lastError = ex.Message;
            _logger?.LogError(ex, "Rithmic connection error");
            return new RithmicConnectResult(false, ex.Message, _currentConfig?.BaseUrl);
        }
    }

    public Task<RithmicDisconnectResult> DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _isConnected = false;
        _currentConfig = null;
        _logger?.LogInformation("Rithmic disconnected");
        return Task.FromResult(new RithmicDisconnectResult(true, "Disconnected"));
    }

    public RithmicStatusResult GetStatus()
    {
        return new RithmicStatusResult(
            _isConnected,
            _currentConfig?.Username,
            _currentConfig?.BaseUrl,
            _lastError
        );
    }

    private static string GetGatewayBaseUrl(string system, string gateway)
    {
        return (system, gateway.ToLowerInvariant()) switch
        {
            ("LucidTrading", "chicago") => "https://api.lucidtrading.com",
            ("LucidTrading", "new york") => "https://api-ny.lucidtrading.com",
            ("LucidTrading", "london") => "https://api-lon.lucidtrading.com",
            ("LucidTrading", "frankfurt") => "https://api-fra.lucidtrading.com",
            ("LucidTrading", "tokyo") => "https://api-tyo.lucidtrading.com",
            ("LucidTrading", "singapore") => "https://api-sin.lucidtrading.com",
            ("LucidTrading", "sydney") => "https://api-syd.lucidtrading.com",
            ("Rithmic", "chicago") => "https://api.rithmic.com",
            ("Rithmic", "new york") => "https://api-ny.rithmic.com",
            ("Rithmic", "london") => "https://api-lon.rithmic.com",
            ("Rithmic", "frankfurt") => "https://api-fra.rithmic.com",
            ("Rithmic", "tokyo") => "https://api-tyo.rithmic.com",
            ("Rithmic", "singapore") => "https://api-sin.rithmic.com",
            ("Rithmic", "sydney") => "https://api-syd.rithmic.com",
            ("Rithmic Paper Trading", _) => "https://api-demo.rithmic.com",
            ("Rithmic Mock Trading", _) => "https://api-mock.rithmic.com",
            _ => "https://api.rithmic.com"
        };
    }
}

public record RithmicConnectRequest(
    string UserId,
    string Password,
    string System,
    string Gateway
);

public record RithmicConnectResult(
    bool Success,
    string Message,
    string GatewayUrl
);

public record RithmicDisconnectResult(
    bool Success,
    string Message
);

public record RithmicStatusResult(
    bool IsConnected,
    string? Username,
    string? GatewayUrl,
    string? LastError
);