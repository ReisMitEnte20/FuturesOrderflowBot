using TradingBot.Core.Interfaces;

namespace TradingBot.Infrastructure.Logging;

/// <summary>No-op-Logger als sicherer Default, bis eine echte Senke (Serilog) konfiguriert ist.</summary>
public sealed class NullLogger : ILogger
{
    public static readonly NullLogger Instance = new();

    public void Info(string message) { }
    public void Warning(string message) { }
    public void Error(string message, Exception? exception = null) { }
}
