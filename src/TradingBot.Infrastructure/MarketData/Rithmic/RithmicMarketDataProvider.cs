using System.Runtime.CompilerServices;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.MarketData.Rithmic;
using TradingBot.Infrastructure.MarketData.Rithmic.Models;

namespace TradingBot.Infrastructure.MarketData;

/// <summary>
/// Pull-only (REST) market data provider for Rithmic.
/// No WebSocket, no push publishing — purely query-based OHLCV/tick fetching.
/// Streams historical data as <see cref="MarketTick"/> for the existing bar pipeline.
/// </summary>
public sealed class RithmicMarketDataProvider : IMarketDataProvider
{
    private readonly RithmicCandleService _candleService;
    private readonly string _defaultInterval;
    private readonly string _defaultSymbol;
    private readonly int _lookbackCandles;
    private readonly ILogger _logger;

    private CancellationTokenSource? _stopCts;
    private volatile bool _connected;

    public RithmicMarketDataProvider(
        RithmicConfig config,
        ILogger? logger = null)
    {
        _candleService = new RithmicCandleService(config);
        _defaultInterval = config.DefaultInterval;
        _defaultSymbol = config.DefaultSymbol;
        _lookbackCandles = config.LookbackCandles;
        _logger = logger ?? NullLogger.Instance;
    }

    public bool IsConnected => _connected;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _stopCts = new CancellationTokenSource();
        _connected = true;
        _logger.Info("Rithmic provider connected (REST/pull-only).");
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _connected = false;
        _stopCts?.Cancel();
        _logger.Info("Rithmic provider disconnected.");
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<MarketTick> SubscribeTicksAsync(
        string symbol,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!_connected)
            throw new InvalidOperationException("Rithmic provider not connected — call ConnectAsync first.");

        var sym = string.IsNullOrEmpty(symbol) ? _defaultSymbol : symbol;

        var stopToken = _stopCts?.Token ?? CancellationToken.None;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopToken);
        var token = linked.Token;

        _logger.Info($"Rithmic: fetching historical ticks for {sym} via {_defaultInterval} (lookback: {_lookbackCandles}).");

        var now = DateTimeOffset.UtcNow;
        var from = now.AddHours(-24);

        var candles = await _candleService.GetCandlesAsync(sym, _defaultInterval, from, now, token);

        var count = 0;
        foreach (var candle in candles)
        {
            if (token.IsCancellationRequested) yield break;

            foreach (var tick in ConvertCandleToTicks(candle))
            {
                if (token.IsCancellationRequested) yield break;
                yield return tick;
                count++;
            }
        }

        _logger.Info($"Rithmic: streamed {count} ticks from {candles.Count} candles for {sym}.");
    }

    private IEnumerable<MarketTick> ConvertCandleToTicks(RithmicCandle candle)
    {
        var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(candle.Timestamp);
        var midVolume = candle.Volume / 2m;

        yield return new MarketTick
        {
            Symbol = candle.Symbol,
            Timestamp = timestamp,
            Price = candle.Open,
            Bid = candle.VWAP ?? candle.Open,
            Ask = candle.VWAP ?? candle.Open,
            BidSize = midVolume,
            AskSize = midVolume,
            Volume = midVolume,
            Aggressor = AggressorSide.Unknown,
        };

        yield return new MarketTick
        {
            Symbol = candle.Symbol,
            Timestamp = timestamp,
            Price = candle.Close,
            Bid = candle.Close,
            Ask = candle.Close,
            BidSize = midVolume,
            AskSize = midVolume,
            Volume = midVolume,
            Aggressor = AggressorSide.Unknown,
        };
    }
}