namespace TradingBot.Domain.Enums;

/// <summary>Art der importierten Marktdaten – bestimmt, welche Analysen ehrlich möglich sind.</summary>
public enum MarketDataSourceType
{
    /// <summary>Nur Timestamp/Symbol/Price/Volume → ausschließlich OHLCV-Analysen.</summary>
    MinimalTick = 0,
    /// <summary>Ticks mit Aggressor-Klassifikation (TradeDirection bzw. Bid/Ask-Volumen).</summary>
    AggressorTick = 1,
    /// <summary>Fertige Orderflow-Bars mit Bid/Ask-Volumen und Delta.</summary>
    OrderFlowBars = 2,
    /// <summary>Footprint-Daten: Bid/Ask-Volumen je Preislevel je Bar.</summary>
    Footprint = 3,
    /// <summary>Volume-Profile: Volumen je Preislevel je Session.</summary>
    VolumeProfile = 4
}
