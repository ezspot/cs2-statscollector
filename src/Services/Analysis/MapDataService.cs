using System.Collections.Generic;
using System.Numerics;
using CounterStrikeSharp.API.Modules.Utils;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace statsCollector.Services;

public record ChokePoint(string Name, Vector Position, float Radius);

public interface IMapDataService
{
    bool IsInChokePoint(string mapName, Vector position, out string chokePointName);
}

public sealed class MapDataService : IMapDataService
{
    private readonly Dictionary<string, List<ChokePoint>> _chokePoints = new()
    {
        ["de_mirage"] = new()
        {
            new ChokePoint("Window", new Vector(-1060, -420, -165), 150f),
            new ChokePoint("Connector", new Vector(-750, -700, -160), 150f),
            new ChokePoint("Palace", new Vector(1000, -1500, -160), 200f),
            new ChokePoint("Apartments", new Vector(-1500, -800, -50), 250f)
        },
        ["de_inferno"] = new()
        {
            new ChokePoint("Banana", new Vector(1500, 500, 100), 300f),
            new ChokePoint("Apartments", new Vector(-500, 2000, 150), 250f),
            new ChokePoint("Pit", new Vector(1800, 2200, 50), 200f)
        }
        // Fallback for other maps can be added or loaded from JSON
    };

    public bool IsInChokePoint(string mapName, Vector position, out string chokePointName)
    {
        chokePointName = string.Empty;
        if (!_chokePoints.TryGetValue(mapName, out var points)) return false;

        foreach (var point in points)
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
