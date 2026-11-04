-- Databases created before 2026-07-18 have no match_id foreign key on the position tables, and so no index for
-- removing a restored round's rows (without one that delete scans and locks the whole table). Adds the index only
-- where none exists; safe to run again.
SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'kill_positions' AND COLUMN_NAME = 'match_id' AND SEQ_IN_INDEX = 1),
    'DO 0', 'ALTER TABLE kill_positions ADD INDEX match_id (match_id)');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'death_positions' AND COLUMN_NAME = 'match_id' AND SEQ_IN_INDEX = 1),
    'DO 0', 'ALTER TABLE death_positions ADD INDEX match_id (match_id)');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;

SET @sql = IF(EXISTS(SELECT 1 FROM information_schema.STATISTICS WHERE TABLE_SCHEMA = DATABASE()
    AND TABLE_NAME = 'utility_positions' AND COLUMN_NAME = 'match_id' AND SEQ_IN_INDEX = 1),
    'DO 0', 'ALTER TABLE utility_positions ADD INDEX match_id (match_id)');
PREPARE stmt FROM @sql; EXECUTE stmt; DEALLOCATE PREPARE stmt;
