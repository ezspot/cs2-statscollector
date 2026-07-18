using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace statsCollector.Domain;

/// <summary>
/// A thread-safe snapshot of CCSPlayerController and its Pawn data.
/// Capture this on the game thread before passing to background services.
/// </summary>
public sealed record PlayerControllerState
{
    public required ulong SteamId { get; init; }
    public required string PlayerName { get; init; }
    public required int Health { get; init; }
    public required PlayerTeam Team { get; init; }
    public required Vector Position { get; init; }
    public required Vector GrenadeThrowPosition { get; init; }
    public required int Money { get; init; }
    public required int EquipmentValue { get; init; }
    public required bool IsValid { get; init; }
    public required bool IsBot { get; init; }
    public required uint PawnHandle { get; init; }

    public static PlayerControllerState From(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid || player.SteamID == 0)
        {
            return new PlayerControllerState
            {
                SteamId = 0,
                PlayerName = "Unknown",
                Health = 0,
                Team = PlayerTeam.Spectator,
                Position = new Vector(0, 0, 0),
                GrenadeThrowPosition = new Vector(0, 0, 0),
                Money = 0,
                EquipmentValue = 0,
                IsValid = false,
                IsBot = false,
                PawnHandle = 0
            };
        }

        var pawn = player.PlayerPawn.Value;
        return new PlayerControllerState
        {
            SteamId = player.SteamID,
            PlayerName = player.PlayerName,
            Health = player.Health,
            Team = (PlayerTeam)player.TeamNum,
            Position = pawn?.AbsOrigin ?? new Vector(0, 0, 0),
            GrenadeThrowPosition = pawn?.StashedGrenadeThrowPosition ?? pawn?.AbsOrigin ?? new Vector(0, 0, 0),
            Money = player.InGameMoneyServices?.Account ?? 0,
            EquipmentValue = pawn?.CurrentEquipmentValue ?? 0,
            IsValid = true,
            IsBot = player.IsBot,
            PawnHandle = (uint)(pawn?.Handle ?? 0)
        };
    }
}
