using System;
using System.IO;
using System.Text.Json;
using System.Threading;
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
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private readonly SemaphoreSlim _fileGate = new(1, 1);
    private readonly ILogger<JsonRecoveryService> _logger = logger;
    private readonly string _filePath = Path.Combine(Server.GameDirectory, "csgo", "addons", "counterstrikesharp", "plugins", "statsCollector", "scrim_state.json");
    private readonly string _tempPath = Path.Combine(Server.GameDirectory, "csgo", "addons", "counterstrikesharp", "plugins", "statsCollector", "scrim_state.json.tmp");

    public async Task SaveScrimStateAsync<T>(T data)
    {
        await _fileGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (directory != null) Directory.CreateDirectory(directory);

            var json = JsonSerializer.Serialize(data, SerializerOptions);
            await File.WriteAllTextAsync(_tempPath, json);
            File.Move(_tempPath, _filePath, true);

            _logger.LogDebug("Scrim state recovery saved atomically to {Path}", _filePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save scrim recovery state to {Path}", _filePath);
            throw;
        }
        finally
        {
            _fileGate.Release();
        }
    }

    public async Task<T?> LoadScrimStateAsync<T>()
    {
        await _fileGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath)) return default;

            var json = await File.ReadAllTextAsync(_filePath);
            return JsonSerializer.Deserialize<T>(json, SerializerOptions);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load scrim recovery state from {Path}", _filePath);
            return default;
        }
        finally
        {
            _fileGate.Release();
        }
    }

    public async Task ClearScrimStateAsync()
    {
        await _fileGate.WaitAsync().ConfigureAwait(false);
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
        finally
        {
            _fileGate.Release();
        }
    }
}
