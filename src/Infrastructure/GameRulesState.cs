using System.Globalization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;

namespace statsCollector.Infrastructure;

/// <summary>Game-thread reads of the current map's game rules.</summary>
public static class GameRulesState
{
    private static CCSGameRulesProxy? _proxy;

    // Looked up once per map: event handlers that run for every shot or hit read this. The listeners for map end and map
    // start clear it so the new map's proxy is looked up at once; a stale proxy's IsValid resolves its handle through the
    // entity system (NativeEntity.Handle), so it reads nothing freed, it is just false.
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

    // Set when a half ends with a side switch (halftime, each overtime half) until the next round resets; not set when
    // regulation ends in a tie and overtime begins.
    public static bool SwitchingTeamsAtRoundReset => Rules?.SwitchingTeamsAtRoundReset ?? false;

    // The engine's own rule (CCSGameRules::IsPistolRound): the first round, and the first round of the second half when
    // there is one, as long as the start money is the pistol-round 800 or less. Overtime rounds never qualify.
    public static bool IsPistolRound =>
        ConVarInt("mp_startmoney", 800) <= 800 &&
        (TotalRoundsPlayed == 0 || (ConVarBool("mp_halftime", true) && TotalRoundsPlayed == ConVarInt("mp_maxrounds", 24) / 2));

    // Read as text, whatever the cvar's own type: the engine prints a bool as "true"/"false" and a number as it is.
    public static string? ConVarValue(string name) => ConVar<int>.Find(name)?.ValueAsString;

    private static int ConVarInt(string name, int fallback) =>
        int.TryParse(ConVarValue(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    internal static bool ConVarBool(string name, bool fallback) =>
        ConVarValue(name)?.ToLowerInvariant() switch { "true" or "1" => true, "false" or "0" => false, _ => fallback };

    public static void Reset() => _proxy = null;
}
