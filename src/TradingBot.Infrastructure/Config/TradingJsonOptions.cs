using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingBot.Infrastructure.Config;

/// <summary>
/// Zentrale JSON-Optionen für alle Config-Operationen: camelCase, Enums als Strings,
/// case-insensitive, eingerückt beim Speichern. Unbekannte Felder (z. B. "note")
/// werden bewusst ignoriert, damit Beispieldateien Dokumentationsfelder tragen können.
/// </summary>
public static class TradingJsonOptions
{
    public static JsonSerializerOptions Default { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }
}
