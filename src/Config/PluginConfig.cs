using System;
using System.ComponentModel.DataAnnotations;
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

    [JsonPropertyName("PersistenceChannelCapacity")]
    [Range(50, 5000)]
    public int PersistenceChannelCapacity { get; set; } = 1000;

    [JsonPropertyName("AutoSaveSeconds")]
    [Range(30, 300)]
    public int AutoSaveSeconds { get; set; } = 60;

    [JsonPropertyName("TradeWindowSeconds")]
    [Range(1, 15)]
    public int TradeWindowSeconds { get; set; } = 5;

    [JsonPropertyName("ClutchSettings")]
    public ClutchConfig ClutchSettings { get; set; } = new();

    [JsonPropertyName("DeathmatchMode")]
    public bool DeathmatchMode { get; set; } = false;

    // Run database/init.sql automatically on startup if the schema is missing.
    [JsonPropertyName("AutoCreateSchema")]
    public bool AutoCreateSchema { get; set; } = false;

    // Spatial heatmap data (kill/death/utility positions) is the heaviest write path; disable on small servers.
    [JsonPropertyName("EnablePositionTracking")]
    public bool EnablePositionTracking { get; set; } = true;

    // High-frequency, low-value counters (footsteps, jumps, pings).
    [JsonPropertyName("EnableMovementTracking")]
    public bool EnableMovementTracking { get; set; } = true;

    // Scrim/match-management system (lobby, captains, picking, knife, pauses). When false, stat
    // tracking runs on every round without requiring a scrim to be started.
    [JsonPropertyName("EnableScrim")]
    public bool EnableScrim { get; set; } = true;

    [JsonPropertyName("LogLevel")]
    public LogLevel LogLevel { get; set; } = LogLevel.Information;

    [JsonPropertyName("Scrim")]
    public ScrimConfig Scrim { get; set; } = new();

    public string BuildConnectionString(bool includeDatabase = true)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = DatabaseHost,
            Port = (uint)DatabasePort,
            Database = includeDatabase ? DatabaseName : string.Empty,
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
        // Overlay only the keys actually present as environment variables onto the
        // JSON-loaded values; every other property keeps its configured value.
        new ConfigurationBuilder()
            .AddEnvironmentVariables(EnvironmentPrefix)
            .Build()
            .Bind(this);

        return this;
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
