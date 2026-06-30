using System.Text.Json;
using TradingBot.Core.Interfaces;

namespace TradingBot.Infrastructure.Config;

/// <summary>
/// JSON-basierte Implementierung von <see cref="IConfigService"/>.
/// Lädt/speichert typisierte Configs. Fehlende Dateien und ungültiges JSON werden
/// als klare Exceptions gemeldet – niemals still verschluckt.
/// </summary>
public sealed class JsonConfigService : IConfigService
{
    private readonly JsonSerializerOptions _options;

    public JsonConfigService(JsonSerializerOptions? options = null)
        => _options = options ?? TradingJsonOptions.Default;

    public bool Exists(string path) => File.Exists(path);

    public async Task<T> LoadAsync<T>(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Pfad darf nicht leer sein.", nameof(path));

        if (!File.Exists(path))
            throw new ConfigFileNotFoundException(path);

        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, useAsync: true);

        try
        {
            var result = await JsonSerializer.DeserializeAsync<T>(stream, _options, cancellationToken)
                .ConfigureAwait(false);

            if (result is null)
                throw new ConfigParseException(path, "Deserialisierung ergab null (leere oder 'null'-Datei).");

            return result;
        }
        catch (JsonException ex)
        {
            throw new ConfigParseException(path, ex.Message, ex);
        }
    }

    public async Task SaveAsync<T>(string path, T value, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Pfad darf nicht leer sein.", nameof(path));

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        await using var stream = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 4096, useAsync: true);

        await JsonSerializer.SerializeAsync(stream, value, _options, cancellationToken)
            .ConfigureAwait(false);
    }
}
