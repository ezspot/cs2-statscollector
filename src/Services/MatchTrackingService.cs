using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using statsCollector.Infrastructure.Database;
using Dapper;

namespace statsCollector.Services;

public record MatchContext(int MatchId, string MatchUuid, string MapName);

public interface IMatchTrackingService
{
    Task<MatchContext> StartMatchAsync(string mapName, CancellationToken ct = default);
    Task EndMatchAsync(int matchId, CancellationToken ct = default);
    Task<int> StartRoundAsync(int matchId, int roundNumber, CancellationToken ct = default);
    Task EndRoundAsync(int roundId, int winnerTeam, int winType, CancellationToken ct = default);
    float GetRoundWinProbability(int ctAlive, int tAlive);
    MatchContext? CurrentMatch { get; }
    int? CurrentRoundId { get; }
}

public sealed class MatchTrackingService : IMatchTrackingService
{
    private readonly IConnectionFactory _connectionFactory;
    private readonly ILogger<MatchTrackingService> _logger;
    private MatchContext? _currentMatch;
    private int? _currentRoundId;

    public MatchContext? CurrentMatch => _currentMatch;
    public int? CurrentRoundId => _currentRoundId;

    public MatchTrackingService(IConnectionFactory connectionFactory, ILogger<MatchTrackingService> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public async Task<MatchContext> StartMatchAsync(string mapName, CancellationToken ct = default)
    {
        var uuid = Guid.NewGuid().ToString();
        _logger.LogInformation("Starting new match tracking: {Uuid} on {Map}", uuid, mapName);

        await using var connection = await _connectionFactory.CreateConnectionAsync(ct);
        const string sql = "INSERT INTO matches (match_uuid, map_name, status) VALUES (@Uuid, @MapName, 'IN_PROGRESS'); SELECT LAST_INSERT_ID();";
        
        var id = await connection.ExecuteScalarAsync<int>(sql, new { Uuid = uuid, MapName = mapName });
        _currentMatch = new MatchContext(id, uuid, mapName);
        _currentRoundId = null;
        
        return _currentMatch;
    }

    public async Task EndMatchAsync(int matchId, CancellationToken ct = default)
    {
        _logger.LogInformation("Ending match tracking: {MatchId}", matchId);
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct);
        const string sql = "UPDATE matches SET end_time = CURRENT_TIMESTAMP, status = 'COMPLETED' WHERE id = @MatchId";
        await connection.ExecuteAsync(sql, new { MatchId = matchId });
        _currentMatch = null;
    }

    public async Task<int> StartRoundAsync(int matchId, int roundNumber, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct);
        const string sql = "INSERT INTO rounds (match_id, round_number) VALUES (@MatchId, @RoundNumber) ON DUPLICATE KEY UPDATE start_time = CURRENT_TIMESTAMP; SELECT id FROM rounds WHERE match_id = @MatchId AND round_number = @RoundNumber;";
        
        _currentRoundId = await connection.ExecuteScalarAsync<int>(sql, new { MatchId = matchId, RoundNumber = roundNumber });
        return _currentRoundId.Value;
    }

    public async Task EndRoundAsync(int roundId, int winnerTeam, int winType, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct);
        const string sql = "UPDATE rounds SET end_time = CURRENT_TIMESTAMP, winner_team = @Winner, win_type = @WinType WHERE id = @RoundId";
        await connection.ExecuteAsync(sql, new { RoundId = roundId, Winner = winnerTeam, WinType = winType });
    }

    public float GetRoundWinProbability(int ctAlive, int tAlive)
    {
        // Simple historical model for CT win probability based on alive counts
        // 5v5 -> 0.5, 5v4 -> 0.7, etc.
        if (ctAlive == 0) return 0.0f;
        if (tAlive == 0) return 1.0f;

        // Using a basic sigmoid-like approach or lookup table for CS2 standard meta
        return (ctAlive, tAlive) switch
        {
            (5, 5) => 0.50f,
            (5, 4) => 0.72f,
            (4, 5) => 0.28f,
            (4, 4) => 0.50f,
            (5, 3) => 0.85f,
            (3, 5) => 0.15f,
            (4, 3) => 0.70f,
            (3, 4) => 0.30f,
            (3, 3) => 0.50f,
            (2, 3) => 0.25f,
            (3, 2) => 0.75f,
            (2, 2) => 0.50f,
            (1, 2) => 0.15f,
            (2, 1) => 0.85f,
            (1, 1) => 0.50f,
            _ => (float)ctAlive / (ctAlive + tAlive)
        };
    }
}
