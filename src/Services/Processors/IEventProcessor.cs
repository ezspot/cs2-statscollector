using statsCollector.Infrastructure;

namespace statsCollector.Services;

public record RoundContext(int RoundNumber, DateTime RoundStartUtc, int CtAliveAtStart, int TAliveAtStart, bool IsPistolRound = false);

public interface IEventProcessor
{
    void RegisterEvents(IEventDispatcher dispatcher);
    void OnRoundStart(RoundContext context) { }
    void OnRoundEnd(int winnerTeam, int winReason) { }
}
