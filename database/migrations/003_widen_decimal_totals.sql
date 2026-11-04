-- Lifetime totals that a narrower column would cap without an error: clutch_points stayed DECIMAL(5,3) on
-- databases created before 2026-07-18, and round_swing is a running sum. Safe to run again.
ALTER TABLE player_stats
    MODIFY COLUMN clutch_points DECIMAL(10,3) DEFAULT 0,
    MODIFY COLUMN round_swing DECIMAL(10,3) DEFAULT 0;

ALTER TABLE player_advanced_analytics
    MODIFY COLUMN clutch_points DECIMAL(10,3) NOT NULL;
