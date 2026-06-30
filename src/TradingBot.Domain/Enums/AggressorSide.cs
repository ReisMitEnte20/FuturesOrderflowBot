namespace TradingBot.Domain.Enums;

/// <summary>
/// Aggressor-Seite eines Trades (für Orderflow). <see cref="Buy"/> = Käufer hob den Ask
/// (Volumen zählt zum Ask), <see cref="Sell"/> = Verkäufer traf den Bid (zum Bid).
/// <see cref="Unknown"/> = nicht klassifiziert – darf NIEMALS für Orderflow-Delta erfunden werden.
/// </summary>
public enum AggressorSide
{
    Unknown = 0,
    Buy = 1,
    Sell = 2
}
