namespace TradingBot.Application.MarketData;

/// <summary>
/// Wird geworfen, wenn Orderflow (Delta/Bid/Ask-Volumen) angefordert wird, die Ticks aber
/// keine Aggressor-Klassifikation tragen. Verhindert das Erfinden von Orderflow-Daten.
/// </summary>
public sealed class OrderFlowUnavailableException : InvalidOperationException
{
    public OrderFlowUnavailableException(string message) : base(message) { }
}
