using TradingBot.Domain.Models;

namespace TradingBot.Core.Interfaces;

/// <summary>
/// Zentrales Sicherheits-Gate. Prüft jede Order-Absicht gegen alle Risk-Regeln und
/// gibt ein vollständiges <see cref="RiskDecision"/> zurück. Der RiskManager sendet
/// NIEMALS Orders und verändert keinen Broker-/Positionszustand – er entscheidet nur.
/// </summary>
public interface IRiskManager
{
    /// <summary>
    /// Bewertet eine Order-Absicht. Liefert Approved/Reason/Message sowie
    /// freigegebene Kontrakte und aktuelle Risiko-Auslastung. Synchron und
    /// deterministisch bezogen auf den übergebenen Kontext und den aktuellen
    /// Zustand von KillSwitch/SafetyMonitor/Clock.
    /// </summary>
    RiskDecision Evaluate(RiskEvaluationRequest request);
}
