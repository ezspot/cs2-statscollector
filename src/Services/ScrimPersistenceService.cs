using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using CounterStrikeSharp.API;

namespace statsCollector.Services;

public sealed class ScrimPersistenceService(ILogger<ScrimPersistenceService> logger) : IScrimPersistenceService
{
    private readonly ILogger<ScrimPersistenceService> _logger = logger;
    private readonly string _filePath = Path.Combine(Server.GameDirectory, "csgo", "addons", "counterstrikesharp", "plugins", "statsCollector", "scrim_state_recovery.json");

    public async Task SaveStateAsync(ScrimRecoveryData data)
    {
        try
        {
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_filePath, json);
            _logger.LogDebug("Scrim state saved to {Path}", _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save scrim state");
        }
    }

    public async Task<ScrimRecoveryData?> LoadStateAsync()
    {
        try
        {
            if (!File.Exists(_filePath)) return null;
            var json = await File.ReadAllTextAsync(_filePath);
            return JsonSerializer.Deserialize<ScrimRecoveryData>(json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load scrim state");
            return null;
        }
    }

    public Task ClearStateAsync()
    {
        try
        {
            if (File.Exists(_filePath)) File.Delete(_filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clear scrim state");
        }
        return Task.CompletedTask;
    }
}
