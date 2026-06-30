using TradingBot.Domain.Models;

namespace TradingBot.Core.Interfaces;

/// <summary>
/// Einziger Punkt mit echtem Broker-Kontakt. Kapselt broker-spezifische API-Regeln,
/// damit Strategie/Risk/Order brokerunabhängig bleiben. Enthält selbst KEINE Risk-Logik.
/// Am Anfang nur als MockBroker implementiert.
/// </summary>
public interface IBrokerExecutionAdapter
{
    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Sendet eine bereits vom OrderManager geprüfte Order an den Broker.</summary>
    Task<OrderResult> SubmitOrderAsync(OrderRequest request, CancellationToken cancellationToken = default);

    Task<OrderResult> CancelOrderAsync(Guid orderId, CancellationToken cancellationToken = default);

    /// <summary>Cancel/Replace einer bestehenden Order (sofern vom Broker unterstützt).</summary>
    Task<OrderResult> ReplaceOrderAsync(OrderRequest request, CancellationToken cancellationToken = default);

    /// <summary>Liefert die broker-seitige Position für den Abgleich (Reconciliation).</summary>
    Task<Position?> GetBrokerPositionAsync(string symbol, CancellationToken cancellationToken = default);

    /// <summary>Wird bei jeder (Teil-)Ausführung ausgelöst.</summary>
    event EventHandler<FillEvent>? Filled;
}
