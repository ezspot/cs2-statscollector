ALTER TABLE rounds
    ADD COLUMN official_end_time TIMESTAMP NULL AFTER end_time;

ALTER TABLE utility_positions
    ADD COLUMN entity_id INT NOT NULL DEFAULT 0 AFTER utility_type,
    ADD COLUMN lifecycle_start_event VARCHAR(32) NOT NULL DEFAULT 'legacy' AFTER entity_id,
    ADD COLUMN lifecycle_end_event VARCHAR(32) NOT NULL DEFAULT 'legacy' AFTER lifecycle_start_event,
    ADD COLUMN lifecycle_duration_ms INT UNSIGNED NOT NULL DEFAULT 0 AFTER lifecycle_end_event;
