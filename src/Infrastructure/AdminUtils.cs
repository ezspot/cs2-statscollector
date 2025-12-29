using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;

namespace statsCollector.Infrastructure;

public static class AdminUtils
{
    public static bool HasPermission(CCSPlayerController player, params string[] permissions)
    {
        if (player.SteamID == 0) return true; // Console

        foreach (var permission in permissions)
        {
            if (AdminManager.PlayerHasPermissions(player, permission))
                return true;
        }

        return false;
    }
}
