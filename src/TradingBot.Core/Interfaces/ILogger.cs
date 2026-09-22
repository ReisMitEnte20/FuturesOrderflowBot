namespace TradingBot.Core.Interfaces;

/// <summary>Minimale Logging-Abstraktion. Konkrete Senke (Serilog o. Ä.) folgt später.</summary>
public interface ILogger
{
    void Info(string message);
    void Warning(string message);
    void Error(string message, Exception? exception = null);
}

public static class NullLogger
{
    public static readonly ILogger Instance = new NullLoggerImpl();

    private sealed class NullLoggerImpl : ILogger
    {
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception? exception = null) { }
    }
}
