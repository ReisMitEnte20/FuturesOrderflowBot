namespace TradingBot.Domain.Enums;

/// <summary>Art des Handelskontos (relevant u. a. für Funded/Prop-Konten).</summary>
public enum AccountType
{
    Demo = 0,
    Paper = 1,
    Evaluation = 2,
    Funded = 3,
    Live = 4
}
