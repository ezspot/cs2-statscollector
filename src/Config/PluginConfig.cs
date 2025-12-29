using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Configuration;
using MySqlConnector;

namespace statsCollector.Config;

public sealed class PluginConfig : BasePluginConfig
{
    internal const string EnvironmentPrefix = "STATSCOLLECTOR_";

    [JsonPropertyName("DatabaseHost")]
    [Required(AllowEmptyStrings = false)]
    public string DatabaseHost { get; set; } = "127.0.0.1";

    [JsonPropertyName("DatabasePort")]
    [Range(1, 65535)]
    public int DatabasePort { get; set; } = 3306;

    [JsonPropertyName("DatabaseName")]
    [Required(AllowEmptyStrings = false)]
    public string DatabaseName { get; set; } = "cs2_statscollector";

    [JsonPropertyName("DatabaseUsername")]
    [Required(AllowEmptyStrings = false)]
    public string DatabaseUsername { get; set; } = "cs2_statscollector";

    [JsonPropertyName("DatabasePassword")]
    [Required(AllowEmptyStrings = false)]
    public string DatabasePassword { get; set; } = string.Empty;

    [JsonPropertyName("DatabaseSslMode")]
    [RegularExpression("None|Preferred|Required|VerifyCA|VerifyFull", ErrorMessage = "DatabaseSslMode must be one of: None, Preferred, Required, VerifyCA, VerifyFull.")]
    public string DatabaseSslMode { get; set; } = "Required";

    [JsonPropertyName("FlushConcurrency")]
    [Range(1, 32)]
    public int FlushConcurrency { get; set; } = 4;

    [JsonPropertyName("PersistenceChannelCapacity")]
    [Range(50, 5000)]
    public int PersistenceChannelCapacity { get; set; } = 1000;

    [JsonPropertyName("AutoSaveSeconds")]
    [Range(30, 300)]
    public int AutoSaveSeconds { get; set; } = 60;

    [JsonPropertyName("TradeWindowSeconds")]
    [Range(1, 15)]
    public int TradeWindowSeconds { get; set; } = 5;

    [JsonPropertyName("TradeDistanceThreshold")]
    [Range(100.0, 5000.0)]
    public float TradeDistanceThreshold { get; set; } = 1000.0f;

    [JsonPropertyName("ClutchSettings")]
    public ClutchConfig ClutchSettings { get; set; } = new();

    [JsonPropertyName("DeathmatchMode")]
    public bool DeathmatchMode { get; set; } = false;

    [JsonPropertyName("LogLevel")]
    public LogLevel LogLevel { get; set; } = LogLevel.Information;

    [JsonPropertyName("Scrim")]
    public ScrimConfig Scrim { get; set; } = new();

    public string BuildConnectionString()
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = DatabaseHost,
            Port = (uint)DatabasePort,
            Database = DatabaseName,
            UserID = DatabaseUsername,
            Password = DatabasePassword,
            MinimumPoolSize = 0,
            MaximumPoolSize = 50,
            ConnectionReset = true,
            ConnectionIdleTimeout = 300,
            SslMode = Enum.TryParse<MySqlSslMode>(DatabaseSslMode, out var sslMode) ? sslMode : MySqlSslMode.Preferred
        };

        return builder.ConnectionString;
    }

    public PluginConfig WithEnvironmentOverrides()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(ToConfigurationDictionary())
            .AddEnvironmentVariables(EnvironmentPrefix)
            .Build();

        var merged = new PluginConfig();
        configuration.Bind(merged);
        return merged;
    }

    public IDictionary<string, string?> ToConfigurationDictionary()
    {
        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(DatabaseHost)] = DatabaseHost,
            [nameof(DatabasePort)] = DatabasePort.ToString(CultureInfo.InvariantCulture),
            [nameof(DatabaseName)] = DatabaseName,
            [nameof(DatabaseUsername)] = DatabaseUsername,
            [nameof(DatabasePassword)] = DatabasePassword,
            [nameof(DatabaseSslMode)] = DatabaseSslMode,
            [nameof(FlushConcurrency)] = FlushConcurrency.ToString(CultureInfo.InvariantCulture),
            [nameof(PersistenceChannelCapacity)] = PersistenceChannelCapacity.ToString(CultureInfo.InvariantCulture),
            [nameof(AutoSaveSeconds)] = AutoSaveSeconds.ToString(CultureInfo.InvariantCulture)
        };
    }

    public void Validate()
    {
        // Basic validation - can be expanded as needed
        if (string.IsNullOrWhiteSpace(DatabaseHost))
            throw new ArgumentException("DatabaseHost is required");
        if (string.IsNullOrWhiteSpace(DatabaseName))
            throw new ArgumentException("DatabaseName is required");
        if (string.IsNullOrWhiteSpace(DatabaseUsername))
            throw new ArgumentException("DatabaseUsername is required");
    }
}

public sealed class ClutchConfig
{
    public decimal BaseMultiplier { get; set; } = 1.0m;
    public decimal DifficultyWeight { get; set; } = 0.2m;
}
