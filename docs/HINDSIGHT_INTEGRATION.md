# Hindsight API Integration Guide

Comprehensive guide for integrating Hindsight trading analysis API into FuturesOrderflowBot.

## Overview

Hindsight is an external trading analysis service that provides:
- Historical market analysis
- Real-time trading signals
- Pattern recognition
- Backtest comparisons with benchmarks
- Multi-timeframe analysis

## Quick Start

### 1. Configuration

Create your Hindsight API credentials:

```bash
# config/hindsight.example.json
cp config/hindsight.example.json config/hindsight.json

# Edit config/hindsight.json
{
  "hindsight": {
    "enabled": true,
    "apiBaseUrl": "https://api.hindsight.example.com/v1",
    "apiKey": "your-actual-api-key-here",
    "timeout": 30
  }
}
```

### 2. Register in Dependency Injection

In `Program.cs`:

```csharp
// Add Hindsight API client
var hindsightConfig = configuration.GetSection("hindsight");
var apiUrl = hindsightConfig["apiBaseUrl"];
var apiKey = hindsightConfig["apiKey"];

builder.Services.AddHttpClient<IHindsightClient, HindsightClient>()
    .ConfigureHttpClient(client =>
    {
        client.BaseAddress = new Uri(apiUrl);
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
    });
```

### 3. Use in Your Application

```csharp
public class MyStrategy
{
    private readonly IHindsightClient _hindsight;
    
    public MyStrategy(IHindsightClient hindsight)
    {
        _hindsight = hindsight;
    }
    
    public async Task OnMarketUpdate(MarketTick tick)
    {
        // Get current signal from Hindsight
        var signal = await _hindsight.GetCurrentSignalAsync("NQ");
        
        if (signal?.SignalType == "buy" && signal.Confidence > 0.8m)
        {
            // Generate buy signal
            GenerateSignal(SignalType.Buy, signal.TargetPrice, signal.StopLoss);
        }
    }
}
```

## API Endpoints

### Health Check
```bash
curl -X GET \
  -H "X-API-Key: YOUR_API_KEY" \
  https://api.hindsight.example.com/v1/health
```

**Response:**
```json
{
  "status": "healthy",
  "version": "1.0.0",
  "timestamp": "2024-08-10T12:00:00Z"
}
```

---

### Get API Status
Check rate limits and supported instruments.

```bash
curl -X GET \
  -H "X-API-Key: YOUR_API_KEY" \
  https://api.hindsight.example.com/v1/status
```

**Response:**
```json
{
  "isAvailable": true,
  "version": "1.0.0",
  "lastUpdated": "2024-08-10T12:00:00Z",
  "apiCallsRemaining": 4985,
  "apiCallsLimit": 5000,
  "apiLimitResetTime": "2024-08-11T00:00:00Z",
  "supportedInstruments": ["NQ", "ES", "MNQ", "MES"],
  "supportedAnalysisTypes": ["default", "advanced", "renko", "heikin-ashi"]
}
```

---

### Historical Analysis
Analyze price action and trends over a time period.

```bash
curl -X GET \
  -H "X-API-Key: YOUR_API_KEY" \
  "https://api.hindsight.example.com/v1/analysis/historical?instrument=NQ&from=2024-01-01T00:00:00Z&to=2024-12-31T23:59:59Z&type=default"
```

**Response:**
```json
{
  "instrumentCode": "NQ",
  "analyzedAt": "2024-08-10T12:00:00Z",
  "periodFrom": "2024-01-01T00:00:00Z",
  "periodTo": "2024-12-31T23:59:59Z",
  "analysisType": "default",
  "highPrice": 18750.50,
  "lowPrice": 14200.25,
  "openPrice": 14550.00,
  "closePrice": 18200.75,
  "totalVolume": 12450000,
  "trend": "uptrend",
  "volatility": 0.2345,
  "momentum": 0.8750,
  "keyLevel": "18500",
  "insight": "Strong uptrend with breakout above 18500 resistance..."
}
```

---

### Current Trading Signal
Get real-time buy/sell signals with confidence scores.

```bash
curl -X GET \
  -H "X-API-Key: YOUR_API_KEY" \
  "https://api.hindsight.example.com/v1/signal/current?instrument=NQ"
```

**Response:**
```json
{
  "instrumentCode": "NQ",
  "signalTime": "2024-08-10T14:30:00Z",
  "signalType": "buy",
  "confidence": 0.87,
  "targetPrice": 18750.00,
  "stopLoss": 18100.00,
  "reason": "Bullish divergence with MACD crossover",
  "pattern": "Ascending Triangle Breakout",
  "trendStrength": 8,
  "additionalData": {
    "rsi": 65,
    "macd": "above_signal",
    "volume_profile": "high_confidence"
  }
}
```

---

### Pattern Recognition
Identify chart patterns and get success rate estimates.

