using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using statsCollector.Infrastructure.Database;
using System.Threading;
using System.Threading.Tasks;

namespace statsCollector.Services;

public interface IPositionTrackingService
{
    Task TrackKillPositionAsync(
        ulong killerSteamId, 
        ulong victimSteamId, 
        Vector killerPos, 
        Vector victimPos, 
        string weapon, 
        bool isHeadshot, 
        bool isWallbang,
        int killerTeam,
        int victimTeam,
        string mapName,
        int roundNumber,
        int roundTime,
        CancellationToken cancellationToken = default);

    Task TrackDeathPositionAsync(
        ulong steamId,
        Vector position,
        string causeOfDeath,
        bool isHeadshot,
        int team,
        string mapName,
        int roundNumber,
        int roundTime,
        CancellationToken cancellationToken = default);

    Task TrackUtilityPositionAsync(
        ulong steamId,
        Vector throwPos,
        Vector landPos,
        int utilityType,
        int opponentsAffected,
        int teammatesAffected,
        int damage,
        string mapName,
        int roundNumber,
        int roundTime,
        CancellationToken cancellationToken = default);
}

public sealed class PositionTrackingService(
    IConnectionFactory connectionFactory,
    ILogger<PositionTrackingService> logger) : IPositionTrackingService
{
    public async Task TrackKillPositionAsync(
        ulong killerSteamId,
        ulong victimSteamId,
        Vector killerPos,
        Vector victimPos,
        string weapon,
        bool isHeadshot,
        bool isWallbang,
        int killerTeam,
        int victimTeam,
        string mapName,
        int roundNumber,
        int roundTime,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var distance = CalculateDistance(killerPos, victimPos);

            const string sql = @"
                INSERT INTO kill_positions 
                (killer_steam_id, victim_steam_id, killer_x, killer_y, killer_z, 
                 victim_x, victim_y, victim_z, weapon_used, is_headshot, is_wallbang,
                 distance, killer_team, victim_team, map_name, round_number, round_time_seconds)
                VALUES 
                (@KillerSteamId, @VictimSteamId, @KillerX, @KillerY, @KillerZ,
                 @VictimX, @VictimY, @VictimZ, @Weapon, @IsHeadshot, @IsWallbang,
                 @Distance, @KillerTeam, @VictimTeam, @MapName, @RoundNumber, @RoundTime)";

            await using var connection = await connectionFactory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            
            command.CommandText = sql;
            command.Parameters.AddWithValue("@KillerSteamId", killerSteamId);
            command.Parameters.AddWithValue("@VictimSteamId", victimSteamId);
            command.Parameters.AddWithValue("@KillerX", killerPos.X);
            command.Parameters.AddWithValue("@KillerY", killerPos.Y);
            command.Parameters.AddWithValue("@KillerZ", killerPos.Z);
            command.Parameters.AddWithValue("@VictimX", victimPos.X);
            command.Parameters.AddWithValue("@VictimY", victimPos.Y);
            command.Parameters.AddWithValue("@VictimZ", victimPos.Z);
            command.Parameters.AddWithValue("@Weapon", weapon);
            command.Parameters.AddWithValue("@IsHeadshot", isHeadshot);
            command.Parameters.AddWithValue("@IsWallbang", isWallbang);
            command.Parameters.AddWithValue("@Distance", distance);
            command.Parameters.AddWithValue("@KillerTeam", killerTeam);
            command.Parameters.AddWithValue("@VictimTeam", victimTeam);
            command.Parameters.AddWithValue("@MapName", mapName);
            command.Parameters.AddWithValue("@RoundNumber", roundNumber);
            command.Parameters.AddWithValue("@RoundTime", roundTime);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            
            logger.LogTrace("Tracked kill position for {Killer} -> {Victim} at distance {Distance}",
                killerSteamId, victimSteamId, distance);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to track kill position");
        }
    }

    public async Task TrackDeathPositionAsync(
        ulong steamId,
        Vector position,
        string causeOfDeath,
        bool isHeadshot,
        int team,
        string mapName,
        int roundNumber,
        int roundTime,
        CancellationToken cancellationToken = default)
    {
        try
        {
            const string sql = @"
                INSERT INTO death_positions 
                (steam_id, x, y, z, cause_of_death, is_headshot, team, map_name, round_number, round_time_seconds)
                VALUES 
                (@SteamId, @X, @Y, @Z, @CauseOfDeath, @IsHeadshot, @Team, @MapName, @RoundNumber, @RoundTime)";

            await using var connection = await connectionFactory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            
            command.CommandText = sql;
            command.Parameters.AddWithValue("@SteamId", steamId);
            command.Parameters.AddWithValue("@X", position.X);
            command.Parameters.AddWithValue("@Y", position.Y);
            command.Parameters.AddWithValue("@Z", position.Z);
            command.Parameters.AddWithValue("@CauseOfDeath", causeOfDeath);
            command.Parameters.AddWithValue("@IsHeadshot", isHeadshot);
            command.Parameters.AddWithValue("@Team", team);
            command.Parameters.AddWithValue("@MapName", mapName);
            command.Parameters.AddWithValue("@RoundNumber", roundNumber);
            command.Parameters.AddWithValue("@RoundTime", roundTime);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            
            logger.LogTrace("Tracked death position for {SteamId}", steamId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to track death position");
        }
    }

    public async Task TrackUtilityPositionAsync(
        ulong steamId,
        Vector throwPos,
        Vector landPos,
        int utilityType,
        int opponentsAffected,
        int teammatesAffected,
        int damage,
        string mapName,
        int roundNumber,
        int roundTime,
        CancellationToken cancellationToken = default)
    {
        try
        {
            const string sql = @"
                INSERT INTO utility_positions 
                (steam_id, throw_x, throw_y, throw_z, land_x, land_y, land_z,
                 utility_type, opponents_affected, teammates_affected, damage,
                 map_name, round_number, round_time_seconds)
                VALUES 
                (@SteamId, @ThrowX, @ThrowY, @ThrowZ, @LandX, @LandY, @LandZ,
                 @UtilityType, @OpponentsAffected, @TeammatesAffected, @Damage,
                 @MapName, @RoundNumber, @RoundTime)";

            await using var connection = await connectionFactory.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            
            command.CommandText = sql;
            command.Parameters.AddWithValue("@SteamId", steamId);
            command.Parameters.AddWithValue("@ThrowX", throwPos.X);
            command.Parameters.AddWithValue("@ThrowY", throwPos.Y);
            command.Parameters.AddWithValue("@ThrowZ", throwPos.Z);
            command.Parameters.AddWithValue("@LandX", landPos.X);
            command.Parameters.AddWithValue("@LandY", landPos.Y);
            command.Parameters.AddWithValue("@LandZ", landPos.Z);
            command.Parameters.AddWithValue("@UtilityType", utilityType);
            command.Parameters.AddWithValue("@OpponentsAffected", opponentsAffected);
            command.Parameters.AddWithValue("@TeammatesAffected", teammatesAffected);
            command.Parameters.AddWithValue("@Damage", damage);
            command.Parameters.AddWithValue("@MapName", mapName);
            command.Parameters.AddWithValue("@RoundNumber", roundNumber);
            command.Parameters.AddWithValue("@RoundTime", roundTime);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            
            logger.LogTrace("Tracked utility position for {SteamId}", steamId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to track utility position");
        }
    }

    /// <summary>
    /// Calculate 3D distance between two positions
    /// </summary>
    private static float CalculateDistance(Vector pos1, Vector pos2)
    {
        var dx = pos2.X - pos1.X;
        var dy = pos2.Y - pos1.Y;
        var dz = pos2.Z - pos1.Z;
        return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}
