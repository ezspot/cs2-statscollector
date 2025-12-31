using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;
using statsCollector.Domain;

namespace statsCollector.Services;

public interface IJsonRecoveryService
{
    Task SaveScrimStateAsync(ScrimRecoveryData data);
    Task<ScrimRecoveryData?> LoadScrimStateAsync();
    Task ClearScrimStateAsync();
}

public sealed class JsonRecoveryService(ILogger<JsonRecoveryService> logger) : IJsonRecoveryService
{
    private readonly ILogger<JsonRecoveryService> _logger = logger;
    private readonly string _filePath = Path.Combine(Server.GameDirectory, "csgo", "addons", "counterstrikesharp", "plugins", "statsCollector", "scrim_state.json");

    public async Task SaveScrimStateAsync(ScrimRecoveryData data)
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (directory != null && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_filePath, json);
            _logger.LogDebug("Scrim state recovery saved to {Path}", _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save scrim recovery state to {Path}", _filePath);
        }
    }

    public async Task<ScrimRecoveryData?> LoadScrimStateAsync()
    {
        try
        {
            if (!File.Exists(_filePath)) return null;
            var json = await File.ReadAllTextAsync(_filePath);
            return JsonSerializer.Deserialize<ScrimRecoveryData>(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load scrim recovery state from {Path}", _filePath);
            return null;
        }
    }

    public async Task ClearScrimStateAsync()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
                _logger.LogInformation("Scrim recovery state cleared.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear scrim recovery state at {Path}", _filePath);
        }
    }
}
