using TradingBot.Infrastructure.MarketData.Rithmic.Protocol.Messages;

namespace TradingBot.Infrastructure.MarketData.Rithmic.Protocol;

/// <summary>
/// Erlaubte Rithmic-Plants. Bewusst NUR Marktdaten: Order- und PnL-Plant sind hier nicht darstellbar,
/// damit über diesen Client keine Orders/Positionen angesprochen werden können.
/// </summary>
public enum RithmicPlant
{
    Ticker,
    History,
}

/// <summary>Template-IDs der verwendeten R|Protocol-Messages (Quelle: async_rithmic TEMPLATES_MAP).</summary>
public static class RithmicTemplates
{
    public const int RequestLogin = 10;
    public const int ResponseLogin = 11;
    public const int RequestLogout = 12;
    public const int ResponseLogout = 13;
    public const int RequestRithmicSystemInfo = 16;
    public const int ResponseRithmicSystemInfo = 17;
    public const int RequestHeartbeat = 18;
    public const int ResponseHeartbeat = 19;
    public const int Reject = 75;
    public const int ForcedLogout = 77;
    public const int RequestMarketDataUpdate = 100;
    public const int ResponseMarketDataUpdate = 101;
    public const int RequestFrontMonthContract = 113;
    public const int ResponseFrontMonthContract = 114;
    public const int LastTrade = 150;
    public const int BestBidOffer = 151;
    public const int RequestTimeBarReplay = 202;
    public const int ResponseTimeBarReplay = 203;
}

/// <summary>Verbindungsparameter. Das Passwort ist NICHT Teil davon (nur einmalig beim Login übergeben).</summary>
public sealed record RithmicProtocolOptions
{
    /// <summary>Gateway-URI, z. B. wss://rituz00100.rithmic.com:443 (Rithmic Test).</summary>
    public required Uri GatewayUri { get; init; }

    /// <summary>Bei Rithmic registrierter App-Name (Conformance).</summary>
    public required string AppName { get; init; }

    public required string AppVersion { get; init; }

    /// <summary>Timeout für Verbindungsaufbau und einzelne Request/Response-Zyklen.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>R|Protocol-Template-Version des Logins (wie async_rithmic).</summary>
    public string TemplateVersion { get; init; } = "3.9";
}

public sealed record RithmicLoginCredentials(string User, string Password, string SystemName)
{
    // Passwort nie in Logs/ToString ausgeben.
    public override string ToString() => $"RithmicLoginCredentials {{ User = {User}, SystemName = {SystemName} }}";
}

public sealed class RithmicProtocolException : Exception
{
    public RithmicProtocolException(string message, IReadOnlyList<string>? rpCode = null) : base(message)
    {
        RpCode = rpCode ?? [];
    }

    /// <summary>Rohe rp_code-Werte der Rithmic-Antwort (z. B. ["13", "permission denied"]).</summary>
    public IReadOnlyList<string> RpCode { get; }
}

/// <summary>Aktueller Best Bid/Offer + letzter Trade eines Instruments (nur echte Werte, sonst null).</summary>
public sealed record RithmicQuote
{
    public required string Symbol { get; init; }
    public required string Exchange { get; init; }
    public decimal? Bid { get; init; }
    public int? BidSize { get; init; }
    public decimal? Ask { get; init; }
    public int? AskSize { get; init; }
    public decimal? LastPrice { get; init; }
    public int? LastSize { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

/// <summary>Historische Zeit-Bar aus dem History Plant. Bid/Ask-Volumen stammen direkt von Rithmic.</summary>
public sealed record RithmicTimeBar
{
    public required string Symbol { get; init; }
    public required string Exchange { get; init; }

    /// <summary>Bar-Ende (Rithmic "marker", Sekunden seit Epoch, UTC).</summary>
    public required DateTimeOffset EndTime { get; init; }

    public required int PeriodMinutes { get; init; }
    public decimal Open { get; init; }
    public decimal High { get; init; }
    public decimal Low { get; init; }
    public decimal Close { get; init; }
    public ulong Volume { get; init; }
    public ulong BidVolume { get; init; }
    public ulong AskVolume { get; init; }
    public ulong NumTrades { get; init; }
}

internal static class RithmicProtocolMapping
{
    public static RequestLogin.Types.SysInfraType ToInfraType(RithmicPlant plant) => plant switch
    {
        RithmicPlant.Ticker => RequestLogin.Types.SysInfraType.TickerPlant,
        RithmicPlant.History => RequestLogin.Types.SysInfraType.HistoryPlant,
        _ => throw new ArgumentOutOfRangeException(nameof(plant), plant, "Nur Ticker- und History-Plant sind erlaubt."),
    };

    public static DateTimeOffset FromSsboe(int ssboe, int usecs) =>
        DateTimeOffset.FromUnixTimeSeconds(ssboe).AddTicks(usecs * 10L);

    public static bool IsSuccess(IList<string> rpCode) => rpCode.Count > 0 && rpCode[0] == "0";

    /// <summary>rp_code "7" = keine Daten (kein Fehler).</summary>
    public static bool IsNoData(IList<string> rpCode) => rpCode.Count > 0 && rpCode[0] == "7";

    public static string Describe(IList<string> rpCode) =>
        rpCode.Count == 0 ? "(kein rp_code)" : string.Join(" / ", rpCode);
}
