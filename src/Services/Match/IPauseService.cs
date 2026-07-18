using System;
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
    void RequestPause(CCSPlayerController? player, PauseType type);
    void RequestUnpause(CCSPlayerController? player);
    void OnRoundEnd();
    void Reset();
}
