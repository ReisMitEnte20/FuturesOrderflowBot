namespace TradingBot.PaperTrading;

/// <summary>
/// Standard-Implementierung von <see cref="IPaperTradingEngine"/>. Erzeugt pro Start eine
/// neue, unabhängige <see cref="PaperTradingSession"/>. Enthält selbst keine Trading-Logik
/// und hat keinerlei Broker-/Netzwerk-Abhängigkeiten.
/// </summary>
public sealed class PaperTradingEngine : IPaperTradingEngine
{
    public PaperTradingSession Start(PaperTradingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = new PaperTradingSession(request);
        session.Start(cancellationToken);
        return session;
    }
}