```bash
curl -X POST \
  -H "X-API-Key: YOUR_API_KEY" \
  -H "Content-Type: application/json" \
  -d '{
    "instrument": "NQ",
    "prices": [18200, 18250, 18300, 18280, 18350, 18400, 18380, 18450, 18500],
    "volumes": [1200000, 1100000, 1300000, 1150000, 1400000, 1500000, 1350000, 1600000, 1700000]
  }' \
  "https://api.hindsight.example.com/v1/pattern/recognize"
```

**Response:**
```json
{
  "instrumentCode": "NQ",
  "recognizedAt": "2024-08-10T14:30:00Z",
  "patternName": "Ascending Triangle",
  "matchPercentage": 94.5,
  "patternType": "continuation",
  "projectedTarget": 18750.00,
  "projectedStopLoss": 18100.00,
  "historicalSuccessRate": 72,
  "description": "Classic ascending triangle with higher lows and flat resistance",
  "patternFormationDate": "2024-08-05T10:00:00Z",
  "similarHistoricalPatterns": ["AscTriangle_20240115", "AscTriangle_20240320"]
}
```

---

### Backtest Comparison
Compare your strategy against benchmarks.

```bash
curl -X GET \
  -H "X-API-Key: YOUR_API_KEY" \
  "https://api.hindsight.example.com/v1/backtest/compare?instrument=NQ&strategy=OrderFlowDivergence&from=2024-01-01T00:00:00Z&to=2024-12-31T23:59:59Z"
```

**Response:**
```json
{
  "instrumentCode": "NQ",
  "strategyName": "OrderFlowDivergence",
  "backtestFrom": "2024-01-01T00:00:00Z",
  "backtestTo": "2024-12-31T23:59:59Z",
  "totalReturn": 0.2845,
  "sharpe": 1.85,
  "maxDrawdown": -0.12,
  "winRate": 68,
  "totalTrades": 145,
  "benchmark": {
    "benchmarkName": "Buy and Hold NQ",
    "return": 0.35,
    "sharpe": 0.95,
    "maxDrawdown": -0.18
  },
  "recommendations": [
    "Increase position sizing during high confidence signals",
    "Add additional exit criteria to reduce drawdown",
    "Consider multi-timeframe confirmation"
  ]
}
```

---

## Code Examples

### Example 1: Simple Signal Following

```csharp
public class HindsightSignalStrategy : IStrategy
{
    private readonly IHindsightClient _hindsight;
    private readonly ILogger<HindsightSignalStrategy> _logger;
    
    public HindsightSignalStrategy(IHindsightClient hindsight, ILogger<HindsightSignalStrategy> logger)
    {
        _hindsight = hindsight;
        _logger = logger;
    }
    
    public async Task ProcessMarketDataAsync(MarketTick tick)
    {
        var signal = await _hindsight.GetCurrentSignalAsync("NQ");
        
        if (signal is null)
        {
            _logger.LogWarning("No signal available");
            return;
        }
        
        // Only trade high confidence signals
        if (signal.Confidence >= 0.85m)
        {
            switch (signal.SignalType)
            {
                case "buy":
                    _logger.LogInformation("Buy signal: {Reason}", signal.Reason);
                    yield return new Signal(SignalType.Buy, signal.TargetPrice, signal.StopLoss);
                    break;
                case "sell":
                    _logger.LogInformation("Sell signal: {Reason}", signal.Reason);
                    yield return new Signal(SignalType.Sell, signal.TargetPrice, signal.StopLoss);
                    break;
            }
        }
    }
}
```

### Example 2: Pattern-Based Entry

```csharp
public class HindsightPatternStrategy : IStrategy
{
    private readonly IHindsightClient _hindsight;
    private readonly List<decimal> _recentPrices = new();
    private readonly List<long> _recentVolumes = new();
    
    public async Task ProcessMarketDataAsync(MarketTick tick)
    {
        _recentPrices.Add(tick.LastTrade);
        _recentVolumes.Add(tick.Volume);
        
        if (_recentPrices.Count < 9) return;
        
        // Keep only last 20 candles
        if (_recentPrices.Count > 20)
        {
            _recentPrices.RemoveAt(0);
            _recentVolumes.RemoveAt(0);
        }
        
        var pattern = await _hindsight.RecognizePatternAsync(
            "NQ",
            _recentPrices.ToArray(),
            _recentVolumes.ToArray()
        );
        
        if (pattern?.MatchPercentage > 90 && pattern.HistoricalSuccessRate > 70)
        {
            yield return new Signal(
                SignalType.Buy,
                pattern.ProjectedTarget,
                pattern.ProjectedStopLoss
            );
        }
    }
}
```

### Example 3: Backtest Validation

