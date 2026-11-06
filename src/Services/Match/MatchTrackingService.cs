using System;
using Microsoft.Extensions.Logging;
using statsCollector.Domain;

namespace statsCollector.Services;

public record MatchContext(string MatchUuid, string MapName);

public interface IMatchTrackingService
{
    void StartMatch(string mapName);
    void EndMatch();
    void StartRound(int roundNumber);
    void EndRound(int roundNumber, int winnerTeam, int winType, RoundBombInfo bomb);
    void RecordRoundEconomy(int roundNumber, RoundEconomy economy);
    float GetRoundWinProbability(int ctAlive, int tAlive);
    MatchContext? CurrentMatch { get; }
}

public sealed class MatchTrackingService : IMatchTrackingService
{
    private readonly ILogger<MatchTrackingService> _logger;
    private readonly IPersistenceChannel _persistenceChannel;
    private readonly object _lock = new();
    private MatchContext? _currentMatch;

    public MatchContext? CurrentMatch
    {
        get { lock (_lock) return _currentMatch; }
    }

    public MatchTrackingService(
        ILogger<MatchTrackingService> logger,
        IPersistenceChannel persistenceChannel)
    {
        _logger = logger;
        _persistenceChannel = persistenceChannel;
    }

    public void StartMatch(string mapName)
    {
        var uuid = Guid.NewGuid().ToString();
        _logger.LogInformation("Starting new match tracking: {Uuid} on {Map}", uuid, mapName);

        string? previousUuid;
        lock (_lock)
        {
            previousUuid = _currentMatch?.MatchUuid;
            _currentMatch = new MatchContext(uuid, mapName);
        }

        if (previousUuid is not null)
        {
            _persistenceChannel.TryWrite(new MatchEndUpdate(previousUuid));
        }
        _persistenceChannel.TryWrite(new MatchStartUpdate(mapName, uuid));
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
            _persistenceChannel.TryWrite(new MatchEndUpdate(uuid));
        }
    }

    public void StartRound(int roundNumber)
    {
        string? uuid;
        lock (_lock)
        {
            uuid = _currentMatch?.MatchUuid;
        }

        if (uuid != null)
        {
            _persistenceChannel.TryWrite(new RoundStartUpdate(uuid, roundNumber));
        }
    }

    public void EndRound(int roundNumber, int winnerTeam, int winType, RoundBombInfo bomb)
    {
        if (CurrentMatch?.MatchUuid is { } uuid)
            _persistenceChannel.TryWrite(new RoundEndUpdate(uuid, roundNumber, winnerTeam, winType, bomb));
    }

    public void RecordRoundEconomy(int roundNumber, RoundEconomy economy)
    {
        if (CurrentMatch?.MatchUuid is { } uuid)
            _persistenceChannel.TryWrite(new RoundEconomyUpdate(uuid, roundNumber, economy));
    }

    // CT round-win rate by players alive (row = Ts alive, column = CTs alive, 1..5), measured over 400k+ CS:GO
    // matchmaking rounds: https://cryptm.org/posts/2020/06/12/csgo.html. It is monotone, so every enemy kill
    // raises the killer's side; the old hand-made table mixed in a ct/(ct+t) fallback that made 8 of the 40
    // 5v5 kill transitions (e.g. CT 5v3 -> 5v2) score negative.
    private static readonly float[,] CtWinRate =
    {
        // 1 CT    2 CT    3 CT    4 CT    5 CT
        { 0.430f, 0.792f, 0.942f, 0.985f, 0.997f }, // 1 T
        { 0.123f, 0.440f, 0.733f, 0.893f, 0.961f }, // 2 T
        { 0.029f, 0.187f, 0.455f, 0.700f, 0.858f }, // 3 T
        { 0.007f, 0.068f, 0.236f, 0.470f, 0.683f }, // 4 T
        { 0.002f, 0.023f, 0.106f, 0.275f, 0.487f }, // 5 T
    };

    public float GetRoundWinProbability(int ctAlive, int tAlive)
    {
        if (ctAlive <= 0) return 0f;
        if (tAlive <= 0) return 1f;
        // ponytail: no data beyond 5 per side, so larger teams read the 5-player cells (kills there score 0).
        return CtWinRate[Math.Min(tAlive, 5) - 1, Math.Min(ctAlive, 5) - 1];
    }
}
