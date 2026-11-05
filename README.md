# statsCollector v1.0.0

A CounterStrikeSharp plugin for CS2 that collects detailed player statistics into MySQL, with non-blocking persistence and a scrim/match management system.

## Features
- **Relational match tracking** — `matches` and `rounds` are tracked automatically, with optional `series_uuid` to group BO3/BO5 series.
- **Lifetime player stats** — 100+ aggregated metrics per player (combat, utility, economy, bomb, clutch, entry). Career totals are **seeded from the database once Steam has validated the client** (at once on a LAN server, `sv_lan 1`), so they accumulate correctly across reconnects and across multiple servers sharing one database (see *Multi-server* below).
- **Per-match stats** — `match_player_stats` / `match_weapon_stats` populated at match end from start-of-match deltas: the whole match (rounds and sides, utility, trades, clutches by 1vX, bomb, economy, pistol rounds, per-weapon headshots, damage and deaths), with a community Rating 2.0 approximation (HLTV's exact formula is not public).
- **Per-round rows** — `player_round_stats` holds every player's numbers for every round (kills, damage, survival, trade, clutch, bomb, MVP, money and equipment), and `rounds` the round's buy (both sides' start money and equipment value) and bomb (site, plant time, time left at the defuse).
- **Engine cross-check** — at match end the engine's own per-match counters (`CSMatchStats_t`) are compared with the recorded ones; a difference in kills, deaths, assists or headshot kills is logged as a warning, the counters the engine defines differently as information.
- **Spatial data** — kill / death / utility positions stored as MySQL `POINT` with spatial indexes for heatmaps.
- **Non-blocking persistence** — game-thread events are snapshotted and written to MySQL on bounded background channels; overload is logged instead of blocking the game thread.
- **Dirty-flag flushing** — only players whose stats changed are written on each auto-save.
- **Resilience** — Polly v8 pipeline (retry + circuit breaker + timeout) wraps every repository operation. Writes that
  fail while the database is unreachable, restarting or read-only (or missing a column or grant) stay buffered and are
  written once it recovers; a write whose own data is rejected is logged and dropped instead of blocking the queue.
  Batches still unwritten when the plugin unloads are spilled to `pending_stats.json` in the plugin directory and
  replayed at the next start. At startup the plugin checks the server's `local_infile` and logs a critical message
  with the fix when it is off (every bulk write needs it).
- **Bots** — bots count in the round state (alive counts, first kill, trades, clutch odds) but get no stats of their
  own: a human's kills, damage and team kills on bots count, and a bot's kill is recorded as a death, not a suicide.
- **Restarts** — `mp_restartgame` after rounds were played ends the recorded match and starts a new one; the
  aborted round is not written.
- **Bulk writes** — player and position batches are written with `MySqlBulkCopy` into a temporary table, then merged.
- **Scrim management** — lobby/ready-up, captain assignment, map vote, player picking, knife round + side selection.
  After the change to the voted map, the knife round or match waits in warmup until the picked players are back
  (up to 3 minutes). Picked players who rejoin are put back on their team, and cannot switch to the other one.
- **Pause system** — `.pause` (tactical: limited and timed), `.pause tech` / `.pause admin` (until an admin
  `.unpause`s, which also ends the engine's pause after a round restore).
- **Round restore** — `.restore <round>` reloads the engine's backup of that round. The recorded stats are rolled back
  to the start of it (also after a restore from the console or a vote), so the replayed rounds are not counted twice.

## Requirements
- CounterStrikeSharp v1.0.379+ on the server (.NET 10; the plugin is built against v1.0.379 and does not load on older
  cores). CS2 1.41.9.2+ crashes cores before v1.0.378 when a client connects, and CounterStrikeSharp v1.0.375+ needs
  Metamod:Source 2.x build 1467+.
- .NET 10 SDK (to build)
- MySQL 8.4 LTS or 9.7 LTS (8.0 and 9.6 are out of support), with `local_infile=ON` — see *Database prerequisites*
- For `.restore`: start the CS2 server with `-allow_round_backup_files` (required for round backups since the
  14 Oct 2026 CS2 update). Without it `.restore` reports that the round has no backup.

A MySQL 9.7 LTS stack is provided under `MySQL-docker/`. Copy both `*.example` files in
`MySQL-docker/secrets/` to matching `*.txt` files, replace their contents, then run
`docker compose up -d`. phpMyAdmin is optional: `docker compose --profile tools up -d`; it asks for a login (use
the `cs2_statscollector` account).
MySQL and phpMyAdmin bind to loopback by default. Set `MYSQL_BIND_ADDRESS` only when game servers connect from
another host, and then to this host's private-network address, never `0.0.0.0`: Docker publishes ports ahead of
ufw/`INPUT` firewall rules, so filter the port in the `DOCKER-USER` chain. `root` can only log in from inside the
container (`MYSQL_ROOT_HOST=localhost`).

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
  with error 3948 while it is off, which is MySQL's default. The plugin checks it at startup and logs a Critical line
  with the fix; while it is off, each bulk write is dropped (and logged) instead of holding up every later write.
- Grants on the database: `SELECT, INSERT, UPDATE, DELETE, CREATE TEMPORARY TABLES`; with `AutoCreateSchema` also
  `CREATE, REFERENCES, CREATE VIEW, DROP`.

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

`database/init.sql` is the whole schema; there are no migrations. Run it once on an empty database
(`mysql -u <user> -p cs2_statscollector < database/init.sql`), or let `AutoCreateSchema` run it at startup. The
Docker image runs it only on an empty volume.

Any option can be overridden by an environment variable prefixed with `STATSCOLLECTOR_`
(e.g. `STATSCOLLECTOR_DatabasePassword`). Nested options use `__` (`STATSCOLLECTOR_Scrim__PlayersPerTeam`); a single
underscore is ignored. List options such as `Scrim__MapPool__0` are appended to the configured list, not replaced.

### Notable toggles
- `AutoCreateSchema` (default `false`) — run the embedded `database/init.sql` on startup (creates the database and any
  missing tables and views).
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
  every round without needing a scrim to be started (plain stats server). Warmup is never tracked. The `.pause`,
  `.unpause` and `.restore` commands exist only when it is `true`.
- `DeathmatchMode` (default `false`) — set on deathmatch servers: nothing is recorded there, because kills without
  rounds would distort the lifetime ratings that competitive servers share.

## Data model
- `players` — registry with first/last seen timestamps.
- `player_stats` — lifetime aggregated statistics, including exact KAST, multi-kill, clutch, and bomb-event counters.
- `matches` / `rounds` — match and round lifecycle, including the official round-end timestamp, both sides' start money and equipment value at freeze end, and the bomb's site, plant time and the seconds left when it was defused.
- `player_round_stats` — one row per player per round: their totals at round end minus those at round start (kills, damage, utility, trades, clutch 1vX, bomb, MVP, money, kill reward, round swing) and the round's flags (side, won, pistol round, survived, KAST). Written at round end, at the official round end, and once more at the next round start (the engine credits the survivors' live time only then); a round restore removes the voided rounds' rows.
- `kill_positions` / `death_positions` / `utility_positions` — spatial event data linked to a match; utility rows include entity and lifecycle event metadata.
- `match_player_stats` / `match_weapon_stats` — per-match performance (the whole match, from start-of-match deltas) and weapon breakdowns with kills, headshots, shots, hits, damage and deaths by weapon (written at match end).
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
- **Lifetime stats are seeded once the client is validated** — when a player joins (and Steam has validated them, or at once on a LAN server), their existing `player_stats` row is loaded off the game thread and added to whatever the session has already counted, then written back. Since a Steam account can only be on one server at a time there is no write race on a row, and moving between servers never overwrites history with a single session. The exception is a database outage: snapshots a server buffers while MySQL is down are written when it recovers, and can overwrite newer totals if the player has since played on another server. A player who leaves while the database is unreachable keeps their session in memory until it has been seeded, and it is written then (their playtime ends at the disconnect).
- **Per-match rows** (`match_player_stats`, `match_weapon_stats`, `rounds`, position tables) are immutable per `(match_id, …)`, so every server records its own matches independently.

> **Connection budget:** each server opens its own MySqlConnector pool (max **50** connections per server). For *N* servers, make sure MySQL `max_connections` comfortably exceeds `N × 50` — e.g. for 5 servers set `max_connections >= 300`.

## Round Swing

`player_stats.round_swing` (also surfaced in `view_global_leaderboard`) is an impact metric inspired by
the Round Swing component of HLTV's Rating 3.0. For each enemy kill it adds the change in the team's
estimated round-win probability, and the round's total is credited only if that team wins the round. That
crediting rule is this plugin's simplification: HLTV's Round Swing is zero-sum, charges deaths and splits a kill's
credit (see [Introducing Rating 3.0](https://www.hltv.org/news/42485/introducing-rating-30)).

How it is computed here:
- The win probability comes from an alive-count table (`MatchTrackingService.GetRoundWinProbability`): the CT round-win rate for every 1–5 vs 1–5 state, measured over 400,000+ CS:GO matchmaking rounds ([source](https://cryptm.org/posts/2020/06/12/csgo.html)). Teams larger than five use the five-player values.
- A kill's swing is `winProb(after the kill) − winProb(before the kill)` for the killer's team. The table is monotone, so with up to five per side every enemy kill scores more than zero (as in HLTV's 2026-10-01 update). A kill is worth most when the alive counts are close and little when either side is far ahead; with more than five per side the five-player values are used, so a kill there scores 0. Totals stored before this table replaced the earlier hand-made one (whose gaps made some kills score negative) are not recomputed.

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
- Test: `dotnet test tests/statsCollector.Tests` (xunit; covers the counters, the snapshot plumbing, the match and
  round deltas, the per-match SQL column lists against `init.sql`, and the position writer's restore ordering)
- Publish: `dotnet publish -c Release -o out`
- Requirements: .NET 10 SDK

## Links
- Issues: https://github.com/ezspot/cs2-statscollector/issues
- CounterStrikeSharp docs: https://docs.cssharp.dev/
- CS2 game events: https://cs2.poggu.me/dumped-data/game-events

## License
MIT
