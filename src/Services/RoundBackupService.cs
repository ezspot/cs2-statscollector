using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using statsCollector.Domain;

namespace statsCollector.Services;

public record RoundSnapshot(
    int RoundNumber,
    int Team1Score,
    int Team2Score,
    Dictionary<ulong, PlayerRoundData> PlayerData
);

public record PlayerRoundData(
    ulong SteamId,
    int Money,
    int Kills,
    int Deaths,
    int Assists,
    CsTeam Team
);

public interface IRoundBackupService
{
    void CreateSnapshot(int roundNumber);
    bool RestoreRound(int roundNumber);
    IReadOnlyList<int> GetAvailableRounds();
}

public sealed class RoundBackupService(
    ILogger<RoundBackupService> logger,
    IPlayerSessionService playerSessions) : IRoundBackupService
{
    private readonly ILogger<RoundBackupService> _logger = logger;
    private readonly IPlayerSessionService _playerSessions = playerSessions;
    private readonly List<RoundSnapshot> _backups = [];

    public void CreateSnapshot(int roundNumber)
    {
        var playerData = new Dictionary<ulong, PlayerRoundData>();
        
        foreach (var steamId in _playerSessions.GetActiveSteamIds())
        {
            var player = Utilities.GetPlayerFromSteamId(steamId);
            if (player is not { IsValid: true, IsBot: false }) continue;

            if (_playerSessions.TryGetSnapshot(steamId, out var snapshot))
            {
                playerData[steamId] = new PlayerRoundData(
                    steamId,
                    player.InGameMoneyServices?.Account ?? 0,
                    snapshot.Kills,
                    snapshot.Deaths,
                    snapshot.Assists,
                    player.Team
                );
            }
        }

        var teamManagers = Utilities.FindAllEntitiesByDesignerName<CCSTeam>("cs_team_manager");
        int tScore = 0;
        int ctScore = 0;

        foreach (var team in teamManagers)
        {
            if (team.TeamNum == (int)CsTeam.Terrorist) tScore = team.Score;
            else if (team.TeamNum == (int)CsTeam.CounterTerrorist) ctScore = team.Score;
        }

        var backup = new RoundSnapshot(roundNumber, tScore, ctScore, playerData);
        _backups.RemoveAll(b => b.RoundNumber == roundNumber);
        _backups.Add(backup);
        
        _logger.LogInformation("Created backup for round {RoundNumber}. T:{TScore} CT:{CTScore}", roundNumber, tScore, ctScore);
    }

    public bool RestoreRound(int roundNumber)
    {
        var backup = _backups.FirstOrDefault(b => b.RoundNumber == roundNumber);
        if (backup == null)
        {
            _logger.LogWarning("Attempted to restore round {RoundNumber} but no backup exists.", roundNumber);
            return false;
        }

        _logger.LogInformation("Restoring round {RoundNumber}...", roundNumber);

        // Restore team scores
        var teamManagers = Utilities.FindAllEntitiesByDesignerName<CCSTeam>("cs_team_manager");
        foreach (var team in teamManagers)
        {
            if (team.TeamNum == (int)CsTeam.Terrorist) team.Score = backup.Team1Score;
            else if (team.TeamNum == (int)CsTeam.CounterTerrorist) team.Score = backup.Team2Score;
            
            Utilities.SetStateChanged(team, "CCSTeam", "m_iScore");
        }

        // Restore player stats and money
        foreach (var kvp in backup.PlayerData)
        {
            var steamId = kvp.Key;
            var data = kvp.Value;
            var player = Utilities.GetPlayerFromSteamId(steamId);

            if (player is { IsValid: true })
            {
                if (player.InGameMoneyServices != null)
                {
                    player.InGameMoneyServices.Account = data.Money;
                }

                _playerSessions.MutatePlayer(steamId, stats =>
                {
                    stats.Kills = data.Kills;
                    stats.Deaths = data.Deaths;
                    stats.Assists = data.Assists;
                });
            }
        }

        // Use native command to set round
        Server.ExecuteCommand($"mp_round_restart_delay 0; mp_restartgame 1; mp_round_restart_delay 5");
        
        // Note: CounterStrikeSharp doesn't expose a direct way to set the internal round counter 
        // that matches mp_restartgame perfectly for a specific round number without some hackery 
        // or using GameRules directly which can be unstable. 
        // Most production plugins (MatchZy) use mp_restartgame and then handle the score/stats correction.
        
        return true;
    }

    public IReadOnlyList<int> GetAvailableRounds() => _backups.Select(b => b.RoundNumber).ToList();
}
