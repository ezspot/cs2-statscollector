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
    Task SaveScrimStateAsync<T>(T data);
    Task<T?> LoadScrimStateAsync<T>();
    Task ClearScrimStateAsync();
}

public sealed class JsonRecoveryService(ILogger<JsonRecoveryService> logger) : IJsonRecoveryService
{
    private readonly ILogger<JsonRecoveryService> _logger = logger;
    private readonly string _filePath = Path.Combine(Server.GameDirectory, "csgo", "addons", "counterstrikesharp", "plugins", "statsCollector", "scrim_state.json");
    private readonly string _tempPath = Path.Combine(Server.GameDirectory, "csgo", "addons", "counterstrikesharp", "plugins", "statsCollector", "scrim_state.json.tmp");

    public async Task SaveScrimStateAsync<T>(T data)
    {
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (directory != null && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            
            // Atomic write: Write to temp file first
            await File.WriteAllTextAsync(_tempPath, json);
            
            // Then move to final path (overwrite)
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }
            File.Move(_tempPath, _filePath);

            _logger.LogDebug("Scrim state recovery saved atomically to {Path}", _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save scrim recovery state to {Path}", _filePath);
        }
    }

    public async Task<T?> LoadScrimStateAsync<T>()
    {
        try
        {
            if (!File.Exists(_filePath)) return default;

            var lastWrite = File.GetLastWriteTimeUtc(_filePath);
            if (DateTime.UtcNow - lastWrite > TimeSpan.FromMinutes(15))
            {
                _logger.LogWarning("Recovery file is older than 15 minutes, skipping recovery.");
                await ClearScrimStateAsync();
                return default;
            }

            var json = await File.ReadAllTextAsync(_filePath);
            return JsonSerializer.Deserialize<T>(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load scrim recovery state from {Path}", _filePath);
            return default;
        }
    }

    public Task ClearScrimStateAsync()
    {
        try
        {
            if (File.Exists(_filePath)) File.Delete(_filePath);
            if (File.Exists(_tempPath)) File.Delete(_tempPath);
            _logger.LogInformation("Scrim recovery state cleared.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear scrim recovery state at {Path}", _filePath);
        }
        return Task.CompletedTask;
    }
}
