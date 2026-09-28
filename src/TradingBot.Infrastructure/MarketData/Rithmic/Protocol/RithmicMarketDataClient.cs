using System.Collections.Concurrent;
using System.Threading.Channels;
using TradingBot.Core.Interfaces;
using TradingBot.Domain.Enums;
using TradingBot.Domain.Models;
using TradingBot.Infrastructure.MarketData.Rithmic.Protocol.Messages;

namespace TradingBot.Infrastructure.MarketData.Rithmic.Protocol;

/// <summary>
/// Marktdaten-Client für Rithmic R|Protocol: Ticker Plant (LastTrade/BBO live) + History Plant (Zeit-Bars).
/// Research/Simulation-only: KEIN Order-/PnL-Plant, keine Orders. Netzwerk nur bei explizitem <see cref="ConnectAsync"/>.
/// </summary>
public sealed class RithmicMarketDataClient : IAsyncDisposable
{
    /// <summary>Rithmic kürzt Replays; so viele Folgeseiten werden maximal nachgeladen.</summary>
    private const int MaxBarPages = 50;

    private readonly RithmicProtocolOptions _options;
    private readonly RithmicTransportFactory _transportFactory;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, RithmicQuote> _quotes = new();
    private readonly ConcurrentDictionary<Guid, (string Key, Channel<MarketTick> Channel)> _tickReaders = new();
    private readonly ConcurrentDictionary<string, byte> _subscriptions = new();

    /// <summary>rp_code, mit dem Rithmic ein bereits bestehendes Abo ablehnt ("update bit type already exists").</summary>
    internal const string AlreadySubscribedRpCode = "1029";

    private RithmicPlantSession? _ticker;
    private RithmicPlantSession? _history;

    public RithmicMarketDataClient(
        RithmicProtocolOptions options,
        RithmicTransportFactory? transportFactory = null,
        ILogger? logger = null)
    {
        _options = options;
        _transportFactory = transportFactory ?? (() => new WebSocketRithmicTransport());
        _logger = logger ?? NullLogger.Instance;
    }

    public bool IsConnected => _ticker?.IsLoggedIn == true;
    public bool HasHistory => _history?.IsLoggedIn == true;
    public string? SystemName { get; private set; }
    public string? User { get; private set; }
    public string? LastError => _ticker?.LastError ?? _history?.LastError;

    public Task<IReadOnlyList<string>> GetSystemNamesAsync(CancellationToken cancellationToken = default) =>
        RithmicPlantSession.GetSystemNamesAsync(_options, _transportFactory, cancellationToken);

    /// <summary>
    /// Prüft den System-Namen gegen die System-Info des Gateways und loggt Ticker- (+ optional History-) Plant ein.
    /// Das Passwort wird nur für die Login-Requests verwendet und nicht gespeichert.
    /// </summary>
    public async Task ConnectAsync(RithmicLoginCredentials credentials, bool includeHistory = true, CancellationToken cancellationToken = default)
    {
        if (IsConnected)
            throw new InvalidOperationException("Rithmic-Client ist bereits verbunden.");

        var systems = await GetSystemNamesAsync(cancellationToken);
        if (!systems.Contains(credentials.SystemName))
            throw new RithmicProtocolException(
                $"System '{credentials.SystemName}' ist auf diesem Gateway nicht verfügbar. Verfügbar: {string.Join(", ", systems)}");

        var ticker = new RithmicPlantSession(RithmicPlant.Ticker, _options, _transportFactory, _logger);
        ticker.StreamMessage += OnTickerMessage;
        await ticker.LoginAsync(credentials, cancellationToken);
        _ticker = ticker;

        if (includeHistory)
        {
            var history = new RithmicPlantSession(RithmicPlant.History, _options, _transportFactory, _logger);
            try
            {
                await history.LoginAsync(credentials, cancellationToken);
                _history = history;
            }
            catch (Exception ex)
            {
                // Live-Daten bleiben nutzbar; History fehlt ehrlich statt still.
                _logger.Warning($"Rithmic History-Plant nicht verfügbar: {ex.Message}");
            }
        }

        SystemName = credentials.SystemName;
        User = credentials.User;
    }

