using System.Collections.Generic;
using CounterStrikeSharp.API.Modules.Utils;

namespace statsCollector.Config;

public record ChokePoint(string Name, Vector Position, float Radius);

public class MapDataConfig
{
    public Dictionary<string, MapInfo> Maps { get; set; } = new();
}

public class MapInfo
{
    public List<ChokePoint> ChokePoints { get; set; } = new();
    public Dictionary<string, Vector> Sites { get; set; } = new();
}
