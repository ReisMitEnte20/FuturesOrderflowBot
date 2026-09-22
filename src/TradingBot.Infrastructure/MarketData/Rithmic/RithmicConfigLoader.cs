using System.Text.Json;
using TradingBot.Core.Interfaces;
using TradingBot.Infrastructure.MarketData.Rithmic.Models;

namespace TradingBot.Infrastructure.MarketData.Rithmic;

public static class RithmicConfigLoader
{
    public static RithmicConfig FromFile(string configPath, ILogger? logger = null)
    {
        var logger2 = logger ?? NullLogger.Instance;

        if (!File.Exists(configPath))
            throw new FileNotFoundException($"Rithmic config not found: '{configPath}'", configPath);

        var json = File.ReadAllText(configPath);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var config = JsonSerializer.Deserialize<RithmicConfig>(json, options);
        if (config is null)
            throw new RithmicException("Failed to parse Rithmic config.");

        logger2.Info($"Loaded Rithmic config from '{configPath}'.");
        return config;
    }

    public static RithmicConfig FromEnvironment(
        string usernameEnv = "RITHMIC_USERNAME",
        string passwordEnv = "RITHMIC_PASSWORD",
        string baseUrlEnv = "RITHMIC_BASE_URL",
        ILogger? logger = null)
    {
        var username = Environment.GetEnvironmentVariable(usernameEnv);
        var password = Environment.GetEnvironmentVariable(passwordEnv);

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            throw new RithmicException(
                $"Missing environment variables: {usernameEnv} and/or {passwordEnv}. "
                + "Set them or use a config file via RithmicConfigLoader.FromFile().");

        var baseUrl = Environment.GetEnvironmentVariable(baseUrlEnv) ?? "https://api.rithmic.com";

        var config = new RithmicConfig
        {
            Username = username,
            Password = password,
            BaseUrl = baseUrl,
        };

        logger?.Info("Loaded Rithmic config from environment variables.");
        return config;
    }
}