    /// <summary>
    /// Abonniert LastTrade + BBO für ein Instrument (z. B. "MESZ6", "CME"). Idempotent: ein bestehendes Abo
    /// wird nicht erneut angefragt, und Rithmics "bereits abonniert" (rp_code 1029) gilt als Erfolg.
    /// </summary>
    public async Task SubscribeAsync(string symbol, string exchange, CancellationToken cancellationToken = default)
    {
        var ticker = RequireTicker();
        var key = Key(symbol, exchange);
        if (!_subscriptions.TryAdd(key, 0))
            return;

        _quotes.TryAdd(key, new RithmicQuote { Symbol = symbol, Exchange = exchange });
        try
        {
            await ticker.RequestAsync(new RequestMarketDataUpdate
            {
                TemplateId = RithmicTemplates.RequestMarketDataUpdate,
                Symbol = symbol,
                Exchange = exchange,
                Request = RequestMarketDataUpdate.Types.Request.Subscribe,
                UpdateBits = (uint)(RequestMarketDataUpdate.Types.UpdateBits.LastTrade | RequestMarketDataUpdate.Types.UpdateBits.Bbo),
            }, cancellationToken);
        }
        catch (RithmicProtocolException ex) when (ex.RpCode.Count > 0 && ex.RpCode[0] == AlreadySubscribedRpCode)
        {
            // Abo besteht serverseitig bereits – gewünschter Zustand ist erreicht.
        }
        catch
        {
            _subscriptions.TryRemove(key, out _);
            throw;
        }
    }

    public bool IsSubscribed(string symbol, string exchange) => _subscriptions.ContainsKey(Key(symbol, exchange));

    public async Task UnsubscribeAsync(string symbol, string exchange, CancellationToken cancellationToken = default)
    {
        var ticker = RequireTicker();
        await ticker.RequestAsync(new RequestMarketDataUpdate
        {
            TemplateId = RithmicTemplates.RequestMarketDataUpdate,
            Symbol = symbol,
            Exchange = exchange,
            Request = RequestMarketDataUpdate.Types.Request.Unsubscribe,
            UpdateBits = (uint)(RequestMarketDataUpdate.Types.UpdateBits.LastTrade | RequestMarketDataUpdate.Types.UpdateBits.Bbo),
        }, cancellationToken);
        _quotes.TryRemove(Key(symbol, exchange), out _);
        _subscriptions.TryRemove(Key(symbol, exchange), out _);
    }

    public RithmicQuote? GetQuote(string symbol, string exchange) =>
        _quotes.TryGetValue(Key(symbol, exchange), out var quote) ? quote : null;

    public IReadOnlyCollection<RithmicQuote> Quotes => _quotes.Values.ToList();

    /// <summary>
    /// Jeder echte Live-Trade (kein Abo-Snapshot) eines abonnierten Instruments: (Symbol, Exchange, Tick).
    /// Wird synchron im Empfangs-Thread ausgelöst – Handler müssen schnell sein.
    /// </summary>
    public event Action<string, string, MarketTick>? TradeReceived;

