namespace TradingBot.Domain.Models;

/// <summary>Ein Preislevel eines Volume-Profiles (Volumen je Preis über eine Session).</summary>
public sealed record VolumeProfileLevel
{
    public decimal PriceLevel { get; init; }
    public decimal VolumeAtPrice { get; init; }

    public decimal? BidVolumeAtPrice { get; init; }
    public decimal? AskVolumeAtPrice { get; init; }

    /// <summary>Optionale Klassifikation des Datenlieferanten (null = nicht klassifiziert).</summary>
    public bool? IsHighVolumeNode { get; init; }
    public bool? IsLowVolumeNode { get; init; }
}

/// <summary>Volume-Profile einer Session. Grundlage für HVN/LVN-Analysen.</summary>
public sealed record VolumeProfile
{
    public required string Symbol { get; init; }
    public DateOnly SessionDate { get; init; }
    public IReadOnlyList<VolumeProfileLevel> Levels { get; init; } = Array.Empty<VolumeProfileLevel>();

    public decimal TotalVolume => Levels.Sum(l => l.VolumeAtPrice);

    /// <summary>Point of Control: Preislevel mit dem höchsten Volumen (null bei leerem Profil).</summary>
    public decimal? PointOfControl => Levels.Count == 0
        ? null
        : Levels.MaxBy(l => l.VolumeAtPrice)!.PriceLevel;
}
