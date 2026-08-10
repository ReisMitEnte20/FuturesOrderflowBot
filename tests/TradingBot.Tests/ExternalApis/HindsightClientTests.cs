using TradingBot.Core.Interfaces;

namespace TradingBot.Tests.ExternalApis;

/// <summary>
/// Unit tests for Hindsight API client integration.
/// </summary>
public class HindsightClientTests
{
    private readonly Mock<ILogger<HindsightClient>> _mockLogger;
    private readonly HttpClient _httpClient;
    private readonly HindsightClient _hindsightClient;
    private const string ApiBaseUrl = "https://api.hindsight.example.com/v1";
    private const string ApiKey = "test-api-key";

    public HindsightClientTests()
    {
        _mockLogger = new Mock<ILogger<HindsightClient>>();
        _httpClient = new HttpClient();
        _hindsightClient = new HindsightClient(_httpClient, ApiBaseUrl, ApiKey, _mockLogger.Object);
    }

    [Fact]
    public void Constructor_ValidatesInputs()
    {
        // Arrange & Act & Assert
        Assert.Throws<ArgumentNullException>(() => 
            new HindsightClient(null!, ApiBaseUrl, ApiKey, _mockLogger.Object));
        
        Assert.Throws<ArgumentNullException>(() => 
            new HindsightClient(_httpClient, null!, ApiKey, _mockLogger.Object));
        
        Assert.Throws<ArgumentNullException>(() => 
            new HindsightClient(_httpClient, ApiBaseUrl, null!, _mockLogger.Object));
        
        Assert.Throws<ArgumentNullException>(() => 
            new HindsightClient(_httpClient, ApiBaseUrl, ApiKey, null!));
    }

    [Fact]
    public async Task GetCurrentSignalAsync_ValidInstrument_ReturnsSignal()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler();
        var expectedSignal = new HindsightSignal
        {
            InstrumentCode = "NQ",
            SignalType = "buy",
            Confidence = 0.87m,
            TargetPrice = 18750.00m,
            StopLoss = 18100.00m
        };

        mockHandler.Expect(HttpMethod.Get, $"{ApiBaseUrl}/signal/current?instrument=NQ")
            .Respond(new StringContent(
                JsonSerializer.Serialize(expectedSignal),
                Encoding.UTF8,
                "application/json"));

        var client = new HttpClient(mockHandler);
        var hindsightClient = new HindsightClient(client, ApiBaseUrl, ApiKey, _mockLogger.Object);

