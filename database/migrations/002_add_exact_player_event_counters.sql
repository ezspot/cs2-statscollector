ALTER TABLE player_stats
    ADD COLUMN kast_rounds INT NOT NULL DEFAULT 0 AFTER rounds_won,
    ADD COLUMN bomb_explosions INT NOT NULL DEFAULT 0 AFTER bomb_defuses,
    ADD COLUMN multi_kill_2 INT NOT NULL DEFAULT 0 AFTER multi_kills,
    ADD COLUMN multi_kill_3 INT NOT NULL DEFAULT 0 AFTER multi_kill_2,
    ADD COLUMN multi_kill_4 INT NOT NULL DEFAULT 0 AFTER multi_kill_3,
    ADD COLUMN multi_kill_5 INT NOT NULL DEFAULT 0 AFTER multi_kill_4,
    ADD COLUMN clutch_kills INT NOT NULL DEFAULT 0 AFTER clutches_lost;

UPDATE player_stats
SET kast_rounds = ROUND(kast_percentage / 100 * rounds_played)
WHERE kast_rounds = 0 AND kast_percentage > 0 AND rounds_played > 0;
