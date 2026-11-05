using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace statsCollector.Domain;

/// <summary>
/// A snapshot of CCSPlayerController and its Pawn data. Capture this on the game thread; every field is a
/// copy, so it stays valid when read later or passed to background services.
/// </summary>
public sealed record PlayerControllerState
{
    public required ulong SteamId { get; init; }
    public required string PlayerName { get; init; }
    public required int Health { get; init; }
    public required PlayerTeam Team { get; init; }
    public required Vector3 Position { get; init; }
    public required Vector3 GrenadeThrowPosition { get; init; }
    public required int Money { get; init; }
    public required int EquipmentValue { get; init; }
    public required bool IsValid { get; init; }
    public required bool IsBot { get; init; }
    public required bool HasPawn { get; init; }

    public static PlayerControllerState From(CCSPlayerController? player)
    {
        // A bot (FL_FAKECLIENT) has no SteamID and is a player all the same; a human without one is not here yet.
        if (player == null || !player.IsValid || (player.SteamID == 0 && !player.IsBot))
        {
            return new PlayerControllerState
            {
                SteamId = 0,
                PlayerName = "Unknown",
                Health = 0,
                Team = PlayerTeam.Spectator,
                Position = Vector3.Zero,
                GrenadeThrowPosition = Vector3.Zero,
                Money = 0,
                EquipmentValue = 0,
                IsValid = false,
                IsBot = false,
                HasPawn = false
            };
        }

        var pawn = player.PlayerPawn.Value;
        var position = Copy(pawn?.AbsOrigin);
        return new PlayerControllerState
        {
            SteamId = player.SteamID,
            PlayerName = player.PlayerName,
            // The controller's own m_iHealth is not the player's HP; the pawn's is.
            Health = pawn?.Health ?? 0,
            Team = (PlayerTeam)player.TeamNum,
            Position = position,
            GrenadeThrowPosition = pawn?.StashedGrenadeThrowPosition is { } thrown ? Copy(thrown) : position,
            Money = player.InGameMoneyServices?.Account ?? 0,
            EquipmentValue = pawn?.CurrentEquipmentValue ?? 0,
            IsValid = true,
            IsBot = player.IsBot,
            HasPawn = pawn is { IsValid: true }
        };
    }

    /// <summary>The connected humans by SteamID: one pass over the player slots for a loop that looks several up.</summary>
    public static Dictionary<ulong, CCSPlayerController> HumansBySteamId() => HumansBySteamId(Utilities.GetPlayers());

    public static Dictionary<ulong, CCSPlayerController> HumansBySteamId(IEnumerable<CCSPlayerController> everyone)
    {
        var players = new Dictionary<ulong, CCSPlayerController>();
        foreach (var player in everyone)
        {
            if (!player.IsBot && player.SteamID != 0) players.TryAdd(player.SteamID, player);
        }
        return players;
    }

    // Schema vectors are views over entity memory, so read the values now rather than keep the view.
    private static Vector3 Copy(Vector? vector) => vector is null ? Vector3.Zero : new Vector3(vector.X, vector.Y, vector.Z);
}
