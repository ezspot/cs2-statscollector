using System;
using System.Collections.Generic;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using statsCollector.Domain;
using statsCollector.Infrastructure;

namespace statsCollector.Services;

public record DamageInfo(int Damage, int Hits);

public interface IDamageReportService
{
    void RecordDamage(ulong attackerId, ulong victimId, int damage);
    void ReportToPlayers();
    void ResetRound();
}

public sealed class DamageReportService : IDamageReportService
{
    private readonly Dictionary<ulong, Dictionary<ulong, DamageInfo>> _damageDealt = [];

    public void RecordDamage(ulong attackerId, ulong victimId, int damage)
    {
        if (attackerId == 0 || victimId == 0 || attackerId == victimId) return;

        if (!_damageDealt.TryGetValue(attackerId, out var victims))
        {
            victims = [];
            _damageDealt[attackerId] = victims;
        }

        victims[victimId] = victims.TryGetValue(victimId, out var existing)
            ? new DamageInfo(existing.Damage + damage, existing.Hits + 1)
            : new DamageInfo(damage, 1);
    }

    public void ReportToPlayers()
    {
        var players = PlayerControllerState.HumansBySteamId();
        foreach (var (attackerId, victims) in _damageDealt)
        {
            if (!players.TryGetValue(attackerId, out var attacker)) continue;

            foreach (var victimEntry in victims)
            {
                if (!players.TryGetValue(victimEntry.Key, out var victim)) continue;

                var info = victimEntry.Value;
                attacker.PrintToChat($" {ChatColors.Green}[Damage]{ChatColors.Default} To {ChatColors.Blue}{victim.PlayerName}{ChatColors.Default}: {ChatColors.Red}{info.Damage}{ChatColors.Default} in {ChatColors.Yellow}{info.Hits}{ChatColors.Default} hits");
            }
        }
    }

    public void ResetRound()
    {
        _damageDealt.Clear();
    }
}
