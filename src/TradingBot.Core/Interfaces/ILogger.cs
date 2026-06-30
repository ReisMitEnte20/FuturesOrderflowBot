namespace TradingBot.Core.Interfaces;

/// <summary>Minimale Logging-Abstraktion. Konkrete Senke (Serilog o. Ä.) folgt später.</summary>
public interface ILogger
{
    void Info(string message);
    void Warning(string message);
    void Error(string message, Exception? exception = null);
}
