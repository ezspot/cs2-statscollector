using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace statsCollector.Infrastructure;

/// <summary>Game-thread reads of the current map's game rules.</summary>
public static class GameRulesState
{
    private static CCSGameRulesProxy? _proxy;

    // Looked up once per map: event handlers that run for every shot or hit read this. A map change frees the proxy,
    // so the listeners for map end and map start clear it (IsValid would read freed memory).
    private static CCSGameRules? Rules
    {
        get
        {
            if (_proxy is not { IsValid: true })
                _proxy = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
            return _proxy?.GameRules;
        }
    }

    public static bool IsWarmup => Rules?.WarmupPeriod ?? false;

    public static int TotalRoundsPlayed => Rules?.TotalRoundsPlayed ?? 0;

    public static void Reset() => _proxy = null;
}
