using System.Collections.Generic;
using System.Numerics;
using Microsoft.Extensions.Options;
using statsCollector.Config;
using CounterStrikeSharp.API.Modules.Utils;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace statsCollector.Services;

public interface IMapDataService
{
    bool IsInChokePoint(string mapName, Vector position, out string chokePointName);
}

public sealed class MapDataService : IMapDataService
{
    private readonly IOptionsMonitor<MapDataConfig> _config;

    public MapDataService(IOptionsMonitor<MapDataConfig> config)
    {
        _config = config;
    }

    public bool IsInChokePoint(string mapName, Vector position, out string chokePointName)
    {
        chokePointName = string.Empty;
        if (!_config.CurrentValue.Maps.TryGetValue(mapName, out var mapInfo)) return false;

        foreach (var point in mapInfo.ChokePoints)
        {
            float dist = Vector3.Distance(
                new Vector3(position.X, position.Y, position.Z),
                new Vector3(point.Position.X, point.Position.Y, point.Position.Z)
            );

            if (dist <= point.Radius)
            {
                chokePointName = point.Name;
                return true;
            }
        }

        return false;
    }
}
