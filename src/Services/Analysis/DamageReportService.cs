using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<DamageReportService> _logger;
    private readonly IGameScheduler _scheduler;
    private readonly ConcurrentDictionary<ulong, ConcurrentDictionary<ulong, DamageInfo>> _damageDealt = new();

    public DamageReportService(ILogger<DamageReportService> logger, IGameScheduler scheduler)
    {
        _logger = logger;
        _scheduler = scheduler;
    }

    public void RecordDamage(ulong attackerId, ulong victimId, int damage)
    {
        if (attackerId == 0 || victimId == 0 || attackerId == victimId) return;

        var victims = _damageDealt.GetOrAdd(attackerId, _ => new ConcurrentDictionary<ulong, DamageInfo>());
        victims.AddOrUpdate(victimId, 
            new DamageInfo(damage, 1), 
            (_, existing) => new DamageInfo(existing.Damage + damage, existing.Hits + 1));
    }

    public void ReportToPlayers()
    {
        _scheduler.Schedule(() =>
        {
            foreach (var attackerId in _damageDealt.Keys)
            {
                var attacker = Utilities.GetPlayerFromSteamId(attackerId);
                if (attacker == null || !attacker.IsValid) continue;

                if (_damageDealt.TryGetValue(attackerId, out var victims))
                {
                    foreach (var victimEntry in victims)
                    {
                        var victim = Utilities.GetPlayerFromSteamId(victimEntry.Key);
                        if (victim == null || !victim.IsValid) continue;

                        var info = victimEntry.Value;
                        attacker.PrintToChat($" {ChatColors.Green}[Damage]{ChatColors.Default} To {ChatColors.Blue}{victim.PlayerName}{ChatColors.Default}: {ChatColors.Red}{info.Damage}{ChatColors.Default} in {ChatColors.Yellow}{info.Hits}{ChatColors.Default} hits");
                    }
                }
            }
        });
    }

    public void ResetRound()
    {
        _damageDealt.Clear();
    }
}
