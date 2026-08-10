#!/bin/bash
# Hindsight API - cURL Examples
# Complete set of examples for interacting with Hindsight API

# Configuration
HINDSIGHT_API_URL="${HINDSIGHT_API_URL:-https://api.hindsight.example.com/v1}"
API_KEY="${HINDSIGHT_API_KEY:-your-api-key-here}"
INSTRUMENT="NQ"  # Nasdaq 100 Futures
FROM_DATE="2024-01-01T00:00:00Z"
TO_DATE="2024-12-31T23:59:59Z"

# Color codes
GREEN='\033[0;32m'
BLUE='\033[0;34m'
YELLOW='\033[1;33m'
NC='\033[0m'

print_header() {
    echo -e "${BLUE}═══════════════════════════════════════════════════════${NC}"
    echo -e "${YELLOW}$1${NC}"
    echo -e "${BLUE}═══════════════════════════════════════════════════════${NC}"
}

print_command() {
    echo -e "${GREEN}→ Executing:${NC}"
    echo "$1"
    echo ""
}

# ═════════════════════════════════════════════════════════════════════════════
print_header "1. Health Check - Verify API Connection"
# ═════════════════════════════════════════════════════════════════════════════

CMD='curl -s -X GET \
  -H "X-API-Key: '"$API_KEY"'" \
  -H "Content-Type: application/json" \
  "'"$HINDSIGHT_API_URL"'/health" | jq .'

print_command "$CMD"
echo "Expected Response:"
echo '{
  "status": "healthy",
  "version": "1.0.0",
  "timestamp": "2024-08-10T12:00:00Z"
}'
echo ""

# ═════════════════════════════════════════════════════════════════════════════
print_header "2. Get API Status - Check Rate Limits & Supported Instruments"
# ═════════════════════════════════════════════════════════════════════════════

CMD='curl -s -X GET \
  -H "X-API-Key: '"$API_KEY"'" \
  -H "Content-Type: application/json" \
  "'"$HINDSIGHT_API_URL"'/status" | jq .'

print_command "$CMD"
echo "Expected Response:"
echo '{
  "isAvailable": true,
  "version": "1.0.0",
  "lastUpdated": "2024-08-10T12:00:00Z",
  "apiCallsRemaining": 4985,
  "apiCallsLimit": 5000,
  "apiLimitResetTime": "2024-08-11T00:00:00Z",
  "supportedInstruments": ["NQ", "ES", "MNQ", "MES"],
  "supportedAnalysisTypes": ["default", "advanced", "renko", "heikin-ashi"]
}'
echo ""

# ═════════════════════════════════════════════════════════════════════════════
print_header "3. Get Historical Analysis - Analyze Past Performance"
# ═════════════════════════════════════════════════════════════════════════════

CMD='curl -s -X GET \
  -H "X-API-Key: '"$API_KEY"'" \
  -H "Content-Type: application/json" \
  "'"$HINDSIGHT_API_URL"'/analysis/historical?instrument='"$INSTRUMENT"'&from='"$FROM_DATE"'&to='"$TO_DATE"'&type=default" | jq .'

print_command "$CMD"
echo "Expected Response:"
echo '{
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
  "insight": "Strong uptrend with breakout above 18500 resistance. Volume confirming continuation."
}'
echo ""

# ═════════════════════════════════════════════════════════════════════════════
print_header "4. Get Current Signal - Real-time Buy/Sell Signals"
# ═════════════════════════════════════════════════════════════════════════════

CMD='curl -s -X GET \
  -H "X-API-Key: '"$API_KEY"'" \
  -H "Content-Type: application/json" \
  "'"$HINDSIGHT_API_URL"'/signal/current?instrument='"$INSTRUMENT"'" | jq .'

print_command "$CMD"
echo "Expected Response:"
echo '{
  "instrumentCode": "NQ",
  "signalTime": "2024-08-10T14:30:00Z",
  "signalType": "buy",
  "confidence": 0.87,
  "targetPrice": 18750.00,
  "stopLoss": 18100.00,
  "reason": "Bullish divergence with MACD crossover above signal line",
  "pattern": "Ascending Triangle Breakout",
  "trendStrength": 8
}'
echo ""

# ═════════════════════════════════════════════════════════════════════════════
print_header "5. Pattern Recognition - Identify Chart Patterns"
# ═════════════════════════════════════════════════════════════════════════════

