using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;

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

    // The engine's own rule (CCSGameRules::IsPistolRound): the first round, and the first round of the second half when
    // there is one, as long as the start money is the pistol-round 800 or less. Overtime rounds never qualify.
    public static bool IsPistolRound =>
        ConVarInt("mp_startmoney", 800) <= 800 &&
        (TotalRoundsPlayed == 0 || (ConVarBool("mp_halftime", true) && TotalRoundsPlayed == ConVarInt("mp_maxrounds", 24) / 2));

    // Read as text, whatever the cvar's own type: the engine prints a bool as "true"/"false" and a number as it is.
    public static string? ConVarValue(string name) => ConVar<int>.Find(name)?.ValueAsString;

    private static int ConVarInt(string name, int fallback) =>
        int.TryParse(ConVarValue(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private static bool ConVarBool(string name, bool fallback) =>
        ConVarValue(name)?.ToLowerInvariant() switch { "true" or "1" => true, "false" or "0" => false, _ => fallback };

    public static void Reset() => _proxy = null;
}
