-- player_advanced_analytics had two UNIQUE keys (idempotency_key, and steam_id + calculated_at): INSERT ... ON DUPLICATE
-- KEY UPDATE matched either, so two rows of one player written in the same second merged into one. idempotency_key (match
-- and player) is the row's one identity; the per-player lookup keeps a plain index, which the steam_id foreign key needs.
-- Also widens the lifetime ratios DECIMAL(5,3) capped at 99.999 without an error, and the blind-time totals that stayed INT
-- (whole seconds) on databases created in the first half of 2026. Safe to run again.
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'player_advanced_analytics' AND INDEX_NAME = 'idx_player_analytics_steam_calculated'),
    'DO 0', 'ALTER TABLE player_advanced_analytics ADD INDEX idx_player_analytics_steam_calculated (steam_id, calculated_at)');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'player_advanced_analytics' AND INDEX_NAME = 'uq_player_analytics_steam_calculated'),
    'ALTER TABLE player_advanced_analytics DROP INDEX uq_player_analytics_steam_calculated', 'DO 0');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

ALTER TABLE player_stats
    MODIFY COLUMN total_blind_time DECIMAL(10,2) DEFAULT 0,
    MODIFY COLUMN total_blind_time_inflicted DECIMAL(10,2) DEFAULT 0,
    MODIFY COLUMN kd_ratio DECIMAL(8,3) DEFAULT 0,
    MODIFY COLUMN impact_rating DECIMAL(8,3) DEFAULT 0,
    MODIFY COLUMN survival_rating DECIMAL(8,3) DEFAULT 0,
    MODIFY COLUMN utility_score DECIMAL(8,3) DEFAULT 0;
