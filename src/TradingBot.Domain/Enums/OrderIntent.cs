namespace TradingBot.Domain.Enums;

/// <summary>
/// Wirkung einer Order auf das bestehende Risiko. Entscheidet, welche Risk-Regeln greifen.
/// <para>RISIKO-ERHÖHEND (volle Entry-Prüfung): <see cref="Entry"/>, <see cref="Add"/>.</para>
/// <para>RISIKO-REDUZIEREND (Entry-Business-Regeln übersprungen): <see cref="Reduce"/>,
/// <see cref="Close"/>, <see cref="Flatten"/> – eine offene Position muss immer geschlossen
/// werden können.</para>
/// </summary>
public enum OrderIntent
{
    /// <summary>Neue Position aus Flat eröffnen.</summary>
    Entry = 0,
    /// <summary>Bestehende Position in gleicher Richtung vergrößern.</summary>
    Add = 1,
    /// <summary>Bestehende Position teilweise verkleinern.</summary>
    Reduce = 2,
    /// <summary>Bestehende Position vollständig schließen.</summary>
    Close = 3,
    /// <summary>Notfall-Glattstellung (überbrückt auch den Kill Switch).</summary>
    Flatten = 4
}