```csharp
public class BacktestValidator
{
    private readonly IHindsightClient _hindsight;
    
    public async Task ValidateStrategyAsync(string strategyName, DateTimeOffset from, DateTimeOffset to)
    {
        var comparison = await _hindsight.GetBacktestComparisonAsync(
            "NQ",
            strategyName,
            from,
            to
        );
        
        if (comparison is null)
        {
            Console.WriteLine("Strategy validation failed");
            return;
        }
        
        Console.WriteLine($"Strategy: {comparison.StrategyName}");
        Console.WriteLine($"Return: {comparison.TotalReturn:P}");
        Console.WriteLine($"Sharpe: {comparison.Sharpe:F2}");
        Console.WriteLine($"Max Drawdown: {comparison.MaxDrawdown:P}");
        Console.WriteLine($"Win Rate: {comparison.WinRate}%");
        Console.WriteLine($"Total Trades: {comparison.TotalTrades}");
        
        if (comparison.Benchmark != null)
        {
            Console.WriteLine($"\nBenchmark ({comparison.Benchmark.BenchmarkName}):");
            Console.WriteLine($"Return: {comparison.Benchmark.Return:P}");
            Console.WriteLine($"Sharpe: {comparison.Benchmark.Sharpe:F2}");
        }
        
        if (comparison.Recommendations?.Count > 0)
        {
            Console.WriteLine("\nRecommendations:");
            foreach (var rec in comparison.Recommendations)
            {
                Console.WriteLine($"  - {rec}");
            }
        }
    }
}
```

## Error Handling

```csharp
try
{
    var signal = await _hindsight.GetCurrentSignalAsync("NQ");
    
    if (signal is null)
    {
        _logger.LogWarning("No signal data available");
    }
    else
    {
        // Process signal
    }
}
catch (HttpRequestException ex)
{
    _logger.LogError(ex, "Hindsight API connection failed");
    // Fallback strategy
}
catch (OperationCanceledException)
{
    _logger.LogWarning("Hindsight API request timed out");
}
```

## Rate Limiting

Hindsight API has rate limits:
- **Default**: 5,000 requests per day
- **Premium**: 50,000 requests per day

Monitor usage via the `/status` endpoint:

```csharp
var status = await _hindsight.GetStatusAsync();

if (status.ApiCallsRemaining < status.ApiCallsLimit / 10)
{
    _logger.LogWarning("Approaching rate limit: {Remaining}/{Limit}", 
        status.ApiCallsRemaining, 
        status.ApiCallsLimit);
}
```

## Testing the Integration

### Test Connection

```bash
# Run manual tests
chmod +x hindsight-api-examples.sh
./hindsight-api-examples.sh
```

### Unit Tests

```csharp
[Fact]
public async Task GetCurrentSignal_ReturnsValidSignal()
{
    // Arrange
    var mockHttp = new MockHttpMessageHandler();
    mockHttp.Expect(HttpMethod.Get, "*/signal/current*")
        .Respond("application/json", JsonConvert.SerializeObject(new HindsightSignal
        {
            InstrumentCode = "NQ",
            SignalType = "buy",
            Confidence = 0.85m
        }));
    
    // Act
    var signal = await _hindsightClient.GetCurrentSignalAsync("NQ");
    
    // Assert
    Assert.NotNull(signal);
    Assert.Equal("buy", signal.SignalType);
    Assert.Equal(0.85m, signal.Confidence);
}
```

## Troubleshooting

### Issue: "401 Unauthorized"
**Solution**: Verify your API key in `config/hindsight.json`

### Issue: "404 Not Found" for instrument
**Solution**: Check supported instruments via `/status` endpoint

### Issue: Timeout errors
**Solution**: Increase timeout in configuration or implement retry policy

### Issue: Rate limit exceeded
**Solution**: 
- Upgrade API plan
- Reduce polling frequency
- Cache responses when possible

## Best Practices

1. **Cache Results**: Cache API responses to reduce API calls
2. **Rate Limit Aware**: Check `ApiCallsRemaining` before making requests
3. **Error Handling**: Implement fallback strategies when Hindsight is unavailable
4. **Confidence Thresholds**: Only act on high-confidence signals (> 0.80)
5. **Multi-Signal Confirmation**: Combine Hindsight signals with other indicators
6. **Pattern Validation**: Validate patterns have > 70% historical success rate

## Integration Checklist

- [ ] Create config/hindsight.json with API credentials
- [ ] Register IHindsightClient in DI container
- [ ] Test connection with `TestConnectionAsync()`
- [ ] Implement strategy using Hindsight signals
- [ ] Add error handling and fallbacks
- [ ] Monitor API usage and rate limits
- [ ] Set up logging for Hindsight events
- [ ] Create unit tests for Hindsight integration
- [ ] Document custom strategies using Hindsight

## Support

For API issues, contact Hindsight support at: support@hindsight.example.com

For integration issues, see the TradingBot documentation:
- Architecture: docs/ARCHITECTURE.md
- Strategy Development: docs/STRATEGY_FRAMEWORK.md
