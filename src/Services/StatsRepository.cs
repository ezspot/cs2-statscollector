using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using statsCollector.Domain;
using statsCollector.Infrastructure.Database;
using statsCollector.Infrastructure;
using Polly;
using Polly.Registry;

namespace statsCollector.Services;

public interface IStatsRepository
{
    Task UpsertPlayersAsync(IEnumerable<PlayerSnapshot> snapshots, CancellationToken cancellationToken);
    Task UpsertPlayerAsync(PlayerSnapshot snapshot, CancellationToken cancellationToken);
    Task UpsertMatchSummariesAsync(IEnumerable<PlayerSnapshot> snapshots, CancellationToken cancellationToken);
    Task UpsertMatchWeaponStatsAsync(IEnumerable<PlayerSnapshot> snapshots, CancellationToken cancellationToken);
}

public sealed class StatsRepository : IStatsRepository
{
    private readonly IConnectionFactory _connectionFactory;
    private readonly ResiliencePipeline _resiliencePipeline;
    private readonly ILogger<StatsRepository> _logger;

    public StatsRepository(
        IConnectionFactory connectionFactory,
        ResiliencePipelineProvider<string> pipelineProvider,
        ILogger<StatsRepository> logger)
    {
        _connectionFactory = connectionFactory;
        _logger = logger;
        _resiliencePipeline = pipelineProvider.GetPipeline("database");
    }

