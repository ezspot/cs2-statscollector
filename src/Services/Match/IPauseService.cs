using System;
using System.Threading.Tasks;
using CounterStrikeSharp.API.Core;

namespace statsCollector.Services;

public enum PauseType
{
    None,
    Tactical,
    Technical,
    Admin
}

public interface IPauseService
{
    Task RequestPauseAsync(CCSPlayerController? player, PauseType type);
    Task RequestUnpauseAsync(CCSPlayerController? player);
    void OnRoundEnd();
}