CMD='curl -s -X POST \
  -H "X-API-Key: '"$API_KEY"'" \
  -H "Content-Type: application/json" \
  -d '"'"'{
    "instrument": "NQ",
    "prices": [18200, 18250, 18300, 18280, 18350, 18400, 18380, 18450, 18500],
    "volumes": [1200000, 1100000, 1300000, 1150000, 1400000, 1500000, 1350000, 1600000, 1700000]
  }'"'"' \
  "'"$HINDSIGHT_API_URL"'/pattern/recognize" | jq .'

print_command "$CMD"
echo "Expected Response:"
echo '{
  "instrumentCode": "NQ",
  "recognizedAt": "2024-08-10T14:30:00Z",
  "patternName": "Ascending Triangle",
  "matchPercentage": 94.5,
  "patternType": "continuation",
  "projectedTarget": 18750.00,
  "projectedStopLoss": 18100.00,
  "historicalSuccessRate": 72,
  "description": "Classic ascending triangle with higher lows and flat resistance at 18500",
  "patternFormationDate": "2024-08-05T10:00:00Z",
  "similarHistoricalPatterns": ["AscTriangle_20240115", "AscTriangle_20240320"]
}'
echo ""

# ═════════════════════════════════════════════════════════════════════════════
print_header "6. Backtest Comparison - Compare Strategy Performance"
# ═════════════════════════════════════════════════════════════════════════════

CMD='curl -s -X GET \
  -H "X-API-Key: '"$API_KEY"'" \
  -H "Content-Type: application/json" \
  "'"$HINDSIGHT_API_URL"'/backtest/compare?instrument='"$INSTRUMENT"'&strategy=OrderFlowDivergence&from='"$FROM_DATE"'&to='"$TO_DATE"'" | jq .'

print_command "$CMD"
echo "Expected Response:"
echo '{
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
}'
echo ""

# ═════════════════════════════════════════════════════════════════════════════
print_header "7. Batch Analysis - Multiple Instruments"
# ═════════════════════════════════════════════════════════════════════════════

CMD='for instrument in NQ ES MNQ MES; do
  echo "Analyzing $instrument..."
  curl -s -X GET \
    -H "X-API-Key: '"$API_KEY"'" \
    -H "Content-Type: application/json" \
    "'"$HINDSIGHT_API_URL"'/signal/current?instrument=$instrument" | jq ".instrumentCode, .signalType, .confidence"
done'

print_command "$CMD"
echo ""

# ═════════════════════════════════════════════════════════════════════════════
print_header "8. Error Handling - Invalid Instrument"
# ═════════════════════════════════════════════════════════════════════════════

CMD='curl -s -X GET \
  -H "X-API-Key: '"$API_KEY"'" \
  -H "Content-Type: application/json" \
  "'"$HINDSIGHT_API_URL"'/signal/current?instrument=INVALID" | jq .'

print_command "$CMD"
echo "Expected Error Response:"
echo '{
  "error": "NotFound",
  "message": "Instrument INVALID not supported",
  "supportedInstruments": ["NQ", "ES", "MNQ", "MES"]
}'
echo ""

# ═════════════════════════════════════════════════════════════════════════════
print_header "9. Rate Limiting Handling"
# ═════════════════════════════════════════════════════════════════════════════

CMD='curl -s -X GET \
  -H "X-API-Key: '"$API_KEY"'" \
  -H "Content-Type: application/json" \
  -w "HTTP Status: %{http_code}\\nX-Rate-Limit-Remaining: %{header{X-Rate-Limit-Remaining}}" \
  "'"$HINDSIGHT_API_URL"'/status"'

print_command "$CMD"
echo ""
echo "Response Headers:"
echo "  X-Rate-Limit-Limit: 5000"
echo "  X-Rate-Limit-Remaining: 4985"
echo "  X-Rate-Limit-Reset: 1691558400"
echo ""

# ═════════════════════════════════════════════════════════════════════════════
print_header "10. Integration with TradingBot via DI"
# ═════════════════════════════════════════════════════════════════════════════

echo "Add to Program.cs:"
echo ""
echo 'builder.Services.AddHttpClient<IHindsightClient, HindsightClient>()
    .ConfigureHttpClient((sp, client) =>
    {
        var config = sp.GetRequiredService<IConfiguration>();
        var apiUrl = config["hindsight:apiBaseUrl"];
        var apiKey = config["hindsight:apiKey"];
        
        client.BaseAddress = new Uri(apiUrl);
        client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
    })
    .SetHandlerLifetime(TimeSpan.FromMinutes(5));'
echo ""