    public async Task UpsertPlayersAsync(IEnumerable<PlayerSnapshot> snapshots, CancellationToken cancellationToken)
    {
        var snapshotList = snapshots.ToList();
        if (snapshotList.Count == 0) return;

        await _resiliencePipeline.ExecuteAsync(async ct => 
        {
            using var activity = Instrumentation.ActivitySource.StartActivity("UpsertPlayersAsync");
            Instrumentation.DbOperationsCounter.Add(1, new KeyValuePair<string, object?>("operation", "upsert_players"));
            
            _logger.LogDebug("Starting bulk upsert for {Count} players", snapshotList.Count);
            try
            {
                await UpsertPlayersCoreAsync(snapshotList, ct).ConfigureAwait(false);
                _logger.LogInformation("Successfully completed bulk upsert for {Count} players", snapshotList.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bulk upsert players failed for {Count} players", snapshotList.Count);
                throw;
            }
        }, cancellationToken);
    }

    public Task UpsertPlayerAsync(PlayerSnapshot snapshot, CancellationToken cancellationToken) =>
        UpsertPlayersAsync([snapshot], cancellationToken);

    public async Task UpsertMatchSummariesAsync(IEnumerable<PlayerSnapshot> snapshots, CancellationToken cancellationToken)
    {
        var snapshotList = snapshots.Where(s => s.MatchId.HasValue).ToList();
        if (snapshotList.Count == 0) return;

        await _resiliencePipeline.ExecuteAsync(async ct => 
        {
            using var activity = Instrumentation.ActivitySource.StartActivity("UpsertMatchSummariesAsync");
            Instrumentation.DbOperationsCounter.Add(1, new KeyValuePair<string, object?>("operation", "upsert_match_summaries"));

            const string sql = """
                INSERT INTO match_player_stats (
                    match_id, steam_id, kills, deaths, assists, headshots, damage_dealt, mvps, score, rating2, adr, kast
                ) VALUES (
                    @MatchId, @SteamId, @Kills, @Deaths, @Assists, @Headshots, @DamageDealt, @Mvps, @Score, @HLTVRating, @AverageDamagePerRound, @KASTPercentage
                )
                ON DUPLICATE KEY UPDATE
                    kills = VALUES(kills), deaths = VALUES(deaths), assists = VALUES(assists),
                    headshots = VALUES(headshots), damage_dealt = VALUES(damage_dealt),
                    mvps = VALUES(mvps), score = VALUES(score), rating2 = VALUES(rating2),
                    adr = VALUES(adr), kast = VALUES(kast);
                """;

            await using var connection = await _connectionFactory.CreateConnectionAsync(ct);
            var count = await connection.ExecuteAsync(new CommandDefinition(sql, snapshotList, cancellationToken: ct)).ConfigureAwait(false);
            _logger.LogInformation("Upserted {Count} match summaries into match_player_stats", count);
        }, cancellationToken);
    }

    public async Task UpsertMatchWeaponStatsAsync(IEnumerable<PlayerSnapshot> snapshots, CancellationToken cancellationToken)
    {
        var snapshotList = snapshots.Where(s => s.MatchId.HasValue).ToList();
        if (snapshotList.Count == 0) return;

        var weaponData = snapshotList.SelectMany(s => s.WeaponKills.Select(kvp => new
        {
            s.MatchId,
            s.SteamId,
            Weapon = kvp.Key,
            Kills = kvp.Value,
            Shots = s.WeaponShots.GetValueOrDefault(kvp.Key, 0),
            Hits = s.WeaponHits.GetValueOrDefault(kvp.Key, 0)
        })).ToList();

        if (weaponData.Count == 0) return;

        await _resiliencePipeline.ExecuteAsync(async ct => 
        {
            using var activity = Instrumentation.ActivitySource.StartActivity("UpsertMatchWeaponStatsAsync");
            Instrumentation.DbOperationsCounter.Add(1, new KeyValuePair<string, object?>("operation", "upsert_match_weapons"));

            const string sql = """
                INSERT INTO match_weapon_stats (
                    match_id, steam_id, weapon_name, kills, shots, hits
                ) VALUES (
                    @MatchId, @SteamId, @Weapon, @Kills, @Shots, @Hits
                )
                ON DUPLICATE KEY UPDATE
                    kills = VALUES(kills), shots = VALUES(shots), hits = VALUES(hits);
                """;

            await using var connection = await _connectionFactory.CreateConnectionAsync(ct);
            var count = await connection.ExecuteAsync(new CommandDefinition(sql, weaponData, cancellationToken: ct)).ConfigureAwait(false);
            _logger.LogInformation("Upserted {Count} match weapon records into match_weapon_stats", count);
        }, cancellationToken);
    }

    private async Task UpsertPlayersCoreAsync(IEnumerable<PlayerSnapshot> snapshots, CancellationToken cancellationToken)
    {
        var snapshotList = snapshots.ToList();
        if (snapshotList.Count == 0) return;

        const string upsertPlayerInfoSql = """
            INSERT INTO players (steam_id, name)
            VALUES (@SteamId, @Name)
            ON DUPLICATE KEY UPDATE
                name = VALUES(name),
                last_seen = CURRENT_TIMESTAMP;
            """;

        const string upsertPlayerStatsSql = """
            INSERT INTO player_stats (
                steam_id, name, kills, deaths, assists, headshots, damage_dealt, damage_taken, damage_armor,
                shots_fired, shots_hit, mvps, score, rounds_played, rounds_won, total_spawns, playtime_seconds,
                ct_rounds, t_rounds, grenades_thrown, flashes_thrown, smokes_thrown, molotovs_thrown,
                he_grenades_thrown, decoys_thrown, tactical_grenades_thrown, players_blinded, times_blinded,
                flash_assists, total_blind_time, total_blind_time_inflicted, utility_damage_dealt, utility_damage_taken,
                bomb_plants, bomb_defuses, bomb_plant_attempts, bomb_plant_aborts, bomb_defuse_attempts,
                bomb_defuse_aborts, bomb_defuse_with_kit, bomb_defuse_without_kit, bomb_drops, bomb_pickups,
                defuser_drops, defuser_pickups, clutch_defuses, total_plant_time, total_defuse_time,
                bomb_kills, bomb_deaths, hostages_rescued, jumps,
                money_spent, equipment_value, items_purchased, items_picked_up, items_dropped, cash_earned, cash_spent,
                enemies_flashed, teammates_flashed, effective_flashes, effective_smokes, effective_he_grenades,
                effective_molotovs, flash_waste, multi_kill_nades, nade_kills,
                entry_kills, trade_kills, traded_deaths, high_impact_kills, low_impact_kills, trade_opportunities, trade_windows_missed, multi_kills, opening_duels_won, opening_duels_lost,
                revenges, clutches_won, clutches_lost, clutch_points, mvps_eliminations, mvps_bomb, mvps_hostage,
                headshots_hit, chest_hits, stomach_hits, arm_hits, leg_hits,
                kd_ratio, headshot_percentage, accuracy_percentage, kast_percentage,
                average_damage_per_round, hltv_rating, impact_rating, survival_rating, utility_score,
                noscope_kills, thru_smoke_kills, attacker_blind_kills, flash_assisted_kills, wallbang_kills
            ) VALUES (
                @SteamId, @Name, @Kills, @Deaths, @Assists, @Headshots, @DamageDealt, @DamageTaken, @DamageArmor,
                @ShotsFired, @ShotsHit, @Mvps, @Score, @RoundsPlayed, @RoundsWon, @TotalSpawns, @PlaytimeSeconds,
                @CtRounds, @TRounds, @GrenadesThrown, @FlashesThrown, @SmokesThrown, @MolotovsThrown,
                @HeGrenadesThrown, @DecoysThrown, @TacticalGrenadesThrown, @PlayersBlinded, @TimesBlinded,
                @FlashAssists, @TotalBlindTime, @TotalBlindTimeInflicted, @UtilityDamageDealt, @UtilityDamageTaken,
                @BombPlants, @BombDefuses, @BombPlantAttempts, @BombPlantAborts, @BombDefuseAttempts,
                @BombDefuseAborts, @BombDefuseWithKit, @BombDefuseWithoutKit, @BombDrops, @BombPickups,
                @DefuserDrops, @DefuserPickups, @ClutchDefuses, @TotalPlantTime, @TotalDefuseTime,
                @BombKills, @BombDeaths, @HostagesRescued, @Jumps,
                @MoneySpent, @EquipmentValue, @ItemsPurchased, @ItemsPickedUp, @ItemsDropped, @CashEarned, @CashSpent,
                @EnemiesFlashed, @TeammatesFlashed, @EffectiveFlashes, @EffectiveSmokes, @EffectiveHEGrenades,
                @EffectiveMolotovs, @FlashWaste, @MultiKillNades, @NadeKills,
                @EntryKills, @TradeKills, @TradedDeaths, @HighImpactKills, @LowImpactKills, @TradeOpportunities, @TradeWindowsMissed, @MultiKills, @OpeningDuelsWon, @OpeningDuelsLost,
                @Revenges, @ClutchesWon, @ClutchesLost, @ClutchPoints, @MvpsEliminations, @MvpsBomb, @MvpsHostage,
                @HeadshotsHit, @ChestHits, @StomachHits, @ArmHits, @LegHits,
                @KDRatio, @HeadshotPercentage, @AccuracyPercentage, @KASTPercentage,
                @AverageDamagePerRound, @HLTVRating, @ImpactRating, @SurvivalRating, @UtilityScore,
                @NoscopeKills, @ThruSmokeKills, @AttackerBlindKills, @FlashAssistedKills, @WallbangKills
            )
            ON DUPLICATE KEY UPDATE
                name = VALUES(name), kills = VALUES(kills), deaths = VALUES(deaths), assists = VALUES(assists),
                headshots = VALUES(headshots), damage_dealt = VALUES(damage_dealt), damage_taken = VALUES(damage_taken),
                damage_armor = VALUES(damage_armor), shots_fired = VALUES(shots_fired), shots_hit = VALUES(shots_hit),
                mvps = VALUES(mvps), score = VALUES(score), rounds_played = VALUES(rounds_played),
                rounds_won = VALUES(rounds_won), total_spawns = VALUES(total_spawns), playtime_seconds = VALUES(playtime_seconds),
                ct_rounds = VALUES(ct_rounds), t_rounds = VALUES(t_rounds), 
                grenades_thrown = VALUES(grenades_thrown),
                flashes_thrown = VALUES(flashes_thrown), smokes_thrown = VALUES(smokes_thrown), molotovs_thrown = VALUES(molotovs_thrown),
                he_grenades_thrown = VALUES(he_grenades_thrown), decoys_thrown = VALUES(decoys_thrown),
                tactical_grenades_thrown = VALUES(tactical_grenades_thrown), players_blinded = VALUES(players_blinded),
                times_blinded = VALUES(times_blinded), flash_assists = VALUES(flash_assists),
                total_blind_time = VALUES(total_blind_time), total_blind_time_inflicted = VALUES(total_blind_time_inflicted),
                utility_damage_dealt = VALUES(utility_damage_dealt), utility_damage_taken = VALUES(utility_damage_taken),
                bomb_plants = VALUES(bomb_plants), bomb_defuses = VALUES(bomb_defuses),
                bomb_plant_attempts = VALUES(bomb_plant_attempts), bomb_plant_aborts = VALUES(bomb_plant_aborts),
                bomb_defuse_attempts = VALUES(bomb_defuse_attempts), bomb_defuse_aborts = VALUES(bomb_defuse_aborts),
                bomb_defuse_with_kit = VALUES(bomb_defuse_with_kit), bomb_defuse_without_kit = VALUES(bomb_defuse_without_kit),
                bomb_drops = VALUES(bomb_drops), bomb_pickups = VALUES(bomb_pickups),
                defuser_drops = VALUES(defuser_drops), defuser_pickups = VALUES(defuser_pickups),
                clutch_defuses = VALUES(clutch_defuses), total_plant_time = VALUES(total_plant_time),
                total_defuse_time = VALUES(total_defuse_time), bomb_kills = VALUES(bomb_kills), bomb_deaths = VALUES(bomb_deaths),
                hostages_rescued = VALUES(hostages_rescued), jumps = VALUES(jumps),
                money_spent = VALUES(money_spent), equipment_value = VALUES(equipment_value),
                items_purchased = VALUES(items_purchased), items_picked_up = VALUES(items_picked_up),
                items_dropped = VALUES(items_dropped), cash_earned = VALUES(cash_earned), cash_spent = VALUES(cash_spent),
                enemies_flashed = VALUES(enemies_flashed), teammates_flashed = VALUES(teammates_flashed),
                effective_flashes = VALUES(effective_flashes), effective_smokes = VALUES(effective_smokes),
                effective_he_grenades = VALUES(effective_he_grenades), effective_molotovs = VALUES(effective_molotovs),
                flash_waste = VALUES(flash_waste), multi_kill_nades = VALUES(multi_kill_nades), nade_kills = VALUES(nade_kills),
                entry_kills = VALUES(entry_kills), trade_kills = VALUES(trade_kills), traded_deaths = VALUES(traded_deaths),
                high_impact_kills = VALUES(high_impact_kills), low_impact_kills = VALUES(low_impact_kills), trade_opportunities = VALUES(trade_opportunities),
                trade_windows_missed = VALUES(trade_windows_missed), multi_kills = VALUES(multi_kills), opening_duels_won = VALUES(opening_duels_won),
                opening_duels_lost = VALUES(opening_duels_lost), revenges = VALUES(revenges),
                clutches_won = VALUES(clutches_won), clutches_lost = VALUES(clutches_lost), clutch_points = VALUES(clutch_points),
                mvps_eliminations = VALUES(mvps_eliminations), mvps_bomb = VALUES(mvps_bomb), mvps_hostage = VALUES(mvps_hostage),
                headshots_hit = VALUES(headshots_hit), chest_hits = VALUES(chest_hits), stomach_hits = VALUES(stomach_hits),
                arm_hits = VALUES(arm_hits), leg_hits = VALUES(leg_hits),
                kd_ratio = VALUES(kd_ratio), headshot_percentage = VALUES(headshot_percentage),
                accuracy_percentage = VALUES(accuracy_percentage), kast_percentage = VALUES(kast_percentage),
                average_damage_per_round = VALUES(average_damage_per_round), hltv_rating = VALUES(hltv_rating),
                impact_rating = VALUES(impact_rating), survival_rating = VALUES(survival_rating),
                utility_score = VALUES(utility_score),
                noscope_kills = VALUES(noscope_kills), thru_smoke_kills = VALUES(thru_smoke_kills),
                attacker_blind_kills = VALUES(attacker_blind_kills), flash_assisted_kills = VALUES(flash_assisted_kills),
                wallbang_kills = VALUES(wallbang_kills);
            """;

        const string upsertWeaponStatsSql = """
            INSERT INTO weapon_stats (steam_id, weapon_name, kills, deaths, shots, hits, headshots)
            VALUES (@SteamId, @Weapon, @Kills, 0, @Shots, @Hits, 0)
            ON DUPLICATE KEY UPDATE
                kills = VALUES(kills),
                shots = VALUES(shots),
                hits = VALUES(hits);
            """;

        const string insertAdvancedAnalyticsSql = """
            INSERT INTO player_advanced_analytics (
                match_id, steam_id, calculated_at, name, rating2, kills_per_round, deaths_per_round, impact_score, kast_percentage,
                average_damage_per_round, utility_impact_score, clutch_success_rate, trade_success_rate, 
                trade_windows_missed, flash_waste, entry_success_rate,
                rounds_played, kd_ratio, headshot_percentage, opening_kill_ratio, trade_kill_ratio,
                grenade_effectiveness_rate, flash_effectiveness_rate, utility_usage_per_round,
                average_money_spent_per_round, performance_score, top_weapon_by_kills, survival_rating, utility_score, clutch_points,
                flash_assisted_kills, wallbang_kills
            ) VALUES (
                @MatchId, @SteamId, @CalculatedAt, @Name, @Rating2, @KillsPerRound, @DeathsPerRound, @ImpactScore, @KastPercentage,
                @AverageDamagePerRound, @UtilityImpactScore, @ClutchSuccessRate, @TradeSuccessRate, 
                @TradeWindowsMissed, @FlashWaste, @EntrySuccessRate,
                @RoundsPlayed, @KdRatio, @HeadshotPercentage, @OpeningKillRatio, @TradeKillRatio,
                @GrenadeEffectivenessRate, @FlashEffectivenessRate, @UtilityUsagePerRound,
                @AverageMoneySpentPerRound, @PerformanceScore, @TopWeaponByKills, @SurvivalRating, @UtilityScore, @ClutchPoints,
                @FlashAssistedKills, @WallbangKills
            );
            """;

        await using var connection = await _connectionFactory.CreateConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        try
        {
            _logger.LogDebug("Executing upsertPlayerInfoSql for {Count} players", snapshotList.Count);
            var playerInfoCount = await connection.ExecuteAsync(new CommandDefinition(upsertPlayerInfoSql, snapshotList, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            _logger.LogDebug("upsertPlayerInfoSql affected {Count} rows", playerInfoCount);

            _logger.LogDebug("Executing upsertPlayerStatsSql for {Count} players", snapshotList.Count);
            var playerStatsCount = await connection.ExecuteAsync(new CommandDefinition(upsertPlayerStatsSql, snapshotList, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            _logger.LogDebug("upsertPlayerStatsSql affected {Count} rows", playerStatsCount);

            var weaponData = snapshotList.SelectMany(s => s.WeaponKills.Select(kvp => new
            {
                s.SteamId,
                Weapon = kvp.Key,
                Kills = kvp.Value,
                Shots = s.WeaponShots.GetValueOrDefault(kvp.Key, 0),
                Hits = s.WeaponHits.GetValueOrDefault(kvp.Key, 0)
            })).ToList();

            if (weaponData.Count > 0)
            {
                _logger.LogDebug("Executing upsertWeaponStatsSql for {Count} weapon records", weaponData.Count);
                var weaponStatsCount = await connection.ExecuteAsync(new CommandDefinition(upsertWeaponStatsSql, weaponData, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
                _logger.LogDebug("upsertWeaponStatsSql affected {Count} rows", weaponStatsCount);
            }

            var now = DateTime.UtcNow;
            var advancedData = snapshotList.Select(s => new
            {
                s.MatchId,
                s.SteamId,
                CalculatedAt = now,
                s.Name,
                Rating2 = s.HLTVRating,
                KillsPerRound = s.AverageKillsPerRound,
                DeathsPerRound = s.AverageDeathsPerRound,
                ImpactScore = s.ImpactRating,
                KastPercentage = s.KASTPercentage,
                s.AverageDamagePerRound,
                UtilityImpactScore = s.RoundsPlayed > 0 ? (decimal)s.UtilityDamageDealt / s.RoundsPlayed : 0m,
                s.ClutchSuccessRate,
                TradeSuccessRate = s.TradeKillRatio,
                s.TradeWindowsMissed,
                s.FlashWaste,
                EntrySuccessRate = s.OpeningKillRatio,
                s.RoundsPlayed,
                KdRatio = s.KDRatio,
                s.HeadshotPercentage,
                s.OpeningKillRatio,
                s.TradeKillRatio,
                s.GrenadeEffectivenessRate,
                s.FlashEffectivenessRate,
                s.UtilityUsagePerRound,
                s.AverageMoneySpentPerRound,
                s.PerformanceScore,
                TopWeaponByKills = s.GetBestWeaponByKills(),
                s.SurvivalRating,
                s.UtilityScore,
                s.ClutchPoints,
                s.FlashAssistedKills,
                s.WallbangKills
            }).ToList();

            _logger.LogDebug("Executing insertAdvancedAnalyticsSql for {Count} records", advancedData.Count);
            var advancedAnalyticsCount = await connection.ExecuteAsync(new CommandDefinition(insertAdvancedAnalyticsSql, advancedData, transaction, cancellationToken: cancellationToken)).ConfigureAwait(false);
            _logger.LogDebug("insertAdvancedAnalyticsSql affected {Count} rows", advancedAnalyticsCount);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Transaction committed successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Transaction failed, rolling back. Error: {Message}", ex.Message);
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }
}