        // Act
        var result = await hindsightClient.GetCurrentSignalAsync("NQ");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("NQ", result.InstrumentCode);
        Assert.Equal("buy", result.SignalType);
        Assert.Equal(0.87m, result.Confidence);
    }

    [Fact]
    public async Task GetCurrentSignalAsync_InvalidInstrument_ReturnsNull()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.Expect(HttpMethod.Get, $"{ApiBaseUrl}/signal/current?instrument=INVALID")
            .Respond(System.Net.HttpStatusCode.NotFound);

        var client = new HttpClient(mockHandler);
        var hindsightClient = new HindsightClient(client, ApiBaseUrl, ApiKey, _mockLogger.Object);

        // Act
        var result = await hindsightClient.GetCurrentSignalAsync("INVALID");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetHistoricalAnalysisAsync_ValidPeriod_ReturnsAnalysis()
    {
        // Arrange
        var from = DateTimeOffset.Parse("2024-01-01T00:00:00Z");
        var to = DateTimeOffset.Parse("2024-12-31T23:59:59Z");

        var mockHandler = new MockHttpMessageHandler();
        var expectedAnalysis = new HindsightAnalysis
        {
            InstrumentCode = "NQ",
            AnalysisType = "default",
            Trend = "uptrend",
            Volatility = 0.2345m,
            Momentum = 0.8750m,
            HighPrice = 18750.50m,
            LowPrice = 14200.25m
        };

        mockHandler.Expect(HttpMethod.Get, $"{ApiBaseUrl}/analysis/historical*")
            .Respond(new StringContent(
                JsonSerializer.Serialize(expectedAnalysis),
                Encoding.UTF8,
                "application/json"));

        var client = new HttpClient(mockHandler);
        var hindsightClient = new HindsightClient(client, ApiBaseUrl, ApiKey, _mockLogger.Object);

        // Act
        var result = await hindsightClient.GetHistoricalAnalysisAsync("NQ", from, to);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("uptrend", result.Trend);
        Assert.Equal(0.2345m, result.Volatility);
    }

    [Fact]
    public async Task RecognizePatternAsync_ValidData_ReturnsPattern()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler();
        var expectedPattern = new HindsightPattern
        {
            InstrumentCode = "NQ",
            PatternName = "Ascending Triangle",
            MatchPercentage = 94.5m,
            HistoricalSuccessRate = 72,
            PatternType = "continuation"
        };

        mockHandler.Expect(HttpMethod.Post, $"{ApiBaseUrl}/pattern/recognize")
            .Respond(new StringContent(
                JsonSerializer.Serialize(expectedPattern),
                Encoding.UTF8,
                "application/json"));

        var client = new HttpClient(mockHandler);
        var hindsightClient = new HindsightClient(client, ApiBaseUrl, ApiKey, _mockLogger.Object);

        var prices = new[] { 18200m, 18250m, 18300m, 18280m, 18350m };
        var volumes = new[] { 1200000L, 1100000L, 1300000L, 1150000L, 1400000L };

        // Act
        var result = await hindsightClient.RecognizePatternAsync("NQ", prices, volumes);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Ascending Triangle", result.PatternName);
        Assert.Equal(94.5m, result.MatchPercentage);
    }

    [Fact]
    public async Task RecognizePatternAsync_InvalidData_ThrowsException()
    {
        // Arrange
        var prices = new[] { 18200m, 18250m };
        var volumes = new[] { 1200000L, 1100000L, 1300000L };  // Mismatch

        // Act & Assert
        await Assert.ThrowsAsync<ArgumentException>(
            () => _hindsightClient.RecognizePatternAsync("NQ", prices, volumes));
    }

    [Fact]
    public async Task GetBacktestComparisonAsync_ValidStrategy_ReturnsComparison()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler();
        var expectedComparison = new HindsightBacktestComparison
        {
            InstrumentCode = "NQ",
            StrategyName = "OrderFlowDivergence",
            TotalReturn = 0.2845m,
            Sharpe = 1.85m,
            MaxDrawdown = -0.12m,
            WinRate = 68,
            TotalTrades = 145
        };

        mockHandler.Expect(HttpMethod.Get, $"{ApiBaseUrl}/backtest/compare*")
            .Respond(new StringContent(
                JsonSerializer.Serialize(expectedComparison),
                Encoding.UTF8,
                "application/json"));

        var client = new HttpClient(mockHandler);
        var hindsightClient = new HindsightClient(client, ApiBaseUrl, ApiKey, _mockLogger.Object);

        var from = DateTimeOffset.Parse("2024-01-01T00:00:00Z");
        var to = DateTimeOffset.Parse("2024-12-31T23:59:59Z");

        // Act
        var result = await hindsightClient.GetBacktestComparisonAsync("NQ", "OrderFlowDivergence", from, to);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(0.2845m, result.TotalReturn);
        Assert.Equal(1.85m, result.Sharpe);
        Assert.Equal(68, result.WinRate);
    }

    [Fact]
    public async Task TestConnectionAsync_HealthyService_ReturnsTrue()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.Expect(HttpMethod.Get, $"{ApiBaseUrl}/health")
            .Respond(System.Net.HttpStatusCode.OK);

        var client = new HttpClient(mockHandler);
        var hindsightClient = new HindsightClient(client, ApiBaseUrl, ApiKey, _mockLogger.Object);

        // Act
        var result = await hindsightClient.TestConnectionAsync();

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task TestConnectionAsync_UnhealthyService_ReturnsFalse()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.Expect(HttpMethod.Get, $"{ApiBaseUrl}/health")
            .Respond(System.Net.HttpStatusCode.ServiceUnavailable);

        var client = new HttpClient(mockHandler);
        var hindsightClient = new HindsightClient(client, ApiBaseUrl, ApiKey, _mockLogger.Object);

        // Act
        var result = await hindsightClient.TestConnectionAsync();

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task GetStatusAsync_ReturnsStatusInfo()
    {
        // Arrange
        var mockHandler = new MockHttpMessageHandler();
        var expectedStatus = new HindsightStatus
        {
            IsAvailable = true,
            Version = "1.0.0",
            ApiCallsRemaining = 4985,
            ApiCallsLimit = 5000,
            SupportedInstruments = new List<string> { "NQ", "ES", "MNQ", "MES" }
        };

        mockHandler.Expect(HttpMethod.Get, $"{ApiBaseUrl}/status")
            .Respond(new StringContent(
                JsonSerializer.Serialize(expectedStatus),
                Encoding.UTF8,
                "application/json"));

        var client = new HttpClient(mockHandler);
        var hindsightClient = new HindsightClient(client, ApiBaseUrl, ApiKey, _mockLogger.Object);

        // Act
        var result = await hindsightClient.GetStatusAsync();

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsAvailable);
        Assert.Equal(4985, result.ApiCallsRemaining);
        Assert.Contains("NQ", result.SupportedInstruments);
    }

    [Fact]
    public async Task MultipleRequests_RespectsCancellation()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        var mockHandler = new MockHttpMessageHandler();
        mockHandler.Expect(HttpMethod.Get, $"{ApiBaseUrl}/signal/current*")
            .Respond(async _ =>
            {
                await Task.Delay(1000);  // Simulate slow response
                return new StringContent("");
            });

        var client = new HttpClient(mockHandler) { Timeout = TimeSpan.FromSeconds(10) };
        var hindsightClient = new HindsightClient(client, ApiBaseUrl, ApiKey, _mockLogger.Object);

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => hindsightClient.GetCurrentSignalAsync("NQ", cts.Token));
    }
}

/// <summary>
/// Mock HTTP message handler for testing HTTP calls.
/// </summary>
public class MockHttpMessageHandler : HttpMessageHandler
{
    private readonly Dictionary<string, HttpResponseMessage> _responses = new();

    public MockHttpMessageHandler Expect(HttpMethod method, string urlPattern)
    {
        var key = $"{method.Method}:{urlPattern}";
        return this;
    }

    public void Respond(StringContent content)
    {
        // Store response
    }

    public void Respond(HttpStatusCode statusCode)
    {
        // Store response
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Mock implementation
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
