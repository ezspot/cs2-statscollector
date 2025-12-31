using System.Numerics;
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
    public required int Armor { get; init; }
    public required bool HasHelmet { get; init; }
    public required PlayerTeam Team { get; init; }
    public required Vector Position { get; init; }
    public required QAngle EyeAngles { get; init; }
    public required Vector Velocity { get; init; }
    public required int Money { get; init; }
    public required int Score { get; init; }
    public required int Kills { get; init; }
    public required int Deaths { get; init; }
    public required int Assists { get; init; }
    public required int Mvps { get; init; }
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
                Armor = 0,
                HasHelmet = false,
                Team = PlayerTeam.Spectator,
                Position = new Vector(0, 0, 0),
                EyeAngles = new QAngle(0, 0, 0),
                Velocity = new Vector(0, 0, 0),
                Money = 0,
                Score = 0,
                Kills = 0,
                Deaths = 0,
                Assists = 0,
                Mvps = 0,
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
            Armor = player.PlayerPawn.Value?.ArmorValue ?? 0,
            HasHelmet = player.PlayerPawn.Value?.HasHelmet ?? false,
            Team = (PlayerTeam)player.TeamNum,
            Position = pawn?.AbsOrigin ?? new Vector(0, 0, 0),
            EyeAngles = pawn?.EyeAngles ?? new QAngle(0, 0, 0),
            Velocity = pawn?.AbsVelocity ?? new Vector(0, 0, 0),
            Money = player.InGameMoneyServices?.Account ?? 0,
            Score = player.Score,
            Kills = player.ActionTrackingServices?.MatchStats.Kills ?? 0,
            Deaths = player.ActionTrackingServices?.MatchStats.Deaths ?? 0,
            Assists = player.ActionTrackingServices?.MatchStats.Assists ?? 0,
            Mvps = player.ActionTrackingServices?.MatchStats.MVPs ?? 0,
            IsValid = true,
            IsBot = player.IsBot,
            PawnHandle = pawn?.Handle.Value ?? 0
        };
    }
}
