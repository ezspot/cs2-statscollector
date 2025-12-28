using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Polly;
using statsCollector.Domain;
using statsCollector.Infrastructure.Database;

namespace statsCollector.Services;

public interface IStatsRepository
{
    Task UpsertPlayerAsync(PlayerSnapshot snapshot, CancellationToken cancellationToken);
}

public sealed class StatsRepository(
    IConnectionFactory connectionFactory,
    IAsyncPolicy retryPolicy,
    ILogger<StatsRepository> logger) : IStatsRepository
{
    public Task UpsertPlayerAsync(PlayerSnapshot snapshot, CancellationToken cancellationToken) =>
        retryPolicy.ExecuteAsync(ct => ExecuteDbOperationAsync("upsert-player", operationCt => UpsertPlayerCoreAsync(snapshot, operationCt), ct), cancellationToken);

    private async Task ExecuteDbOperationAsync(string operationName, Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["DbOperation"] = operationName });
        try
        {
            await operation(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Database operation '{OperationName}' failed", operationName);
            throw;
        }
    }

    private async Task UpsertPlayerCoreAsync(PlayerSnapshot snapshot, CancellationToken cancellationToken)
    {
        const string upsertPlayerInfoSql = """
INSERT INTO players (steam_id, name)
VALUES (@steamId, @name)
ON DUPLICATE KEY UPDATE
    name = VALUES(name),
    last_seen = CURRENT_TIMESTAMP
""";

        const string upsertPlayerSql = """
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
    effective_molotovs, multi_kill_nades, nade_kills,
    entry_kills, trade_kills, traded_deaths, high_impact_kills, low_impact_kills, trade_opportunities, multi_kills, opening_duels_won, opening_duels_lost,
    revenges, clutches_won, clutches_lost, clutch_points, mvps_eliminations, mvps_bomb, mvps_hostage,
    headshots_hit, chest_hits, stomach_hits, arm_hits, leg_hits,
    kd_ratio, headshot_percentage, accuracy_percentage, kast_percentage,
    average_damage_per_round, hltv_rating, impact_rating, survival_rating, utility_score,
    noscope_kills, thru_smoke_kills, attacker_blind_kills, flash_assisted_kills, wallbang_kills
) VALUES (
    @steamId, @name, @kills, @deaths, @assists, @headshots, @damageDealt, @damageTaken, @damageArmor,
    @shotsFired, @shotsHit, @mvps, @score, @roundsPlayed, @roundsWon, @totalSpawns, @playtimeSeconds,
    @ctRounds, @tRounds, @grenadesThrown, @flashesThrown, @smokesThrown, @molotovsThrown,
    @heGrenadesThrown, @decoysThrown, @tacticalGrenadesThrown, @playersBlinded, @timesBlinded,
    @flashAssists, @totalBlindTime, @totalBlindTimeInflicted, @utilityDamageDealt, @utilityDamageTaken,
    @bombPlants, @bombDefuses, @bombPlantAttempts, @bombPlantAborts, @bombDefuseAttempts,
    @bombDefuseAborts, @bombDefuseWithKit, @bombDefuseWithoutKit, @bombDrops, @bombPickups,
    @defuserDrops, @defuserPickups, @clutchDefuses, @totalPlantTime, @totalDefuseTime,
    @bombKills, @bombDeaths, @hostagesRescued, @jumps,
    @moneySpent, @equipmentValue, @itemsPurchased, @itemsPickedUp, @itemsDropped, @cashEarned, @cashSpent,
    @enemiesFlashed, @teammatesFlashed, @effectiveFlashes, @effectiveSmokes, @effectiveHEGrenades,
    @effectiveMolotovs, @multiKillNades, @nadeKills,
    @entryKills, @tradeKills, @tradedDeaths, @highImpactKills, @lowImpactKills, @tradeOpportunities, @multiKills, @openingDuelsWon, @openingDuelsLost,
    @revenges, @clutchesWon, @clutchesLost, @clutchPoints, @mvpsEliminations, @mvpsBomb, @mvpsHostage,
    @headshotsHit, @chestHits, @stomachHits, @armHits, @legHits,
    @kdRatio, @headshotPercentage, @accuracyPercentage, @kastPercentage,
    @averageDamagePerRound, @hltvRating, @impactRating, @survivalRating, @utilityScore,
    @noscopeKills, @thruSmokeKills, @attackerBlindKills, @flashAssistedKills, @wallbangKills
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
    multi_kill_nades = VALUES(multi_kill_nades), nade_kills = VALUES(nade_kills),
    entry_kills = VALUES(entry_kills), trade_kills = VALUES(trade_kills), traded_deaths = VALUES(traded_deaths),
    high_impact_kills = VALUES(high_impact_kills), low_impact_kills = VALUES(low_impact_kills), trade_opportunities = VALUES(trade_opportunities),
    multi_kills = VALUES(multi_kills), opening_duels_won = VALUES(opening_duels_won),
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
    wallbang_kills = VALUES(wallbang_kills)
""";

        const string upsertWeaponSql = """
INSERT INTO weapon_stats (steam_id, weapon_name, kills, deaths, shots, hits, headshots)
VALUES (@steamId, @weapon, @kills, 0, @shots, @hits, 0)
ON DUPLICATE KEY UPDATE
    kills = VALUES(kills),
    shots = VALUES(shots),
    hits = VALUES(hits)
""";

        const string insertAdvancedSql = """
INSERT INTO player_advanced_analytics (
    steam_id, calculated_at, name, rating2, kills_per_round, deaths_per_round, impact_score, kast_percentage,
    average_damage_per_round, utility_impact_score, clutch_success_rate, trade_success_rate, entry_success_rate,
    rounds_played, kd_ratio, headshot_percentage, opening_kill_ratio, trade_kill_ratio,
    grenade_effectiveness_rate, flash_effectiveness_rate, utility_usage_per_round,
    average_money_spent_per_round, performance_score, top_weapon_by_kills, survival_rating, utility_score, clutch_points,
    flash_assisted_kills, wallbang_kills
) VALUES (
    @steamId, @calculatedAt, @name, @rating2, @killsPerRound, @deathsPerRound, @impactScore, @kastPercentage,
    @averageDamagePerRound, @utilityImpactScore, @clutchSuccessRate, @tradeSuccessRate, @entrySuccessRate,
    @roundsPlayed, @kdRatio, @headshotPercentage, @openingKillRatio, @tradeKillRatio,
    @grenadeEffectivenessRate, @flashEffectivenessRate, @utilityUsagePerRound,
    @averageMoneySpentPerRound, @performanceScore, @topWeaponByKills, @survivalRating, @utilityScore, @clutchPoints,
    @flashAssistedKills, @wallbangKills
)
""";

        await using var connection = connectionFactory.CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            await using var playerInfoCmd = new MySqlCommand(upsertPlayerInfoSql, connection, transaction);
            playerInfoCmd.Parameters.AddWithValue("@steamId", snapshot.SteamId);
            playerInfoCmd.Parameters.AddWithValue("@name", snapshot.Name);
            await playerInfoCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var playerCmd = new MySqlCommand(upsertPlayerSql, connection, transaction);
            AddPlayerParameters(playerCmd, snapshot);
            await playerCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            // weapon stats (latest snapshot, per weapon)
            foreach (var kvp in snapshot.WeaponKills)
            {
                var weapon = kvp.Key;
                var kills = kvp.Value;
                snapshot.WeaponShots.TryGetValue(weapon, out var shots);
                snapshot.WeaponHits.TryGetValue(weapon, out var hits);

                await using var weaponCmd = new MySqlCommand(upsertWeaponSql, connection, transaction);
                weaponCmd.Parameters.AddWithValue("@steamId", snapshot.SteamId);
                weaponCmd.Parameters.AddWithValue("@weapon", weapon);
                weaponCmd.Parameters.AddWithValue("@kills", kills);
                weaponCmd.Parameters.AddWithValue("@shots", shots);
                weaponCmd.Parameters.AddWithValue("@hits", hits);
                await weaponCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var advancedCmd = new MySqlCommand(insertAdvancedSql, connection, transaction);
            AddAdvancedParameters(advancedCmd, snapshot);
            await advancedCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    private static void AddPlayerParameters(MySqlCommand cmd, PlayerSnapshot snapshot)
    {
        // Core stats
        cmd.Parameters.AddWithValue("@steamId", snapshot.SteamId);
        cmd.Parameters.AddWithValue("@name", snapshot.Name);
        cmd.Parameters.AddWithValue("@kills", snapshot.Kills);
        cmd.Parameters.AddWithValue("@deaths", snapshot.Deaths);
        cmd.Parameters.AddWithValue("@assists", snapshot.Assists);
        cmd.Parameters.AddWithValue("@headshots", snapshot.Headshots);
        cmd.Parameters.AddWithValue("@damageDealt", snapshot.DamageDealt);
        cmd.Parameters.AddWithValue("@damageTaken", snapshot.DamageTaken);
        cmd.Parameters.AddWithValue("@damageArmor", snapshot.DamageArmor);
        cmd.Parameters.AddWithValue("@shotsFired", snapshot.ShotsFired);
        cmd.Parameters.AddWithValue("@shotsHit", snapshot.ShotsHit);
        cmd.Parameters.AddWithValue("@mvps", snapshot.Mvps);
        cmd.Parameters.AddWithValue("@score", snapshot.Score);
        cmd.Parameters.AddWithValue("@roundsPlayed", snapshot.RoundsPlayed);
        cmd.Parameters.AddWithValue("@roundsWon", snapshot.RoundsWon);
        cmd.Parameters.AddWithValue("@totalSpawns", snapshot.TotalSpawns);
        cmd.Parameters.AddWithValue("@playtimeSeconds", snapshot.PlaytimeSeconds);
        cmd.Parameters.AddWithValue("@ctRounds", snapshot.CtRounds);
        cmd.Parameters.AddWithValue("@tRounds", snapshot.TRounds);
        
        // Utility stats
        cmd.Parameters.AddWithValue("@grenadesThrown", snapshot.GrenadesThrown);
        cmd.Parameters.AddWithValue("@flashesThrown", snapshot.FlashesThrown);
        cmd.Parameters.AddWithValue("@smokesThrown", snapshot.SmokesThrown);
        cmd.Parameters.AddWithValue("@molotovsThrown", snapshot.MolotovsThrown);
        cmd.Parameters.AddWithValue("@heGrenadesThrown", snapshot.HeGrenadesThrown);
        cmd.Parameters.AddWithValue("@decoysThrown", snapshot.DecoysThrown);
        cmd.Parameters.AddWithValue("@tacticalGrenadesThrown", snapshot.TacticalGrenadesThrown);
        cmd.Parameters.AddWithValue("@playersBlinded", snapshot.PlayersBlinded);
        cmd.Parameters.AddWithValue("@timesBlinded", snapshot.TimesBlinded);
        cmd.Parameters.AddWithValue("@flashAssists", snapshot.FlashAssists);
        cmd.Parameters.AddWithValue("@totalBlindTime", snapshot.TotalBlindTime);
        cmd.Parameters.AddWithValue("@totalBlindTimeInflicted", snapshot.TotalBlindTimeInflicted);
        cmd.Parameters.AddWithValue("@utilityDamageDealt", snapshot.UtilityDamageDealt);
        cmd.Parameters.AddWithValue("@utilityDamageTaken", snapshot.UtilityDamageTaken);
        
        // Bomb stats
        cmd.Parameters.AddWithValue("@bombPlants", snapshot.BombPlants);
        cmd.Parameters.AddWithValue("@bombDefuses", snapshot.BombDefuses);
        cmd.Parameters.AddWithValue("@bombPlantAttempts", snapshot.BombPlantAttempts);
        cmd.Parameters.AddWithValue("@bombPlantAborts", snapshot.BombPlantAborts);
        cmd.Parameters.AddWithValue("@bombDefuseAttempts", snapshot.BombDefuseAttempts);
        cmd.Parameters.AddWithValue("@bombDefuseAborts", snapshot.BombDefuseAborts);
        cmd.Parameters.AddWithValue("@bombDefuseWithKit", snapshot.BombDefuseWithKit);
        cmd.Parameters.AddWithValue("@bombDefuseWithoutKit", snapshot.BombDefuseWithoutKit);
        cmd.Parameters.AddWithValue("@bombDrops", snapshot.BombDrops);
        cmd.Parameters.AddWithValue("@bombPickups", snapshot.BombPickups);
        cmd.Parameters.AddWithValue("@defuserDrops", snapshot.DefuserDrops);
        cmd.Parameters.AddWithValue("@defuserPickups", snapshot.DefuserPickups);
        cmd.Parameters.AddWithValue("@clutchDefuses", snapshot.ClutchDefuses);
        cmd.Parameters.AddWithValue("@totalPlantTime", snapshot.TotalPlantTime);
        cmd.Parameters.AddWithValue("@totalDefuseTime", snapshot.TotalDefuseTime);
        cmd.Parameters.AddWithValue("@bombKills", snapshot.BombKills);
        cmd.Parameters.AddWithValue("@bombDeaths", snapshot.BombDeaths);
        
        // Other stats
        cmd.Parameters.AddWithValue("@hostagesRescued", snapshot.HostagesRescued);
        cmd.Parameters.AddWithValue("@jumps", snapshot.Jumps);
        
        // Economy stats
        cmd.Parameters.AddWithValue("@moneySpent", snapshot.MoneySpent);
        cmd.Parameters.AddWithValue("@equipmentValue", snapshot.EquipmentValue);
        cmd.Parameters.AddWithValue("@itemsPurchased", snapshot.ItemsPurchased);
        cmd.Parameters.AddWithValue("@itemsPickedUp", snapshot.ItemsPickedUp);
        cmd.Parameters.AddWithValue("@itemsDropped", snapshot.ItemsDropped);
        cmd.Parameters.AddWithValue("@cashEarned", snapshot.CashEarned);
        cmd.Parameters.AddWithValue("@cashSpent", snapshot.CashSpent);
        
        // Grenade effectiveness
        cmd.Parameters.AddWithValue("@enemiesFlashed", snapshot.EnemiesFlashed);
        cmd.Parameters.AddWithValue("@teammatesFlashed", snapshot.TeammatesFlashed);
        cmd.Parameters.AddWithValue("@effectiveFlashes", snapshot.EffectiveFlashes);
        cmd.Parameters.AddWithValue("@effectiveSmokes", snapshot.EffectiveSmokes);
        cmd.Parameters.AddWithValue("@effectiveHEGrenades", snapshot.EffectiveHEGrenades);
        cmd.Parameters.AddWithValue("@effectiveMolotovs", snapshot.EffectiveMolotovs);
        cmd.Parameters.AddWithValue("@multiKillNades", snapshot.MultiKillNades);
        cmd.Parameters.AddWithValue("@nadeKills", snapshot.NadeKills);
        
        // Advanced combat
        cmd.Parameters.AddWithValue("@entryKills", snapshot.EntryKills);
        cmd.Parameters.AddWithValue("@tradeKills", snapshot.TradeKills);
        cmd.Parameters.AddWithValue("@tradedDeaths", snapshot.TradedDeaths);
        cmd.Parameters.AddWithValue("@highImpactKills", snapshot.HighImpactKills);
        cmd.Parameters.AddWithValue("@lowImpactKills", snapshot.LowImpactKills);
        cmd.Parameters.AddWithValue("@tradeOpportunities", snapshot.TradeOpportunities);
        cmd.Parameters.AddWithValue("@multiKills", snapshot.MultiKills);
        cmd.Parameters.AddWithValue("@openingDuelsWon", snapshot.OpeningDuelsWon);
        cmd.Parameters.AddWithValue("@openingDuelsLost", snapshot.OpeningDuelsLost);
        cmd.Parameters.AddWithValue("@revenges", snapshot.Revenges);
        cmd.Parameters.AddWithValue("@clutchesWon", snapshot.ClutchesWon);
        cmd.Parameters.AddWithValue("@clutchesLost", snapshot.ClutchesLost);
        cmd.Parameters.Add("@clutchPoints", MySqlDbType.Decimal).Value = snapshot.ClutchPoints;
        
        // MVP stats
        cmd.Parameters.AddWithValue("@mvpsEliminations", snapshot.MvpsEliminations);
        cmd.Parameters.AddWithValue("@mvpsBomb", snapshot.MvpsBomb);
        cmd.Parameters.AddWithValue("@mvpsHostage", snapshot.MvpsHostage);
        
        // Hit groups
        cmd.Parameters.AddWithValue("@headshotsHit", snapshot.HeadshotsHit);
        cmd.Parameters.AddWithValue("@chestHits", snapshot.ChestHits);
        cmd.Parameters.AddWithValue("@stomachHits", snapshot.StomachHits);
        cmd.Parameters.AddWithValue("@armHits", snapshot.ArmHits);
        cmd.Parameters.AddWithValue("@legHits", snapshot.LegHits);

        // Advanced metrics with proper precision
        cmd.Parameters.Add("@kdRatio", MySqlDbType.Decimal).Value = snapshot.KDRatio;
        cmd.Parameters.Add("@headshotPercentage", MySqlDbType.Decimal).Value = snapshot.HeadshotPercentage;
        cmd.Parameters.Add("@accuracyPercentage", MySqlDbType.Decimal).Value = snapshot.AccuracyPercentage;
        cmd.Parameters.Add("@kastPercentage", MySqlDbType.Decimal).Value = snapshot.KASTPercentage;
        cmd.Parameters.Add("@averageDamagePerRound", MySqlDbType.Decimal).Value = snapshot.AverageDamagePerRound;
        cmd.Parameters.Add("@hltvRating", MySqlDbType.Decimal).Value = snapshot.HLTVRating;
        cmd.Parameters.Add("@impactRating", MySqlDbType.Decimal).Value = snapshot.ImpactRating;
        cmd.Parameters.Add("@survivalRating", MySqlDbType.Decimal).Value = snapshot.SurvivalRating;
        cmd.Parameters.Add("@utilityScore", MySqlDbType.Decimal).Value = snapshot.UtilityScore;
        cmd.Parameters.AddWithValue("@noscopeKills", snapshot.NoscopeKills);
        cmd.Parameters.AddWithValue("@thruSmokeKills", snapshot.ThruSmokeKills);
        cmd.Parameters.AddWithValue("@attackerBlindKills", snapshot.AttackerBlindKills);
        cmd.Parameters.AddWithValue("@flashAssistedKills", snapshot.FlashAssistedKills);
        cmd.Parameters.AddWithValue("@wallbangKills", snapshot.WallbangKills);
    }

    private static void AddAdvancedParameters(MySqlCommand cmd, PlayerSnapshot snapshot)
    {
        var calculatedAt = DateTime.UtcNow;
        var killsPerRound = snapshot.AverageKillsPerRound;
        var deathsPerRound = snapshot.AverageDeathsPerRound;
        var impactScore = snapshot.ImpactRating;
        var kastPercentage = snapshot.KASTPercentage;
        var averageDamagePerRound = snapshot.AverageDamagePerRound;
        var utilityImpactScore = snapshot.RoundsPlayed > 0
            ? (decimal)snapshot.UtilityDamageDealt / snapshot.RoundsPlayed
            : 0m;
        var clutchSuccessRate = snapshot.ClutchSuccessRate;
        var tradeSuccessRate = snapshot.TradeKillRatio;
        var entrySuccessRate = snapshot.OpeningKillRatio;
        var roundsPlayed = snapshot.RoundsPlayed;
        var kdRatio = snapshot.KDRatio;
        var headshotPercentage = snapshot.HeadshotPercentage;
        var openingKillRatio = snapshot.OpeningKillRatio;
        var tradeKillRatio = snapshot.TradeKillRatio;
        var grenadeEffectivenessRate = snapshot.GrenadeEffectivenessRate;
        var flashEffectivenessRate = snapshot.FlashEffectivenessRate;
        var utilityUsagePerRound = snapshot.UtilityUsagePerRound;
        var averageMoneySpentPerRound = snapshot.AverageMoneySpentPerRound;
        var performanceScore = snapshot.PerformanceScore;
        var topWeaponByKills = snapshot.GetBestWeaponByKills();
        var clutchPoints = snapshot.ClutchPoints;

        cmd.Parameters.AddWithValue("@steamId", snapshot.SteamId);
        cmd.Parameters.AddWithValue("@calculatedAt", calculatedAt);
        cmd.Parameters.AddWithValue("@name", snapshot.Name);
        cmd.Parameters.Add("@rating2", MySqlDbType.Decimal).Value = snapshot.HLTVRating;
        cmd.Parameters.Add("@killsPerRound", MySqlDbType.Decimal).Value = killsPerRound;
        cmd.Parameters.Add("@deathsPerRound", MySqlDbType.Decimal).Value = deathsPerRound;
        cmd.Parameters.Add("@impactScore", MySqlDbType.Decimal).Value = impactScore;
        cmd.Parameters.Add("@kastPercentage", MySqlDbType.Decimal).Value = kastPercentage;
        cmd.Parameters.Add("@averageDamagePerRound", MySqlDbType.Decimal).Value = averageDamagePerRound;
        cmd.Parameters.Add("@utilityImpactScore", MySqlDbType.Decimal).Value = utilityImpactScore;
        cmd.Parameters.Add("@clutchSuccessRate", MySqlDbType.Decimal).Value = clutchSuccessRate;
        cmd.Parameters.Add("@tradeSuccessRate", MySqlDbType.Decimal).Value = tradeSuccessRate;
        cmd.Parameters.Add("@entrySuccessRate", MySqlDbType.Decimal).Value = entrySuccessRate;
        cmd.Parameters.AddWithValue("@roundsPlayed", roundsPlayed);
        cmd.Parameters.Add("@kdRatio", MySqlDbType.Decimal).Value = kdRatio;
        cmd.Parameters.Add("@headshotPercentage", MySqlDbType.Decimal).Value = headshotPercentage;
        cmd.Parameters.Add("@openingKillRatio", MySqlDbType.Decimal).Value = openingKillRatio;
        cmd.Parameters.Add("@tradeKillRatio", MySqlDbType.Decimal).Value = tradeKillRatio;
        cmd.Parameters.Add("@grenadeEffectivenessRate", MySqlDbType.Decimal).Value = grenadeEffectivenessRate;
        cmd.Parameters.Add("@flashEffectivenessRate", MySqlDbType.Decimal).Value = flashEffectivenessRate;
        cmd.Parameters.Add("@utilityUsagePerRound", MySqlDbType.Decimal).Value = utilityUsagePerRound;
        cmd.Parameters.Add("@averageMoneySpentPerRound", MySqlDbType.Decimal).Value = averageMoneySpentPerRound;
        cmd.Parameters.Add("@performanceScore", MySqlDbType.Decimal).Value = performanceScore;
        cmd.Parameters.AddWithValue("@topWeaponByKills", topWeaponByKills);
        cmd.Parameters.Add("@survivalRating", MySqlDbType.Decimal).Value = snapshot.SurvivalRating;
        cmd.Parameters.Add("@utilityScore", MySqlDbType.Decimal).Value = snapshot.UtilityScore;
        cmd.Parameters.Add("@clutchPoints", MySqlDbType.Decimal).Value = clutchPoints;
        cmd.Parameters.AddWithValue("@flashAssistedKills", snapshot.FlashAssistedKills);
        cmd.Parameters.AddWithValue("@wallbangKills", snapshot.WallbangKills);
    }
}
