namespace TradingBot.Domain.Enums;

/// <summary>Schweregrad eines Datenqualitäts-Befunds.</summary>
public enum DataQualitySeverity
{
    /// <summary>Hinweis (z. B. abgeleiteter Wert, kleine Lücke).</summary>
    Info = 0,
    /// <summary>Auffälligkeit, Daten nutzbar, aber mit Vorsicht.</summary>
    Warning = 1,
    /// <summary>Datenfehler – betroffene Zeile/Bar wurde verworfen oder Datensatz ist unzuverlässig.</summary>
    Error = 2
}
