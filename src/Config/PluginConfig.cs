using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
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
    [Required(AllowEmptyStrings = false)]
    [RegularExpression("None|Preferred|Required|VerifyCA|VerifyFull", ErrorMessage = "DatabaseSslMode must be one of: None, Preferred, Required, VerifyCA, VerifyFull.")]
    public string DatabaseSslMode { get; set; } = "Required";

    [JsonPropertyName("DatabaseSslCa")]
    public string? DatabaseSslCa { get; set; }

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

    // Run database/init.sql and compatible column migrations automatically on startup.
    [JsonPropertyName("AutoCreateSchema")]
    public bool AutoCreateSchema { get; set; } = false;

    // Spatial heatmap data (kill/death/utility positions) is the heaviest write path; disable on small servers.
    [JsonPropertyName("EnablePositionTracking")]
    public bool EnablePositionTracking { get; set; } = true;

    // High-frequency, low-value counters (footsteps, jumps, pings). Off by default: footstep is one
    // of the most frequently fired events in CS2 and the data has little analytical value.
    [JsonPropertyName("EnableMovementTracking")]
    public bool EnableMovementTracking { get; set; } = false;

    // Scrim/match-management system (lobby, captains, picking, knife, pauses). When false, stat
    // tracking runs on every round without requiring a scrim to be started.
    [JsonPropertyName("EnableScrim")]
    public bool EnableScrim { get; set; } = true;

    // CounterStrikeSharp deserializes plugin configs without a string-enum converter, so without this the
    // documented "LogLevel": "Information" fails to load the plugin. Numeric values still work.
    [JsonPropertyName("LogLevel")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
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
            AllowLoadLocalInfile = true,
            SslCa = DatabaseSslCa ?? string.Empty,
            // An empty value passes validation; it must not fall back to Preferred, which accepts plaintext.
            SslMode = Enum.TryParse<MySqlSslMode>(DatabaseSslMode, out var sslMode) ? sslMode : MySqlSslMode.Required
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
        Validator.ValidateObject(this, new ValidationContext(this), validateAllProperties: true);
        ArgumentNullException.ThrowIfNull(Scrim);
        ArgumentNullException.ThrowIfNull(ClutchSettings);
        if (Path.IsPathRooted(Scrim.ScrimConfigPath))
            throw new ArgumentException("ScrimConfigPath must be relative to the CS2 game directory");
        if (Scrim.PlayersPerTeam is < 1 or > 32)
            throw new ArgumentException("PlayersPerTeam must be between 1 and 32");
        if (Scrim.MinReadyPlayers is < 2 or > 64 || Scrim.MinReadyPlayers > Scrim.PlayersPerTeam * 2)
            throw new ArgumentException("MinReadyPlayers must fit within the configured player slots");
        if (Scrim.MapPool.Count == 0 || Scrim.MapPool.Any(map => string.IsNullOrWhiteSpace(map) || map.Any(character => !char.IsLetterOrDigit(character) && character is not ('_' or '-'))))
            throw new ArgumentException("MapPool must contain safe CS2 map names");
        if (Scrim.WhitelistEnabled && Scrim.WhitelistedSteamIds.Count == 0)
            throw new ArgumentException("WhitelistedSteamIds must contain at least one Steam ID when the whitelist is enabled");
        if (string.IsNullOrWhiteSpace(Scrim.LiveConfigPath) || string.IsNullOrWhiteSpace(Scrim.KnifeConfigPath))
            throw new ArgumentException("LiveConfigPath and KnifeConfigPath are required");
        if (ClutchSettings.BaseMultiplier < 0 || ClutchSettings.DifficultyWeight < 0)
            throw new ArgumentException("Clutch settings cannot be negative");
    }
}

public sealed class ClutchConfig
{
    public decimal BaseMultiplier { get; set; } = 1.0m;
    public decimal DifficultyWeight { get; set; } = 0.2m;
}
