using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using statsCollector.Config;
using statsCollector.Infrastructure;

namespace statsCollector.Services;

public class ConfigLoaderService : IConfigLoaderService
{
    private readonly ILogger<ConfigLoaderService> _logger;
    private readonly PluginConfig _config;
    private readonly IGameScheduler _scheduler;
    private readonly HashSet<ulong> _whitelistedSteamIds = new();
    private readonly string _configDirectory;

    public ConfigLoaderService(
        ILogger<ConfigLoaderService> logger,
        IOptions<PluginConfig> config,
        IGameScheduler scheduler)
    {
        _logger = logger;
        _config = config.Value;
        _scheduler = scheduler;
        _whitelistedSteamIds.UnionWith(_config.Scrim.WhitelistedSteamIds);
        _configDirectory = Path.GetFullPath(Path.Combine(Server.GameDirectory, "csgo", _config.Scrim.ScrimConfigPath));
    }

    public async Task LoadAndExecuteConfigAsync(string fileName)
    {
        var configPath = Path.GetFullPath(Path.Combine(_configDirectory, fileName));
        if (!configPath.StartsWith(_configDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Scrim config path resolves outside the configured directory.");
        }

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
                if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.StartsWith("//") || trimmedLine.StartsWith('#'))
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
}
