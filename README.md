# statsCollector v3.1.0

A CounterStrikeSharp plugin for CS2 that collects detailed player statistics into MySQL, with non-blocking persistence and a scrim/match management system.

## Features
- **Relational match tracking** — `matches` and `rounds` are tracked automatically, with optional `series_uuid` to group BO3/BO5 series.
- **Lifetime player stats** — 100+ aggregated metrics per player (combat, utility, economy, bomb, clutch, entry).
- **Per-match stats** — `match_player_stats` / `match_weapon_stats` populated at match end from start-of-match deltas, with a standard HLTV 2.0 per-match rating.
- **Spatial data** — kill / death / utility positions stored as MySQL `POINT` with spatial indexes for heatmaps.
- **Non-blocking persistence** — game-thread events are snapshotted and written to MySQL on a background channel; the server thread never waits on the database.
- **Dirty-flag flushing** — only players whose stats changed are written on each auto-save.
- **Resilience** — Polly v8 pipeline (retry + circuit breaker + timeout) wraps every database operation.
- **Bulk writes** — player and position batches are written with `MySqlBulkCopy` into a temporary table, then merged.
- **Scrim management** — lobby/ready-up, captain assignment, map vote, player picking, knife round + side selection.
- **Tactical pause system** — `.pause` / `.unpause` with technical/tactical limits.

## Requirements
- CounterStrikeSharp v1.0.369+ (.NET 10 runtime)
- .NET 10 SDK (to build) / .NET 10 runtime (to run)
- MySQL 8.0+ / 9.x (or MariaDB 10.6+) — required for `POINT` columns and spatial indexes

A ready-to-run MySQL 9.6 + phpMyAdmin stack is provided under `MySQL-docker/` (`docker compose up -d`).

> Note: current CounterStrikeSharp targets .NET 10. If your server still runs a .NET 8 build of
> CounterStrikeSharp, pin `CounterStrikeSharp.API` to its last `net8.0` release and set
> `<TargetFramework>net8.0</TargetFramework>`.

## Installation
1. Install [CounterStrikeSharp](https://docs.cssharp.dev/) on your CS2 server.
2. Copy the plugin to `game/csgo/addons/counterstrikesharp/plugins/statsCollector/`.
3. Create `config.json` in that folder (see `config.sample.json`).
4. Run `database/init.sql` against your MySQL server to create the schema and views.
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
See `config.sample.json` for the full set of options (persistence, trade window, clutch weighting, scrim settings).

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
- `EnableMovementTracking` (default `true`) — counts footsteps, jumps, and pings per player.
  **Performance:** `player_footstep` fires very frequently (multiple times per second for every moving
  player), and each one takes a per-player lock to increment a counter on the game thread. The
  analytical value is low, so disabling it removes a constant source of game-thread work on a busy
  server. When off, these handlers aren't even subscribed (zero per-event cost).
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
- Lightweight `System.Diagnostics` `ActivitySource`/`Meter` instrumentation is present but has no exporter wired by default (no external collector required).

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
