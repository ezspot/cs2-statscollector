# statsCollector v3.1.0

A CounterStrikeSharp plugin for CS2 that collects detailed player statistics into MySQL, with non-blocking persistence and a scrim/match management system.

## Features
- **Relational match tracking** — `matches` and `rounds` are tracked automatically, with optional `series_uuid` to group BO3/BO5 series.
- **Lifetime player stats** — 100+ aggregated metrics per player (combat, utility, economy, bomb, clutch, entry). Career totals are **seeded from the database on connect**, so they accumulate correctly across reconnects and across multiple servers sharing one database (see *Multi-server* below).
- **Per-match stats** — `match_player_stats` / `match_weapon_stats` populated at match end from start-of-match deltas, with a community Rating 2.0 approximation (HLTV's exact formula is not public).
- **Spatial data** — kill / death / utility positions stored as MySQL `POINT` with spatial indexes for heatmaps.
- **Non-blocking persistence** — game-thread events are snapshotted and written to MySQL on bounded background channels; overload is logged instead of blocking the game thread.
- **Dirty-flag flushing** — only players whose stats changed are written on each auto-save.
- **Resilience** — Polly v8 pipeline (retry + circuit breaker + timeout) wraps every database operation.
- **Bulk writes** — player and position batches are written with `MySqlBulkCopy` into a temporary table, then merged.
- **Scrim management** — lobby/ready-up, captain assignment, map vote, player picking, knife round + side selection.
- **Tactical pause system** — `.pause` / `.unpause` with technical/tactical limits.

## Requirements
- CounterStrikeSharp v1.0.371+ (.NET 10 runtime)
- .NET 10 SDK (to build) / .NET 10 runtime (to run)
- MySQL 8.0+ / 9.x — required for `POINT` columns and spatial indexes

A MySQL 9.6 stack is provided under `MySQL-docker/`. Copy both `*.example` files in
`MySQL-docker/secrets/` to matching `*.txt` files, replace their contents, then run
`docker compose up -d`. phpMyAdmin is optional: `docker compose --profile tools up -d`.
MySQL and phpMyAdmin bind to loopback by default; set `MYSQL_BIND_ADDRESS` only when the game
server connects from another host, and restrict that port at the firewall.

> Note: current CounterStrikeSharp targets .NET 10. If your server still runs a .NET 8 build of
> CounterStrikeSharp, pin `CounterStrikeSharp.API` to its last `net8.0` release and set
> `<TargetFramework>net8.0</TargetFramework>`.

## Installation
1. Install [CounterStrikeSharp](https://docs.cssharp.dev/) on your CS2 server.
2. Copy the plugin to `game/csgo/addons/counterstrikesharp/plugins/statsCollector/`.
3. Create `config.json` in that folder (see `config.sample.json`).
4. Create the configured database, select it, then run `database/init.sql` to create the tables and views.
5. Start the server and confirm with `css_plugins list`.

### Minimal config
```json
{
  "DatabaseHost": "127.0.0.1",
  "DatabasePort": 3306,
  "DatabaseName": "cs2_statscollector",
  "DatabaseUsername": "cs2_statscollector",
  "DatabasePassword": "your_password",
  "DatabaseSslMode": "Required",
  "AutoSaveSeconds": 60,
  "LogLevel": "Information"
}
```
Use `VerifyFull` and set `DatabaseSslCa` to your CA certificate when connecting to a remote MySQL host.
See `config.sample.json` for the full set of options (persistence, trade window, clutch weighting, scrim settings).

For an existing database, treat `database/init.sql` as a bootstrap script rather than a migration.
This release widens `clutch_points`; apply the following before deployment:
```sql
ALTER TABLE player_stats MODIFY clutch_points DECIMAL(10,3) DEFAULT 0;
ALTER TABLE player_advanced_analytics MODIFY clutch_points DECIMAL(10,3) NOT NULL;
```
New match foreign keys in `init.sql` apply automatically only to clean installations. Validate and remove
any orphaned rows before adding those constraints to an existing database.

Any option can be overridden by an environment variable prefixed with `STATSCOLLECTOR_`
(e.g. `STATSCOLLECTOR_DatabasePassword`).

### Notable toggles
- `AutoCreateSchema` (default `false`) — apply the embedded `init.sql` on startup if the schema is missing.
- `EnablePositionTracking` (default `true`) — records the X/Y/Z coordinates of every kill, death, and
  utility throw into `kill_positions` / `death_positions` / `utility_positions` (the data behind
  heatmaps and spatial queries). **Performance:** this is the heaviest write path — it generates the
  most database rows and the most batched inserts, so on busy or resource-constrained servers it is
  the first thing to disable if you don't need heatmaps. Disabling it has no effect on combat,
  economy, or match statistics.
- `EnableMovementTracking` (default `false`) — counts footsteps, jumps, and pings per player.
  **Performance:** `player_footstep` fires very frequently (multiple times per second for every moving
  player), and each one takes a per-player lock to increment a counter on the game thread. The
  analytical value is low, so it is off by default. When off, these handlers aren't even subscribed
  (zero per-event cost); enable it only if you specifically want movement counters.
- `EnableScrim` (default `true`) — scrim/match-management commands. When `false`, stat tracking runs on
  every round without needing a scrim to be started (plain stats server).

## Data model
- `players` — registry with first/last seen timestamps.
- `player_stats` — lifetime aggregated statistics.
- `matches` / `rounds` — match and round lifecycle.
- `kill_positions` / `death_positions` / `utility_positions` — spatial event data linked to a match.
- `match_player_stats` / `match_weapon_stats` — per-match performance and weapon breakdowns (written at match end).
- `player_advanced_analytics` — temporal performance snapshots (Rating 2.0, Impact, KAST, ADR).

### Dashboard views
- `view_global_leaderboard` — ranked by HLTV rating (10-round activity threshold).
- `view_player_profile` — per-player summary.
- `view_player_match_history` — match-by-match breakdown.
- `view_clutch_performance`, `view_entry_efficiency`, `view_enhanced_player_analytics`.

## Multi-server (shared database)
Several CS2 servers (target: up to ~5) can run this plugin against one MySQL database safely:

- **Player identity** is the SteamID64 (`player_stats.steam_id`, primary key) — globally unique, so a player has exactly one lifetime row no matter which server they play on.
- **Match identity** is a per-server generated UUID (`matches.match_uuid`, `UNIQUE`); the integer `matches.id` is assigned by the database. No cross-server collisions, so match UUIDs do not need to be a primary key.
- **Lifetime stats are seeded on connect** — when a player joins, their existing `player_stats` row is loaded into the in-memory session (off the game thread) *before* new stats accumulate, then written back. Since a Steam account can only be on one server at a time there is no write race on a row, and moving between servers never overwrites history with a single session.
- **Per-match rows** (`match_player_stats`, `match_weapon_stats`, `rounds`, position tables) are immutable per `(match_id, …)`, so every server records its own matches independently.

> **Connection budget:** each server opens its own MySqlConnector pool (max **50** connections per server). For *N* servers, make sure MySQL `max_connections` comfortably exceeds `N × 50` — e.g. for 5 servers set `max_connections >= 300`.

> **Note on derived ratings after a reseed:** raw counters (K/D/A, damage, accuracy, KAST, utility, bomb, economy) are restored exactly. A few rating *sub-terms* whose intermediate counts aren't stored as columns (per-tier multi-kills, clutch-kill impact, exact weighted-kill bonuses) are reconstructed approximately, so `hltv_rating`/`impact_rating` can differ very slightly immediately after a reconnect; they re-converge as the session plays. KAST and the KAST-driven part of the rating are reconstructed exactly from the stored KAST%.

## Round Swing

`player_stats.round_swing` (also surfaced in `view_global_leaderboard`) is an impact metric inspired by
the Round Swing component of HLTV's Rating 3.0. For each enemy kill it adds the change in the team's
estimated round-win probability, and — following HLTV's published rule — the round's total is credited
only if that team wins the round.

How it is computed here:
- The win probability comes from this plugin's own alive-count table (`MatchTrackingService.GetRoundWinProbability`), i.e. a function of how many players are alive on each side.
- A kill's swing is `winProb(after the kill) − winProb(before the kill)` for the killer's team. Kills from behind are worth more; kills while already ahead are worth less.

What it is **not**: this is not HLTV's Rating 3.0, and it will not match HLTV's numbers. HLTV's formula
is proprietary and trained on a large match dataset. This metric implements only the Round Swing idea,
and only from alive counts — it does **not** model map, side, economy, bomb state, or weapon/equipment
win-rates, and it does not split kill credit by damage share or flash assists. It is a self-contained
approximation built from data this plugin already collects.

## Architecture
Flow: CS2 event → event processor → `PlayerSessionService` (per-player locked state) →
`PlayerSnapshot` → `PersistenceChannel` (bounded background queue) → `StatsRepository` → MySQL.

- Database access goes through `IConnectionFactory` + Dapper, wrapped in a Polly resilience pipeline.
- Logging is via Serilog (console + daily rolling file under `logs/`).
- Observability is structured logging (Serilog). A lightweight `System.Diagnostics.ActivitySource` is
  present for optional tracing (a no-op with no listener attached); no metrics `Meter`/exporter is wired
  since the plugin runs in-process with no scrape endpoint.

## Development
- Build: `dotnet build -c Release`
- Publish: `dotnet publish -c Release -o out`
- Requirements: .NET 10 SDK

## Links
- Issues: https://github.com/ezspot/cs2-statscollector/issues
- CounterStrikeSharp docs: https://docs.cssharp.dev/
- CS2 game events: https://cs2.poggu.me/dumped-data/game-events

## License
MIT
