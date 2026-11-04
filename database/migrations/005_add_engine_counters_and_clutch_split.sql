-- Team kills, suicides and 1v1 to 1v5 clutch attempts/wins, and the engine's own per-match kill reward, cash earned,
-- money saved and time alive (CSMatchStats_t, read at round end). Not idempotent (a re-run fails with error 1060);
-- needs round_swing to exist.
ALTER TABLE player_stats
    ADD COLUMN team_kills INT NOT NULL DEFAULT 0 AFTER round_swing,
    ADD COLUMN suicides INT NOT NULL DEFAULT 0 AFTER team_kills,
    ADD COLUMN clutch_1v1_attempts INT NOT NULL DEFAULT 0 AFTER suicides,
    ADD COLUMN clutch_1v1_wins INT NOT NULL DEFAULT 0 AFTER clutch_1v1_attempts,
    ADD COLUMN clutch_1v2_attempts INT NOT NULL DEFAULT 0 AFTER clutch_1v1_wins,
    ADD COLUMN clutch_1v2_wins INT NOT NULL DEFAULT 0 AFTER clutch_1v2_attempts,
    ADD COLUMN clutch_1v3_attempts INT NOT NULL DEFAULT 0 AFTER clutch_1v2_wins,
    ADD COLUMN clutch_1v3_wins INT NOT NULL DEFAULT 0 AFTER clutch_1v3_attempts,
    ADD COLUMN clutch_1v4_attempts INT NOT NULL DEFAULT 0 AFTER clutch_1v3_wins,
    ADD COLUMN clutch_1v4_wins INT NOT NULL DEFAULT 0 AFTER clutch_1v4_attempts,
    ADD COLUMN clutch_1v5_attempts INT NOT NULL DEFAULT 0 AFTER clutch_1v4_wins,
    ADD COLUMN clutch_1v5_wins INT NOT NULL DEFAULT 0 AFTER clutch_1v5_attempts,
    ADD COLUMN kill_reward INT NOT NULL DEFAULT 0 AFTER clutch_1v5_wins,
    ADD COLUMN cash_earned INT NOT NULL DEFAULT 0 AFTER kill_reward,
    ADD COLUMN money_saved INT NOT NULL DEFAULT 0 AFTER cash_earned,
    ADD COLUMN live_time INT NOT NULL DEFAULT 0 AFTER money_saved;