    /// <summary>
    /// Live-Trades eines abonnierten Instruments als <see cref="MarketTick"/>. Aggressor stammt direkt aus
    /// Rithmic (LastTrade.aggressor); Snapshots beim Abo werden NICHT als neue Trades ausgegeben.
    /// </summary>
    public async IAsyncEnumerable<MarketTick> ReadTicksAsync(
        string symbol, string exchange,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<MarketTick>(new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        _tickReaders[id] = (Key(symbol, exchange), channel);
        try
        {
            await foreach (var tick in channel.Reader.ReadAllAsync(cancellationToken))
                yield return tick;
        }
        finally
        {
            _tickReaders.TryRemove(id, out _);
        }
    }

    /// <summary>Front-Month-Kontrakt zu einem Root-Symbol (z. B. "MES" → "MESZ6").</summary>
    public async Task<string?> GetFrontMonthAsync(string rootSymbol, string exchange, CancellationToken cancellationToken = default)
    {
        var responses = await RequireTicker().RequestAsync(new RequestFrontMonthContract
        {
            TemplateId = RithmicTemplates.RequestFrontMonthContract,
            Symbol = rootSymbol,
            Exchange = exchange,
        }, cancellationToken);
        var response = responses.OfType<ResponseFrontMonthContract>().FirstOrDefault(r => r.TradingSymbol.Length > 0);
        return response?.TradingSymbol;
    }

    /// <summary>Historische Minuten-Bars [from, to] aus dem History Plant, inkl. Folgeseiten bei Kürzung.</summary>
    public async Task<IReadOnlyList<RithmicTimeBar>> GetMinuteBarsAsync(
        string symbol, string exchange, DateTimeOffset from, DateTimeOffset to, int periodMinutes = 1,
        CancellationToken cancellationToken = default)
    {
        if (to <= from)
            throw new ArgumentException("'to' muss nach 'from' liegen.", nameof(to));
        var history = _history is { IsLoggedIn: true } h
            ? h
            : throw new InvalidOperationException("Rithmic History-Plant ist nicht verbunden.");

        var bars = new List<RithmicTimeBar>();
        var startIndex = (int)from.ToUnixTimeSeconds();
        var finishIndex = (int)to.ToUnixTimeSeconds();
        for (var page = 0; page < MaxBarPages && startIndex <= finishIndex; page++)
        {
            var responses = await history.RequestAsync(new RequestTimeBarReplay
            {
                TemplateId = RithmicTemplates.RequestTimeBarReplay,
                Symbol = symbol,
                Exchange = exchange,
                BarType = RequestTimeBarReplay.Types.BarType.MinuteBar,
                BarTypePeriod = periodMinutes,
                StartIndex = startIndex,
                FinishIndex = finishIndex,
                TimeOrder = RequestTimeBarReplay.Types.TimeOrder.Forwards,
            }, cancellationToken);

            var pageBars = responses.OfType<ResponseTimeBarReplay>()
                .Where(r => r.RpCode.Count == 0 && r.Marker > 0)
                .Select(r => ToBar(r, periodMinutes))
                .ToList();
            if (pageBars.Count == 0)
                break;

            bars.AddRange(pageBars);
            var lastMarker = pageBars[^1].EndTime.ToUnixTimeSeconds();
            if (lastMarker >= finishIndex)
                break;
            startIndex = (int)lastMarker + 1;
        }

        return bars;
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        var ticker = Interlocked.Exchange(ref _ticker, null);
        var history = Interlocked.Exchange(ref _history, null);
        if (ticker is not null)
        {
            ticker.StreamMessage -= OnTickerMessage;
            await ticker.DisposeAsync();
        }
        if (history is not null)
            await history.DisposeAsync();

        _quotes.Clear();
        _subscriptions.Clear();
        foreach (var reader in _tickReaders.Values)
            reader.Channel.Writer.TryComplete();
        SystemName = null;
        User = null;
    }

    public ValueTask DisposeAsync() => new(DisconnectAsync());

    private RithmicPlantSession RequireTicker() =>
        _ticker is { IsLoggedIn: true } t ? t : throw new InvalidOperationException("Rithmic Ticker-Plant ist nicht verbunden.");

    private void OnTickerMessage(int templateId, byte[] bytes)
    {
        try
        {
            switch (templateId)
            {
                case RithmicTemplates.LastTrade:
                    OnLastTrade(LastTrade.Parser.ParseFrom(bytes));
                    break;
                case RithmicTemplates.BestBidOffer:
                    OnBestBidOffer(BestBidOffer.Parser.ParseFrom(bytes));
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.Warning($"Rithmic-Stream-Message {templateId} nicht verarbeitbar: {ex.Message}");
        }
    }

    private void OnLastTrade(LastTrade trade)
    {
        if ((trade.PresenceBits & (uint)LastTrade.Types.PresenceBits.LastTrade) == 0)
            return;

        var key = Key(trade.Symbol, trade.Exchange);
        var time = RithmicProtocolMapping.FromSsboe(trade.Ssboe, trade.Usecs);
        var price = (decimal)trade.TradePrice;
        var quote = _quotes.AddOrUpdate(key,
            _ => new RithmicQuote { Symbol = trade.Symbol, Exchange = trade.Exchange, LastPrice = price, LastSize = trade.TradeSize, UpdatedAt = time },
            (_, q) => q with { LastPrice = price, LastSize = trade.TradeSize, UpdatedAt = time });

        if (trade.IsSnapshot)
            return; // Stand vor dem Abo – kein neuer Trade.

        var tick = new MarketTick
        {
            Symbol = trade.Symbol,
            Timestamp = time,
            Price = price,
            Volume = trade.TradeSize,
            Bid = quote.Bid ?? 0m,
            Ask = quote.Ask ?? 0m,
            BidSize = quote.BidSize ?? 0,
            AskSize = quote.AskSize ?? 0,
            Aggressor = trade.Aggressor switch
            {
                LastTrade.Types.TransactionType.Buy => AggressorSide.Buy,
                LastTrade.Types.TransactionType.Sell => AggressorSide.Sell,
                _ => AggressorSide.Unknown,
            },
        };
        foreach (var reader in _tickReaders.Values)
        {
            if (reader.Key == key)
                reader.Channel.Writer.TryWrite(tick);
        }

        try
        {
            TradeReceived?.Invoke(trade.Symbol, trade.Exchange, tick);
        }
        catch (Exception ex)
        {
            _logger.Warning($"TradeReceived-Handler fehlgeschlagen: {ex.Message}");
        }
    }

    private void OnBestBidOffer(BestBidOffer bbo)
    {
        var hasBid = (bbo.PresenceBits & (uint)BestBidOffer.Types.PresenceBits.Bid) != 0;
        var hasAsk = (bbo.PresenceBits & (uint)BestBidOffer.Types.PresenceBits.Ask) != 0;
        var clearBid = (bbo.ClearBits & (uint)BestBidOffer.Types.PresenceBits.Bid) != 0;
        var clearAsk = (bbo.ClearBits & (uint)BestBidOffer.Types.PresenceBits.Ask) != 0;
        var time = RithmicProtocolMapping.FromSsboe(bbo.Ssboe, bbo.Usecs);

        _quotes.AddOrUpdate(Key(bbo.Symbol, bbo.Exchange),
            _ => Apply(new RithmicQuote { Symbol = bbo.Symbol, Exchange = bbo.Exchange }),
            (_, q) => Apply(q));

        RithmicQuote Apply(RithmicQuote q) => q with
        {
            Bid = clearBid ? null : hasBid ? (decimal)bbo.BidPrice : q.Bid,
            BidSize = clearBid ? null : hasBid ? bbo.BidSize : q.BidSize,
            Ask = clearAsk ? null : hasAsk ? (decimal)bbo.AskPrice : q.Ask,
            AskSize = clearAsk ? null : hasAsk ? bbo.AskSize : q.AskSize,
            UpdatedAt = time,
        };
    }

    private static RithmicTimeBar ToBar(ResponseTimeBarReplay r, int periodMinutes) => new()
    {
        Symbol = r.Symbol,
        Exchange = r.Exchange,
        EndTime = DateTimeOffset.FromUnixTimeSeconds(r.Marker),
        PeriodMinutes = periodMinutes,
        Open = (decimal)r.OpenPrice,
        High = (decimal)r.HighPrice,
        Low = (decimal)r.LowPrice,
        Close = (decimal)r.ClosePrice,
        Volume = r.Volume,
        BidVolume = r.BidVolume,
        AskVolume = r.AskVolume,
        NumTrades = r.NumTrades,
    };

    private static string Key(string symbol, string exchange) => $"{exchange}:{symbol}";
}
