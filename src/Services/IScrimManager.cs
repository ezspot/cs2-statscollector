using System.Threading.Tasks;
using statsCollector.Domain;

namespace statsCollector.Services;

public interface IScrimManager
{
    ScrimState CurrentState { get; }
    Task StartScrimAsync();
    Task StopScrimAsync();
    Task SetReadyAsync(ulong steamId, bool ready);
    Task SetCaptainAsync(int team, ulong steamId);
    Task VoteMapAsync(ulong steamId, string mapName);
    Task PickPlayerAsync(ulong steamId, ulong targetSteamId);
    Task RecoverAsync();
    Task SelectSideAsync(ulong steamId, string side);
    Task HandleKnifeRoundEnd(int winnerTeam);
    void SetOverride(string key, string value);
    void HandleDisconnect(ulong steamId);
}
