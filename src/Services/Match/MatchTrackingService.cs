using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using statsCollector.Infrastructure.Database;
using Dapper;

namespace statsCollector.Services;

public record MatchContext(string MatchUuid, string MapName, string? SeriesUuid = null, int? MatchId = null);

public interface IMatchTrackingService
{
    void StartMatch(string mapName, string? seriesUuid = null);
    void EndMatch();
    void StartRound(int roundNumber);
    void EndRound(int roundNumber, int winnerTeam, int winType);
    Task<string?> GetMatchStatusAsync(int matchId, CancellationToken ct = default);
    float GetRoundWinProbability(int ctAlive, int tAlive);
    MatchContext? CurrentMatch { get; }
    int? CurrentRoundNumber { get; }
}

public sealed class MatchTrackingService : IMatchTrackingService
{
    private readonly ILogger<MatchTrackingService> _logger;
    private readonly IPersistenceChannel _persistenceChannel;
    private readonly IConnectionFactory _connectionFactory;
    private readonly object _lock = new();
    private MatchContext? _currentMatch;
    private int? _currentRoundNumber;

    public MatchContext? CurrentMatch 
    { 
        get { lock (_lock) return _currentMatch; } 
    }
    
    public int? CurrentRoundNumber 
    { 
        get { lock (_lock) return _currentRoundNumber; } 
    }

    public MatchTrackingService(
        IConnectionFactory connectionFactory, 
        ILogger<MatchTrackingService> logger,
        IPersistenceChannel persistenceChannel)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
        _persistenceChannel = persistenceChannel;
    }

    public void StartMatch(string mapName, string? seriesUuid = null)
    {
        var uuid = Guid.NewGuid().ToString();
        _logger.LogInformation("Starting new match tracking: {Uuid} (Series: {Series}) on {Map}", uuid, seriesUuid ?? "None", mapName);

        lock (_lock)
        {
            _currentMatch = new MatchContext(uuid, mapName, seriesUuid);
            _currentRoundNumber = null;
        }

        _persistenceChannel.TryWrite(new StatsUpdate(UpdateType.MatchStart, (mapName, uuid, seriesUuid)));
    }

    public void EndMatch()
    {
        string? uuid;
        lock (_lock)
        {
            uuid = _currentMatch?.MatchUuid;
            _currentMatch = null;
        }

        if (uuid != null)
        {
            _logger.LogInformation("Ending match tracking: {Uuid}", uuid);
            _persistenceChannel.TryWrite(new StatsUpdate(UpdateType.MatchEnd, uuid));
        }
    }

    public void StartRound(int roundNumber)
    {
        string? uuid;
        lock (_lock)
        {
            uuid = _currentMatch?.MatchUuid;
            _currentRoundNumber = roundNumber;
        }

        if (uuid != null)
        {
            _persistenceChannel.TryWrite(new StatsUpdate(UpdateType.RoundStart, (uuid, roundNumber)));
        }
    }

    public void EndRound(int roundNumber, int winnerTeam, int winType)
    {
        string? uuid;
        lock (_lock)
        {
            uuid = _currentMatch?.MatchUuid;
        }

        if (uuid != null)
        {
            _persistenceChannel.TryWrite(new StatsUpdate(UpdateType.RoundEnd, (uuid, roundNumber, winnerTeam, winType)));
        }
    }

    public async Task<string?> GetMatchStatusAsync(int matchId, CancellationToken ct = default)
    {
        await using var connection = await _connectionFactory.CreateConnectionAsync(ct).ConfigureAwait(false);
        const string sql = "SELECT status FROM matches WHERE id = @MatchId";
        return await connection.ExecuteScalarAsync<string>(new CommandDefinition(sql, new { MatchId = matchId }, cancellationToken: ct)).ConfigureAwait(false);
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
