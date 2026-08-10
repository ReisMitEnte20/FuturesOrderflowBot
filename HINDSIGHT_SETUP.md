#!/bin/bash
# Quick Integration Setup for Hindsight API
# This script shows how to integrate Hindsight into your TradingBot setup

cat << 'EOF'

╔═══════════════════════════════════════════════════════════════════════════╗
║                                                                           ║
║          FuturesOrderflowBot - Hindsight API Integration Setup           ║
║                                                                           ║
╚═══════════════════════════════════════════════════════════════════════════╝

📋 STEP 1: Create Configuration File
═══════════════════════════════════════════════════════════════════════════

Copy the example configuration:

  cp config/hindsight.example.json config/hindsight.json

Edit it with your API credentials:

  {
    "hindsight": {
      "enabled": true,
      "apiBaseUrl": "https://api.hindsight.example.com/v1",
      "apiKey": "your-actual-api-key-here",
      "timeout": 30,
      "retryPolicy": {
        "maxRetries": 3,
        "delayMilliseconds": 1000
      }
    }
  }

═══════════════════════════════════════════════════════════════════════════
✅ STEP 2: Register in Dependency Injection (Program.cs)
═══════════════════════════════════════════════════════════════════════════

Add this code to your Program.cs after creating the builder:

---

// Add Hindsight API Integration
var hindsightConfig = builder.Configuration.GetSection("hindsight");
if (hindsightConfig.GetValue<bool>("enabled"))
{
    var apiUrl = hindsightConfig["apiBaseUrl"];
    var apiKey = hindsightConfig["apiKey"];
    var timeout = hindsightConfig.GetValue<int>("timeout", 30);

    builder.Services.AddHttpClient<IHindsightClient, HindsightClient>()
        .ConfigureHttpClient(client =>
        {
            client.BaseAddress = new Uri(apiUrl);
            client.Timeout = TimeSpan.FromSeconds(timeout);
            client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        })
        .SetHandlerLifetime(TimeSpan.FromMinutes(5));
    
    // Optional: Add Polly retry policy
    builder.Services.AddHttpClient<IHindsightClient, HindsightClient>()
        .AddTransientHttpErrorPolicy(p => 
            p.WaitAndRetryAsync(3, retryAttempt =>
                TimeSpan.FromSeconds(Math.Pow(2, retryAttempt))));
}

---

═══════════════════════════════════════════════════════════════════════════
💡 STEP 3: Use in Your Strategy (Example)
═══════════════════════════════════════════════════════════════════════════

Create a strategy that uses Hindsight:

---

using TradingBot.Core.Interfaces;

public class HindsightSignalStrategy : IStrategy
{
    private readonly IHindsightClient _hindsight;
    private readonly ILogger<HindsightSignalStrategy> _logger;
    
    public HindsightSignalStrategy(
        IHindsightClient hindsight,
        ILogger<HindsightSignalStrategy> logger)
    {
        _hindsight = hindsight;
        _logger = logger;
    }
    
    public async IAsyncEnumerable<Signal> ProcessMarketDataAsync(
        MarketTick tick,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Get current signal from Hindsight
        var signal = await _hindsight.GetCurrentSignalAsync("NQ", cancellationToken);
        
        if (signal is null)
        {
            _logger.LogWarning("No Hindsight signal available");
            yield break;
        }
        
        // Only trade high confidence signals
        if (signal.Confidence < 0.80m)
        {
            _logger.LogDebug("Signal confidence too low: {Confidence}", signal.Confidence);
            yield break;
        }
        
        _logger.LogInformation(
            "Hindsight Signal - Type: {Type}, Confidence: {Confidence:P}, Target: {Target}",
            signal.SignalType,
            signal.Confidence,
            signal.TargetPrice);
        
        if (signal.SignalType == "buy")
        {
            yield return new Signal(
                signalType: SignalType.BuyEntry,
                targetPrice: signal.TargetPrice,
                stopLoss: signal.StopLoss,
                metadata: new Dictionary<string, object>
                {
                    { "source", "hindsight" },
                    { "confidence", signal.Confidence },
                    { "pattern", signal.Pattern ?? "" },
                    { "reason", signal.Reason ?? "" }
                }
            );
        }
        else if (signal.SignalType == "sell")
        {
            yield return new Signal(
                signalType: SignalType.SellEntry,
                targetPrice: signal.TargetPrice,
                stopLoss: signal.StopLoss,
                metadata: new Dictionary<string, object>
                {
                    { "source", "hindsight" },
                    { "confidence", signal.Confidence }
                }
            );
        }
    }
}

