-- statsCollector Database Schema
-- Creator: Anders Giske Hagen
-- GitHub: https://github.com/ezspot/cs2-statscollector

CREATE DATABASE IF NOT EXISTS `cs2_statscollector`
    CHARACTER SET utf8mb4
    COLLATE utf8mb4_unicode_ci;

USE `cs2_statscollector`;

-- 1. Base Tables (No dependencies)
CREATE TABLE IF NOT EXISTS players (
    id INT AUTO_INCREMENT PRIMARY KEY,
    steam_id BIGINT UNIQUE NOT NULL,
    name VARCHAR(255),
    first_seen TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    last_seen TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    INDEX idx_players_steam_id (steam_id)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS matches (
    id INT AUTO_INCREMENT PRIMARY KEY,
    match_uuid VARCHAR(36) UNIQUE NOT NULL,
    map_name VARCHAR(100) NOT NULL,
    start_time TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    end_time TIMESTAMP NULL,
    status ENUM('IN_PROGRESS', 'COMPLETED', 'ABORTED') DEFAULT 'IN_PROGRESS',
    INDEX idx_matches_time (start_time)
) ENGINE=InnoDB;

-- 2. Tables with dependencies
CREATE TABLE IF NOT EXISTS player_stats (
    steam_id BIGINT PRIMARY KEY,
    name VARCHAR(255),
    kills INT DEFAULT 0,
    deaths INT DEFAULT 0,
    assists INT DEFAULT 0,
    headshots INT DEFAULT 0,
    damage_dealt INT DEFAULT 0,
    damage_taken INT DEFAULT 0,
    damage_armor INT DEFAULT 0,
    shots_fired INT DEFAULT 0,
    shots_hit INT DEFAULT 0,
    mvps INT DEFAULT 0,
    score INT DEFAULT 0,
    rounds_played INT DEFAULT 0,
    rounds_won INT DEFAULT 0,
    total_spawns INT DEFAULT 0,
    playtime_seconds INT DEFAULT 0,
    ct_rounds INT DEFAULT 0,
    t_rounds INT DEFAULT 0,
    grenades_thrown INT DEFAULT 0,
    flashes_thrown INT DEFAULT 0,
    smokes_thrown INT DEFAULT 0,
    molotovs_thrown INT DEFAULT 0,
    he_grenades_thrown INT DEFAULT 0,
    decoys_thrown INT DEFAULT 0,
    tactical_grenades_thrown INT DEFAULT 0,
    players_blinded INT DEFAULT 0,
    times_blinded INT DEFAULT 0,
    flash_assists INT DEFAULT 0,
    total_blind_time INT DEFAULT 0,
    total_blind_time_inflicted INT DEFAULT 0,
    utility_damage_dealt INT DEFAULT 0,
    utility_damage_taken INT DEFAULT 0,
    bomb_plants INT DEFAULT 0,
    bomb_defuses INT DEFAULT 0,
    bomb_plant_attempts INT DEFAULT 0,
    bomb_plant_aborts INT DEFAULT 0,
    bomb_defuse_attempts INT DEFAULT 0,
    bomb_defuse_aborts INT DEFAULT 0,
    bomb_defuse_with_kit INT DEFAULT 0,
    bomb_defuse_without_kit INT DEFAULT 0,
    bomb_drops INT DEFAULT 0,
    bomb_pickups INT DEFAULT 0,
    defuser_drops INT DEFAULT 0,
    defuser_pickups INT DEFAULT 0,
    clutch_defuses INT DEFAULT 0,
    total_plant_time INT DEFAULT 0,
    total_defuse_time INT DEFAULT 0,
    bomb_kills INT DEFAULT 0,
    bomb_deaths INT DEFAULT 0,
    hostages_rescued INT DEFAULT 0,
    jumps INT DEFAULT 0,
    money_spent INT DEFAULT 0,
    equipment_value INT DEFAULT 0,
    items_purchased INT DEFAULT 0,
    items_picked_up INT DEFAULT 0,
    items_dropped INT DEFAULT 0,
    cash_earned INT DEFAULT 0,
    cash_spent INT DEFAULT 0,
    enemies_flashed INT DEFAULT 0,
    teammates_flashed INT DEFAULT 0,
    effective_flashes INT DEFAULT 0,
    effective_smokes INT DEFAULT 0,
    effective_he_grenades INT DEFAULT 0,
    effective_molotovs INT DEFAULT 0,
    flash_waste INT DEFAULT 0,
    multi_kill_nades INT DEFAULT 0,
    nade_kills INT DEFAULT 0,
    entry_kills INT DEFAULT 0,
    trade_kills INT DEFAULT 0,
    traded_deaths INT DEFAULT 0,
    high_impact_kills INT DEFAULT 0,
    low_impact_kills INT DEFAULT 0,
    trade_opportunities INT DEFAULT 0,
    trade_windows_missed INT DEFAULT 0,
    multi_kills INT DEFAULT 0,
    opening_duels_won INT DEFAULT 0,
    opening_duels_lost INT DEFAULT 0,
    noscope_kills INT DEFAULT 0,
    thru_smoke_kills INT DEFAULT 0,
    attacker_blind_kills INT DEFAULT 0,
    flash_assisted_kills INT DEFAULT 0,
    wallbang_kills INT DEFAULT 0,
    revenges INT DEFAULT 0,
    clutches_won INT DEFAULT 0,
    clutches_lost INT DEFAULT 0,
    clutch_points DECIMAL(5,3) DEFAULT 0,
    mvps_eliminations INT DEFAULT 0,
    mvps_bomb INT DEFAULT 0,
    mvps_hostage INT DEFAULT 0,
    headshots_hit INT DEFAULT 0,
    chest_hits INT DEFAULT 0,
    stomach_hits INT DEFAULT 0,
    arm_hits INT DEFAULT 0,
    leg_hits INT DEFAULT 0,
    kd_ratio DECIMAL(5,3) DEFAULT 0,
    headshot_percentage DECIMAL(5,2) DEFAULT 0,
    accuracy_percentage DECIMAL(5,2) DEFAULT 0,
    kast_percentage DECIMAL(5,2) DEFAULT 0,
    average_damage_per_round DECIMAL(6,2) DEFAULT 0,
    hltv_rating DECIMAL(5,3) DEFAULT 0,
    impact_rating DECIMAL(5,3) DEFAULT 0,
    survival_rating DECIMAL(5,3) DEFAULT 0,
    utility_score DECIMAL(5,3) DEFAULT 0,
    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
    FOREIGN KEY (steam_id) REFERENCES players(steam_id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE INDEX idx_player_stats_updated_at ON player_stats (updated_at);
CREATE INDEX idx_player_stats_hltv_rating ON player_stats (hltv_rating DESC);

CREATE TABLE IF NOT EXISTS rounds (
    id INT AUTO_INCREMENT PRIMARY KEY,
    match_id INT NOT NULL,
    round_number INT NOT NULL,
    start_time TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    end_time TIMESTAMP NULL,
    winner_team INT, -- 2: T, 3: CT
    win_type INT, -- Reason for win
    FOREIGN KEY (match_id) REFERENCES matches(id) ON DELETE CASCADE,
    UNIQUE KEY uq_match_round (match_id, round_number)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS match_player_stats (
    id INT AUTO_INCREMENT PRIMARY KEY,
    match_id INT NOT NULL,
    steam_id BIGINT NOT NULL,
    kills INT DEFAULT 0,
    deaths INT DEFAULT 0,
    assists INT DEFAULT 0,
    headshots INT DEFAULT 0,
    damage_dealt INT DEFAULT 0,
    mvps INT DEFAULT 0,
    score INT DEFAULT 0,
    rating2 DECIMAL(5,3) DEFAULT 0,
    adr DECIMAL(6,2) DEFAULT 0,
    kast DECIMAL(5,2) DEFAULT 0,
    FOREIGN KEY (match_id) REFERENCES matches(id) ON DELETE CASCADE,
    FOREIGN KEY (steam_id) REFERENCES players(steam_id) ON DELETE CASCADE,
    INDEX idx_match_player (match_id, steam_id)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS match_weapon_stats (
    id INT AUTO_INCREMENT PRIMARY KEY,
    match_id INT NOT NULL,
    steam_id BIGINT NOT NULL,
    weapon_name VARCHAR(100) NOT NULL,
    kills INT DEFAULT 0,
    shots INT DEFAULT 0,
    hits INT DEFAULT 0,
    FOREIGN KEY (match_id) REFERENCES matches(id) ON DELETE CASCADE,
    FOREIGN KEY (steam_id) REFERENCES players(steam_id) ON DELETE CASCADE,
    INDEX idx_match_weapon (match_id, steam_id, weapon_name)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS weapon_stats (
    steam_id BIGINT NOT NULL,
    weapon_name VARCHAR(100) NOT NULL,
    kills INT DEFAULT 0,
    deaths INT DEFAULT 0,
    shots INT DEFAULT 0,
    hits INT DEFAULT 0,
    headshots INT DEFAULT 0,
    PRIMARY KEY (steam_id, weapon_name),
    FOREIGN KEY (steam_id) REFERENCES players(steam_id) ON DELETE CASCADE
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS player_advanced_analytics (
    id INT AUTO_INCREMENT PRIMARY KEY,
    match_id INT,
    steam_id BIGINT NOT NULL,
    calculated_at TIMESTAMP NOT NULL,
    name VARCHAR(255) NOT NULL,
    rating2 DECIMAL(5,3) NOT NULL,
    kills_per_round DECIMAL(5,3) NOT NULL,
    deaths_per_round DECIMAL(5,3) NOT NULL,
    impact_score DECIMAL(5,3) NOT NULL,
    kast_percentage DECIMAL(5,2) NOT NULL,
    average_damage_per_round DECIMAL(6,2) NOT NULL,
    utility_impact_score DECIMAL(5,3) NOT NULL,
    clutch_success_rate DECIMAL(5,2) NOT NULL,
    trade_success_rate DECIMAL(5,2) NOT NULL,
    trade_windows_missed INT DEFAULT 0,
    flash_waste INT DEFAULT 0,
    entry_success_rate DECIMAL(5,2) NOT NULL,
    rounds_played INT NOT NULL,
    kd_ratio DECIMAL(5,3) NOT NULL,
    headshot_percentage DECIMAL(5,2) NOT NULL,
    opening_kill_ratio DECIMAL(5,2) NOT NULL,
    trade_kill_ratio DECIMAL(5,2) NOT NULL,
    grenade_effectiveness_rate DECIMAL(5,2) NOT NULL,
    flash_effectiveness_rate DECIMAL(5,2) NOT NULL,
    utility_usage_per_round DECIMAL(5,3) NOT NULL,
    average_money_spent_per_round DECIMAL(8,2) NOT NULL,
    performance_score DECIMAL(5,2) NOT NULL,
    top_weapon_by_kills VARCHAR(100) NOT NULL,
    survival_rating DECIMAL(5,3) NOT NULL,
    utility_score DECIMAL(5,3) NOT NULL,
    clutch_points DECIMAL(5,3) NOT NULL,
    flash_assisted_kills INT DEFAULT 0,
    wallbang_kills INT DEFAULT 0,
    FOREIGN KEY (steam_id) REFERENCES players(steam_id) ON DELETE CASCADE,
    UNIQUE KEY uq_player_analytics_steam_calculated (steam_id, calculated_at),
    INDEX idx_player_advanced_analytics_calculated_at (calculated_at)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS kill_positions (
    id INT AUTO_INCREMENT PRIMARY KEY,
    match_id INT,
    killer_steam_id BIGINT NOT NULL,
    victim_steam_id BIGINT NOT NULL,
    killer_x FLOAT NOT NULL,
    killer_y FLOAT NOT NULL,
    killer_z FLOAT NOT NULL,
    victim_x FLOAT NOT NULL,
    victim_y FLOAT NOT NULL,
    victim_z FLOAT NOT NULL,
    weapon_used VARCHAR(100),
    is_headshot BOOLEAN DEFAULT FALSE,
    is_wallbang BOOLEAN DEFAULT FALSE,
    distance FLOAT DEFAULT 0,
    killer_team INT NOT NULL,
    victim_team INT NOT NULL,
    map_name VARCHAR(100) NOT NULL,
    round_number INT NOT NULL,
    round_time_seconds INT NOT NULL,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (killer_steam_id) REFERENCES players(steam_id) ON DELETE CASCADE,
    FOREIGN KEY (victim_steam_id) REFERENCES players(steam_id) ON DELETE CASCADE,
    INDEX idx_kill_pos_map (map_name)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS death_positions (
    id INT AUTO_INCREMENT PRIMARY KEY,
    match_id INT,
    steam_id BIGINT NOT NULL,
    x FLOAT NOT NULL,
    y FLOAT NOT NULL,
    z FLOAT NOT NULL,
    cause_of_death VARCHAR(100),
    is_headshot BOOLEAN DEFAULT FALSE,
    team INT NOT NULL,
    map_name VARCHAR(100) NOT NULL,
    round_number INT NOT NULL,
    round_time_seconds INT NOT NULL,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (steam_id) REFERENCES players(steam_id) ON DELETE CASCADE,
    INDEX idx_death_pos_map (map_name)
) ENGINE=InnoDB;

CREATE TABLE IF NOT EXISTS utility_positions (
    id INT AUTO_INCREMENT PRIMARY KEY,
    match_id INT,
    steam_id BIGINT NOT NULL,
    throw_x FLOAT NOT NULL,
    throw_y FLOAT NOT NULL,
    throw_z FLOAT NOT NULL,
    land_x FLOAT NOT NULL,
    land_y FLOAT NOT NULL,
    land_z FLOAT NOT NULL,
    utility_type INT NOT NULL,
    opponents_affected INT DEFAULT 0,
    teammates_affected INT DEFAULT 0,
    damage INT DEFAULT 0,
    map_name VARCHAR(100) NOT NULL,
    round_number INT NOT NULL,
    round_time_seconds INT NOT NULL,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (steam_id) REFERENCES players(steam_id) ON DELETE CASCADE,
    INDEX idx_utility_pos_map (map_name)
) ENGINE=InnoDB;

-- 3. Views (Dependencies satisfied)
CREATE OR REPLACE VIEW view_player_match_history AS
SELECT 
    m.id AS match_id,
    m.match_uuid,
    m.map_name,
    m.start_time,
    mps.steam_id,
    mps.kills,
    mps.deaths,
    mps.assists,
    mps.rating2,
    mps.adr,
    mps.kast,
    (mps.kills / GREATEST(mps.deaths, 1)) as kd_ratio
FROM matches m
JOIN match_player_stats mps ON m.id = mps.match_id;

-- Leetify/HLTV Style Enhanced Analytics View
CREATE OR REPLACE VIEW view_enhanced_player_analytics AS
SELECT 
    p.steam_id,
    p.name,
    paa.rating2,
    paa.performance_score,
    paa.average_damage_per_round as adr,
    paa.kast_percentage as kast,
    paa.entry_success_rate as entry_win_rate,
    paa.opening_kill_ratio,
    paa.clutch_success_rate,
    paa.clutch_points,
    paa.utility_impact_score as util_rating,
    paa.flash_effectiveness_rate as flash_eff,
    paa.trade_success_rate as trade_rate,
    paa.trade_kill_ratio,
    paa.survival_rating,
    paa.top_weapon_by_kills as main_weapon,
    paa.calculated_at
FROM players p
JOIN player_advanced_analytics paa ON p.steam_id = paa.steam_id;

-- Clutch Performance Standard View
CREATE OR REPLACE VIEW view_clutch_performance AS
SELECT 
    steam_id,
    name,
    clutches_won,
    clutches_lost,
    clutch_points,
    ROUND((clutches_won / GREATEST(clutches_won + clutches_lost, 1)) * 100, 2) as clutch_win_percent
FROM player_stats
WHERE (clutches_won + clutches_lost) > 0;

-- Entry Fragging Efficiency View
CREATE OR REPLACE VIEW view_entry_efficiency AS
SELECT 
    steam_id,
    name,
    opening_duels_won,
    opening_duels_lost,
    ROUND((opening_duels_won / GREATEST(opening_duels_won + opening_duels_lost, 1)) * 100, 2) as entry_success_rate
FROM player_stats
WHERE (opening_duels_won + opening_duels_lost) > 0;

CREATE OR REPLACE VIEW view_global_leaderboard AS
SELECT 
    p.steam_id,
    p.name,
    ps.hltv_rating,
    ps.kd_ratio,
    ps.average_damage_per_round AS adr,
    ps.kast_percentage AS kast,
    ps.kills,
    ps.deaths,
    ps.rounds_played,
    ps.updated_at
FROM players p
JOIN player_stats ps ON p.steam_id = ps.steam_id
WHERE ps.rounds_played > 10
ORDER BY ps.hltv_rating DESC;

CREATE OR REPLACE VIEW view_player_performance_timeline AS
SELECT 
    steam_id,
    calculated_at,
    rating2,
    performance_score,
    kast_percentage,
    average_damage_per_round
FROM player_advanced_analytics;

CREATE OR REPLACE VIEW view_player_profile AS
SELECT 
    p.steam_id,
    p.name,
    ps.hltv_rating AS lifetime_rating,
    ps.kills AS total_kills,
    ps.deaths AS total_deaths,
    ps.rounds_played AS total_rounds,
    (SELECT COUNT(*) FROM matches m JOIN match_player_stats mps ON m.id = mps.match_id WHERE mps.steam_id = p.steam_id) AS matches_played,
    ps.updated_at AS last_updated
FROM players p
JOIN player_stats ps ON p.steam_id = ps.steam_id;
