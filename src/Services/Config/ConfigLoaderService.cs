using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using statsCollector.Config;

namespace statsCollector.Services;

public class ConfigLoaderService : IConfigLoaderService
{
    private readonly ILogger<ConfigLoaderService> _logger;
    private readonly PluginConfig _config;
    private readonly IGameScheduler _scheduler;
    private readonly HashSet<ulong> _whitelistedSteamIds = new();
    private readonly string _gameDir;

    public ConfigLoaderService(
        ILogger<ConfigLoaderService> logger, 
        IOptions<PluginConfig> config,
        IGameScheduler scheduler)
    {
        _logger = logger;
        _config = config.Value;
        _scheduler = scheduler;
        _gameDir = Server.GameDirectory;
    }

    public async Task LoadAndExecuteConfigAsync(string fileName)
    {
        var configPath = Path.Combine(_gameDir, "csgo", _config.Scrim.MatchZyConfigPath, fileName);
        
        if (!File.Exists(configPath))
        {
            _logger.LogWarning("Config file not found: {Path}", configPath);
            return;
        }

        _logger.LogInformation("Loading config file: {Path}", configPath);
        var lines = await File.ReadAllLinesAsync(configPath);
        await ExecuteLinesAsync(lines);
    }

    public Task ExecuteLinesAsync(string[] lines)
    {
        _scheduler.Schedule(() =>
        {
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith("//") || trimmedLine.StartsWith("#"))
                    continue;

                Server.ExecuteCommand(trimmedLine);
            }
        });
        return Task.CompletedTask;
    }

    public bool IsPlayerWhitelisted(ulong steamId)
    {
        if (!_config.Scrim.WhitelistEnabled) return true;
        return _whitelistedSteamIds.Contains(steamId);
    }

    public async Task ReloadWhitelistAsync()
    {
        _whitelistedSteamIds.Clear();
        var whitelistPath = Path.Combine(_gameDir, "csgo", _config.Scrim.MatchZyConfigPath, "whitelist.cfg");

        if (!File.Exists(whitelistPath))
        {
            _logger.LogWarning("Whitelist file not found: {Path}", whitelistPath);
            return;
        }

        var lines = await File.ReadAllLinesAsync(whitelistPath);
        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            if (ulong.TryParse(trimmedLine, out var steamId))
            {
                _whitelistedSteamIds.Add(steamId);
            }
        }
        _logger.LogInformation("Loaded {Count} whitelisted SteamIDs", _whitelistedSteamIds.Count);
    }
}