---

═══════════════════════════════════════════════════════════════════════════
🧪 STEP 4: Test the Integration
═══════════════════════════════════════════════════════════════════════════

Run the API examples:

  chmod +x hindsight-api-examples.sh
  ./hindsight-api-examples.sh

Test connection in code:

  using (var scope = app.Services.CreateScope())
  {
      var hindsight = scope.ServiceProvider.GetRequiredService<IHindsightClient>();
      var isConnected = await hindsight.TestConnectionAsync();
      
      if (isConnected)
          Console.WriteLine("✓ Hindsight API connected successfully");
      else
          Console.WriteLine("✗ Failed to connect to Hindsight API");
  }

═══════════════════════════════════════════════════════════════════════════
📊 STEP 5: Monitor and Log Events
═══════════════════════════════════════════════════════════════════════════

Integrate with Filebeat for monitoring:

Add to Filebeat configuration (filebeat.yml):

  - type: log
    enabled: true
    paths:
      - "logs/hindsight-*.log"
    fields:
      service: tradingbot
      component: hindsight
      monitoring_type: external_api

═══════════════════════════════════════════════════════════════════════════
🔗 Useful cURL Examples
═══════════════════════════════════════════════════════════════════════════

Test API connection:

  curl -X GET \\
    -H "X-API-Key: YOUR_API_KEY" \\
    https://api.hindsight.example.com/v1/health

Get current signal:

  curl -X GET \\
    -H "X-API-Key: YOUR_API_KEY" \\
    "https://api.hindsight.example.com/v1/signal/current?instrument=NQ"

Recognize pattern:

  curl -X POST \\
    -H "X-API-Key: YOUR_API_KEY" \\
    -H "Content-Type: application/json" \\
    -d '{
      "instrument": "NQ",
      "prices": [18200, 18250, 18300, 18280, 18350],
      "volumes": [1200000, 1100000, 1300000, 1150000, 1400000]
    }' \\
    https://api.hindsight.example.com/v1/pattern/recognize

═══════════════════════════════════════════════════════════════════════════
📚 Documentation
═══════════════════════════════════════════════════════════════════════════

Complete Integration Guide:
  → docs/HINDSIGHT_INTEGRATION.md

Hindsight API Interface:
  → src/TradingBot.Core/Interfaces/IHindsightClient.cs

Hindsight Client Implementation:
  → src/TradingBot.Infrastructure/ExternalApis/HindsightClient.cs

Unit Tests:
  → tests/TradingBot.Tests/ExternalApis/HindsightClientTests.cs

Example cURL Commands:
  → hindsight-api-examples.sh

═══════════════════════════════════════════════════════════════════════════
🚀 Next Steps
═══════════════════════════════════════════════════════════════════════════

1. ✓ Create config/hindsight.json with your API key
2. ✓ Add Hindsight registration to Program.cs
3. ✓ Develop a strategy using IHindsightClient
4. ✓ Run unit tests: dotnet test -k HindsightClientTests
5. ✓ Run the application: dotnet run --project src/TradingBot.DevDashboard
6. ✓ Monitor API calls in Kibana: http://localhost:5601

═══════════════════════════════════════════════════════════════════════════

Questions? Check docs/HINDSIGHT_INTEGRATION.md for complete information!

═══════════════════════════════════════════════════════════════════════════

EOF
