-- Per-round player rows (player_round_stats); the side split, KAST parts, pistol rounds, team damage and kill distances on
-- player_stats; the whole match on match_player_stats; headshots, damage and deaths per weapon; the round's buy and bomb on
-- rounds. Drops player_stats.defuser_drops and effective_smokes, which nothing ever incremented; only this file drops them,
-- AutoCreateSchema leaves them in place. Every statement checks information_schema first, so the file can be run again and
-- a run that failed midway resumes where it stopped. Needs migration 005.

SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'player_stats' AND COLUMN_NAME = 'defuser_drops'),
    'ALTER TABLE player_stats DROP COLUMN defuser_drops', 'DO 0');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'player_stats' AND COLUMN_NAME = 'effective_smokes'),
    'ALTER TABLE player_stats DROP COLUMN effective_smokes', 'DO 0');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'player_stats' AND COLUMN_NAME = 'team_damage'),
    'DO 0', 'ALTER TABLE player_stats ADD COLUMN team_damage INT NOT NULL DEFAULT 0 AFTER live_time');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'player_stats' AND COLUMN_NAME = 'multi_kill_1'),
    'DO 0', 'ALTER TABLE player_stats ADD COLUMN multi_kill_1 INT NOT NULL DEFAULT 0 AFTER team_damage');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'player_stats' AND COLUMN_NAME = 'ct_kills'),
    'DO 0', 'ALTER TABLE player_stats ADD COLUMN ct_kills INT NOT NULL DEFAULT 0 AFTER multi_kill_1');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'player_stats' AND COLUMN_NAME = 't_kills'),
    'DO 0', 'ALTER TABLE player_stats ADD COLUMN t_kills INT NOT NULL DEFAULT 0 AFTER ct_kills');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'player_stats' AND COLUMN_NAME = 'ct_deaths'),
    'DO 0', 'ALTER TABLE player_stats ADD COLUMN ct_deaths INT NOT NULL DEFAULT 0 AFTER t_kills');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'player_stats' AND COLUMN_NAME = 't_deaths'),
    'DO 0', 'ALTER TABLE player_stats ADD COLUMN t_deaths INT NOT NULL DEFAULT 0 AFTER ct_deaths');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'player_stats' AND COLUMN_NAME = 'ct_damage'),
    'DO 0', 'ALTER TABLE player_stats ADD COLUMN ct_damage INT NOT NULL DEFAULT 0 AFTER t_deaths');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'player_stats' AND COLUMN_NAME = 't_damage'),
    'DO 0', 'ALTER TABLE player_stats ADD COLUMN t_damage INT NOT NULL DEFAULT 0 AFTER ct_damage');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'player_stats' AND COLUMN_NAME = 'ct_first_kills'),
    'DO 0', 'ALTER TABLE player_stats ADD COLUMN ct_first_kills INT NOT NULL DEFAULT 0 AFTER t_damage');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'player_stats' AND COLUMN_NAME = 't_first_kills'),
    'DO 0', 'ALTER TABLE player_stats ADD COLUMN t_first_kills INT NOT NULL DEFAULT 0 AFTER ct_first_kills');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'player_stats' AND COLUMN_NAME = 'ct_first_deaths'),
    'DO 0', 'ALTER TABLE player_stats ADD COLUMN ct_first_deaths INT NOT NULL DEFAULT 0 AFTER t_first_kills');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'player_stats' AND COLUMN_NAME = 't_first_deaths'),
    'DO 0', 'ALTER TABLE player_stats ADD COLUMN t_first_deaths INT NOT NULL DEFAULT 0 AFTER ct_first_deaths');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'player_stats' AND COLUMN_NAME = 'rounds_survived'),
    'DO 0', 'ALTER TABLE player_stats ADD COLUMN rounds_survived INT NOT NULL DEFAULT 0 AFTER t_first_deaths');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'player_stats' AND COLUMN_NAME = 'rounds_traded'),
    'DO 0', 'ALTER TABLE player_stats ADD COLUMN rounds_traded INT NOT NULL DEFAULT 0 AFTER rounds_survived');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'player_stats' AND COLUMN_NAME = 'ct_rounds_won'),
    'DO 0', 'ALTER TABLE player_stats ADD COLUMN ct_rounds_won INT NOT NULL DEFAULT 0 AFTER rounds_traded');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'player_stats' AND COLUMN_NAME = 't_rounds_won'),
    'DO 0', 'ALTER TABLE player_stats ADD COLUMN t_rounds_won INT NOT NULL DEFAULT 0 AFTER ct_rounds_won');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'player_stats' AND COLUMN_NAME = 'pistol_rounds_played'),
    'DO 0', 'ALTER TABLE player_stats ADD COLUMN pistol_rounds_played INT NOT NULL DEFAULT 0 AFTER t_rounds_won');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'player_stats' AND COLUMN_NAME = 'pistol_rounds_won'),
    'DO 0', 'ALTER TABLE player_stats ADD COLUMN pistol_rounds_won INT NOT NULL DEFAULT 0 AFTER pistol_rounds_played');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'player_stats' AND COLUMN_NAME = 'pistol_round_kills'),
    'DO 0', 'ALTER TABLE player_stats ADD COLUMN pistol_round_kills INT NOT NULL DEFAULT 0 AFTER pistol_rounds_won');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'player_stats' AND COLUMN_NAME = 'kill_distance_total'),
    'DO 0', 'ALTER TABLE player_stats ADD COLUMN kill_distance_total DECIMAL(12,2) NOT NULL DEFAULT 0 AFTER pistol_round_kills');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'player_stats' AND COLUMN_NAME = 'longest_kill'),
    'DO 0', 'ALTER TABLE player_stats ADD COLUMN longest_kill DECIMAL(8,2) NOT NULL DEFAULT 0 AFTER kill_distance_total');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'rounds_played'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN rounds_played INT DEFAULT 0 AFTER entry_kill_attempt_wins');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'rounds_won'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN rounds_won INT DEFAULT 0 AFTER rounds_played');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'ct_rounds'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN ct_rounds INT DEFAULT 0 AFTER rounds_won');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 't_rounds'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN t_rounds INT DEFAULT 0 AFTER ct_rounds');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'ct_rounds_won'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN ct_rounds_won INT DEFAULT 0 AFTER t_rounds');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 't_rounds_won'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN t_rounds_won INT DEFAULT 0 AFTER ct_rounds_won');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'kast_rounds'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN kast_rounds INT DEFAULT 0 AFTER t_rounds_won');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'rounds_survived'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN rounds_survived INT DEFAULT 0 AFTER kast_rounds');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'rounds_traded'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN rounds_traded INT DEFAULT 0 AFTER rounds_survived');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'damage_taken'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN damage_taken INT DEFAULT 0 AFTER rounds_traded');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'team_damage'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN team_damage INT DEFAULT 0 AFTER damage_taken');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'team_kills'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN team_kills INT DEFAULT 0 AFTER team_damage');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'suicides'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN suicides INT DEFAULT 0 AFTER team_kills');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'utility_damage'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN utility_damage INT DEFAULT 0 AFTER suicides');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'enemies_flashed'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN enemies_flashed INT DEFAULT 0 AFTER utility_damage');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'flash_assists'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN flash_assists INT DEFAULT 0 AFTER enemies_flashed');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'flashes_thrown'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN flashes_thrown INT DEFAULT 0 AFTER flash_assists');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'smokes_thrown'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN smokes_thrown INT DEFAULT 0 AFTER flashes_thrown');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'he_grenades_thrown'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN he_grenades_thrown INT DEFAULT 0 AFTER smokes_thrown');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'molotovs_thrown'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN molotovs_thrown INT DEFAULT 0 AFTER he_grenades_thrown');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'trade_kills'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN trade_kills INT DEFAULT 0 AFTER molotovs_thrown');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'traded_deaths'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN traded_deaths INT DEFAULT 0 AFTER trade_kills');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'first_kills'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN first_kills INT DEFAULT 0 AFTER traded_deaths');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'first_deaths'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN first_deaths INT DEFAULT 0 AFTER first_kills');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'multi_kill_1'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN multi_kill_1 INT DEFAULT 0 AFTER first_deaths');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'multi_kill_2'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN multi_kill_2 INT DEFAULT 0 AFTER multi_kill_1');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'multi_kill_3'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN multi_kill_3 INT DEFAULT 0 AFTER multi_kill_2');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'multi_kill_4'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN multi_kill_4 INT DEFAULT 0 AFTER multi_kill_3');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'multi_kill_5'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN multi_kill_5 INT DEFAULT 0 AFTER multi_kill_4');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'clutches_won'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN clutches_won INT DEFAULT 0 AFTER multi_kill_5');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'clutches_lost'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN clutches_lost INT DEFAULT 0 AFTER clutches_won');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'clutch_1v1_attempts'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN clutch_1v1_attempts INT DEFAULT 0 AFTER clutches_lost');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'clutch_1v1_wins'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN clutch_1v1_wins INT DEFAULT 0 AFTER clutch_1v1_attempts');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'clutch_1v2_attempts'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN clutch_1v2_attempts INT DEFAULT 0 AFTER clutch_1v1_wins');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'clutch_1v2_wins'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN clutch_1v2_wins INT DEFAULT 0 AFTER clutch_1v2_attempts');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'clutch_1v3_attempts'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN clutch_1v3_attempts INT DEFAULT 0 AFTER clutch_1v2_wins');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'clutch_1v3_wins'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN clutch_1v3_wins INT DEFAULT 0 AFTER clutch_1v3_attempts');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'clutch_1v4_attempts'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN clutch_1v4_attempts INT DEFAULT 0 AFTER clutch_1v3_wins');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'clutch_1v4_wins'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN clutch_1v4_wins INT DEFAULT 0 AFTER clutch_1v4_attempts');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'clutch_1v5_attempts'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN clutch_1v5_attempts INT DEFAULT 0 AFTER clutch_1v4_wins');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'clutch_1v5_wins'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN clutch_1v5_wins INT DEFAULT 0 AFTER clutch_1v5_attempts');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'bomb_plants'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN bomb_plants INT DEFAULT 0 AFTER clutch_1v5_wins');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'bomb_defuses'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN bomb_defuses INT DEFAULT 0 AFTER bomb_plants');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'shots_fired'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN shots_fired INT DEFAULT 0 AFTER bomb_defuses');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'shots_hit'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN shots_hit INT DEFAULT 0 AFTER shots_fired');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'wallbang_kills'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN wallbang_kills INT DEFAULT 0 AFTER shots_hit');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'noscope_kills'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN noscope_kills INT DEFAULT 0 AFTER wallbang_kills');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'thru_smoke_kills'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN thru_smoke_kills INT DEFAULT 0 AFTER noscope_kills');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'attacker_blind_kills'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN attacker_blind_kills INT DEFAULT 0 AFTER thru_smoke_kills');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'money_spent'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN money_spent INT DEFAULT 0 AFTER attacker_blind_kills');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'kill_reward'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN kill_reward INT DEFAULT 0 AFTER money_spent');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'cash_earned'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN cash_earned INT DEFAULT 0 AFTER kill_reward');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'ct_kills'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN ct_kills INT DEFAULT 0 AFTER cash_earned');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 't_kills'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN t_kills INT DEFAULT 0 AFTER ct_kills');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'ct_deaths'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN ct_deaths INT DEFAULT 0 AFTER t_kills');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 't_deaths'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN t_deaths INT DEFAULT 0 AFTER ct_deaths');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'ct_damage'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN ct_damage INT DEFAULT 0 AFTER t_deaths');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 't_damage'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN t_damage INT DEFAULT 0 AFTER ct_damage');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'pistol_rounds_played'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN pistol_rounds_played INT DEFAULT 0 AFTER t_damage');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'pistol_rounds_won'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN pistol_rounds_won INT DEFAULT 0 AFTER pistol_rounds_played');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'pistol_round_kills'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN pistol_round_kills INT DEFAULT 0 AFTER pistol_rounds_won');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'kill_distance_total'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN kill_distance_total DECIMAL(12,2) DEFAULT 0 AFTER pistol_round_kills');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_player_stats' AND COLUMN_NAME = 'round_swing'),
    'DO 0', 'ALTER TABLE match_player_stats ADD COLUMN round_swing DECIMAL(10,3) DEFAULT 0 AFTER kill_distance_total');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_weapon_stats' AND COLUMN_NAME = 'headshots'),
    'DO 0', 'ALTER TABLE match_weapon_stats ADD COLUMN headshots INT DEFAULT 0 AFTER kills');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_weapon_stats' AND COLUMN_NAME = 'damage'),
    'DO 0', 'ALTER TABLE match_weapon_stats ADD COLUMN damage INT DEFAULT 0 AFTER hits');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'match_weapon_stats' AND COLUMN_NAME = 'deaths'),
    'DO 0', 'ALTER TABLE match_weapon_stats ADD COLUMN deaths INT DEFAULT 0 AFTER damage');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'rounds' AND COLUMN_NAME = 'ct_start_money'),
    'DO 0', 'ALTER TABLE rounds ADD COLUMN ct_start_money INT NULL AFTER win_type');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'rounds' AND COLUMN_NAME = 't_start_money'),
    'DO 0', 'ALTER TABLE rounds ADD COLUMN t_start_money INT NULL AFTER ct_start_money');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'rounds' AND COLUMN_NAME = 'ct_equipment_value'),
    'DO 0', 'ALTER TABLE rounds ADD COLUMN ct_equipment_value INT NULL AFTER t_start_money');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'rounds' AND COLUMN_NAME = 't_equipment_value'),
    'DO 0', 'ALTER TABLE rounds ADD COLUMN t_equipment_value INT NULL AFTER ct_equipment_value');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'rounds' AND COLUMN_NAME = 'bomb_site'),
    'DO 0', 'ALTER TABLE rounds ADD COLUMN bomb_site CHAR(1) NULL AFTER t_equipment_value');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'rounds' AND COLUMN_NAME = 'bomb_plant_time'),
    'DO 0', 'ALTER TABLE rounds ADD COLUMN bomb_plant_time DECIMAL(6,1) NULL AFTER bomb_site');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'rounds' AND COLUMN_NAME = 'defuse_time_left'),
    'DO 0', 'ALTER TABLE rounds ADD COLUMN defuse_time_left DECIMAL(5,2) NULL AFTER bomb_plant_time');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

