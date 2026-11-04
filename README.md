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
- CounterStrikeSharp v1.0.378+ on the server (the plugin is built against v1.0.379; .NET 10). CS2 1.41.9.2+
  crashes older cores when a client connects, and CounterStrikeSharp v1.0.375+ needs Metamod:Source 2.x build 1467+.
- .NET 10 SDK (to build)
- MySQL 8.4 LTS or 9.7 LTS (8.0 and 9.6 are out of support), with `local_infile=ON` — see *Database prerequisites*
- For `.restore`: start the CS2 server with `-allow_round_backup_files` (required for round backups since the
  14 Oct 2026 CS2 update)

A MySQL 9.7 LTS stack is provided under `MySQL-docker/`. Copy both `*.example` files in
`MySQL-docker/secrets/` to matching `*.txt` files, replace their contents, then run
`docker compose up -d`. phpMyAdmin is optional: `docker compose --profile tools up -d`.
A volume created by the earlier `mysql:9.6` image upgrades in place on the first 9.7 start and cannot be
downgraded afterwards, so back up the `mysql_data` volume first.
MySQL and phpMyAdmin bind to loopback by default; set `MYSQL_BIND_ADDRESS` only when the game
server connects from another host, and restrict that port at the firewall.

> Note: current CounterStrikeSharp targets .NET 10. If your server still runs a .NET 8 build of
> CounterStrikeSharp, pin `CounterStrikeSharp.API` to its last `net8.0` release and set
> `<TargetFramework>net8.0</TargetFramework>`.

## Installation
1. Install [CounterStrikeSharp](https://docs.cssharp.dev/) on your CS2 server.
2. Copy the plugin to `game/csgo/addons/counterstrikesharp/plugins/statsCollector/`, and copy `cfg/*.cfg` to
   `game/csgo/cfg/` (scrim configs are loaded from there, see `Scrim.ScrimConfigPath`).
3. Configure `game/csgo/addons/counterstrikesharp/configs/plugins/statsCollector/statsCollector.json`
   (CounterStrikeSharp creates it on first start; see `config.sample.json`).
4. Create the configured database, select it, then run `database/init.sql` to create the tables and views.
5. Start the server and confirm with `css_plugins list`. After replacing the DLL, restart the server or
   `css_plugins unload` + `load` it: on CounterStrikeSharp v1.0.379, `css_plugins reload` after an
   upload-and-rename leaves the plugin unloaded.

Scrim and pause commands are registered as `css_*`. CounterStrikeSharp maps the `!` and `/` chat prefixes by
default (`!ready`, `!pause`); add `"."` to `PublicChatTrigger` in `configs/core.json` to use the `.ready` style
shown in the in-game prompts.

### Database prerequisites
The bundled Docker stack already satisfies these. For your own MySQL server:
- `local_infile=ON` (`SET PERSIST local_infile = ON;`). All bulk writes use `LOAD DATA LOCAL INFILE` and fail
  with error 3948 while it is off, which is MySQL's default.
- Grants on the database: `SELECT, INSERT, UPDATE, CREATE TEMPORARY TABLES`; with `AutoCreateSchema` also
  `CREATE, ALTER, INDEX, REFERENCES, CREATE VIEW, DROP`.

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
For a remote MySQL host use `VerifyCA` with `DatabaseSslCa` set to the server's CA certificate (for the Docker
stack, a copy of the container's `/var/lib/mysql/ca.pem`; its auto-generated certificate fails `VerifyFull`'s
hostname check), or `VerifyFull` with a certificate issued for `DatabaseHost`. `None` only works over loopback:
MySQL's default `caching_sha2_password` rejects non-TLS remote logins after every server restart.
See `config.sample.json` for the full set of options (persistence, trade window, clutch weighting, scrim settings).

For an existing database, treat `database/init.sql` as a bootstrap script rather than a migration.
When upgrading, apply each SQL file under `database/migrations/` that has not been applied yet, in numeric
order. They are not idempotent (MySQL has no `ADD COLUMN IF NOT EXISTS`, so a re-run fails with error 1060);
a fresh `init.sql` already includes them, and `AutoCreateSchema` adds missing columns itself. The Docker image
runs `init.sql` only on an empty volume.
Migration `002_add_exact_player_event_counters.sql` preserves exact KAST rounds, multi-kill tiers,
clutch kills, and bomb explosions across reconnects.
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
- `AutoCreateSchema` (default `false`) — apply the embedded schema and compatible column migrations on startup.
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
- `player_stats` — lifetime aggregated statistics, including exact KAST, multi-kill, clutch, and bomb-event counters.
- `matches` / `rounds` — match and round lifecycle, including the official round-end timestamp.
- `kill_positions` / `death_positions` / `utility_positions` — spatial event data linked to a match; utility rows include entity and lifecycle event metadata.
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
- **Lifetime stats are seeded on connect** — when a player joins, their existing `player_stats` row is loaded into the in-memory session (off the game thread) *before* new stats accumulate, then written back. Since a Steam account can only be on one server at a time there is no write race on a row, and moving between servers never overwrites history with a single session. The exception is a database outage: snapshots a server buffers while MySQL is down are written when it recovers, and can overwrite newer totals if the player has since played on another server.
- **Per-match rows** (`match_player_stats`, `match_weapon_stats`, `rounds`, position tables) are immutable per `(match_id, …)`, so every server records its own matches independently.

> **Connection budget:** each server opens its own MySqlConnector pool (max **50** connections per server). For *N* servers, make sure MySQL `max_connections` comfortably exceeds `N × 50` — e.g. for 5 servers set `max_connections >= 300`.

> **Existing-data note:** after migration 002, KAST rounds, multi-kill tiers, and clutch kills are restored exactly across reconnects. Multi-kill tiers are exclusive (a 3k round counts only as a 3k). Rows written before the migration have no tiers, and their `multi_kills` reflects only the player's most recent session, counted the old way (a 3k round also counted as a 2k); it is carried into `multi_kill_2` on the player's next connect. Pre-migration tiers and clutch kills cannot be recovered; KAST rounds are reconstructed from the stored KAST%.

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
- Logging is via Serilog (console + daily rolling file under the plugin folder's `logs/`).
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
