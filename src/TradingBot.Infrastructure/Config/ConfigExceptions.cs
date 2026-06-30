namespace TradingBot.Infrastructure.Config;

/// <summary>Basisfehler für alle Config-Probleme. Keine stillen Fehler – alles wirft.</summary>
public class ConfigException : Exception
{
    public ConfigException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>Eine erwartete Config-Datei oder ein Verzeichnis fehlt.</summary>
public sealed class ConfigFileNotFoundException : ConfigException
{
    public string Path { get; }

    public ConfigFileNotFoundException(string path)
        : base($"Config-Quelle nicht gefunden: '{path}'.")
        => Path = path;
}

/// <summary>JSON konnte nicht geparst oder deserialisiert werden (ungültig/leer).</summary>
public sealed class ConfigParseException : ConfigException
{
    public string Path { get; }

    public ConfigParseException(string path, string message, Exception? inner = null)
        : base($"Ungültiges JSON in '{path}': {message}", inner)
        => Path = path;
}

/// <summary>Ein Profil/Config-Objekt hat die Validierung nicht bestanden.</summary>
public sealed class ProfileValidationException : ConfigException
{
    public ProfileValidationException(string message) : base($"Validierung fehlgeschlagen: {message}") { }
}