CREATE TABLE IF NOT EXISTS player_round_stats (
    id INT AUTO_INCREMENT PRIMARY KEY,
    match_id INT NOT NULL,
    round_number INT NOT NULL,
    steam_id BIGINT NOT NULL,
    team TINYINT NOT NULL, -- 2: T, 3: CT
    won TINYINT NOT NULL DEFAULT 0,
    pistol_round TINYINT NOT NULL DEFAULT 0,
    kills INT NOT NULL DEFAULT 0,
    deaths INT NOT NULL DEFAULT 0,
    assists INT NOT NULL DEFAULT 0,
    headshots INT NOT NULL DEFAULT 0,
    damage_dealt INT NOT NULL DEFAULT 0,
    damage_taken INT NOT NULL DEFAULT 0,
    utility_damage INT NOT NULL DEFAULT 0,
    enemies_flashed INT NOT NULL DEFAULT 0,
    flash_assists INT NOT NULL DEFAULT 0,
    team_damage INT NOT NULL DEFAULT 0,
    team_kills INT NOT NULL DEFAULT 0,
    first_kill TINYINT NOT NULL DEFAULT 0,
    first_death TINYINT NOT NULL DEFAULT 0,
    trade_kills INT NOT NULL DEFAULT 0,
    traded_death TINYINT NOT NULL DEFAULT 0,
    survived TINYINT NOT NULL DEFAULT 0,
    kast TINYINT NOT NULL DEFAULT 0,
    clutch_opponents TINYINT NOT NULL DEFAULT 0,
    clutch_won TINYINT NOT NULL DEFAULT 0,
    bomb_plants INT NOT NULL DEFAULT 0,
    bomb_defuses INT NOT NULL DEFAULT 0,
    mvp TINYINT NOT NULL DEFAULT 0,
    score INT NOT NULL DEFAULT 0,
    start_money INT NOT NULL DEFAULT 0,
    end_money INT NOT NULL DEFAULT 0,
    equipment_value INT NOT NULL DEFAULT 0,
    money_spent INT NOT NULL DEFAULT 0,
    kill_reward INT NOT NULL DEFAULT 0,
    cash_earned INT NOT NULL DEFAULT 0,
    money_saved INT NOT NULL DEFAULT 0,
    live_time INT NOT NULL DEFAULT 0,
    round_swing DECIMAL(10,3) NOT NULL DEFAULT 0,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (match_id) REFERENCES matches(id) ON DELETE CASCADE,
    FOREIGN KEY (steam_id) REFERENCES players(steam_id) ON DELETE CASCADE,
    UNIQUE KEY uq_player_round (match_id, round_number, steam_id)
) ENGINE=InnoDB;
